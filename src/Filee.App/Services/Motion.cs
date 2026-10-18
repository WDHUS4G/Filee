// Global "reduce animations" switch. Styles react through the .reduced-motion class on windows;
// custom-drawn controls (DonutMenu) read Motion.Enabled directly.

using Avalonia.Controls;

namespace Filee.App.Services;

/// <summary>Whether animations are enabled.</summary>
public static class Motion
{
    private static readonly List<WeakReference<Window>> Windows = [];

    /// <summary>False when the user (or the OS) asked for reduced motion.</summary>
    public static bool Enabled { get; private set; } = true;

    public static event EventHandler? Changed;

    private static bool? _reduceMotion;
    private static Func<bool>? _systemPrefersReduced;

    /// <summary>
    /// The user's choice (null follows the system) and how to read the system's. Following the system, the value is
    /// read again by <see cref="Refresh"/>: a moment when Windows reported animations off (a remote session, waking
    /// from sleep) must not switch them off for good.
    /// </summary>
    public static void Configure(bool? reduceMotion, Func<bool> systemPrefersReduced)
    {
        _reduceMotion = reduceMotion;
        _systemPrefersReduced = systemPrefersReduced;
        Set(!Current());
    }

    /// <summary>Reads the system's setting again when following it (call before showing a window).</summary>
    public static void Refresh()
    {
        if (_reduceMotion is null && _systemPrefersReduced is not null && Enabled == Current())
            Set(!Current());
    }

    private static bool Current()
    {
        try
        {
            return _reduceMotion ?? _systemPrefersReduced?.Invoke() ?? false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        Enabled = enabled;
        lock (Windows)
        {
            Windows.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Windows)
                if (weak.TryGetTarget(out var window))
                    Apply(window);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Call once per window so its styles follow the setting.</summary>
    public static void Track(Window window)
    {
        lock (Windows)
            Windows.Add(new WeakReference<Window>(window));
        Apply(window);
    }

    private static void Apply(Window window) => window.Classes.Set("reduced-motion", !Enabled);
}
