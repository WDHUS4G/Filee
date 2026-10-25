// PDF → PPTX with a picture of each page on its own slide: slide count and size, pictures fitted and centred, the page
// range, the speaker notes, the Open XML SDK validator, Filee's own PPTX reader as a second reader, clear failures, and
// routes: the picture deck is reached from a PDF only and is never a step towards another format.
// The test PDFs are generated (PdfPig's writer, PDFsharp for rotation and passwords) and use the standard 14 fonts.

using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf.IO;
using P = DocumentFormat.OpenXml.Presentation;

namespace Filee.Engines.Tests;

public class PdfSlidesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const double A4Width = 595, A4Height = 842;

    /// <summary>A PDF with one page per size, each with "Page n" and a line of text on it.</summary>
    private static string MakePdf(string folder, params (double Width, double Height)[] pages)
    {
        var pdf = new PdfBuilder();
        for (var i = 0; i < pages.Length; i++)
        {
            pdf.NewPage(pages[i].Width, pages[i].Height);
            pdf.Line($"Page {i + 1}", 18, pdf.Font(bold: true), 40, 60);
            pdf.Line($"Text on page number {i + 1} of the deck.", 11, pdf.Font(), 40, 90);
        }
        return pdf.Save(Path.Combine(folder, "deck.pdf"));
    }

    private async Task<ConversionJob> RunAsync(string input, Preset? preset = null) =>
        await fx.ConvertAsync([input], preset ?? new Preset { TargetFormat = "pptx" });

    private async Task<string> ConvertAsync(string input, Preset? preset = null)
    {
        var job = await RunAsync(input, preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        Assert.Equal(".pptx", Path.GetExtension(output));
        return output;
    }

    /// <summary>What a slide shows: its picture's name, place and pixel size, and the speaker notes.</summary>
    private sealed record Slide(string Name, long X, long Y, long Width, long Height, int PixelWidth, int PixelHeight, string Notes);

    private static (long Width, long Height, List<Slide> Slides) Read(string pptx)
    {
        using var document = PresentationDocument.Open(pptx, false);
        var presentation = document.PresentationPart!;
        var size = presentation.Presentation!.SlideSize!;
        var slides = new List<Slide>();
        foreach (var id in presentation.Presentation.SlideIdList!.Elements<P.SlideId>())
        {
            var part = (SlidePart)presentation.GetPartById(id.RelationshipId!.Value!);
            var picture = Assert.Single(part.Slide!.Descendants<P.Picture>());
            Assert.Empty(part.Slide!.Descendants<P.Shape>()); // nothing but the page
            var image = (ImagePart)part.GetPartById(picture.BlipFill!.Blip!.Embed!.Value!);
            Assert.Equal("image/jpeg", image.ContentType);
            MagickImageInfo info;
            using (var stream = image.GetStream())
                info = new MagickImageInfo(stream);
            Assert.Equal(MagickFormat.Jpeg, info.Format);
            var xfrm = picture.ShapeProperties!.Transform2D!;
            slides.Add(new Slide(picture.NonVisualPictureProperties!.NonVisualDrawingProperties!.Name!.Value!,
                xfrm.Offset!.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents!.Cx!.Value, xfrm.Extents.Cy!.Value,
                (int)info.Width, (int)info.Height, part.NotesSlidePart?.NotesSlide?.InnerText ?? ""));
        }
        return (size.Cx!.Value, size.Cy!.Value, slides);
    }

    /// <summary>
    /// No schema or semantic error for PowerPoint (Open XML SDK), every part has a content type and every relationship
    /// a target.
    /// </summary>
    private static void ValidPackage(string pptx)
    {
        using (var document = PresentationDocument.Open(pptx, false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
                .ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var zip = ZipFile.OpenRead(pptx);
        var contentTypes = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open()).Root!;
        var overrides = contentTypes.Elements(types + "Override").Select(o => ((string)o.Attribute("PartName")!).TrimStart('/')).ToHashSet();
        var defaults = contentTypes.Elements(types + "Default").Select(d => (string)d.Attribute("Extension")!).ToHashSet();
        foreach (var entry in zip.Entries.Where(e => e.FullName != "[Content_Types].xml"))
            Assert.True(overrides.Contains(entry.FullName) || defaults.Contains(Path.GetExtension(entry.FullName).TrimStart('.')), $"{entry.FullName} has no content type");
        Assert.All(overrides, part => Assert.NotNull(zip.GetEntry(part)));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = entry.FullName[..entry.FullName.IndexOf("_rels/", StringComparison.Ordinal)];
            foreach (var target in XDocument.Load(entry.Open()).Root!.Elements(rels + "Relationship").Select(r => (string)r.Attribute("Target")!))
            {
                var resolved = new Uri(new Uri("pptx:///" + folder), target).AbsolutePath.TrimStart('/');
                Assert.True(zip.GetEntry(resolved) is not null, $"{entry.FullName} → {target}");
            }
        }
    }

    [Fact]
    public async Task Every_page_becomes_a_picture_slide_shaped_like_the_first_page()
    {
        var pptx = await ConvertAsync(MakePdf(fx.NewFolder(), (A4Width, A4Height), (A4Width, A4Height), (A4Width, A4Height)));

        ValidPackage(pptx);
        var (width, height, slides) = Read(pptx);
        // A4 portrait: the slide is the page's own size (points × 12700 EMU).
        Assert.Equal((7556500, 10693400), (width, height));
        Assert.Equal(["Page 1", "Page 2", "Page 3"], slides.Select(s => s.Name));
        Assert.All(slides, s => Assert.Equal((0, 0, width, height), (s.X, s.Y, s.Width, s.Height)));
        // 150 dpi (the default): 595 pt = 8.26 in → about 1240 px.
        Assert.All(slides, s => Assert.InRange(s.PixelWidth, 1238, 1241));
        Assert.All(slides, s => Assert.Equal(A4Height / A4Width, (double)s.PixelHeight / s.PixelWidth, 2));
        // The page's text is in the speaker notes, so it can be searched and copied.
        for (var i = 0; i < slides.Count; i++)
        {
            Assert.Contains($"Page {i + 1}", slides[i].Notes);
            Assert.Contains($"Text on page number {i + 1} of the deck.", slides[i].Notes);
        }
    }

    [Fact]
    public async Task Pages_of_another_size_or_orientation_are_fitted_and_centred()
    {
        // A 16:9 first page (PowerPoint's widescreen size, 13.33 × 7.5 in) then an A4 portrait page.
        var pptx = await ConvertAsync(MakePdf(fx.NewFolder(), (960, 540), (A4Width, A4Height)));

        ValidPackage(pptx);
        var (width, height, slides) = Read(pptx);
        Assert.Equal((12192000, 6858000), (width, height));
        Assert.Equal((0, 0, width, height), (slides[0].X, slides[0].Y, slides[0].Width, slides[0].Height));
        // The portrait page is as tall as the slide and centred across it.
        var portrait = slides[1];
        Assert.Equal((0, height), (portrait.Y, portrait.Height));
        Assert.Equal(height * A4Width / A4Height, portrait.Width, 1.0);
        Assert.Equal((width - portrait.Width) / 2, portrait.X);
    }

    [Fact]
    public async Task A_page_turned_in_the_pdf_is_shown_turned()
    {
        var folder = fx.NewFolder();
        var path = MakePdf(folder, (A4Width, A4Height), (A4Width, A4Height));
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Rotate = 90; // shown landscape
            document.Save(path);
        }

        var (width, height, slides) = Read(await ConvertAsync(path));

        // The turned first page makes landscape slides; the upright second page is fitted onto one, centred.
        Assert.Equal((10693400, 7556500), (width, height));
        Assert.True(slides[0].PixelWidth > slides[0].PixelHeight);
        Assert.Equal((0, height), (slides[1].Y, slides[1].Height));
        Assert.Equal((width - slides[1].Width) / 2, slides[1].X);
    }

    [Fact]
    public async Task The_page_range_picks_the_slides_and_the_resolution_is_at_least_150_dpi()
    {
        var path = MakePdf(fx.NewFolder(), (A4Width, A4Height), (A4Width, A4Height), (A4Width, A4Height), (A4Width, A4Height));
        var preset = new Preset { TargetFormat = "pptx", Pdf = { PageRange = "2-3", RenderDpi = 72 } };

        var pptx = await ConvertAsync(path, preset);

        ValidPackage(pptx);
        var (_, _, slides) = Read(pptx);
        Assert.Equal(["Page 2", "Page 3"], slides.Select(s => s.Name));
        Assert.Contains("Text on page number 2 of the deck.", slides[0].Notes);
        Assert.Contains("Text on page number 3 of the deck.", slides[1].Notes);
        Assert.All(slides, s => Assert.InRange(s.PixelWidth, 1238, 1241)); // 72 dpi asked, 150 used
    }

    [Fact]
    public async Task Filees_own_pptx_reader_sees_a_picture_on_each_slide()
    {
        var folder = fx.NewFolder();
        var pptx = await ConvertAsync(MakePdf(folder, (960, 540), (960, 540)));

        var document = PptxReader.Read(pptx, Path.Combine(folder, "media"));

        var section = Assert.Single(document.Sections);
        Assert.Equal(16.0 / 9, (double)section.Page!.Width / section.Page.Height, 3);
        var pages = section.Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Single(page.Inlines.OfType<HImage>()));
    }

    [Fact]
    public async Task A_scan_without_text_has_no_notes()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(PdfBuilder.Photo(folder, "scan.jpg", MagickFormat.Jpeg, 300, 400), 0, 0, A4Width, A4Height);
        var path = pdf.Save(Path.Combine(folder, "scan.pdf"));

        var pptx = await ConvertAsync(path);

        ValidPackage(pptx);
        Assert.Equal("", Assert.Single(Read(pptx).Slides).Notes);
        using var zip = ZipFile.OpenRead(pptx);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("notes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Password_protected_pdf_fails_with_a_clear_message()
    {
        var folder = fx.NewFolder();
        var path = MakePdf(folder, (A4Width, A4Height));
        PdfBuilder.Protect(path, "1234");

        var job = await RunAsync(path);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("password", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(folder, "*.pptx*"));
    }

    [Fact]
    public async Task A_broken_pdf_fails_and_leaves_no_file()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "broken.pdf");
        await File.WriteAllTextAsync(path, "%PDF-1.7\nthis is not really a PDF\n", TestContext.Current.CancellationToken);

        var job = await RunAsync(path);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(Directory.GetFiles(folder, "*.pptx*"));
    }

    [Theory]
    [InlineData(A4Width, A4Height, 7556500, 10693400)]
    [InlineData(960, 540, 12192000, 6858000)]
    [InlineData(10000, 5000, PictureDeckWriter.MaxSlideSide, PictureDeckWriter.MaxSlideSide / 2)] // larger than 56 in
    [InlineData(36, 18, PictureDeckWriter.MinSlideSide * 2, PictureDeckWriter.MinSlideSide)] // smaller than 1 in
    public void Slides_take_the_page_size_within_powerpoints_limits(double width, double height, long slideWidth, long slideHeight) =>
        Assert.Equal((slideWidth, slideHeight), PictureDeckWriter.SlideSize(width, height));

    [Fact]
    public void Huge_pages_render_at_a_lower_resolution()
    {
        Assert.Equal(300, PdfSlidesConverter.PageDpi(A4Width, A4Height, 300));
        // A0 (841 × 1189 mm) at 300 dpi would be 139 megapixels.
        var dpi = PdfSlidesConverter.PageDpi(2384, 3370, 300);
        Assert.InRange(dpi, 100, 299);
        Assert.True(2384 / 72.0 * dpi * (3370 / 72.0 * dpi) <= PdfSlidesConverter.MaxPagePixels);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Picture_slides_come_from_a_pdf_only_and_leave_every_other_route_as_it_was()
    {
        var everyEngine = fx.Converters.Select(c => c.Id).ToArray();
        var with = fx.Catalog.CreatePlanner(everyEngine);
        var without = new ConverterCatalog(fx.Converters.Where(c => c is not PdfSlidesConverter)) { Priority = fx.Catalog.Priority }
            .CreatePlanner(everyEngine);

        Assert.Equal(["pdf-slides"], with.Plan("pdf", "pptx")!.Steps.Select(s => s.Converter.Id));

        var formats = FormatRegistry.Known.Select(f => f.Id).ToList();
        var checkedRoutes = 0;
        foreach (var from in formats)
        {
            foreach (var to in formats.Where(t => t != from && (from, t) != ("pdf", "pptx")))
            {
                var route = with.Plan(from, to);
                var before = without.Plan(from, to);
                Assert.False(route?.Steps.Any(s => s.Converter is PdfSlidesConverter) ?? false,
                    $"{from} → {to} goes through picture slides: {string.Join(" → ", route?.Steps.Select(s => s.To) ?? [])}");
                Assert.Equal(before is null, route is null);
                if (before is null)
                    continue;
                Assert.Equal(before.Cost, route!.Cost);
                Assert.Equal(before.Steps.Select(s => (s.Converter.Id, s.To)), route.Steps.Select(s => (s.Converter.Id, s.To)));
                checkedRoutes++;
            }
        }
        Assert.True(checkedRoutes > 1000);

        // Presentations keep their own routes to PDF and images (PPTX → PDF is not changed by a PDF → PPTX edge).
        Assert.DoesNotContain(with.Plan("pptx", "pdf")!.Steps, s => s.Converter is PdfSlidesConverter);
    }
}
