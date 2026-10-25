// State of one "drop" — a batch of files converted with one preset.

using Filee.Core.Presets;

namespace Filee.Core.Conversion;

public enum JobState
{
    Queued,
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
}

public enum FileState
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>Result for one source file of a job.</summary>
public sealed class FileResult
{
    /// <summary>
    /// The file converted. For a source the job fetches first (<see cref="ConversionJob.Fetcher"/>) a placeholder
    /// until it has been saved, then where it was saved.
    /// </summary>
    public required string SourcePath { get; set; }
    public FileState State { get; set; } = FileState.Pending;
    public double Progress { get; set; }
    public List<string> Outputs { get; } = [];

    /// <summary>Localization key of the error (e.g. <c>error.no_route</c>), if any.</summary>
    public string? ErrorKey { get; set; }

    /// <summary>The job's fetcher saves this source first (a file from a browser).</summary>
    public bool NeedsFetch { get; init; }

    /// <summary>The fetcher has saved it (<see cref="SourcePath"/> is the saved file now).</summary>
    public bool Saved { get; set; }

    /// <summary>Bytes saved so far by the fetcher (the last count once it is saved); null before it starts.</summary>
    public TransferProgress? Transfer { get; set; }

    /// <summary>Raw error detail for logs / tooltips.</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>
    /// Localization key of a note on a file that converted (e.g. <c>notice.pdf_not_smaller</c>: compressing saved
    /// nothing, so the PDF was copied as it is), if any.
    /// </summary>
    public string? NoticeKey { get; set; }

    /// <summary>Why, after the notice in brackets: a localization key (why the original was kept), or null.</summary>
    public string? NoticeReasonKey { get; set; }

    /// <summary>The system's own words after <see cref="NoticeReasonKey"/> (not localized), or null.</summary>
    public string? NoticeDetail { get; set; }

    /// <summary>
    /// Referenced document font families that weren't installed before rendering. Conversion still completes with the
    /// renderer's substitutions; the UI can offer trusted sources afterwards.
    /// </summary>
    public IReadOnlyList<string> MissingFonts { get; set; } = [];
}

/// <summary>A batch of files converted with one preset.</summary>
public sealed class ConversionJob
{
    private readonly CancellationTokenSource _cts = new();

    /// <param name="fetcher">Saves sources that aren't files yet before they are converted (files from a browser).</param>
    public ConversionJob(IReadOnlyList<string> sources, Preset preset, string presetDisplayName, ISourceFetcher? fetcher = null)
    {
        // A Shapefile's .dbf, .shx, .prj, ... dropped with its .shp are part of it, not files to convert on their own.
        sources = Formats.CompanionFiles.WithoutCompanions(sources);
        Preset = preset.Clone(); // later edits of the preset must not affect a running job
        PresetDisplayName = presetDisplayName;
        Fetcher = fetcher;
        Files = sources.Select(s => new FileResult { SourcePath = s, NeedsFetch = fetcher?.Fetches(s) ?? false }).ToList();
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary>The source files, as <see cref="FileResult.SourcePath"/> currently names them.</summary>
    public IReadOnlyList<string> Sources => Files.Select(f => f.SourcePath).ToList();

    /// <summary>Saves the sources that aren't files yet when the job runs; null when all of them are files.</summary>
    public ISourceFetcher? Fetcher { get; }
    public Preset Preset { get; }
    public string PresetDisplayName { get; }
    public IReadOnlyList<FileResult> Files { get; }
    public JobState State { get; internal set; } = JobState.Queued;

    /// <summary>Overall progress 0..1.</summary>
    public double Progress => Files.Count == 0 ? 1 : Files.Average(f => f.Progress);

    /// <summary>Every file written by the job.</summary>
    public IEnumerable<string> Outputs => Files.SelectMany(f => f.Outputs);

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// What the job's fetcher is saving now, summed over the files being saved (several download at once), or null
    /// when none is. The total is known only when every one of them says its size.
    /// </summary>
    public JobTransfer? Transfer()
    {
        var fetched = Files.Where(f => f.NeedsFetch)
            // Read once: the queue's threads replace these values while the UI reads them.
            .Select(f => (f.Transfer, f.Saved, Active: f.State == FileState.Running && !f.Saved))
            .ToList();
        var active = fetched.Where(f => f.Active).Select(f => f.Transfer).OfType<TransferProgress>().ToList();
        if (active.Count == 0)
            return null;
        long? total = active.All(t => t.Total is not null) ? active.Sum(t => t.Total!.Value) : null;
        return new JobTransfer(active.Sum(t => t.Bytes), total, fetched.Count(f => f.Saved), fetched.Count,
            fetched.Sum(f => f.Transfer?.Bytes ?? 0));
    }

    /// <summary>Requests cancellation. Running engines stop as soon as they can.</summary>
    public void Cancel() => _cts.Cancel();
}

/// <summary>
/// Brings sources that aren't files yet onto disk when their job runs: files dragged out of a web browser are saved
/// (or downloaded) then, so the download shows its progress and errors like the conversion.
/// </summary>
public interface ISourceFetcher
{
    /// <summary>True for a source this fetcher saves (the others are files already).</summary>
    bool Fetches(string source);

    /// <summary>Saves the file <paramref name="source"/> stands for and returns its path.</summary>
    /// <exception cref="ConversionRefusedException">A reason the user can act on (a web page instead of a file, …).</exception>
    Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken);

    /// <summary>
    /// The job is over (finished, failed or cancelled before it ran): delete what only the conversion needed. Called
    /// once, before <see cref="JobQueue.JobFinished"/>.
    /// </summary>
    void Release(ConversionJob job);
}

/// <summary>How far saving one source has come: bytes so far, and the size when the source says it.</summary>
public readonly record struct TransferProgress(long Bytes, long? Total)
{
    /// <summary>0..1, or null when the size isn't known.</summary>
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Bytes / Total.Value, 0, 1) : null;
}

/// <summary>What a job is saving now (see <see cref="ConversionJob.Transfer"/>).</summary>
/// <param name="Bytes">Bytes saved so far by the files being saved.</param>
/// <param name="Total">Their size together, or null when one of them doesn't say.</param>
/// <param name="Saved">Files of the job already saved.</param>
/// <param name="Count">Files of the job that are saved first.</param>
/// <param name="Received">Every byte saved so far, finished files included: it only grows, for the speed.</param>
public readonly record struct JobTransfer(long Bytes, long? Total, int Saved, int Count, long Received);
