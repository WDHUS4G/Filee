// Shared checks for HWPX files written by Filee: package structure, id references, and a render with rhwp.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

internal static class HwpxAssert
{
    public static readonly XNamespace Hh = "http://www.hancom.co.kr/hwpml/2011/head";
    public static readonly XNamespace Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    public static readonly XNamespace Hc = "http://www.hancom.co.kr/hwpml/2011/core";

    /// <summary>
    /// The package is what 한글 expects: mimetype first and stored, well-formed parts, item counts that match,
    /// every id reference resolvable, one manifest entry per section and picture.
    /// </summary>
    public static void ValidPackage(string hwpx, int? expectImages = null)
    {
        // mimetype must be the first entry and stored (compression method 0 in the local file header).
        using (var raw = File.OpenRead(hwpx))
        {
            var header = new byte[38];
            raw.ReadExactly(header);
            Assert.Equal(0, BitConverter.ToUInt16(header, 8));
            Assert.Equal("mimetype", System.Text.Encoding.ASCII.GetString(header, 30, 8));
        }

        using var zip = ZipFile.OpenRead(hwpx);
        Assert.Equal("application/hwp+zip", new StreamReader(zip.GetEntry("mimetype")!.Open()).ReadToEnd());
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".hpf") || e.FullName.EndsWith(".rdf")))
            XDocument.Load(entry.Open()); // throws if malformed

        var head = XDocument.Load(zip.GetEntry("Contents/header.xml")!.Open()).Root!;
        var sectionEntries = zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal)).ToList();
        Assert.Equal(sectionEntries.Count, (int)head.Attribute("secCnt")!);
        var sections = sectionEntries.Select(e => XDocument.Load(e.Open()).Root!).ToList();

        foreach (var (container, item) in new[] { ("charProperties", "charPr"), ("paraProperties", "paraPr"), ("numberings", "numbering"), ("borderFills", "borderFill"), ("tabProperties", "tabPr") })
        {
            var element = head.Descendants(Hh + container).Single();
            Assert.Equal(element.Elements(Hh + item).Count(), (int)element.Attribute("itemCnt")!);
        }
        foreach (var fontface in head.Descendants(Hh + "fontface"))
        {
            var count = fontface.Elements(Hh + "font").Count();
            Assert.Equal(count, (int)fontface.Attribute("fontCnt")!);
            var language = ((string)fontface.Attribute("lang")!).ToLowerInvariant();
            Assert.All(head.Descendants(Hh + "fontRef"), r => Assert.InRange((int)r.Attribute(language)!, 0, count - 1));
        }

        HashSet<string> Ids(string name) => head.Descendants(Hh + name).Select(e => (string)e.Attribute("id")!).ToHashSet();
        var charIds = Ids("charPr");
        var paraIds = Ids("paraPr");
        var numberingIds = Ids("numbering");
        var borderFillIds = Ids("borderFill");
        var tabIds = Ids("tabPr");
        foreach (var section in sections)
        {
            Assert.All(section.Descendants(Hp + "run"), r => Assert.Contains((string)r.Attribute("charPrIDRef")!, charIds));
            Assert.All(section.Descendants(Hp + "p"), p => Assert.Contains((string)p.Attribute("paraPrIDRef")!, paraIds));
            Assert.All(section.Descendants().Where(e => e.Name.LocalName is "tbl" or "tc"), e => Assert.Contains((string)e.Attribute("borderFillIDRef")!, borderFillIds));
            // Page setup lives in the first run of each section.
            Assert.NotNull(section.Descendants(Hp + "run").First().Element(Hp + "secPr"));
        }
        Assert.All(head.Descendants(Hh + "heading").Where(h => (string?)h.Attribute("type") == "NUMBER"),
            h => Assert.Contains((string)h.Attribute("idRef")!, numberingIds));
        Assert.All(head.Descendants(Hh + "paraPr"), p => Assert.Contains((string)p.Attribute("tabPrIDRef")!, tabIds));

        var manifest = new StreamReader(zip.GetEntry("Contents/content.hpf")!.Open()).ReadToEnd();
        Assert.All(sectionEntries, s => Assert.Contains(s.FullName, manifest));
        var images = zip.Entries.Where(e => e.FullName.StartsWith("BinData/", StringComparison.Ordinal)).ToList();
        if (expectImages is { } expected)
            Assert.Equal(expected, images.Count);
        Assert.All(images, i => Assert.Contains(i.FullName, manifest));
        var imageIds = images.Select(i => Path.GetFileNameWithoutExtension(i.Name)).ToHashSet();
        foreach (var section in sections)
            Assert.All(section.Descendants(Hc + "img"), img => Assert.Contains((string)img.Attribute("binaryItemIDRef")!, imageIds));
    }

    public static XElement Xml(string hwpx, string entry = "Contents/section0.xml")
    {
        using var zip = ZipFile.OpenRead(hwpx);
        return XDocument.Load(zip.GetEntry(entry)!.Open(), LoadOptions.PreserveWhitespace).Root!;
    }

    /// <summary>
    /// The text of an HWPX as an independent reader sees it: Unhwp where its native library exists, else (Linux ARM64)
    /// the text of the section XML.
    /// </summary>
    public static string ReadBackText(string hwpx)
    {
        if (new Filee.Engines.Hwp.UnhwpConverter().GetStatus().IsAvailable)
        {
            using var document = Unhwp.UnhwpDocument.ParseFile(hwpx);
            return document.ToText();
        }
        using var zip = ZipFile.OpenRead(hwpx);
        return string.Join("\n", zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .Select(e => XDocument.Load(e.Open()).Root!.Value));
    }

    /// <summary>The header element with the given id.</summary>
    public static XElement HeadItem(string hwpx, string name, string id) =>
        Xml(hwpx, "Contents/header.xml").Descendants(Hh + name).Single(e => (string?)e.Attribute("id") == id);

    /// <summary>The paragraph containing <paramref name="text"/>.</summary>
    public static XElement ParagraphWith(XElement root, string text) =>
        root.Descendants(Hp + "p").Last(p => p.Elements(Hp + "run").Elements(Hp + "t").Any(t => t.Value.Contains(text, StringComparison.Ordinal)));

    /// <summary>The run whose own text contains <paramref name="text"/>.</summary>
    public static XElement RunWith(XElement root, string text) =>
        root.Descendants(Hp + "run").First(r => r.Elements(Hp + "t").Any(t => t.Value.Contains(text, StringComparison.Ordinal)));

    /// <summary>Renders with rhwp (independent HWPX engine) when available and keeps a PNG of page 1 for review.</summary>
    /// <returns>Number of pages, or null when rhwp is not available.</returns>
    public static int? RenderWithRhwp(EngineFixture fx, string hwpx, string name)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        // The PNG preview uses PDFium, which the analyzer only knows for desktop/mobile platforms.
        if (!fx.Catalog.StatusOf(rhwp).IsAvailable || !OperatingSystem.IsWindows())
            return null;
        var pdf = Path.Combine(Path.GetDirectoryName(hwpx)!, name + ".pdf");
        var written = rhwp.ConvertAsync(new ConversionStep(hwpx, "hwpx", "pdf", new Preset(), new FixedPath(pdf), Path.GetDirectoryName(hwpx)!),
            null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(new FileInfo(written.Single()).Length > 1000);

        var shots = Path.Combine(AppContext.BaseDirectory, "hwpx-renders");
        Directory.CreateDirectory(shots);
        var bytes = File.ReadAllBytes(pdf);
        using var page = PDFtoImage.Conversion.ToImage(bytes, 0, null, new PDFtoImage.RenderOptions(Dpi: 72));
        using var data = page.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(shots, name + ".png"), data.ToArray());
        return PDFtoImage.Conversion.GetPageCount(bytes);
    }

    /// <summary>
    /// Opens the file with LibreOffice + H2Orestart, a third independent HWPX reader (Java), when installed.
    /// It is strict where 한글 is strict too, e.g. about element prefixes and negative object offsets.
    /// </summary>
    public static async Task ReadableByH2OrestartAsync(EngineFixture fx, string hwpx)
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        if (!fx.Catalog.StatusOf(libreOffice).IsAvailable || !libreOffice.Edges.Any(e => e.From == "hwpx"))
            return;
        var odt = Path.ChangeExtension(hwpx, ".h2o.odt");
        // Tests run in parallel; the job queue normally limits LibreOffice to its worker profiles.
        await LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var written = await libreOffice.ConvertAsync(new ConversionStep(hwpx, "hwpx", "odt", new Preset(), new FixedPath(odt), Path.GetDirectoryName(hwpx)!),
                null, TestContext.Current.CancellationToken);
            Assert.True(new FileInfo(written.Single()).Length > 1000);
        }
        finally
        {
            LibreOfficeGate.Release();
        }
    }

    internal static readonly SemaphoreSlim LibreOfficeGate = new(1, 1);

    internal sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
