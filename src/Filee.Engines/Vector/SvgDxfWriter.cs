// SVG → DXF: the drawing's outlines as CAD geometry, written with ACadSharp (MIT).
//
// The SVG is loaded with Svg.Skia like for PDF output, so groups, transforms, <use>, nested <svg> and CSS are resolved
// the same way. Its drawing commands are replayed on a canvas that draws nothing (Svg.Skia keeps track of the
// transforms) and every shape becomes outlines:
//   - circles and ellipses become CIRCLE and ELLIPSE entities (an ellipse also when a transform skews a circle);
//   - everything else (paths, lines, polylines, polygons, rectangles, rounded corners, arcs) becomes LWPOLYLINEs,
//     closed where the shape is closed, or a LINE for a single straight segment; Bézier curves and arcs are divided
//     into straight pieces that stay within 0.01 mm of the curve, which every CAD and CAM program reads;
//   - text becomes its glyph outlines.
// Colours are kept as true colours (the stroke's, else the fill's); fills, stroke widths, gradients, clipping, masks,
// images and filters are not represented.
//
// Units: SVG user units are CSS pixels, 96 per inch, so 1 px = 25.4 / 96 mm and the DXF is in millimetres ($INSUNITS
// 4): a drawing made 100 mm wide in an SVG editor is 100 mm wide in CAD. SVG's y axis points down and DXF's up, so
// y is flipped, with the bottom-left corner of the SVG's viewport at (0, 0).

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Types.Units;
using CSMath;
using Filee.Engines.Cad;
using ShimSkiaSharp;
using SkiaSharp;
using SKCanvas = SkiaSharp.SKCanvas;
using SKMatrix = SkiaSharp.SKMatrix;
using SKPath = SkiaSharp.SKPath;
using SKPoint = SkiaSharp.SKPoint;
using SKRect = SkiaSharp.SKRect;

namespace Filee.Engines.Vector;

/// <summary>Writes the outlines of an SVG drawing as a DXF.</summary>
internal sealed class SvgDxfWriter
{
    /// <summary>Millimetres per CSS pixel (96 per inch).</summary>
    public const double MillimetresPerPixel = 25.4 / 96;

    /// <summary>Largest distance, in millimetres, between a curve and the straight pieces that replace it.</summary>
    public const double FlatnessMillimetres = 0.01;

    private const int MaxPiecesPerCurve = 512;

    private readonly CadDocument _document = new();
    private readonly SvgRenderer.Drawing _drawing;
    private readonly SKRect _bounds;
    private readonly double _flatness = FlatnessMillimetres / MillimetresPerPixel;
    private readonly CancellationToken _ct;

    private SvgDxfWriter(SvgRenderer.Drawing drawing, CancellationToken ct)
    {
        _drawing = drawing;
        _bounds = drawing.Bounds;
        _ct = ct;
        _document.Header.InsUnits = UnitsType.Millimeters;
        _document.Header.MeasurementUnits = MeasurementUnits.Metric;
    }

    /// <summary>Number of entities written (for tests and the log).</summary>
    public int EntityCount { get; private set; }

    /// <summary>Writes <paramref name="drawing"/> to <paramref name="output"/> as DXF, in the preset's version (AutoCAD 2018 by default).</summary>
    /// <exception cref="InvalidDataException">The drawing has no outlines to write.</exception>
    public static void Write(SvgRenderer.Drawing drawing, string output, CancellationToken ct, Core.Presets.CadVersion version = Core.Presets.CadVersion.AutoCad2018)
    {
        var writer = new SvgDxfWriter(drawing, ct);
        writer.Collect();
        if (writer.EntityCount == 0)
            throw new InvalidDataException("The SVG has no shapes that can be written as CAD geometry (only images or empty groups).");
        CadFile.Write(writer._document, output, "dxf", version);
    }

    private void Collect()
    {
        if (_drawing.Svg.Model is not { } picture || _drawing.Svg.SkiaModel is not { } model)
            return;
        using var canvas = new SKNoDrawCanvas((int)Math.Ceiling(_bounds.Right) + 1, (int)Math.Ceiling(_bounds.Bottom) + 1);
        using var text = new OutlinedTextPlayback(model);
        Replay(picture, canvas, model, text);
    }

