// Pure state machine that turns low-level mouse/keyboard events into "open the donut" gestures.
// It knows nothing about SharpHook or Avalonia so it can be unit tested (see Filee.App.Tests).
//
// Drag gesture:  button down with the exact modifiers  →  (armed)  →  moved past threshold  →  DragStarted
//                →  button up  →  DragEnded
// Hold gesture:  button down with modifiers  →  (armed)  →  HoldElapsed without moving  →  HoldTriggered
// Drag and Hold on the same button and modifiers arm together: moving past the threshold drags, holding still holds.
// Key chord:     key down with the exact modifiers  →  KeyChordTriggered at the last known cursor position
//                (the scope filter judges a key chord by the window in front, not by the cursor)

using Filee.Core.Settings;

namespace Filee.App.Services.Triggers;

/// <summary>Detects configured <see cref="TriggerGesture"/>s in a stream of input events. Thread-safe.</summary>
public sealed class GestureDetector
{
    private readonly object _lock = new();
    private IReadOnlyList<TriggerGesture> _gestures = [];
    private Armed? _armed;
    private bool _dragActive;
    private bool _lateRejected; // no late-modifier drag for this press: its scope failed, or a hold already fired
    private int _lastX, _lastY;

    /// <summary>A press that may still become a drag, a hold, or both until one of them happens.</summary>
    private sealed record Armed(TriggerGesture? Drag, TriggerGesture? Hold, int X, int Y, long Token);

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

            // A drag and a hold may share the button and modifiers (#450): both are armed, and what the pointer does
            // next decides between them.
            var drag = FirstInScope(TriggerKind.Drag, button, modifiers, x, y);
            holdGesture = FirstInScope(TriggerKind.Hold, button, modifiers, x, y);
            if (drag is null && holdGesture is null)
                return;

            token = Environment.TickCount64 ^ (x * 7919L) ^ y;
            _armed = new Armed(drag, holdGesture, x, y, token);
        }

        if (holdGesture is not null)
            HoldTimerRequested?.Invoke(token, holdGesture.HoldMilliseconds);
    }

    /// <summary>The first gesture of a kind on this button and modifiers that may start here. Caller holds the lock.</summary>
    private TriggerGesture? FirstInScope(TriggerKind kind, TriggerMouseButton button, ModifierKeys modifiers, int x, int y) =>
        _gestures.FirstOrDefault(g => g.Kind == kind && g.Button == button && g.Modifiers == modifiers && ScopeFilter(g, x, y));

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
    /// An armed press moved: past its threshold a hold is cancelled, and a drag fires. Caller holds the lock.
    /// </summary>
    private TriggerGesture? DetectDragPastThreshold(int x, int y)
    {
        var armed = _armed!;
        if (armed.Hold is { } hold && Moved(hold, armed, x, y))
            armed = armed with { Hold = null };
        if (armed.Drag is { } drag && Moved(drag, armed, x, y))
        {
            _armed = null;
            return drag;
        }
        _armed = armed.Drag is null && armed.Hold is null ? null : armed;
        return null;
    }

    private bool Moved(TriggerGesture gesture, Armed armed, int x, int y)
    {
        var threshold = gesture.DragThreshold > 0 ? gesture.DragThreshold : SystemDragThreshold;
        return Math.Abs(x - armed.X) > threshold || Math.Abs(y - armed.Y) > threshold;
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
            if (_armed is { Hold: { } hold } armed && armed.Token == token)
            {
                fire = hold;
                (x, y) = (armed.X, armed.Y);
                _armed = null;
                _lateRejected = true; // moving on after the hold doesn't start the drag that shares its modifiers
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
            (x, y) = (_lastX, _lastY);
            // Two shortcuts on one key (one per scope, say): the first whose scope fits fires.
            fire = _gestures.FirstOrDefault(g =>
                g.Kind == TriggerKind.KeyChord &&
                string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase) &&
                g.Modifiers == modifiers &&
                ScopeFilter(g, x, y));
        }
        if (fire is not null)
            KeyChordTriggered?.Invoke(fire, x, y);
    }
}
