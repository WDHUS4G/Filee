// "Convert with Filee" in Finder's right-click menu, as a service Filee.app provides itself (NSServices in its
// Info.plist). macOS hands the selected files straight to the running Filee: no Automator workflow, no shell, no
// second process. Info.plist names the message (convertFiles) and the port (Filee); this registers the object
// that answers it, an NSObject subclass made at run time with one method, convertFiles:userData:error:.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.MacOS;

public static class MacOSServicesProvider
{
    /// <summary>The service's menu title in Info.plist; ServicesMenu.strings in each .lproj translates it.</summary>
    public const string MenuItem = "Convert with Filee";

    /// <summary>The selector macOS sends, without its argument labels (NSMessage in Info.plist).</summary>
    public const string Message = "convertFiles";

    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private delegate void ServiceMethod(nint self, nint selector, nint pasteboard, nint userData, nint error);

    // Kept alive for as long as Objective-C may call it.
    private static readonly ServiceMethod Method = ConvertFiles;
    private static Action<IReadOnlyList<string>>? _handler;
    private static nint _provider;

    /// <summary>
    /// Answers the service from now on; <paramref name="handler"/> gets the files (on the main thread). Call it on the
    /// main thread early at start: a service picked while Filee wasn't running is delivered once this is set.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Register(Action<IReadOnlyList<string>> handler)
    {
        _handler = handler;
        if (_provider != 0)
            return;
        NativeLibrary.Load(AppKit);
        var type = objc_getClass("FileeServicesProvider");
        if (type == 0)
        {
            type = objc_allocateClassPair(objc_getClass("NSObject"), "FileeServicesProvider", 0);
            class_addMethod(type, sel_registerName(Message + ":userData:error:"), Marshal.GetFunctionPointerForDelegate(Method), "v@:@@^@");
            objc_registerClassPair(type);
        }
        _provider = Send(Send(type, sel_registerName("alloc")), sel_registerName("init"));
        var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        SendVoid(application, sel_registerName("setServicesProvider:"), _provider);
        NSUpdateDynamicServices();
    }

    private static void ConvertFiles(nint self, nint selector, nint pasteboard, nint userData, nint error)
    {
        try
        {
            var files = FilesOn(pasteboard);
            if (files.Count > 0)
                _handler?.Invoke(files);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An exception must not unwind into Objective-C; the menu item simply does nothing then.
        }
    }

    /// <summary>The file paths on a pasteboard (Finder puts file URLs there).</summary>
    private static List<string> FilesOn(nint pasteboard)
    {
        var files = new List<string>();
        var classes = Send(objc_getClass("NSArray"), sel_registerName("arrayWithObject:"), objc_getClass("NSURL"));
        var urls = Send(pasteboard, sel_registerName("readObjectsForClasses:options:"), classes, 0);
        if (urls == 0)
            return files;
        var count = Send(urls, sel_registerName("count"));
        for (nint index = 0; index < count; index++)
        {
            var url = Send(urls, sel_registerName("objectAtIndex:"), index);
            if (!SendBool(url, sel_registerName("isFileURL")))
                continue;
            var path = Marshal.PtrToStringUTF8(Send(Send(url, sel_registerName("path")), sel_registerName("UTF8String")));
            if (!string.IsNullOrEmpty(path))
                files.Add(path);
        }
        return files;
    }

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(nint type);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint type, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint first, nint second);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector);

    [DllImport(AppKit)]
    private static extern void NSUpdateDynamicServices();
}
