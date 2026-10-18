// Approximates cubic Bézier curves (CFF) with quadratic splines (TrueType) within a distance tolerance. Follows the
// approach of cu2qu (Google, Apache-2.0; part of fontTools): split the cubic into n equal pieces, give each piece one
// quadratic control point, let TrueType's implied on-curve midpoints join them, and take the smallest n that fits.

namespace Filee.Engines.Fonts.Cff;

/// <summary>Cubic → quadratic curve conversion.</summary>
internal static class CubicToQuadratic
{
    private const int MaxPieces = 100;
    private const int MaxCheckDepth = 24;

    /// <summary>
    /// Returns the off-curve control points of a quadratic spline from <paramref name="p0"/> to
    /// <paramref name="p3"/> that stays within <paramref name="tolerance"/> of the cubic. Consecutive off-curve
    /// points have an implied on-curve point halfway between them. An empty result means a straight line.
    /// </summary>
    public static List<Vec2> Convert(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance)
    {
        if (IsLine(p0, p1, p2, p3, tolerance))
            return [];
        for (var n = 1; n <= MaxPieces; n++)
        {
            var spline = n == 1 ? Single(p0, p1, p2, p3, tolerance) : Spline(p0, p1, p2, p3, n, tolerance);
            if (spline is not null)
                return spline;
        }
        // Not reachable for real outlines (100 pieces fit anything); keep the finest split rather than failing.
        return Spline(p0, p1, p2, p3, MaxPieces, double.PositiveInfinity)!;
    }

    /// <summary>
    /// True when the control points lie on the chord within a small fraction of the tolerance and between its ends:
    /// the curve is then exactly the chord.
    /// </summary>
    private static bool IsLine(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance)
    {
        var chord = p3 - p0;
        var length = chord.Length;
        if (length < 1e-9)
            return (p1 - p0).Length < 1e-9 && (p2 - p0).Length < 1e-9;
        foreach (var p in (ReadOnlySpan<Vec2>)[p1, p2])
        {
            var v = p - p0;
            var along = Vec2.Dot(v, chord) / (length * length);
            var distance = Math.Abs(v.X * chord.Y - v.Y * chord.X) / length;
            if (along < 0 || along > 1 || distance > tolerance * 0.01)
                return false;
        }
        return true;
    }

    /// <summary>One quadratic whose control point is where the cubic's end tangents meet.</summary>
    private static List<Vec2>? Single(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance)
    {
        if (Intersect(p0, p1, p2, p3) is not { } q)
            return null;
        // Degree-elevated quadratic vs. the cubic: compare the differences of their control points.
        var c1 = p0 + (q - p0) * (2.0 / 3);
        var c2 = p3 + (q - p3) * (2.0 / 3);
        return FitsInside(default, c1 - p1, c2 - p2, default, tolerance, 0) ? [q] : null;
    }

    /// <summary>n quadratics chained through implied on-curve points; null when the error exceeds the tolerance.</summary>
    private static List<Vec2>? Spline(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, int n, double tolerance)
    {
        var pieces = Split(p0, p1, p2, p3, n);
        var controls = new List<Vec2>(n);
        for (var i = 0; i < n; i++)
            controls.Add(Control((double)i / (n - 1), pieces[i]));

        var d1 = default(Vec2);
        var q2 = p0;
        for (var i = 0; i < n; i++)
        {
            var (_, c1, c2, c3) = pieces[i];
            var q0 = q2;
            var q1 = controls[i];
            q2 = i + 1 < n ? (q1 + controls[i + 1]) * 0.5 : c3;
            var d0 = d1;
            d1 = q2 - c3;
            if (d1.Length > tolerance ||
                !FitsInside(d0, q0 + (q1 - q0) * (2.0 / 3) - c1, q2 + (q1 - q2) * (2.0 / 3) - c2, d1, tolerance, 0))
            {
                return null;
            }
        }
        return controls;
    }

    /// <summary>
    /// Control point for piece <paramref name="t"/> (0 = first, 1 = last): interpolates between the points where the
    /// piece's start and end tangents, extended by 1.5, land.
    /// </summary>
    private static Vec2 Control(double t, (Vec2 A, Vec2 B, Vec2 C, Vec2 D) piece)
    {
        var start = piece.A + (piece.B - piece.A) * 1.5;
        var end = piece.D + (piece.C - piece.D) * 1.5;
        return start + (end - start) * t;
    }

    /// <summary>Splits the cubic into n pieces of equal parameter length.</summary>
    private static (Vec2 A, Vec2 B, Vec2 C, Vec2 D)[] Split(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, int n)
    {
        // Power basis: B(t) = a t³ + b t² + c t + d.
        var c = (p1 - p0) * 3;
        var b = (p2 - p1) * 3 - c;
        var d = p0;
        var a = p3 - d - c - b;

        var pieces = new (Vec2, Vec2, Vec2, Vec2)[n];
        var dt = 1.0 / n;
        for (var i = 0; i < n; i++)
        {
            // Substitute t = t0 + dt·u and convert the piece back to Bézier control points.
            var t0 = i * dt;
            var a1 = a * (dt * dt * dt);
            var b1 = (a * (3 * t0) + b) * (dt * dt);
            var c1 = (b * (2 * t0) + c + a * (3 * t0 * t0)) * dt;
            var d1 = a * (t0 * t0 * t0) + b * (t0 * t0) + c * t0 + d;
            var q1 = c1 * (1.0 / 3) + d1;
            var q2 = (b1 + c1) * (1.0 / 3) + q1;
            var q3 = a1 + d1 + c1 + b1;
            pieces[i] = (d1, q1, q2, q3);
        }
        return pieces;
    }

    /// <summary>Where line a→b meets line c→d, or null when they are parallel.</summary>
    private static Vec2? Intersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var ab = b - a;
        var cd = d - c;
        var normal = new Vec2(-ab.Y, ab.X);
        var denominator = Vec2.Dot(normal, cd);
        if (Math.Abs(denominator) < 1e-12)
            return null;
        var h = Vec2.Dot(normal, a - c) / denominator;
        return c + cd * h;
    }

    /// <summary>
    /// True when the cubic with these control points (a difference curve) stays within the tolerance of the origin
    /// everywhere, checked by subdividing until its control points are all close enough.
    /// </summary>
    private static bool FitsInside(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance, int depth)
    {
        if (p2.Length <= tolerance && p1.Length <= tolerance)
            return true;
        var mid = (p0 + (p1 + p2) * 3 + p3) * 0.125;
        if (mid.Length > tolerance)
            return false;
        if (depth >= MaxCheckDepth)
            return true;
        var deriv = (p3 + p2 - p1 - p0) * 0.125;
        return FitsInside(p0, (p0 + p1) * 0.5, mid - deriv, mid, tolerance, depth + 1) &&
               FitsInside(mid, mid + deriv, (p2 + p3) * 0.5, p3, tolerance, depth + 1);
    }
}
