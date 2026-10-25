// Text recognition with Apple Vision (VisionConverter): the language mapping, the invisible text layer it writes and
// the converter around a stand-in recognizer run everywhere; AppleVisionTests read real pictures on macOS only.

using System.Collections.Concurrent;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Ocr;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;

namespace Filee.Engines.Tests;

public class VisionLanguageTests
{
    private static readonly string[] Ventura = [.. VisionLanguages.Fallback];

    [Fact]
    public void A_language_vision_reads_is_asked_for_with_english_next_to_it()
    {
        Assert.Equal(["en-US"], VisionLanguages.For("eng", Ventura));
        Assert.Equal(["ko-KR", "en-US"], VisionLanguages.For("kor", Ventura));
        Assert.Equal(["zh-Hant", "en-US"], VisionLanguages.For("chi_tra", Ventura));
        Assert.Equal(["DE-de", "en-US"], VisionLanguages.For("deu", ["DE-de", "en-US"])); // as this Mac spells it
    }

    [Fact]
    public void A_language_this_mac_has_no_model_for_is_left_to_tesseract()
    {
        Assert.Null(VisionLanguages.For("hin", Ventura)); // no Devanagari in Vision
        Assert.Null(VisionLanguages.For("tha", Ventura)); // Thai came with a later macOS
        Assert.Equal(["th-TH", "en-US"], VisionLanguages.For("tha", [.. Ventura, "th-TH"]));
        Assert.Equal(["no-NO", "en-US"], VisionLanguages.For("nor", [.. Ventura, "no-NO"]));
        Assert.Equal(["fr-FR"], VisionLanguages.For("fra", ["fr-FR"]));
    }

    [Fact]
    public void Every_mapped_language_is_one_filee_offers()
    {
        foreach (var code in new[] { "eng", "deu", "fra", "spa", "por", "ita", "nld", "pol", "ces", "ron", "swe", "dan", "nor", "tur",
                     "ind", "msa", "rus", "ukr", "ara", "tha", "vie", "kor", "jpn", "chi_sim", "chi_tra" })
            Assert.NotNull(OcrLanguages.Find(code));
    }
}

