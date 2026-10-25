// Files dragged out of a web browser, read from Avalonia's drag data. Avalonia 12 gives Windows virtual files
// (FileGroupDescriptorW + FileContents: a picture Chrome or Edge hands over) as storage items without a local path,
// and every other format a drag offers as a platform format by its native name. Which of those make a file is decided
// in Filee.Core (BrowserDrag); this file only collects them.
//
// Linux: Avalonia turns text/uri-list into local files only (http addresses are dropped), so links come from
// text/x-moz-url and _NETSCAPE_URL there. macOS: file promises can't be received; public.url, HTML and picture data can.

using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Filee.Core.Platform;
using Filee.Core.Web;

namespace Filee.App.Services;

internal static class BrowserDrags
{
    /// <summary>Formats whose text Filee reads, by native name.</summary>
    private static readonly string[] TextFormats =
    [
        BrowserDragFormats.UniformResourceLocatorW, BrowserDragFormats.UniformResourceLocator, BrowserDragFormats.MozUrl,
        BrowserDragFormats.NetscapeUrl, BrowserDragFormats.UriList, BrowserDragFormats.DownloadUrlFormat,
        BrowserDragFormats.MozFilePromiseUrl, BrowserDragFormats.MozFilePromiseName, BrowserDragFormats.MacUrl,
        BrowserDragFormats.MacUrlName, BrowserDragFormats.Html, BrowserDragFormats.WindowsHtml, BrowserDragFormats.MacHtml,
    ];

    /// <summary>Picture data some browsers put on a drag (macOS, X11), with the extension it is saved with.</summary>
    private static readonly (string Format, string Extension)[] PictureFormats =
    [
        ("public.jpeg", "jpg"), ("public.tiff", "tiff"), ("com.compuserve.gif", "gif"), ("org.webmproject.webp", "webp"),
        ("image/png", "png"), ("image/jpeg", "jpg"), ("image/gif", "gif"), ("image/webp", "webp"),
    ];

    /// <summary>
    /// Whether the drag may hold a file from a browser (an address or a virtual file): cheap, for the drop effect while
    /// dragging over the drop zone.
    /// </summary>
    public static bool MayOffer(IDataTransfer data) =>
        data.Formats.Any(f => f.Kind == DataFormatKind.Platform
                              && (BrowserDragFormats.FileGroupDescriptorW.Equals(f.Identifier, StringComparison.OrdinalIgnoreCase)
                                  || BrowserDragContent.AddressFormatNames.Contains(f.Identifier, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// The files of a drag from a browser (or another app that holds them only in memory), standing as placeholders;
    /// null when there are none or the drag holds files on disk (those are read as they are).
    /// </summary>
    /// <param name="forDrop">
    /// False while dragging: only the names count, and names a Windows drag lists without handing their bytes yet are
    /// taken too. The returned drop must be disposed either way.
    /// </param>
    public static WebDrop? Read(IDataTransfer data, bool forDrop) =>
        Read(data, forDrop, WebDrop.DefaultWorkRoot, IsTemporary);

    internal static WebDrop? Read(IDataTransfer data, bool forDrop, string workRoot, Func<string, bool> isTemporary)
    {
        var content = Content(data, forDrop, isTemporary);
        if (content is null)
            return null;
        var items = BrowserDrag.Files(content);
        return items.Count == 0 ? null : new WebDrop(items, workRoot);
    }

    internal static BrowserDragContent? Content(IDataTransfer data, bool forDrop, Func<string, bool> isTemporary)
    {
        var texts = Texts(data);
        var hasAddress = new BrowserDragContent { Texts = texts }.HasAddress;
        var storageItems = data.TryGetFiles() ?? [];

        var files = new List<WebDropItem>();
        foreach (var storageItem in storageItems)
        {
            if (storageItem.TryGetLocalPath() is { } path)
            {
                // A file on disk is read as it is, unless it is a browser's temporary copy (Firefox writes the dragged
                // picture into the temp folder): that one is saved like any file from the browser.
                if (!hasAddress || !isTemporary(path) || !File.Exists(path))
                {
                    DisposeAll(files);
                    return null;
                }
                files.Add(WebDropItem.FromTemporaryFile(path));
            }
            else if (storageItem is IStorageFile file)
            {
                // Disposing a Windows virtual file ends the transfer with the source app: only the drop's files, once
                // saved. While dragging, the drag still needs it.
                files.Add(WebDropItem.FromStream(file.Name, _ => file.OpenReadAsync(), owner: forDrop ? file : null));
            }
        }

        // While dragging, Windows may list a browser's files before it hands over their bytes.
        if (files.Count == 0 && !forDrop && Raw(data, BrowserDragFormats.FileGroupDescriptorW) is byte[] descriptor)
        {
            foreach (var entry in BrowserDragFormats.ParseFileGroupDescriptor(descriptor).Where(e => !e.IsFolder))
                files.Add(WebDropItem.FromStream(entry.Name, _ => throw new IOException("The file's contents weren't handed over.")));
        }

        if (files.Count == 0 && !hasAddress)
            return null;
        return new BrowserDragContent { Files = files, Texts = texts, Picture = Picture(data, texts) };
    }

    /// <summary>True for a path in the system's temporary folder (but not in Filee's own work folders).</summary>
    internal static bool IsTemporary(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WebDrop.DefaultWorkRoot)) + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, FileSystemPaths.Comparison) && !full.StartsWith(own, FileSystemPaths.Comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Texts(IDataTransfer data)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Items)
        {
            foreach (var format in item.Formats)
            {
                if (format.Kind != DataFormatKind.Platform || texts.ContainsKey(format.Identifier)
                    || !TextFormats.Contains(format.Identifier, StringComparer.OrdinalIgnoreCase))
                    continue;
                var text = Text(item.TryGetRaw(format));
                if (!string.IsNullOrWhiteSpace(text))
                    texts[format.Identifier] = text;
            }
        }
        // Formats are matched by their native name, whatever its case: keep one spelling, the constants'.
        return TextFormats.Where(texts.ContainsKey).ToDictionary(f => f, f => texts[f], StringComparer.OrdinalIgnoreCase);
    }

