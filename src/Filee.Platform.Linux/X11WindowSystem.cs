using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Filee.Platform.Linux;

internal static class X11WindowSystem
{
    private const string Library = "libX11.so.6";

    internal static bool IsAvailable => OperatingSystem.IsLinux()
        && !LinuxPlatformServices.IsWaylandSession
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    internal static string? ProcessNameAt(int x, int y)
    {
        if (!IsAvailable)
            return null;
        var display = OpenDisplay();
        if (display == 0)
            return null;
        try
        {
            // Keep windows alive between hit testing and reading their properties.
            XGrabServer(display);
            var root = XDefaultRootWindow(display);
            var window = root;
            string? name = null;
            for (var depth = 0; depth < 32; depth++)
            {
                name = ReadProcessName(display, window) ?? name;
                if (XTranslateCoordinates(display, root, window, x, y, out _, out _, out var child) == 0 || child == 0)
                    break;
                window = child;
            }
            return name;
        }
        finally
        {
            XUngrabServer(display);
            XCloseDisplay(display);
        }
    }

    /// <summary>Process names of the windows the window manager lists (_NET_CLIENT_LIST).</summary>
    internal static IReadOnlyList<string> ClientProcessNames()
    {
        if (!IsAvailable)
            return [];
        var display = OpenDisplay();
        if (display == 0)
            return [];
        try
        {
            var list = XInternAtom(display, "_NET_CLIENT_LIST", 1);
            if (list == 0 || XGetWindowProperty(display, XDefaultRootWindow(display), list, 0, 4096, 0, 0,
                    out _, out var format, out var count, out _, out var data) != 0)
                return [];
            try
            {
                if (data == 0 || format != 32 || count == 0)
                    return [];
                var names = new List<string>();
                for (var i = 0; i < (int)count; i++)
                {
                    // Format-32 properties come back as C longs, one per window.
                    var window = (nuint)(ulong)Marshal.ReadIntPtr(data, i * IntPtr.Size);
                    if (ReadProcessName(display, window) is { } name)
                        names.Add(name);
                }
                return names;
            }
            finally
            {
                XFree(data);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    internal static void RaiseTopmost(nint window)
    {
        if (window == 0 || !IsAvailable)
            return;
        var display = OpenDisplay();
        if (display == 0)
            return;
        try
        {
            var above = XInternAtom(display, "_NET_WM_STATE_ABOVE", 0);
            SendClientMessage(display, window, "_NET_WM_STATE", 1, (nint)above, 0, 1);
            SendClientMessage(display, window, "_NET_RESTACK_WINDOW", 1, 0, 0, 0);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static nint OpenDisplay()
    {
        try { return XOpenDisplay(0); }
        catch (DllNotFoundException exception)
        {
            Trace.TraceWarning($"X11 integration is unavailable: {exception.Message}");
            return 0;
        }
        catch (EntryPointNotFoundException exception)
        {
            Trace.TraceWarning($"X11 integration is unavailable: {exception.Message}");
            return 0;
        }
    }

    private static string? ReadProcessName(nint display, nuint window)
    {
        var property = XInternAtom(display, "_NET_WM_PID", 1);
        if (property != 0 && XGetWindowProperty(display, window, property, 0, 1, 0, 0,
                out _, out var format, out var count, out _, out var data) == 0)
        {
            try
            {
                if (format == 32 && count > 0 && data != 0)
                {
                    var pid = unchecked((int)Marshal.ReadIntPtr(data));
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        return process.ProcessName;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
            finally
            {
                if (data != 0)
                    XFree(data);
            }
        }
        var windowClass = XInternAtom(display, "WM_CLASS", 1);
        if (windowClass == 0 || XGetWindowProperty(display, window, windowClass, 0, 256, 0, 0,
                out _, out var classFormat, out var classCount, out _, out var classData) != 0)
            return null;
        try
        {
            if (classData == 0 || classFormat != 8 || classCount == 0)
                return null;
            var bytes = new byte[Math.Min((int)classCount, 1024)];
            Marshal.Copy(classData, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        finally
        {
            if (classData != 0)
                XFree(classData);
        }
    }

    private static void SendClientMessage(nint display, nint window, string message, nint a, nint b, nint c, nint d)
    {
        var clientEvent = new XClientMessageEvent
        {
            Type = 33,
            SendEvent = 1,
            Display = display,
            Window = (nuint)window,
            MessageType = XInternAtom(display, message, 0),
            Format = 32,
            Data0 = a,
            Data1 = b,
            Data2 = c,
            Data3 = d,
        };
        XSendEvent(display, XDefaultRootWindow(display), 0, (1L << 20) | (1L << 19), ref clientEvent);
    }

    // XEvent is a union of 24 native longs on supported 64-bit Linux targets.
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public nint Data0;
        public nint Data1;
        public nint Data2;
        public nint Data3;
        public nint Data4;
    }

    [DllImport(Library)] private static extern nint XOpenDisplay(nint displayName);
    [DllImport(Library)] private static extern int XCloseDisplay(nint display);
    [DllImport(Library)] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport(Library)] private static extern int XGrabServer(nint display);
    [DllImport(Library)] private static extern int XUngrabServer(nint display);
    [DllImport(Library)]
    private static extern int XTranslateCoordinates(nint display, nuint source, nuint destination,
        int x, int y, out int destinationX, out int destinationY, out nuint child);
    [DllImport(Library)] private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport(Library)]
    private static extern int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, int delete, nuint requestedType, out nuint actualType, out int actualFormat,
        out nuint itemCount, out nuint bytesAfter, out nint propertyData);
    [DllImport(Library)] private static extern int XFree(nint data);
    [DllImport(Library)] private static extern int XSendEvent(nint display, nuint window, int propagate, long mask, ref XClientMessageEvent clientEvent);
    [DllImport(Library)] private static extern int XFlush(nint display);
}
