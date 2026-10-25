// Everything in Filee's windows larger or smaller, as a browser zooms a page: Ctrl + mouse wheel, Ctrl + plus / minus,
// Ctrl + 0 back to normal (Cmd on a Mac), and Theme → Window size. Each tracked window's content is scaled by a layout
// transform (so layouts that adapt to their width see the room they really have), every window at once, and the size
// is remembered (written a moment after the last change, without re-applying the other settings).
// Windows that can't be resized grow with the zoom instead, as far as their screen allows, and scroll when it doesn't.
// The donut, the toast, the corner notices and the crop overlay aren't tracked; popups (drop-down lists, menus,
// tooltips) open in their own windows and stay at 100 %. See docs/ARCHITECTURE.md.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Filee.App.Services;

public static class UiZoom
{
    /// <summary>The smallest and largest size, and one step.</summary>
    public const double Min = 0.7, Max = 1.8, Step = 0.1;

    /// <summary>How long the size has to stay put before it is written to settings.json.</summary>
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>A pause in the wheel (ms) after which a part of a notch left over no longer counts.</summary>
    private const ulong WheelPause = 1000;

    /// <summary>Room left between a window that can't be resized and the edges of its screen.</summary>
    private const double ScreenMargin = 12;

    private const string Marker = "ui-zoom";

    private static readonly List<WeakReference<Window>> Windows = [];
    private static readonly ConditionalWeakTable<Window, Designed> Sizes = [];
    private static Action<double>? _save;
    private static DispatcherTimer? _saveTimer;
    private static double _wheel;
    private static ulong _wheelAt;

    /// <summary>How large everything is drawn: 1 as designed.</summary>
    public static double Scale { get; private set; } = 1;

    /// <summary>Raised after <see cref="Scale"/> changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>A new size is waiting to be written (<see cref="Flush"/> writes it now).</summary>
    internal static bool SavePending => _saveTimer?.IsEnabled == true;

    /// <summary>The size remembered and how to remember a new one (called once the size has stayed put a moment).</summary>
    public static void Configure(double scale, Action<double>? save)
    {
        _saveTimer?.Stop();
        _save = save;
        Set(scale, remember: false);
    }