    private void Replay(ShimSkiaSharp.SKPicture picture, SKCanvas canvas, Svg.Skia.SkiaModel model, OutlinedTextPlayback text)
    {
        if (picture.Commands is null)
            return;
        ShimSkiaSharp.SKPath? previous = null;
        var previousMatrix = SKMatrix.Identity;
        foreach (var command in picture.Commands)
        {
            _ct.ThrowIfCancellationRequested();
            switch (command)
            {
                case DrawPathCanvasCommand { Path: { } path } draw:
                    // A shape with fill and stroke is drawn twice: write its outline once.
                    var matrix = canvas.TotalMatrix;
                    if (!ReferenceEquals(path, previous) || !matrix.Equals(previousMatrix))
                        AddPath(path, matrix, model, ColorOf(draw.Paint));
                    previous = path;
                    previousMatrix = matrix;
                    break;
                case DrawPictureCanvasCommand { Picture: { } nested }:
                    canvas.Save();
                    Replay(nested, canvas, model, text);
                    canvas.Restore();
                    break;
                case DrawTextCanvasCommand or DrawTextBlobCanvasCommand or DrawPositionedTextRunCanvasCommand:
                    using (var outline = text.TextOutline(command, out var paint))
                    {
                        if (outline is { IsEmpty: false })
                        {
                            outline.Transform(canvas.TotalMatrix);
                            AddOutlines(outline, ColorOf(paint));
                        }
                    }
                    break;
                case SaveCanvasCommand or SaveLayerCanvasCommand or RestoreCanvasCommand or SetMatrixCanvasCommand:
                    model.Draw(command, canvas);
                    break;
                    // Clips, images and text on a path have no outline to write.
            }
        }
    }

    /// <summary>The colour of a paint as a DXF true colour; black for gradients and patterns.</summary>
    private static Color ColorOf(ShimSkiaSharp.SKPaint? paint) =>
        paint?.Shader is null && paint?.Color is { } color
            ? new Color(color.Red, color.Green, color.Blue)
            : new Color(0, 0, 0);

    private void AddPath(ShimSkiaSharp.SKPath path, SKMatrix matrix, Svg.Skia.SkiaModel model, Color color)
    {
        using var rest = new SKPath();
        foreach (var command in path.Commands ?? [])
        {
            switch (command)
            {
                case AddCirclePathCommand circle:
                    AddEllipse(circle.X, circle.Y, circle.Radius, circle.Radius, matrix, color);
                    break;
                case AddOvalPathCommand oval:
                    AddEllipse((oval.Rect.Left + oval.Rect.Right) / 2, (oval.Rect.Top + oval.Rect.Bottom) / 2, oval.Rect.Width / 2, oval.Rect.Height / 2, matrix, color);
                    break;
                default:
                    model.ToSKPath(command, rest);
                    break;
            }
        }
        if (rest.IsEmpty)
            return;
        rest.Transform(matrix);
        AddOutlines(rest, color);
    }

    // ───────────────────────── Circles and ellipses ─────────────────────────

    /// <summary>
    /// An axis-aligned ellipse (centre, radii) of the drawing, transformed by <paramref name="matrix"/>: its image is
    /// an ellipse whose axes come from the singular values of the transformed radius vectors.
    /// </summary>
    private void AddEllipse(float cx, float cy, float rx, float ry, SKMatrix matrix, Color color)
    {
        if (rx <= 0 || ry <= 0)
            return;
        var center = matrix.MapPoint(cx, cy);
        // Conjugate semi-diameters after the transform (the linear part only).
        double ux = matrix.ScaleX * rx, uy = matrix.SkewY * rx;
        double vx = matrix.SkewX * ry, vy = matrix.ScaleY * ry;
        double a = ux * ux + vx * vx, b = ux * uy + vx * vy, c = uy * uy + vy * vy;
        var root = Math.Sqrt((a - c) * (a - c) / 4 + b * b);
        var major = Math.Sqrt(Math.Max(0, (a + c) / 2 + root));
        var minor = Math.Sqrt(Math.Max(0, (a + c) / 2 - root));
        if (major <= 1e-9)
            return;
        var centre = ToDxf(center.X, center.Y);
        if (minor / major > 1 - 1e-6)
        {
            Add(new Circle { Center = centre, Radius = major * MillimetresPerPixel, Color = color });
            return;
        }
        var angle = 0.5 * Math.Atan2(2 * b, a - c);
        // The y flip mirrors the axis direction.
        var axis = new XYZ(Math.Cos(angle) * major * MillimetresPerPixel, -Math.Sin(angle) * major * MillimetresPerPixel, 0);
        Add(new Ellipse
        {
            Center = centre,
            MajorAxisEndPoint = axis,
            RadiusRatio = minor / major,
            StartParameter = 0,
            EndParameter = 2 * Math.PI,
            Color = color,
        });
    }

    // ───────────────────────── Outlines ─────────────────────────

