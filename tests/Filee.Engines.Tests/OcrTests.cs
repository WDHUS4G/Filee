// Text recognition with Tesseract (issue #288): scans and pictures become searchable PDFs, and their text goes on into
// DOCX and TXT. The real tests need Tesseract (pwsh build/fetch-engines.ps1 -Only 7zip,tesseract) and download the
// language data they read the first time (English, German).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Ocr;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class OcrLanguageListTests
{
    [Fact]
    public void Every_language_is_pinned_and_named()
    {
        Assert.Equal("eng", OcrLanguages.All[0].Code);
        Assert.Equal(69, OcrLanguages.All.Count);
        Assert.All(OcrLanguages.All, language =>
        {
            Assert.NotEqual(language.Code, language.NativeName); // its culture is known
            var component = OcrLanguages.Components[language.Code];
            Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
            Assert.True(component.Size > 0);
            Assert.Equal($"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/{language.Code}.traineddata", component.Url);
        });
        Assert.Equal("Deutsch", OcrLanguages.Find("deu")!.NativeName);
        Assert.Null(OcrLanguages.Find("klingon"));
    }

    [Fact]
    public void Tesseract_is_offered_but_not_suggested()
    {
        var package = EngineDownloads.Packages.Single(p => p.Id == "tesseract");

        Assert.False(EngineDownloads.IsSuggested(package));
        Assert.Equal(OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract", Assert.Single(EngineEnvironment.OwnCopyPrograms["tesseract"]));
    }
}

/// <summary>Which pages count as having text, so OCR leaves them alone (no Tesseract needed).</summary>
public class OcrPagesWithTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// A one-page A4 PDF written by hand: optionally a picture over the whole page (a scan), then lines of Helvetica
    /// text, visible or invisible (render mode 3, as an OCR layer is written).
    /// </summary>
    private string Pdf(bool scan, int lines, string text = "ABC-000123", bool invisible = false)
    {
        var content = new StringBuilder();
        if (scan)
            content.Append("q 595 0 0 842 0 0 cm /Im1 Do Q\n");
        for (var i = 0; i < lines; i++)
            content.Append($"BT /F1 10 Tf {(invisible ? "3" : "0")} Tr 40 {800 - i * 12} Td ({text}) Tj ET\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u00c8\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(fx.NewFolder(), "page.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    [Fact]
    public void A_scan_with_only_a_stamp_is_recognized()
    {
        Assert.Equal([false], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 1)));
        Assert.Equal([false], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 0)));
    }

    [Fact]
    public void An_ocr_layer_a_page_of_text_and_a_drawn_note_are_left_alone()
    {
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 1, invisible: true)));
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 40, text: "The quick brown fox jumps over the lazy dog again and again")));
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: false, lines: 1)));
    }
}

