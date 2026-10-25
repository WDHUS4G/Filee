// The badge over a download's progress: its text (speed, amounts, files), when it shows, and how it looks above the
// donut's ring and in the toast, light and dark.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DownloadBadgeTests
{
    private const long MB = 1_000_000;

    private static void InCulture(string name, Action test)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [AvaloniaFact]
    public void Shows_speed_and_amounts_with_one_unit_when_they_share_it()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        InCulture("en-US", () =>
        {
            Assert.Equal("2.4 MB/s · 12.3 / 48.0 MB", DownloadBadge.Format(loc, 2.4 * MB, new JobTransfer(12_300_000, 48 * MB, 0, 1, 12_300_000)));
            Assert.Equal("2.4 MB/s · 12.3 MB", DownloadBadge.Format(loc, 2.4 * MB, new JobTransfer(12_300_000, null, 0, 1, 12_300_000)));
            Assert.Equal("850 KB / 1.20 GB", DownloadBadge.Amounts(850_000, 1_200_000_000));
            Assert.Equal("640 KB/s · 2.0 / 3.1 MB · 2 of 3 files",
                DownloadBadge.Format(loc, 640_000, new JobTransfer(2 * MB, 3_100_000, 1, 3, 9 * MB)));
        });
        // The user's decimal separator, as on the Engines page.
        InCulture("de-DE", () =>
            Assert.Equal("2,4 MB/s · 12,3 / 48,0 MB", DownloadBadge.Format(loc, 2.4 * MB, new JobTransfer(12_300_000, 48 * MB, 0, 1, 12_300_000))));
    }

    [AvaloniaFact]
    public void Each_language_has_its_own_files_counter()
    {
        TestServices.EnsureInitialized("ko");
        var loc = AppHost.Get<ILocalizer>();
        InCulture("en-US", () =>
            Assert.Equal("640 KB/s · 2.0 / 3.1 MB · 3개 중 2번째", DownloadBadge.Format(loc, 640_000, new JobTransfer(2 * MB, 3_100_000, 1, 3, 9 * MB))));
        TestServices.EnsureInitialized("en");
    }

    [AvaloniaFact]
    public void Shows_once_the_speed_can_be_told_averaged_over_the_last_second()
    {
        TestServices.EnsureInitialized("en");
        var badge = new DownloadBadge(AppHost.Get<ILocalizer>());
        InCulture("en-US", () =>
        {
            Assert.Null(badge.Update(TimeSpan.Zero, null)); // nothing downloads
            Assert.Null(badge.Update(TimeSpan.Zero, new JobTransfer(0, 10 * MB, 0, 1, 0)));
            Assert.Null(badge.Update(TimeSpan.FromMilliseconds(250), new JobTransfer(MB / 2, 10 * MB, 0, 1, MB / 2))); // too early

            // 2 MB/s for the first half second, then 3 MB/s: only the last second counts once there is one.
            Assert.Equal("2.0 MB/s · 1.0 / 10.0 MB", badge.Update(TimeSpan.FromMilliseconds(500), new JobTransfer(MB, 10 * MB, 0, 1, MB)));
            badge.Update(TimeSpan.FromMilliseconds(1000), new JobTransfer(2_500_000, 10 * MB, 0, 1, 2_500_000));
            badge.Update(TimeSpan.FromMilliseconds(1500), new JobTransfer(4 * MB, 10 * MB, 0, 1, 4 * MB));
            Assert.Equal("3.0 MB/s · 5.5 / 10.0 MB", badge.Update(TimeSpan.FromMilliseconds(2000), new JobTransfer(5_500_000, 10 * MB, 0, 1, 5_500_000)));
        });
    }

    [AvaloniaFact]
    public void A_finished_file_doesnt_drag_the_speed_down()
    {
        TestServices.EnsureInitialized("en");
        var badge = new DownloadBadge(AppHost.Get<ILocalizer>());
        InCulture("en-US", () =>
        {
            badge.Update(TimeSpan.Zero, new JobTransfer(0, 4 * MB, 0, 2, 0));
            badge.Update(TimeSpan.FromSeconds(0.5), new JobTransfer(MB, 4 * MB, 0, 2, MB));
            // The first file is saved: the bytes shown restart with the second, the speed keeps counting all of them.
            Assert.Equal("2.0 MB/s · 0 KB / 4.0 MB · 2 of 2 files",
                badge.Update(TimeSpan.FromSeconds(1), new JobTransfer(0, 4 * MB, 1, 2, 2 * MB)));
        });
    }

    private static JobViewModel Downloading(string text)
    {
        var job = new ConversionJob([Path.Combine(Path.GetTempPath(), "cat.png")], new Preset { TargetFormat = "jpg" }, "JPG");
        return new JobViewModel(job, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { })
        {
            DownloadText = text,
            IsDownloading = true,
        };
    }

    private static void Pump()
    {
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(TopLevel window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void InTheme(ThemeMode mode, Action test)
    {
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.Theme.Mode;
        store.Settings.Theme.Mode = mode;
        AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "en");
        Application.Current!.RequestedThemeVariant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Motion.Set(false); // the badge snaps into place for the screenshot
        try
        {
            test();
        }
        finally
        {
            Motion.Set(true);
            store.Settings.Theme.Mode = before;
            AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "en");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void The_badge_pops_up_just_above_the_progress_ring(ThemeMode mode)
    {
        TestServices.EnsureInitialized("en");
        InTheme(mode, () =>
        {
            var radial = AppHost.Get<RadialController>();
            var model = AppHost.Get<RadialViewModel>();
            // As the drop zone opens it (the drag-mode window stays see-through until a drag brings files).
            radial.ShowForFiles(500, 500, [Path.Combine(Path.GetTempPath(), "cat.png")]);
            // Showing reads the system's setting again (Motion.Refresh): snap the badge for the checks below.
            Motion.Set(false);
            var window = radial.Window!;
            Assert.Contains("reduced-motion", window.Classes);
            try
            {
                window.DonutControl.BeginProgress();
                window.DonutControl.SetProgress(0.18);
                for (var t = 0.0; t < 1.2; t += 1 / 60.0)
                    window.DonutControl.Advance(1 / 60.0); // the slices collapse and the ring springs in
                var job = Downloading("2.4 MB/s · 12.3 / 48.0 MB");
                model.ProgressJob = job;
                Pump();

                var badge = window.FindControl<Border>("DownloadBadge")!;
                Assert.Contains("shown", badge.Classes);
                Assert.Equal(1, badge.Opacity);
                var donut = window.DonutControl;
                var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
                var bottom = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height), donut)!.Value;
                Assert.InRange(center.Y - bottom.Y, 36 + 6, 60); // above the ring, close to it
                Assert.Equal(center.X, bottom.X, 1);
                Save(window, $"donut-download-badge-{mode.ToString().ToLowerInvariant()}.png");

                // Downloads done, converting: the badge goes.
                job.IsDownloading = false;
                Pump();
                Assert.DoesNotContain("shown", badge.Classes);
                Assert.Equal(0, badge.Opacity);
            }
            finally
            {
                model.ProgressJob = null;
                window.DonutControl.LeaveProgressMode();
                radial.Close(animate: false);
            }
        });
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void The_toast_shows_the_badge_above_its_progress_bar(ThemeMode mode)
    {
        TestServices.EnsureInitialized("en");
        InTheme(mode, () =>
        {
            var conversions = AppHost.Get<ConversionService>();
            var job = Downloading("640 KB/s · 2.0 / 3.1 MB · 2 of 3 files");
            conversions.Jobs.Add(job);
            conversions.ToastJobs.Add(job);
            var toast = new ToastWindow { DataContext = conversions };
            try
            {
                toast.Show();
                Pump();
                var text = toast.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == job.DownloadText);
                Assert.True(text.IsEffectivelyVisible);
                Save(toast, $"toast-download-badge-{mode.ToString().ToLowerInvariant()}.png");

                job.IsDownloading = false;
                Pump();
                Assert.False(text.IsEffectivelyVisible);
            }
            finally
            {
                toast.Close();
                conversions.ToastJobs.Remove(job);
                conversions.Jobs.Remove(job);
            }
        });
    }
}
