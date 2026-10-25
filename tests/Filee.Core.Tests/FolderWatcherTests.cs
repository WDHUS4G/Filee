using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class FolderWatcherTests
{
    /// <summary>A clock the test moves by hand; timers never fire (the test calls ProcessPendingAsync).</summary>
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A watcher whose conversions run on a JobQueue with a fake jpg → png converter into the rule's output folder.</summary>
    private static (FolderWatcher Watcher, FakeTime Time, List<ConversionJob> Jobs, JobQueue Queue) Create(WatchRule rule)
    {
        var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var jobs = new List<ConversionJob>();
        var time = new FakeTime();
        var watcher = new FolderWatcher(rule, async (files, _) =>
        {
            var preset = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.CustomFolder, CustomFolder = rule.ResolvedOutputFolder } };
            var job = new ConversionJob(files, preset, "Test");
            await queue.RunAsync(job);
            jobs.Add(job);
            return job;
        }, settleTime: TimeSpan.FromSeconds(2), time: time);
        return (watcher, time, jobs, queue);
    }

    [Fact]
    public void Default_wait_is_longer_where_files_being_written_are_not_locked() =>
        Assert.Equal(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 2 : 5), FolderWatcher.DefaultSettleTime);

    [Theory]
    [InlineData(AfterConversion.Keep, false)]
    [InlineData(AfterConversion.MoveToOriginals, false)]
    [InlineData(AfterConversion.Keep, true)]
    [InlineData(AfterConversion.MoveToOriginals, true)]
    public async Task Output_can_share_the_watched_folder_without_converting_its_own_results(AfterConversion originals, bool trailingSeparator)
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, OutputFolder = dir.Path + (trailingSeparator ? Path.DirectorySeparatorChar : ""), Originals = originals };
        var (watcher, time, jobs, queue) = Create(rule);
        await using var queueLifetime = queue;
        await using var watcherLifetime = watcher;
        var source = dir.File("photo.jpg");

        watcher.Notice(source);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        var output = Assert.Single(Assert.Single(jobs).Outputs);
        Assert.Equal(Path.Combine(dir.Path, "photo.png"), output);
        Assert.True(File.Exists(output));
        Assert.Equal(originals == AfterConversion.Keep, File.Exists(source));
        if (originals == AfterConversion.MoveToOriginals)
            Assert.True(File.Exists(Path.Combine(rule.OriginalsFolder, "photo.jpg")));

        watcher.Notice(output);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);

        // A new incoming file still works after the result has been excluded.
        watcher.Notice(dir.File("next.jpg"));
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public async Task Shared_folder_results_survive_a_restart_but_a_replaced_file_is_new_input()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, OutputFolder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var (first, firstTime, firstJobs, firstQueue) = Create(rule);
        await using (firstQueue)
        await using (first)
        {
            first.Notice(dir.File("photo.jpg"));
            firstTime.Advance(3);
            Assert.Equal(1, await first.ProcessPendingAsync());
            Assert.Single(firstJobs);
        }

        var (restarted, time, jobs, queue) = Create(rule);
        await using var queueLifetime = queue;
        await using var watcherLifetime = restarted;
        Assert.True(restarted.Start()); // inbox mode scans existing files on startup
        time.Advance(3);
        Assert.Equal(0, await restarted.ProcessPendingAsync());
        Assert.Empty(jobs);
        Assert.True(File.Exists(Path.Combine(dir.Path, "photo.png")));

        // An incoming replacement with the old output's name must not be permanently blacklisted.
        var replaced = Path.Combine(dir.Path, "photo.png");
        File.WriteAllText(replaced, "a different incoming file, not the generated result");
        restarted.Notice(replaced);
        time.Advance(3);
        Assert.Equal(1, await restarted.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public void An_output_folder_above_the_watch_root_does_not_exclude_the_inputs()
    {
        using var dir = new TempDir();
        var input = dir.File("photo.jpg");
        Assert.False(FolderWatcher.ShouldIgnore(input, new WatchRule
        {
            Folder = dir.Path,
            OutputFolder = Path.GetDirectoryName(dir.Path)!,
        }));
    }

    [Fact]
    public async Task A_file_is_converted_once_it_has_settled()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path };
        var (watcher, time, jobs, queue) = Create(rule);
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(1);
        Assert.Equal(0, await watcher.ProcessPendingAsync()); // still within the settle time

        time.Advance(2);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "photo.png")));
        Assert.True(File.Exists(photo), "Keep leaves the source where it is");

        // Another event for the unchanged file does not convert it again.
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_file_that_is_still_growing_waits()
    {
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg", "part");
        watcher.Notice(photo);
        time.Advance(3);
        File.AppendAllText(photo, " and more"); // the download continues
        Assert.Equal(0, await watcher.ProcessPendingAsync());

        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_file_another_program_holds_open_waits()
    {
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(3);
        using (new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.Equal(0, await watcher.ProcessPendingAsync());

        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Theory]
    [InlineData("photo.jpg.crdownload")]
    [InlineData("~$report.docx")]
    [InlineData(".hidden.jpg")]
    [InlineData("notes.unknownformat")]
    [InlineData("converted/photo.jpg")]
    [InlineData("originals/photo.jpg")]
    [InlineData("sub/photo.jpg")]
    public void Temporary_unknown_and_own_files_are_ignored(string relative)
    {
        var rule = new WatchRule { Folder = Path.Combine(Path.GetTempPath(), "inbox") };
        Assert.True(FolderWatcher.ShouldIgnore(Path.Combine(rule.Folder, relative), rule));
    }

    [Fact]
    public void Subfolders_count_only_when_the_rule_includes_them()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "sub", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.True(FolderWatcher.ShouldIgnore(file, new WatchRule { Folder = dir.Path }));
        Assert.False(FolderWatcher.ShouldIgnore(file, new WatchRule { Folder = dir.Path, IncludeSubfolders = true }));
    }

    [Fact]
    public async Task Move_to_originals_converts_what_is_already_there_and_empties_the_folder()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var rule = new WatchRule { Folder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var (watcher, time, _, queue) = Create(rule);
        await using var __ = queue;
        await using var ___ = watcher;

        Assert.True(watcher.Start()); // picks up the file that was already waiting
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());

        Assert.False(File.Exists(photo));
        Assert.True(File.Exists(Path.Combine(dir.Path, "originals", "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "photo.png")));
    }

    [Fact]
    public async Task A_missing_folder_is_reported_instead_of_watched()
    {
        var (watcher, _, _, queue) = Create(new WatchRule { Folder = Path.Combine(Path.GetTempPath(), "filee-missing-" + Guid.NewGuid().ToString("N")) });
        await using var _ = queue;
        await using var __ = watcher;

        Assert.False(watcher.Start());
        Assert.NotNull(watcher.Error);
    }

    [Fact]
    public async Task Case_distinct_files_with_identical_metadata_are_both_converted()
    {
        using var dir = new TempDir();
        var lower = dir.File("photo.jpg", "one");
        var upper = Path.Combine(dir.Path, "PHOTO.jpg");
        if (File.Exists(upper))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        File.WriteAllText(upper, "two");
        File.SetLastWriteTimeUtc(upper, File.GetLastWriteTimeUtc(lower));
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var queueLifetime = queue;
        await using var watcherLifetime = watcher;

        watcher.Notice(lower);
        watcher.Notice(upper);
        time.Advance(3);

        Assert.Equal(2, await watcher.ProcessPendingAsync());
        var job = Assert.Single(jobs);
        Assert.Equal(2, job.Files.Count);
        Assert.Equal(2, job.Outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var result in job.Files)
        {
            Assert.Equal(FileState.Done, result.State);
            Assert.Contains(Path.GetFileName(result.SourcePath), File.ReadAllText(Assert.Single(result.Outputs)));
        }

        watcher.Notice(lower);
        watcher.Notice(upper);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public void A_case_distinct_folder_is_not_mistaken_for_the_output_folder()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, IncludeSubfolders = true };
        Directory.CreateDirectory(rule.ResolvedOutputFolder);
        var alternate = Path.Combine(dir.Path, "Converted");
        if (Directory.Exists(alternate))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        Directory.CreateDirectory(alternate);
        var input = Path.Combine(alternate, "photo.jpg");
        var output = Path.Combine(rule.ResolvedOutputFolder, "photo.jpg");
        File.WriteAllText(input, "input");
        File.WriteAllText(output, "output");

        Assert.False(FolderWatcher.ShouldIgnore(input, rule));
        Assert.True(FolderWatcher.ShouldIgnore(output, rule));
    }
}
