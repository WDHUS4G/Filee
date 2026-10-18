// The drag-wait donut is an invisible, topmost window that catches clicks so a drag can reach it: it must close when
// the gesture ends without files, even after a conversion left the ring behind (issue: desktop icons stopped reacting
// to clicks until Filee quit).

using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.App.Services;

namespace Filee.App.Tests;

public class RadialControllerTests
{
    /// <summary>A conversion ran through the donut and its ring finished: progress mode stays on, nothing is pending.</summary>
    private static void FinishARing(DonutMenu donut)
    {
        donut.BeginProgress();
        Motion.Set(false);
        try
        {
            donut.HandOffProgress(() => { });
        }
        finally
        {
            Motion.Set(true);
        }
        Assert.True(donut.IsInProgressMode);
        Assert.False(donut.IsFinishingProgress);
    }

    [AvaloniaFact]
    public void A_drag_without_files_after_a_conversion_closes_the_invisible_window()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        radial.ShowForDrag(400, 400);
        FinishARing(radial.Window!.DonutControl);

        // A later Ctrl+drag that brings no files: a rubber-band selection on the desktop, a folder.
        radial.ShowForDrag(600, 500);
        Assert.False(radial.Window.DonutControl.IsInProgressMode);
        radial.OnDragGestureEnded();

        Assert.False(radial.IsVisible);
        Assert.False(radial.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Esc_closes_a_donut_whose_ring_already_finished()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        radial.ShowForDrag(400, 400);
        FinishARing(radial.Window!.DonutControl);

        radial.Close(animate: false);

        Assert.False(radial.IsVisible);
        Assert.False(radial.Window.IsVisible);
    }
}
