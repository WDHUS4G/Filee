// Book → EPUB 3 with an EPUB 2 NCX for older readers: "mimetype" first and stored, container.xml, an OPF with Dublin
// Core metadata, a navigation document built from the headings, one XHTML file per chapter (split at level-1
// headings and page breaks by XhtmlWriter), one style sheet and the pictures. Pictures in formats reading systems
// do not all show (BMP, TIFF, WEBP, ...) are converted to PNG or JPEG. Fixed-layout books (comics) have one page
// per picture, sized like the picture.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Ebooks;

internal static class EpubWriter
{
    /// <summary>Writes a reflowable EPUB.</summary>
    /// <param name="sourcePath">The file being converted, for a title when the book has none.</param>
    public static void Write(Book book, string outputPath, string sourcePath, string workFolder)
    {
        var title = book.DisplayTitle(sourcePath);
        var language = book.DisplayLanguage();
        var package = new EpubPackage(title, language, book.Metadata);
        var imageFolder = EbookFiles.NewFolder(workFolder, "epub-images");

        var chapters = XhtmlWriter.Write(book.Document, new XhtmlOptions
        {
            SplitChapters = true,
            ChapterFile = i => $"ch{i + 1:000}.xhtml",
            ImageSource = path => package.AddImage(path, imageFolder),
            Epub = true,
        });

        // A cover page for covers the content does not show already (MOBI and FB2 keep the cover apart).
        var cover = book.Metadata.CoverImage is { } coverFile && File.Exists(coverFile) ? package.AddImage(coverFile, imageFolder, cover: true) : null;
        if (cover is not null && !HDocumentWalker.Images(book.Document).Any(i => string.Equals(i.Path, book.Metadata.CoverImage, StringComparison.OrdinalIgnoreCase)))
        {
            var body = $"<div style=\"text-align:center\"><img src=\"{XhtmlWriter.Escape(cover)}\" alt=\"{XhtmlWriter.Escape(title)}\" style=\"max-height:100%\"/></div>\n";
            package.AddPage("cover.xhtml", XhtmlWriter.Page(title, body, language, "style.css", epub: true));
        }

        foreach (var chapter in chapters)
            package.AddPage(chapter.FileName, XhtmlWriter.Page(chapter.Title ?? title, chapter.Body, language, "style.css", epub: true));
        package.Toc.AddRange(TableOfContents(chapters, title));
        package.Save(outputPath, XhtmlWriter.Css, fixedLayout: false);
    }

    /// <summary>Writes a fixed-layout EPUB with one page per picture (comics).</summary>
    /// <param name="pages">Picture files in reading order.</param>
    public static void WriteFixedLayout(string title, IReadOnlyList<string> pages, string outputPath, string workFolder, CancellationToken cancellationToken)
    {
        var package = new EpubPackage(title, "en", new BookMetadata());
        var imageFolder = EbookFiles.NewFolder(workFolder, "epub-images");
        var count = 0;
        foreach (var picture in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.AddImage(picture, imageFolder, cover: count == 0) is not { } source)
                continue; // unreadable picture: leave the page out
            var info = new MagickImageInfo(picture);
            var page = $"p{++count:0000}.xhtml";
            var head = $"<meta name=\"viewport\" content=\"width={info.Width}, height={info.Height}\"/>\n";
            var body = $"<div class=\"page\"><img src=\"{XhtmlWriter.Escape(source)}\" alt=\"\" width=\"{info.Width}\" height=\"{info.Height}\"/></div>\n";
            package.AddPage(page, XhtmlWriter.Page(title, body, "en", "style.css", epub: true, head));
        }
        if (count == 0)
            throw new InvalidDataException("The comic contains no readable pictures.");
        package.Save(outputPath, "body{margin:0;padding:0;}\n.page{margin:0;padding:0;}\nimg{display:block;margin:0;padding:0;}\n", fixedLayout: true);
    }

    /// <summary>
    /// Entries for the navigation document: the two highest heading levels used in the book; chapters without
    /// headings appear by their first words when no chapter has a heading.
    /// </summary>
    private static List<XhtmlHeading> TableOfContents(IReadOnlyList<XhtmlChapter> chapters, string title)
    {
        var headings = chapters.SelectMany(c => c.Headings).Where(h => h.Text.Length > 0).ToList();
        if (headings.Count > 0)
        {
            var top = headings.Min(h => h.Level);
            return headings.Where(h => h.Level <= top + 1).Select(h => h with { Level = h.Level - top + 1 }).ToList();
        }
        return chapters.Select((c, i) => new XhtmlHeading(1, i == 0 ? title : c.PlainStart.Length > 0 ? c.PlainStart + "…" : $"{i + 1}", c.FileName)).ToList();
    }
}