    /// <summary>Adds every subpath of <paramref name="path"/> (already in drawing coordinates) as a polyline.</summary>
    private void AddOutlines(SKPath path, Color color)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        var current = new List<SKPoint>();
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush(current, closed: false, color);
                    current.Add(points[0]);
                    break;
                case SKPathVerb.Line:
                    current.Add(points[1]);
                    break;
                case SKPathVerb.Quad:
                    AddQuad(current, points[0], points[1], points[2]);
                    break;
                case SKPathVerb.Conic:
                    foreach (var quad in ConicToQuads(points[0], points[1], points[2], iterator.ConicWeight()))
                        AddQuad(current, quad.A, quad.B, quad.C);
                    break;
                case SKPathVerb.Cubic:
                    AddCubic(current, points[0], points[1], points[2], points[3]);
                    break;
                case SKPathVerb.Close:
                    var start = current.Count > 0 ? current[0] : default;
                    Flush(current, closed: true, color);
                    current.Add(start);
                    break;
            }
        }
        Flush(current, closed: false, color);
    }

    private void AddQuad(List<SKPoint> points, SKPoint p0, SKPoint p1, SKPoint p2)
    {
        var bend = Distance(p0.X - 2 * p1.X + p2.X, p0.Y - 2 * p1.Y + p2.Y);
        var pieces = Pieces(bend / 4);
        for (var i = 1; i <= pieces; i++)
        {
            var t = i / (double)pieces;
            var mt = 1 - t;
            points.Add(new SKPoint((float)(mt * mt * p0.X + 2 * mt * t * p1.X + t * t * p2.X),
                                   (float)(mt * mt * p0.Y + 2 * mt * t * p1.Y + t * t * p2.Y)));
        }
    }

    private void AddCubic(List<SKPoint> points, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
    {
        // A chord of a curve divided into n equal parameter steps is off by at most 1/8 · max|B''| / n², and
        // max|B''| ≤ 6 · the largest second difference of the control points.
        var bend = Math.Max(Distance(p0.X - 2 * p1.X + p2.X, p0.Y - 2 * p1.Y + p2.Y),
                            Distance(p1.X - 2 * p2.X + p3.X, p1.Y - 2 * p2.Y + p3.Y));
        var pieces = Pieces(bend * 0.75);
        for (var i = 1; i <= pieces; i++)
        {
            var t = i / (double)pieces;
            var mt = 1 - t;
            double b0 = mt * mt * mt, b1 = 3 * mt * mt * t, b2 = 3 * mt * t * t, b3 = t * t * t;
            points.Add(new SKPoint((float)(b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X),
                                   (float)(b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y)));
        }
    }

    /// <summary>Pieces needed when a single chord would be off by <paramref name="deviation"/> pixels.</summary>
    private int Pieces(double deviation) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(deviation / _flatness)), 1, MaxPiecesPerCurve);

    private static List<(SKPoint A, SKPoint B, SKPoint C)> ConicToQuads(SKPoint p0, SKPoint p1, SKPoint p2, float weight)
    {
        var quads = SKPath.ConvertConicToQuads(p0, p1, p2, weight, 4);
        var result = new List<(SKPoint, SKPoint, SKPoint)>();
        for (var i = 0; i + 2 < quads.Length; i += 2)
            result.Add((quads[i], quads[i + 1], quads[i + 2]));
        return result;
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>Writes the collected points as one entity and clears them.</summary>
    private void Flush(List<SKPoint> points, bool closed, Color color)
    {
        // Repeated points (zero-length pieces) mean nothing in CAD.
        var clean = new List<XYZ>(points.Count);
        foreach (var point in points)
        {
            var p = ToDxf(point.X, point.Y);
            if (clean.Count == 0 || !Near(clean[^1], p))
                clean.Add(p);
        }
        points.Clear();
        if (closed && clean.Count > 2 && Near(clean[0], clean[^1]))
            clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 2)
            return;
        if (clean.Count == 2 && !closed)
        {
            Add(new Line(clean[0], clean[1]) { Color = color });
            return;
        }
        var polyline = new LwPolyline(clean.Select(p => new LwPolyline.Vertex(p.X, p.Y))) { IsClosed = closed, Color = color };
        Add(polyline);
    }

    private static bool Near(XYZ a, XYZ b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    /// <summary>Drawing coordinates (CSS pixels, y down) → DXF (millimetres, y up, viewport's bottom-left at 0, 0).</summary>
    private XYZ ToDxf(double x, double y) =>
        new((x - _bounds.Left) * MillimetresPerPixel, (_bounds.Bottom - y) * MillimetresPerPixel, 0);

    private void Add(Entity entity)
    {
        _document.Entities.Add(entity);
        EntityCount++;
    }
}
