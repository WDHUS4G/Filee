// What the desktop compositor (DWM) shows behind a transparent Filee window. Filee never asks it for a material (Mica,
// Acrylic), so the transparent pixels around the donut show the windows behind it. Programs that restyle every
// window can still put one there: the donut then stands on a blurred, tinted rectangle (issue #269). Read says what the
// window got; ClearForeign takes such a material away again. The older way to the same look, the undocumented accent
// policy, can't be read back: ResetAccent turns it off without looking, which changes nothing on a window without one.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Filee.Platform.Windows;

/// <param name="Redirected">The window draws into a redirection surface (no DirectComposition / WinUI tree).</param>
/// <param name="Layered">WS_EX_LAYERED: Avalonia never sets it, so something else did.</param>
/// <param name="SystemBackdrop">DWMWA_SYSTEMBACKDROP_TYPE (Windows 11 22H2+), -1 when it can't be read.</param>
public readonly record struct WindowBackdropState(bool Redirected, bool Layered, int SystemBackdrop)
{
    // DWM_SYSTEMBACKDROP_TYPE: 0 auto, 1 none, 2 Mica, 3 Acrylic, 4 Mica Alt.
    public const int BackdropNone = 1;

    /// <summary>A backdrop material Filee didn't ask for.</summary>
    public bool HasForeignMaterial => SystemBackdrop > BackdropNone;

    public override string ToString() =>
        $"{(Redirected ? "redirection surface" : "composition surface")}{(Layered ? ", layered" : "")}, " +
        $"system backdrop {BackdropName(SystemBackdrop)}";

    public static string BackdropName(int type) => type switch
    {
        -1 => "unknown",
        0 => "auto",
        1 => "none",
        2 => "Mica",
        3 => "Acrylic",
        4 => "Mica Alt",
        _ => type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

public static partial class WindowBackdrop
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int WCA_ACCENT_POLICY = 19;

    [SupportedOSPlatform("windows")]
    public static WindowBackdropState Read(nint hwnd)
    {
        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        return new WindowBackdropState(
            Redirected: (exStyle & WS_EX_NOREDIRECTIONBITMAP) == 0,
            Layered: (exStyle & WS_EX_LAYERED) != 0,
            SystemBackdrop: ReadSystemBackdrop(hwnd));
    }

    /// <summary>
    /// Sets the backdrop back to none. True when Windows took the change. Only for windows Filee never gives a material.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe bool ClearForeign(nint hwnd)
    {
        var none = WindowBackdropState.BackdropNone;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &none, sizeof(int)) == 0;
    }

    /// <summary>Turns off an accent blur or acrylic (ACCENT_DISABLED). True when Windows took it.</summary>
    [SupportedOSPlatform("windows")]
    public static unsafe bool ResetAccent(nint hwnd)
    {
        var policy = new AccentPolicy();
        var data = new CompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = &policy, Size = sizeof(AccentPolicy) };
        try
        {
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Settings → Personalization → Colors → Transparency effects; null when it can't be read.</summary>
    [SupportedOSPlatform("windows")]
    public static bool? TransparencyEffects()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static unsafe int ReadSystemBackdrop(nint hwnd)
    {
        int type;
        return DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(int)) == 0 ? type : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CompositionAttributeData
    {
        public int Attribute;
        public void* Data;
        public nint Size;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static unsafe partial int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    [LibraryImport("dwmapi.dll")]
    private static unsafe partial int DwmSetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowCompositionAttribute(nint hwnd, ref CompositionAttributeData data);
}
