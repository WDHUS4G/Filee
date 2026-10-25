// MHTML web archives (.mhtml / .mht, RFC 2557): a web page saved with its pictures and style sheets in one MIME
// message, the structure of an e-mail, so MimeKit reads it. Chrome and Edge write multipart/related with the page
// first, pictures named by Content-Location (the URL they came from) and style sheets as "cid:…@mhtml.blink" parts;
// Word and Internet Explorer write .mht files whose parts sit at file:///C:/… locations next to the page, referred to
// relatively ("page_files/image001.png"). The page becomes one standalone HTML file: linked style sheets are inlined,
// pictures of the archive become data: URIs, and the e-mail sanitizer (EmlConverter.SafeHtml) drops scripts, frames,
// forms and anything that would load from elsewhere. Nothing is fetched: a reference the archive does not hold is
// removed, and Content-Location values are only compared as text, never opened or written as files.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Filee.Engines.Hwp.Hwpx;
using MimeKit;

namespace Filee.Engines.Email;

/// <summary>MHTML → a standalone, sanitized HTML page (and the document model, for the HWPX and DOCX writers).</summary>
internal static partial class MhtmlArchive
{
    /// <summary>Larger archives are refused: MimeKit keeps every part in memory.</summary>
    internal const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>A picture larger than this (decoded) is left out.</summary>
    internal const int MaxPictureBytes = 32 * 1024 * 1024;

    /// <summary>The data URIs of one page together; a picture shown many times counts every time.</summary>
    internal const long MaxEmbeddedChars = 192L * 1024 * 1024;

    /// <summary>Style sheet text inlined into one page; @import is followed this deep.</summary>
    private const int MaxStyleChars = 8 * 1024 * 1024;
    private const int MaxImportDepth = 4;

    /// <summary>Parses an archive, refusing files over <see cref="MaxFileBytes"/>.</summary>
    public static MimeMessage Load(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException($"The web archive is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        return EmlConverter.Load(path, cancellationToken);
    }

    /// <summary>Reads an archive into the document model (MHTML → HWPX, DOCX and on to PDF).</summary>
    public static HDocument ReadDocument(string path, string mediaFolder)
    {
        var html = Html(Load(path));
        // The page refers to nothing but data: URIs; no local file is looked up outside the job's media folder.
        var content = HtmlReader.Parse(html, new HtmlReadOptions { BaseFolder = mediaFolder, MediaFolder = mediaFolder, ResourceRoot = mediaFolder });
        return HtmlReader.ToDocument(content);
    }

    /// <summary>The saved page as one HTML file: style sheets inline, pictures as data URIs, nothing active or remote.</summary>
    public static string Html(MimeMessage message)
    {
        var page = Page(message) ?? throw new InvalidDataException("The web archive holds no HTML page.");
        var archive = new Archive(message, page);
        var document = new HtmlParser().ParseDocument(Text(page, HtmlReader.Decode));
        var baseUri = document.QuerySelector("base[href]")?.GetAttribute("href") is { } baseHref
                      && Absolute(baseHref, archive.PageLocation) is { } declared
            ? declared
            : archive.PageLocation;

        foreach (var style in document.QuerySelectorAll("style").ToArray())
            style.TextContent = archive.Css(style.TextContent, baseUri, 0);
        foreach (var link in document.QuerySelectorAll("link[href]").ToArray())
        {
            if (!(link.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase)
                || archive.StyleSheet(link.GetAttribute("href"), baseUri, 0) is not { } css)
                continue;
            var media = link.GetAttribute("media")?.Trim();
            var inline = document.CreateElement("style");
            inline.TextContent = string.IsNullOrEmpty(media) || media.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? css
                : $"@media {media.Replace("<", "\\3c ", StringComparison.Ordinal)} {{\n{css}\n}}";
            link.Parent?.ReplaceChild(inline, link);
        }
        foreach (var element in document.QuerySelectorAll("[style]").ToArray())
            element.SetAttribute("style", archive.Css(element.GetAttribute("style") ?? "", baseUri, 0));
        foreach (var element in document.QuerySelectorAll("[background]").ToArray())
        {
            // Old pages and Word put the background picture in an attribute the sanitizer drops: move it into CSS.
            if (archive.Picture(element.GetAttribute("background"), baseUri) is { } picture)
                element.SetAttribute("style", $"background-image: url(\"{picture}\"); {element.GetAttribute("style")}");
        }
        foreach (var image in document.QuerySelectorAll("img").ToArray())
        {
            var candidates = new[] { image.GetAttribute("src") }
                .Concat(SourceSet(image.GetAttribute("srcset")))
                .Concat(image.ParentElement is { LocalName: "picture" } picture
                    ? picture.Children.Where(c => c.LocalName == "source").SelectMany(c => SourceSet(c.GetAttribute("srcset")))
                    : []);
            if (candidates.Select(c => archive.Picture(c, baseUri)).FirstOrDefault(p => p is not null) is { } data)
                image.SetAttribute("src", data);
            else
                image.RemoveAttribute("src");
        }
        foreach (var anchor in document.QuerySelectorAll("a[href]").ToArray())
        {
            var href = anchor.GetAttribute("href")!.Trim();
            if (href.StartsWith('#') || Absolute(href, baseUri) is not { } target)
                continue;
            if (archive.PageLocation is { } location && Key(target) == Key(location))
                anchor.SetAttribute("href", target.Fragment.Length > 1 ? target.Fragment : "#");
            else if (target.Scheme is "http" or "https" or "mailto")
                anchor.SetAttribute("href", target.AbsoluteUri);
        }

        var title = document.Title?.Trim() is { Length: > 0 } documentTitle ? documentTitle : message.Subject?.Trim() ?? "";
        var language = document.DocumentElement?.GetAttribute("lang") is { } lang && LanguageTag().IsMatch(lang) ? lang : null;
        var bodyAttributes = new StringBuilder();
        foreach (var attribute in document.Body?.Attributes.ToArray() ?? [])
        {
            if (EmlConverter.SafeHtmlAttributes.Contains(attribute.Name))
                bodyAttributes.Append($" {attribute.Name}=\"{WebUtility.HtmlEncode(attribute.Value)}\"");
        }
        var safe = EmlConverter.SafeHtml(document.DocumentElement!.OuterHtml);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html").Append(language is null ? "" : $" lang=\"{language}\"").Append(">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append(EmlConverter.ContentSecurityPolicy).Append('\n');
        sb.Append($"<title>{WebUtility.HtmlEncode(title)}</title>\n");
        sb.Append(safe.Styles);
        sb.Append("\n</head>\n<body").Append(bodyAttributes).Append(">\n");
        sb.Append(safe.Body);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The page: the root of multipart/related ("start" parameter or the first part), else the first HTML part.</summary>
    private static MimePart? Page(MimeMessage message) =>
        message.Body is MultipartRelated { Root: MimePart root } && IsHtml(root)
            ? root
            : message.BodyParts.OfType<MimePart>().FirstOrDefault(IsHtml);

    private static bool IsHtml(MimePart part) =>
        part.ContentType.IsMimeType("text", "html") || part.ContentType.IsMimeType("application", "xhtml+xml");

    /// <summary>
    /// A text part decoded with its charset parameter; without one (or a wrong "us-ascii" over 8-bit text) the
    /// <paramref name="fallback"/> looks at the BOM, the page's own declaration and UTF-8.
    /// </summary>
    private static string Text(MimePart part, Func<byte[], string> fallback)
    {
        using var data = new MemoryStream();
        part.Content?.DecodeTo(data);
        var bytes = data.ToArray();
        var charset = part.ContentType.Charset?.Trim();
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..];
        var ascii = charset is not null && (charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) || charset.Equals("ascii", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(charset) && !bom && !(ascii && bytes.Any(b => b >= 0x80)))
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (ArgumentException)
            {
                // Unknown charset name: detect below.
            }
        }
        return fallback(bytes);
    }

