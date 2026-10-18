// The user's language on macOS. An app started from Finder gets no LANG variable, so .NET's InstalledUICulture is
// the invariant culture there; macOS keeps the language list (System Settings > General > Language & Region) in
// AppleLanguages, which CFLocaleCopyPreferredLanguages returns, unfiltered by the app's own localizations.

using System.Runtime.InteropServices;
using System.Text;

namespace Filee.Platform.MacOS;

public static class MacOSLocale
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    /// <summary>The first language the user prefers, e.g. "ko-KR" or "zh-Hans-CN"; null when macOS gives none.</summary>
    public static string? PreferredLanguage()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var languages = CFLocaleCopyPreferredLanguages();
        if (languages == 0)
            return null;
        try
        {
            if (CFArrayGetCount(languages) == 0)
                return null;
            var first = CFArrayGetValueAtIndex(languages, 0);
            var buffer = new byte[128];
            return CFStringGetCString(first, buffer, buffer.Length, Utf8)
                ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
                : null;
        }
        finally
        {
            CFRelease(languages);
        }
    }

    [DllImport(CoreFoundation)]
    private static extern nint CFLocaleCopyPreferredLanguages();

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
