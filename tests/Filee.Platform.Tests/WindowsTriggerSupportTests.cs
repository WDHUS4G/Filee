// #450: the selection shortcut reads the desktop's selection when the desktop is in front, and "start at sign-in"
// says so when the entry it wrote didn't stay.

using System.Runtime.Versioning;
using Filee.Core.Platform;
using Filee.Platform.Windows;

namespace Filee.Platform.Tests;

// Plain checks: they run on every OS, the class is only marked for the Windows-only type.
[SupportedOSPlatform("windows")]
public sealed class WindowsTriggerSupportTests
{
    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("CabinetWClass", false)] // a folder window: Shell.Application lists it
    [InlineData("Shell_TrayWnd", false)] // the taskbar
    [InlineData(null, false)]
    public void The_desktop_is_told_from_folder_windows(string? windowClass, bool desktop) =>
        Assert.Equal(desktop, WindowsPlatformServices.IsDesktop(windowClass));

    [Fact]
    public void The_startup_entry_read_back_must_be_the_one_written()
    {
        const string written = "\"C:\\Program Files\\Filee\\Filee.exe\" --background";
        WindowsPlatformServices.CheckStartupEntry(written, written);

        Assert.Throws<StartupEntryNotKeptException>(() => WindowsPlatformServices.CheckStartupEntry(written, null));
        Assert.Throws<StartupEntryNotKeptException>(() =>
            WindowsPlatformServices.CheckStartupEntry(written, "\"C:\\Program Files\\Filee\\Filee.exe\""));
        Assert.Throws<StartupEntryNotKeptException>(() => WindowsPlatformServices.CheckStartupEntry(written, new byte[] { 1 }));
    }
}
