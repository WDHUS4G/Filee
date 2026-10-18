using System.Diagnostics;
using System.Runtime.InteropServices;
using Filee.Platform.Linux;

namespace Filee.Platform.Tests;

public sealed class LinuxNativeIntegrationTests
{
    [Fact]
    public void NautilusScriptPreservesDistinctPathsAndShellCharacters()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Requires the Linux POSIX shell.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "filee-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var capture = Path.Combine(root, "arguments");
            var target = Path.Combine(root, "Filee's app");
            File.WriteAllText(target, "#!/bin/sh\nprintf '%s\\0' \"$@\" > " + LinuxDesktopIntegration.ShellArgument(capture) + "\n");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var first = Path.Combine(root, "a", "한글 ' $(touch BAD) %F.txt");
            var second = Path.Combine(root, "b", Path.GetFileName(first));
            foreach (var file in new[] { first, second })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "sample");
            }
            var data = Path.Combine(root, "data");
            new LinuxDesktopIntegration(Path.Combine(root, "config"), data).SetContextMenu(true, target, "Convert");
            var processInfo = new ProcessStartInfo(Path.Combine(data, "nautilus", "scripts", "Filee"))
            {
                UseShellExecute = false,
                WorkingDirectory = root,
            };
            processInfo.ArgumentList.Add(Path.GetRelativePath(root, first));
            processInfo.ArgumentList.Add(second);
            using var process = Process.Start(processInfo)!;
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(new[] { "--convert", first, second }, File.ReadAllText(capture).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            Assert.False(File.Exists(Path.Combine(root, "BAD")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void X11CanIdentifyAnOwnedWindowAndRequestRestackingWithoutChangingFocus()
    {
        if (!OperatingSystem.IsLinux() || !X11WindowSystem.IsAvailable)
        {
            Assert.Skip("Requires a Linux X11 desktop session.");
            return;
        }
        var display = XOpenDisplay(0);
        Assert.NotEqual(0, display);
        var window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 40, 40, 80, 80, 0, 0, 0);
        var pid = Marshal.AllocHGlobal(nint.Size);
        try
        {
            var attributes = new XSetWindowAttributes { OverrideRedirect = 1 };
            XChangeWindowAttributes(display, window, 1 << 9, ref attributes);
            Marshal.WriteIntPtr(pid, Environment.ProcessId);
            XChangeProperty(display, window, XInternAtom(display, "_NET_WM_PID", 0), 6, 32, 0, pid, 1);
            XMapRaised(display, window);
            XSync(display, 0);
            XTranslateCoordinates(display, window, XDefaultRootWindow(display), 20, 20, out var x, out var y, out _);
            Assert.Equal(Process.GetCurrentProcess().ProcessName, X11WindowSystem.ProcessNameAt(x, y));
            XGetInputFocus(display, out var previousFocus, out _);
            X11WindowSystem.RaiseTopmost((nint)window);
            XSync(display, 0);
            XGetInputFocus(display, out var currentFocus, out _);
            Assert.Equal(previousFocus, currentFocus);
        }
        finally
        {
            Marshal.FreeHGlobal(pid);
            XDestroyWindow(display, window);
            XCloseDisplay(display);
        }
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, nint data, int count);
    [DllImport("libX11.so.6")] private static extern int XMapRaised(nint display, nuint window);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(nint display, out nuint focus, out int revertTo);
    [DllImport("libX11.so.6")] private static extern int XTranslateCoordinates(nint display, nuint source, nuint destination, int x, int y, out int destinationX, out int destinationY, out nuint child);
    [DllImport("libX11.so.6")] private static extern int XChangeWindowAttributes(nint display, nuint window, nuint mask, ref XSetWindowAttributes attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public nuint BackgroundPixmap;
        public nuint BackgroundPixel;
        public nuint BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public nuint BackingPlanes;
        public nuint BackingPixel;
        public int SaveUnder;
        public nint EventMask;
        public nint DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap;
        public nuint Cursor;
    }
}
