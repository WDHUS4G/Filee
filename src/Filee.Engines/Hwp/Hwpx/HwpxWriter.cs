// Writes HWPX (한글 / OWPML, KS X 6101) packages from the HDocument model — no Hancom Office needed.
//
// The package layout, styles and the reference document blank.hwpx come from pypandoc-hwpx
// (MIT, https://github.com/msjang/pypandoc-hwpx). Element order and attribute values follow files saved by
// 한글 itself where the schema and 한글 disagree (see the notes in each partial file):
//  * HwpxWriter.cs         package: mimetype first and stored, one section file per 한글 section, manifest;
//  * HwpxWriter.Header.cs  header.xml: fonts, character/paragraph shapes, numberings, borders, tab stops;
//  * HwpxWriter.Body.cs    sections (page setup, columns, headers/footers), paragraphs, runs and tables;
//  * HwpxWriter.Objects.cs pictures, text boxes, notes, hyperlinks, bookmarks and page numbers.

using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Converts an <see cref="HDocument"/> into an HWPX package.</summary>
internal sealed partial class HwpxWriter
{
    private static readonly XNamespace Hh = "http://www.hancom.co.kr/hwpml/2011/head";
    private static readonly XNamespace Hc = "http://www.hancom.co.kr/hwpml/2011/core";
    private static readonly XNamespace Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    private static readonly XNamespace Hs = "http://www.hancom.co.kr/hwpml/2011/section";

    private static readonly HashSet<string> HwpImageTypes = ["png", "jpg", "gif", "bmp"];

    private readonly List<(string Id, string SourcePath, string Extension)> _images = [];
    private readonly StringBuilder _plainText = new();
    private readonly XElement _templateSection;
    private int _nextObjectId = 1_000_000;

    private HwpxWriter(XDocument header, XDocument section)
    {
        _head = header.Root ?? throw new InvalidDataException("header.xml has no root element.");
        _templateSection = section.Root ?? throw new InvalidDataException("section0.xml has no root element.");
        InitializeHeader();
    }

    // ───────────────────────── Entry point ─────────────────────────

    /// <summary>Writes <paramref name="document"/> as an HWPX file.</summary>
    /// <param name="outputPath">HWPX file to create (replaced atomically).</param>
    /// <param name="template">Reference HWPX supplying styles and the default page; the built-in blank.hwpx when null.</param>
    public static void Write(HDocument document, string outputPath, Stream? template = null)
    {
        using var templateStream = template ?? OpenBuiltInTemplate();
        using var buffer = new MemoryStream();
        templateStream.CopyTo(buffer);
        buffer.Position = 0;
        using var reference = new ZipArchive(buffer, ZipArchiveMode.Read);

        var writer = new HwpxWriter(
            XDocument.Parse(ReadEntry(reference, "Contents/header.xml")),
            XDocument.Parse(ReadEntry(reference, "Contents/section0.xml")));

        var sections = document.Sections.Count > 0 ? document.Sections : [new HSection()];
        var sectionXml = sections.Select(writer.Section).ToList();
        writer.WritePackage(reference, outputPath, sectionXml, document.Title);
    }

    internal static Stream OpenBuiltInTemplate() =>
        typeof(HwpxWriter).Assembly.GetManifestResourceStream("Filee.Engines.Hwpx.blank.hwpx")
        ?? throw new InvalidOperationException("Embedded blank.hwpx is missing.");

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage(ZipArchive reference, string outputPath, List<string> sections, string? title)
    {
        var header = SerializeHeader(sections.Count);
        var temp = outputPath + ".tmp";
        using (var file = File.Create(temp))
        using (var output = new ZipArchive(file, ZipArchiveMode.Create))
        {
            // OCF rule: "mimetype" first and stored uncompressed.
            WriteText(output, "mimetype", ReadEntry(reference, "mimetype"), CompressionLevel.NoCompression);

            foreach (var entry in reference.Entries)
            {
                if (entry.FullName == "mimetype" || entry.FullName.EndsWith('/'))
                    continue;
                switch (entry.FullName)
                {
                    case "Contents/section0.xml":
                        for (var i = 0; i < sections.Count; i++)
                            WriteText(output, $"Contents/section{i}.xml", sections[i]);
                        break;
                    case "Contents/header.xml":
                        WriteText(output, entry.FullName, header);
                        break;
                    case "Contents/content.hpf":
                        WriteText(output, entry.FullName, BuildManifest(ReadEntry(reference, entry.FullName), sections.Count, title));
                        break;
                    case "META-INF/container.rdf":
                        WriteText(output, entry.FullName, BuildRdf(ReadEntry(reference, entry.FullName), sections.Count));
                        break;
                    case "Preview/PrvText.txt":
                        WriteText(output, entry.FullName, PreviewText());
                        break;
                    default:
                        using (var source = entry.Open())
                        using (var target = output.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open())
                            source.CopyTo(target);
                        break;
                }
            }

            foreach (var (id, path, extension) in _images)
            {
                var ext = HwpImageTypes.Contains(extension) ? extension : "png";
                using var target = output.CreateEntry($"BinData/{id}.{ext}", CompressionLevel.Optimal).Open();
                if (ext == extension)
                {
                    using var source = File.OpenRead(path);
                    source.CopyTo(target);
                }
                else
                {
                    // WEBP, TIFF, SVG, EMF, WMF ... → PNG, which 한글 always displays.
                    using var image = new MagickImage(path);
                    image.Write(target, MagickFormat.Png);
                }
            }
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    /// <summary>Opening tag of a section part, declaring the same namespaces as header.xml (as 한글 does).</summary>
    private string SectionOpenTag()
    {
        var namespaces = _head.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
        if (!namespaces.Any(a => a.Value == Hs.NamespaceName))
            namespaces.Add(new XAttribute(XNamespace.Xmlns + "hs", Hs.NamespaceName));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><hs:sec " + string.Join(' ', namespaces.Select(a => $"{a.Name.LocalName switch { "xmlns" => "xmlns", var n => "xmlns:" + n }}=\"{a.Value}\"")) + ">";
    }

