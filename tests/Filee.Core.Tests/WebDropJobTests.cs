// Files from a browser through a whole conversion: saved into the download folder (or a work folder that is deleted
// afterwards with "Save only the converted file"), converted next to where the user wanted them, failures reported
// per file.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class WebDropJobTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private static JobQueue Queue() =>
        new(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("png", "jpg"), new ConversionEdge("png", "pdf"))]));

    private static async Task<ConversionJob> Convert(WebDrop drop, WebDropOptions options, Preset preset)
    {
        using var downloader = new WebDownloader();
        var fetcher = drop.CreateFetcher(options, downloader);
        await using var queue = Queue();
        var job = new ConversionJob(drop.Placeholders, fetcher.Apply(preset), "Test", fetcher);
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task The_original_is_kept_in_the_download_folder_and_converted_next_to_it()
    {
        using var downloads = new TempDir();
        using var work = new TempDir();
        var drop = new WebDrop([WebDropItem.FromBytes("cat.png", Png)], work.Path);

        var job = await Convert(drop, new WebDropOptions(downloads.Path, SaveOnlyConverted: false),
            new Preset { TargetFormat = "jpg", Output = { Location = OutputLocation.SameFolder } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(Path.Combine(downloads.Path, "cat.png"), job.Files[0].SourcePath);
        Assert.Equal([Path.Combine(downloads.Path, "cat.jpg")], job.Outputs);
        Assert.True(File.Exists(Path.Combine(downloads.Path, "cat.png")));
        Assert.Equal([Path.Combine(downloads.Path, "cat.png")], job.Sources); // the history records where it went
        Assert.False(Directory.Exists(drop.WorkFolder));
    }

    [Fact]
    public async Task With_save_only_converted_the_original_is_deleted_and_the_output_still_goes_to_the_download_folder()
    {
        using var downloads = new TempDir();
        using var work = new TempDir();
        var drop = new WebDrop([WebDropItem.FromBytes("cat.png", Png), WebDropItem.FromBytes("dog.png", Png)], work.Path);

        var job = await Convert(drop, new WebDropOptions(downloads.Path, SaveOnlyConverted: true),
            new Preset { TargetFormat = "jpg", Output = { Location = OutputLocation.SameFolder } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["cat.jpg", "dog.jpg"], Directory.GetFiles(downloads.Path).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(drop.WorkFolder));
        Assert.All(job.Files, f => Assert.False(File.Exists(f.SourcePath)));
    }

    [Fact]
    public async Task A_subfolder_is_made_in_the_download_folder_and_a_fixed_folder_stays_fixed()
    {
        using var downloads = new TempDir();
        using var fixedFolder = new TempDir();
        using var work = new TempDir();

        var sub = await Convert(new WebDrop([WebDropItem.FromBytes("a.png", Png)], work.Path), new WebDropOptions(downloads.Path, true),
            new Preset { TargetFormat = "pdf", Output = { Location = OutputLocation.Subfolder, SubfolderName = "converted" } });
        Assert.Equal([Path.Combine(downloads.Path, "converted", "a.pdf")], sub.Outputs);

        var custom = await Convert(new WebDrop([WebDropItem.FromBytes("b.png", Png)], work.Path), new WebDropOptions(downloads.Path, true),
            new Preset { TargetFormat = "pdf", Output = { Location = OutputLocation.CustomFolder, CustomFolder = fixedFolder.Path } });
        Assert.Equal([Path.Combine(fixedFolder.Path, "b.pdf")], custom.Outputs);
    }

    [Fact]
    public async Task A_file_that_cant_be_saved_fails_with_its_reason_and_the_others_still_convert()
    {
        using var downloads = new TempDir();
        using var work = new TempDir();
        var drop = new WebDrop(
        [
            WebDropItem.FromBytes("good.png", Png),
            WebDropItem.FromBytes("bad.png", "MZ\x90\0"u8.ToArray()),
            WebDropItem.FromStream("gone.png", _ => throw new IOException("The source app went away")),
        ], work.Path);

        var job = await Convert(drop, new WebDropOptions(downloads.Path, SaveOnlyConverted: true), new Preset { TargetFormat = "jpg" });

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal(FileState.Done, job.Files[0].State);
        Assert.Equal(("error.web_blocked", FileState.Failed), (job.Files[1].ErrorKey, job.Files[1].State));
        Assert.Equal(("error.web_download_failed", FileState.Failed), (job.Files[2].ErrorKey, job.Files[2].State));
        Assert.Equal(["good.jpg"], Directory.GetFiles(downloads.Path).Select(Path.GetFileName));
        Assert.Equal(1, job.Progress);
        Assert.False(Directory.Exists(drop.WorkFolder));
    }

    [Fact]
    public async Task A_job_cancelled_before_it_runs_still_cleans_up_and_ends_the_transfer()
    {
        using var downloads = new TempDir();
        using var work = new TempDir();
        var ended = false;
        var drop = new WebDrop([WebDropItem.FromStream("a.png", _ => Task.FromResult<Stream>(new MemoryStream(Png)), owner: new Disposer(() => ended = true))], work.Path);
        using var downloader = new WebDownloader();
        var fetcher = drop.CreateFetcher(new WebDropOptions(downloads.Path, true), downloader);
        var gate = new TaskCompletionSource();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("png", "jpg"))]));
        var finished = new TaskCompletionSource<ConversionJob>();
        queue.JobFinished += (_, j) => finished.TrySetResult(j);

        var job = queue.Enqueue(drop.Placeholders, fetcher.Apply(new Preset { TargetFormat = "jpg" }), "Test", fetcher);
        job.Cancel();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.True(ended);
        Assert.Empty(Directory.GetFiles(downloads.Path));
    }

    [Fact]
    public void Next_to_the_source_means_the_download_folder_only_for_this_job()
    {
        using var downloads = new TempDir();
        var drop = new WebDrop([WebDropItem.FromBytes("a.png", Png)], downloads.Path);
        using var downloader = new WebDownloader();
        var preset = new Preset { TargetFormat = "jpg", Output = { Location = OutputLocation.SameFolder } };

        var applied = drop.CreateFetcher(new WebDropOptions(downloads.Path, true), downloader).Apply(preset);

        Assert.Equal(downloads.Path, applied.Output.SourceFolder);
        Assert.Null(preset.Output.SourceFolder);
        var json = System.Text.Json.JsonSerializer.Serialize(applied.Output);
        Assert.DoesNotContain("SourceFolder", json, StringComparison.OrdinalIgnoreCase);

        var resolved = OutputPathResolver.Resolve(applied.Output,
            new OutputPathResolver.Tokens(Path.Combine(Path.GetTempPath(), "work", "a.png"), "x", 1, DateTime.Now), "jpg", _ => false);
        Assert.Equal(Path.Combine(downloads.Path, "a.jpg"), resolved);
    }

    [Fact]
    public void Stale_work_folders_are_deleted_and_fresh_ones_kept()
    {
        using var root = new TempDir();
        var stale = Directory.CreateDirectory(Path.Combine(root.Path, "old")).FullName;
        File.WriteAllText(Path.Combine(stale, "x.png"), "x");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        var fresh = Directory.CreateDirectory(Path.Combine(root.Path, "new")).FullName;

        WebDrop.DeleteStale(root.Path, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    /// <summary>Saves each source halfway, waits for the test, then finishes it.</summary>
    private sealed class HalfwayFetcher(IReadOnlyDictionary<string, long?> sizes) : ISourceFetcher
    {
        public TaskCompletionSource Halfway { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiting;

        public bool Fetches(string source) => sizes.ContainsKey(source);

        public async Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
        {
            var size = sizes[source];
            progress.Report(new TransferProgress(1000, size));
            if (Interlocked.Increment(ref _waiting) == sizes.Count)
                Halfway.TrySetResult();
            await Go.Task.WaitAsync(cancellationToken);
            progress.Report(new TransferProgress(size ?? 2000, size));
            var path = Path.Combine(Path.GetTempPath(), "filee-tests", Guid.NewGuid().ToString("N")[..8], Path.GetFileName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Png, cancellationToken);
            return path;
        }

        public void Release(ConversionJob job)
        {
        }
    }

    [Fact]
    public async Task A_job_tells_what_it_is_saving_summed_over_its_downloads()
    {
        using var dir = new TempDir();
        var local = dir.File("local.png");
        var fetcher = new HalfwayFetcher(new Dictionary<string, long?> { ["web/a.png"] = 4000, ["web/b.png"] = 6000 });
        await using var queue = Queue();
        var job = new ConversionJob(["web/a.png", local, "web/b.png"], new Preset { TargetFormat = "jpg" }, "Test", fetcher);
        Assert.Null(job.Transfer()); // nothing is being saved yet

        var run = queue.RunAsync(job);
        await fetcher.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(new JobTransfer(2000, 10_000, 0, 2, 2000), job.Transfer());
        Assert.Equal(0.125, job.Files[0].Progress, 3); // 1000 of 4000, saving being the first half of a file's progress
        fetcher.Go.SetResult();
        await run;

        Assert.Null(job.Transfer());
        Assert.All(job.Files.Where(f => f.NeedsFetch), f => Assert.True(f.Saved));
        Assert.False(job.Files[1].NeedsFetch);
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public void A_total_is_only_given_when_every_download_says_its_size()
    {
        var fetcher = new HalfwayFetcher(new Dictionary<string, long?> { ["web/a.png"] = 4000, ["web/b.png"] = null });
        var job = new ConversionJob(["web/a.png", "web/b.png"], new Preset { TargetFormat = "jpg" }, "Test", fetcher);
        foreach (var file in job.Files)
            file.State = FileState.Running;
        job.Files[0].Transfer = new TransferProgress(1000, 4000);
        job.Files[1].Transfer = new TransferProgress(500, null);
        Assert.Equal(new JobTransfer(1500, null, 0, 2, 1500), job.Transfer());

        // The second one is saved: what is left to show is the first, the speed still counts both.
        job.Files[1].Transfer = new TransferProgress(900, null);
        job.Files[1].Saved = true;
        Assert.Equal(new JobTransfer(1000, 4000, 1, 2, 1900), job.Transfer());
    }

    private sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
