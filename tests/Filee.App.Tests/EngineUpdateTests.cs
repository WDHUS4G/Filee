// Engines the user downloaded that are older than what this Filee pins: an Update button and a one-time notice.

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Engines.Infrastructure;

namespace Filee.App.Tests;

public class EngineUpdateTests
{
    private static EnginePackageState Ffmpeg() =>
        new(EngineDownloads.Packages.Single(p => p.Id == "ffmpeg"), AppHost.Get<ILocalizer>());

    [AvaloniaFact]
    public void An_outdated_download_offers_the_new_version()
    {
        TestServices.EnsureInitialized("ko");
        var state = Ffmpeg();
        Assert.SkipUnless(EngineDownloads.CanDownload(state.Package), "FFmpeg is not downloaded on this platform");

        state.Status = EnginePackageStatus.Installed;
        Assert.False(state.CanUpdate);
        Assert.Equal(AppHost.Get<ILocalizer>()["engines.state.installed"], state.StatusText);

        state.IsOutdated = true;
        Assert.True(state.CanUpdate);
        Assert.False(state.CanInstall);
        Assert.Contains(state.Version!, state.StatusText);
    }

    [AvaloniaFact]
    public void Copies_filee_did_not_download_are_never_offered_an_update()
    {
        TestServices.EnsureInitialized("en");
        var state = Ffmpeg();
        state.IsOutdated = true;

        state.Status = EnginePackageStatus.OwnCopy;
        Assert.False(state.CanUpdate);
        state.Status = EnginePackageStatus.NotInstalled;
        Assert.False(state.CanUpdate);
    }

    [AvaloniaFact]
    public void The_notice_names_the_engines_and_update_starts_them()
    {
        TestServices.EnsureInitialized("ko");
        var updated = 0;
        var notice = new EngineUpdateNoticeWindow("FFmpeg 8.0 · LibreOffice 26.8", () => updated++);
        notice.Show();
        for (var i = 0; i < 40; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        // Disposed here: a frame left to the finalizer crashed the test process on Windows (0xC0000005).
        using (var frame = notice.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, "engine-update-notice-ko.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var update = notice.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .First(b => b.Classes.Contains("accent"));
        update.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.Equal(1, updated);
        Assert.False(notice.IsVisible);
    }
}