public class OcrTextLayerTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Fact]
    public void Words_are_found_at_spaces_and_spread_over_the_line_when_they_have_no_boxes()
    {
        Assert.Equal([(0, 5), (7, 5), (13, 1)], OcrLine.WordRanges("Filee  reads\tA "));
        var line = OcrLine.Estimate("ab cd", 0.1, 0.5, 0.5, 0.1);
        Assert.Equal(["ab", "cd"], line.Words.Select(w => w.Text));
        Assert.Equal(0.1, line.Words[0].Left, 6);
        Assert.Equal(0.2, line.Words[0].Width, 6); // 5 letters' width, the space included
        Assert.Equal(0.4, line.Words[1].Left, 6);
        Assert.Empty(OcrLine.Estimate("   ", 0, 0, 1, 1).Words);
    }

    [Fact]
    public void The_glyphless_font_is_a_well_formed_truetype_font()
    {
        var font = GlyphlessFont.Bytes;
        Assert.Equal(0x00010000u, BigEndian(font, 0));
        uint sum = 0;
        for (var i = 0; i < font.Length; i += 4)
            sum = unchecked(sum + BigEndian(font, i));
        Assert.Equal(0xB1B0AFBAu, sum); // the whole font sums to the magic number (head.checkSumAdjustment)
        var tags = Enumerable.Range(0, (font[4] << 8) | font[5]).Select(i => System.Text.Encoding.ASCII.GetString(font, 12 + 16 * i, 4)).ToList();
        Assert.Equal(tags.Order(StringComparer.Ordinal), tags);
        Assert.Contains("head", tags);
        Assert.Contains("hmtx", tags);
        using var typeface = SkiaSharp.SKTypeface.FromData(SkiaSharp.SKData.CreateCopy(font));
        Assert.NotNull(typeface); // the system's font loader takes it
        Assert.Equal(2, typeface.GlyphCount);
    }

    private static uint BigEndian(byte[] bytes, int at) =>
        (uint)((at < bytes.Length ? bytes[at] : 0) << 24 | (at + 1 < bytes.Length ? bytes[at + 1] : 0) << 16
               | (at + 2 < bytes.Length ? bytes[at + 2] : 0) << 8 | (at + 3 < bytes.Length ? bytes[at + 3] : 0));

    [Fact]
    public void The_recognized_words_are_invisible_text_where_the_picture_shows_them()
    {
        var folder = fx.NewFolder();
        var picture = VisionFakes.Picture(folder, "page.png", 1240, 1754);
        var pdf = Path.Combine(folder, "page.pdf");
        using (var writer = new OcrPdfWriter())
        {
            // An A4 page at 150 dpi: 595.2 × 841.92 points.
            writer.AddPage(picture, 595.2, 841.92,
            [
                new OcrLine(0.1, 0.8, 0.6, 0.02, [new OcrWord("Quarterly", 0.1, 0.2), new OcrWord("report", 0.35, 0.15)]),
                OcrLine.Estimate("검색 가능한 문서 Документ", 0.1, 0.5, 0.7, 0.03),
            ]);
            writer.Save(pdf);
        }

        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        Assert.Equal(595.2, page.Width, 1);
        Assert.Equal(841.92, page.Height, 1);
        var words = page.GetWords().ToList();
        Assert.Equal(["Quarterly", "report", "검색", "가능한", "문서", "Документ"], words.Select(w => w.Text));
        Assert.All(page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)), l => Assert.Equal(TextRenderingMode.Neither, l.RenderingMode));
        var quarterly = words[0];
        Assert.Equal(0.1 * 595.2, quarterly.BoundingBox.Left, 0);
        Assert.Equal(0.8 * 841.92, quarterly.Letters[0].StartBaseLine.Y, 0);
        Assert.InRange(quarterly.BoundingBox.Right, 0.29 * 595.2, 0.35 * 595.2 + 1); // stretched up to the next word
        Assert.InRange(words[1].BoundingBox.Left, 0.35 * 595.2 - 1, 0.35 * 595.2 + 1);
        Assert.Single(page.GetImages());

        // A page with an OCR layer counts as having text: OCR leaves it alone the next time.
        Assert.Equal([true], TesseractConverter.PagesWithText(pdf));
    }

    [Fact]
    public async Task A_page_with_the_layer_reads_into_txt()
    {
        var folder = fx.NewFolder();
        var picture = VisionFakes.Picture(folder, "scan.png", 600, 800);
        var pdf = Path.Combine(folder, "scan.pdf");
        using (var writer = new OcrPdfWriter())
        {
            writer.AddPage(picture, 144, 192, [OcrLine.Estimate("The quarterly report is ready", 0.1, 0.7, 0.8, 0.04)]);
            writer.Save(pdf);
        }

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "txt" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("The quarterly report is ready", text);
    }
}

