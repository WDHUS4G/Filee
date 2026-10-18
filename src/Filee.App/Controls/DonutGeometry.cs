// Pure math for the donut: slice angles, hit testing and slice outlines. Unit tested (DonutGeometryTests).
//
// Angles are measured clockwise from 12 o'clock, in radians. Slice i is centred on i·span,
// so the first preset sits at the top and the rest follow clockwise.

using Avalonia;

namespace Filee.App.Controls;

/// <summary>What is under a point of the donut.</summary>
public enum DonutHitKind
{
    /// <summary>Outside the donut.</summary>
    Outside,
    /// <summary>The hole in the middle (cancel / close).</summary>
    Center,
    /// <summary>A slice; <see cref="DonutHit.Index"/> is valid.</summary>
    Segment,
    /// <summary>The ring of a donut without slices (drop target for the first preset).</summary>
    EmptyRing,
    /// <summary>The ✎ edit button of the hovered slice (edit mode only).</summary>
    EditButton,
    /// <summary>The progress ring shown after a drop.</summary>
    ProgressRing,
    /// <summary>The × (cancel) button on the progress ring.</summary>
    ProgressCancel,

    /// <summary>A slice of the small ring a group slice fans out; <see cref="DonutHit.Index"/> is the group, <see cref="DonutHit.Sub"/> the slice.</summary>
    SubSegment,
}

/// <param name="Sub">With <see cref="DonutHitKind.SubSegment"/>: which of the group's presets.</param>
public readonly record struct DonutHit(DonutHitKind Kind, int Index = -1, int Sub = -1);

/// <summary>Geometry helpers.</summary>
public static class DonutGeometry
{
    /// <summary>Angle covered by one of <paramref name="count"/> slices.</summary>
    public static double Span(int count) => count <= 0 ? 2 * Math.PI : 2 * Math.PI / count;

    /// <summary>Centre angle of slice <paramref name="index"/>.</summary>
    public static double MidAngle(int index, int count) => index * Span(count);

    /// <summary>Converts a clockwise-from-top angle and radius into a point relative to <paramref name="center"/>.</summary>
    public static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

    /// <summary>Clockwise-from-top angle (0..2π) of <paramref name="p"/> around <paramref name="center"/>.</summary>
    public static double AngleOf(Point center, Point p)
    {
        var a = Math.Atan2(p.X - center.X, center.Y - p.Y);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Index of the slice containing the given angle.</summary>
    public static int IndexAt(double angle, int count)
    {
        if (count <= 0)
            return -1;
        var span = Span(count);
        return (int)Math.Floor((angle + span / 2) / span) % count;
    }

    /// <summary>How far a group's fan reaches into each neighbouring slice, as a share of a slice.</summary>
    public const double SubOverhang = 0.25;

    /// <summary>Narrowest a slice of a group's ring gets (along its middle), so its label still fits.</summary>
    public const double MinSubWidth = 44;

    /// <summary>
    /// Width of one slice of a group's outer ring: the fan covers the group's slice and a quarter of each neighbour
    /// (<see cref="SubOverhang"/>), split evenly, so it stays close to the slice it belongs to. On a crowded donut it
    /// widens to keep each slice <see cref="MinSubWidth"/> wide at <paramref name="midRadius"/>; it never goes round
    /// more than once (a donut of one slice), where slices would lie on top of each other.
    /// </summary>
    public static double SubSpan(double parentSpan, int count, double midRadius = 0)
    {
        if (count <= 0)
            return 0;
        var span = parentSpan * (1 + 2 * SubOverhang) / count;
        if (midRadius > 0)
            span = Math.Max(span, MinSubWidth / midRadius);
        return Math.Min(span, 2 * Math.PI / count);
    }

    /// <summary>Centre angle of slice <paramref name="sub"/> of a fan of <paramref name="count"/>, centred on <paramref name="parentMid"/>.</summary>
    public static double SubMidAngle(double parentMid, int sub, int count, double subSpan) =>
        parentMid + (sub - (count - 1) / 2.0) * subSpan;

    /// <summary>
    /// Which slice of a group's fan (between <paramref name="innerRadius"/> and <paramref name="outerRadius"/>) holds
    /// <paramref name="p"/>, or -1.
    /// </summary>
    public static int SubIndexAt(Point center, Point p, double innerRadius, double outerRadius, double parentMid, int count, double subSpan)
    {
        var distance = Math.Sqrt(Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2));
        if (count <= 0 || distance < innerRadius || distance > outerRadius)
            return -1;
        // Signed angle from the fan's centre, wrapped to -π..π so a fan across 12 o'clock works.
        var delta = AngleOf(center, p) - parentMid;
        delta = Math.IEEERemainder(delta, 2 * Math.PI);
        var sub = (int)Math.Floor(delta / subSpan + count / 2.0);
        return sub >= 0 && sub < count ? sub : -1;
    }

    /// <summary>Classifies a point. <paramref name="slack"/> extends the outer radius to include popped-out slices.</summary>
    public static DonutHit HitTest(Point center, Point p, double innerRadius, double outerRadius, int count, double slack = 0)
    {
        var distance = Math.Sqrt(Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2));
        if (distance < innerRadius)
            return new DonutHit(DonutHitKind.Center);
        if (distance > outerRadius + slack)
            return new DonutHit(DonutHitKind.Outside);
        if (count == 0)
            return new DonutHit(DonutHitKind.EmptyRing);
        return new DonutHit(DonutHitKind.Segment, IndexAt(AngleOf(center, p), count));
    }
}
