// Geometry for the CAD renderer: 3D affine transforms (block inserts, object coordinate systems), 2D paths whose
// arcs are stored as cubic Béziers so they stay exact under any affine transform, and bounding boxes.

using CSMath;

namespace Filee.Engines.Cad;

/// <summary>A point or vector in the drawing plane (WCS X/Y after the top-view projection).</summary>
internal readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>The vector rotated by +90°.</summary>
    public Vec2 Perp => new(-Y, X);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public Vec2 Normalized()
    {
        var length = Length;
        return length > 0 ? new Vec2(X / length, Y / length) : new Vec2(1, 0);
    }

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    // The generated record ToString would print Perp, which is a Vec2 again: endless recursion.
    public override string ToString() => FormattableString.Invariant($"({X}, {Y})");
}

/// <summary>A 3D point or vector (double precision) used while walking entities and blocks.</summary>
internal readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static implicit operator Vec3(XYZ p) => new(p.X, p.Y, p.Z);

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public Vec3 Normalized()
    {
        var length = Length;
        return length > 0 ? new Vec3(X / length, Y / length, Z / length) : new Vec3(0, 0, 1);
    }

    public override string ToString() => FormattableString.Invariant($"({X}, {Y}, {Z})");
}

/// <summary>
/// A 3D affine transform from an entity's own coordinates to WCS: the images of the X, Y and Z axes plus a
/// translation. Block inserts, arrays and object coordinate systems (OCS) are chained as products of these.
/// </summary>
internal readonly struct Affine3(Vec3 x, Vec3 y, Vec3 z, Vec3 t)
{
    public static readonly Affine3 Identity = new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), default);

    public Vec3 X { get; } = x;
    public Vec3 Y { get; } = y;
    public Vec3 Z { get; } = z;
    public Vec3 T { get; } = t;

    public static Affine3 Translation(Vec3 t) => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), t);

    public static Affine3 Scale(double sx, double sy, double sz) => new(new(sx, 0, 0), new(0, sy, 0), new(0, 0, sz), default);

    public static Affine3 RotationZ(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new(new(cos, sin, 0), new(-sin, cos, 0), new(0, 0, 1), default);
    }

    /// <summary>
    /// The object coordinate system of an entity with the given extrusion direction, built with AutoCAD's
    /// "arbitrary axis algorithm". Mirrored 2D entities typically carry the normal (0, 0, -1).
    /// </summary>
    public static Affine3 Ocs(Vec3 normal)
    {
        var n = normal.Normalized();
        if (Math.Abs(n.X) < 1e-12 && Math.Abs(n.Y) < 1e-12 && n.Z > 0)
            return Identity;
        const double Limit = 1.0 / 64;
        var ax = Math.Abs(n.X) < Limit && Math.Abs(n.Y) < Limit
            ? Vec3.Cross(new Vec3(0, 1, 0), n)
            : Vec3.Cross(new Vec3(0, 0, 1), n);
        ax = ax.Normalized();
        var ay = Vec3.Cross(n, ax).Normalized();
        return new(ax, ay, n, default);
    }

    public Vec3 Apply(Vec3 p) => new(
        T.X + X.X * p.X + Y.X * p.Y + Z.X * p.Z,
        T.Y + X.Y * p.X + Y.Y * p.Y + Z.Y * p.Z,
        T.Z + X.Z * p.X + Y.Z * p.Y + Z.Z * p.Z);

    public Vec3 ApplyVector(Vec3 v) => new(
        X.X * v.X + Y.X * v.Y + Z.X * v.Z,
        X.Y * v.X + Y.Y * v.Y + Z.Y * v.Z,
        X.Z * v.X + Y.Z * v.Y + Z.Z * v.Z);

    /// <summary>Maps a point and projects it onto the drawing plane.</summary>
    public Vec2 Point(Vec3 p)
    {
        var w = Apply(p);
        return new Vec2(w.X, w.Y);
    }

    /// <summary>Maps a point given in the entity's XY plane (Z = <paramref name="z"/>).</summary>
    public Vec2 Point(double x, double y, double z = 0) => Point(new Vec3(x, y, z));

    /// <summary>Maps a direction vector and projects it onto the drawing plane.</summary>
    public Vec2 Vector(Vec3 v)
    {
        var w = ApplyVector(v);
        return new Vec2(w.X, w.Y);
    }

    public Vec2 Vector(double x, double y) => Vector(new Vec3(x, y, 0));

    /// <summary>Composition: first <paramref name="inner"/>, then this transform.</summary>
    public Affine3 Multiply(Affine3 inner) =>
        new(ApplyVector(inner.X), ApplyVector(inner.Y), ApplyVector(inner.Z), Apply(inner.T));

    /// <summary>
    /// Average scale of the projected XY plane (square root of the area ratio), used for widths and dash lengths
    /// that are defined in the entity's units.
    /// </summary>
    public double PlanScale
    {
        get
        {
            var det = Math.Abs(X.X * Y.Y - X.Y * Y.X);
            if (det > 1e-300)
                return Math.Sqrt(det);
            // A plane seen edge-on: fall back to the longest axis image so widths do not vanish.
            return Math.Max(new Vec2(X.X, X.Y).Length, new Vec2(Y.X, Y.Y).Length);
        }
    }
}

