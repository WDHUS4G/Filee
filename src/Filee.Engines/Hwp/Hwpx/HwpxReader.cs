// Reads HWPX (한글 / OWPML, KS X 6101) packages into the HDocument model — the inverse of HwpxWriter — so HWP and
// HWPX can become DOCX, HTML or EPUB without Hancom Office or LibreOffice (HWP is turned into HWPX by rhwp first).
//
// Files come from 한글 itself, from rhwp's export-hwpx and from Filee's own writer, which differ in details. The
// reader therefore matches elements by local name (any prefix, any namespace version), resolves hp:switch blocks and
// looks every idRef up in header.xml. Split over partial files:
//  * HwpxReader.cs         package: container, manifest and spine, pictures (BinData), section setup, XML helpers;
//  * HwpxReader.Header.cs  header.xml: fonts, character and paragraph shapes, numberings, bullets, borders, tab stops;
//  * HwpxReader.Body.cs    paragraphs, runs, text, fields (hyperlinks), notes, bookmarks and tables;
//  * HwpxReader.Objects.cs pictures, drawing objects, text boxes, groups and equations.

using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Reads an .hwpx file into the HWPX document model.</summary>
internal sealed partial class HwpxReader
{
    /// <summary>hp:case branches written for readers that know these namespaces hold the exact values.</summary>
    private const string HwpUnitCharNamespace = "HwpUnitChar";
    private const string Paragraph2016Namespace = "http://www.hancom.co.kr/hwpml/2016/paragraph";

    private readonly ZipArchive _zip;
    private readonly string _mediaFolder;

    /// <summary>Manifest item id → zip entry of embedded binaries (pictures).</summary>
    private readonly Dictionary<string, string> _binaryItems = new(StringComparer.Ordinal);

    /// <summary>Binary item id → extracted file (null when the item is missing).</summary>
    private readonly Dictionary<string, string?> _media = new(StringComparer.Ordinal);

    /// <summary>Elements the model cannot hold, by local name, with the number of times they were skipped.</summary>
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

    private HwpxReader(ZipArchive zip, string mediaFolder)
    {
        _zip = zip;
        _mediaFolder = mediaFolder;
    }