    private static string? Text(object? raw) => raw switch
    {
        string text => text.TrimEnd('\0'),
        byte[] bytes => BrowserDragFormats.DecodeText(bytes),
        _ => null,
    };

    private static object? Raw(IDataTransfer data, string identifier)
    {
        foreach (var item in data.Items)
        {
            foreach (var format in item.Formats)
            {
                if (format.Kind == DataFormatKind.Platform && format.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    return item.TryGetRaw(format);
            }
        }
        return null;
    }

    /// <summary>The picture's pixels from the drag, made into a file when nothing better is found (a blob: picture).</summary>
    private static Func<WebDropItem?>? Picture(IDataTransfer data, IReadOnlyDictionary<string, string> texts)
    {
        var raw = PictureFormats.FirstOrDefault(p => Raw(data, p.Format) is byte[] { Length: > 0 });
        var hasBitmap = data.Formats.Contains(DataFormat.Bitmap);
        if (raw.Format is null && !hasBitmap)
            return null;
        return () =>
        {
            var name = PictureName(texts);
            if (raw.Format is not null && Raw(data, raw.Format) is byte[] { Length: > 0 } bytes)
                return WebDropItem.FromBytes($"{name}.{raw.Extension}", bytes);
            if (data.TryGetValue(DataFormat.Bitmap) is not Bitmap bitmap)
                return null;
            using (bitmap)
            {
                using var png = new MemoryStream();
                bitmap.Save(png, new PngBitmapEncoderOptions());
                return WebDropItem.FromBytes($"{name}.png", png.ToArray());
            }
        };
    }

    /// <summary>The picture's name from its address ("photo" for …/photo.jpg?w=200), else "image".</summary>
    private static string PictureName(IReadOnlyDictionary<string, string> texts)
    {
        foreach (var format in BrowserDragContent.AddressFormatNames)
        {
            if (!texts.TryGetValue(format, out var text))
                continue;
            var first = BrowserDragFormats.ParseMozUrl(text).FirstOrDefault();
            if (first is not null && Uri.TryCreate(first, UriKind.Absolute, out var url) && WebFileNames.FromUrl(url) is { } fromUrl
                && !WebFileNames.IsPage(fromUrl))
                return Path.GetFileNameWithoutExtension(WebFileNames.Sanitize(fromUrl, "image"));
        }
        return "image";
    }

    private static void DisposeAll(List<WebDropItem> items)
    {
        foreach (var item in items)
            item.Dispose();
    }
}
