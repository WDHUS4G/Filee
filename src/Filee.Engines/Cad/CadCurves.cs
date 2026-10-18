// Curve math for the CAD renderer: NURBS evaluation (SPLINE entities, spline hatch edges, spline-fit polylines),
// smooth curves through fit points, and polyline bulge arcs.

namespace Filee.Engines.Cad;

/// <summary>Curve evaluation helpers. Everything works in the entity's own coordinates.</summary>
internal static class CadCurves
{
    /// <summary>Upper bound for the points generated for one curve, so a broken knot vector cannot explode.</summary>
    private const int MaxSamples = 4000;

    /// <summary>
    /// Samples a (rational) B-spline with de Boor's algorithm. Missing or inconsistent knots are replaced by a
    /// clamped uniform knot vector; missing weights mean 1. Returns an empty list for unusable input.
    /// </summary>
    public static List<Vec3> SampleNurbs(int degree, IReadOnlyList<Vec3> controlPoints, IReadOnlyList<double> knots, IReadOnlyList<double>? weights)
    {
        var n = controlPoints.Count;
        var result = new List<Vec3>();
        if (n < 2)
            return result;

        var p = Math.Clamp(degree, 1, n - 1);
        var t = knots.Count == n + p + 1 && IsNonDecreasing(knots) ? knots.ToArray() : ClampedUniform(n, p);
        var w = weights is { Count: > 0 } && weights.Count == n && weights.All(v => v > 0 && double.IsFinite(v))
            ? weights.ToArray()
            : null;

        var spans = new List<int>();
        for (var k = p; k < n; k++)
            if (t[k + 1] > t[k])
                spans.Add(k);
        if (spans.Count == 0)
            return result;

        // Straight segments need only their end points; curved spans get up to 32 points (smooth in vector output).
        var perSpan = p == 1 ? 1 : Math.Clamp(MaxSamples / spans.Count, 1, 32);
        var homogeneous = new (double X, double Y, double Z, double W)[p + 1];
        foreach (var k in spans)
        {
            for (var s = 0; s < perSpan; s++)
                result.Add(Evaluate(t[k] + (t[k + 1] - t[k]) * s / perSpan, k));
        }
        result.Add(Evaluate(t[spans[^1] + 1], spans[^1]));
        return result;

        Vec3 Evaluate(double u, int k)
        {
            for (var j = 0; j <= p; j++)
            {
                var point = controlPoints[k - p + j];
                var weight = w?[k - p + j] ?? 1;
                homogeneous[j] = (point.X * weight, point.Y * weight, point.Z * weight, weight);
            }
            for (var r = 1; r <= p; r++)
            {
                for (var j = p; j >= r; j--)
                {
                    var denominator = t[j + 1 + k - r] - t[j + k - p];
                    var alpha = denominator == 0 ? 0 : (u - t[j + k - p]) / denominator;
                    var a = homogeneous[j - 1];
                    var b = homogeneous[j];
                    homogeneous[j] = (
                        (1 - alpha) * a.X + alpha * b.X,
                        (1 - alpha) * a.Y + alpha * b.Y,
                        (1 - alpha) * a.Z + alpha * b.Z,
                        (1 - alpha) * a.W + alpha * b.W);
                }
            }
            var h = homogeneous[p];
            return h.W == 0 ? new Vec3(h.X, h.Y, h.Z) : new Vec3(h.X / h.W, h.Y / h.W, h.Z / h.W);
        }
    }

    /// <summary>
    /// A smooth curve through fit points (centripetal Catmull-Rom), used for splines stored only as fit points
    /// and for spline leaders. Close to what AutoCAD draws, without solving the interpolation system.
    /// </summary>
    public static List<Vec3> ThroughPoints(IReadOnlyList<Vec3> points, bool closed)
    {
        var count = points.Count;
        var result = new List<Vec3>();
        if (count < 3)
        {
            result.AddRange(points);
            return result;
        }

        var segments = closed ? count : count - 1;
        var perSegment = Math.Clamp(MaxSamples / segments, 1, 10);
        for (var i = 0; i < segments; i++)
        {
            var p0 = Get(i - 1);
            var p1 = Get(i);
            var p2 = Get(i + 1);
            var p3 = Get(i + 2);
            for (var s = 0; s < perSegment; s++)
                result.Add(CatmullRom(p0, p1, p2, p3, (double)s / perSegment));
        }
        result.Add(closed ? points[0] : points[^1]);
        return result;

        Vec3 Get(int index)
        {
            if (closed)
                return points[((index % count) + count) % count];
            // Open ends: mirror the neighbour so the tangent at the end points follows the polygon.
            if (index < 0)
                return points[0] + (points[0] - points[1]);
            if (index >= count)
                return points[^1] + (points[^1] - points[^2]);
            return points[index];
        }
    }

    /// <summary>
    /// The arc of a polyline segment with bulge <paramref name="bulge"/> (tan of a quarter of the included angle,
    /// positive = counter-clockwise) from <paramref name="p0"/> to <paramref name="p1"/>.
    /// </summary>
    public static (Vec2 Center, double Radius, double StartAngle, double Sweep) BulgeArc(Vec2 p0, Vec2 p1, double bulge)
    {
        var chord = p1 - p0;
        var center = p0 + chord * 0.5 + chord.Perp * ((1 - bulge * bulge) / (4 * bulge));
        var radius = (p0 - center).Length;
        var start = Math.Atan2(p0.Y - center.Y, p0.X - center.X);
        return (center, radius, start, 4 * Math.Atan(bulge));
    }

    private static Vec3 CatmullRom(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double u)
    {
        // Centripetal parameterisation (alpha = 0.5) avoids loops and cusps on unevenly spaced points.
        var t0 = 0.0;
        var t1 = t0 + Math.Sqrt(Math.Max((p1 - p0).Length, 1e-12));
        var t2 = t1 + Math.Sqrt(Math.Max((p2 - p1).Length, 1e-12));
        var t3 = t2 + Math.Sqrt(Math.Max((p3 - p2).Length, 1e-12));
        var t = t1 + (t2 - t1) * u;

        var a1 = Lerp(p0, p1, t0, t1, t);
        var a2 = Lerp(p1, p2, t1, t2, t);
        var a3 = Lerp(p2, p3, t2, t3, t);
        var b1 = Lerp(a1, a2, t0, t2, t);
        var b2 = Lerp(a2, a3, t1, t3, t);
        return Lerp(b1, b2, t1, t2, t);

        static Vec3 Lerp(Vec3 a, Vec3 b, double ta, double tb, double t) =>
            a * ((tb - t) / (tb - ta)) + b * ((t - ta) / (tb - ta));
    }

    private static bool IsNonDecreasing(IReadOnlyList<double> values)
    {
        for (var i = 1; i < values.Count; i++)
            if (!(values[i] >= values[i - 1]) || !double.IsFinite(values[i]))
                return false;
        return values.Count > 0 && values[^1] > values[0];
    }

    private static double[] ClampedUniform(int n, int p)
    {
        var knots = new double[n + p + 1];
        for (var i = 0; i < knots.Length; i++)
            knots[i] = i <= p ? 0 : i >= n ? 1 : (double)(i - p) / (n - p);
        return knots;
    }
}
