// The forms web browsers put a dragged picture, link or download into: pure parsing, so it can be tested anywhere.
// The App reads the raw formats from the drag (Avalonia) and DraggedWebItems decides what in them is a file.
//
//   FileGroupDescriptorW      Windows: names (and sizes) of files that exist only inside the source app, whose
//                             bytes come with FileContents (Chrome and Edge: a dragged picture; any link: a ".url").
//   UniformResourceLocatorW   Windows: the link or picture address, UTF-16.
//   text/x-moz-url            Firefox (and Chrome, for Firefox's sake): "address\ntitle" pairs, UTF-16.
//   _NETSCAPE_URL             Linux: "address\ntitle", UTF-8.
//   text/uri-list             RFC 2483: one address per line, "#" lines are comments.
//   DownloadURL               Chrome, when a page sets it (Gmail attachments): "type:name:address".
//   application/x-moz-file-promise-url (+ -dest-filename)   Firefox: the picture a drag promises, and its name.
//   public.url / public.url-name                              macOS: the address and its title.
//   text/html, HTML Format    the dragged fragment; a picture drag is one <img> (maybe inside a link), no text.

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Core.Web;

/// <summary>One entry of a FileGroupDescriptorW.</summary>
/// <param name="Name">The file's name (may contain a relative folder, "photos\a.jpg").</param>
/// <param name="Size">Its size when the source says it.</param>
/// <param name="IsFolder">A folder entry (no contents of its own).</param>
public sealed record FileDescriptor(string Name, long? Size, bool IsFolder);

/// <summary>A DownloadURL value: the file's media type, its name and where it is.</summary>
public sealed record DownloadUrl(string Mime, string Name, Uri Url);

public static partial class BrowserDragFormats
{
    public const string FileGroupDescriptorW = "FileGroupDescriptorW";
    public const string UniformResourceLocatorW = "UniformResourceLocatorW";
    public const string UniformResourceLocator = "UniformResourceLocator";
    public const string MozUrl = "text/x-moz-url";
    public const string NetscapeUrl = "_NETSCAPE_URL";
    public const string UriList = "text/uri-list";
    public const string DownloadUrlFormat = "DownloadURL";
    public const string MozFilePromiseUrl = "application/x-moz-file-promise-url";
    public const string MozFilePromiseName = "application/x-moz-file-promise-dest-filename";
    public const string MacUrl = "public.url";
    public const string MacUrlName = "public.url-name";
    public const string Html = "text/html";
    public const string WindowsHtml = "HTML Format";
    public const string MacHtml = "public.html";

    // FILEDESCRIPTORW: dwFlags, clsid, sizel, pointl, dwFileAttributes, three FILETIMEs, nFileSizeHigh/Low,
    // cFileName[MAX_PATH] (UTF-16) = 592 bytes, after a 4-byte count.
    private const int DescriptorSize = 592;
    private const int SizeHighOffset = 64;
    private const int AttributesOffset = 36;
    private const int NameOffset = 72;
    private const int NameChars = 260;
    private const uint FdAttributes = 0x4;
    private const uint FdFileSize = 0x40;
    private const uint FileAttributeDirectory = 0x10;

