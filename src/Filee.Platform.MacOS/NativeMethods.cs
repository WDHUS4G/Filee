using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal static class NativeMethods
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    private static readonly nint BoundsKey = CFStringCreateWithCString(0, "kCGWindowBounds", 0x08000100);
    private static readonly nint PidKey = CFStringCreateWithCString(0, "kCGWindowOwnerPID", 0x08000100);
    private static readonly nint AlphaKey = CFStringCreateWithCString(0, "kCGWindowAlpha", 0x08000100);
    private static readonly nint LayerKey = CFStringCreateWithCString(0, "kCGWindowLayer", 0x08000100);

    /// <summary>Process names of the app windows on screen (layer 0), front to back.</summary>
    internal static IEnumerable<string> WindowOwnerNames()
    {
        var windows = CGWindowListCopyWindowInfo(1, 0);
        if (windows == 0)
            yield break;
        try
        {
            for (nint index = 0; index < CFArrayGetCount(windows); index++)
            {
                var window = CFArrayGetValueAtIndex(windows, index);
                var layerValue = CFDictionaryGetValue(window, LayerKey);
                if (layerValue == 0 || !CFNumberGetInt(layerValue, 3, out var layer) || layer != 0)
                    continue;
                var pidValue = CFDictionaryGetValue(window, PidKey);
                if (pidValue == 0 || !CFNumberGetInt(pidValue, 3, out var pid))
                    continue;
                string? name = null;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    name = process.ProcessName;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                if (name is not null)
                    yield return name;
            }
        }
        finally
        {
            CFRelease(windows);
        }
    }

    internal static string? ProcessNameAt(int x, int y)
    {
        var windows = CGWindowListCopyWindowInfo(1, 0);
        if (windows == 0)
            return null;
        try
        {
            for (nint index = 0; index < CFArrayGetCount(windows); index++)
            {
                var window = CFArrayGetValueAtIndex(windows, index);
                var boundsValue = CFDictionaryGetValue(window, BoundsKey);
                if (boundsValue == 0 || !CGRectMakeWithDictionaryRepresentation(boundsValue, out var bounds))
                    continue;
                // Quartz and Avalonia's macOS desktop coordinates use top-left points, including on Retina displays.
                if (x < bounds.X || y < bounds.Y || x >= bounds.X + bounds.Width || y >= bounds.Y + bounds.Height)
                    continue;
                // Only app windows (layer 0) and the desktop (below 0): the Dock, the menu bar and other system
                // overlays sit above (the Dock keeps a transparent window over the whole screen).
                var layerValue = CFDictionaryGetValue(window, LayerKey);
                if (layerValue != 0 && CFNumberGetInt(layerValue, 3, out var layer) && layer > 0)
                    continue;
                var alphaValue = CFDictionaryGetValue(window, AlphaKey);
                if (alphaValue != 0 && CFNumberGetDouble(alphaValue, 6, out var alpha) && alpha <= 0)
                    continue;
                var pidValue = CFDictionaryGetValue(window, PidKey);
                if (pidValue == 0 || !CFNumberGetInt(pidValue, 3, out var pid))
                    continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var name = process.ProcessName;
                    if (!string.Equals(name, "WindowServer", StringComparison.OrdinalIgnoreCase))
                        return name;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return null;
        }
        finally
        {
            CFRelease(windows);
        }
    }

    internal static bool IsFinderFrontmost()
    {
        using var pool = new AutoreleasePool();
        var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        var application = Send(workspace, sel_registerName("frontmostApplication"));
        var bundle = Send(application, sel_registerName("bundleIdentifier"));
        return Marshal.PtrToStringUTF8(Send(bundle, sel_registerName("UTF8String"))) == "com.apple.finder";
    }

    internal static bool PrefersReducedMotion()
    {
        using var pool = new AutoreleasePool();
        var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        return SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion"));
    }

    internal static void RaiseTopmost(nint window)
    {
        const nint canJoinAllSpaces = 1, moveToActiveSpace = 1 << 1;
        const nint fullScreenPrimary = 1 << 7, fullScreenAuxiliary = 1 << 8, fullScreenNone = 1 << 9;
        var behavior = Send(window, sel_registerName("collectionBehavior"));
        behavior = (behavior & ~(moveToActiveSpace | fullScreenPrimary | fullScreenNone)) | canJoinAllSpaces | fullScreenAuxiliary;
        if (OperatingSystem.IsMacOSVersionAtLeast(13))
        {
            const nint primary = 1 << 16, auxiliary = 1 << 17, canJoinAllApplications = 1 << 18;
            behavior = (behavior & ~(primary | auxiliary)) | canJoinAllApplications;
        }
        // Finder can occupy a full-screen Space that would otherwise hide another app's toolbar.
        SendInteger(window, sel_registerName("setCollectionBehavior:"), behavior);
        SendInteger(window, sel_registerName("setLevel:"), 3);
        SendVoid(window, sel_registerName("orderFrontRegardless"));
    }

    /// <summary>
    /// Adds <paramref name="types"/> to the drag types of every view in <paramref name="window"/> that already takes
    /// drags (Avalonia's view, registered for public.data only). AppKit sends a drag to a view only when it offers one
    /// of the view's types, and some apps' file drags may not conform to public.data.
    /// </summary>
    internal static void AddDraggedTypes(nint window, IReadOnlyList<string> types)
    {
        using var pool = new AutoreleasePool();
        AddDraggedTypesTo(Send(window, sel_registerName("contentView")), types);
    }

    private static void AddDraggedTypesTo(nint view, IReadOnlyList<string> types)
    {
        if (view == 0)
            return;
        var registered = Send(view, sel_registerName("registeredDraggedTypes"));
        if (registered != 0 && Send(registered, sel_registerName("count")) > 0)
        {
            // The whole list: AppKit doesn't document whether registering adds to the view's types or replaces them.
            var all = Send(objc_getClass("NSMutableArray"), sel_registerName("arrayWithArray:"), registered);
            foreach (var type in types)
            {
                var name = CFStringCreateWithCString(0, type, 0x08000100);
                if (!SendBool(all, sel_registerName("containsObject:"), name))
                    SendVoid(all, sel_registerName("addObject:"), name);
                CFRelease(name);
            }
            SendVoid(view, sel_registerName("registerForDraggedTypes:"), all);
        }
        var subviews = Send(view, sel_registerName("subviews"));
        var count = subviews == 0 ? 0 : Send(subviews, sel_registerName("count"));
        for (nint index = 0; index < count; index++)
            AddDraggedTypesTo(Send(subviews, sel_registerName("objectAtIndex:"), index), types);
    }

    /// <summary>
    /// Sets whether Filee appears in the Dock by changing the application activation policy. Changing the policy
    /// deactivates the app, so activate it again when the change succeeds.
    /// </summary>
    internal static bool SetShowInDock(bool show)
    {
        using var pool = new AutoreleasePool();
        var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var succeeded = SendBoolWithInteger(application, sel_registerName("setActivationPolicy:"), show ? 0 : 1);
        if (succeeded)
            SendInteger(application, sel_registerName("activateIgnoringOtherApps:"), 1);
        return succeeded;
    }

    /// <summary>
    /// NSFileManager trashItemAtURL: the file goes to the Trash of its volume, where Finder can put it back. Null when
    /// it worked, else the reason macOS gives.
    /// </summary>
    public static Filee.Core.Platform.TrashRefusal? MoveToTrash(string path)
    {
        using var pool = new AutoreleasePool();
        var text = CFStringCreateWithCString(0, Path.GetFullPath(path), 0x08000100);
        try
        {
            var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), text);
            var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            if (SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, 0, out var error))
                return null;
            var description = error == 0 ? 0 : Send(error, sel_registerName("localizedDescription"));
            return new Filee.Core.Platform.TrashRefusal("trash.failed",
                description == 0 ? null : Marshal.PtrToStringUTF8(Send(description, sel_registerName("UTF8String"))));
        }
        finally
        {
            CFRelease(text);
        }
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);

    private readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        public AutoreleasePool() { }
        public void Dispose() => SendVoid(_pool, sel_registerName("drain"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXIsProcessTrusted();

    /// <summary>
    /// Whether Filee may watch input; when not, macOS shows its own alert (Open System Settings / Deny) and lists
    /// Filee under Accessibility, switched off, so the user only has to switch it on.
    /// </summary>
    internal static bool RequestAccessibility()
    {
        var coreFoundation = NativeLibrary.Load(CoreFoundation);
        var trueValue = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFBooleanTrue"));
        var key = CFStringCreateWithCString(0, "AXTrustedCheckOptionPrompt", 0x08000100);
        var options = CFDictionaryCreate(0, [key], [trueValue], 1,
            NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"));
        try
        {
            return AXIsProcessTrustedWithOptions(options);
        }
        finally
        {
            CFRelease(options);
            CFRelease(key);
        }
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(nint options);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallBacks, nint valueCallBacks);

    [DllImport(CoreGraphics)]
    private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out CGRect rect);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetValue(nint dictionary, nint key);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetInt(nint number, nint type, out int value);

    [DllImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetDouble(nint number, nint type, out double value);

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolWithInteger(nint receiver, nint selector, nint value);
}