/// <summary>Collects the files of an EPUB and writes the package.</summary>
internal sealed class EpubPackage(string title, string language, BookMetadata metadata)
{
    private readonly List<(string Href, string Content)> _pages = [];
    private readonly List<(string Href, string Source, string MediaType, bool Cover)> _images = [];
    private readonly Dictionary<string, string> _imageBySource = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Navigation entries; level 1 is the top.</summary>
    public List<XhtmlHeading> Toc { get; } = [];

    public void AddPage(string href, string content) => _pages.Add((href, content));

    /// <summary>
    /// Adds a picture and returns its path in the package, or null when it cannot be read. JPEG, PNG, GIF and SVG
    /// are kept; other formats become PNG (JPEG for opaque photos, which would grow a lot as PNG).
    /// </summary>
    public string? AddImage(string path, string convertFolder, bool cover = false)
    {
        if (_imageBySource.TryGetValue(path, out var known))
        {
            if (cover)
                MarkCover(known);
            return known;
        }
        if (EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is not ({ } file, { } mediaType))
            return null;
        var extension = mediaType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            _ => "svg",
        };
        var href = $"images/img{_images.Count + 1:0000}.{extension}";
        _images.Add((href, file, mediaType!, cover));
        _imageBySource[path] = href;
        return href;
    }

    private void MarkCover(string href)
    {
        var index = _images.FindIndex(i => i.Href == href);
        if (index >= 0)
            _images[index] = _images[index] with { Cover = true };
    }

    public void Save(string outputPath, string css, bool fixedLayout)
    {
        var identifier = $"urn:uuid:{Guid.NewGuid()}";
        var temp = outputPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // OCF: "mimetype" first, stored, without extra fields.
            Text(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Text(zip, "META-INF/container.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles>\n</container>\n");
            Text(zip, "OEBPS/content.opf", Opf(identifier, fixedLayout));
            Text(zip, "OEBPS/nav.xhtml", Nav());
            Text(zip, "OEBPS/toc.ncx", Ncx(identifier));
            Text(zip, "OEBPS/style.css", css);
            foreach (var (href, content) in _pages)
                Text(zip, "OEBPS/" + href, content);
            foreach (var (href, source, _, _) in _images)
            {
                // Pictures are compressed already.
                using var target = zip.CreateEntry("OEBPS/" + href, CompressionLevel.NoCompression).Open();
                using var input = File.OpenRead(source);
                input.CopyTo(target);
            }
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    private string Opf(string identifier, bool fixedLayout)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\" xml:lang=\"{E(language)}\"");
        if (fixedLayout)
            sb.Append(" prefix=\"rendition: http://www.idpf.org/vocab/rendition/#\"");
        sb.Append(">\n<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append($"<dc:identifier id=\"book-id\">{identifier}</dc:identifier>\n");
        sb.Append($"<dc:title>{E(title)}</dc:title>\n");
        sb.Append($"<dc:language>{E(language)}</dc:language>\n");
        foreach (var author in metadata.Authors)
            sb.Append($"<dc:creator>{E(author)}</dc:creator>\n");
        if (metadata.Publisher is { } publisher)
            sb.Append($"<dc:publisher>{E(publisher)}</dc:publisher>\n");
        if (metadata.Description is { } description)
            sb.Append($"<dc:description>{E(description)}</dc:description>\n");
        sb.Append($"<meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>\n");
        if (_images.FindIndex(i => i.Cover) is var cover and >= 0)
            sb.Append($"<meta name=\"cover\" content=\"img{cover + 1}\"/>\n"); // EPUB 2 readers (and Kindle converters)
        if (fixedLayout)
            sb.Append("<meta property=\"rendition:layout\">pre-paginated</meta>\n<meta property=\"rendition:spread\">auto</meta>\n");
        sb.Append("</metadata>\n<manifest>\n");
        sb.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
        sb.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n");
        sb.Append("<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
        for (var i = 0; i < _pages.Count; i++)
            sb.Append($"<item id=\"page{i + 1}\" href=\"{E(_pages[i].Href)}\" media-type=\"application/xhtml+xml\"/>\n");
        for (var i = 0; i < _images.Count; i++)
            sb.Append($"<item id=\"img{i + 1}\" href=\"{E(_images[i].Href)}\" media-type=\"{_images[i].MediaType}\"{(_images[i].Cover ? " properties=\"cover-image\"" : "")}/>\n");
        sb.Append("</manifest>\n<spine toc=\"ncx\">\n");
        for (var i = 0; i < _pages.Count; i++)
            sb.Append($"<itemref idref=\"page{i + 1}\"/>\n");
        sb.Append("</spine>\n</package>\n");
        return sb.ToString();
    }

    /// <summary>The EPUB 3 navigation document: a nested list of the table of contents.</summary>
    private string Nav()
    {
        var sb = new StringBuilder();
        sb.Append("<nav epub:type=\"toc\" id=\"toc\">\n<h1>").Append(E(ContentsLabel())).Append("</h1>\n");
        var entries = Entries();
        var depth = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var level = Math.Min(entries[i].Level, depth + 1);
            if (level > depth)
                sb.Append("<ol>\n");
            else
                sb.Append("</li>\n");
            for (; depth > level; depth--)
                sb.Append("</ol>\n</li>\n");
            depth = level;
            sb.Append($"<li><a href=\"{E(entries[i].Target)}\">{E(entries[i].Text)}</a>");
        }
        for (; depth > 0; depth--)
            sb.Append("</li>\n</ol>\n");
        sb.Append("</nav>\n");
        return XhtmlWriter.Page(title, sb.ToString(), language, "style.css", epub: true);
    }

    private string Ncx(string identifier)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n<head>\n");
        sb.Append($"<meta name=\"dtb:uid\" content=\"{identifier}\"/>\n<meta name=\"dtb:depth\" content=\"{Math.Max(1, Entries().Max(e => e.Level))}\"/>\n");
        sb.Append("<meta name=\"dtb:totalPageCount\" content=\"0\"/>\n<meta name=\"dtb:maxPageNumber\" content=\"0\"/>\n</head>\n");
        sb.Append($"<docTitle><text>{E(title)}</text></docTitle>\n<navMap>\n");
        var entries = Entries();
        var depth = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var level = Math.Min(entries[i].Level, depth + 1);
            for (; depth >= level; depth--)
                sb.Append("</navPoint>\n");
            depth = level;
            sb.Append($"<navPoint id=\"nav{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{E(entries[i].Text)}</text></navLabel><content src=\"{E(entries[i].Target)}\"/>\n");
        }
        for (; depth > 0; depth--)
            sb.Append("</navPoint>\n");
        sb.Append("</navMap>\n</ncx>\n");
        return sb.ToString();
    }

    /// <summary>Navigation entries; at least one (the first page), as EPUB requires a non-empty table of contents.</summary>
    private List<XhtmlHeading> Entries() =>
        Toc.Count > 0 ? Toc : [new XhtmlHeading(1, title, _pages.Count > 0 ? _pages[0].Href : "nav.xhtml")];

    private string ContentsLabel() => language.Split('-')[0].ToLowerInvariant() switch
    {
        "ko" => "목차",
        "zh" => "目录",
        "ja" => "目次",
        _ => "Contents",
    };

    private static string E(string text) => XhtmlWriter.Escape(text);

    private static void Text(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
