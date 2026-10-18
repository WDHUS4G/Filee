// Pure state machine that turns low-level mouse/keyboard events into "open the donut" gestures.
// It knows nothing about SharpHook or Avalonia so it can be unit tested (see Filee.App.Tests).
//
// Drag gesture:  button down with the exact modifiers  →  (armed)  →  moved past threshold  →  DragStarted
//                →  button up  →  DragEnded
// Hold gesture:  button down with modifiers  →  (armed)  →  HoldElapsed without moving  →  HoldTriggered
// Key chord:     key down with the exact modifiers  →  KeyChordTriggered at the last known cursor position

using Filee.Core.Settings;

namespace Filee.App.Services.Triggers;

/// <summary>Detects configured <see cref="TriggerGesture"/>s in a stream of input events. Thread-safe.</summary>
public sealed class GestureDetector
{
    private readonly object _lock = new();
    private IReadOnlyList<TriggerGesture> _gestures = [];
    private Armed? _armed;
    private bool _dragActive;
    private bool _lateRejected; // scope check failed for this drag; don't re-check on every move
    private int _lastX, _lastY;

    private sealed record Armed(TriggerGesture Gesture, TriggerMouseButton Button, int X, int Y, long Token);

    /// <summary>
    /// Decides whether a gesture may start at a screen point (scope / excluded apps / own windows).
    /// Called on the hook thread; must be fast.
    /// </summary>
    public Func<TriggerGesture, int, int, bool> ScopeFilter { get; set; } = (_, _, _) => true;

    /// <summary>Fallback drag threshold (physical pixels) when a gesture uses 0 = "system default".</summary>
    public int SystemDragThreshold { get; set; } = 4;

    /// <summary>When true nothing is detected (tray "Pause").</summary>
    public bool Paused { get; set; }

    public event Action<TriggerGesture, int, int>? DragStarted;
    public event Action<int, int>? DragEnded;
    public event Action<TriggerGesture, int, int>? HoldTriggered;
    public event Action<TriggerGesture, int, int>? KeyChordTriggered;

    /// <summary>Raised when a hold gesture is armed; the owner must call <see cref="HoldElapsed"/> after the delay.</summary>
    public event Action<long, int>? HoldTimerRequested;

    public void Configure(IEnumerable<TriggerGesture> gestures)
    {
        lock (_lock)
        {
            _gestures = gestures.Where(g => g.Enabled).Select(g => g.Clone()).ToList();
            _armed = null;
        }
    }

    public void MouseDown(TriggerMouseButton button, int x, int y, ModifierKeys modifiers)
    {
        TriggerGesture? holdGesture = null;
        long token = 0;
        lock (_lock)
        {
            _lastX = x;
            _lastY = y;
            _armed = null;
            _lateRejected = false;
            if (Paused)
                return;

            var gesture = _gestures.FirstOrDefault(g =>
                g.Kind is TriggerKind.Drag or TriggerKind.Hold &&
                g.Button == button &&
                g.Modifiers == modifiers);
            if (gesture is null || !ScopeFilter(gesture, x, y))
                return;

            token = Environment.TickCount64 ^ (x * 7919L) ^ y;
            _armed = new Armed(gesture, button, x, y, token);
            if (gesture.Kind == TriggerKind.Hold)
                holdGesture = gesture;
        }

        if (holdGesture is not null)
            HoldTimerRequested?.Invoke(token, holdGesture.HoldMilliseconds);
    }

    /// <summary>Pointer moved (physical pixels). <paramref name="heldButton"/> is the button currently held, or None.</summary>
    public void MouseMove(int x, int y, ModifierKeys modifiers, TriggerMouseButton heldButton = TriggerMouseButton.None)
    {
        TriggerGesture? fire;
        lock (_lock)
        {
            _lastX = x;
            _lastY = y;
            fire = _armed is null ? DetectLateModifier(x, y, modifiers, heldButton) : DetectDragPastThreshold(x, y);
            if (fire is not null)
                _dragActive = true;
        }

        if (fire is not null)
            DragStarted?.Invoke(fire, x, y);
    }

    /// <summary>
    /// An armed gesture moved: a drag gesture fires once the threshold is passed; a hold gesture is cancelled.
    /// Caller holds the lock.
    /// </summary>
    private TriggerGesture? DetectDragPastThreshold(int x, int y)
    {
        var armed = _armed!;
        var threshold = armed.Gesture.DragThreshold > 0 ? armed.Gesture.DragThreshold : SystemDragThreshold;
        if (Math.Abs(x - armed.X) <= threshold && Math.Abs(y - armed.Y) <= threshold)
            return null;

        _armed = null;
        return armed.Gesture.Kind == TriggerKind.Drag ? armed.Gesture : null;
    }

    /// <summary>
    /// The modifier was pressed after the drag had already started (common in Explorer: start dragging,
    /// then press Ctrl to copy). Treat it like a drag gesture that just passed the threshold. Caller holds the lock.
    /// </summary>
    private TriggerGesture? DetectLateModifier(int x, int y, ModifierKeys modifiers, TriggerMouseButton heldButton)
    {
        if (_dragActive || _lateRejected || Paused || heldButton == TriggerMouseButton.None || modifiers == ModifierKeys.None)
            return null;

        var late = _gestures.FirstOrDefault(g =>
            g.Kind == TriggerKind.Drag && g.Button == heldButton && g.Modifiers == modifiers);
        if (late is null)
            return null;
        if (ScopeFilter(late, x, y))
            return late;
        _lateRejected = true;
        return null;
    }

    public void MouseUp(TriggerMouseButton button, int x, int y)
    {
        bool ended;
        lock (_lock)
        {
            _lastX = x;
            _lastY = y;
            _armed = null;
            ended = _dragActive;
            _dragActive = false;
            _lateRejected = false;
        }
        if (ended)
            DragEnded?.Invoke(x, y);
    }

    /// <summary>Called when the hold timer for <paramref name="token"/> fires.</summary>
    public void HoldElapsed(long token)
    {
        TriggerGesture? fire = null;
        int x = 0, y = 0;
        lock (_lock)
        {
            if (_armed is { } armed && armed.Token == token && armed.Gesture.Kind == TriggerKind.Hold)
            {
                fire = armed.Gesture;
                (x, y) = (armed.X, armed.Y);
                _armed = null;
            }
        }
        if (fire is not null)
            HoldTriggered?.Invoke(fire, x, y);
    }

    /// <summary>A key went down. <paramref name="key"/> is the key name without the "Vc" prefix, e.g. "Space".</summary>
    public void KeyDown(string key, ModifierKeys modifiers)
    {
        TriggerGesture? fire;
        int x, y;
        lock (_lock)
        {
            if (Paused)
                return;
            fire = _gestures.FirstOrDefault(g =>
                g.Kind == TriggerKind.KeyChord &&
                string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase) &&
                g.Modifiers == modifiers);
            (x, y) = (_lastX, _lastY);
            if (fire is not null && !ScopeFilter(fire, x, y))
                fire = null;
        }
        if (fire is not null)
            KeyChordTriggered?.Invoke(fire, x, y);
    }
}
