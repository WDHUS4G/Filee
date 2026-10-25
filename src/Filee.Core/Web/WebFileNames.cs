// Names for files that come from the web: whatever a server or a page says a file is called is cut down to a plain
// file name that is safe on every supported OS, and files are created under a free name (never over an existing one).

using System.Net.Http.Headers;
using Filee.Core.Formats;
using Filee.Core.Presets;

namespace Filee.Core.Web;

public static class WebFileNames
{
    /// <summary>Longest name Filee gives a downloaded file, extension included (paths stay well under 260 characters).</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// Programs, installers and scripts. Filee converts none of them; a drag or a server that offers one is refused
    /// rather than saved.
    /// </summary>
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "msp", "com", "scr", "pif", "cpl", "dll", "sys",
        "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "mjs", "wsf", "wsh", "hta", "reg", "inf",
        "lnk", "url", "scf", "application", "appref-ms", "gadget", "msc",
        "sh", "bash", "zsh", "csh", "ksh", "command", "tool", "run", "bin", "appimage", "desktop",
        "app", "dmg", "pkg", "mpkg", "kext", "workflow", "scpt", "applescript",
        "jar", "jnlp", "apk", "xapk", "ipa", "deb", "rpm", "snap", "flatpak", "py", "pyw", "pl", "rb",
    };

    /// <summary>Extensions of web pages: a link to one is a page to read, not a file to convert.</summary>
    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "htm", "html", "xhtml", "shtml", "php", "php3", "php4", "php5", "phtml", "asp", "aspx", "jsp", "jspx", "cgi",
        "cfm", "do", "action",
    };

    /// <summary>Names Windows keeps for devices, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True for a program, installer or script (by extension), which Filee never saves from the web.</summary>
    public static bool IsBlocked(string name) => Blocked.Contains(LastExtension(name));

    /// <summary>True for the extension of a web page (.html, .php, …).</summary>
    public static bool IsPage(string name) => PageExtensions.Contains(LastExtension(name));

    /// <summary>
    /// A plain file name made from <paramref name="name"/>: only its last path part, without characters any supported
    /// OS refuses, not a device name, at most <see cref="MaxLength"/> characters (the extension is kept), and never
    /// empty (<paramref name="fallback"/>).
    /// </summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        name ??= "";
        // Only the last part: "../../x.png", "C:\x.png" and "a/b.png" all become a name in the target folder.
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        name = OutputPathResolver.Sanitize(name, "").Trim(' ', '.');
        if (name.Length == 0)
            name = fallback;

        var extension = Path.GetExtension(name);
        if (extension.Length > 16)
            extension = "";
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        if (ReservedStems.Contains(stem.TrimEnd(' ', '.')))
            stem = "_" + stem;
        if (stem.Length + extension.Length > MaxLength)
            stem = stem[..(MaxLength - extension.Length)].TrimEnd(' ', '.');
        // Don't end in half of a surrogate pair after cutting.
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1]))
            stem = stem[..^1];
        if (stem.Length == 0)
            stem = fallback;
        return stem + extension;
    }

    /// <summary>The file name a Content-Disposition header gives (RFC 6266, <c>filename*</c> first), or null.</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !ContentDispositionHeaderValue.TryParse(header, out var value))
            return null;
        var name = value.FileNameStar ?? value.FileName;
        name = name?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>The last part of the URL's path, unescaped ("photo%201.jpg" → "photo 1.jpg"), or null when there is none.</summary>
    public static string? FromUrl(Uri url)
    {
        if (!url.IsAbsoluteUri)
            return null;
        var path = url.AbsolutePath;
        var slash = path.LastIndexOf('/');
        var last = slash >= 0 ? path[(slash + 1)..] : path;
        if (last.Length == 0)
            return null;
        try
        {
            last = Uri.UnescapeDataString(last);
        }
        catch (UriFormatException)
        {
        }
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    /// <summary>
    /// <paramref name="name"/> with <paramref name="extension"/> (no dot) when it doesn't already have an extension of
    /// the same format: "photo" → "photo.jpg", "photo.jpeg" stays, "photo.png" with "webp" → "photo.webp".
    /// </summary>
    public static string WithExtension(string name, string extension)
    {
        var current = FormatRegistry.ExtensionOf(name);
        var wanted = FormatRegistry.FindByExtension(extension);
        if (current.Length > 0 && wanted is not null && FormatRegistry.FindByExtension(current) == wanted)
            return name;
        var stem = current.Length > 0 && FormatRegistry.FindByExtension(current) is not null
            ? FormatRegistry.NameWithoutExtension(name)
            : name;
        return Sanitize($"{stem}.{extension}");
    }

    /// <summary>
    /// Creates <paramref name="name"/> in <paramref name="folder"/>, or "name (2).ext", "name (3).ext" … when that is
    /// taken (as converted files are named), and returns the open file. Never replaces an existing file.
    /// </summary>
    public static FileStream CreateUnique(string folder, string name, out string path)
    {
        Directory.CreateDirectory(folder);
        for (var n = 1; ; n++)
        {
            path = Path.Combine(folder, Numbered(name, n));
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }
            catch (IOException) when ((File.Exists(path) || Directory.Exists(path)) && n < 10_000)
            {
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> into <paramref name="folder"/> as <paramref name="name"/>, numbered like
    /// <see cref="CreateUnique"/> when taken, and returns where it went. Never replaces an existing file.
    /// </summary>
    public static string MoveUnique(string source, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, Numbered(name, n));
            if (File.Exists(path) || Directory.Exists(path))
                continue;
            try
            {
                File.Move(source, path, overwrite: false);
                return path;
            }
            catch (IOException) when ((File.Exists(path) || Directory.Exists(path)) && n < 10_000)
            {
            }
        }
    }

    private static string Numbered(string name, int n)
    {
        if (n == 1)
            return name;
        var extension = FormatRegistry.ExtensionOf(name);
        var stem = extension.Length == 0 ? name : name[..^(extension.Length + 1)];
        return extension.Length == 0 ? $"{stem} ({n})" : $"{stem} ({n}).{extension}";
    }

    private static string LastExtension(string name) => Path.GetExtension(name.TrimEnd(' ', '.')).TrimStart('.');
}
