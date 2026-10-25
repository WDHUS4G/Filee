// File paths in the forms other Mac apps put them on a pasteboard. Finder writes one file URL per item
// (public.file-url); other file managers (QSpace Pro, issue #160) write the older NSFilenamesPboardType, a property
// list array of paths, and some only plain text. Pure parsing here, so it can be tested anywhere; the pasteboard
// itself is read by MacOSServicesProvider (the Services menu) and RadialWindow (drags, through Avalonia).

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Platform;

namespace Filee.Platform.MacOS;

public static class PasteboardPaths
{
    /// <summary>The pasteboard type of an array of paths, as older apps write it.</summary>
    public const string FilenamesType = "NSFilenamesPboardType";

    /// <summary>
    /// The same type as a pasteboard item lists it: AppKit gives types that aren't UTIs a dynamic UTI.
    /// </summary>
    public const string FilenamesUti = "dyn.ah62d4rv4gu8y6y4grf0gn5xbrzw1gydcr7u1e3cytf2gn";

    public const string FileUrlType = "public.file-url";

    public const string TextType = "public.utf8-plain-text";

    /// <summary>
    /// Types the donut registers for on top of Avalonia's own, so a drag offering only these still reaches it (and its
    /// types get logged when Filee can't read them). The three file promise types can't be received; a browser drag
    /// that offers one also offers the picture's or link's address (public.url) and HTML, which Filee reads instead,
    /// and sometimes the picture itself.
    /// </summary>
    public static IReadOnlyList<string> DragTypes { get; } =
    [
        FileUrlType, FilenamesType,
        "com.apple.NSFilePromiseItemMetaData", "com.apple.pasteboard.promised-file-url", "Apple files promise pasteboard type",
        "public.url", "public.html", "public.png", "public.jpeg", "public.tiff", "com.compuserve.gif", "org.webmproject.webp",
    ];

    /// <summary>Whether <paramref name="type"/> names NSFilenamesPboardType, by its name or its dynamic UTI.</summary>
    public static bool IsFilenamesType(string type) => type is FilenamesType or FilenamesUti;

    /// <summary>
    /// The paths in an NSFilenamesPboardType property list as a pasteboard item's data: AppKit writes property lists to
    /// the pasteboard as XML.
    /// </summary>
    public static IReadOnlyList<string> FromPropertyList(byte[]? data) =>
        data is null || data.Length == 0 ? [] : FromPropertyList(Encoding.UTF8.GetString(data));

    /// <summary>The paths in an NSFilenamesPboardType property list written as XML.</summary>
    public static IReadOnlyList<string> FromPropertyList(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];
        try
        {
            // The plist DOCTYPE names Apple's DTD: never fetched (DtdProcessing.Ignore).
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var root = XDocument.Load(reader).Root;
            var array = root?.Name.LocalName == "plist" ? root.Element("array") : root?.Name.LocalName == "array" ? root : null;
            return array?.Elements("string").Select(e => e.Value).Where(IsAbsolute).ToList() ?? [];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    /// <summary>The path of a file URL ("file:///Users/a/b%20c.png"), or null for anything else.</summary>
    public static string? FromFileUrl(string? url)
    {
        // The scheme is checked first: on Unix, .NET reads a bare "/path" as an absolute file URI as well.
        url = url?.Trim();
        if (url is null || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Host is not ("" or "localhost"))
            return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // A file reference URL (/.file/id=…) names no path; AppKit resolves those itself.
        return IsAbsolute(path) && !path.StartsWith("/.file/id=", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>Lines of plain text that are absolute paths or file URLs; anything else in the text is skipped.</summary>
    public static IReadOnlyList<string> FromText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? FromFileUrl(line) : line)
            .OfType<string>()
            .Where(IsAbsolute)
            .ToList();
    }

    /// <summary>
    /// The paths of the first source, in order of preference, that names any path that <paramref name="exists"/>:
    /// only those, without repeats. Later sources aren't read once one has given paths.
    /// </summary>
    public static IReadOnlyList<string> FirstExisting(IEnumerable<Func<IEnumerable<string>>> sources, Func<string, bool> exists)
    {
        foreach (var source in sources)
        {
            var paths = source().Where(exists).Distinct(FileSystemPaths.Comparer).ToList();
            if (paths.Count > 0)
                return paths;
        }
        return [];
    }

    /// <summary>A file or folder at <paramref name="path"/>.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsAbsolute(string path) => path.StartsWith('/') && path.Length > 1;
}