    /// <summary>The URLs of a srcset ("a.png 1x, b.png 2x"), largest first as the last is usually the sharpest.</summary>
    private static IEnumerable<string> SourceSet(string? srcset) =>
        string.IsNullOrWhiteSpace(srcset)
            ? []
            : srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(candidate => candidate.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
                .Reverse();

    /// <summary>
    /// <paramref name="reference"/> as an absolute URI: as written when it has a scheme, else against
    /// <paramref name="baseUri"/>. Only ever compared with Content-Location values, never opened.
    /// </summary>
    private static Uri? Absolute(string? reference, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        reference = reference.Trim();
        try
        {
            // "/images/a.png" is an absolute file path to Uri on Linux and macOS; on a web page it is relative.
            if (UriScheme().IsMatch(reference))
                return Uri.TryCreate(reference, UriKind.Absolute, out var absolute) ? absolute : null;
            return baseUri is not null && Uri.TryCreate(baseUri, reference, out var combined) ? combined : null;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The URI without its fragment, for lookups.</summary>
    private static string Key(Uri uri)
    {
        var text = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
        var hash = text.IndexOf('#');
        return hash < 0 ? text : text[..hash];
    }

    /// <summary>The parts of one archive by Content-Location and Content-ID, and what was inlined from them so far.</summary>
    private sealed class Archive
    {
        private readonly Dictionary<string, MimePart> _byLocation = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MimePart> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<MimePart, string?> _pictures = [];
        private long _embedded;
        private long _styles;

        public Archive(MimeMessage message, MimePart page)
        {
            PageLocation = Absolute(Location(page), null);
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                if (part.ContentId is { Length: > 0 } id)
                    _byId.TryAdd(id.Trim().Trim('<', '>'), part);
                if (Location(part) is not { } location)
                    continue;
                _byLocation.TryAdd(location, part);
                if (Absolute(location, PageLocation) is { } absolute)
                    _byLocation.TryAdd(Key(absolute), part);
            }
        }

        /// <summary>Where the page was saved from (its Content-Location), the base of its relative references.</summary>
        public Uri? PageLocation { get; }

        /// <summary>The Content-Location header as written, unfolded (URLs hold no white space).</summary>
        private static string? Location(MimePart part) =>
            part.Headers[HeaderId.ContentLocation] is { } value && Whitespace().Replace(value, "") is { Length: > 0 } location
                ? location
                : null;

        /// <summary>The part a reference names: its Content-Location as written or resolved, or "cid:" its Content-ID.</summary>
        private MimePart? Find(string? reference, Uri? baseUri)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return null;
            reference = reference.Trim();
            if (_byLocation.TryGetValue(reference, out var part))
                return part;
            if (reference.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                return _byId.GetValueOrDefault(Uri.UnescapeDataString(reference[4..]).Trim('<', '>'));
            return Absolute(reference, baseUri) is { } absolute ? _byLocation.GetValueOrDefault(Key(absolute)) : null;
        }

        /// <summary>
        /// A data URI of the raster picture a reference names, or null when the archive does not hold one (remote,
        /// SVG, too large, or the page's picture budget is spent). Data URIs already in the page are kept when raster.
        /// </summary>
        public string? Picture(string? reference, Uri? baseUri)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return null;
            reference = reference.Trim();
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return EmlConverter.SafeInlineImage(reference) ? Spend(reference) : null;
            if (Find(reference, baseUri) is not { } part)
                return null;
            if (!_pictures.TryGetValue(part, out var uri))
                _pictures[part] = uri = DataUri(part);
            return Spend(uri);
        }

        private string? Spend(string? uri)
        {
            if (uri is null || _embedded + uri.Length > MaxEmbeddedChars)
                return null;
            _embedded += uri.Length;
            return uri;
        }

        private static string? DataUri(MimePart part)
        {
            if (part.Content is null)
                return null;
            var declared = part.ContentType.MimeType.ToLowerInvariant();
            var maybeImage = declared.StartsWith("image/", StringComparison.Ordinal) || declared is "application/octet-stream";
            // Base64 and quoted-printable only grow data, so the encoded size bounds the decoded one.
            if (!maybeImage || part.Content.Stream is { CanSeek: true } encoded && encoded.Length > MaxPictureBytes * 2L)
                return null;
            using var data = new MemoryStream();
            part.Content.DecodeTo(data);
            if (data.Length is 0 or > MaxPictureBytes)
                return null;
            var bytes = data.ToArray();
            var type = SniffPicture(bytes) ?? (declared.StartsWith("image/", StringComparison.Ordinal) && !declared.Contains("svg", StringComparison.Ordinal) ? declared : null);
            return type is null ? null : $"data:{type};base64,{Convert.ToBase64String(bytes)}";
        }

        /// <summary>The style sheet a reference names, its url()s rewritten (see <see cref="Css"/>), or null.</summary>
        public string? StyleSheet(string? reference, Uri? baseUri, int depth)
        {
            if (depth > MaxImportDepth || Find(reference, baseUri) is not { } part || !part.ContentType.IsMimeType("text", "css"))
                return null;
            // Chrome names style sheets "cid:…", which is no base: their url()s are absolute, else read like the page's.
            var location = Absolute(Location(part), PageLocation);
            var sheetBase = location?.Scheme is "http" or "https" or "file" or "ftp" ? location : baseUri;
            return Css(Text(part, HtmlReader.Decode), sheetBase, depth);
        }

        /// <summary>
        /// CSS with @import replaced by the imported sheet when the archive holds it, url() of archived raster pictures
        /// as data URIs and every other url() as "none", so a browser loads nothing. "&lt;" is escaped so the text
        /// cannot close its &lt;style&gt; element.
        /// </summary>
        public string Css(string css, Uri? baseUri, int depth)
        {
            if (css.Length == 0)
                return css;
            _styles += css.Length;
            if (_styles > MaxStyleChars)
                return "";
            // Imported sheets are rewritten on their own (against their own location) and put in after this sheet's
            // url()s are, so their data URIs aren't scanned and counted twice. NUL marks the spots; CSS has none.
            var imported = new List<string>();
            css = CssImport().Replace(css.Replace('\0', '�'), match =>
            {
                imported.Add(StyleSheet(match.Groups["url"].Value, baseUri, depth + 1) ?? "");
                return $"\0{imported.Count - 1}\0";
            });
            css = CssUrl().Replace(css, match => Picture(match.Groups["url"].Value, baseUri) is { } data ? $"url(\"{data}\")" : "none");
            css = css.Replace("<", "\\3c ", StringComparison.Ordinal);
            return imported.Count == 0 ? css : ImportMark().Replace(css, match => imported[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }
    }

    /// <summary>The picture type from the first bytes, for parts saved as application/octet-stream or mislabelled.</summary>
    private static string? SniffPicture(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x42, 0x4D, ..] => "image/bmp",
        [_, _, _, _, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66, ..] => "image/avif",
        _ => null,
    };

    [GeneratedRegex(@"@import\s+(?:url\(\s*)?(?<q>[""']?)(?<url>[^""')\s;]+)\k<q>\s*\)?[^;{}]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"url\(\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^)\s""']*))\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex("\0([0-9]+)\0")]
    private static partial Regex ImportMark();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex UriScheme();

    [GeneratedRegex("^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
