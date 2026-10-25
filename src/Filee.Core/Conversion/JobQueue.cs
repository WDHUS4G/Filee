// Runs conversion jobs in the background: plans routes, limits engine concurrency,
// allocates output paths and reports progress. UI code only enqueues and listens to events.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

/// <summary>Background queue that executes <see cref="ConversionJob"/>s one after another.</summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly ConverterCatalog _catalog;
    private readonly IPdfMerger? _pdfMerger;
    private readonly IFileCombiner? _combiner;
    private readonly ITiffMerger? _tiffMerger;
    private readonly IMediaJoiner? _mediaJoiner;
    private readonly ILogger _log;
    private readonly Channel<ConversionJob> _channel = Channel.CreateUnbounded<ConversionJob>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _engineGates = new();
    /// <summary>
    /// Output paths handed out and not released yet, with how many conversions hold each. Also the lock for everything
    /// about output paths.
    /// </summary>
    private readonly Dictionary<string, Reservation> _reservedOutputs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Output folders this queue made (a subfolder, a fixed folder that wasn't there): removed again when left empty.</summary>
    private readonly HashSet<string> _madeFolders = new(Platform.FileSystemPaths.Comparer);
    private readonly Task _worker;
    private readonly Func<string, Platform.TrashRefusal?>? _moveToTrash;
    private int _busy;

    /// <param name="catalog">Converters and their priority.</param>
    /// <param name="pdfMerger">Used by "Merge into one PDF" presets.</param>
    /// <param name="log">Optional logger.</param>
    /// <param name="combiner">Used by "Compress into one archive" presets (<see cref="ArchiveOptions.CombineIntoOne"/>).</param>
    /// <param name="tiffMerger">Used by multi-page TIFF presets (<see cref="ImageOptions.MultiPageTiff"/>).</param>
    /// <param name="mediaJoiner">Used by "Merge videos", "Merge audio" and "Video from images" (<see cref="Preset.JoinsIntoOneMedia"/>).</param>
    /// <param name="moveToTrash">
    /// Moves a file to the Recycle Bin / Trash for <see cref="OutputRule.ReplaceOriginal"/>: null when it worked, else
    /// why not. Without it originals are always kept.
    /// </param>
    public JobQueue(ConverterCatalog catalog, IPdfMerger? pdfMerger = null, ILogger<JobQueue>? log = null, IFileCombiner? combiner = null,
        ITiffMerger? tiffMerger = null, IMediaJoiner? mediaJoiner = null, Func<string, Platform.TrashRefusal?>? moveToTrash = null)
    {
        _moveToTrash = moveToTrash;
        _catalog = catalog;
        _pdfMerger = pdfMerger;
        _combiner = combiner;
        _tiffMerger = tiffMerger;
        _mediaJoiner = mediaJoiner;
        _log = log ?? (ILogger)NullLogger.Instance;
        _worker = Task.Run(WorkLoopAsync);
    }

    /// <summary>Raised (on a background thread) whenever a job's state or progress changes.</summary>
    public event EventHandler<ConversionJob>? JobUpdated;

    /// <summary>Raised (on a background thread) once when a job reaches a final state.</summary>
    public event EventHandler<ConversionJob>? JobFinished;

    /// <summary>True while a job waits in the queue or runs (the app frees memory only when this is false).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>Adds a job to the queue and returns it immediately.</summary>
    /// <param name="fetcher">Saves sources that aren't files yet (files from a browser) when the job runs.</param>
    public ConversionJob Enqueue(IReadOnlyList<string> sources, Preset preset, string presetDisplayName, ISourceFetcher? fetcher = null)
    {
        var job = new ConversionJob(sources, preset, presetDisplayName, fetcher);
        Interlocked.Increment(ref _busy); // until the worker is done with it
        _channel.Writer.TryWrite(job);
        JobUpdated?.Invoke(this, job);
        return job;
    }

    /// <summary>Runs a job to completion on the calling task. Used by tests and by <see cref="Enqueue"/>'s worker.</summary>
    public async Task RunAsync(ConversionJob job)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await RunJobAsync(job);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task RunJobAsync(ConversionJob job)
    {
        job.State = JobState.Running;
        Notify(job);

        var workDir = Path.Combine(DefaultWorkRoot, job.Id);
        Directory.CreateDirectory(workDir);
        try
        {
            if (job.Fetcher is { } fetcher)
                await FetchSourcesAsync(job, fetcher);

            var planner = _catalog.CreatePlanner();
            var merge = job.Preset.Pdf.MergeIntoSingle
                        && job.Preset.TargetFormat == "pdf"
                        && job.Files.Count > 1
                        && _pdfMerger is not null;
            // Any files, one of them or many, archives or not: they go into the archive as they are.
            var combine = job.Preset.PacksIntoOneArchive && _combiner is not null;

            // One file is enough: a 10-page PDF becomes one 10-page TIFF.
            var multiPageTiff = job.Preset.Image.MultiPageTiff && job.Preset.TargetFormat == "tiff" && _tiffMerger is not null;
            // Clips back to back, or pictures one after the other, in the order they were dropped.
            var join = job.Preset.JoinsIntoOneMedia && _mediaJoiner is not null;

            if (join)
                await RunJoinedMediaAsync(job, planner, workDir);
            else if (merge)
                await RunMergedAsync(job, planner, workDir);
            else if (multiPageTiff)
                await RunMultiPageTiffAsync(job, planner, workDir);
            else if (combine)
                await RunCombinedAsync(job, workDir);
            else
                await Parallel.ForEachAsync(
                    job.Files.Select((f, i) => (File: f, Index: i + 1)),
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = CancellationToken.None },
                    async (item, _) => await RunFileAsync(job, item.File, item.Index, planner, workDir, finalOutput: true));

            job.State = job.CancellationToken.IsCancellationRequested ? JobState.Cancelled
                : job.Files.All(f => f.State is FileState.Done or FileState.Skipped) ? JobState.Completed
                : job.Files.Any(f => f.State == FileState.Done) ? JobState.CompletedWithErrors
                : JobState.Failed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Job} failed", job.Id);
            job.State = JobState.Failed;
        }
        finally
        {
            lock (_reservedOutputs)
            {
                foreach (var output in job.Outputs)
                    Unreserve(output);
                // Folders no running job writes into any more are the user's from now on.
                _madeFolders.RemoveWhere(folder => !HasReservationIn(folder));
            }
            TryDelete(workDir);
            Release(job);
        }

        Notify(job);
        JobFinished?.Invoke(this, job);
    }

    /// <summary>Share of a fetched file's progress that saving it takes; converting it fills the rest.</summary>
    private const double FetchShare = 0.5;

    /// <summary>
    /// Saves the sources the job's fetcher brings (files dragged from a browser) before anything is converted. A file
    /// that can't be saved fails with its reason; the others still convert.
    /// </summary>
    private async Task FetchSourcesAsync(ConversionJob job, ISourceFetcher fetcher)
    {
        var files = job.Files.Where(f => f.NeedsFetch).ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, _) =>
        {
            file.State = FileState.Running;
            file.Transfer = new TransferProgress(0, null);
            Notify(job);
            var progress = new SyncProgress<TransferProgress>(p =>
            {
                if (file.State != FileState.Running || file.Saved)
                    return;
                file.Transfer = p;
                var value = (p.Fraction ?? 0) * FetchShare;
                if (value > file.Progress)
                    file.Progress = value;
                Notify(job);
            });
            try
            {
                file.SourcePath = await fetcher.FetchAsync(file.SourcePath, progress, job.CancellationToken);
                file.Saved = true;
                file.State = FileState.Pending;
                file.Progress = FetchShare;
            }
            catch (OperationCanceledException)
            {
                file.State = FileState.Cancelled;
            }
            catch (ConversionRefusedException ex)
            {
                _log.LogInformation("Saving {File} was refused: {Reason}", file.SourcePath, ex.Message);
                FailFetch(file, ex.ErrorKey, ex.Detail);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Saving {File} failed", file.SourcePath);
                FailFetch(file, "error.web_download_failed", ex.Message);
            }
            Notify(job);
        });

        static void FailFetch(FileResult file, string key, string? detail)
        {
            file.State = FileState.Failed;
            file.ErrorKey = key;
            file.ErrorDetail = detail;
            file.Progress = 1;
        }
    }

    /// <summary>
    /// Reports on the reporting thread, in order: Progress&lt;T&gt; posts to the thread pool, where a late byte count could
    /// land after the file was saved.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>A file that didn't get as far as being converted (saving it failed or was cancelled).</summary>
    private static bool Stopped(FileResult file) => file.State is FileState.Failed or FileState.Cancelled;

    /// <summary>Lets the job's fetcher clean up (originals that aren't kept).</summary>
    private void Release(ConversionJob job)
    {
        try
        {
            job.Fetcher?.Release(job);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleaning up after job {Job} failed", job.Id);
        }
    }

    /// <summary>Applies the preset's PDF finishing (<see cref="PdfOptions.FinishesPdf"/>) to the PDFs among <paramref name="outputs"/>.</summary>
    private async Task FinishPdfsAsync(Preset preset, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        // A retry from history whose preset (and so its password) is gone: no PDF without the password it was meant to have.
        if (preset.Pdf.PasswordLeftOut && preset.Pdf.Password.Length == 0)
            throw new ConversionRefusedException("error.pdf_password_gone");
        if (!preset.Pdf.FinishesPdf || _catalog.All.OfType<IPdfFinisher>().FirstOrDefault() is not { } finisher)
            return;
        foreach (var pdf in outputs.Where(o => FormatRegistry.Detect(o)?.Id == "pdf"))
            await finisher.FinishAsync(pdf, preset.Pdf, ct);
    }

    /// <summary>Converts every file to PDF in the work directory, then merges them into one output.</summary>
    private async Task RunMergedAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var parts = new string?[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                // An empty PDF goes the long way, where it fails as empty instead of breaking the whole merge; so does
                // one whose text is recognized first.
                if (FormatRegistry.Detect(file.SourcePath)?.Id == "pdf" && !IsEmptyFile(file.SourcePath) && !job.Preset.RecognizesTextOf("pdf"))
                {
                    // Already a PDF, merged as it is: its pages can't be turned (only images going into it can).
                    if (job.Preset.Image.TurnsOrMirrors)
                        FailUnturned(job, file, "pdf → pdf");
                    else
                        parts[i] = file.SourcePath;
                    return;
                }
                var produced = await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false);
                parts[i] = produced.FirstOrDefault();
            });

        var inputs = parts.OfType<string>().ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
            return;

        var first = job.Files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate("pdf");
        if (output is null)
        {
            foreach (var f in job.Files) f.State = FileState.Skipped;
            return;
        }

        try
        {
            await _pdfMerger!.MergeAsync(inputs, output, job.CancellationToken);
            // Once for the merged PDF, so its pages are numbered through.
            await FinishPdfsAsync(job.Preset, [output], job.CancellationToken);
        }
        catch
        {
            allocator.Discard();
            throw;
        }
        first.Outputs.Add(output);
        foreach (var f in job.Files.Where(f => f.State != FileState.Failed))
        {
            f.State = FileState.Done;
            f.Progress = 1;
        }
        Notify(job);
    }

    /// <summary>
    /// Converts every file to TIFF pages in the work directory (a TIFF is taken as it is), then writes them all as one
    /// multi-page TIFF named after the first file.
    /// </summary>
    private async Task RunMultiPageTiffAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var parts = new IReadOnlyList<string>[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                // A TIFF joins as it is, unless it is to be turned: then it goes through the engine like the rest.
                parts[i] = FormatRegistry.Detect(file.SourcePath)?.Id == "tiff" && !job.Preset.Image.TurnsOrMirrors
                           && !IsEmptyFile(file.SourcePath)
                    ? [file.SourcePath]
                    : await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false, intermediate: "tiff");
            });

        var inputs = parts.SelectMany(p => p ?? []).ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
            return;

        var first = job.Files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate("tiff");
        if (output is null)
        {
            foreach (var f in job.Files) f.State = FileState.Skipped;
            return;
        }

        try
        {
            await _tiffMerger!.MergeAsync(inputs, output, job.Preset.Image, job.CancellationToken);
        }
        catch
        {
            allocator.Discard();
            throw;
        }
        first.Outputs.Add(output);
        foreach (var f in job.Files.Where(f => f.State != FileState.Failed))
        {
            f.State = FileState.Done;
            f.Progress = 1;
        }
        Notify(job);
    }

    /// <summary>Share of a slideshow's progress that preparing its pictures takes; encoding the video fills the rest.</summary>
    private const double SlidesReadyShare = 0.3;

    /// <summary>
    /// "Merge videos" / "Merge audio": the videos or audio files go into one file as they are. "Video from images":
    /// every picture becomes PNG in the work directory first (a PNG is taken as it is), with its orientation applied by
    /// the image engines, then the pictures become one video. The output is named after the first file.
    /// </summary>
    private async Task RunJoinedMediaAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var slideshow = job.Preset.Media.Slideshow;
        var target = FormatRegistry.Get(job.Preset.TargetFormat);
        // Before any picture is converted: too many fail at once instead of after minutes of PNG steps. Files whose
        // download already failed don't count.
        var pictures = job.Files.Count(f => !Stopped(f));
        if (slideshow && pictures > MediaOptions.MaxSlides)
        {
            foreach (var f in job.Files.Where(f => !Stopped(f)))
                FailFile(job, f, "error.too_many_pictures", $"{pictures} > {MediaOptions.MaxSlides}");
            return;
        }
        var parts = new IReadOnlyList<string>?[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                var source = FormatRegistry.Detect(file.SourcePath);
                if (source is null)
                {
                    FailFile(job, file, "error.unsupported_source", "." + FormatRegistry.ExtensionOf(file.SourcePath));
                    return;
                }
                if (IsEmptyFile(file.SourcePath))
                {
                    FailFile(job, file, "error.empty_file", null);
                    return;
                }
                if (slideshow)
                {
                    parts[i] = source.Id == "png" && !job.Preset.Image.TurnsOrMirrors
                        ? [file.SourcePath]
                        : await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false, intermediate: "png",
                            ceiling: SlidesReadyShare);
                    return;
                }
                // A video's sound can join an audio file; a picture or a document joins nothing.
                var joins = source.Category == FormatCategory.Video
                    || source.Category == FormatCategory.Audio && target.Category == FormatCategory.Audio;
                if (!joins)
                {
                    FailFile(job, file, "error.no_route", $"{source.Id} → {target.Id}");
                    return;
                }
                parts[i] = [file.SourcePath];
            });

        var joined = job.Files.Where((f, i) => parts[i] is { Count: > 0 } && f.State != FileState.Failed).ToList();
        var inputs = parts.Where(p => p is not null).SelectMany(p => p!).ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
        {
            Notify(job);
            return;
        }

        var first = joined[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate(target.PrimaryExtension);
        if (output is null)
        {
            foreach (var f in joined)
            {
                f.State = FileState.Skipped;
                f.Progress = 1;
            }
            Notify(job);
            return;
        }

        foreach (var f in joined)
            f.State = FileState.Running;
        Notify(job);
        var floor = joined.Min(f => f.Progress);
        var progress = new Progress<double>(p =>
        {
            // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
            var value = floor + (1 - floor) * Math.Clamp(p, 0, 1);
            if (value <= first.Progress)
                return;
            foreach (var f in joined)
                f.Progress = value;
            Notify(job);
        });

        try
        {
            await _mediaJoiner!.JoinAsync(inputs, output, target.Id, job.Preset, workDir, progress, job.CancellationToken);
            first.Outputs.Add(output);
            foreach (var f in joined)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (OperationCanceledException)
        {
            allocator.Discard();
            foreach (var f in joined)
                f.State = FileState.Cancelled;
        }
        catch (ConversionRefusedException ex)
        {
            allocator.Discard();
            _log.LogInformation("Joining {Count} file(s) into {Output} was refused: {Reason}", inputs.Count, output, ex.Message);
            foreach (var f in joined)
                FailFile(job, f, ex.ErrorKey, ex.Detail);
        }
        catch (Exception ex)
        {
            allocator.Discard();
            _log.LogWarning(ex, "Joining {Count} file(s) into {Output} failed", inputs.Count, output);
            foreach (var f in joined)
                FailFile(job, f, "error.conversion_failed", ex.Message);
        }
        Notify(job);
    }

    private void FailFile(ConversionJob job, FileResult file, string key, string? detail)
    {
        file.State = FileState.Failed;
        file.ErrorKey = key;
        file.ErrorDetail = detail;
        file.Progress = 1;
        Notify(job);
    }

    /// <summary>A file that would be used as it is although the preset turns or mirrors it.</summary>
    private void FailUnturned(ConversionJob job, FileResult file, string detail)
    {
        file.State = FileState.Failed;
        file.ErrorKey = "error.cannot_turn";
        file.ErrorDetail = detail;
        file.Progress = 1;
        Notify(job);
    }

    /// <summary>Packs every source file, unchanged, into one archive named after the first file.</summary>
    private async Task RunCombinedAsync(ConversionJob job, string workDir)
    {
        var format = FormatRegistry.Get(job.Preset.TargetFormat);
        var inputs = new List<FileResult>();
        foreach (var file in job.Files.Where(f => !Stopped(f)))
        {
            // Folders are packed with their content.
            if (File.Exists(file.SourcePath) || Directory.Exists(file.SourcePath))
                inputs.Add(file);
            else
                MarkFailed(file, "The file no longer exists.");
        }
        if (inputs.Count == 0)
        {
            Notify(job);
            return;
        }

        var allocator = CreateFinalAllocator(job, inputs[0].SourcePath, 1, oneFile: true);
        var output = allocator.Allocate(format.PrimaryExtension);
        if (output is null)
        {
            foreach (var f in inputs)
            {
                f.State = FileState.Skipped;
                f.Progress = 1;
            }
            Notify(job);
            return;
        }

        foreach (var f in inputs)
            f.State = FileState.Running;
        Notify(job);
        var progress = new Progress<double>(p =>
        {
            // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
            var value = Math.Clamp(p, 0, 1);
            if (value <= inputs[0].Progress)
                return;
            foreach (var f in inputs)
                f.Progress = value;
            Notify(job);
        });

        try
        {
            // A Shapefile goes into the archive with its companion files (.shx, .dbf, .prj, ...): the .shp alone is no map.
            var files = new List<string>();
            var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in inputs.Select(f => f.SourcePath))
            {
                foreach (var file in new[] { source }.Concat(CompanionFiles.Of(source)))
                {
                    if (packed.Add(Path.GetFullPath(file)))
                        files.Add(file);
                }
            }
            await _combiner!.CombineAsync(files, output, format.Id, job.Preset, workDir, progress, job.CancellationToken);
            inputs[0].Outputs.Add(output);
            foreach (var f in inputs)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (OperationCanceledException)
        {
            allocator.Discard();
            foreach (var f in inputs)
                f.State = FileState.Cancelled;
        }
        catch (Exception ex)
        {
            allocator.Discard();
            _log.LogWarning(ex, "Packing {Count} file(s) into {Output} failed", inputs.Count, output);
            foreach (var f in inputs)
                MarkFailed(f, ex.Message);
        }
        Notify(job);

        static void MarkFailed(FileResult f, string detail)
        {
            f.State = FileState.Failed;
            f.ErrorKey = "error.conversion_failed";
            f.ErrorDetail = detail;
            f.Progress = 1;
        }
    }

    /// <summary>A file (not a folder) with nothing in it. Combining still packs such a file as it is.</summary>
    private static bool IsEmptyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The creation and modification dates of <paramref name="source"/>, or null when they can't be read.</summary>
    private static (DateTime Created, DateTime Modified)? DatesOf(string source)
    {
        try
        {
            return (File.GetCreationTimeUtc(source), File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Gives the outputs the creation and modification dates of the source (<see cref="OutputRule.KeepDates"/>).</summary>
    private void CopyDates((DateTime Created, DateTime Modified) dates, IEnumerable<string> outputs)
    {
        try
        {
            var (created, modified) = dates;
            foreach (var output in outputs)
            {
                if (File.Exists(output))
                {
                    File.SetCreationTimeUtc(output, created);
                    File.SetLastWriteTimeUtc(output, modified);
                }
                else if (Directory.Exists(output))
                {
                    Directory.SetCreationTimeUtc(output, created);
                    Directory.SetLastWriteTimeUtc(output, modified);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The conversion itself worked: keep the files, only the dates are new.
            _log.LogWarning(ex, "Could not copy the source's dates to {Outputs}", string.Join(", ", outputs));
        }
    }

    /// <summary>
    /// Converts one source file along its planned route.
    /// With <paramref name="finalOutput"/> false, results stay in the work directory as <paramref name="intermediate"/>
    /// files (PDF for merging, TIFF pages for a multi-page TIFF). <paramref name="ceiling"/> is the file's progress once
    /// the route is done (a slideshow's pictures leave the rest to the video).
    /// </summary>
    private async Task<IReadOnlyList<string>> RunFileAsync(
        ConversionJob job, FileResult file, int index, RoutePlanner planner, string workDir, bool finalOutput,
        string intermediate = "pdf", double ceiling = 1)
    {
        var ct = job.CancellationToken;
        if (Stopped(file))
            return [];
        var source = FormatRegistry.Detect(file.SourcePath);
        if (source is null)
            return Fail(file, "error.unsupported_source", "." + FormatRegistry.ExtensionOf(file.SourcePath));
        // A file just made with "New" in the file manager stays 0 bytes until its app saves it: no engine can read it,
        // and their errors ("Central Directory corrupt") don't say why.
        if (IsEmptyFile(file.SourcePath))
            return Fail(file, "error.empty_file", null);

        var target = !finalOutput ? intermediate
            : job.Preset.TargetFormat == BuiltInData.SameAsSource ? source.Id
            : job.Preset.TargetFormat;

        // "Save every frame": a video becomes a folder of pictures in one step of its own; "Extract Live/Motion Photo
        // video" likewise takes a photo's video, and "Live / Motion Photo" makes one of a video. Never for an
        // intermediate file, and never on the way to another format (video → JPG is otherwise one still frame, photo →
        // MP4 a video of the still picture).
        var frames = finalOutput && job.Preset.SavesFramesOf(source.Id);
        var motionPhoto = finalOutput && job.Preset.ExtractsMotionOf(source.Id);
        var livePhoto = finalOutput && job.Preset.MakesLivePhotoOf(source.Id);
        // "Recognize text": Tesseract writes a searchable PDF first, read on into DOCX, TXT, … from there. Also for the
        // PDFs that are merged into one.
        var ocr = (finalOutput || target == "pdf") && job.Preset.RecognizesTextOf(source.Id);
        var route = planner.Plan(source.Id, target, frames, motionPhoto: motionPhoto, livePhoto: livePhoto, ocr: ocr);
        if (route is null)
            return Fail(file, "error.no_route", $"{source.Id} → {target}");

        // A step that draws text without shaping (rhwp) would break Arabic, Hebrew, Indic or Thai text: such a file
        // takes a route without it (LibreOffice), or is refused with the reason rather than written broken. One
        // scan of the file's text answers this and the widths question below.
        var scripts = route.Steps.FirstOrDefault(s => s.Unshaped)?.Converter is ITextShapingCheck check
            ? ScanScripts(check, file.SourcePath, source.Id)
            : null;
        if (scripts?.NeedsShaping is { } script)
        {
            _log.LogInformation("{File} has {Script} text, which {Engine} can't draw: planning a route without it",
                file.SourcePath, script, route.Steps.First(s => s.Unshaped).Converter.DisplayName);
            route = planner.Plan(source.Id, target, frames, shaping: true, ocr: ocr);
            if (route is null)
                return Fail(file, "error.needs_shaping", script);
        }
        // Letters the engine has no widths for — anything outside ASCII and Latin-1 but the full-width CJK it
        // estimates at an em (Polish, Turkish, Vietnamese, Greek, Cyrillic, ... — rhwp#8143) — overlap at a fixed
        // half-em: still readable, so the file is never refused, it only takes a route without the unshaped step
        // when there is one.
        else if (scripts?.WithoutWidths is { } unmeasured)
        {
            _log.LogInformation("{File} has {Script} text, which {Engine} can't measure: planning a route without it",
                file.SourcePath, unmeasured, route.Steps.First(s => s.Unshaped).Converter.DisplayName);
            route = planner.Plan(source.Id, target, frames, shaping: true) ?? route;
        }

        // Rotate / flip: only an engine that turns images does it. Without one the file would come out unturned
        // (a PDF or a video re-encoded "as rotated"); with two in the route (EPS → PNG → JPG) it would turn twice.
        var turnAt = job.Preset.Image.TurnsOrMirrors ? route.FirstTransformStep() : -1;
        if (job.Preset.Image.TurnsOrMirrors && turnAt < 0)
            return Fail(file, "error.cannot_turn", $"{source.Id} → {target}");
        var unturned = turnAt < 0 ? job.Preset : Unturned(job.Preset);

        file.State = FileState.Running;
        Notify(job);
        // A file saved from a browser first has part of its progress already.
        var floor = file.Progress;
        ceiling = Math.Max(ceiling, floor);
        // The last step's allocator: how many paths it handed out tells a single output from several, and what a
        // failed or cancelled conversion started there is undone through it.
        FinalAllocator? final = null;

        try
        {
            IReadOnlyList<string> inputs = [file.SourcePath];
            // "Save as ZIP": the last step writes into the work folder and the files are packed into one ZIP afterwards.
            var zipResult = finalOutput && job.Preset.Output.ZipResult;
            for (var s = 0; s < route.Steps.Count; s++)
            {
                var step = route.Steps[s];
                var isLast = s == route.Steps.Count - 1;
                IOutputAllocator allocator = isLast && finalOutput && !zipResult
                    ? final = CreateFinalAllocator(job, file.SourcePath, index)
                    : new TempAllocator(workDir, FormatRegistry.NameWithoutExtension(file.SourcePath));

                var outputs = new List<string>();
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var stepIndex = s;
                    var progress = new Progress<double>(p =>
                    {
                        // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
                        var value = floor + (ceiling - floor) * (stepIndex + Math.Clamp(p, 0, 1)) / route.Steps.Count;
                        if (value <= file.Progress)
                            return;
                        file.Progress = value;
                        Notify(job);
                    });

                    var gate = GateFor(step.Converter);
                    await gate.WaitAsync(ct);
                    try
                    {
                        // Turned once, in step turnAt: every other step gets the preset without rotation and flip,
                        // also one before it whose engine applies image options but doesn't say it turns.
                        var preset = s == turnAt ? job.Preset : unturned;
                        // Playback speed changes once, in the step that writes the output: an animation passed from one
                        // engine to another would otherwise change speed in both.
                        if (preset.Image.ChangesSpeed && !(isLast && finalOutput))
                            preset = AtOriginalSpeed(preset);
                        var stepInfo = new ConversionStep(input, step.From, step.To, preset, allocator, workDir, step.FrameSequence,
                            key => file.NoticeKey = key, step.MotionPhoto, step.LivePhoto, fonts =>
                            {
                                // Replace the collection as one value: the UI can enumerate its snapshot while this
                                // worker reports another step without racing a mutable List.
                                file.MissingFonts = file.MissingFonts.Concat(fonts)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                Notify(job);
                            }, file.SourcePath);
                        outputs.AddRange(await step.Converter.ConvertAsync(stepInfo, progress, ct));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                inputs = outputs;
            }

            // Under "Save as ZIP" the output is still in the work folder: the source copied over it goes into the ZIP.
            var copiedSource = finalOutput && job.Preset.Output.OnlyIfSmaller && inputs.Count == 1 && CopyIfNotSmaller(file.SourcePath, inputs[0]);
            if (copiedSource)
                file.NoticeKey = "notice.pdf_not_smaller";
            // Watermark, page numbers, password: on every PDF this file became, from whichever engine.
            if (finalOutput && job.Preset.TargetFormat == "pdf")
                await FinishPdfsAsync(job.Preset, inputs, ct);

            if (zipResult && inputs.Count > 0)
                inputs = PackZip(job, file, index, inputs, ct);
            // One output, and nothing else handed out for it (a Markdown file's picture folder goes with it).
            else if (finalOutput && inputs.Count == 1 && final is { Handed: 1 })
                inputs = [NextToSource(job, file, index, inputs[0], final)];

            // The source's dates are read now: "Replace the original" may send it to the Trash below.
            var dates = finalOutput && job.Preset.Output.KeepDates ? DatesOf(file.SourcePath) : null;
            if (finalOutput && !zipResult && inputs.Count == 1)
                inputs = [ReplaceOriginal(job, file, source.Id, inputs[0], copiedSource)];

            // A name the engine took but wrote nothing to is free again.
            final?.Release(except: inputs);
            if (finalOutput)
            {
                file.Outputs.AddRange(inputs);
                if (dates is { } kept)
                    CopyDates(kept, inputs);
            }
            file.State = inputs.Count == 0 && finalOutput ? FileState.Skipped : FileState.Done;
            file.Progress = ceiling;
            Notify(job);
            return inputs;
        }
        catch (OperationCanceledException)
        {
            final?.Discard();
            file.State = FileState.Cancelled;
            Notify(job);
            return [];
        }
        catch (ConversionRefusedException ex)
        {
            final?.Discard();
            _log.LogInformation("Converting {File} was refused: {Reason}", file.SourcePath, ex.Message);
            return Fail(file, ex.ErrorKey, ex.Detail);
        }
        catch (Exception ex)
        {
            final?.Discard();
            _log.LogWarning(ex, "Converting {File} failed", file.SourcePath);
            return Fail(file, "error.conversion_failed", ex.Message);
        }

        static Preset Unturned(Preset preset)
        {
            var copy = preset.Clone();
            copy.Image.Rotation = ImageRotation.None;
            copy.Image.Flip = ImageFlip.None;
            return copy;
        }

        static Preset AtOriginalSpeed(Preset preset)
        {
            var copy = preset.Clone();
            copy.Image.AnimationSpeed = 1;
            return copy;
        }

        IReadOnlyList<string> Fail(FileResult f, string key, string? detail)
        {
            f.State = FileState.Failed;
            f.ErrorKey = key;
            f.ErrorDetail = detail;
            f.Progress = 1;
            Notify(job);
            return [];
        }
    }

    /// <summary>
    /// Packs the files one source was converted to into one ZIP (named by the preset's output rule, so the conflict
    /// policy applies to it) and returns it; empty when the rule says to skip an existing ZIP. Files keep their names
    /// inside, a clash gets "name (2).ext", and a ZIP that can't be completed is deleted.
    /// </summary>
    private IReadOnlyList<string> PackZip(ConversionJob job, FileResult file, int index, IReadOnlyList<string> files, CancellationToken ct)
    {
        var allocator = CreateFinalAllocator(job, file.SourcePath, index, oneFile: true);
        if (allocator.Allocate("zip") is not { } zipPath)
            return [];
        try
        {
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    for (var n = 2; !used.Add(name); n++)
                        name = $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}";
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, name, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
        }
        catch
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            allocator.Discard();
            throw;
        }
        return [zipPath];
    }

    /// <summary>
    /// Puts the source in place of <paramref name="output"/> when both have the same format and the output is not
    /// smaller (<see cref="OutputRule.OnlyIfSmaller"/>). True when it did.
    /// </summary>
    private static bool CopyIfNotSmaller(string source, string output)
    {
        if (!string.Equals(FormatRegistry.Detect(source)?.Id, FormatRegistry.Detect(output)?.Id, StringComparison.Ordinal)
            || !File.Exists(output) || new FileInfo(output).Length < new FileInfo(source).Length)
            return false;
        File.Copy(source, output, overwrite: true);
        return true;
    }

    /// <summary>The engine's scan; one that fails (an unreadable file) finds nothing, and the conversion reports the file.</summary>
    private TextScripts? ScanScripts(ITextShapingCheck check, string path, string format)
    {
        try
        {
            return check.ScanScripts(path, format);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check the scripts of {File}", path);
            return null;
        }
    }

    private SemaphoreSlim GateFor(IConverter converter) =>
        _engineGates.GetOrAdd(converter.Id, _ =>
        {
            var max = converter.MaxParallelism <= 0 ? Environment.ProcessorCount : converter.MaxParallelism;
            return new SemaphoreSlim(max, max);
        });

    /// <param name="oneFile">The output is one file whatever happens (a merged PDF, a multi-page TIFF, an archive).</param>
    private FinalAllocator CreateFinalAllocator(ConversionJob job, string sourcePath, int index, bool oneFile = false) =>
        new(this, oneFile ? ForOneFile(job.Preset.Output) : job.Preset.Output,
            new OutputPathResolver.Tokens(sourcePath, job.PresetDisplayName, index, job.CreatedAt));

    /// <summary>
    /// The rule for a single output file: with <see cref="OutputRule.SubfolderOnlyForSeveralFiles"/> it is saved next to
    /// the source instead of in the subfolder.
    /// </summary>
    private static OutputRule ForOneFile(OutputRule rule)
    {
        if (!rule.SubfolderOnlyForSeveralFiles || rule.Location != OutputLocation.Subfolder)
            return rule;
        var single = rule.Clone();
        single.Location = OutputLocation.SameFolder;
        return single;
    }

    /// <summary>
    /// With <see cref="OutputRule.SubfolderOnlyForSeveralFiles"/>: moves the one file a source became out of the
    /// subfolder to the folder next to the source, keeping its name (the conflict policy applies there), and removes
    /// the subfolder if that left it empty. A file that can't be moved (the policy skips an existing file, or the file
    /// is in use) stays in the subfolder.
    /// </summary>
    private string NextToSource(ConversionJob job, FileResult file, int index, string output, FinalAllocator allocator)
    {
        var rule = ForOneFile(job.Preset.Output);
        if (ReferenceEquals(rule, job.Preset.Output) || !File.Exists(output)
            // Pandoc writes a document's pictures into "<name>_files" beside it, outside the allocator: they stay together.
            || Directory.Exists(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files")))
            return output;
        // The name was already made from the pattern and renames: it is kept as it is.
        rule.FileNamePattern = Path.GetFileNameWithoutExtension(output);
        rule.Renames = [];

        lock (_reservedOutputs)
        {
            var tokens = new OutputPathResolver.Tokens(file.SourcePath, job.PresetDisplayName, index, job.CreatedAt);
            var target = OutputPathResolver.Resolve(rule, tokens, Path.GetExtension(output),
                p => !PathsEqual(p, output) && (File.Exists(p) || Directory.Exists(p) || _reservedOutputs.ContainsKey(p)));
            if (target is null || PathsEqual(target, output))
                return output;
            try
            {
                // Over an existing file only when the policy says so: one that appeared since is never replaced.
                File.Move(output, target, overwrite: rule.Conflict == ConflictPolicy.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not move {Output} next to its source", output);
                return output;
            }
            allocator.Moved(output, target);
            // Another file of this or a running job may still be written into the same subfolder: RemoveIfEmpty waits.
            RemoveIfEmpty(Path.GetDirectoryName(output)!);
            return target;
        }

        static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Platform.FileSystemPaths.Comparison);
    }

    /// <summary>How many conversions hold an output path, and whether more than one ever did.</summary>
    private sealed class Reservation
    {
        public int Holders = 1;

        /// <summary>
        /// Handed to a second conversion too: "Overwrite" gives two files that become the same name the same path. Then
        /// neither may delete it when it fails: the other one's finished file may be there.
        /// </summary>
        public bool Shared;
    }

    /// <summary>Takes <paramref name="path"/> (under the lock); true if nobody held it.</summary>
    private bool Reserve(string path)
    {
        if (_reservedOutputs.TryGetValue(path, out var reservation))
        {
            reservation.Holders++;
            reservation.Shared = true;
            return false;
        }
        _reservedOutputs[path] = new Reservation();
        return true;
    }

    /// <summary>
    /// "Replace the original" (<see cref="OutputRule.ReplaceOriginal"/>): the finished <paramref name="output"/> takes the
    /// source's place and the source goes to the Recycle Bin / Trash. The path of the result is returned. The result is
    /// first moved next to the source under a temporary name (a copy when it was saved on another drive), so a failure
    /// there leaves the source alone; then the source goes to the Trash; then the result is renamed within the folder.
    /// Anything that stops it keeps the source, with a note saying why.
    /// </summary>
    /// <param name="copiedSource">"Only if smaller" copied the source as the result: nothing changed, so the source stays
    /// where it is and the copy goes.</param>
    private string ReplaceOriginal(ConversionJob job, FileResult file, string sourceFormat, string output, bool copiedSource)
    {
        var source = file.SourcePath;
        if (!job.Preset.Output.ReplaceOriginal || job.Fetcher is not null || !File.Exists(output)
            || FormatRegistry.Detect(output) is not { } format || format.Id != sourceFormat
            || string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), Platform.FileSystemPaths.Comparison))
            return output;
        // One format can cover different kinds of file (TXT: .bat, .py, .log; PPT: .pot; AIFF: .aifc), and the result
        // takes the original's name: only the same extension, or another spelling of it (.jpeg / .jpg), replaces.
        if (!SameKindOfFile(Path.GetExtension(output), Path.GetExtension(source)))
            return output;
        if (copiedSource)
        {
            try
            {
                File.Delete(output);
                file.NoticeKey = "notice.original_unchanged";
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return output;
            }
        }
        if (_moveToTrash is null)
            return Kept(new Platform.TrashRefusal("trash.no_recycle_bin"), output);

        var staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,
            $".{Path.GetFileName(source)}.{Guid.NewGuid().ToString("N")[..8]}.filee-tmp");
        try
        {
            File.Move(output, staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not move {Output} next to {Source}", output, source);
            return Kept(new Platform.TrashRefusal("trash.cannot_place", ex.Message), output);
        }

        if (_moveToTrash(source) is { } refusal)
        {
            // The result goes back to where it was saved.
            try
            {
                File.Move(staged, output);
                return Kept(refusal, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not move {Staged} back to {Output}", staged, output);
                return Kept(refusal, staged);
            }
        }
        try
        {
            File.Move(staged, source);
            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is in the Trash already: the result goes back to the name it was saved under.
            _log.LogWarning(ex, "Could not rename {Staged} to {Source}", staged, source);
            file.NoticeKey = "notice.original_in_trash";
            file.NoticeReasonKey = "trash.cannot_place";
            file.NoticeDetail = ex.Message;
            try
            {
                File.Move(staged, output);
                return output;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(again, "Could not move {Staged} back to {Output}", staged, output);
                return staged;
            }
        }

        string Kept(Platform.TrashRefusal why, string result)
        {
            _log.LogInformation("Kept the original {Source}: {Reason} {Detail}", source, why.ReasonKey, why.Detail);
            file.NoticeKey = "notice.original_kept";
            file.NoticeReasonKey = why.ReasonKey;
            file.NoticeDetail = why.Detail;
            return result;
        }
    }

    /// <summary>Extensions that only spell the same kind of file differently ("Replace the original" may swap them).</summary>
    private static readonly string[][] Spellings =
    [
        [".jpg", ".jpeg", ".jpe", ".jfif"], [".tiff", ".tif"], [".html", ".htm"], [".md", ".markdown"], [".yaml", ".yml"],
        [".mpg", ".mpeg"], [".wav", ".wave"], [".aiff", ".aif"], [".ogg", ".oga"], [".3gp", ".3gpp"], [".m2ts", ".mts"],
        [".mov", ".qt"], [".tex", ".latex"],
    ];

    private static bool SameKindOfFile(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || Spellings.Any(s => s.Contains(a, StringComparer.OrdinalIgnoreCase) && s.Contains(b, StringComparer.OrdinalIgnoreCase));

    /// <summary>Lets go of <paramref name="path"/> (under the lock): free once nobody holds it.</summary>
    private void Unreserve(string path)
    {
        if (_reservedOutputs.TryGetValue(path, out var reservation) && --reservation.Holders <= 0)
            _reservedOutputs.Remove(path);
    }

    private bool HasReservationIn(string folder) =>
        _reservedOutputs.Keys.Any(p => Platform.FileSystemPaths.Comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), folder));

    /// <summary>
    /// Removes a folder this queue made for outputs once nothing is in it and no running conversion is to write into it
    /// (under the lock). Never a folder that was there before, and never one with anything inside.
    /// </summary>
    private void RemoveIfEmpty(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!_madeFolders.Contains(folder) || HasReservationIn(folder))
            return;
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
            _madeFolders.Remove(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Notify(ConversionJob job)
    {
        try
        {
            JobUpdated?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JobUpdated handler threw");
        }
    }

    private async Task WorkLoopAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            try
            {
                if (job.CancellationToken.IsCancellationRequested)
                {
                    job.State = JobState.Cancelled;
                    Release(job);
                    Notify(job);
                    JobFinished?.Invoke(this, job);
                    continue;
                }
                await RunAsync(job);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
    }

    /// <summary>Where every job has its work folder, named by the job's <see cref="ConversionJob.Id"/>.</summary>
    public static string DefaultWorkRoot => Path.Combine(Path.GetTempPath(), "Filee");

    /// <summary>
    /// Deletes the work folders of jobs a crash, a forced quit or a power cut kept from removing them: intermediate
    /// PDFs, TIFF pages and video frames can take gigabytes, and the OS doesn't empty its temp folder by itself. Only a
    /// job's folder nothing was written in for <paramref name="age"/> goes, so a job still running in another Filee (the
    /// command line) keeps its own; files from a browser have a sweep of their own (<c>WebDrop.DeleteStale</c>).
    /// </summary>
    public static void DeleteStaleWorkFolders(string workRoot, TimeSpan age)
    {
        try
        {
            var root = new DirectoryInfo(workRoot);
            // A temp folder everyone can write in (/tmp on Linux) may hold a link to somewhere else: never follow one.
            if (!root.Exists || root.LinkTarget is not null)
                return;
            foreach (var folder in root.EnumerateDirectories())
            {
                if (!IsJobId(folder.Name) || folder.LinkTarget is not null)
                    continue;
                try
                {
                    if (DateTime.UtcNow - LastWrite(folder) > age)
                        TryDelete(folder.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something in it can't be read: it stays, and the other folders are still looked at.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        static bool IsJobId(string name) => name.Length == 10 && name.All(char.IsAsciiHexDigitLower);

        // A folder's own time changes only when an entry right inside it does: a long job writes deeper down. Links
        // inside are not followed, and a folder that can't be read throws (the job's folder then stays).
        static DateTime LastWrite(DirectoryInfo folder)
        {
            var last = folder.LastWriteTimeUtc;
            var everything = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            foreach (var entry in folder.EnumerateFileSystemInfos("*", everything))
            {
                if (entry.LastWriteTimeUtc > last)
                    last = entry.LastWriteTimeUtc;
            }
            return last;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* a viewer may still hold a temp file; the OS cleans temp eventually */ }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
    }

    /// <summary>Allocates final output paths according to the preset's <see cref="OutputRule"/>.</summary>
    private sealed class FinalAllocator(JobQueue queue, OutputRule rule, OutputPathResolver.Tokens tokens) : IOutputAllocator
    {
        /// <summary>
        /// Every path handed out, and whether it is this conversion's alone: nothing was there before (not a file it
        /// overwrites) and no other conversion had it.
        /// </summary>
        private readonly List<(string Path, bool Own)> _allocated = [];

        /// <summary>How many paths were handed out: an output and the picture folder that goes with it are two.</summary>
        public int Handed => _allocated.Count;

        public string? Allocate(string extension, string? suffix = null)
        {
            // Reserve under a lock so two parallel files never pick the same name.
            lock (queue._reservedOutputs)
            {
                var path = OutputPathResolver.Resolve(rule, tokens, extension,
                    p => File.Exists(p) || Directory.Exists(p) || queue._reservedOutputs.ContainsKey(p), suffix);
                if (path is null)
                    return null;

                var nothingThere = !File.Exists(path) && !Directory.Exists(path);
                _allocated.Add((path, queue.Reserve(path) && nothingThere));
                var folder = Path.GetFullPath(Path.GetDirectoryName(path)!);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    queue._madeFolders.Add(folder);
                }
                // An empty extension asks for a folder (archive extraction): the converter creates it.
                return path;
            }
        }

        /// <summary>The one output was moved to <paramref name="to"/> (next to its source): its name goes with it.</summary>
        public void Moved(string from, string to)
        {
            lock (queue._reservedOutputs)
            {
                var index = _allocated.FindIndex(a => string.Equals(a.Path, from, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return;
                // Finished and in its place: nothing to undo there any more.
                _allocated[index] = (to, false);
                queue.Unreserve(from);
                queue.Reserve(to);
            }
        }

        /// <summary>
        /// Frees the names handed out but not in <paramref name="except"/> (the outputs the engine returned, which stay
        /// taken until the job ends).
        /// </summary>
        public void Release(IEnumerable<string> except)
        {
            var kept = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (queue._reservedOutputs)
            {
                foreach (var (path, _) in _allocated)
                {
                    if (!kept.Contains(path))
                        queue.Unreserve(path);
                }
                _allocated.Clear();
            }
        }

        /// <summary>
        /// Undoes a conversion that failed or was cancelled: deletes what it started writing at paths that were its
        /// alone (never a file it was to overwrite, nor one another conversion had too), so no half-written file is left
        /// where the user expects a finished one; frees every name, so trying again gets the same one instead of
        /// "name (2)"; and removes a folder it made for them if that left it empty.
        /// </summary>
        public void Discard()
        {
            lock (queue._reservedOutputs)
            {
                foreach (var (path, own) in _allocated)
                {
                    if (own && queue._reservedOutputs.TryGetValue(path, out var reservation) && !reservation.Shared)
                        TryDeleteOutput(path);
                    queue.Unreserve(path);
                }
                foreach (var folder in _allocated.Select(a => Path.GetDirectoryName(Path.GetFullPath(a.Path))!)
                             .Distinct(Platform.FileSystemPaths.Comparer).ToList())
                    queue.RemoveIfEmpty(folder);
                _allocated.Clear();
            }
        }

        private void TryDeleteOutput(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                queue._log.LogWarning(ex, "Could not delete the unfinished output {Output}", path);
            }
        }
    }

    /// <summary>Allocates unique paths inside the job's work directory for intermediate files.</summary>
    private sealed class TempAllocator(string workDir, string baseName) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var ext = extension.TrimStart('.');
            return Path.Combine(dir, ext.Length == 0 ? $"{baseName}{suffix}" : $"{baseName}{suffix}.{ext}");
        }
    }
}
