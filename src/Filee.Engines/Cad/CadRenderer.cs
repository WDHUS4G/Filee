// Draws a CadScene with SkiaSharp. One drawing routine serves every output: SKDocument (PDF, vector),
// SKSvgCanvas (SVG) and a raster SKBitmap (PNG and the other image formats). Only the page setup differs.

using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Cad;

/// <summary>
/// Where the drawing lands on the page: a uniform scale that fits the extents into the printable area, centred,
/// with Y pointing down as on every page.
/// </summary>
internal sealed class PageLayout
{
    private readonly double _offsetX;
    private readonly double _offsetY;

    private PageLayout(float width, float height, double unitsPerMm, double scale, double offsetX, double offsetY)
    {
        Width = width;
        Height = height;
        UnitsPerMm = unitsPerMm;
        Scale = scale;
        _offsetX = offsetX;
        _offsetY = offsetY;
    }

    /// <summary>Page width in output units (PDF points, SVG pixels, bitmap pixels).</summary>
    public float Width { get; }

    public float Height { get; }

    /// <summary>Output units per millimetre of paper (line weights are given in millimetres).</summary>
    public double UnitsPerMm { get; }

    /// <summary>Output units per drawing unit.</summary>
    public double Scale { get; }

    public Vec2 ToPage(Vec2 p) => new(p.X * Scale + _offsetX, -p.Y * Scale + _offsetY);

    public Vec2 VectorToPage(Vec2 v) => new(v.X * Scale, -v.Y * Scale);

    public SKPoint Point(Vec2 p)
    {
        var q = ToPage(p);
        return new SKPoint((float)q.X, (float)q.Y);
    }

    /// <summary>The WCS point shown at page position (x, y).</summary>
    public Vec2 FromPage(double x, double y) => new((x - _offsetX) / Scale, -(y - _offsetY) / Scale);

    /// <summary>
    /// PDF: an ISO sheet (A3 unless the preset asks for A4 or Letter), landscape when the drawing is wider than
    /// tall, 10 mm margins unless the preset sets its own.
    /// </summary>
    public static PageLayout ForPdf(Box2 extents, PdfOptions options)
    {
        var (shortMm, longMm) = options.PageSize switch
        {
            PdfPageSize.A4 => (210.0, 297.0),
            PdfPageSize.Letter => (215.9, 279.4),
            _ => (297.0, 420.0),
        };
        var (width, height) = Size(extents);
        var landscape = width >= height;
        var margin = options.MarginMm > 0 ? Math.Min(options.MarginMm, shortMm / 4) : 10;
        return Fit(extents, landscape ? longMm : shortMm, landscape ? shortMm : longMm, margin, 72 / 25.4);
    }

    /// <summary>
    /// SVG and images: a page with the drawing's aspect ratio whose long edge is an A3 sheet (420 mm) and a
    /// 6 mm margin. <paramref name="unitsPerMm"/> sets the resolution (96/25.4 for SVG pixels, dpi/25.4 for bitmaps).
    /// </summary>
    public static PageLayout ForImage(Box2 extents, double unitsPerMm)
    {
        const double LongMm = 420;
        const double MarginMm = 6;
        var (width, height) = Size(extents);
        // Very thin drawings (one long line) still get a usable page: at most 20:1.
        var aspect = Math.Clamp(height / width, 1.0 / 20, 20);
        var content = LongMm - 2 * MarginMm;
        var (contentWidth, contentHeight) = aspect <= 1 ? (content, content * aspect) : (content / aspect, content);
        return Fit(extents, contentWidth + 2 * MarginMm, contentHeight + 2 * MarginMm, MarginMm, unitsPerMm);
    }

    private static PageLayout Fit(Box2 extents, double pageWidthMm, double pageHeightMm, double marginMm, double unitsPerMm)
    {
        var (width, height) = Size(extents);
        var availableWidth = (pageWidthMm - 2 * marginMm) * unitsPerMm;
        var availableHeight = (pageHeightMm - 2 * marginMm) * unitsPerMm;
        var scale = Math.Min(availableWidth / width, availableHeight / height);
        var centerX = extents.IsEmpty ? 0 : (extents.MinX + extents.MaxX) / 2;
        var centerY = extents.IsEmpty ? 0 : (extents.MinY + extents.MaxY) / 2;
        var pageWidth = pageWidthMm * unitsPerMm;
        var pageHeight = pageHeightMm * unitsPerMm;
        return new PageLayout(
            (float)pageWidth, (float)pageHeight, unitsPerMm, scale,
            pageWidth / 2 - centerX * scale,
            pageHeight / 2 + centerY * scale);
    }

