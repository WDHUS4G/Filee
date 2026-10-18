// Recent conversions list the files that failed with their reason and can convert them again.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class FailedFilesTests
{
    [AvaloniaFact]
    public void Failures_saved_by_older_versions_are_listed_in_the_current_language()
    {
        TestServices.EnsureInitialized("ko");
        var entry = new HistoryEntry
        {
            PresetName = "PNG",
            State = JobState.CompletedWithErrors,
            Sources = ["photo.jpg", "notes.xyz"],
            Errors = ["notes.xyz: error.unsupported_source xyz"],
        };

        var item = new HistoryItemViewModel(entry, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), AppHost.Get<ConversionService>());

        var failure = Assert.Single(item.Failures);
        Assert.Equal("notes.xyz", failure.Name);
        Assert.Equal($"{AppHost.Get<ILocalizer>()["error.unsupported_source"]} (xyz)", failure.Reason);
        Assert.Equal("1개 실패", item.FailureSummary);
        Assert.False(item.CanRetry); // no preset saved: nothing to retry with
    }

    [AvaloniaFact]
    public void Retry_converts_the_failed_files_that_are_still_there_with_the_saved_preset()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-retry-").FullName;
        try
        {
            var kept = Path.Combine(folder, "kept.png");
            File.WriteAllBytes(kept, [0x89, 0x50, 0x4E, 0x47]);
            var gone = Path.Combine(folder, "gone.png");
            var preset = new Preset { Id = "jpg-70", Name = "JPG 70%", TargetFormat = "jpg", Output = { Location = OutputLocation.CustomFolder, CustomFolder = Path.Combine(folder, "out") } };
            var entry = new HistoryEntry
            {
                PresetName = "JPG 70%",
                State = JobState.Failed,
                Sources = [kept, gone],
                Failures = [new HistoryFailure { Source = kept, ErrorKey = "error.conversion_failed" }, new HistoryFailure { Source = gone }],
                Preset = preset,
            };
            var conversions = AppHost.Get<ConversionService>();
            var item = new HistoryItemViewModel(entry, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), conversions);
            Assert.True(item.CanRetry);

            item.RetryCommand.Execute(null);

            var retried = conversions.Jobs[0];
            var job = retried.Job;
            Assert.Equal([kept], job.Sources);
            Assert.Equal("jpg", job.Preset.TargetFormat);
            Assert.Equal(Path.Combine(folder, "out"), job.Preset.Output.CustomFolder);

            // Let the job end inside the test: a conversion finishing after it would post to a dispatcher that is gone.
            job.Cancel();
            for (var i = 0; i < 200 && job.State is JobState.Queued or JobState.Running; i++)
            {
                Thread.Sleep(25);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            retried.DismissCommand.Execute(null);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
