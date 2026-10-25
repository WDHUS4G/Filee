// Windows: looks at every transparent window (the donut, the toast, the notices) as it shows. The first time each kind
// shows, the log gets the transparency Avalonia reports next to the one asked for, and what the desktop compositor
// puts behind it. A system backdrop on such a window turns its transparent area into a blurred, tinted rectangle, the
// look of issue #269, and so does an accent blur. Filee never sets either, so one found there came from another program
// (tools that restyle every window do this): a backdrop is logged and set back to none; the accent, which can't be read
// back, is turned off each time. Such tools react to a window showing from their own process, so the window is looked
// at again a moment later.

using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

internal static class TransparentWindows
{
    private static readonly TimeSpan LookAgainAfter = TimeSpan.FromMilliseconds(600);
    private static readonly HashSet<string> Described = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    [SupportedOSPlatform("windows")]
    public static void Watch(ILogger log) =>
        Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (!e.GetNewValue<bool>() || !window.TransparencyLevelHint.Contains(WindowTransparencyLevel.Transparent))
                return;
            // After the native window has been shown and styled.
            Dispatcher.UIThread.Post(() => Check(window, log), DispatcherPriority.Background);
            DispatcherTimer.RunOnce(() => Check(window, log), LookAgainAfter);
        });

    [SupportedOSPlatform("windows")]
    private static void Check(Window window, ILogger log)
    {
        if (!window.IsVisible || window.TryGetPlatformHandle() is not { Handle: var hwnd and not 0 })
            return;
        var kind = window.GetType().Name;
        WindowBackdrop.ResetAccent(hwnd);
        var state = WindowBackdrop.Read(hwnd);
        if (Described.Add(kind))
            log.LogInformation("{Window}: transparency {Actual} (asked {Asked}), {State}, transparency effects {Effects}",
                kind, window.ActualTransparencyLevel, string.Join(", ", window.TransparencyLevelHint), state,
                WindowBackdrop.TransparencyEffects() switch { true => "on", false => "off", null => "unknown" });
        if (!state.HasForeignMaterial)
            return;
        var cleared = WindowBackdrop.ClearForeign(hwnd);
        if (Warned.Add(kind))
            log.LogWarning("{Window} got a backdrop Filee didn't ask for ({State}), probably from a program that restyles " +
                           "windows; {Result}", kind, state, cleared ? "set it back to none" : "could not set it back to none");
    }
}
