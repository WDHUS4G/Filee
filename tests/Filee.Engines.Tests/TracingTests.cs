// Raster → SVG tracing and SVG → DXF / DWG. Pictures are drawn with SkiaSharp in the tests; the traced SVG is rendered
// back with Svg.Skia and compared with the input pixel by pixel, and DXF output is read back with ACadSharp.

using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using Filee.Engines.Vector.Tracing;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class TracingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private async Task<string> ConvertAsync(string input, Preset preset)
    {
        var job = await fx.ConvertAsync([input], preset);
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        return job.Outputs.Single();
    }

    private static Preset LineArt(int detail = 50) =>
        new() { Name = "Line art", TargetFormat = "svg", Trace = { Mode = TracingMode.BlackAndWhite, Detail = detail } };

    private static Preset Colors(int colors, int detail = 50) =>
        new() { Name = "Trace", TargetFormat = "svg", Trace = { Mode = TracingMode.Color, Colors = colors, Detail = detail } };

    /// <summary>Draws a picture with SkiaSharp and saves it as PNG (or, through ImageMagick, another format).</summary>
    private static string Draw(string folder, string name, int width, int height, SKColor background, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            draw(canvas);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = Path.Combine(folder, Path.ChangeExtension(name, ".png"));
        File.WriteAllBytes(png, data.ToArray());
        if (Path.GetExtension(name) is ".png")
            return png;
        var path = Path.Combine(folder, name);
        using (var image = new MagickImage(png))
            image.Write(path);
        File.Delete(png);
        return path;
    }

    private static SKPaint Fill(SKColor color, bool antialias = true) => new() { Color = color, IsAntialias = antialias, Style = SKPaintStyle.Fill };

    /// <summary>Line art: a disc, a rectangle, a ring (with a hole), a thin bar and a slanted bar.</summary>
    private static void DrawLineArt(SKCanvas canvas)
    {
        using var black = Fill(SKColors.Black);
        canvas.DrawCircle(80, 80, 55, black);
        canvas.DrawRect(170, 30, 120, 90, black);
        canvas.DrawCircle(90, 220, 60, black);
        using (var white = Fill(SKColors.White))
            canvas.DrawCircle(90, 220, 30, white);
        canvas.DrawRect(180, 170, 180, 3, black);
        canvas.Save();
        canvas.RotateDegrees(-25, 270, 240);
        canvas.DrawRect(200, 230, 140, 14, black);
        canvas.Restore();
    }

    /// <summary>RGBA pixels of a picture file drawn over <paramref name="background"/>.</summary>
    private static byte[] Pixels(string path, int width, int height, SKColor background)
    {
        using var source = SKBitmap.Decode(path) ?? SKBitmap.Decode(new MagickImage(path).ToByteArray(MagickFormat.Png));
        Assert.NotNull(source);
        return Render(width, height, background, canvas => canvas.DrawBitmap(source, new SKRect(0, 0, width, height)));
    }

    /// <summary>RGBA pixels of an SVG rendered at <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static byte[] RenderSvg(string path, int width, int height, SKColor background)
    {
        using var drawing = SvgRenderer.Load(path);
        return Render(width, height, background, canvas =>
        {
            canvas.Scale(width / drawing.Bounds.Width, height / drawing.Bounds.Height);
            canvas.Translate(-drawing.Bounds.Left, -drawing.Bounds.Top);
            canvas.DrawPicture(drawing.Picture);
        });
    }

    private static byte[] Render(int width, int height, SKColor background, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            draw(canvas);
        }
        return bitmap.GetPixelSpan().ToArray();
    }

    /// <summary>Share of pixels whose colour differs by more than <paramref name="tolerance"/> in some channel.</summary>
    private static double Difference(byte[] a, byte[] b, int tolerance = 64)
    {
        Assert.Equal(a.Length, b.Length);
        var different = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > tolerance || Math.Abs(a[i + 1] - b[i + 1]) > tolerance
                || Math.Abs(a[i + 2] - b[i + 2]) > tolerance || Math.Abs(a[i + 3] - b[i + 3]) > tolerance)
                different++;
        }
        return different / (a.Length / 4.0);
    }

    private static XElement[] PathsOf(string svg) => XDocument.Load(svg).Descendants(Svg + "path").ToArray();

    private static int SubpathsOf(XElement path) => path.Attribute("d")!.Value.Count(c => c == 'M');

    // ───────────────────────── Black and white ─────────────────────────

    [Fact]
    public async Task Line_art_becomes_one_black_path_that_looks_like_the_picture()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "logo.png", 400, 300, SKColors.White, DrawLineArt);

        var svg = await ConvertAsync(png, LineArt());

        var root = XDocument.Load(svg).Root!;
        Assert.Equal(("400", "300", "0 0 400 300"), (root.Attribute("width")?.Value, root.Attribute("height")?.Value, root.Attribute("viewBox")?.Value));
        var path = Assert.Single(PathsOf(svg));
        Assert.Equal("#000000", path.Attribute("fill")?.Value);
        // Disc, rectangle, ring outside + inside, thin bar, slanted bar.
        Assert.Equal(6, SubpathsOf(path));
        var data = path.Attribute("d")!.Value;
        Assert.Contains('C', data); // curves for the round shapes
        Assert.Contains('L', data); // straight lines for the rectangle

        var difference = Difference(Pixels(png, 400, 300, SKColors.White), RenderSvg(svg, 400, 300, SKColors.White));
        Assert.True(difference < 0.01, $"{difference:P2} of the pixels differ");
    }

    [Fact]
    public async Task Line_art_drops_specks_unless_the_detail_is_highest()
    {
        var dir = fx.NewFolder();
        var specks = 0;
        var png = Draw(dir, "scan.png", 300, 200, SKColors.White, canvas =>
        {
            using var black = Fill(SKColors.Black, antialias: false);
            canvas.DrawRect(50, 50, 200, 100, black);
            var random = new Random(7);
            for (var i = 0; i < 40; i++)
            {
                var x = random.Next(0, 300);
                var y = random.Next(0, 200);
                if (x is >= 45 and <= 255 && y is >= 45 and <= 155)
                    continue;
                canvas.DrawRect(x, y, 2, 1, black); // dust: 2 pixels each
                specks++;
            }
        });

        var cleaned = PathsOf(await ConvertAsync(png, LineArt()));
        var everything = PathsOf(await ConvertAsync(png, LineArt(detail: 100).Also(p => p.Output.FileNamePattern = "{name}_all")));

        Assert.Equal(1, SubpathsOf(Assert.Single(cleaned)));
        Assert.InRange(SubpathsOf(Assert.Single(everything)), 1 + specks * 0.8, 1 + specks);
    }

    [Fact]
    public async Task Line_art_on_a_transparent_background_traces_the_opaque_shapes()
    {
        var dir = fx.NewFolder();
        // A white logo on nothing: over white paper it would vanish, so the opaque pixels are the ink.
        var png = Draw(dir, "white-logo.png", 200, 200, SKColors.Transparent, canvas =>
        {
            using var white = Fill(SKColors.White);
            canvas.DrawCircle(100, 100, 70, white);
        });

        var path = Assert.Single(PathsOf(await ConvertAsync(png, LineArt())));
        Assert.Equal(1, SubpathsOf(path));
    }

    // ───────────────────────── Colour ─────────────────────────

    private static void DrawIllustration(SKCanvas canvas)
    {
        using var red = Fill(new SKColor(0xE5, 0x39, 0x35));
        using var blue = Fill(new SKColor(0x1E, 0x63, 0xC8));
        using var green = Fill(new SKColor(0x2E, 0x9D, 0x4F));
        using var yellow = Fill(new SKColor(0xF9, 0xC8, 0x2B));
        canvas.DrawCircle(110, 110, 80, red);
        canvas.DrawRect(150, 120, 120, 110, blue);
        using (var triangle = new SKPath())
        {
            triangle.MoveTo(40, 280);
            triangle.LineTo(120, 170);
            triangle.LineTo(200, 280);
            triangle.Close();
            canvas.DrawPath(triangle, green);
        }
        canvas.DrawCircle(210, 175, 25, yellow);
    }

    [Fact]
    public async Task Colour_tracing_stacks_one_path_per_colour_without_gaps()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "illustration.png", 300, 300, SKColors.White, DrawIllustration);

        var svg = await ConvertAsync(png, Colors(8));

        var paths = PathsOf(svg);
        // White, red, blue, green, yellow: the anti-aliased edges don't get colours of their own.
        Assert.Equal(5, paths.Length);
        Assert.Equal("#ffffff", paths[0].Attribute("fill")?.Value); // the background at the bottom
        Assert.Equal(1, SubpathsOf(paths[0]));

        var expected = Pixels(png, 300, 300, SKColors.White);
        Assert.True(Difference(expected, RenderSvg(svg, 300, 300, SKColors.White)) < 0.02);
        // Drawn over magenta, no magenta shows: lower shapes run under the upper ones.
        var overMagenta = RenderSvg(svg, 300, 300, SKColors.Magenta);
        Assert.True(Difference(expected, overMagenta) < 0.02);
        var magenta = 0;
        for (var i = 0; i < overMagenta.Length; i += 4)
            if (overMagenta[i] > 200 && overMagenta[i + 1] < 60 && overMagenta[i + 2] > 200)
                magenta++;
        Assert.True(magenta < 30, $"{magenta} magenta pixels");
    }

    [Fact]
    public async Task Colour_tracing_reduces_to_the_colour_count()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "gradient.png", 256, 64, SKColors.White, canvas =>
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 0), [SKColors.Black, SKColors.White], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 256, 64, paint);
        });

        var paths = PathsOf(await ConvertAsync(png, Colors(4)));

        Assert.Equal(4, paths.Length);
        Assert.Equal(4, paths.Select(p => p.Attribute("fill")!.Value).Distinct().Count());
    }

    [Fact]
    public async Task Transparent_areas_stay_transparent_in_colour_tracing()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "sticker.png", 200, 200, SKColors.Transparent, canvas =>
        {
            using var red = Fill(SKColors.Red);
            canvas.DrawCircle(100, 100, 60, red);
        });

        var svg = await ConvertAsync(png, Colors(8));

        var path = Assert.Single(PathsOf(svg));
        Assert.Equal("#ff0000", path.Attribute("fill")?.Value);
        var rendered = RenderSvg(svg, 200, 200, SKColors.Transparent);
        Assert.Equal(0, rendered[(10 * 200 + 10) * 4 + 3]); // corner
        Assert.Equal(255, rendered[(100 * 200 + 100) * 4 + 3]); // centre
    }

    // ───────────────────────── Inputs and size ─────────────────────────

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("tiff")]
    public async Task Common_image_formats_are_traced(string format)
    {
        var dir = fx.NewFolder();
        var input = Draw(dir, "art." + format, 300, 300, SKColors.White, DrawIllustration);

        var svg = await ConvertAsync(input, Colors(8));

        Assert.Equal(".svg", Path.GetExtension(svg));
        Assert.InRange(PathsOf(svg).Length, 4, 8);
        var difference = Difference(Pixels(input, 300, 300, SKColors.White), RenderSvg(svg, 300, 300, SKColors.White));
        Assert.True(difference < 0.03, $"{difference:P2} of the pixels differ");
    }

    [Fact]
    public async Task Only_the_first_page_of_a_tiff_is_traced()
    {
        var dir = fx.NewFolder();
        var tiff = EngineFixture.MakeMultiPageTiff(dir, 3);

        var job = await fx.ConvertAsync([tiff], Colors(4));

        Assert.Equal(JobState.Completed, job.State);
        var path = Assert.Single(PathsOf(Assert.Single(job.Outputs)));
        Assert.Equal("#ff7f50", path.Attribute("fill")?.Value); // coral, the first page
    }

    [Fact]
    public async Task Huge_pictures_are_reduced_for_tracing_and_keep_their_size()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "banner.png", 5000, 400, SKColors.White, canvas =>
        {
            using var black = Fill(SKColors.Black);
            for (var x = 100; x < 4900; x += 600)
                canvas.DrawCircle(x, 200, 150, black);
        });

        var svg = await ConvertAsync(png, LineArt());

        var root = XDocument.Load(svg).Root!;
        Assert.Equal(("5000", "400"), (root.Attribute("width")?.Value, root.Attribute("height")?.Value));
        Assert.Contains(XDocument.Load(svg).DescendantNodes().OfType<XComment>(), c => c.Value.Contains("reduced to 4096 x 328", StringComparison.Ordinal));
        Assert.Equal(8, SubpathsOf(Assert.Single(PathsOf(svg))));
        var difference = Difference(Pixels(png, 1250, 100, SKColors.White), RenderSvg(svg, 1250, 100, SKColors.White));
        Assert.True(difference < 0.02, $"{difference:P2} of the pixels differ");
    }

    [Theory]
    [InlineData(5000, 400, 4096, 328)]
    [InlineData(4000, 4000, 2449, 2449)]
    [InlineData(2000, 2000, 2000, 2000)]
    public void Traced_size_is_capped(int width, int height, int tracedWidth, int tracedHeight) =>
        Assert.Equal((tracedWidth, tracedHeight), RasterTracing.TracedSize(width, height));

    [Fact]
    public async Task A_2000_pixel_illustration_traces_in_a_few_seconds()
    {
        var dir = fx.NewFolder();
        var random = new Random(3);
        SKColor[] palette = [new(0xE5, 0x39, 0x35), new(0x1E, 0x63, 0xC8), new(0x2E, 0x9D, 0x4F), new(0xF9, 0xC8, 0x2B),
                             new(0x6A, 0x1B, 0x9A), new(0x00, 0x83, 0x8F), new(0x37, 0x47, 0x4F), new(0xFF, 0x8F, 0x00)];
        var png = Draw(dir, "poster.png", 2000, 2000, new SKColor(0xFA, 0xF7, 0xF0), canvas =>
        {
            for (var i = 0; i < 150; i++)
            {
                using var paint = Fill(palette[random.Next(palette.Length)]);
                var x = random.Next(0, 2000);
                var y = random.Next(0, 2000);
                if (i % 3 == 0)
                    canvas.DrawRect(x, y, random.Next(40, 300), random.Next(40, 300), paint);
                else
                    canvas.DrawCircle(x, y, random.Next(10, 160), paint);
            }
            using var text = Fill(SKColors.Black);
            using var font = new SKFont(SKTypeface.Default, 120);
            canvas.DrawText("Filee 2000", 200, 1000, font, text);
        });

        foreach (var preset in new[] { Colors(16), LineArt() })
        {
            preset.Output.FileNamePattern = "{name}_" + preset.Trace.Mode;
            var watch = Stopwatch.StartNew();
            var svg = await ConvertAsync(png, preset);
            watch.Stop();
            TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{preset.Trace.Mode}: {watch.Elapsed.TotalSeconds:0.00} s, {new FileInfo(svg).Length / 1024} KB, {PathsOf(svg).Sum(SubpathsOf)} outlines"));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{preset.Trace.Mode} took {watch.Elapsed}");
            if (preset.Trace.Mode == TracingMode.Color)
            {
                var difference = Difference(Pixels(png, 500, 500, SKColors.White), RenderSvg(svg, 500, 500, SKColors.White));
                Assert.True(difference < 0.03, $"{difference:P2} of the pixels differ");
            }
        }
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("heic")]
    public void Raster_images_reach_svg_by_tracing_and_cad_through_svg(string from)
    {
        var planner = fx.Catalog.CreatePlanner();

        Assert.Equal("vector", Assert.Single(planner.Plan(from, "svg")!.Steps).Converter.Id);
        Assert.Equal(["svg", "dxf"], planner.Plan(from, "dxf")!.Steps.Select(s => s.To));
        Assert.Equal(["svg", "dxf", "dwg"], planner.Plan(from, "dwg")!.Steps.Select(s => s.To));
        // Everything else keeps the image engines' routes.
        Assert.Equal("pdfsharp", Assert.Single(planner.Plan(from, "pdf")!.Steps).Converter.Id);
        Assert.DoesNotContain(planner.Plan(from, "eps")?.Steps ?? [], s => s.To == "svg");
    }

    [Fact]
    public void Icns_is_traced_through_png()
    {
        var route = fx.Catalog.CreatePlanner().Plan("icns", "svg");

        Assert.NotNull(route);
        Assert.Equal(["png", "svg"], route.Steps.Select(s => s.To));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("eps")]
    [InlineData("ps")]
    [InlineData("ai")]
    [InlineData("emf")]
    [InlineData("wmf")]
    [InlineData("cdr")]
    [InlineData("docx")]
    [InlineData("mp4")]
    public void Files_that_are_not_raster_images_are_never_traced(string from)
    {
        // With every optional engine as if installed, and with the built-in ones only: a vector route (LibreOffice for
        // PDF, EMF, CDR) is fine, tracing a rendering is not.
        var everyEngine = fx.Catalog.CreatePlanner(fx.Converters.Select(c => c.Id).ToArray());
        var builtIn = new RoutePlanner(fx.Converters.Where(c => c.Id is not ("libreoffice" or "ghostscript" or "pandoc" or "calibre" or "ffmpeg")));
        foreach (var planner in new[] { everyEngine, builtIn })
        {
            foreach (var to in new[] { "svg", "svgz", "dxf", "dwg" })
                Assert.DoesNotContain(planner.Plan(from, to)?.Steps ?? [], IsTracing);
        }
        Assert.Null(builtIn.Plan(from, "svg"));
    }

    [Fact]
    public void Tracing_is_never_a_step_towards_a_format_that_has_another_route()
    {
        // Every engine as if installed, so optional engines' edges are part of the check.
        var everyEngine = fx.Converters.Select(c => c.Id).ToArray();
        var withTracing = fx.Catalog.CreatePlanner(everyEngine);
        var without = new ConverterCatalog(fx.Converters.Select(c => c is VectorConverter ? new WithoutTracing(c) : c))
        {
            Priority = fx.Catalog.Priority,
        }.CreatePlanner(everyEngine);

        var formats = FormatRegistry.Known.Select(f => f.Id).ToList();
        var checkedRoutes = 0;
        foreach (var from in formats)
        {
            foreach (var to in formats.Where(t => t != from))
            {
                var route = withTracing.Plan(from, to);
                // Only raster images are ever traced.
                if (FormatRegistry.FindById(from)?.Category != FormatCategory.Image)
                    Assert.False(route?.Steps.Any(IsTracing) ?? false, $"{from} → {to} traces a rendering");
                var other = without.Plan(from, to);
                if (other is null)
                    continue;
                Assert.NotNull(route);
                Assert.False(route.Steps.Any(IsTracing), $"{from} → {to} traces: {string.Join(" → ", route.Steps.Select(s => s.To))}");
                Assert.Equal(other.Cost, route.Cost);
                checkedRoutes++;
            }
        }
        Assert.True(checkedRoutes > 1000);
    }

    private static bool IsTracing(RouteStep step) => step.Converter is VectorConverter && step.To == "svg" && step.From is not "svgz";

    /// <summary>The vector engine without its tracing edges.</summary>
    private sealed class WithoutTracing(IConverter inner) : IConverter
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public int MaxParallelism => inner.MaxParallelism;
        public IReadOnlyList<ConversionEdge> Edges { get; } = inner.Edges.Where(e => !e.ImageSourcesOnly).ToList();
        public EngineStatus GetStatus() => inner.GetStatus();
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            inner.ConvertAsync(step, progress, cancellationToken);
    }

    // ───────────────────────── SVG → DXF / DWG ─────────────────────────

    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="96" height="96" viewBox="0 0 96 96">
          <defs><rect id="tile" width="10" height="10"/></defs>
          <rect x="10" y="10" width="30" height="20" fill="#ff0000"/>
          <circle cx="60" cy="20" r="9.6" fill="none" stroke="#0000ff"/>
          <ellipse cx="20" cy="60" rx="12" ry="6" transform="rotate(30 20 60)" fill="#00ff00"/>
          <line x1="50" y1="50" x2="90" y2="50" stroke="black"/>
          <polyline points="50,60 60,70 70,60 80,70" fill="none" stroke="black"/>
          <polygon points="50,80 60,90 40,90"/>
          <path d="M 10 80 C 10 70, 30 70, 30 80 A 5 5 0 0 1 20 85 Z" fill="none" stroke="black"/>
          <g transform="translate(70 75) scale(2)"><use xlink:href="#tile"/></g>
        </svg>
        """;

    [Fact]
    public async Task Svg_becomes_dxf_in_millimetres_with_y_up()
    {
        var dir = fx.NewFolder();
        var svg = Path.Combine(dir, "parts.svg");
        await File.WriteAllTextAsync(svg, Drawing, TestContext.Current.CancellationToken);

        var dxf = await ConvertAsync(svg, new Preset { TargetFormat = "dxf" });

        var document = DxfReader.Read(dxf);
        Assert.Equal(UnitsType.Millimeters, document.Header.InsUnits);
        const double mm = 25.4 / 96; // 96 px = 1 inch, so the drawing is 25.4 mm square
        var entities = document.Entities.ToList();

        // The rectangle: closed, its top-left corner (10, 10) at y = 96 - 10 from the bottom.
        var rectangle = entities.OfType<LwPolyline>().Single(p => p.Color.R == 255 && p.Color.G == 0);
        Assert.True(rectangle.IsClosed);
        Assert.Equal(4, rectangle.Vertices.Count);
        Assert.Contains(rectangle.Vertices, v => Near(v.Location.X, 10 * mm) && Near(v.Location.Y, 86 * mm));
        Assert.Contains(rectangle.Vertices, v => Near(v.Location.X, 40 * mm) && Near(v.Location.Y, 66 * mm));

        var circle = Assert.Single(entities.OfType<Circle>());
        Assert.True(Near(circle.Center.X, 60 * mm) && Near(circle.Center.Y, 76 * mm) && Near(circle.Radius, 9.6 * mm), $"{circle.Center} r {circle.Radius}");
        Assert.Equal((0, 0, 255), (circle.Color.R, circle.Color.G, circle.Color.B));

        var ellipse = Assert.Single(entities.OfType<Ellipse>());
        Assert.True(Near(ellipse.Center.X, 20 * mm) && Near(ellipse.Center.Y, 36 * mm));
        Assert.True(Near(ellipse.RadiusRatio, 0.5));
        Assert.True(Near(ellipse.MajorAxisEndPoint.X, 12 * mm * Math.Cos(Math.PI / 6)) && Near(ellipse.MajorAxisEndPoint.Y, -12 * mm * Math.Sin(Math.PI / 6)),
            ellipse.MajorAxisEndPoint.ToString());

        var line = Assert.Single(entities.OfType<Line>());
        Assert.True(Near(line.StartPoint.X, 50 * mm) && Near(line.StartPoint.Y, 46 * mm) && Near(line.EndPoint.X, 90 * mm));

        Assert.Contains(entities.OfType<LwPolyline>(), p => !p.IsClosed && p.Vertices.Count == 4); // the polyline
        Assert.Contains(entities.OfType<LwPolyline>(), p => p.IsClosed && p.Vertices.Count == 3); // the polygon
        // The path's curve and arc are divided into short straight pieces.
        var curved = entities.OfType<LwPolyline>().Single(p => p.IsClosed && p.Vertices.Count > 20);
        // The arc is too short for its radius, so SVG enlarges it (to 5.59) and it bulges a little past x = 30.
        Assert.All(curved.Vertices, v => Assert.InRange(v.Location.X, 9.99 * mm, 30.7 * mm));
        Assert.Contains(curved.Vertices, v => v.Location.X > 30.5 * mm);
        Assert.Contains(curved.Vertices, v => Near(v.Location.Y, (96 - 85) * mm, 0.05));

        // <use> inside a translated and scaled group: (70, 75) to (90, 95).
        var tile = entities.OfType<LwPolyline>().Single(p => p.IsClosed && p.Vertices.Count == 4 && p.Color.R == 0);
        Assert.Contains(tile.Vertices, v => Near(v.Location.X, 70 * mm) && Near(v.Location.Y, 21 * mm));
        Assert.Contains(tile.Vertices, v => Near(v.Location.X, 90 * mm) && Near(v.Location.Y, 1 * mm));
    }

    private static bool Near(double a, double b, double tolerance = 1e-3) => Math.Abs(a - b) < tolerance;

    [Fact]
    public async Task Svg_text_becomes_outlines_in_dxf()
    {
        // Text needs a font with outlines; a CI machine without fonts has none to draw with.
        using (var font = new SKFont(SKFontManager.Default.MatchFamily("sans-serif") ?? SKTypeface.Default, 40))
        using (var glyph = font.GetGlyphPath(font.GetGlyphs("H")[0]))
            Assert.SkipWhen(glyph is null || glyph.IsEmpty, "no font with outlines on this machine");
        var dir = fx.NewFolder();
        var svg = Path.Combine(dir, "label.svg");
        await File.WriteAllTextAsync(svg, """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="60"><text x="10" y="45" font-size="40" font-family="sans-serif">Hi</text></svg>
            """, TestContext.Current.CancellationToken);

        var document = DxfReader.Read(await ConvertAsync(svg, new Preset { TargetFormat = "dxf" }));

        var outlines = document.Entities.OfType<LwPolyline>().ToList();
        Assert.True(outlines.Count >= 3, $"{outlines.Count} outlines"); // H, the stem and dot of i
        Assert.All(outlines, o => Assert.True(o.IsClosed));
    }

    [Fact]
    public async Task Svg_reaches_dwg_through_dxf()
    {
        var dir = fx.NewFolder();
        var svg = Path.Combine(dir, "parts.svg");
        await File.WriteAllTextAsync(svg, Drawing, TestContext.Current.CancellationToken);

        Assert.Equal("vector,cad", string.Join(',', fx.Catalog.CreatePlanner().Plan("svg", "dwg")!.Steps.Select(s => s.Converter.Id)));
        var dwg = await ConvertAsync(svg, new Preset { TargetFormat = "dwg" });

        var document = DwgReader.Read(dwg);
        Assert.Single(document.Entities.OfType<Circle>());
        Assert.Single(document.Entities.OfType<Ellipse>());
        Assert.Equal(8, document.Entities.Count()); // every shape of the drawing
    }

    [Fact]
    public async Task Traced_line_art_becomes_closed_dxf_outlines()
    {
        var dir = fx.NewFolder();
        var png = Draw(dir, "logo.png", 400, 300, SKColors.White, DrawLineArt);

        var dxf = await ConvertAsync(png, LineArt().Also(p => p.TargetFormat = "dxf"));

        var outlines = DxfReader.Read(dxf).Entities.OfType<LwPolyline>().ToList();
        Assert.Equal(6, outlines.Count);
        Assert.All(outlines, o => Assert.True(o.IsClosed));
        // 400 px wide at 96 px per inch.
        Assert.True(outlines.Max(o => o.Vertices.Max(v => v.Location.X)) < 400 * 25.4 / 96);
    }

    [Fact]
    public async Task Svg_with_nothing_to_outline_fails_with_a_message()
    {
        var dir = fx.NewFolder();
        var svg = Path.Combine(dir, "empty.svg");
        await File.WriteAllTextAsync(svg, """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><g/></svg>""", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([svg], new Preset { TargetFormat = "dxf" });

        Assert.Equal(JobState.Failed, job.State);
    }
}

internal static class PresetTestExtensions
{
    /// <summary>Changes a preset in an expression.</summary>
    public static Preset Also(this Preset preset, Action<Preset> change)
    {
        change(preset);
        return preset;
    }
}