    /// <summary>
    /// Scales <paramref name="window"/>'s content (call it once the content is in place) and gives it the zoom keys and
    /// wheel. A window that can't be resized also grows with the zoom and scrolls when its screen is too small, unless
    /// <paramref name="fitsItself"/>: then it keeps itself on screen and scrolls its own content.
    /// </summary>
    public static void Track(Window window, bool fitsItself = false)
    {
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            Windows.Add(new WeakReference<Window>(window));
        }
        Sizes.AddOrUpdate(window, new Designed(window.Width, window.MaxHeight, fitsItself));
        // The wheel before anything inside (a list would scroll); the keys after, so a control that uses the
        // combination itself (recording a shortcut on the Triggers page) gets it first.
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Bubble);
        window.Opened += (_, _) => Apply(window);
        window.SizeChanged += (_, _) => KeepOnScreen(window);
        Apply(window);
    }

    /// <summary>Sets the size (kept between <see cref="Min"/> and <see cref="Max"/>) in every window.</summary>
    public static void Set(double scale, bool remember = true)
    {
        var value = Math.Round(Math.Clamp(double.IsFinite(scale) ? scale : 1, Min, Max), 2);
        var changed = Math.Abs(value - Scale) > 1e-9;
        Scale = value;
        List<Window> windows;
        lock (Windows)
            windows = Windows.Select(w => w.TryGetTarget(out var window) ? window : null).OfType<Window>().ToList();
        foreach (var window in windows)
            Apply(window);
        if (!changed)
            return;
        if (remember)
            ScheduleSave();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ZoomIn() => Set(Scale + Step);

    public static void ZoomOut() => Set(Scale - Step);

    public static void Reset() => Set(1);

    /// <summary>Writes a size still waiting for its moment (when Filee exits).</summary>
    public static void Flush()
    {
        if (_saveTimer is not { IsEnabled: true } timer)
            return;
        timer.Stop();
        _save?.Invoke(Scale);
    }

    // Turning the wheel a few notches writes settings.json once.
    private static void ScheduleSave()
    {
        if (_save is null)
            return;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = SaveDelay };
            _saveTimer.Tick += (_, _) => Flush();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void Apply(Window window)
    {
        if (window.Content is not Control content || !Sizes.TryGetValue(window, out var designed))
            return;
        var growsWithZoom = !window.CanResize && double.IsFinite(designed.Width);
        var scaled = content switch
        {
            LayoutTransformControl { Tag: Marker } existing => existing,
            ScrollViewer { Tag: Marker, Content: LayoutTransformControl inner } => inner,
            _ => null,
        };
        if (scaled is null)
        {
            window.Content = null;
            scaled = new LayoutTransformControl { Tag = Marker, Child = content };
            window.Content = growsWithZoom && !designed.FitsItself
                ? new ScrollViewer
                {
                    Tag = Marker,
                    Content = scaled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                }
                : scaled;
        }
        scaled.LayoutTransform = Math.Abs(Scale - 1) < 1e-9 ? null : new ScaleTransform(Scale, Scale);
        if (!growsWithZoom)
            return;

        // As wide as designed times the zoom and, unless it fits itself, no taller than its screen: what doesn't fit
        // scrolls. Without a screen (headless), only the width.
        var area = WorkingArea(window);
        var width = designed.Width * Scale;
        if (area is { } room)
            width = Math.Min(width, Math.Max(designed.Width, room.Width - 2 * ScreenMargin));
        window.Width = width;
        if (!designed.FitsItself && area is { } screen)
        {
            var frame = window.FrameSize is { } size ? Math.Max(0, size.Height - window.ClientSize.Height) : 0;
            window.MaxHeight = Math.Min(designed.MaxHeight, Math.Max(window.MinHeight, screen.Height - frame - 2 * ScreenMargin));
        }
    }

    /// <summary>The working area of the window's screen in device-independent pixels, or null when unknown.</summary>
    private static Rect? WorkingArea(Window window)
    {
        var screens = window.Screens;
        var screen = (window.IsVisible ? screens.ScreenFromWindow(window) : null) ?? screens.Primary;
        if (screen is null || screen.Scaling <= 0)
            return null;
        var area = screen.WorkingArea;
        return new Rect(area.X / screen.Scaling, area.Y / screen.Scaling, area.Width / screen.Scaling, area.Height / screen.Scaling);
    }

    /// <summary>A window that can't be resized and grew past the edge of its screen moves back onto it.</summary>
    private static void KeepOnScreen(Window window)
    {
        if (window.CanResize || !window.IsVisible || window.WindowState != WindowState.Normal)
            return;
        var screen = window.Screens.ScreenFromWindow(window);
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var size = window.FrameSize ?? window.ClientSize;
        var width = (int)Math.Ceiling(size.Width * screen.Scaling);
        var height = (int)Math.Ceiling(size.Height * screen.Scaling);
        var margin = (int)Math.Round(ScreenMargin * screen.Scaling);
        var x = Math.Max(area.X, Math.Min(window.Position.X, area.Right - margin - width));
        var y = Math.Max(area.Y, Math.Min(window.Position.Y, area.Bottom - margin - height));
        if (x != window.Position.X || y != window.Position.Y)
            window.Position = new PixelPoint(x, y);
    }

    /// <summary>Ctrl on Windows and Linux, Cmd on a Mac.</summary>
    private static KeyModifiers Command(object? sender) =>
        (sender as Visual)?.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != Command(sender) || e.Delta.Y == 0)
            return;
        e.Handled = true;
        // Touchpads (and pinching on one, which Windows sends as Ctrl + wheel) and smooth wheels send many small
        // deltas: they add up to one step per whole notch. Turning the other way, or a pause, starts the count again.
        if (Math.Sign(e.Delta.Y) != Math.Sign(_wheel) || e.Timestamp - _wheelAt > WheelPause)
            _wheel = 0;
        _wheelAt = e.Timestamp;
        _wheel += e.Delta.Y;
        var notches = (int)(_wheel + Math.Sign(_wheel) * 1e-6); // three thirds make a notch
        if (notches == 0)
            return;
        _wheel -= notches;
        Set(Scale + notches * Step);
    }

    // Ctrl (Cmd) with plus, minus or 0, Shift allowed: Ctrl + Shift + = is "Ctrl +" on a US keyboard and the only way
    // to type + on some layouts. Alt is not: AltGr is Ctrl + Alt on Windows, and AltGr + 0 types "}" on German keyboards.
    private static void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (e.KeyModifiers & ~KeyModifiers.Shift) != Command(sender))
            return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomIn();
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomOut();
                break;
            case Key.D0 or Key.NumPad0:
                Reset();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>A window's width and maximum height at 100 %.</summary>
    private sealed record Designed(double Width, double MaxHeight, bool FitsItself);
}
