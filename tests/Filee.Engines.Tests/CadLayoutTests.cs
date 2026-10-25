// CAD → PDF with one page per paper-space layout (issue #172): each layout's own drawing, and model space through its
// viewports at their scale, clipped to them.

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Cad;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416

namespace Filee.Engines.Tests;

public class CadLayoutTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// Model space: a blue 100 × 100 square at the origin and a blue circle far right at (1000, 0). Two layouts on
    /// 420 × 297 paper, each with a border and one viewport: "Plan" shows the square at 1:1, "Detail" the circle at 2:1,
    /// through a view target at (900, 0) with the view centre 100 to its right.
    /// </summary>
    private static CadDocument TwoSheets()
    {
        var doc = new CadDocument();
        var drawing = new Layer("Drawing") { Color = CadBuilder.Visible };
        doc.Layers.Add(drawing);
        foreach (var (a, b) in new[] { ((0, 0), (100, 0)), ((100, 0), (100, 100)), ((100, 100), (0, 100)), ((0, 100), (0, 0)) })
            CadBuilder.Add(doc, drawing, new Line(new XYZ(a.Item1, a.Item2, 0), new XYZ(b.Item1, b.Item2, 0)));
        CadBuilder.Add(doc, drawing, new Circle(new XYZ(1000, 0, 0), 20));

        AddSheet(doc, "Plan", 1, new XY(50, 50), viewHeight: 150);
        AddSheet(doc, "Detail", 2, new XY(100, 0), viewHeight: 75, target: new XYZ(900, 0, 0));
        return doc;
    }

    private static Viewport AddSheet(CadDocument doc, string name, int tab, XY viewCenter, double viewHeight, XYZ target = default,
        bool border = true)
    {
        var layout = new Layout(name) { TabOrder = tab };
        doc.Layouts.Add(layout);
        var block = layout.AssociatedBlock;
        // The sheet border (title block) in paper space, and one 200 × 150 viewport in the middle of the sheet.
        if (border)
            block.Entities.Add(new LwPolyline(new LwPolyline.Vertex(0, 0), new LwPolyline.Vertex(420, 0), new LwPolyline.Vertex(420, 297),
                new LwPolyline.Vertex(0, 297))
            { IsClosed = true });
        var viewport = new Viewport
        {
            Center = new XYZ(210, 148.5, 0),
            Width = 200,
            Height = 150,
            ViewCenter = viewCenter,
            ViewHeight = viewHeight,
            ViewTarget = target,
        };
        block.Entities.Add(viewport);
        return viewport;
    }

    [Fact]
    public void Each_layout_with_something_on_it_is_a_sheet_in_tab_order()
    {
        var sheets = CadSceneBuilder.BuildLayouts(TwoSheets(), TestContext.Current.CancellationToken);

        Assert.Equal(["Plan", "Detail"], sheets.Select(s => s.Name));
        var detail = Assert.Single(sheets[1].Viewports);
        Assert.Equal(2, detail.Scale, 9); // 150 paper units show 75 model units
        Assert.Equal((1000.0, 0.0), (detail.ViewCenter.X, detail.ViewCenter.Y)); // target + centre
        Assert.Equal((110.0, 73.5, 310.0, 223.5), (detail.Frame.MinX, detail.Frame.MinY, detail.Frame.MaxX, detail.Frame.MaxY));
        Assert.Equal(420, sheets[0].Extents.Width, 9);

        // A drawing without layouts of its own (only the empty default one) keeps its single page.
        Assert.Empty(CadSceneBuilder.BuildLayouts(CadBuilder.Sample(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pdf_has_one_page_per_layout_with_model_space_in_the_viewports()
    {
        var dxf = CadBuilder.SaveDxf(TwoSheets(), fx.NewFolder(), "sheets.dxf");

        var job = await fx.ConvertAsync([dxf], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var pdf = job.Outputs.Single();
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            Assert.Equal(2, document.PageCount);

        // Blue model-space lines show up inside the viewport (the middle of the sheet) on both pages, and nowhere
        // outside it: the square's far corner and the circle are clipped to their frames.
        for (var page = 0; page < 2; page++)
        {
            Assert.True(Blue(pdf, page, 0.27, 0.26, 0.73, 0.74) > 20, $"page {page + 1}: model space in the viewport");
            Assert.Equal(0, Blue(pdf, page, 0.02, 0.02, 0.25, 0.24)); // outside the frame, only the black border
        }
    }

    [Fact]
    public void A_viewport_that_is_off_is_skipped()
    {
        var doc = TwoSheets();
        AddSheet(doc, "Off", 3, new XY(50, 50), viewHeight: 150).Status |= ViewportStatusFlags.ViewportOff;

        var sheets = CadSceneBuilder.BuildLayouts(doc, TestContext.Current.CancellationToken);

        Assert.Equal(["Plan", "Detail", "Off"], sheets.Select(s => s.Name));
        Assert.Empty(sheets[2].Viewports); // the border is still a page
    }

    [Fact]
    public async Task A_drawing_made_in_model_space_keeps_one_page_of_all_of_it()
    {
        // Layout1 holds only the viewport AutoCAD added when its tab was first opened, with a view of the square alone.
        var doc = CadBuilder.Sample();
        AddSheet(doc, "Sheet", 1, new XY(50, 50), viewHeight: 150, border: false);
        Assert.Empty(CadSceneBuilder.BuildLayouts(doc, TestContext.Current.CancellationToken));

        var dxf = CadBuilder.SaveDxf(doc, fx.NewFolder(), "model.dxf");
        var job = await fx.ConvertAsync([dxf], new Preset { TargetFormat = "pdf" });

        Assert.Equal(JobState.Completed, job.State);
        using var document = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task Empty_model_space_shows_nothing_through_the_viewports()
    {
        // Model space is empty, so the one-picture scene is the active layout's paper space; that must not be drawn
        // again inside the viewport. The blue border runs along the sheet edges, and a view of model space around
        // (50, 50) would put its corner in the middle of the frame.
        var doc = new CadDocument();
        var border = new Layer("Border") { Color = CadBuilder.Visible };
        doc.Layers.Add(border);
        doc.PaperSpace.Entities.Add(new LwPolyline(new LwPolyline.Vertex(0, 0), new LwPolyline.Vertex(420, 0),
            new LwPolyline.Vertex(420, 297), new LwPolyline.Vertex(0, 297))
        { IsClosed = true, Layer = border });
        doc.PaperSpace.Entities.Add(new Viewport
        {
            Center = new XYZ(210, 148.5, 0),
            Width = 200,
            Height = 150,
            ViewCenter = new XY(50, 50),
            ViewHeight = 150,
        });
        var dxf = CadBuilder.SaveDxf(doc, fx.NewFolder(), "paper.dxf");

        var job = await fx.ConvertAsync([dxf], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var pdf = job.Outputs.Single();
        Assert.True(Blue(pdf, 0, 0, 0, 1, 1) > 20, "the border is on the page");
        Assert.Equal(0, Blue(pdf, 0, 0.27, 0.26, 0.73, 0.74));
    }

    [Fact]
    public async Task Images_and_svg_stay_one_picture_of_model_space()
    {
        var dxf = CadBuilder.SaveDxf(TwoSheets(), fx.NewFolder(), "sheets.dxf");

        var job = await fx.ConvertAsync([dxf], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Single(job.Outputs);
    }

    /// <summary>Blue pixels of a rendered page in a box given as fractions of the page.</summary>
    private static int Blue(string pdf, int page, double left, double top, double right, double bottom)
    {
        using var bitmap = Conversion.ToImage(File.ReadAllBytes(pdf), page, null, new RenderOptions(Dpi: 40, BackgroundColor: SKColors.White));
        var count = 0;
        for (var y = (int)(top * bitmap.Height); y < (int)(bottom * bitmap.Height); y++)
            for (var x = (int)(left * bitmap.Width); x < (int)(right * bitmap.Width); x++)
                if (bitmap.GetPixel(x, y) is { Blue: > 150, Red: < 100 })
                    count++;
        return count;
    }
}
