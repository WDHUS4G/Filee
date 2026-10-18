using Filee.App.Services.Triggers;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class GestureDetectorTests
{
    private static GestureDetector Create(params TriggerGesture[] gestures)
    {
        var detector = new GestureDetector { SystemDragThreshold = 4 };
        detector.Configure(gestures);
        return detector;
    }

    private static readonly TriggerGesture CtrlDrag = new() { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, Button = TriggerMouseButton.Left };

    [Fact]
    public void Drag_fires_after_threshold_with_exact_modifiers()
    {
        var detector = Create(CtrlDrag);
        var started = 0;
        detector.DragStarted += (_, _, _) => started++;

        detector.MouseDown(TriggerMouseButton.Left, 100, 100, ModifierKeys.Ctrl);
        detector.MouseMove(102, 101, ModifierKeys.Ctrl);
        Assert.Equal(0, started);
        detector.MouseMove(110, 100, ModifierKeys.Ctrl);
        detector.MouseMove(150, 100, ModifierKeys.Ctrl);
        Assert.Equal(1, started);
    }

    [Fact]
    public void Wrong_modifiers_or_button_do_not_fire()
    {
        var detector = Create(CtrlDrag);
        var started = 0;
        detector.DragStarted += (_, _, _) => started++;

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl | ModifierKeys.Shift);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl | ModifierKeys.Shift);
        detector.MouseUp(TriggerMouseButton.Left, 50, 0);
        detector.MouseDown(TriggerMouseButton.Right, 0, 0, ModifierKeys.Ctrl);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl);

        Assert.Equal(0, started);
    }

    [Fact]
    public void Modifier_pressed_after_the_drag_started_still_fires_once()
    {
        var detector = Create(CtrlDrag);
        var started = 0;
        detector.DragStarted += (_, _, _) => started++;

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseMove(40, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        detector.MouseMove(50, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);

        Assert.Equal(1, started);
    }

    [Fact]
    public void Drag_end_is_reported_only_after_a_drag_started()
    {
        var detector = Create(CtrlDrag);
        var ended = 0;
        detector.DragEnded += (_, _) => ended++;

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl);
        detector.MouseUp(TriggerMouseButton.Left, 0, 0); // a click, not a drag
        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl);
        detector.MouseMove(60, 0, ModifierKeys.Ctrl);
        detector.MouseUp(TriggerMouseButton.Left, 60, 0);

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Scope_filter_blocks_gestures()
    {
        var detector = Create(CtrlDrag);
        detector.ScopeFilter = (_, x, _) => x < 1000;
        var started = 0;
        detector.DragStarted += (_, _, _) => started++;

        detector.MouseDown(TriggerMouseButton.Left, 2000, 0, ModifierKeys.Ctrl);
        detector.MouseMove(2100, 0, ModifierKeys.Ctrl);

        Assert.Equal(0, started);
    }

    [Fact]
    public void Paused_detector_ignores_everything()
    {
        var detector = Create(CtrlDrag, new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space" });
        detector.Paused = true;
        var fired = 0;
        detector.DragStarted += (_, _, _) => fired++;
        detector.KeyChordTriggered += (_, _, _) => fired++;

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.Ctrl);
        detector.MouseMove(80, 0, ModifierKeys.Ctrl);
        detector.KeyDown("Space", ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Key_chord_uses_last_cursor_position()
    {
        var detector = Create(new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space" });
        (int X, int Y)? at = null;
        detector.KeyChordTriggered += (_, x, y) => at = (x, y);

        detector.MouseMove(321, 654, ModifierKeys.None);
        detector.KeyDown("space", ModifierKeys.Ctrl | ModifierKeys.Alt);

        Assert.Equal((321, 654), at);
    }

    [Fact]
    public void Hold_fires_when_timer_elapses_without_movement()
    {
        var detector = Create(new TriggerGesture { Kind = TriggerKind.Hold, Modifiers = ModifierKeys.Alt, Button = TriggerMouseButton.Middle });
        long token = 0;
        var held = 0;
        detector.HoldTimerRequested += (t, _) => token = t;
        detector.HoldTriggered += (_, _, _) => held++;

        detector.MouseDown(TriggerMouseButton.Middle, 10, 10, ModifierKeys.Alt);
        detector.HoldElapsed(token);
        Assert.Equal(1, held);

        detector.MouseDown(TriggerMouseButton.Middle, 10, 10, ModifierKeys.Alt);
        detector.MouseMove(60, 10, ModifierKeys.Alt); // moving cancels a hold
        detector.HoldElapsed(token);
        Assert.Equal(1, held);
    }
}
