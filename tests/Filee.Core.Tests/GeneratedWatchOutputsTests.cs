using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class GeneratedWatchOutputsTests
{
    [Fact]
    public void Extracted_result_trees_are_ignored_after_restart_without_excluding_other_folders()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, OutputFolder = dir.Path };
        var output = Directory.CreateDirectory(Path.Combine(dir.Path, "extracted")).FullName;
        var child = dir.File("extracted/image.png");
        Directory.CreateDirectory(Path.Combine(dir.Path, "incoming"));
        var incoming = dir.File("incoming/image.png");
        var results = new GeneratedWatchOutputs(rule);
        Assert.True(results.Remember(output));
        Assert.False(results.Remember(dir.Path)); // the entire inbox must never become an ignored result tree
        results.Save();
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(Assert.Single(Directory.GetFiles(dir.Path, ".filee-watch-*.json"))).HasFlag(FileAttributes.Hidden));

        var reopened = new GeneratedWatchOutputs(rule);
        reopened.Load();
        Assert.True(reopened.Contains(child));
        Assert.False(reopened.Contains(incoming));
        Directory.Delete(output, true);
        Assert.False(reopened.Contains(child));
        reopened.Save();
    }

    [Fact]
    public void Missing_or_outside_results_are_not_remembered_and_null_state_entries_are_ignored()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        var rule = new WatchRule { Folder = dir.Path };
        var results = new GeneratedWatchOutputs(rule);
        Assert.False(results.Remember(outside.File("elsewhere.png")));
        Assert.False(results.Remember(Path.Combine(dir.Path, "missing.png")));
        results.Save();
        var state = Assert.Single(Directory.GetFiles(dir.Path, ".filee-watch-*.json"));
        File.SetAttributes(state, FileAttributes.Normal); // simulate editing the otherwise hidden state file
        File.WriteAllText(state, "[null, {\"Path\":null}]");
        results.Load();
        Assert.False(results.Contains(dir.File("arrived.png")));
    }
}