    /// <summary>The entries of a FileGroupDescriptorW; empty when the data is cut short or makes no sense.</summary>
    public static IReadOnlyList<FileDescriptor> ParseFileGroupDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return [];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count == 0 || count > 10_000 || data.Length < 4 + (long)count * DescriptorSize)
            return [];
        var entries = new List<FileDescriptor>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            var entry = data.Slice(4 + i * DescriptorSize, DescriptorSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var nameBytes = entry.Slice(NameOffset, NameChars * 2);
            var name = Encoding.Unicode.GetString(nameBytes);
            var end = name.IndexOf('\0');
            if (end >= 0)
                name = name[..end];
            long? size = (flags & FdFileSize) != 0
                ? (long)(((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry[SizeHighOffset..]) << 32)
                         | BinaryPrimitives.ReadUInt32LittleEndian(entry[(SizeHighOffset + 4)..]))
                : null;
            var folder = (flags & FdAttributes) != 0
                         && (BinaryPrimitives.ReadUInt32LittleEndian(entry[AttributesOffset..]) & FileAttributeDirectory) != 0;
            if (name.Length > 0)
                entries.Add(new FileDescriptor(name, size, folder));
        }
        return entries;
    }

    /// <summary>Builds a FileGroupDescriptorW (for tests and fakes).</summary>
    public static byte[] BuildFileGroupDescriptor(IReadOnlyList<FileDescriptor> entries)
    {
        var data = new byte[4 + entries.Count * DescriptorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = data.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            var flags = FdAttributes | (entries[i].Size is null ? 0 : FdFileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[AttributesOffset..], entries[i].IsFolder ? FileAttributeDirectory : 0x80);
            if (entries[i].Size is { } size)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[SizeHighOffset..], (uint)(size >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[(SizeHighOffset + 4)..], (uint)size);
            }
            var name = entries[i].Name.Length >= NameChars ? entries[i].Name[..(NameChars - 1)] : entries[i].Name;
            Encoding.Unicode.GetBytes(name).CopyTo(entry[NameOffset..]);
        }
        return data;
    }

    /// <summary>
    /// Text from a drag format's bytes: UTF-16 (little endian, as Windows and Firefox write it; with or without a BOM)
    /// or UTF-8, up to the first NUL (Windows pads its memory blocks).
    /// </summary>
    public static string DecodeText(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return "";
        string text;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            text = Encoding.Unicode.GetString(data, 2, (data.Length - 2) & ~1);
        else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            text = Encoding.UTF8.GetString(data, 3, data.Length - 3);
        else if (LooksLikeUtf16(data))
            text = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        else
            text = Encoding.UTF8.GetString(data);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>
    /// ASCII text in UTF-16 has a zero in every odd byte; UTF-8 text has none. The zeros Windows pads a memory block
    /// with don't count.
    /// </summary>
    private static bool LooksLikeUtf16(byte[] data)
    {
        var length = data.Length;
        while (length > 0 && data[length - 1] == 0)
            length--;
        var pairs = Math.Min((length + 1) / 2, 64);
        if (pairs == 0)
            return false;
        var zeros = 0;
        for (var i = 0; i < pairs; i++)
        {
            if (i * 2 + 1 >= length || data[i * 2 + 1] == 0)
                zeros++;
        }
        return zeros * 2 >= pairs;
    }

    /// <summary>The addresses in a text/x-moz-url or _NETSCAPE_URL value: lines alternate address and title.</summary>
    public static IReadOnlyList<string> ParseMozUrl(string? text)
    {
        var lines = Lines(text);
        var urls = new List<string>();
        for (var i = 0; i < lines.Count; i += 2)
            urls.Add(lines[i]);
        return urls;
    }

    /// <summary>The addresses in a text/uri-list value (comments and blank lines skipped).</summary>
    public static IReadOnlyList<string> ParseUriList(string? text) =>
        Lines(text).Where(line => !line.StartsWith('#')).ToList();

    /// <summary>A DownloadURL value ("image/png:photo.png:https://…"), or null.</summary>
    public static DownloadUrl? ParseDownloadUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        var first = text.IndexOf(':');
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (first <= 0 || second < 0)
            return null;
        var mime = text[..first];
        var name = text[(first + 1)..second];
        var url = text[(second + 1)..];
        if (!mime.Contains('/') || name.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return new DownloadUrl(mime, name, uri);
    }

    /// <summary>The URL= line of an Internet shortcut (".url" file contents), or null.</summary>
    public static string? ParseInternetShortcut(string? text) =>
        Lines(text).FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();

    /// <summary>
    /// The picture a dragged HTML fragment is: the src of its only &lt;img&gt;, when the fragment shows no text (a
    /// selection of text with pictures in it is not a picture). Windows' "HTML Format" header is skipped.
    /// </summary>
    public static string? ImageOnly(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
            html = html[(start + "<!--StartFragment-->".Length)..end];
        var images = ImageTag().Matches(html);
        if (images.Count != 1)
            return null;
        var withoutMarkup = Comment().Replace(html, "");
        withoutMarkup = Tag().Replace(withoutMarkup, "");
        if (WebUtility.HtmlDecode(withoutMarkup).Any(c => !char.IsWhiteSpace(c)))
            return null;
        var src = WebUtility.HtmlDecode(images[0].Groups["src"].Value).Trim();
        return src.Length == 0 ? null : src;
    }

    private static List<string> Lines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*(?:"(?<src>[^"]*)"|'(?<src>[^']*)'|(?<src>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    /// <summary>
    /// The bytes and media type of a data: URL (base64 or percent-encoded), or null when it isn't one or holds more
    /// than <paramref name="maxBytes"/>.
    /// </summary>
    public static (string Mime, byte[] Bytes)? DecodeDataUrl(string? url, int maxBytes = 256 * 1024 * 1024)
    {
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        var comma = url.IndexOf(',');
        if (comma < 0)
            return null;
        var header = url[5..comma];
        var payload = url[(comma + 1)..];
        var parts = header.Split(';');
        var mime = parts[0].Trim().Length == 0 ? "text/plain" : parts[0].Trim();
        var base64 = parts.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));
        try
        {
            byte[] bytes;
            if (base64)
            {
                payload = Uri.UnescapeDataString(payload).Replace(" ", "").Replace("\r", "").Replace("\n", "");
                if ((long)payload.Length * 3 / 4 > maxBytes)
                    return null;
                bytes = Convert.FromBase64String(payload);
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                if (bytes.Length > maxBytes)
                    return null;
            }
            return (mime, bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