    // ───────────────────────── Entry points ─────────────────────────

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, null);

    /// <param name="skipped">Receives the elements that were skipped because the model has no equivalent (local name → count).</param>
    public static HDocument Read(string path, string mediaFolder, IDictionary<string, int>? skipped)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw NotAPackage(path);
        }

        using (zip)
        {
            var reader = new HwpxReader(zip, mediaFolder);
            var document = reader.ReadDocument();
            if (skipped is not null)
            {
                foreach (var (name, count) in reader._skipped)
                    skipped[name] = count;
            }
            return document;
        }
    }

    /// <summary>A clear message for files that are not a zip package: HWP 5.0 binaries and DRM-wrapped files.</summary>
    private static InvalidDataException NotAPackage(string path)
    {
        var header = new byte[8];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(header);
        return header is [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]
            ? new InvalidDataException("The file is an HWP 5.0 (binary) document, not HWPX. Rename it to .hwp and convert it again.")
            : new InvalidDataException("The file is not an HWPX package. It may be damaged or protected by document security (DRM) software.");
    }

    // ───────────────────────── Package ─────────────────────────

    private HDocument ReadDocument()
    {
        RejectEncrypted();

        var packagePath = RootFile();
        var package = LoadXml(packagePath, required: false);
        var folder = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";

        // Manifest items: id → zip path. Hrefs are relative to the package root in files from 한글, but resolve them
        // against the manifest's folder too for other producers.
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Descendants(package?.Root, "item"))
        {
            if (Attr(item, "id") is not { Length: > 0 } id || Attr(item, "href") is not { Length: > 0 } href)
                continue;
            var entry = EntryPath(href, folder);
            if (entry is null)
                continue;
            items[id] = entry;
            if (entry.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) && Attr(item, "isEmbeded") is not "0")
                _binaryItems[id] = entry;
        }

        var header = LoadXml(items.Values.FirstOrDefault(p => p.EndsWith("header.xml", StringComparison.OrdinalIgnoreCase)) ?? "Contents/header.xml", required: true)!;
        LoadHeader(header.Root!);

        var document = new HDocument
        {
            Title = Descendants(package?.Root, "title").FirstOrDefault()?.Value is { } title && !string.IsNullOrWhiteSpace(title) ? title.Trim() : null,
        };
        foreach (var sectionPath in SectionPaths(package, items))
        {
            var root = LoadXml(sectionPath, required: true)!.Root!;
            ReadSection(root, document);
        }
        if (document.Sections.Count == 0)
            throw new InvalidDataException("The HWPX file has no sections (Contents/section0.xml is missing).");
        return document;
    }

    /// <summary>Password-protected HWPX files keep their parts encrypted, which is declared in META-INF/manifest.xml.</summary>
    private void RejectEncrypted()
    {
        var manifest = LoadXml("META-INF/manifest.xml", required: false);
        if (Descendants(manifest?.Root, "encryption-data").Any())
            throw new InvalidDataException("The HWPX file is password-protected. Open it in 한글 (Hangul), remove the password, save it and convert it again.");
    }

    /// <summary>The package document (content.hpf) named in META-INF/container.xml.</summary>
    private string RootFile()
    {
        var container = LoadXml("META-INF/container.xml", required: false);
        var rootFiles = Descendants(container?.Root, "rootfile").ToList();
        var hpf = rootFiles.FirstOrDefault(r => Attr(r, "media-type") == "application/hwpml-package+xml")
                  ?? rootFiles.FirstOrDefault(r => Attr(r, "full-path")?.EndsWith(".hpf", StringComparison.OrdinalIgnoreCase) == true);
        return Attr(hpf, "full-path") ?? "Contents/content.hpf";
    }

    /// <summary>Section parts in reading order: the spine, else Contents/sectionN.xml by number.</summary>
    private List<string> SectionPaths(XDocument? package, Dictionary<string, string> items)
    {
        static bool IsSection(string path) =>
            Path.GetFileName(path) is var name && name.StartsWith("section", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var itemRef in Descendants(package?.Root, "itemref"))
        {
            if (items.TryGetValue(Attr(itemRef, "idref") ?? "", out var path) && IsSection(path) && !result.Contains(path))
                result.Add(path);
        }
        if (result.Count > 0)
            return result;

        return _zip.Entries
            .Where(e => e.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) && IsSection(e.FullName))
            .OrderBy(e => int.TryParse(Path.GetFileNameWithoutExtension(e.Name).AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .Select(e => e.FullName)
            .ToList();
    }

    private string? EntryPath(string href, string folder)
    {
        var path = Uri.UnescapeDataString(href).Replace('\\', '/').TrimStart('/');
        if (_zip.GetEntry(path) is not null)
            return path;
        return folder.Length > 0 && _zip.GetEntry(folder + path) is not null ? folder + path : path;
    }

    private XDocument? LoadXml(string path, bool required)
    {
        var entry = _zip.GetEntry(path);
        if (entry is null)
        {
            return required
                ? throw new InvalidDataException($"The HWPX file is incomplete: {path} is missing.")
                : null;
        }
        try
        {
            using var stream = entry.Open();
            // Whitespace matters: <hp:t> </hp:t> is a real space.
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            // Encrypted (distribution / DRM) documents have binary parts where XML is expected.
            throw new InvalidDataException($"The HWPX file cannot be read ({path} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>Extracts a BinData item (by manifest id) into the media folder once; null when it is missing.</summary>
    private string? ExtractBinary(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return null;
        if (_media.TryGetValue(itemId, out var cached))
            return cached;

        // Without a manifest entry, 한글's naming (BinData/<id>.<ext>) still finds the file.
        var entryPath = _binaryItems.GetValueOrDefault(itemId)
                        ?? _zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) &&
                                                            string.Equals(Path.GetFileNameWithoutExtension(e.Name), itemId, StringComparison.OrdinalIgnoreCase))?.FullName;
        string? path = null;
        if (entryPath is not null && _zip.GetEntry(entryPath) is { } entry)
        {
            Directory.CreateDirectory(_mediaFolder);
            path = Path.Combine(_mediaFolder, $"hwpx-media-{_media.Count + 1}{Path.GetExtension(entry.Name).ToLowerInvariant()}");
            using var source = entry.Open();
            using var target = File.Create(path);
            source.CopyTo(target);
        }
        _media[itemId] = path;
        return path;
    }

    // ───────────────────────── Sections ─────────────────────────

    /// <summary>
    /// State of the section being read. A secPr starts a 한글 section (구역); its first colPr gives the columns and
    /// later ones change them mid-page. Header, footer and page number controls may sit in any body paragraph.
    /// </summary>
    private sealed class SectionState(HDocument document)
    {
        public HSection? Section;
        public bool ColumnsSet;
        public bool StartNumberSet;
        public XElement? PageNumber;

        public HSection Current => Section ?? Begin(null);

        public HSection Begin(HSection? section)
        {
            Finish();
            Section = section ?? new HSection();
            ColumnsSet = false;
            StartNumberSet = Section.StartPageNumber is not null;
            PageNumber = null;
            document.Sections.Add(Section);
            return Section;
        }

        /// <summary>Turns an automatic page number (hp:pageNum, "쪽 번호 매기기") into a header or footer paragraph.</summary>
        public void Finish()
        {
            if (Section is null || PageNumber is not { } control)
                return;
            PageNumber = null;
            var position = Attr(control, "pos") ?? "BOTTOM_CENTER";
            if (position == "NONE")
                return;

            var side = Attr(control, "sideChar") is { Length: > 0 } s && s != "\0" ? s : null;
            var paragraph = new HParagraph
            {
                Format = new HParaFormat(Align: position switch
                {
                    _ when position.EndsWith("LEFT", StringComparison.Ordinal) || position.StartsWith("INSIDE", StringComparison.Ordinal) => HAlign.Left,
                    _ when position.EndsWith("RIGHT", StringComparison.Ordinal) || position.StartsWith("OUTSIDE", StringComparison.Ordinal) => HAlign.Right,
                    _ => HAlign.Center,
                }),
            };
            if (side is not null)
                paragraph.Inlines.Add(new HText(side + " ", default));
            paragraph.Inlines.Add(new HField(HFieldKind.PageNumber, default));
            if (side is not null)
                paragraph.Inlines.Add(new HText(" " + side, default));

            var top = position.Contains("TOP", StringComparison.Ordinal);
            var target = top ? Section.Headers : Section.Footers;
            if (target.Count == 0)
                target.Add(new HHeaderFooter(HPageType.Both));
            foreach (var item in target)
                item.Blocks.Add(paragraph);
        }
    }

    private void ReadSection(XElement root, HDocument document)
    {
        var state = new SectionState(document);
        foreach (var element in Elements(root))
        {
            if (element.Name.LocalName == "p")
                ReadTopParagraph(element, state);
            else
                Skip(element);
        }
        if (state.Section is null)
            state.Begin(null); // a section part without paragraphs is still a section
        state.Finish();
    }

    /// <summary>A body paragraph: a secPr in it starts a new section (normally only in a part's first paragraph).</summary>
    private void ReadTopParagraph(XElement p, SectionState state)
    {
        var secPr = Elements(p).Where(e => e.Name.LocalName == "run").SelectMany(Elements).FirstOrDefault(e => e.Name.LocalName == "secPr");
        if (secPr is not null)
        {
            state.Begin(Section(secPr));
            _outlineNumberingId = Attr(secPr, "outlineShapeIDRef");
        }
        ReadParagraph(p, state.Current.Blocks, state);
    }

    private static HSection Section(XElement secPr)
    {
        var pagePr = Child(secPr, "pagePr");
        var margin = Child(pagePr, "margin");
        var width = Int(pagePr, "width", 59530);
        var height = Int(pagePr, "height", 84190);
        // Width and height describe the portrait sheet; "NARROWLY" turns it (landscape).
        if (Attr(pagePr, "landscape") == "NARROWLY")
            (width, height) = (Math.Max(width, height), Math.Min(width, height));

        var visibility = Child(secPr, "visibility");
        var start = Int(Child(secPr, "startNum"), "page");
        return new HSection
        {
            Page = new HPage(width, height,
                Left: Int(margin, "left", 7200), Right: Int(margin, "right", 7200),
                Top: Int(margin, "top", 4255), Bottom: Int(margin, "bottom", 4960),
                Header: Int(margin, "header", 4250), Footer: Int(margin, "footer", 2240),
                Gutter: Int(margin, "gutter")),
            HideFirstHeader = Flag(visibility, "hideFirstHeader"),
            HideFirstFooter = Flag(visibility, "hideFirstFooter"),
            StartPageNumber = start > 0 ? start : null,
        };
    }

    private static HColumns Columns(XElement colPr)
    {
        var count = Math.Max(1, Int(colPr, "colCount", 1));
        if (count == 1)
            return HColumns.Single;
        // Columns of different widths (sameSz="0") list each width and gap; the model keeps one gap.
        var gap = Flag(colPr, "sameSz", true) ? Int(colPr, "sameGap") : Int(Child(colPr, "colSz"), "gap", Int(colPr, "sameGap"));
        var line = Child(colPr, "colLine");
        return new HColumns(count, gap, line is not null && Attr(line, "type") is not (null or "NONE"));
    }

    // ───────────────────────── XML helpers ─────────────────────────

    /// <summary>Child elements with hp:switch blocks replaced by the branch this reader understands.</summary>
    private static IEnumerable<XElement> Elements(XElement? parent)
    {
        if (parent is null)
            yield break;
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == "switch")
            {
                foreach (var inner in Elements(Branch(child).Branch))
                    yield return inner;
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The hp:case whose required namespace is known (exact HWPUNIT values, 2016 paragraph features), else hp:default.
    /// <c>Exact</c> is false for hp:default, where 한글 doubles lengths for older readers.
    /// </summary>
    private static (XElement? Branch, bool Exact) Branch(XElement switchElement)
    {
        foreach (var branch in switchElement.Elements())
        {
            if (branch.Name.LocalName != "case")
                continue;
            var required = branch.Attributes().FirstOrDefault(a => a.Name.LocalName == "required-namespace")?.Value ?? "";
            if (required.Contains(HwpUnitCharNamespace, StringComparison.Ordinal) || required == Paragraph2016Namespace)
                return (branch, true);
        }
        return (switchElement.Elements().FirstOrDefault(e => e.Name.LocalName == "default"), false);
    }

    /// <summary>The first child with this local name, looking into hp:switch blocks (a loop: it runs for every property).</summary>
    private static XElement? Child(XElement? parent, string name)
    {
        if (parent is null)
            return null;
        foreach (var child in parent.Elements())
        {
            var local = child.Name.LocalName;
            if (local == name)
                return child;
            if (local == "switch" && Child(Branch(child).Branch, name) is { } inner)
                return inner;
        }
        return null;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string name) => Elements(parent).Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Descendants(XElement? root, string name) =>
        root?.Descendants().Where(e => e.Name.LocalName == name) ?? [];

    private static string? Attr(XElement? element, string name) => (string?)element?.Attribute(name);

    /// <summary>
    /// An integer attribute. Offsets are unsigned 32-bit values in the file, so negative positions written by some
    /// producers arrive as numbers above 2³¹ and are wrapped back.
    /// </summary>
    private static int Int(XElement? element, string name, int fallback = 0)
    {
        if (!long.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                ? (int)Math.Clamp(Math.Round(real), int.MinValue, int.MaxValue)
                : fallback;
        }
        if (value is > int.MaxValue and <= uint.MaxValue)
            value -= 1L << 32;
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static bool Flag(XElement? element, string name, bool fallback = false) => Attr(element, name) switch
    {
        null => fallback,
        "1" or "true" => true,
        _ => false,
    };

    /// <summary>"#RRGGBB" in upper case; null for "none" or unknown values. 한글 may write "#AARRGGBB".</summary>
    private static string? Color(string? value)
    {
        if (value is null || !value.StartsWith('#'))
            return null;
        var hex = value.Length == 9 ? value[3..] : value[1..];
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>Counts an element the model cannot hold (see <see cref="Read(string, string, IDictionary{string, int}?)"/>).</summary>
    private void Skip(XElement element) => Skip(element.Name.LocalName);

    private void Skip(string name) => _skipped[name] = _skipped.GetValueOrDefault(name) + 1;
}
