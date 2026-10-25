// Decides what in a drag from a web browser is a file to convert. The rules, in order:
//
//   1. Files the browser hands over itself (the picture's own bytes as a Windows virtual file, Firefox's temporary
//      copy): taken as they are. Internet shortcuts (".url", what any dragged link also comes with) and programs are
//      not taken.
//   2. A picture (the dragged HTML fragment is one <img> and no text; a Firefox file promise): its address.
//   3. A download a page offers (DownloadURL): its address, named as the page says.
//   4. Links: only when the address ends in an extension Filee converts (".pdf", ".docx", ".png"), never a web page
//      (".html", ".php", no extension). The server's answer is checked again when downloading.
//   5. Nothing else, but the drag carries the picture's pixels (a blob: address Filee can't fetch): those, as PNG.
//
// Plain text is never read: a selected address is text, not a file.

using Filee.Core.Formats;

namespace Filee.Core.Web;

/// <summary>The parts of a drag Filee reads for files from a browser.</summary>
public sealed class BrowserDragContent
{
    /// <summary>Files the drag hands over with their bytes (virtual files, a browser's temporary copy).</summary>
    public IReadOnlyList<WebDropItem> Files { get; init; } = [];

    /// <summary>Text of the address formats, by format name (<see cref="BrowserDragFormats"/>).</summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>();

    /// <summary>The picture's pixels from the drag itself, made into a file on request; null when there are none.</summary>
    public Func<WebDropItem?>? Picture { get; init; }

    /// <summary>True when the drag names a web address in a link format: it comes from a browser.</summary>
    public bool HasAddress => AddressFormatNames.Any(f => Texts.TryGetValue(f, out var text) && !string.IsNullOrWhiteSpace(text));

    /// <summary>The formats that carry a web address (by native name).</summary>
    public static IReadOnlyList<string> AddressFormatNames { get; } =
    [
        BrowserDragFormats.DownloadUrlFormat, BrowserDragFormats.MozFilePromiseUrl, BrowserDragFormats.MozUrl,
        BrowserDragFormats.UniformResourceLocatorW, BrowserDragFormats.UniformResourceLocator, BrowserDragFormats.NetscapeUrl,
        BrowserDragFormats.MacUrl, BrowserDragFormats.UriList,
    ];

    public string? Text(string format) =>
        Texts.TryGetValue(format, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

public static class BrowserDrag
{
    /// <summary>
    /// The files in <paramref name="content"/>, in the order they were dragged; empty when it holds none (a page link,
    /// a text selection). Files of <see cref="BrowserDragContent.Files"/> not returned are disposed.
    /// </summary>
    public static IReadOnlyList<WebDropItem> Files(BrowserDragContent content)
    {
        var handed = new List<WebDropItem>();
        foreach (var file in content.Files)
        {
            if (WebFileNames.IsBlocked(file.Name))
                file.Dispose();
            else
                handed.Add(file);
        }
        if (handed.Count > 0)
            return handed;

        var picture = content.Picture is not null;
        var html = content.Text(BrowserDragFormats.Html) ?? content.Text(BrowserDragFormats.WindowsHtml) ?? content.Text(BrowserDragFormats.MacHtml);
        if (BrowserDragFormats.ImageOnly(html) is { } src && Address(src, image: true, Base(content)) is { } image)
            return [image];

        if (content.Text(BrowserDragFormats.MozFilePromiseUrl) is { } promised
            && Address(promised.Trim(), image: true, null, content.Text(BrowserDragFormats.MozFilePromiseName)?.Trim()) is { } promise)
            return [promise];

        if (BrowserDragFormats.ParseDownloadUrl(content.Text(BrowserDragFormats.DownloadUrlFormat)) is { } download
            && Address(download.Url.OriginalString, image: false, null, download.Name, download.Mime) is { } offered)
            return [offered];

        var items = new List<WebDropItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in Links(content))
        {
            if (seen.Add(url) && Address(url, picture, null) is { } item)
                items.Add(item);
        }
        if (items.Count > 0)
            return items;

        return picture && content.HasAddress && content.Picture!() is { } pixels ? [pixels] : [];
    }

    /// <summary>The addresses of the link formats, the most precise first.</summary>
    private static IEnumerable<string> Links(BrowserDragContent content)
    {
        foreach (var url in BrowserDragFormats.ParseMozUrl(content.Text(BrowserDragFormats.MozUrl)))
            yield return url;
        foreach (var format in new[] { BrowserDragFormats.UniformResourceLocatorW, BrowserDragFormats.UniformResourceLocator, BrowserDragFormats.MacUrl })
        {
            if (content.Text(format) is { } text)
                yield return text.Trim();
        }
        foreach (var url in BrowserDragFormats.ParseMozUrl(content.Text(BrowserDragFormats.NetscapeUrl)))
            yield return url;
        foreach (var url in BrowserDragFormats.ParseUriList(content.Text(BrowserDragFormats.UriList)))
            yield return url;
    }

    /// <summary>The page address, for a picture whose src is relative.</summary>
    private static Uri? Base(BrowserDragContent content) =>
        Links(content).Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null).FirstOrDefault(u => u is not null);

    /// <summary>
    /// The file at <paramref name="address"/>, or null when it isn't one Filee takes: not http(s) or data:, a web
    /// page, a program, or a link whose type can't be told before downloading.
    /// </summary>
    /// <param name="image">The address is a picture's: taken even without an extension (".jpg" stands in until the bytes say).</param>
    internal static WebDropItem? Address(string address, bool image, Uri? pageAddress, string? name = null, string? mime = null)
    {
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (BrowserDragFormats.DecodeDataUrl(address) is not { } data
                || WebFormats.ExtensionForMime(data.Mime) is not { } dataExtension
                || FormatRegistry.FindByExtension(dataExtension) is null)
                return null;
            return WebDropItem.FromBytes(WebFileNames.WithExtension(name ?? "image", dataExtension), data.Bytes, data.Mime);
        }

        // "/img/a.png" and "//cdn/a.png" are relative to the page (on Unix .NET would take the first for a file path).
        Uri? url = null;
        if (address.Contains("://", StringComparison.Ordinal))
            Uri.TryCreate(address, UriKind.Absolute, out url);
        else if (pageAddress is not null)
            Uri.TryCreate(pageAddress, address, out url);
        if (url is null || !WebDownloader.IsWebAddress(url))
            return null;

        var fileName = WebFileNames.Sanitize(name ?? WebFileNames.FromUrl(url), image ? "image" : "download");
        if (WebFileNames.IsBlocked(fileName))
            return null;
        if (WebFileNames.IsPage(fileName))
        {
            if (!image)
                return null;
            fileName = FormatRegistry.NameWithoutExtension(fileName);
        }
        if (FormatRegistry.Detect(fileName) is not null)
            return WebDropItem.FromAddress(fileName, url);
        if (image || WebFormats.IsImage(mime))
            return WebDropItem.FromAddress(WebFileNames.WithExtension(fileName, WebFormats.ExtensionForMime(mime) ?? "jpg"), url);
        if (WebFormats.ExtensionForMime(mime) is { } extension && FormatRegistry.FindByExtension(extension) is not null)
            return WebDropItem.FromAddress(WebFileNames.WithExtension(fileName, extension), url);
        return null;
    }
}
