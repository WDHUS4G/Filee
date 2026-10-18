// PDF → DOCX / HWPX / TXT with the built-in PDF reader (PdfPig): paragraphs in reading order (two-column pages
// too), headings from font sizes, bold / italic / colour, links, pictures (JPEG kept as it is), repeated headers and
// footers left out, scanned pages as page pictures, and a clear error for password-protected files.
// The test PDFs use the standard 14 fonts, so no installed font is needed (only the Korean test embeds one, if any).

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Pdf;
using ImageMagick;
using static Filee.Engines.Tests.DocxAssert;

namespace Filee.Engines.Tests;

public class PdfReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string First =
        "Filee converts files on your own computer. This paragraph is long enough to wrap over several lines, so the reader " +
        "has to join the lines again into one paragraph without losing or doubling any of the words in it.";
    private const string Second =
        "The second page continues the report with more details. Headers and footers repeat on every page and are left out.";

    private async Task<ConversionJob> RunAsync(string input, string target) =>
        await fx.ConvertAsync([input], new Preset { TargetFormat = target });

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await RunAsync(input, target);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>Two A4 pages: title, headings, wrapped paragraphs, coloured and italic words, a link, two pictures, and a running header and footer.</summary>
    private static string Report(string folder)
    {
        var pdf = new PdfBuilder();
        for (var page = 1; page <= 2; page++)
        {
            pdf.NewPage();
            var regular = pdf.Font();
            var bold = pdf.Font(bold: true);
            pdf.Line("Filee Quarterly Report", 9, regular, 50, 30);
            pdf.Line($"Page {page}", 9, regular, 280, 815);
            if (page == 1)
            {
                pdf.Line("Annual Report 2026", 24, bold, 50, 90);
                pdf.Line("1. Introduction", 16, bold, 50, 135);
                var y = pdf.Paragraph(First, 11, regular, 50, 165, 495, 14);
                pdf.Line("Warning", 11, bold, 50, y + 12, (255, 0, 0));
                pdf.Line(" this line is italic", 11, pdf.Font(italic: true), 50 + pdf.Width("Warning", 11, bold), y + 12);
                pdf.Line("Visit https://example.com", 11, regular, 50, y + 40);
                pdf.Link("https://example.com/", 50, y + 30, 150, 14);
                pdf.Picture(PdfBuilder.Photo(folder, "photo.jpg", MagickFormat.Jpeg), 50, y + 60, 200, 120);
                pdf.Picture(PdfBuilder.Photo(folder, "chart.png", MagickFormat.Png, 100, 100), 300, y + 60, 100, 100);
            }
            else
            {
                pdf.Line("2. Details", 16, bold, 50, 90);
                pdf.Paragraph(Second, 11, regular, 50, 120, 495, 14);
            }
        }
        return pdf.Save(Path.Combine(folder, "report.pdf"));
    }

    [Fact]
    public async Task Pdf_to_docx_keeps_paragraphs_headings_formatting_links_and_pictures()
    {
        var docx = await ConvertAsync(Report(fx.NewFolder()), "docx");

        ValidPackage(docx);
        var model = ReadBack(docx);
        var paragraphs = Paragraphs(model.Sections.SelectMany(s => s.Blocks)).Where(p => TextOf(p).Length > 0).ToList();
        Assert.Equal(["Annual Report 2026", "1. Introduction", First, "Warning this line is italic", "Visit https://example.com", "2. Details", Second],
            paragraphs.Select(p => TextOf(p).Trim()));
        // Heading levels by size: 24 pt → 1, 16 pt → 2; body text is no heading.
        Assert.Equal([1, 2, 0, 0, 0, 2, 0], paragraphs.Select(p => p.HeadingLevel));

        var warning = paragraphs[3].Inlines.OfType<HText>().ToList();
        Assert.Equal((true, "#FF0000"), (warning[0].Format.Bold, warning[0].Format.Color));
        Assert.True(warning[^1].Format.Italic);
        Assert.Equal("Arial", warning[0].Format.Font); // Helvetica, as Word knows it
        Assert.Equal((1100, "#000000"), (paragraphs[2].Inlines.OfType<HText>().First().Format.Size, paragraphs[2].Inlines.OfType<HText>().First().Format.Color));
        Assert.Equal("https://example.com/", paragraphs[4].Inlines.OfType<HLink>().Single().Target);

        // The second page starts on a new page; the running header and footer are gone.
        Assert.True(paragraphs[5].PageBreakBefore);
        var allText = string.Concat(Paragraphs(model.Sections.SelectMany(s => s.Blocks)).Select(TextOf));
        Assert.DoesNotContain("Quarterly", allText);
        Assert.DoesNotContain("Page 1", allText);

        // A4 page; pictures at their size, the JPEG kept as it was.
        Assert.Equal((59500, 84200), (model.Sections[0].Page!.Width, model.Sections[0].Page!.Height));
        var pictures = Paragraphs(model.Sections.SelectMany(s => s.Blocks)).SelectMany(p => p.Inlines).OfType<HImage>().ToList();
        Assert.Equal([(20000, 12000), (10000, 10000)], pictures.Select(p => (p.Width!.Value, p.Height!.Value)));
        using (var zip = ZipFile.OpenRead(docx))
        {
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal) && e.FullName.EndsWith(".jpg", StringComparison.Ordinal));
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal) && e.FullName.EndsWith(".png", StringComparison.Ordinal));
        }
        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(2, pages);
    }

    [Fact]
    public async Task Two_column_pages_are_read_column_by_column()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        var body = pdf.Font();
        pdf.Line("Two Column Layout", 20, pdf.Font(bold: true), 50, 70);
        static string Text(string label) => $"{label} paragraph of the column: it has enough words to wrap over a few lines inside its narrow column of text.";
        var left = pdf.Paragraph(Text("Left first"), 10, body, 50, 110, 230, 13);
        pdf.Paragraph(Text("Left second"), 10, body, 50, left + 10, 230, 13);
        var right = pdf.Paragraph(Text("Right first"), 10, body, 315, 110, 230, 13);
        right = pdf.Paragraph(Text("Right second"), 10, body, 315, right + 10, 230, 13);
        pdf.Paragraph("A closing paragraph spans both columns again at the end of the page, below everything else.", 10, body, 50, right + 40, 495, 13);
        var path = pdf.Save(Path.Combine(dir, "columns.pdf"));

        var txt = await ConvertAsync(path, "txt");

        var text = await File.ReadAllTextAsync(txt, TestContext.Current.CancellationToken);
        var order = new[] { "Two Column Layout", "Left first", "Left second", "Right first", "Right second", "A closing paragraph" }
            .Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(order, index => Assert.True(index >= 0, text));
        Assert.Equal(order.OrderBy(i => i), order);
        // One paragraph per line, separated by empty lines.
        Assert.Contains(Text("Left first") + "\r\n\r\n" + Text("Left second"), text);
    }

    /// <summary>
    /// Page 5 of the RFP in #38: a schedule laid out with tab stops, an address block of short lines, and a form whose
    /// labels are short and long.
    /// </summary>
    private static string Schedule(string folder)
    {
        var pdf = new PdfBuilder().NewPage(612, 792);
        var body = pdf.Font();
        pdf.Paragraph("E. PROPOSED SCHEDULE OF ACTIVITIES: the dates below may change. This paragraph is long enough to wrap, so the page has a full text width.",
            12, body, 54, 60, 500, 15);
        string[][] rows = [["1.", "RFP Published", "08/14/2026"], ["2.", "Proposal Submission", "10/16/2026"], ["3.", "Proposal Selection", "10/19/2026"]];
        for (var r = 0; r < rows.Length; r++)
        {
            pdf.Line(rows[r][0], 12, body, 76.5, 110 + r * 14.6);
            pdf.Line(rows[r][1], 12, body, 108, 110 + r * 14.6);
            pdf.Line(rows[r][2], 12, body, 432, 110 + r * 14.6);
        }
        pdf.Paragraph("F. INQUIRIES: Send all inquiries (transmittal by E-Mail preferred) to the address below, which is the only one.", 12, body, 54, 170, 500, 15);
        string[] address = ["WESTAR", "1880 Willamette Falls Drive, Ste. 200E", "West Linn, Oregon 97068", "Attn: Jeffrey P. Gabler"];
        for (var l = 0; l < address.Length; l++)
            pdf.Line(address[l], 12, body, 144, 215 + l * 14.6);
        string[][] form = [["Name:", "JEFFREY P. GABLER"], ["Organization:", "WESTAR COUNCIL"], ["Phone:", "503-657-4838"]];
        for (var r = 0; r < form.Length; r++)
        {
            pdf.Line(form[r][0], 11, body, 72, 300 + r * 14);
            pdf.Line(form[r][1], 11, body, 176, 300 + r * 14);
        }
        return pdf.Save(Path.Combine(folder, "schedule.pdf"));
    }

    /// <summary>Text with tabs as \t and line breaks as \n.</summary>
    private static string Shown(HParagraph paragraph) => string.Concat(paragraph.Inlines.SelectMany(i => i is HLink link ? link.Content : [i])
        .Select(i => i switch { HText t => t.Text, HTab => "\t", HLineBreak => "\n", _ => "" }));

    [Fact]
    public async Task Tabbed_rows_and_line_breaks_are_kept()
    {
        var path = Schedule(fx.NewFolder());

        var docx = await ConvertAsync(path, "docx");

        ValidPackage(docx);
        var paragraphs = Paragraphs(ReadBack(docx).Sections.SelectMany(s => s.Blocks)).Where(p => TextOf(p).Length > 0).ToList();
        Assert.Equal([
            "1.\tRFP Published\t08/14/2026", "2.\tProposal Submission\t10/16/2026", "3.\tProposal Selection\t10/19/2026",
            "1880 Willamette Falls Drive, Ste. 200E\nWest Linn, Oregon 97068",
            "Name:\tJEFFREY P. GABLER", "Organization:\tWESTAR COUNCIL", "Phone:\t503-657-4838",
        ], paragraphs.Select(Shown).Where(t => t.Contains('\t') || t.Contains('\n')));
        // The tab stops sit where the parts start in the PDF, from the text's left edge (54 pt): 108 - 54 and 432 - 54.
        var row = paragraphs.Single(p => Shown(p).StartsWith("1.\t", StringComparison.Ordinal));
        Assert.Equal([5400, 37800], row.Format.Tabs!.Select(t => t.Position));
        Assert.All(row.Format.Tabs!, t => Assert.Equal(HTabKind.Left, t.Kind));
        // The street and the town stay apart; the short lines around them are paragraphs of their own.
        Assert.Contains(paragraphs, p => Shown(p) == "WESTAR");
        Assert.Contains(paragraphs, p => Shown(p) == "Attn: Jeffrey P. Gabler");

        var text = await File.ReadAllTextAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("1.\tRFP Published\t08/14/2026\r\n\r\n2.\tProposal Submission", text);
        Assert.Contains("Ste. 200E\r\nWest Linn", text);
    }

    [Fact]
    public async Task Scanned_pages_become_page_pictures()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(PdfBuilder.Photo(dir, "scan.jpg", MagickFormat.Jpeg, 595, 842), 0, 0, 595, 842);
        pdf.NewPage().Line("Text after the scan", 12, pdf.Font(), 50, 80);
        var path = pdf.Save(Path.Combine(dir, "scan.pdf"));

        var docx = await ConvertAsync(path, "docx");

        ValidPackage(docx);
        var section = Assert.Single(ReadBack(docx).Sections);
        var scan = section.Blocks.OfType<HParagraph>().First().Inlines.OfType<HImage>().Single();
        Assert.Equal((59500, 84200), (scan.Width, scan.Height));
        Assert.Equal(new HAnchor("PAPER", "LEFT", 0, "PAPER", "TOP", 0, "BEHIND_TEXT", true), scan.Anchor);
        var text = section.Blocks.OfType<HParagraph>().Single(p => TextOf(p) == "Text after the scan");
        Assert.True(text.PageBreakBefore);

        // Nothing to extract as text from the scan, but the text page still is.
        Assert.Equal("Text after the scan\r\n", await File.ReadAllTextAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pdf_to_hwpx_goes_through_the_document_model()
    {
        Assert.Equal("hwpx-writer", Assert.Single(fx.Catalog.CreatePlanner().Plan("pdf", "hwpx")!.Steps).Converter.Id);
        Assert.Equal("pdf-text", Assert.Single(fx.Catalog.CreatePlanner().Plan("pdf", "txt")!.Steps).Converter.Id);

        var hwpx = await ConvertAsync(Report(fx.NewFolder()), "hwpx");

        HwpxAssert.ValidPackage(hwpx, expectImages: 2);
        Assert.Contains("Annual Report 2026", HwpxAssert.Xml(hwpx).Value);
        HwpxAssert.RenderWithRhwp(fx, hwpx, "pdf-report");
    }

    /// <summary>Hangul needs an embedded font; the test uses Malgun Gothic when the system has it.</summary>
    [Fact]
    public async Task Korean_lines_join_into_one_paragraph()
    {
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
        Assert.SkipUnless(File.Exists(font), "no Hangul font (Malgun Gothic) on this system");
        const string Korean = "한글 문단도 읽을 수 있어야 합니다. 줄이 바뀌어도 한 문단으로 이어지고 단어 사이의 빈칸도 그대로 남습니다.";
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        var hangul = pdf.TrueType(await File.ReadAllBytesAsync(font, TestContext.Current.CancellationToken));
        pdf.Line("Korean", 16, pdf.Font(bold: true), 50, 80);
        pdf.Paragraph(Korean, 11, hangul, 50, 110, 200, 16);
        var path = pdf.Save(Path.Combine(dir, "korean.pdf"));

        var text = await File.ReadAllTextAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken);

        Assert.Equal("Korean\r\n\r\n" + Korean + "\r\n", text.TrimStart('﻿'));
    }

    [Fact]
    public async Task Password_protected_pdf_fails_with_a_clear_message()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Line("Secret", 12, pdf.Font(), 50, 80);
        var path = pdf.Save(Path.Combine(dir, "locked.pdf"));
        PdfBuilder.Protect(path, "1234");

        var job = await RunAsync(path, "docx");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ABCDEF+TimesNewRomanPS-BoldMT", "Times New Roman")]
    [InlineData("Arial-BoldMT", "Arial")]
    [InlineData("Helvetica-Oblique", "Arial")]
    [InlineData("QWERTY+MalgunGothic,Bold", "Malgun Gothic")]
    [InlineData("NanumGothic", "NanumGothic")]
    [InlineData("F1", null)]
    [InlineData("T3Font_2", null)]
    public void Font_names_become_families(string postScript, string? family) =>
        Assert.Equal(family, PdfDocumentReader.FontFamily(postScript));
}
