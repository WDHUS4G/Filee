// A watch folder's recent results: converted and failed files from the history, and moving originals after a retry.

using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Tests;

public class WatchResultsTests
{
    [Fact]
    public void A_file_that_converted_after_failing_no_longer_counts_as_failed()
    {
        var preset = new Preset { TargetFormat = "pdf" };
        var history = new List<HistoryEntry> // newest first, as the history keeps them
        {
            new() { WatchRuleId = "inbox", Sources = ["a.jpg"] }, // the retry of a.jpg worked
            new() { WatchRuleId = "other", Sources = ["x.jpg"], Failures = [new() { Source = "x.jpg" }] },
            new()
            {
                WatchRuleId = "inbox",
                Sources = ["a.jpg", "b.jpg", "c.jpg"],
                Failures = [new() { Source = "a.jpg" }, new() { Source = "b.jpg", ErrorKey = "error.conversion_failed" }],
                Preset = preset,
            },
        };

        var results = WatchResults.Of(history, "inbox");

        Assert.Equal(2, results.Converted); // a.jpg (retried) and c.jpg
        var failure = Assert.Single(results.Failures);
        Assert.Equal("b.jpg", failure.Failure.Source);
        Assert.Same(preset, failure.Preset);
        Assert.True(WatchResults.Of(history, "nothing").IsEmpty);
    }

    [Fact]
    public void Originals_move_into_the_originals_folder_only_from_the_watched_folder()
    {
        using var dir = new TempDir();
        var inbox = Path.Combine(dir.Path, "Inbox");
        Directory.CreateDirectory(inbox);
        var inside = Path.Combine(inbox, "scan.jpg");
        File.WriteAllText(inside, "x");
        var outside = dir.File("elsewhere.jpg");
        var rule = new WatchRule { Folder = inbox, Originals = AfterConversion.MoveToOriginals };

        FolderWatcher.MoveToOriginals(rule, inside, NullLogger.Instance);
        FolderWatcher.MoveToOriginals(rule, outside, NullLogger.Instance);

        Assert.False(File.Exists(inside));
        Assert.True(File.Exists(Path.Combine(inbox, "originals", "scan.jpg")));
        Assert.True(File.Exists(outside));
    }
}
