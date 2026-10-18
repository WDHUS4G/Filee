// Connects the global input hook (SharpHook / libuiohook) to the GestureDetector and forwards detected
// gestures to the UI thread. Hook callbacks must return quickly: Windows silently removes a low-level hook
// whose callback takes longer than ~1 s, so no UI work happens here.

using System.Diagnostics;
using Avalonia.Threading;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;

namespace Filee.App.Services.Triggers;

/// <summary>Global gesture detection. Events are raised on the UI thread with physical screen coordinates.</summary>
public sealed class TriggerService : IDisposable
{
    private readonly IPlatformServices _platform;
    private readonly ILogger<TriggerService> _log;
    private readonly GestureDetector _detector = new();
    private readonly string _ownProcess = Process.GetCurrentProcess().ProcessName;
    private EventLoopGlobalHook? _hook;
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>FILEE_TRACE_GESTURES=1 logs each mouse press and scope decision, to find why a gesture doesn't fire.</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("FILEE_TRACE_GESTURES") == "1";

    public TriggerService(IPlatformServices platform, ILogger<TriggerService> log)
    {
        _platform = platform;
        _log = log;
        _detector.SystemDragThreshold = platform.SystemDragThreshold;
        _detector.ScopeFilter = InScope;

        _detector.DragStarted += (g, x, y) => Post(() => DragStarted?.Invoke(this, new GestureEventArgs(g, x, y)));
        _detector.DragEnded += (x, y) => Post(() => DragEnded?.Invoke(this, new GestureEventArgs(null, x, y)));
        _detector.HoldTriggered += (g, x, y) => Post(() => SelectionGesture?.Invoke(this, new GestureEventArgs(g, x, y)));
        _detector.KeyChordTriggered += (g, x, y) => Post(() => SelectionGesture?.Invoke(this, new GestureEventArgs(g, x, y)));
        _detector.HoldTimerRequested += (token, ms) =>
            _ = Task.Delay(ms).ContinueWith(_ => _detector.HoldElapsed(token), TaskScheduler.Default);
    }

    /// <summary>A modifier+drag started (files may be dragged). Show the donut and wait for DragEnter.</summary>
    public event EventHandler<GestureEventArgs>? DragStarted;

    /// <summary>The mouse button of an active drag gesture was released.</summary>
    public event EventHandler<GestureEventArgs>? DragEnded;

    /// <summary>A hold or keyboard gesture: open the donut for the file manager's current selection.</summary>
    public event EventHandler<GestureEventArgs>? SelectionGesture;

    /// <summary>Last known cursor position (physical pixels).</summary>
    public (int X, int Y) LastCursor { get; private set; }

    public bool IsRunning => _hook?.IsRunning == true;
    public string? Error { get; private set; }
    public event EventHandler? StatusChanged;

    private void NotifyStatus() => Post(() => StatusChanged?.Invoke(this, EventArgs.Empty));

