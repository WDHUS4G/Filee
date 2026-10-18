// Recent conversions remember which files failed, why, and the preset they ran with, so they can be converted again.

using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class HistoryEntryTests
{
    [Fact]
    public async Task Failed_files_are_kept_with_their_reason_and_the_preset_as_it_ran()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var notes = dir.File("notes.xyz");
        var output = Path.Combine(dir.Path, "out");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var preset = new Preset { Id = "to-png", TargetFormat = "png", Output = { Location = OutputLocation.CustomFolder, CustomFolder = output } };
        var job = new ConversionJob([photo, notes], preset, "PNG");
        await queue.RunAsync(job);

        var store = new UserDataStore(dir.Path);
        store.Load();
        store.AddHistory(HistoryEntry.From(job));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var entry = Assert.Single(reloaded.History);

        Assert.Equal(JobState.CompletedWithErrors, entry.State);
        var failure = Assert.Single(entry.Failures);
        Assert.Equal(notes, failure.Source);
        Assert.Equal("error.unsupported_source", failure.ErrorKey);
        Assert.Equal("png", entry.Preset!.TargetFormat);
        Assert.Equal(OutputLocation.CustomFolder, entry.Preset.Output.Location);
        Assert.Equal(output, entry.Preset.Output.CustomFolder);
    }

    [Fact]
    public async Task A_job_without_failures_keeps_no_preset()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var job = new ConversionJob([photo], new Preset { TargetFormat = "png" }, "PNG");
        await queue.RunAsync(job);

        var entry = HistoryEntry.From(job);

        Assert.Empty(entry.Failures);
        Assert.Null(entry.Preset);
    }
}