    /// <summary>Extents size with degenerate cases (a point, a horizontal line) given some room.</summary>
    private static (double Width, double Height) Size(Box2 extents)
    {
        var width = extents.Width;
        var height = extents.Height;
        var size = Math.Max(width, height);
        if (size <= 0)
            return (1, 1);
        return (Math.Max(width, size * 1e-3), Math.Max(height, size * 1e-3));
    }
}

/// <summary>Renders scenes to PDF, SVG and bitmaps.</summary>
internal static class CadRenderer
{
    /// <summary>Dash patterns shorter than this on paper are drawn as continuous lines (they would look solid anyway).</summary>
    private const double MinDashPatternMm = 0.8;

    /// <summary>Upper bound for dashes of one figure; beyond it the line is drawn continuous.</summary>
    private const int MaxDashesPerFigure = 20_000;

    /// <summary>Writes a one-page vector PDF.</summary>
    public static void WritePdf(CadScene scene, PdfOptions options, string title, Stream output)
    {
        var page = PageLayout.ForPdf(scene.Extents, options);
        var metadata = new SKDocumentPdfMetadata
        {
            Title = title,
            Creator = "Filee",
            Producer = "Filee (SkiaSharp)",
            RasterDpi = 300,
            Creation = DateTime.Now,
            Modified = DateTime.Now,
        };
        using var document = SKDocument.CreatePdf(output, metadata);
        var canvas = document.BeginPage(page.Width, page.Height);
        Draw(canvas, scene, page, background: false);
        document.EndPage();
        document.Close();
    }

