// HATCH for the CAD scene: boundary loops (lines, arcs, ellipses, splines, polylines) become even-odd filled paths;
// pattern hatches draw their pattern lines clipped to the boundary, or a light fill when the pattern is too dense.

using ACadSharp.Entities;
using ACadSharp.Tables;
using SkiaSharp;

namespace Filee.Engines.Cad;

internal sealed partial class CadSceneBuilder
{
    /// <summary>Pattern segments allowed for one hatch; denser patterns are shown as a light fill instead.</summary>
    private const int MaxPatternSegmentsPerHatch = 20_000;

    /// <summary>Pattern segments allowed for the whole drawing.</summary>
    private const int MaxPatternSegments = 2_000_000;

    private int _patternSegments;

    private void DrawHatch(Hatch hatch, Layer layer, DrawContext context)
    {
        var loops = new List<Figure>();
        foreach (var path in hatch.Paths)
            if (Loop(path) is { } loop)
                loops.Add(loop);
        if (loops.Count == 0)
            return;

        var plane = context.Transform
            .Multiply(Affine3.Ocs(hatch.Normal))
            .Multiply(Affine3.Translation(new Vec3(0, 0, hatch.Elevation)));
        var boundary = loops.Select(loop => Map(loop, plane)).ToList();
        var color = ResolveColor(hatch, layer, context);

        if (hatch.IsSolid)
        {
            var gradient = hatch.GradientColor;
            SKColor? second = null;
            if (gradient is { Enabled: true, Colors.Count: > 0 })
            {
                color = ResolveColor(gradient.Colors[0].Color, layer, context);
                second = gradient.Colors.Count > 1 ? ResolveColor(gradient.Colors[1].Color, layer, context) : Blend(color, SKColors.White, 0.8);
            }
            var fill = new FillPrimitive { Color = color, GradientColor = second, GradientAngle = gradient?.Angle ?? 0 };
            fill.Figures.AddRange(boundary);
            AddFill(fill);
            return;
        }

        var box = Box2.Empty;
        foreach (var loop in loops)
            loop.AddTo(ref box);
        var lines = hatch.Pattern is { Lines.Count: > 0 } pattern && _patternSegments < MaxPatternSegments
            ? PatternLines(pattern, box)
            : null;
        if (lines is null)
        {
            // Too dense (or no pattern data): a light tint of the hatch colour keeps the area recognisable.
            var tint = new FillPrimitive { Color = Blend(color, SKColors.White, 0.75) };
            tint.Figures.AddRange(boundary);
            AddFill(tint);
            return;
        }

        _patternSegments += lines.Count;
        var primitive = new HatchLinesPrimitive { Color = color, WidthMm = Math.Min(ResolveWidth(hatch, layer, context), DefaultLineWeightMm) };
        primitive.Boundary.AddRange(boundary);
        primitive.Lines.AddRange(lines.Select(l => (plane.Point(l.A.X, l.A.Y), plane.Point(l.B.X, l.B.Y))));
        if (Reserve())
        {
            foreach (var figure in boundary)
                figure.AddTo(ref _scene.Extents);
            _scene.Items.Add(primitive);
        }
    }

    /// <summary>One boundary loop in the hatch's OCS plane.</summary>
    private static Figure? Loop(Hatch.BoundaryPath path)
    {
        Figure? figure = null;
        foreach (var edge in path.Edges)
        {
            switch (edge)
            {
                case Hatch.BoundaryPath.Line line:
                    MoveOrLine(new Vec2(line.Start.X, line.Start.Y));
                    figure!.LineTo(new Vec2(line.End.X, line.End.Y));
                    break;
                case Hatch.BoundaryPath.Arc arc:
                    {
                        var r = arc.Radius;
                        EdgeArc(new Vec2(arc.Center.X, arc.Center.Y), new Vec2(r, 0), new Vec2(0, r), arc.StartAngle, arc.EndAngle, arc.CounterClockWise);
                        break;
                    }
                case Hatch.BoundaryPath.Ellipse ellipse:
                    {
                        var major = new Vec2(ellipse.MajorAxisEndPoint.X, ellipse.MajorAxisEndPoint.Y);
                        EdgeArc(new Vec2(ellipse.Center.X, ellipse.Center.Y), major, major.Perp * ellipse.RadiusRatio,
                            ellipse.StartAngle, ellipse.EndAngle, ellipse.CounterClockWise);
                        break;
                    }
                case Hatch.BoundaryPath.Spline spline:
                    {
                        // Rational spline edges keep each weight in the control point's Z.
                        var points = spline.ControlPoints.Count >= 2
                            ? CadCurves.SampleNurbs(spline.Degree, spline.ControlPoints.Select(c => new Vec3(c.X, c.Y, 0)).ToList(), spline.Knots,
                                spline.IsRational ? spline.ControlPoints.Select(c => c.Z).ToList() : null)
                            : CadCurves.ThroughPoints(spline.FitPoints.Select(p => new Vec3(p.X, p.Y, 0)).ToList(), closed: false);
                        foreach (var point in points)
                            MoveOrLine(new Vec2(point.X, point.Y));
                        break;
                    }
                case Hatch.BoundaryPath.Polyline polyline:
                    {
                        var vertices = polyline.Vertices;
                        if (vertices.Count == 0)
                            break;
                        MoveOrLine(new Vec2(vertices[0].X, vertices[0].Y));
                        var count = polyline.IsClosed ? vertices.Count : vertices.Count - 1;
                        for (var i = 0; i < count; i++)
                        {
                            var a = vertices[i];
                            var b = vertices[(i + 1) % vertices.Count];
                            AppendSegment(figure!, Affine3.Identity, new PolyVertex(new Vec2(a.X, a.Y), a.Z, 0, 0), new PolyVertex(new Vec2(b.X, b.Y), b.Z, 0, 0));
                        }
                        break;
                    }
            }
        }
        if (figure is null || figure.Segments.Count == 0)
            return null;
        figure.Closed = true;
        return figure;

        void MoveOrLine(Vec2 p)
        {
            if (figure is null)
                figure = new Figure(p);
            else if ((figure.End - p).Length > 1e-9)
                figure.LineTo(p);
        }

        // Clockwise edges store their angles mirrored at the X axis: the arc runs from -start to -end.
        void EdgeArc(Vec2 center, Vec2 u, Vec2 v, double start, double end, bool counterClockwise)
        {
            double t0, t1;
            if (counterClockwise)
            {
                (t0, t1) = (start, end);
                while (t1 <= t0)
                    t1 += 2 * Math.PI;
            }
            else
            {
                (t0, t1) = (-start, -end);
                while (t1 >= t0)
                    t1 -= 2 * Math.PI;
            }
            MoveOrLine(center + u * Math.Cos(t0) + v * Math.Sin(t0));
            figure!.ArcTo(center, u, v, t0, t1);
        }
    }

