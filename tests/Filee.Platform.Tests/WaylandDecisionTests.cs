// The Wayland decisions that only depend on environment values (#367): which compositors can place the donut, and
// when the native backend is used.

using Filee.Platform.Linux;

namespace Filee.Platform.Tests;

public sealed class WaylandDecisionTests
{
    [Theory]
    [InlineData("KDE", null, true)]
    [InlineData("kde", null, true)]
    [InlineData("KDE:GNOME", null, true)]
    [InlineData("Hyprland", null, true)]
    [InlineData("hyprland", null, true)]
    [InlineData(null, "instance_12345", true)]
    [InlineData("", "instance_12345", true)]
    [InlineData("GNOME", null, false)]
    [InlineData("sway", null, false)]
    [InlineData("XFCE", null, false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void KDE_and_Hyprland_can_place_the_donut(
        string? xdgCurrentDesktop, string? hyprlandSignature, bool expected)
    {
        var result = WaylandSupport.IsSupportedCompositor(xdgCurrentDesktop, hyprlandSignature);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, "1", "wayland-0", true)]
    [InlineData(true, "1", "wayland-1", true)]
    [InlineData(false, "1", "wayland-0", false)]        // Not Linux
    [InlineData(true, null, "wayland-0", false)]       // Switch unset
    [InlineData(true, "", "wayland-0", false)]         // Switch empty
    [InlineData(true, "0", "wayland-0", false)]        // Switch 0
    [InlineData(true, "true", "wayland-0", false)]     // Switch not "1"
    [InlineData(true, "1", null, false)]               // No WAYLAND_DISPLAY
    [InlineData(true, "1", "", false)]                 // Empty WAYLAND_DISPLAY
    public void The_native_backend_needs_Linux_the_switch_and_a_display(
        bool isLinux, string? fileeUseWayland, string? waylandDisplay, bool expected)
    {
        var result = WaylandSupport.IsNativeWaylandRequested(isLinux, fileeUseWayland, waylandDisplay);
        Assert.Equal(expected, result);
    }
}