    /// <summary>Writes an SVG (pixel units at 96 dpi) with a white background.</summary>
    public static void WriteSvg(CadScene scene, Stream output)
    {
        var page = PageLayout.ForImage(scene.Extents, 96 / 25.4);
        // The SVG canvas writes its XML when disposed; the stream must stay open until then.
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, page.Width, page.Height), output))
            Draw(canvas, scene, page, background: true);
        output.Flush();
    }

    /// <summary>
    /// Renders a white-background bitmap at <paramref name="dpi"/> (for the virtual A3 page), limited to a
    /// sensible pixel count.
    /// </summary>
    public static SKBitmap RenderBitmap(CadScene scene, double dpi)
    {
        const double MaxEdge = 12_000;
        const double MaxPixels = 80_000_000;
        var page = PageLayout.ForImage(scene.Extents, dpi / 25.4);
        var reduce = Math.Min(1, Math.Min(MaxEdge / Math.Max(page.Width, page.Height), Math.Sqrt(MaxPixels / ((double)page.Width * page.Height))));
        if (reduce < 1)
            page = PageLayout.ForImage(scene.Extents, dpi * reduce / 25.4);

        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(page.Width)), Math.Max(1, (int)Math.Round(page.Height)), SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, scene, page, background: true);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Draws every primitive in order. Consecutive strokes with the same pen are merged into one path.</summary>
    public static void Draw(SKCanvas canvas, CadScene scene, PageLayout page, bool background)
    {
        if (background)
        {
            using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, page.Width, page.Height, white);
        }

        using var batch = new StrokeBatch(canvas);
        foreach (var item in scene.Items)
        {
            switch (item)
            {
                case StrokePrimitive stroke:
                    DrawStroke(batch, stroke, page);
                    break;
                case InfiniteLinePrimitive line:
                    DrawInfinite(batch, line, page);
                    break;
                case FillPrimitive fill:
                    batch.Flush();
                    DrawFill(canvas, fill, page);
                    break;
                case HatchLinesPrimitive hatch:
                    batch.Flush();
                    DrawHatchLines(canvas, hatch, page);
                    break;
                case TextPrimitive text:
                    batch.Flush();
                    DrawText(canvas, text, page);
                    break;
                case PointPrimitive point:
                    batch.Flush();
                    DrawPoint(canvas, point, page);
                    break;
            }
        }
        batch.Flush();
    }

    private static void DrawStroke(StrokeBatch batch, StrokePrimitive stroke, PageLayout page)
    {
        var penWidth = stroke.WidthMm * page.UnitsPerMm;
        var wide = stroke.WorldWidth > 0 && stroke.WorldWidth * page.Scale > penWidth;
        var width = wide ? stroke.WorldWidth * page.Scale : penWidth;
        using var path = new SKPath();
        foreach (var figure in stroke.Figures)
            AddFigure(path, figure, page);
        var dashes = PageDashes(stroke.Dashes, page);
        if (dashes is null)
        {
            batch.Add(path, stroke.Color, (float)width, wide);
            return;
        }
        using var dashed = Dash(path, dashes);
        batch.Add(dashed ?? path, stroke.Color, (float)width, wide);
    }

    private static void DrawInfinite(StrokeBatch batch, InfiniteLinePrimitive line, PageLayout page)
    {
        // Clip to the page: the lines only need to reach its corners.
        var corners = new[] { page.FromPage(0, 0), page.FromPage(page.Width, 0), page.FromPage(0, page.Height), page.FromPage(page.Width, page.Height) };
        var tMax = corners.Max(c => Vec2.Dot(c - line.Origin, line.Direction));
        var tMin = line.IsRay ? 0 : corners.Min(c => Vec2.Dot(c - line.Origin, line.Direction));
        if (tMax <= tMin)
            return;
        var figure = new Figure(line.Origin + line.Direction * tMin);
        figure.LineTo(line.Origin + line.Direction * tMax);
        DrawStroke(batch, new StrokePrimitive { Color = line.Color, WidthMm = line.WidthMm, Dashes = line.Dashes, Figures = { figure } }, page);
    }

    private static void DrawFill(SKCanvas canvas, FillPrimitive fill, PageLayout page)
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var figure in fill.Figures)
            AddFigure(path, figure, page, close: true);
        using var paint = new SKPaint { Color = fill.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        if (fill.GradientColor is { } second)
        {
            var bounds = path.Bounds;
            var (sin, cos) = Math.SinCos(fill.GradientAngle);
            var half = (float)(Math.Abs(cos) * bounds.Width + Math.Abs(sin) * bounds.Height) / 2;
            var direction = new SKPoint((float)cos * half, (float)-sin * half);
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(bounds.MidX - direction.X, bounds.MidY - direction.Y),
                new SKPoint(bounds.MidX + direction.X, bounds.MidY + direction.Y),
                [fill.Color, second], SKShaderTileMode.Clamp);
        }
        canvas.DrawPath(path, paint);
    }

    private static void DrawHatchLines(SKCanvas canvas, HatchLinesPrimitive hatch, PageLayout page)
    {
        using var clip = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var figure in hatch.Boundary)
            AddFigure(clip, figure, page, close: true);
        using var lines = new SKPath();
        foreach (var (a, b) in hatch.Lines)
        {
            lines.MoveTo(page.Point(a));
            lines.LineTo(page.Point(b));
        }
        using var paint = Pen(hatch.Color, (float)(hatch.WidthMm * page.UnitsPerMm), wide: false);
        canvas.Save();
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        canvas.DrawPath(lines, paint);
        canvas.Restore();
    }

    private static void DrawText(SKCanvas canvas, TextPrimitive text, PageLayout page)
    {
        var origin = page.ToPage(text.Origin);
        var x = page.VectorToPage(text.XAxis);
        var y = page.VectorToPage(text.YAxis);
        // Draw at a font size equal to the em height on the page and put only the direction into the matrix:
        // PDF and SVG viewers handle normal font sizes better than tiny ones blown up by a transform.
        var size = y.Length;
        if (size < 0.01 || !double.IsFinite(size) || size > 1e6)
            return;
        var matrix = new SKMatrix(
            (float)(x.X / size), (float)(y.X / size), (float)origin.X,
            (float)(x.Y / size), (float)(y.Y / size), (float)origin.Y,
            0, 0, 1);
        using var font = CadFonts.CreateFont(text.Typeface, (float)size);
        using var paint = new SKPaint { Color = text.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.Save();
        canvas.Concat(in matrix);
        canvas.DrawText(text.Text, 0, 0, font, paint);
        canvas.Restore();
    }

    private static void DrawPoint(SKCanvas canvas, PointPrimitive point, PageLayout page)
    {
        var radius = (float)Math.Max(point.WidthMm * page.UnitsPerMm, 0.3 * page.UnitsPerMm) / 2;
        using var paint = new SKPaint { Color = point.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(page.Point(point.Location), radius, paint);
    }

    private static void AddFigure(SKPath path, Figure figure, PageLayout page, bool close = false)
    {
        path.MoveTo(page.Point(figure.Start));
        if (figure.Segments.Count == 0)
        {
            path.LineTo(page.Point(figure.Start)); // zero-length: a dot with round caps
            return;
        }
        foreach (var segment in figure.Segments)
        {
            if (segment.IsCubic)
                path.CubicTo(page.Point(segment.C1), page.Point(segment.C2), page.Point(segment.P));
            else
                path.LineTo(page.Point(segment.P));
        }
        if (figure.Closed || close)
            path.Close();
    }

    /// <summary>The dash pattern in page units, or null to draw a continuous line.</summary>
    private static float[]? PageDashes(double[]? dashes, PageLayout page)
    {
        if (dashes is null)
            return null;
        var scaled = dashes.Select(d => (float)(d * page.Scale)).ToArray();
        var period = scaled.Sum(Math.Abs);
        if (!float.IsFinite(period) || period < MinDashPatternMm * page.UnitsPerMm)
            return null;
        return scaled;
    }

    /// <summary>
    /// Applies a dash pattern by cutting the path into pieces. Doing it here (not with a path effect) gives PDF,
    /// SVG and bitmaps the same result. Returns null when the pattern would create too many pieces.
    /// </summary>
    private static SKPath? Dash(SKPath source, float[] pattern)
    {
        var period = pattern.Sum(Math.Abs);
        var result = new SKPath();
        using var measure = new SKPathMeasure(source, false, 1);
        do
        {
            var length = measure.Length;
            if (length <= 0)
                continue;
            if (length / period * pattern.Length > MaxDashesPerFigure)
            {
                result.Dispose();
                return null;
            }
            var position = 0f;
            var index = 0;
            while (position < length)
            {
                var element = pattern[index];
                if (element > 0)
                {
                    measure.GetSegment(position, Math.Min(position + element, length), result, true);
                }
                else if (element == 0 && measure.GetPosition(position, out var dot))
                {
                    result.MoveTo(dot);
                    result.LineTo(dot);
                }
                position += Math.Abs(element);
                index = (index + 1) % pattern.Length;
            }
        }
        while (measure.NextContour());
        return result;
    }

    private static SKPaint Pen(SKColor color, float width, bool wide) => new()
    {
        Color = color,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        IsAntialias = true,
        // Line weights plot with round ends; wide polylines have square ends and mitred corners like in AutoCAD.
        StrokeCap = wide ? SKStrokeCap.Butt : SKStrokeCap.Round,
        StrokeJoin = wide ? SKStrokeJoin.Miter : SKStrokeJoin.Round,
    };

    /// <summary>
    /// Merges consecutive strokes with the same colour and width into one path (smaller PDF / SVG, faster), up to
    /// a size that keeps viewers and the SVG writer responsive.
    /// </summary>
    private sealed class StrokeBatch(SKCanvas canvas) : IDisposable
    {
        private const int MaxPoints = 4096;

        private readonly SKPath _path = new();
        private SKColor _color;
        private float _width;
        private bool _wide;
        private bool _empty = true;

        public void Add(SKPath path, SKColor color, float width, bool wide)
        {
            if (!_empty && (color != _color || width != _width || wide != _wide || _path.PointCount > MaxPoints))
                Flush();
            _color = color;
            _width = width;
            _wide = wide;
            _path.AddPath(path);
            _empty = false;
        }

        public void Flush()
        {
            if (_empty)
                return;
            using var paint = Pen(_color, _width, _wide);
            canvas.DrawPath(_path, paint);
            _path.Reset();
            _empty = true;
        }

        public void Dispose() => _path.Dispose();
    }
}
