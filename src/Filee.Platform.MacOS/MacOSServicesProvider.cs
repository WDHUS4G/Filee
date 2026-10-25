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
    private static Action<IReadOnlyList<string>>? _nothingUsable;
    private static nint _provider;

    /// <summary>
    /// Answers the service from now on; <paramref name="handler"/> gets the files (on the main thread). Call it on the
    /// main thread early at start: a service picked while Filee wasn't running is delivered once this is set. When the
    /// pasteboard names no existing file, <paramref name="nothingUsable"/> gets the types it offered instead.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Register(Action<IReadOnlyList<string>> handler, Action<IReadOnlyList<string>>? nothingUsable = null)
    {
        _handler = handler;
        _nothingUsable = nothingUsable;
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
            else
                _nothingUsable?.Invoke(Strings(Send(pasteboard, sel_registerName("types"))));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An exception must not unwind into Objective-C; the menu item simply does nothing then.
        }
    }

    /// <summary>
    /// The existing files and folders on a pasteboard. Finder puts file URLs there; other file managers (QSpace Pro,
    /// issue #160) may put the older NSFilenamesPboardType array of paths, or only text: the first of these that names any.
    /// </summary>
    private static IReadOnlyList<string> FilesOn(nint pasteboard) => PasteboardPaths.FirstExisting(
    [
        () => FileUrls(pasteboard),
        () => Strings(Send(pasteboard, sel_registerName("propertyListForType:"), NSString(PasteboardPaths.FilenamesType))),
        () => ItemStrings(pasteboard, PasteboardPaths.FileUrlType).Select(PasteboardPaths.FromFileUrl).OfType<string>(),
        () => ItemStrings(pasteboard, PasteboardPaths.TextType).SelectMany(PasteboardPaths.FromText),
    ], PasteboardPaths.Exists);

    /// <summary>File URLs read as NSURL objects (AppKit resolves file reference URLs).</summary>
    private static List<string> FileUrls(nint pasteboard)
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

    /// <summary>The value of <paramref name="type"/> as text on each pasteboard item that has it.</summary>
    private static List<string> ItemStrings(nint pasteboard, string type)
    {
        var values = new List<string>();
        var items = Send(pasteboard, sel_registerName("pasteboardItems"));
        if (items == 0)
            return values;
        var name = NSString(type);
        var count = Send(items, sel_registerName("count"));
        for (nint index = 0; index < count; index++)
        {
            var item = Send(items, sel_registerName("objectAtIndex:"), index);
            if (Utf8(Send(item, sel_registerName("stringForType:"), name)) is { Length: > 0 } value)
                values.Add(value);
        }
        return values;
    }

    /// <summary>The strings in an NSArray (anything else in it is skipped); none for nil or any other object.</summary>
    private static List<string> Strings(nint array)
    {
        var values = new List<string>();
        if (array == 0 || !SendBool(array, sel_registerName("isKindOfClass:"), objc_getClass("NSArray")))
            return values;
        var stringClass = objc_getClass("NSString");
        var count = Send(array, sel_registerName("count"));
        for (nint index = 0; index < count; index++)
        {
            var value = Send(array, sel_registerName("objectAtIndex:"), index);
            if (SendBool(value, sel_registerName("isKindOfClass:"), stringClass) && Utf8(value) is { } text)
                values.Add(text);
        }
        return values;
    }

    /// <summary>An autoreleased NSString (the service method runs inside AppKit's autorelease pool).</summary>
    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string? Utf8(nint text) =>
        text == 0 ? null : Marshal.PtrToStringUTF8(Send(text, sel_registerName("UTF8String")));

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

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint argument);

    [DllImport(AppKit)]
    private static extern void NSUpdateDynamicServices();
}
