// The donut's progress ring: timing rules and rendered states (running / ✓ / !).

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutProgressTests
{
    private const double Frame = 1 / 60.0;

    private static (Window Window, DonutMenu Donut) Create()
    {
        TestServices.EnsureInitialized("ko");
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP", "PDF", "TIFF", "BMP" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, donut);
    }

    /// <summary>Steps the animation clock until <paramref name="until"/> is true or the time budget runs out.</summary>
    private static double Run(DonutMenu donut, Func<bool> until, double maxSeconds = 10)
    {
        var elapsed = 0.0;
        while (!until() && elapsed < maxSeconds)
        {
            donut.Advance(Frame);
            elapsed += Frame;
        }
        return elapsed;
    }

    [AvaloniaFact]
    public void Fast_success_still_shows_the_ring_then_a_check_then_finishes()
    {
        var (window, donut) = Create();
        var done = false;

        donut.BeginProgress();
        donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => done = true); // finished instantly

        var untilCheck = Run(donut, () => donut.ShownOutcome is not null);
        Assert.Equal(DonutProgressOutcome.Succeeded, donut.ShownOutcome);
        Assert.True(untilCheck >= 0.6, $"check appeared after {untilCheck:0.00}s; the ring must be visible for at least 0.6s");
        Assert.False(done);

        var untilDone = Run(donut, () => done);
        Assert.True(done);
        Assert.InRange(untilDone, 0.9, 1.6); // hold + fade
        window.Close();
    }

    [AvaloniaFact]
    public void Hand_off_to_the_toast_is_quick()
    {
        var (window, donut) = Create();
        var done = false;
        donut.BeginProgress();
        donut.SetProgress(0.3);
        Run(donut, () => false, 1.0);

        donut.HandOffProgress(() => done = true);
        var elapsed = Run(donut, () => done);

        Assert.True(done);
        Assert.True(elapsed < 0.4);
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_the_donut_again_leaves_progress_mode()
    {
        var (window, donut) = Create();
        donut.BeginProgress();
        Assert.True(donut.IsInProgressMode);

        donut.PlayOpenAnimation();

        Assert.False(donut.IsInProgressMode);
        window.Close();
    }

    [AvaloniaFact]
    public void Reduced_motion_skips_animation_but_keeps_the_check_visible_for_a_moment()
    {
        var (window, donut) = Create(); // applies the theme, which sets Motion from settings: switch it off afterwards
        Motion.Set(false);
        try
        {
            var done = false;
            donut.BeginProgress();
            donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => done = true);

            donut.Advance(Frame);
            Assert.False(done);
            var elapsed = Run(donut, () => done);
            Assert.True(done);
            Assert.InRange(elapsed, 1.0, 2.0); // min ring time + hold, no animation time
            window.Close();
        }
        finally
        {
            Motion.Set(true);
        }
    }

    [AvaloniaFact]
    public void Ring_states_render()
    {
        var (window, donut) = Create();

        donut.BeginProgress();
        donut.SetProgress(0.42);
        Run(donut, () => false, 1.2);
        Save(window, "progress-running.png");

        donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => { });
        Run(donut, () => donut.ShownOutcome is not null);
        Run(donut, () => false, 0.4); // let the ✓ pop settle
        Save(window, "progress-success.png");

        donut.BeginProgress();
        donut.SetProgress(0.6);
        Run(donut, () => false, 1.0);
        donut.CompleteProgress(DonutProgressOutcome.Failed, () => { });
        Run(donut, () => donut.ShownOutcome is not null);
        Run(donut, () => false, 0.4);
        Save(window, "progress-failed.png");

        // Mid-collapse frame, to see the slices being sucked in.
        donut.PlayOpenAnimation();
        Run(donut, () => false, 0.6);
        donut.BeginProgress();
        Run(donut, () => false, 0.15);
        Save(window, "progress-collapsing.png");
        window.Close();
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame()!;
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new PngBitmapEncoderOptions());
    }
}
