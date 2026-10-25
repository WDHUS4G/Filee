// Watermark, page numbers and password on real PDFs (PdfFinishing): the stamps are drawn where they belong, any
// language has outlines, and a locked PDF opens only with its password.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416

namespace Filee.Engines.Tests;

public class PdfFinishingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A PDF of blank A4 pages.</summary>
    private static string BlankPdf(string folder, int pages)
    {
        var path = Path.Combine(folder, "blank.pdf");
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        document.Save(path);
        return path;
    }

    /// <summary>Pixels of a rendered page that aren't white, in a box given as fractions of the page.</summary>
    private static int Marked(string pdf, int page, double left, double top, double right, double bottom, string? password = null)
    {
        using var bitmap = Conversion.ToImage(File.ReadAllBytes(pdf), page, password, new RenderOptions(Dpi: 50, BackgroundColor: SKColors.White));
        var count = 0;
        for (var y = (int)(top * bitmap.Height); y < (int)(bottom * bitmap.Height); y++)
            for (var x = (int)(left * bitmap.Width); x < (int)(right * bitmap.Width); x++)
                if (bitmap.GetPixel(x, y).Red < 245)
                    count++;
        return count;
    }

    [Fact]
    public async Task A_watermark_and_page_numbers_are_drawn_on_every_page()
    {
        var pdf = BlankPdf(fx.NewFolder(), 2);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Watermark = "CONFIDENTIAL", PageNumbers = true } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        for (var page = 0; page < 2; page++)
        {
            Assert.True(Marked(output, page, 0.3, 0.4, 0.7, 0.6) > 50, "the watermark crosses the middle");
            Assert.True(Marked(output, page, 0.4, 0.9, 0.6, 0.99) > 5, "the page number sits at the bottom");
            Assert.Equal(0, Marked(output, page, 0, 0, 0.1, 0.1)); // the corners stay clean
        }
        Assert.Equal(0, Marked(pdf, 0, 0, 0, 1, 1)); // the source is untouched
    }

    [Fact]
    public async Task A_password_locks_the_pdf()
    {
        var pdf = BlankPdf(fx.NewFolder(), 1);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Password = "filee-1234" } });

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(output, PdfDocumentOpenMode.Import).Dispose());
        using var opened = PdfReader.Open(output, "filee-1234", PdfDocumentOpenMode.Import);
        Assert.Equal(1, opened.PageCount);
        Assert.Contains("/AESV3", await File.ReadAllTextAsync(output, System.Text.Encoding.Latin1, TestContext.Current.CancellationToken));
    }

    /// <summary>One blank A4 page, shown turned by <paramref name="rotate"/> and cut to <paramref name="inset"/> points in.</summary>
    private static string TurnedPdf(string folder, int rotate, double inset = 0)
    {
        var path = Path.Combine(folder, $"turned-{rotate}-{inset}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Rotate = rotate;
        if (inset > 0)
            page.CropBox = new PdfRectangle(new XPoint(inset, inset), new XPoint(page.MediaBox.X2 - inset, page.MediaBox.Y2 - inset));
        document.Save(path);
        return path;
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Stamps_land_on_the_page_as_it_is_shown_when_it_is_turned(int rotate)
    {
        var pdf = TurnedPdf(fx.NewFolder(), rotate);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Watermark = "CONFIDENTIAL", PageNumbers = true } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        Assert.Equal(rotate, PdfReader.Open(output, PdfDocumentOpenMode.Import).Pages[0].Rotate); // still turned
        Assert.True(Marked(output, 0, 0.4, 0.9, 0.6, 0.99) > 5, "the page number sits at the bottom of the shown page");
        Assert.Equal(0, Marked(output, 0, 0, 0, 1, 0.1)); // nothing along the shown top edge
        Assert.True(Marked(output, 0, 0.3, 0.4, 0.7, 0.6) > 50, "the watermark crosses the middle");
    }

    [Fact]
    public async Task Stamps_stay_inside_a_smaller_crop_box()
    {
        var pdf = TurnedPdf(fx.NewFolder(), 0, inset: 100);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Watermark = "DRAFT", PageNumbers = true } });

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        // PDFium shows only the crop box: the number is at its bottom, the watermark in its middle.
        Assert.True(Marked(output, 0, 0.35, 0.88, 0.65, 0.99) > 5, "the page number is inside the crop box");
        Assert.True(Marked(output, 0, 0.3, 0.4, 0.7, 0.6) > 50, "the watermark is centred in the crop box");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task Stamps_land_at_the_bottom_of_a_landscape_page(int rotate)
    {
        var path = Path.Combine(fx.NewFolder(), "landscape.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(842, 595));
            page.Rotate = rotate;
            document.Save(path);
        }

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf", Pdf = { Watermark = "DRAFT", PageNumbers = true } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        Assert.True(Marked(output, 0, 0.4, 0.9, 0.6, 0.99) > 5, "the page number sits at the bottom of the shown page");
        Assert.Equal(0, Marked(output, 0, 0.4, 0, 0.6, 0.1)); // not at the top
        Assert.True(Marked(output, 0, 0.3, 0.4, 0.7, 0.6) > 50, "the watermark crosses the middle");
    }

    [Theory]
    [InlineData("pässwörd")]
    [InlineData("비밀번호입니다")]
    [InlineData("비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비비a")] // 127 UTF-8 bytes, all a reader uses
    public async Task A_non_ascii_password_opens_in_pdfium(string password)
    {
        var pdf = BlankPdf(fx.NewFolder(), 1);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Password = password, PageNumbers = true } });

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        Assert.True(Marked(output, 0, 0.4, 0.9, 0.6, 0.99, password) > 5);
        Assert.ThrowsAny<Exception>(() => Marked(output, 0, 0, 0, 1, 1, "wrong"));
    }

    [Fact]
    public async Task A_password_too_long_for_readers_fails_and_leaves_nothing_behind()
    {
        var folder = fx.NewFolder();
        var pdf = BlankPdf(folder, 1);
        var password = new string('비', 43); // 129 UTF-8 bytes

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Password = password } });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(Directory.GetFiles(folder, "*.finished", SearchOption.AllDirectories));
    }

    [Fact]
    public void Text_in_other_scripts_has_outlines()
    {
        using var glyphs = new GlyphOutlines();
        foreach (var text in new[] { "DRAFT", "기밀", "機密", "Черновик" })
        {
            using var outline = PdfFinishing.Outline(glyphs, text, 40, bold: true);
            Assert.SkipWhen(outline.IsEmpty && text != "DRAFT", $"no font for {text} on this machine");
            Assert.False(outline.IsEmpty, text);
        }
    }
}
