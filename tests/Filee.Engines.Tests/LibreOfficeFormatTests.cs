// Formats only LibreOffice reads (Works / WPS, WordPerfect, AbiWord, Apple iWork, StarOffice, Publisher, CorelDRAW,
// Visio, CGM, ODG): the --convert-to filter of every edge must match the application that opens the source, and
// sample files (AbiWord, ODG written in the test) convert when LibreOffice is installed.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using ImageMagick;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public class LibreOfficeFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private IConverter LibreOffice => fx.Converters.Single(c => c.Id == "libreoffice");

    private void SkipUnlessInstalled() =>
        Assert.SkipUnless(fx.Catalog.StatusOf(LibreOffice).IsAvailable, "LibreOffice not installed");

    [Theory]
    [InlineData("wpd", "pdf", null, "pdf:writer_pdf_Export")]
    [InlineData("pages", "docx", null, "docx:MS Word 2007 XML")]
    [InlineData("abw", "odt", null, "odt")]
    [InlineData("wps", "txt", null, "txt:Text (encoded):UTF8")]
    [InlineData("numbers", "xlsx", null, "xlsx:Calc MS Excel 2007 XML")]
    [InlineData("sdc", "csv", null, "csv:Text - txt - csv (StarCalc):44,34,76")]
    [InlineData("et", "pdf", null, "pdf:calc_pdf_Export")]
    [InlineData("key", "pptx", null, "pptx:Impress MS PowerPoint 2007 XML")]
    [InlineData("dps", "odp", null, "odp")]
    [InlineData("sda", "pdf", "sdd", "pdf:impress_pdf_Export")]
    [InlineData("sda", "pdf", "sda", "pdf:draw_pdf_Export")]
    [InlineData("cdr", "pdf", null, "pdf:draw_pdf_Export")]
    [InlineData("vsd", "svg", null, "svg:draw_svg_Export")]
    [InlineData("pub", "png", null, "png:draw_png_Export")]
    [InlineData("odg", "odg", null, "odg:draw8")]
    [InlineData("cgm", "odg", null, "odg:impress8_draw")]
    [InlineData("cgm", "svg", null, "svg:impress_svg_Export")]
    [InlineData("emf", "pdf", null, "pdf:draw_pdf_Export")]
    [InlineData("wmf", "png", null, "png:draw_png_Export")]
    [InlineData("cwk", "docx", null, "docx:MS Word 2007 XML")]
    [InlineData("602", "pdf", null, "pdf:writer_pdf_Export")]
    [InlineData("psw", "odt", null, "odt")]
    [InlineData("fods", "xlsx", null, "xlsx:Calc MS Excel 2007 XML")]
    [InlineData("fodp", "pdf", null, "pdf:impress_pdf_Export")]
    [InlineData("fodg", "emf", null, "emf:draw_emf_Export")]
    [InlineData("cgm", "wmf", null, "wmf:impress_wmf_Export")]
    [InlineData("svg", "wmf", null, "wmf:draw_wmf_Export")]
    [InlineData("pdf", "svg", null, "svg:draw_svg_Export")]
    [InlineData("pdf", "odg", null, "odg:draw8")]
    [InlineData("docx", "fodt", null, "fodt:OpenDocument Text Flat XML")]
    [InlineData("odt", "ott", null, "ott:writer8_template")]
    [InlineData("ods", "ots", null, "ots:calc8_template")]
    [InlineData("xlsx", "xlt", null, "xlt:MS Excel 97 Vorlage/Template")]
    [InlineData("pptx", "otp", null, "otp:impress8_template")]
    public void Filters_follow_the_application_that_opens_the_file(string from, string to, string? extension, string expected) =>
        Assert.Equal(expected, LibreOfficeConverter.FilterFor(from, to, pdfA: false, extension));

    [Fact]
    public void Every_edge_has_a_filter_of_its_application()
    {
        string[] draw = ["pub", "cdr", "vsd", "odg", "fodg", "emf", "wmf", "svg", "pdf"];
        string[] impress = ["pptx", "ppt", "odp", "otp", "fodp", "key", "dps", "sda", "cgm"];
        string[] calc = ["xlsx", "xls", "xlt", "ods", "ots", "fods", "csv", "et", "numbers", "sdc"];
        foreach (var edge in LibreOffice.Edges)
        {
            var filter = LibreOfficeConverter.FilterFor(edge.From, edge.To, pdfA: false);
            var application = draw.Contains(edge.From) ? "draw" : impress.Contains(edge.From) ? "impress" : calc.Contains(edge.From) ? "calc" : "writer";
            if (edge.To is "pdf" or "svg" or "png" or "emf" or "wmf")
                Assert.Equal($"{edge.To}:{application}_{edge.To}_Export", filter);
            else if (edge.To is not ("odt" or "ods" or "odp"))
                Assert.Contains(':', filter);
        }
        // Built-in engines stay first: LibreOffice's edges cost more than a normal direct conversion, except
        // presentations → PDF, which it lays out like PowerPoint (Settings → Engines, on by default).
        Assert.All(LibreOffice.Edges.Where(e => !(impress.Contains(e.From) && e.To == "pdf")), e => Assert.True(e.Cost >= 25));
        foreach (var from in new[] { "dot", "wps", "wpd", "lwp", "abw", "pages", "sdw", "cwk", "mcw", "602", "psw", "fodt", "ott", "et", "numbers", "sdc", "fods", "ots", "xlt", "key", "dps", "sda", "fodp", "otp", "pub", "cdr", "cgm", "vsd", "odg", "fodg" })
            Assert.Contains(LibreOffice.Edges, e => e.From == from && e.To == "pdf");
        // Read-only formats are never targets.
        Assert.All(LibreOffice.Edges, e => Assert.True(Core.Formats.FormatRegistry.Get(e.To).Writable, e.To));
    }

    [Fact]
    public void Svg_and_pdf_open_in_draw_only_where_nothing_else_does_better()
    {
        var planner = fx.Catalog.CreatePlanner(["libreoffice", "ghostscript"]);

        // The vector engine keeps SVG → PDF and images.
        Assert.Equal("vector", Assert.Single(planner.Plan("svg", "pdf")!.Steps).Converter.Id);
        Assert.Equal("vector", Assert.Single(planner.Plan("svg", "png")!.Steps).Converter.Id);
        Assert.Equal("pdfium", Assert.Single(planner.Plan("pdf", "png")!.Steps).Converter.Id);
        foreach (var to in new[] { "emf", "wmf", "odg" })
            Assert.Equal("libreoffice", Assert.Single(planner.Plan("svg", to)!.Steps).Converter.Id);
        Assert.Equal("libreoffice", Assert.Single(planner.Plan("pdf", "svg")!.Steps).Converter.Id);
        // EPS goes through Ghostscript's PDF: Draw would only show the EPS preview picture.
        Assert.Equal(["ghostscript", "libreoffice"], planner.Plan("eps", "svg")!.Steps.Select(s => s.Converter.Id));
        Assert.DoesNotContain(LibreOffice.Edges, e => e.From is "eps" or "ps");
    }

    [Fact]
    public async Task Flat_opendocument_and_text_formats_convert()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        async Task<string> Write(string name, string text)
        {
            var path = Path.Combine(folder, name);
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            return path;
        }
        const string Office = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
                              "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                              "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" office:version=\"1.3\"";
        var fodt = await Write("letter.fodt", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Office} office:mimetype=\"application/vnd.oasis.opendocument.text\"><office:body><office:text><text:p>Hello flat text</text:p></office:text></office:body></office:document>");
        var fods = await Write("sheet.fods", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Office} office:mimetype=\"application/vnd.oasis.opendocument.spreadsheet\"><office:body><office:spreadsheet><table:table table:name=\"Data\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>Flat cell</text:p></table:table-cell><table:table-cell office:value-type=\"float\" office:value=\"42\"><text:p>42</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document>");
        var fodp = await Write("deck.fodp", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Office} office:mimetype=\"application/vnd.oasis.opendocument.presentation\"><office:body><office:presentation><draw:page draw:name=\"p1\"><draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"20cm\" svg:height=\"3cm\"><draw:text-box><text:p>Flat slide</text:p></draw:text-box></draw:frame></draw:page></office:presentation></office:body></office:document>");
        var fodg = await Write("shape.fodg", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Office} office:mimetype=\"application/vnd.oasis.opendocument.graphics\"><office:body><office:drawing><draw:page draw:name=\"p1\"><draw:rect svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"4cm\"><text:p>Flat drawing</text:p></draw:rect></draw:page></office:drawing></office:body></office:document>");
        // T602: plain text with dot commands, written by the Czech word processor.
        var t602 = await Write("memo.602", "@CT 0\n@LM 1\nHello T602 memo\n");

        Assert.Contains("Hello flat text", DocxText(await ConvertAsync(fodt, "docx")));
        using (var pdf = PdfDocument.Open(await ConvertAsync(t602, "pdf")))
            Assert.Contains("Hello T602 memo", pdf.GetPage(1).Text);
        using (var pdf = PdfDocument.Open(await ConvertAsync(fodp, "pdf")))
            Assert.Contains("Flat slide", pdf.GetPage(1).Text);
        Assert.Contains("Flat drawing", await File.ReadAllTextAsync(await ConvertAsync(fodg, "svg"), TestContext.Current.CancellationToken));

        // Templates and flat XML out: OpenDocument packages with the template media type.
        Assert.Equal("application/vnd.oasis.opendocument.text-template", Mimetype(await ConvertAsync(fodt, "ott")));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet-template", Mimetype(await ConvertAsync(fods, "ots")));
        Assert.Equal("application/vnd.oasis.opendocument.presentation-template", Mimetype(await ConvertAsync(fodp, "otp")));
        var flat = await ConvertAsync(await ConvertAsync(fodt, "odt"), "fodt");
        Assert.Contains("Hello flat text", await File.ReadAllTextAsync(flat, TestContext.Current.CancellationToken));
        // An Excel 97 template, read back by the built-in spreadsheet engine.
        var xlt = await ConvertAsync(fods, "xlt");
        Assert.Equal("xlt", Core.Formats.FormatRegistry.Detect(xlt)!.Id);
        Assert.Contains("Flat cell,42", await File.ReadAllTextAsync(await ConvertAsync(xlt, "csv"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Svg_becomes_emf_wmf_and_odg_and_a_pdf_page_becomes_svg()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var svg = Path.Combine(folder, "badge.svg");
        await File.WriteAllTextAsync(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><rect x=\"10\" y=\"10\" width=\"180\" height=\"80\" fill=\"#3a7\"/><text x=\"20\" y=\"60\" font-size=\"20\">Filee</text></svg>", TestContext.Current.CancellationToken);

        var emf = await File.ReadAllBytesAsync(await ConvertAsync(svg, "emf"), TestContext.Current.CancellationToken);
        Assert.Equal(" EMF"u8.ToArray(), emf[40..44]); // EMR_HEADER signature
        var wmf = await File.ReadAllBytesAsync(await ConvertAsync(svg, "wmf"), TestContext.Current.CancellationToken);
        Assert.True(wmf[..4].SequenceEqual(new byte[] { 0xD7, 0xCD, 0xC6, 0x9A }) || wmf[..2].SequenceEqual(new byte[] { 1, 0 }), "not a WMF");
        Assert.Equal("application/vnd.oasis.opendocument.graphics", Mimetype(await ConvertAsync(svg, "odg")));

        // Draw's SVG export draws every page of a PDF on top of each other: only the first one is converted.
        var fodt = Path.Combine(folder, "pages.fodt");
        await File.WriteAllTextAsync(fodt, """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.text">
            <office:automatic-styles><style:style style:name="P1" style:family="paragraph"><style:paragraph-properties fo:break-before="page"/></style:style></office:automatic-styles>
            <office:body><office:text><text:p>Page one</text:p><text:p text:style-name="P1">Page two</text:p></office:text></office:body></office:document>
            """, TestContext.Current.CancellationToken);
        var pdf = await ConvertAsync(fodt, "pdf");
        using (var document = PdfDocument.Open(pdf))
            Assert.Equal(2, document.NumberOfPages);
        var page = await File.ReadAllTextAsync(await ConvertAsync(pdf, "svg"), TestContext.Current.CancellationToken);
        Assert.Contains("Page one", page);
        Assert.DoesNotContain("Page two", page);
        var second = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "svg", Pdf = { PageRange = "2" } });
        Assert.Contains("Page two", await File.ReadAllTextAsync(second.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    private static string DocxText(string docx) =>
        string.Concat(DocxAssert.Paragraphs(DocxAssert.ReadBack(docx).Sections.SelectMany(s => s.Blocks)).Select(DocxAssert.TextOf));

    private static string Mimetype(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        return new StreamReader(zip.GetEntry("mimetype")!.Open()).ReadToEnd();
    }

    [Fact]
    public async Task AbiWord_to_pdf_and_docx()
    {
        SkipUnlessInstalled();
        var abw = Path.Combine(fx.NewFolder(), "note.abw");
        await File.WriteAllTextAsync(abw, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE abiword PUBLIC "-//ABISOURCE//DTD AWML 1.0 Strict//EN" "http://www.abisource.com/awml.dtd">
            <abiword template="false" xmlns="http://www.abisource.com/awml.dtd" version="3.0.4" fileformat="1.1">
            <section><p>Hello AbiWord</p><p>Second paragraph</p></section>
            </abiword>
            """, TestContext.Current.CancellationToken);

        var pdf = await ConvertAsync(abw, "pdf");
        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("Hello AbiWord", string.Concat(document.GetPages().Select(p => p.Text)));
        var docx = await ConvertAsync(abw, "docx");
        Assert.Contains("Second paragraph", string.Concat(DocxAssert.Paragraphs(DocxAssert.ReadBack(docx).Sections.SelectMany(s => s.Blocks)).Select(DocxAssert.TextOf)));
    }

    [Fact]
    public async Task Drawing_to_svg_png_and_pdf()
    {
        SkipUnlessInstalled();
        var odg = Path.Combine(fx.NewFolder(), "drawing.odg");
        using (var zip = ZipFile.Open(odg, ZipArchiveMode.Create))
        {
            void Write(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            Write("mimetype", "application/vnd.oasis.opendocument.graphics", CompressionLevel.NoCompression);
            Write("META-INF/manifest.xml",
                "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
                "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.graphics\"/>" +
                "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
            Write("content.xml",
                "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">" +
                "<office:body><office:drawing><draw:page draw:name=\"page1\"><draw:rect svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"4cm\"><text:p>Filee drawing</text:p></draw:rect>" +
                "</draw:page></office:drawing></office:body></office:document-content>");
        }

        Assert.Contains("<svg", await File.ReadAllTextAsync(await ConvertAsync(odg, "svg"), TestContext.Current.CancellationToken));
        var png = new MagickImageInfo(await ConvertAsync(odg, "png"));
        Assert.True(png.Width > 100);
        using var pdf = PdfDocument.Open(await ConvertAsync(odg, "pdf"));
        Assert.Contains("Filee drawing", pdf.GetPage(1).Text);
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }
}
