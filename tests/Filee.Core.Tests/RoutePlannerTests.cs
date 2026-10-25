using Filee.Core.Conversion;

namespace Filee.Core.Tests;

public class RoutePlannerTests
{
    [Fact]
    public void Direct_edge_is_used()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        var route = new RoutePlanner([magick]).Plan("jpg", "png");

        Assert.NotNull(route);
        Assert.Single(route.Steps);
        Assert.Same(magick, route.Steps[0].Converter);
    }

    [Fact]
    public void Two_step_route_is_found()
    {
        var rhwp = new FakeConverter("rhwp", new ConversionEdge("hwpx", "pdf"));
        var pdfium = new FakeConverter("pdfium", new ConversionEdge("pdf", "png"));

        var route = new RoutePlanner([rhwp, pdfium]).Plan("hwpx", "png");

        Assert.NotNull(route);
        Assert.Equal(["hwpx→pdf", "pdf→png"], route.Steps.Select(s => $"{s.From}→{s.To}"));
    }

    [Fact]
    public void Edges_for_image_sources_only_are_skipped_for_other_sources()
    {
        var tracer = new FakeConverter("trace", new ConversionEdge("png", "svg", 40, ImageSourcesOnly: true));
        var render = new FakeConverter("render", new ConversionEdge("pdf", "png"), new ConversionEdge("icns", "png"));
        var planner = new RoutePlanner([tracer, render]);

        Assert.Equal(["png→svg"], planner.Plan("png", "svg")!.Steps.Select(s => $"{s.From}→{s.To}"));
        Assert.Equal(["icns→png", "png→svg"], planner.Plan("icns", "svg")!.Steps.Select(s => $"{s.From}→{s.To}")); // a raster image too
        Assert.Null(planner.Plan("pdf", "svg")); // a document rendered to PNG is never traced
    }

    [Fact]
    public void Edges_not_for_image_sources_leave_images_to_tracing()
    {
        // LibreOffice's PDF → SVG keeps a PDF's vectors, but a photo through PDF would only be a picture in the SVG.
        var tracer = new FakeConverter("trace", new ConversionEdge("png", "svg", 40, ImageSourcesOnly: true));
        var pdf = new FakeConverter("pdf", new ConversionEdge("png", "pdf"), new ConversionEdge("pdf", "svg", 25, NotForImageSources: true));
        var planner = new RoutePlanner([tracer, pdf]);

        Assert.Equal(["png→svg"], planner.Plan("png", "svg")!.Steps.Select(s => $"{s.From}→{s.To}"));
        Assert.Equal(["pdf→svg"], planner.Plan("pdf", "svg")!.Steps.Select(s => $"{s.From}→{s.To}"));
    }

    [Fact]
    public void Unshaped_edges_are_left_out_for_text_that_needs_shaping()
    {
        // rhwp draws Arabic and Indic text unshaped: such a document goes through LibreOffice, though it costs more.
        var writer = new FakeConverter("hwpx-writer", new ConversionEdge("docx", "hwpx"));
        var rhwp = new FakeConverter("rhwp", new ConversionEdge("hwpx", "pdf", Unshaped: true));
        var office = new FakeConverter("libreoffice", new ConversionEdge("docx", "pdf", 25));
        var planner = new RoutePlanner([writer, rhwp, office]);

        var usual = planner.Plan("docx", "pdf")!;
        Assert.Equal(["hwpx-writer", "rhwp"], usual.Steps.Select(s => s.Converter.Id));
        Assert.Equal([false, true], usual.Steps.Select(s => s.Unshaped));
        Assert.Equal(["libreoffice"], planner.Plan("docx", "pdf", shaping: true)!.Steps.Select(s => s.Converter.Id));
        Assert.Null(new RoutePlanner([writer, rhwp]).Plan("docx", "pdf", shaping: true));
    }

    [Fact]
    public void Direct_only_edges_are_never_a_step_of_a_longer_route()
    {
        // PDF → PPTX puts pictures of the pages on slides: only for a PDF the user asked to turn into PPTX.
        var slides = new FakeConverter("slides", new ConversionEdge("pdf", "pptx", DirectOnly: true));
        var word = new FakeConverter("word", new ConversionEdge("docx", "pdf"));
        var office = new FakeConverter("office", new ConversionEdge("pptx", "odp"), new ConversionEdge("pptx", "ppsx"));
        var planner = new RoutePlanner([slides, word, office]);

        Assert.Equal(["pdf→pptx"], planner.Plan("pdf", "pptx")!.Steps.Select(s => $"{s.From}→{s.To}"));
        Assert.Null(planner.Plan("docx", "pptx")); // not after another step
        Assert.Null(planner.Plan("pdf", "odp")); // nor before one
        Assert.Null(planner.Plan("pdf", "ppsx"));
        Assert.Equal(["pptx→odp"], planner.Plan("pptx", "odp")!.Steps.Select(s => $"{s.From}→{s.To}"));
    }

    [Fact]
    public void Frame_sequence_edges_are_a_route_of_their_own_only_when_asked_for()
    {
        var ffmpeg = new FakeConverter("ffmpeg", new ConversionEdge("mp4", "png"),
            new ConversionEdge("mp4", "png", FrameSequence: true), new ConversionEdge("mp4", "jpg", FrameSequence: true));
        var magick = new FakeConverter("magick", new ConversionEdge("png", "jpg"), new ConversionEdge("png", "webp"));
        var planner = new RoutePlanner([ffmpeg, magick]);

        // Ordinary routes: one still frame, then the image engine. The frame edge to JPG is never taken.
        var still = planner.Plan("mp4", "jpg")!;
        Assert.Equal(["mp4→png", "png→jpg"], still.Steps.Select(s => $"{s.From}→{s.To}"));
        Assert.DoesNotContain(still.Steps, s => s.FrameSequence);
        Assert.False(planner.Plan("mp4", "png")!.Steps.Single().FrameSequence);

        // Frames: the frame edge alone, never as a step towards another format.
        var frames = planner.Plan("mp4", "jpg", frames: true)!;
        Assert.Equal(("mp4", "jpg", true), (frames.Steps.Single().From, frames.Steps.Single().To, frames.Steps.Single().FrameSequence));
        Assert.True(planner.Plan("mp4", "png", frames: true)!.Steps.Single().FrameSequence);
        Assert.Null(planner.Plan("mp4", "webp", frames: true));
        Assert.Null(planner.Plan("png", "jpg", frames: true));
        Assert.False(planner.CanConvert("mov", "jpg", frames: true));
    }

    [Fact]
    public void Cheaper_route_wins()
    {
        var slow = new FakeConverter("slow", new ConversionEdge("docx", "pdf", 30));
        var fast = new FakeConverter("fast", new ConversionEdge("docx", "pdf", 10));

        var route = new RoutePlanner([slow, fast]).Plan("docx", "pdf");

        Assert.Equal("fast", route!.Steps[0].Converter.Id);
    }

    [Fact]
    public void Priority_breaks_ties()
    {
        var word = new FakeConverter("word", new ConversionEdge("docx", "pdf"));
        var libre = new FakeConverter("libreoffice", new ConversionEdge("docx", "pdf"));

        var route = new RoutePlanner([word, libre], c => c.Id == "word" ? 5 : 0).Plan("docx", "pdf");

        Assert.Equal("libreoffice", route!.Steps[0].Converter.Id);
    }

    [Fact]
    public void Same_format_requires_self_edge()
    {
        var withSelf = new FakeConverter("magick", new ConversionEdge("jpg", "jpg"));
        var withoutSelf = new FakeConverter("other", new ConversionEdge("jpg", "png"), new ConversionEdge("png", "jpg"));

        Assert.NotNull(new RoutePlanner([withSelf]).Plan("jpg", "jpg"));
        Assert.Null(new RoutePlanner([withoutSelf]).Plan("jpg", "jpg"));
    }

    [Fact]
    public void Impossible_conversion_returns_null()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        Assert.Null(new RoutePlanner([magick]).Plan("docx", "png"));
    }

    [Fact]
    public void Routes_longer_than_max_steps_are_rejected()
    {
        var chain = new FakeConverter("chain",
            new ConversionEdge("a", "b"), new ConversionEdge("b", "c"),
            new ConversionEdge("c", "d"), new ConversionEdge("d", "e"));

        Assert.NotNull(new RoutePlanner([chain]).Plan("a", "d"));
        Assert.Null(new RoutePlanner([chain]).Plan("a", "e"));
    }

    [Fact]
    public void Catalog_excludes_unavailable_engines()
    {
        var missing = new FakeConverter("missing", new ConversionEdge("docx", "hwpx")) { Available = false };
        var catalog = new ConverterCatalog([missing]);

        Assert.False(catalog.CreatePlanner().CanConvert("docx", "hwpx"));
    }

    [Fact]
    public void A_planner_can_assume_an_optional_engine_is_installed()
    {
        var office = new FakeConverter("office", new ConversionEdge("xlsx", "pdf")) { Available = false };
        var images = new FakeConverter("images", new ConversionEdge("pdf", "png"));
        var catalog = new ConverterCatalog([office, images]);

        Assert.False(catalog.CreatePlanner().CanConvert("xlsx", "png"));
        Assert.True(catalog.CreatePlanner(["office"]).CanConvert("xlsx", "png"));
    }
}