    public void Apply(AppSettings settings)
    {
        _detector.Configure(settings.Triggers.Where(g => _platform.SupportsSelectionShortcut || g.Kind == TriggerKind.Drag).ToList());
        _detector.Paused = settings.Paused;
        _excluded = new HashSet<string>(settings.ExcludedProcesses.Select(p => p.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>macOS without Accessibility permission: gestures wait for it (and start by themselves once it is given).</summary>
    public bool NeedsPermission { get; private set; }

    private Timer? _permissionWatch;

    public void Start()
    {
        if (_hook is not null)
            return;
        if (!_platform.SupportsGlobalPointerGestures)
        {
            if (OperatingSystem.IsMacOS())
                WatchForPermission();
            return;
        }
        if (NeedsPermission)
        {
            NeedsPermission = false;
            NotifyStatus();
        }
        Error = null;
        try
        {
            // Background threads: a hook that fails to stop can never keep Filee running after it quits.
            _hook = new EventLoopGlobalHook(useBackgroundThreadForEventLoop: true);
            _hook.HookEnabled += (_, _) => { _log.LogInformation("Global input hook started"); NotifyStatus(); };
            _hook.HookDisabled += (_, _) => { _log.LogInformation("Global input hook stopped"); NotifyStatus(); };
            _hook.MousePressed += (_, e) =>
            {
                if (Trace)
                    _log.LogInformation("Trace: press {Button} at {X},{Y}, mask {Mask} = {Modifiers}", e.Data.Button, e.Data.X, e.Data.Y, e.RawEvent.Mask, Map(e.RawEvent.Mask));
                _detector.MouseDown(Map(e.Data.Button), e.Data.X, e.Data.Y, Map(e.RawEvent.Mask));
            };
            _hook.MouseReleased += (_, e) => _detector.MouseUp(Map(e.Data.Button), e.Data.X, e.Data.Y);
            _hook.MouseDragged += (_, e) => OnMove(e);
            _hook.MouseMoved += (_, e) => OnMove(e);
            _hook.KeyPressed += (_, e) => _detector.KeyDown(KeyName(e.Data.KeyCode), Map(e.RawEvent.Mask));
            _ = _hook.RunAsync(useBackgroundThread: true).ContinueWith(t =>
            {
                // e.g. macOS without Accessibility permission. Drop zone and context menu keep working.
                if (t.Exception is not null)
                {
                    Error = t.Exception.GetBaseException().Message;
                    _log.LogError(t.Exception, "Global input hook failed; gestures are unavailable");
                    NotifyStatus();
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // e.g. macOS without Accessibility permission. The app keeps working via the drop zone / context menu.
            _log.LogError(ex, "Could not start the global input hook");
            Error = ex.Message;
            _hook?.Dispose();
            _hook = null;
            NotifyStatus();
        }
    }

    private void OnMove(MouseHookEventArgs e)
    {
        LastCursor = (e.Data.X, e.Data.Y);
        _detector.MouseMove(e.Data.X, e.Data.Y, Map(e.RawEvent.Mask), HeldButton(e.RawEvent.Mask));
    }

    private bool InScope(TriggerGesture gesture, int x, int y)
    {
        var process = _platform.ProcessNameAt(x, y);
        if (Trace)
            _log.LogInformation("Trace: {Gesture} at {X},{Y} over {Process}", gesture.Kind, x, y, process ?? "(none)");
        if (process is not null && (string.Equals(process, _ownProcess, StringComparison.OrdinalIgnoreCase) || _excluded.Contains(process)))
            return false;
        return gesture.Scope == TriggerScope.Anywhere || _platform.IsFileManagerAt(x, y);
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    internal static TriggerMouseButton Map(MouseButton button) => button switch
    {
        MouseButton.Button1 => TriggerMouseButton.Left,
        MouseButton.Button2 => TriggerMouseButton.Right,
        MouseButton.Button3 => TriggerMouseButton.Middle,
        MouseButton.Button4 => TriggerMouseButton.X1,
        MouseButton.Button5 => TriggerMouseButton.X2,
        _ => TriggerMouseButton.None,
    };

    internal static ModifierKeys Map(EventMask mask)
    {
        var result = ModifierKeys.None;
        if (mask.HasCtrl()) result |= ModifierKeys.Ctrl;
        if (mask.HasAlt()) result |= ModifierKeys.Alt;
        if (mask.HasShift()) result |= ModifierKeys.Shift;
        if (mask.HasMeta()) result |= ModifierKeys.Meta;
        return result;
    }

    internal static TriggerMouseButton HeldButton(EventMask mask) =>
        mask.HasFlag(EventMask.Button1) ? TriggerMouseButton.Left
        : mask.HasFlag(EventMask.Button2) ? TriggerMouseButton.Right
        : mask.HasFlag(EventMask.Button3) ? TriggerMouseButton.Middle
        : mask.HasFlag(EventMask.Button4) ? TriggerMouseButton.X1
        : mask.HasFlag(EventMask.Button5) ? TriggerMouseButton.X2
        : TriggerMouseButton.None;

    /// <summary>"VcSpace" → "Space".</summary>
    internal static string KeyName(KeyCode code)
    {
        var name = code.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }

    /// <summary>
    /// Checks every two seconds whether the user switched Filee on under Accessibility, then starts the gestures;
    /// no restart needed.
    /// </summary>
    private void WatchForPermission()
    {
        if (!NeedsPermission)
        {
            NeedsPermission = true;
            _log.LogInformation("Gestures wait for the Accessibility permission");
            NotifyStatus();
        }
        _permissionWatch ??= new Timer(_ =>
        {
            if (!_platform.SupportsGlobalPointerGestures)
                return;
            _permissionWatch?.Dispose();
            _permissionWatch = null;
            _log.LogInformation("Accessibility permission granted");
            Post(Start);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        _permissionWatch?.Dispose();
        _permissionWatch = null;
        try
        {
            _hook?.Dispose();
        }
        catch (HookException ex)
        {
            // Quitting goes on: the hook runs on background threads, which end with the process.
            _log.LogWarning(ex, "Could not stop the global input hook");
        }
        _hook = null;
    }
}

/// <summary>Gesture notification with physical screen coordinates.</summary>
public sealed class GestureEventArgs(TriggerGesture? gesture, int x, int y) : EventArgs
{
    public TriggerGesture? Gesture { get; } = gesture;
    public int X { get; } = x;
    public int Y { get; } = y;
}
