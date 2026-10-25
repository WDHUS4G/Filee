// PDF → smaller PDF ("Compress PDF"): pictures scaled down to the level's resolution where they are drawn (also inside
// forms), soft masks kept, text untouched, page ranges and one file per page, nothing made bigger, password-protected
// files refused like the other PDF conversions, and Ghostscript's rewrite when it is installed. The PDFs are built in
// the tests: photos (noise, so they don't compress to nothing) drawn small at a high resolution, and Helvetica text.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfCompressionTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sentence = "Quarterly report with photos";

    private static bool GhostscriptInstalled => GhostscriptConverter.Locate() is not null;

    /// <summary>A photo-like picture: a gradient with noise, so JPEG and Flate can't make it tiny.</summary>
    private static string Photo(string folder, string name, uint width, uint height, MagickFormat format, bool transparentCorner = false)
    {
        using var image = new MagickImage("gradient:#C04020-#2060C0", width, height);
        image.AddNoise(NoiseType.Gaussian, 0.6);
        if (transparentCorner)
        {
            image.Alpha(AlphaOption.Set);
            using var pixels = image.GetPixels();
            pixels.SetArea(0, 0, width / 4, height / 4, new byte[width / 4 * (height / 4) * image.ChannelCount]);
        }
        image.Quality = 95;
        var path = Path.Combine(folder, name);
        image.Write(path, format);
        return path;
    }

    /// <summary>
    /// <paramref name="pages"/> pages, each with a line of text and a 2400 × 1600 picture drawn 3 × 2 inches (800 dpi).
    /// </summary>
    private static string ReportPdf(string folder, int pages = 1, MagickFormat format = MagickFormat.Jpeg, bool transparent = false)
    {
        var picture = Photo(folder, format == MagickFormat.Jpeg ? "photo.jpg" : "photo.png", 2400, 1600, format, transparent);
        var pdf = new PdfBuilder();
        for (var i = 1; i <= pages; i++)
        {
            pdf.NewPage();
            pdf.Line($"{Sentence} {i}", 14, pdf.Font(), 72, 72);
            pdf.Picture(picture, 72, 100, 216, 144);
        }
        return pdf.Save(Path.Combine(folder, "report.pdf"));
    }

    private static Preset Compress(PdfCompression level = PdfCompression.Ebook, bool ghostscript = false) => new()
    {
        TargetFormat = "pdf",
        Pdf = { Compression = level, CompressWithGhostscript = ghostscript },
        Output = { FileNamePattern = "{name}_compressed" },
    };

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    /// <summary>Width and height in pixels of every picture on every page, with whether it has a soft mask.</summary>
    private static List<(int Width, int Height, bool Masked)> Pictures(string path)
    {
        using var pdf = PigDocument.Open(path);
        return pdf.GetPages().SelectMany(p => p.GetImages())
            .Select(i => (i.WidthInSamples, i.HeightInSamples, i.MaskImage is not null))
            .ToList();
    }

    private static List<string> Texts(string path)
    {
        using var pdf = PigDocument.Open(path);
        return pdf.GetPages().Select(p => p.Text).ToList();
    }

    [Theory]
    [InlineData(PdfCompression.Screen, 216)]
    [InlineData(PdfCompression.Ebook, 450)]
    [InlineData(PdfCompression.Print, 900)]
    public async Task Pictures_are_scaled_to_the_level_resolution_and_text_stays(PdfCompression level, int expectedWidth)
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 2);

        var job = await fx.ConvertAsync([source], Compress(level));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("report_compressed.pdf", Path.GetFileName(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length / 4, $"{new FileInfo(output).Length} of {new FileInfo(source).Length}");
        Assert.Equal(Texts(source), Texts(output));
        Assert.All(Pictures(output), p => Assert.InRange(p.Width, expectedWidth - 2, expectedWidth + 2));
        Assert.All(Pictures(output), p => Assert.Equal(p.Width * 2 / 3, p.Height, 2.0));
        Assert.Null(job.Files.Single().NoticeKey);
    }

    [Fact]
    public async Task A_picture_embedded_on_every_page_is_stored_once()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 3, format: MagickFormat.Png, transparent: true);
        using (var input = PdfReader.Open(source, PdfDocumentOpenMode.Import))
            Assert.Equal(6, ImagesOf(input).Count); // three pictures, three soft masks

        var job = await fx.ConvertAsync([source], Compress());

        AssertDone(job);
        using var output = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, ImagesOf(output).Count);
        Assert.Equal(3, Pictures(job.Outputs.Single()).Count(p => p.Masked));
    }

    [Fact]
    public async Task A_transparent_picture_keeps_its_soft_mask()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, format: MagickFormat.Png, transparent: true);
        Assert.True(Pictures(source).Single().Masked);

        var job = await fx.ConvertAsync([source], Compress(PdfCompression.Screen));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length);
        var picture = Pictures(output).Single();
        Assert.True(picture.Masked);
        Assert.InRange(picture.Width, 214, 218);

        using var pdf = PigDocument.Open(output);
        var mask = pdf.GetPage(1).GetImages().Single().MaskImage!;
        Assert.Equal(picture.Width, mask.WidthInSamples);
        // The transparent corner is still transparent, the rest still opaque.
        Assert.True(mask.TryGetPng(out var png));
        using var alpha = new MagickImage(png);
        using var values = alpha.GetPixels();
        Assert.True(values.GetPixel(2, 2).GetChannel(0) < 16);
        Assert.True(values.GetPixel((int)alpha.Width - 3, (int)alpha.Height - 3).GetChannel(0) > 240);
    }

    [Fact]
    public async Task Gray_pictures_stay_gray()
    {
        var dir = fx.NewFolder();
        var photo = Photo(dir, "gray.jpg", 2000, 2000, MagickFormat.Jpeg);
        using (var gray = new MagickImage(photo))
        {
            gray.Grayscale();
            gray.Write(photo, MagickFormat.Jpeg);
        }
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(photo, 72, 72, 144, 144);
        var source = pdf.Save(Path.Combine(dir, "gray.pdf"));

        var job = await fx.ConvertAsync([source], Compress());

        AssertDone(job);
        using var output = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        var image = ImagesOf(output).Single();
        Assert.Equal("/DeviceGray", image.Elements.GetName("/ColorSpace"));
        Assert.Equal(300, image.Elements.GetInteger("/Width"));
        Assert.Equal("/DCTDecode", image.Elements.GetName("/Filter"));
    }

    [Fact]
    public async Task Pictures_in_forms_are_sized_where_the_form_draws_them()
    {
        var dir = fx.NewFolder();
        var photo = Photo(dir, "photo.jpg", 3000, 3000, MagickFormat.Jpeg);
        var source = Path.Combine(dir, "form.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            // The form holds the picture at 1000 × 1000 points; the page draws the form at a tenth: 100 pt = 1.39 in.
            var form = new XForm(document, XUnit.FromPoint(1000), XUnit.FromPoint(1000));
            using (var image = XImage.FromFile(photo))
            using (var formGraphics = XGraphics.FromForm(form))
                formGraphics.DrawImage(image, 0, 0, 1000, 1000);
            using (var graphics = XGraphics.FromPdfPage(page))
                graphics.DrawImage(form, 72, 72, 100, 100);
            document.Save(source);
        }

        var job = await fx.ConvertAsync([source], Compress(PdfCompression.Ebook));

        AssertDone(job);
        using var output = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        // 100 pt at 150 dpi = 208 px.
        Assert.InRange(ImagesOf(output).Single().Elements.GetInteger("/Width"), 206, 210);
    }

    [Fact]
    public async Task Page_ranges_and_one_file_per_page_are_compressed_too()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 3);

        var range = Compress();
        range.Pdf.PageRange = "2";
        var one = await fx.ConvertAsync([source], range);
        var split = Compress();
        split.Pdf.SplitPages = true;
        var each = await fx.ConvertAsync([source], split);

        AssertDone(one);
        Assert.Equal([$"{Sentence} 2"], Texts(one.Outputs.Single()));
        Assert.InRange(Pictures(one.Outputs.Single()).Single().Width, 448, 452);
        AssertDone(each);
        Assert.Equal(["report_compressed_p1.pdf", "report_compressed_p2.pdf", "report_compressed_p3.pdf"],
            each.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(each.Outputs, o => Assert.InRange(Pictures(o).Single().Width, 448, 452));
    }

    [Fact]
    public async Task Bookmarks_survive_when_the_whole_file_is_compressed()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 2);
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.Outlines.Add("Second page", document.Pages[1]);
            document.Save(source);
        }

        var job = await fx.ConvertAsync([source], Compress());

        AssertDone(job);
        using var output = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal("Second page", output.Outlines.Single().Title);
    }

    [Fact]
    public async Task Page_metadata_goes_only_when_the_preset_drops_it()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir);
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Elements["/Metadata"] = Xmp(document, "filee-page-xmp").Reference;
            // A large packet, as image editors leave them: PDFsharp writes the document's XMP afresh, so the old one goes.
            document.Internals.Catalog.Elements["/Metadata"] = Xmp(document, "filee-document-xmp" + new string(' ', 20_000)).Reference;
            document.Save(source);
        }

        var keep = await fx.ConvertAsync([source], Compress());
        var drop = Compress();
        drop.Image.KeepMetadata = false;
        drop.Output.FileNamePattern = "{name}_stripped";
        var dropped = await fx.ConvertAsync([source], drop);

        AssertDone(keep);
        AssertDone(dropped);
        Assert.Contains("filee-page-xmp", Latin1(keep.Outputs.Single()));
        Assert.DoesNotContain("filee-page-xmp", Latin1(dropped.Outputs.Single()));
        Assert.DoesNotContain("filee-document-xmp", Latin1(keep.Outputs.Single()));
        Assert.DoesNotContain("filee-document-xmp", Latin1(dropped.Outputs.Single()));

        static PdfDictionary Xmp(PdfDocument document, string text)
        {
            var xmp = new PdfDictionary(document);
            xmp.Elements.SetName("/Type", "/Metadata");
            xmp.Elements.SetName("/Subtype", "/XML");
            xmp.CreateStream(System.Text.Encoding.ASCII.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><!-- {text} --></x:xmpmeta>"));
            document.Internals.AddObject(xmp);
            return xmp;
        }
    }

    [Fact]
    public async Task A_pdf_that_cant_get_smaller_is_copied_as_it_is_with_a_notice()
    {
        var dir = fx.NewFolder();
        // Compressed once already: nothing left to gain the second time.
        var once = (await fx.ConvertAsync([ReportPdf(dir)], Compress(PdfCompression.Screen))).Outputs.Single();

        var job = await fx.ConvertAsync([once], Compress(PdfCompression.Screen));

        AssertDone(job);
        Assert.Equal(File.ReadAllBytes(once), File.ReadAllBytes(job.Outputs.Single()));
        Assert.Equal("notice.pdf_not_smaller", job.Files.Single().NoticeKey);
    }

    [Fact]
    public async Task A_pdf_without_pictures_never_gets_bigger()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        var y = 72.0;
        for (var i = 0; i < 40; i++)
            y = pdf.Paragraph($"Line {i}: the quick brown fox jumps over the lazy dog.", 10, pdf.Font(), 72, y, 450, 14);
        var source = pdf.Save(Path.Combine(dir, "text.pdf"));

        var job = await fx.ConvertAsync([source], Compress(PdfCompression.Screen));

        AssertDone(job);
        Assert.True(new FileInfo(job.Outputs.Single()).Length <= new FileInfo(source).Length);
        Assert.Equal(Texts(source), Texts(job.Outputs.Single()));
    }

    [Fact]
    public async Task Password_protected_pdf_fails_with_a_clear_message()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir);
        PdfBuilder.Protect(source, "1234");

        var job = await fx.ConvertAsync([source], Compress());

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task A_pdf_locked_only_against_changes_is_compressed_like_a_split_one()
    {
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 2);
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(source);
        }

        var job = await fx.ConvertAsync([source], Compress());

        AssertDone(job);
        Assert.True(new FileInfo(job.Outputs.Single()).Length < new FileInfo(source).Length / 4);
        Assert.Equal(Texts(source), Texts(job.Outputs.Single()));
    }

    [Fact]
    public async Task Ghostscript_is_used_when_asked_and_installed()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var source = ReportPdf(dir, pages: 2);

        var job = await fx.ConvertAsync([source], Compress(PdfCompression.Screen, ghostscript: true));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length / 4);
        Assert.Equal(Texts(source).Select(Words), Texts(output).Select(Words));
        Assert.All(Pictures(output), p => Assert.True(p.Width <= 300, $"{p.Width} px"));
    }

    [Fact]
    public void Compression_adds_no_route()
    {
        // PDF → PDF stays one PDFsharp step; Ghostscript is called from it, never planned.
        var planner = fx.Catalog.CreatePlanner(["ghostscript"]);
        Assert.Equal("pdfsharp", string.Join(',', planner.Plan("pdf", "pdf")!.Steps.Select(s => s.Converter.Id)));
        Assert.Equal("pdfium", string.Join(',', planner.Plan("pdf", "png")!.Steps.Select(s => s.Converter.Id)));
        Assert.Equal("ghostscript", string.Join(',', planner.Plan("pdf", "eps")!.Steps.Select(s => s.Converter.Id)));
        Assert.Equal("docx-writer", string.Join(',', planner.Plan("pdf", "docx")!.Steps.Select(s => s.Converter.Id)));
    }

    private static string Words(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static string Latin1(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static List<PdfDictionary> ImagesOf(PdfDocument document) =>
        [.. document.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Subtype") == "/Image")];
}
