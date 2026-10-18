// The slice under a dragged file lights up even after a frame of the donut's animation loop got lost.

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutHighlightTests
{
    [AvaloniaFact]
    public void A_lost_animation_frame_does_not_keep_the_slice_dark()
    {
        TestServices.EnsureInitialized("ko");
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP", "PDF" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // As after a frame request that never came back: the loop still counts as running, its last frame long ago.
        Set(donut, "_animating", true);
        Set(donut, "_lastTick", Environment.TickCount64 - DonutMenu.StalledLoopMs * 4);

        donut.SetExternalHighlight(2);
        for (var i = 0; i < 90 && donut.LitAmount(2) < 0.9; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(donut.LitAmount(2) > 0.9, $"lit {donut.LitAmount(2):0.00}");
        window.Close();
    }

    [AvaloniaFact]
    public void An_opening_donut_appears_even_when_no_frame_ever_comes()
    {
        TestServices.EnsureInitialized("ko");
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP", "PDF" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Animations on, whatever the machine reports (build servers report Windows animations off).
        Filee.App.Services.Motion.Configure(false, () => false);
        donut.PlayOpenAnimation(); // the render timer never ticks in this test: every frame request is lost
        Assert.Equal(0, Open(donut).Max());
        // The watchdog's checks, each after the loop got no frame for a while: two retries, then the donut is drawn open.
        for (var check = 0; check <= DonutMenu.FrameRetries; check++)
        {
            Set(donut, "_lastTick", Environment.TickCount64 - DonutMenu.StalledLoopMs * 2);
            donut.CheckLoop();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.All(Open(donut), open => Assert.Equal(1, open, 3));
        window.Close();
    }

    [AvaloniaFact]
    public void A_highlight_settles_without_dipping_below_zero()
    {
        TestServices.EnsureInitialized("ko");
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        donut.SetExternalHighlight(1);
        for (var i = 0; i < 120; i++)
            donut.Advance(1 / 60.0);
        Assert.True(donut.LitAmount(1) > 0.99);

        donut.SetExternalHighlight(2); // the pointer moves on: slice 1 lets go
        var lowest = double.MaxValue;
        for (var i = 0; i < 120; i++)
        {
            donut.Advance(1 / 60.0);
            lowest = Math.Min(lowest, donut.LitAmount(1));
        }
        Assert.True(lowest >= 0, $"dipped to {lowest:0.000}");
        Assert.Equal(0, donut.LitAmount(1), 3);
    }

    [AvaloniaFact]
    public void Highlights_settle_when_frames_come_late()
    {
        TestServices.EnsureInitialized("ko");
        Filee.App.Services.Motion.Configure(false, () => false);
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP", "PDF" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        // Every frame as late as the loop allows (a busy GPU, a screen recorder): the pointer crosses the slices.
        const double late = 0.05;
        for (var slice = 0; slice < 4; slice++)
        {
            donut.SetExternalHighlight(slice);
            for (var i = 0; i < 6; i++)
                donut.Advance(late);
        }
        for (var i = 0; i < 60; i++)
        {
            donut.Advance(late);
            for (var slice = 0; slice < 3; slice++)
                Assert.InRange(donut.LitAmount(slice), 0, 1);
        }

        Assert.All(Enumerable.Range(0, 3), slice => Assert.Equal(0, donut.LitAmount(slice), 3));
        Assert.Equal(1, donut.LitAmount(3), 3);
    }

    [AvaloniaFact] // switching the setting restyles the open windows, which belong to the UI thread
    public void Following_the_system_reads_it_again_when_a_window_opens()
    {
        var reduced = true;
        try
        {
            Filee.App.Services.Motion.Configure(null, () => reduced);
            Assert.False(Filee.App.Services.Motion.Enabled); // the system reported animations off for a moment

            reduced = false;
            Filee.App.Services.Motion.Refresh();
            Assert.True(Filee.App.Services.Motion.Enabled);

            Filee.App.Services.Motion.Configure(true, () => false); // the user's own choice wins over the system
            Filee.App.Services.Motion.Refresh();
            Assert.False(Filee.App.Services.Motion.Enabled);
        }
        finally
        {
            Filee.App.Services.Motion.Configure(false, () => false);
        }
    }

    private static double[] Open(DonutMenu donut) =>
        (double[])typeof(DonutMenu).GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(donut)!;

    private static void Set(DonutMenu donut, string field, object value) =>
        typeof(DonutMenu).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(donut, value);
}

/// <summary>
/// Tests that switch the global animation setting (<see cref="Filee.App.Services.Motion"/>) run on their own: run next
/// to others, the app's start-up in those would switch it back halfway through.
/// </summary>
[CollectionDefinition("Motion", DisableParallelization = true)]
public sealed class MotionCollection;