    private string BuildManifest(string hpf, int sectionCount, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var start = hpf.IndexOf("<opf:title>", StringComparison.Ordinal);
            var end = hpf.IndexOf("</opf:title>", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                hpf = hpf[..(start + "<opf:title>".Length)] + Escape(title) + hpf[end..];
            else
                hpf = hpf.Replace("<opf:title/>", $"<opf:title>{Escape(title)}</opf:title>", StringComparison.Ordinal);
        }

        var items = new StringBuilder();
        var spine = new StringBuilder();
        for (var i = 1; i < sectionCount; i++)
        {
            items.Append($"<opf:item id=\"section{i}\" href=\"Contents/section{i}.xml\" media-type=\"application/xml\"/>");
            spine.Append($"<opf:itemref idref=\"section{i}\" linear=\"yes\"/>");
        }
        foreach (var image in _images)
        {
            var ext = HwpImageTypes.Contains(image.Extension) ? image.Extension : "png";
            var mime = ext switch { "jpg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", _ => "image/png" };
            items.Append($"<opf:item id=\"{image.Id}\" href=\"BinData/{image.Id}.{ext}\" media-type=\"{mime}\" isEmbeded=\"1\"/>");
        }
        hpf = InsertAfter(hpf, "href=\"Contents/section0.xml\" media-type=\"application/xml\"/>", items.ToString())
              ?? InsertBefore(hpf, "</opf:manifest>", items.ToString());
        return InsertAfter(hpf, "<opf:itemref idref=\"section0\" linear=\"yes\"/>", spine.ToString())
               ?? InsertBefore(hpf, "</opf:spine>", spine.ToString());
    }

    private static string BuildRdf(string rdf, int sectionCount)
    {
        var parts = new StringBuilder();
        for (var i = 1; i < sectionCount; i++)
        {
            parts.Append($"<rdf:Description rdf:about=\"\"><ns0:hasPart xmlns:ns0=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#\" rdf:resource=\"Contents/section{i}.xml\"/></rdf:Description>");
            parts.Append($"<rdf:Description rdf:about=\"Contents/section{i}.xml\"><rdf:type rdf:resource=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#SectionFile\"/></rdf:Description>");
        }
        return InsertBefore(rdf, "</rdf:RDF>", parts.ToString());
    }

    private static string? InsertAfter(string text, string marker, string insert)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : text.Insert(index + marker.Length, insert);
    }

    private static string InsertBefore(string text, string marker, string insert)
    {
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? text : text.Insert(index, insert);
    }

    private string PreviewText()
    {
        var text = _plainText.ToString().Trim();
        return text.Length > 1000 ? text[..1000] : text;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private int NextId() => _nextObjectId++;

    private static int MaxId(IEnumerable<XElement> elements) =>
        elements.Select(e => int.TryParse((string?)e.Attribute("id"), out var id) ? id : 0).DefaultIfEmpty(0).Max();

    /// <summary>XML-escapes text and removes characters XML 1.0 does not allow (e.g. vertical tab from Word).</summary>
    internal static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF))
                clean.Append(ch);
        }
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Reference HWPX has no {name}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteText(ZipArchive archive, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