    /// <summary>
    /// The pattern's line families across the boundary's bounding box (in the hatch plane). Each family repeats
    /// every <c>Offset</c>; dashes start at the family's base point. Returns null when the pattern is too dense.
    /// </summary>
    private static List<(Vec2 A, Vec2 B)>? PatternLines(HatchPattern pattern, Box2 box)
    {
        var result = new List<(Vec2 A, Vec2 B)>();
        Vec2[] corners = [new(box.MinX, box.MinY), new(box.MaxX, box.MinY), new(box.MinX, box.MaxY), new(box.MaxX, box.MaxY)];
        var size = Math.Max(Math.Max(box.Width, box.Height), 1e-9);

        foreach (var line in pattern.Lines)
        {
            var d = new Vec2(Math.Cos(line.Angle), Math.Sin(line.Angle));
            var n = d.Perp;
            var basePoint = new Vec2(line.BasePoint.X, line.BasePoint.Y);
            var offset = new Vec2(line.Offset.X, line.Offset.Y);
            var spacing = Vec2.Dot(offset, n);
            var c0 = Vec2.Dot(basePoint, n);
            var low = corners.Min(c => Vec2.Dot(c, n));
            var high = corners.Max(c => Vec2.Dot(c, n));

            long first, last;
            if (Math.Abs(spacing) < size * 1e-9)
            {
                if (c0 < low || c0 > high)
                    continue;
                first = last = 0;
            }
            else
            {
                var k1 = (low - c0) / spacing;
                var k2 = (high - c0) / spacing;
                first = (long)Math.Floor(Math.Min(k1, k2));
                last = (long)Math.Ceiling(Math.Max(k1, k2));
            }
            if (last - first > MaxPatternSegmentsPerHatch)
                return null;

            var dashes = line.DashLengths.Where(double.IsFinite).ToArray();
            var period = dashes.Sum(Math.Abs);
            for (var k = first; k <= last; k++)
            {
                var origin = basePoint + offset * k;
                var tMin = corners.Min(c => Vec2.Dot(c - origin, d));
                var tMax = corners.Max(c => Vec2.Dot(c - origin, d));
                if (dashes.Length == 0 || period <= 0 || dashes.All(x => x > 0))
                {
                    result.Add((origin + d * tMin, origin + d * tMax));
                }
                else
                {
                    if ((tMax - tMin) / period * dashes.Length + result.Count > MaxPatternSegmentsPerHatch)
                        return null;
                    for (var t = Math.Floor(tMin / period) * period; t < tMax;)
                    {
                        foreach (var dash in dashes)
                        {
                            if (dash > 0 && t + dash > tMin && t < tMax)
                                result.Add((origin + d * Math.Max(t, tMin), origin + d * Math.Min(t + dash, tMax)));
                            else if (dash == 0 && t >= tMin && t <= tMax)
                                result.Add((origin + d * t, origin + d * t));
                            t += Math.Abs(dash);
                        }
                    }
                }
                if (result.Count > MaxPatternSegmentsPerHatch)
                    return null;
            }
        }
        return result;
    }

    /// <summary>A figure built in a plane's own coordinates, mapped to WCS (exact for the Bézier arcs).</summary>
    private static Figure Map(Figure local, Affine3 plane)
    {
        var mapped = new Figure(plane.Point(local.Start.X, local.Start.Y)) { Closed = local.Closed };
        foreach (var s in local.Segments)
        {
            var p = plane.Point(s.P.X, s.P.Y);
            mapped.Segments.Add(s.IsCubic
                ? Segment.Cubic(plane.Point(s.C1.X, s.C1.Y), plane.Point(s.C2.X, s.C2.Y), p)
                : Segment.Line(p));
        }
        return mapped;
    }

    private static SKColor Blend(SKColor color, SKColor toward, double amount) => new(
        (byte)(color.Red + (toward.Red - color.Red) * amount),
        (byte)(color.Green + (toward.Green - color.Green) * amount),
        (byte)(color.Blue + (toward.Blue - color.Blue) * amount));
}