/// <summary>One segment of a <see cref="Figure"/>: a straight line or a cubic Bézier ending in <see cref="P"/>.</summary>
internal readonly record struct Segment(Vec2 C1, Vec2 C2, Vec2 P, bool IsCubic)
{
    public static Segment Line(Vec2 p) => new(default, default, p, false);

    public static Segment Cubic(Vec2 c1, Vec2 c2, Vec2 p) => new(c1, c2, p, true);
}

/// <summary>A connected run of segments in WCS (a polyline, an arc, a hatch loop ...).</summary>
internal sealed class Figure(Vec2 start)
{
    public Vec2 Start { get; } = start;
    public List<Segment> Segments { get; } = [];
    public bool Closed { get; set; }

    public Vec2 End => Segments.Count == 0 ? Start : Segments[^1].P;

    public void LineTo(Vec2 p) => Segments.Add(Segment.Line(p));

    /// <summary>
    /// Appends the elliptical arc <c>C + U·cos t + V·sin t</c> for t from <paramref name="t0"/> to
    /// <paramref name="t1"/> as cubic Béziers of at most 90° each. The figure should already end at the arc start.
    /// Because U and V are the images of the original axes, this is exact for circles seen through any affine
    /// transform (non-uniform block scales, tilted planes).
    /// </summary>
    public void ArcTo(Vec2 center, Vec2 u, Vec2 v, double t0, double t1)
    {
        var sweep = t1 - t0;
        var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        var step = sweep / count;
        var k = 4.0 / 3.0 * Math.Tan(step / 4);
        for (var i = 0; i < count; i++)
        {
            var a = t0 + step * i;
            var b = a + step;
            var (sa, ca) = Math.SinCos(a);
            var (sb, cb) = Math.SinCos(b);
            var p0 = center + u * ca + v * sa;
            var p3 = center + u * cb + v * sb;
            var d0 = u * -sa + v * ca;
            var d3 = u * -sb + v * cb;
            Segments.Add(Segment.Cubic(p0 + d0 * k, p3 - d3 * k, p3));
        }
    }

    /// <summary>Includes every point of the figure (not just the Bézier control points) in <paramref name="box"/>.</summary>
    public void AddTo(ref Box2 box)
    {
        box.Add(Start);
        var previous = Start;
        foreach (var segment in Segments)
        {
            if (segment.IsCubic)
                AddCubic(ref box, previous, segment.C1, segment.C2, segment.P);
            else
                box.Add(segment.P);
            previous = segment.P;
        }
    }

    /// <summary>Exact bounds of a cubic Bézier: its end points and the roots of the derivative per axis.</summary>
    private static void AddCubic(ref Box2 box, Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
        box.Add(p3);
        Span<double> roots = stackalloc double[4];
        var count = DerivativeRoots(p0.X, p1.X, p2.X, p3.X, roots);
        count += DerivativeRoots(p0.Y, p1.Y, p2.Y, p3.Y, roots[count..]);
        for (var i = 0; i < count; i++)
        {
            var t = roots[i];
            var mt = 1 - t;
            var a = mt * mt * mt;
            var b = 3 * mt * mt * t;
            var c = 3 * mt * t * t;
            var d = t * t * t;
            box.Add(new Vec2(
                a * p0.X + b * p1.X + c * p2.X + d * p3.X,
                a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y));
        }
    }

    private static int DerivativeRoots(double p0, double p1, double p2, double p3, Span<double> roots)
    {
        // B'(t)/3 = a t² + b t + c
        var a = -p0 + 3 * p1 - 3 * p2 + p3;
        var b = 2 * (p0 - 2 * p1 + p2);
        var c = p1 - p0;
        var count = 0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
                count = Keep(-c / b, roots, count);
            return count;
        }
        var disc = b * b - 4 * a * c;
        if (disc < 0)
            return 0;
        var sq = Math.Sqrt(disc);
        count = Keep((-b + sq) / (2 * a), roots, count);
        count = Keep((-b - sq) / (2 * a), roots, count);
        return count;

        static int Keep(double t, Span<double> roots, int count)
        {
            if (t > 0 && t < 1)
                roots[count++] = t;
            return count;
        }
    }
}

/// <summary>An axis-aligned bounding box in WCS; empty until the first point is added.</summary>
internal struct Box2
{
    public double MinX, MinY, MaxX, MaxY;
    public bool IsEmpty;

    public static Box2 Empty => new() { MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue, IsEmpty = true };

    public readonly double Width => IsEmpty ? 0 : MaxX - MinX;
    public readonly double Height => IsEmpty ? 0 : MaxY - MinY;

    public void Add(Vec2 p)
    {
        // Garbage coordinates (NaN, 1e300 from corrupt files) must not blow up the page fit.
        if (!p.IsFinite || Math.Abs(p.X) > 1e15 || Math.Abs(p.Y) > 1e15)
            return;
        MinX = Math.Min(MinX, p.X);
        MinY = Math.Min(MinY, p.Y);
        MaxX = Math.Max(MaxX, p.X);
        MaxY = Math.Max(MaxY, p.Y);
        IsEmpty = false;
    }
}