public class VisionConverterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Preset Ocr(string language = "eng") => new() { TargetFormat = "pdf", Pdf = { Ocr = true, OcrLanguage = language } };

    private static ConversionStep Step(string input, string from, string output, Preset preset, Action<string>? notice = null) =>
        new(input, from, "pdf", preset, new HwpxAssert.FixedPath(output), Path.GetDirectoryName(output)!, Notice: notice);

    private static string PageText(string pdf, int page)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPage(page).GetWords().Select(w => w.Text));
    }

    [Fact]
    public void Off_a_mac_it_is_unavailable_and_only_vision_takes_its_place_on_one()
    {
        var off = new VisionConverter(null, new VisionFakes.Tesseract(available: true));
        Assert.Equal("engine.reason.unsupported_platform", off.GetStatus().ReasonKey);
        Assert.False(off.Reads("eng"));

        var mac = new VisionConverter(new VisionFakes.Recognizer(), new VisionFakes.Tesseract(available: true));
        Assert.True(mac.GetStatus().IsAvailable);
        Assert.True(mac.Reads("kor"));
        Assert.False(mac.Reads("hin"));
        Assert.Equal(OperatingSystem.IsMacOS(), new VisionConverter().GetStatus().IsAvailable);
    }

    [Fact]
    public void On_a_mac_vision_takes_the_ocr_step_before_tesseract()
    {
        var all = EngineRegistry.CreateAll(new EngineEnvironment(fx.NewFolder()));
        var ids = all.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf("vision") < ids.IndexOf("tesseract"));

        var vision = new VisionConverter(new VisionFakes.Recognizer(), new VisionFakes.Tesseract(available: true));
        var tesseract = new TesseractConverter();
        var catalog = new ConverterCatalog([vision, tesseract]) { Priority = new AppSettings().EnginePriority };
        var planner = new RoutePlanner(catalog.All, _ => 9);

        Assert.Equal("vision", planner.Plan("pdf", "pdf", ocr: true)!.Steps.Single().Converter.Id);
        Assert.Equal("vision", planner.Plan("jpg", "pdf", ocr: true)!.Steps.Single().Converter.Id);
        Assert.Equal("tesseract", new RoutePlanner([tesseract]).Plan("png", "pdf", ocr: true)!.Steps.Single().Converter.Id);
    }

    [Fact]
    public async Task A_picture_becomes_a_page_of_its_size_with_the_text_on_it()
    {
        var folder = fx.NewFolder();
        var picture = VisionFakes.Picture(folder, "letter.jpg", 1200, 600, dpi: 150);
        var recognizer = new VisionFakes.Recognizer();
        var output = Path.Combine(folder, "letter.pdf");

        await new VisionConverter(recognizer, new VisionFakes.Tesseract(available: false))
            .ConvertAsync(Step(picture, "jpg", output, Ocr("deu")), null, TestContext.Current.CancellationToken);

        Assert.Equal(["de-DE", "en-US"], Assert.Single(recognizer.Calls).Languages);
        Assert.Equal(picture, recognizer.Calls.Single().Picture); // a JPEG goes in as it is
        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(576, document.GetPage(1).Width, 1); // 1200 px at 150 dpi = 8 inches
        Assert.Equal(VisionFakes.Text, PageText(output, 1));
    }

    [Fact]
    public async Task Every_page_of_a_tiff_is_recognized()
    {
        var folder = fx.NewFolder();
        var tiff = EngineFixture.MakeMultiPageTiff(folder, 3);
        var recognizer = new VisionFakes.Recognizer();
        var output = Path.Combine(folder, "pages.pdf");

        await new VisionConverter(recognizer, new VisionFakes.Tesseract(available: false))
            .ConvertAsync(Step(tiff, "tiff", output, Ocr()), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, recognizer.Calls.Count);
        Assert.All(recognizer.Calls, c => Assert.EndsWith(".png", c.Picture));
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.PageCount);
    }

    [Fact]
    public async Task Only_the_scanned_pages_of_a_pdf_are_recognized()
    {
        var folder = fx.NewFolder();
        var scan = (await fx.ConvertAsync([VisionFakes.Picture(folder, "scan.png", 600, 800)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var searchable = Path.Combine(folder, "searchable.pdf");
        using (var writer = new OcrPdfWriter())
        {
            writer.AddPage(VisionFakes.Picture(folder, "done.png", 600, 800), 144, 192, [OcrLine.Estimate("Already searchable", 0.1, 0.5, 0.8, 0.05)]);
            writer.Save(searchable);
        }
        var mixed = Path.Combine(folder, "mixed.pdf");
        using (var document = new PdfDocument())
        {
            foreach (var part in new[] { searchable, scan })
            {
                using var source = PdfReader.Open(part, PdfDocumentOpenMode.Import);
                document.AddPage(source.Pages[0]);
            }
            document.Save(mixed);
        }
        var recognizer = new VisionFakes.Recognizer();
        var output = Path.Combine(folder, "out.pdf");

        await new VisionConverter(recognizer, new VisionFakes.Tesseract(available: false))
            .ConvertAsync(Step(mixed, "pdf", output, Ocr()), null, TestContext.Current.CancellationToken);

        Assert.Single(recognizer.Calls);
        Assert.Equal("Already searchable", PageText(output, 1));
        Assert.Equal(VisionFakes.Text, PageText(output, 2));
        using var result = UglyToad.PdfPig.PdfDocument.Open(output);
        using var original = UglyToad.PdfPig.PdfDocument.Open(scan);
        Assert.Equal(original.GetPage(1).Width, result.GetPage(2).Width, 0); // the page keeps its size
    }

    [Fact]
    public async Task A_pdf_with_text_on_every_page_is_copied_with_a_note()
    {
        var folder = fx.NewFolder();
        var searchable = Path.Combine(folder, "searchable.pdf");
        using (var writer = new OcrPdfWriter())
        {
            writer.AddPage(VisionFakes.Picture(folder, "done.png", 600, 800), 144, 192, [OcrLine.Estimate("Already searchable", 0.1, 0.5, 0.8, 0.05)]);
            writer.Save(searchable);
        }
        var recognizer = new VisionFakes.Recognizer();
        var output = Path.Combine(folder, "out.pdf");
        string? notice = null;

        await new VisionConverter(recognizer, new VisionFakes.Tesseract(available: false))
            .ConvertAsync(Step(searchable, "pdf", output, Ocr(), n => notice = n), null, TestContext.Current.CancellationToken);

        Assert.Empty(recognizer.Calls);
        Assert.Equal("notice.ocr_has_text", notice);
    }

    [Fact]
    public async Task A_language_vision_doesnt_read_goes_to_tesseract_or_asks_for_it()
    {
        var folder = fx.NewFolder();
        var picture = VisionFakes.Picture(folder, "hindi.png", 300, 200);
        var recognizer = new VisionFakes.Recognizer();
        var installed = new VisionFakes.Tesseract(available: true);

        var written = await new VisionConverter(recognizer, installed)
            .ConvertAsync(Step(picture, "png", Path.Combine(folder, "a.pdf"), Ocr("hin")), null, TestContext.Current.CancellationToken);

        Assert.Equal(["tesseract"], written);
        Assert.Equal(1, installed.Conversions);
        Assert.Empty(recognizer.Calls);

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => new VisionConverter(recognizer, new VisionFakes.Tesseract(available: false))
            .ConvertAsync(Step(picture, "png", Path.Combine(folder, "b.pdf"), Ocr("hin")), null, TestContext.Current.CancellationToken));
        Assert.Equal("error.ocr_needs_tesseract", refused.ErrorKey);
        Assert.Equal(OcrLanguages.Find("hin")!.NativeName, refused.Detail);
    }

    [Fact]
    public void When_vision_refuses_two_languages_it_reads_the_first_alone()
    {
        var recognizer = new VisionFakes.Recognizer { FailWithSeveralLanguages = true };

        var lines = VisionConverter.Recognize(recognizer, "page.png", ["ko-KR", "en-US"], TestContext.Current.CancellationToken);

        Assert.Single(lines);
        Assert.Equal(2, recognizer.Calls.Count);
        Assert.Equal(["ko-KR", "en-US"], recognizer.Calls[0].Languages);
        Assert.Equal(["ko-KR"], recognizer.Calls[1].Languages);
    }
}

/// <summary>Apple Vision itself: only on macOS (CI's macOS jobs run these on Apple silicon and on Intel).</summary>
public class AppleVisionTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Skip = "Apple Vision runs on macOS only";

    /// <summary>Black text on white at 300 dpi, as a scanner writes it, in a font macOS has.</summary>
    private static string Scan(string folder, string name, string font, params string[] lines)
    {
        using var image = new MagickImage(MagickColors.White, 2480, 800) { Density = new Density(300) };
        var drawables = new Drawables().FontPointSize(32).FillColor(MagickColors.Black).Font(font);
        for (var i = 0; i < lines.Length; i++)
            drawables.Text(150, 240 + i * 200, lines[i]);
        drawables.Draw(image);
        var path = Path.Combine(folder, name);
        image.Write(path);
        return path;
    }

    private const string Helvetica = "/System/Library/Fonts/Helvetica.ttc";
    private const string Gothic = "/System/Library/Fonts/AppleSDGothicNeo.ttc";

    [Fact]
    public void Vision_reads_the_lines_and_words_of_a_picture()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Skip);
            return;
        }
        var vision = new AppleVision();
        Assert.True(vision.IsAvailable);
        Assert.Contains("en-US", vision.Languages);
        Assert.Contains("ko-KR", vision.Languages);
        var scan = Scan(fx.NewFolder(), "letter.png", Helvetica, "Filee reads scanned pages", "into searchable documents");

        var lines = vision.Recognize(scan, ["en-US"], TestContext.Current.CancellationToken);

        Assert.Equal(2, lines.Count);
        var first = lines.Single(l => l.Words.Any(w => w.Text == "Filee"));
        var second = lines.Single(l => l != first);
        Assert.Equal(["Filee", "reads", "scanned", "pages"], first.Words.Select(w => w.Text));
        Assert.True(first.Bottom > second.Bottom); // measured from the bottom of the picture
        foreach (var line in lines)
        {
            Assert.InRange(line.Height, 0.01, 0.2);
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                Assert.InRange(word.Left, line.Left - 0.01, line.Left + line.Width);
                Assert.InRange(word.Width, 0.005, line.Width + 0.01);
                if (i > 0)
                    Assert.True(word.Left > line.Words[i - 1].Left);
            }
        }
    }

    [Fact]
    public async Task A_picture_and_a_scanned_pdf_become_searchable_with_vision()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), Skip);
        var folder = fx.NewFolder();
        var picture = Scan(folder, "편지.png", Helvetica, "Filee reads scanned pages");
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "Скан.png", Helvetica, "The quarterly report is ready")], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([picture, scanPdf], new Preset { TargetFormat = "pdf", Pdf = { Ocr = true } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Contains("scanned", Text(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("편지", StringComparison.Ordinal))));
        Assert.Contains("quarterly", Text(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("Скан", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Korean_text_is_read_into_txt()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), Skip);
        var folder = fx.NewFolder();
        var scan = Scan(folder, "scan.png", Gothic, "분기 보고서가 준비되었습니다");

        var job = await fx.ConvertAsync([scan], new Preset { TargetFormat = "txt", Pdf = { Ocr = true, OcrLanguage = "kor" } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("보고서", text);
    }

    private static string Text(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
    }
}

/// <summary>Stand-ins for Apple Vision and Tesseract.</summary>
internal static class VisionFakes
{
    public const string Text = "Filee reads scanned pages";

    /// <summary>A grey picture of the given size and resolution.</summary>
    public static string Picture(string folder, string name, uint width, uint height, int dpi = 300)
    {
        using var image = new MagickImage(MagickColors.LightGray, width, height) { Density = new Density(dpi) };
        var path = Path.Combine(folder, name);
        image.Write(path);
        return path;
    }

    /// <summary>Finds <see cref="Text"/> on every picture and reads the languages Vision reads on macOS 13.</summary>
    public sealed class Recognizer : ITextRecognizer
    {
        public ConcurrentQueue<(string Picture, IReadOnlyList<string> Languages)> CallQueue { get; } = new();

        public IReadOnlyList<(string Picture, IReadOnlyList<string> Languages)> Calls => [.. CallQueue];

        /// <summary>Fails like Vision on a combination of languages it doesn't take.</summary>
        public bool FailWithSeveralLanguages { get; init; }

        public bool IsAvailable => true;

        public IReadOnlyCollection<string> Languages => VisionLanguages.Fallback;

        public IReadOnlyList<OcrLine> Recognize(string picture, IReadOnlyList<string> languages, CancellationToken cancellationToken)
        {
            CallQueue.Enqueue((picture, languages));
            if (FailWithSeveralLanguages && languages.Count > 1)
                throw new InvalidOperationException("Apple Vision couldn't read the picture.");
            return [OcrLine.Estimate(Text, 0.1, 0.6, 0.8, 0.05)];
        }
    }

    /// <summary>A Tesseract that is or isn't installed and only counts its conversions.</summary>
    public sealed class Tesseract(bool available) : IConverter
    {
        public int Conversions { get; private set; }
        public string Id => "tesseract";
        public string DisplayName => "Tesseract OCR";
        public int MaxParallelism => 1;
        public IReadOnlyList<ConversionEdge> Edges => [];

        public EngineStatus GetStatus() => available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_installed");

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Conversions++;
            return Task.FromResult<IReadOnlyList<string>>(["tesseract"]);
        }
    }
}