public class OcrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>On a Mac Apple Vision takes the OCR step (VisionConverter, AppleVisionTests), so these are Tesseract's elsewhere.</summary>
    private static readonly bool Installed = TesseractConverter.Locate() is not null && !new VisionConverter().GetStatus().IsAvailable;

    private const string Skip = "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only 7zip,tesseract), or Apple Vision reads instead";

    private static readonly string? Arial = EngineEnvironment.FirstExisting(@"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf");

    /// <summary>A page as a scanner writes it: black text on white at 300 dpi, no text layer.</summary>
    private static string Scan(string folder, string name, params string[] lines)
    {
        using var image = new MagickImage(MagickColors.White, 2480, 800) { Density = new Density(300) };
        var drawables = new Drawables().FontPointSize(28).FillColor(MagickColors.Black);
        if (Arial is not null)
            drawables.Font(Arial);
        for (var i = 0; i < lines.Length; i++)
            drawables.Text(150, 220 + i * 180, lines[i]);
        drawables.Draw(image);
        var path = Path.Combine(folder, name);
        image.Write(path);
        return path;
    }

    private static string PageText(string pdf, int page)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPage(page).GetWords().Select(w => w.Text));
    }

    private static Preset Ocr(string target, string language = "eng") => new() { TargetFormat = target, Pdf = { Ocr = true, OcrLanguage = language } };

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    [Fact]
    public async Task A_picture_becomes_a_searchable_pdf()
    {
        Assert.SkipUnless(Installed, Skip);
        var scan = Scan(fx.NewFolder(), "letter.png", "Filee reads scanned pages", "into searchable documents");

        var job = await fx.ConvertAsync([scan], Ocr("pdf"));

        AssertDone(job);
        var text = PageText(job.Outputs.Single(), 1);
        Assert.Contains("scanned", text);
        Assert.Contains("searchable", text);
    }

    [Fact]
    public async Task Pictures_and_scans_with_korean_and_cyrillic_names_are_recognized()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = Path.Combine(fx.NewFolder(), "김민수 Документы");
        Directory.CreateDirectory(folder);
        var picture = Scan(folder, "스캔.png", "Filee reads scanned pages");
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "Скан.png", "The quarterly report is ready")], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([picture, scanPdf], Ocr("pdf"));

        AssertDone(job);
        Assert.Contains("scanned", PageText(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("스캔", StringComparison.Ordinal)), 1));
        Assert.Contains("quarterly", PageText(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("Скан", StringComparison.Ordinal)), 1));
    }

    [Fact]
    public async Task Removing_metadata_leaves_the_document_information_behind()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "scan.png", "Private letter")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using (var document = PdfReader.Open(scanPdf, PdfDocumentOpenMode.Modify))
        {
            document.Info.Title = "Secret title";
            document.Info.Author = "Someone";
            document.Save(scanPdf);
        }
        var keep = Ocr("pdf");
        var remove = Ocr("pdf");
        remove.Pdf.RemoveMetadata = true;

        var kept = (await fx.ConvertAsync([scanPdf], keep)).Outputs.Single();
        var removed = (await fx.ConvertAsync([scanPdf], remove)).Outputs.Single();

        using (var document = PdfReader.Open(kept, PdfDocumentOpenMode.Import))
            Assert.Equal("Secret title", document.Info.Title);
        using (var document = PdfReader.Open(removed, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("", document.Info.Title);
            Assert.Equal("", document.Info.Author);
        }
        Assert.Contains("Private", PageText(removed, 1));
    }

    [Fact]
    public async Task A_scanned_pdf_becomes_a_docx_with_its_text_and_without_the_scan()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "scan.png", "The quarterly report is ready")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        Assert.Equal("", PageText(scanPdf, 1)); // only a picture

        var job = await fx.ConvertAsync([scanPdf], Ocr("docx"));

        AssertDone(job);
        using var docx = ZipFile.OpenRead(job.Outputs.Single());
        using var reader = new StreamReader(docx.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quarterly", xml);
        Assert.DoesNotContain("<w:drawing", xml);
    }

    [Fact]
    public async Task Pages_with_text_stay_as_they_are_and_only_scans_are_recognized()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var withText = (await fx.ConvertAsync([Scan(folder, "first.png", "This page was searchable before")], Ocr("pdf"))).Outputs.Single();
        var scan = (await fx.ConvertAsync([Scan(folder, "second.png", "This page is only a picture")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var mixed = Path.Combine(folder, "mixed.pdf");
        using (var document = new PdfDocument())
        {
            foreach (var part in new[] { withText, scan })
            {
                using var source = PdfReader.Open(part, PdfDocumentOpenMode.Import);
                document.AddPage(source.Pages[0]);
            }
            document.Save(mixed);
        }

        var job = await fx.ConvertAsync([mixed], Ocr("pdf"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal(PageText(withText, 1), PageText(output, 1)); // copied, not recognized again
        Assert.Contains("picture", PageText(output, 2));
    }

    [Fact]
    public async Task A_long_scan_is_recognized_page_by_page_in_order()
    {
        Assert.SkipUnless(Installed, Skip);
        Assert.InRange(TesseractConverter.Runs, 1, 4);
        var folder = fx.NewFolder();
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf" };
        var scans = words.Select((w, i) => Scan(folder, $"p{i}.png", $"Page {w}")).ToList();
        var pdf = (await fx.ConvertAsync(scans, new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], Ocr("pdf"));

        AssertDone(job);
        var output = job.Outputs.Single();
        for (var i = 0; i < words.Length; i++)
            Assert.Contains(words[i], PageText(output, i + 1));
    }

    [Fact]
    public async Task A_pdf_that_has_text_everywhere_is_copied_with_a_note()
    {
        Assert.SkipUnless(Installed, Skip);
        var searchable = (await fx.ConvertAsync([Scan(fx.NewFolder(), "done.png", "Already recognized")], Ocr("pdf"))).Outputs.Single();

        var job = await fx.ConvertAsync([searchable], Ocr("pdf"));

        AssertDone(job);
        Assert.Equal("notice.ocr_has_text", job.Files[0].NoticeKey);
        Assert.Equal(await File.ReadAllBytesAsync(searchable, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(job.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_language_is_downloaded_when_a_preset_needs_it()
    {
        Assert.SkipUnless(Installed, Skip);
        var scan = Scan(fx.NewFolder(), "brief.jpg", "Die Größe der Straße ändert sich");

        var job = await fx.ConvertAsync([scan], Ocr("txt", "deu"));

        AssertDone(job);
        Assert.True(OcrLanguages.IsInstalled("deu"));
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("Größe", text);
        Assert.Contains("Straße", text);
    }
}
