using Avalonia;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class DonutGeometryTests
{
    private static readonly Point C = new(200, 200);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public void Slices_cover_the_full_circle(int count)
    {
        Assert.Equal(2 * Math.PI, DonutGeometry.Span(count) * count, 6);

        // Every direction maps to exactly one slice, and each slice's centre maps back to itself.
        for (var i = 0; i < count; i++)
            Assert.Equal(i, DonutGeometry.IndexAt(DonutGeometry.MidAngle(i, count), count));
        for (var deg = 0; deg < 360; deg++)
            Assert.InRange(DonutGeometry.IndexAt(deg * Math.PI / 180, count), 0, count - 1);
    }

    [Fact]
    public void First_slice_is_centred_at_twelve_o_clock()
    {
        // Straight up belongs to slice 0; slightly left of up still belongs to slice 0 (it straddles the top).
        Assert.Equal(0, DonutGeometry.HitTest(C, new Point(200, 80), 60, 150, 6).Index);
        Assert.Equal(0, DonutGeometry.HitTest(C, new Point(190, 80), 60, 150, 6).Index);
        // Right side (3 o'clock) with 4 slices is slice 1, bottom is 2, left is 3.
        Assert.Equal(1, DonutGeometry.HitTest(C, new Point(320, 200), 60, 150, 4).Index);
        Assert.Equal(2, DonutGeometry.HitTest(C, new Point(200, 320), 60, 150, 4).Index);
        Assert.Equal(3, DonutGeometry.HitTest(C, new Point(80, 200), 60, 150, 4).Index);
    }

    [Fact]
    public void A_groups_ring_fans_out_around_its_slice()
    {
        var subSpan = DonutGeometry.SubSpan(DonutGeometry.Span(9), 3);
        var parentMid = DonutGeometry.MidAngle(0, 9); // 12 o'clock: the fan straddles 0 / 2π
        for (var k = 0; k < 3; k++)
        {
            var p = DonutGeometry.PointAt(C, 190, DonutGeometry.SubMidAngle(parentMid, k, 3, subSpan));
            Assert.Equal(k, DonutGeometry.SubIndexAt(C, p, 170, 210, parentMid, 3, subSpan));
        }
        Assert.Equal(-1, DonutGeometry.SubIndexAt(C, DonutGeometry.PointAt(C, 190, Math.PI), 170, 210, parentMid, 3, subSpan));
        Assert.Equal(-1, DonutGeometry.SubIndexAt(C, DonutGeometry.PointAt(C, 150, parentMid), 170, 210, parentMid, 3, subSpan));
        // The fan covers its slice and a quarter of each neighbour.
        Assert.Equal(DonutGeometry.Span(9) * 1.5, subSpan * 3, 6);
    }

    [Fact]
    public void A_groups_ring_never_overlaps_itself_and_keeps_room_for_labels()
    {
        // A donut of just the group: 1.5 turns would put slices on top of each other; it goes round once at most.
        Assert.Equal(2 * Math.PI, DonutGeometry.SubSpan(DonutGeometry.Span(1), 3, 190) * 3, 6);
        Assert.Equal(2 * Math.PI, DonutGeometry.SubSpan(DonutGeometry.Span(1), 2) * 2, 6);
        // A crowded donut (12 slices, a group of 6): each ring slice stays wide enough for its label.
        Assert.Equal(DonutGeometry.MinSubWidth, DonutGeometry.SubSpan(DonutGeometry.Span(12), 6, 190) * 190, 6);
        // Roomy enough already: unchanged.
        Assert.Equal(DonutGeometry.Span(4) * 1.5 / 3, DonutGeometry.SubSpan(DonutGeometry.Span(4), 3, 190), 6);
    }

    [Fact]
    public void Hole_ring_and_outside_are_classified()
    {
        Assert.Equal(DonutHitKind.Center, DonutGeometry.HitTest(C, new Point(210, 205), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Segment, DonutGeometry.HitTest(C, new Point(200, 100), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Outside, DonutGeometry.HitTest(C, new Point(200, 10), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Segment, DonutGeometry.HitTest(C, new Point(200, 45), 60, 150, 6, slack: 8).Kind);
        Assert.Equal(DonutHitKind.EmptyRing, DonutGeometry.HitTest(C, new Point(200, 100), 60, 150, 0).Kind);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    public void A_sharp_slice_keeps_the_outline_it_always_had(int count)
    {
        var mid = DonutGeometry.MidAngle(1, count);
        var span = DonutGeometry.Span(count);
        var outline = DonutGeometry.Wedge(C, 63, 150, mid, span, 3, cornerRadius: 0);

        // Outer arc clockwise, side, inner arc back: exactly the slice drawn before the softness reached the donut.
        var outerHalf = span / 2 - 1.5 / 150;
        var innerHalf = span / 2 - 1.5 / 63;
        AssertNear(DonutGeometry.PointAt(C, 150, mid - outerHalf), outline.Start);
        Assert.Equal(3, outline.Steps.Count);
        AssertStep(new OutlineStep(DonutGeometry.PointAt(C, 150, mid + outerHalf), 150, outerHalf * 2 > Math.PI, true), outline.Steps[0]);
        AssertStep(new OutlineStep(DonutGeometry.PointAt(C, 63, mid + innerHalf)), outline.Steps[1]);
        AssertStep(new OutlineStep(DonutGeometry.PointAt(C, 63, mid - innerHalf), 63, innerHalf * 2 > Math.PI, false), outline.Steps[2]);
        Assert.Equal(0, outline.CornerRadius);
    }

    [Theory]
    [InlineData(2, 14)]
    [InlineData(6, 14)]
    [InlineData(10, 14)]
    [InlineData(12, 7)]
    public void A_rounded_slice_stays_inside_its_sharp_shape(int count, double corner)
    {
        const double inner = 63, outer = 150, gap = 3;
        var mid = DonutGeometry.MidAngle(2, count);
        var span = DonutGeometry.Span(count);
        var outline = DonutGeometry.Wedge(C, inner, outer, mid, span, gap, corner);

        Assert.Equal(corner, outline.CornerRadius, 6);
        Assert.Equal(8, outline.Steps.Count); // two arcs, two sides, four rounded corners
        Assert.Equal(4, outline.Steps.Count(s => s.IsArc && s.ArcRadius == corner && s.Clockwise));

        foreach (var p in OutlinePoints(outline))
        {
            var distance = Math.Sqrt(Math.Pow(p.X - C.X, 2) + Math.Pow(p.Y - C.Y, 2));
            Assert.InRange(distance, inner - 1e-6, outer + 1e-6);
            // Inside the sector, and no closer to its edge than half the gap: the gap between slices stays the same.
            var offset = Math.IEEERemainder(DonutGeometry.AngleOf(C, p) - mid, 2 * Math.PI);
            Assert.InRange(Math.Abs(offset), 0, span / 2);
            Assert.True(distance * Math.Sin(span / 2 - Math.Abs(offset)) >= gap / 2 - 0.05, $"{p} is in the gap");
        }

        // The corners are cut: the sharp outline's first corner is no longer on it; the edges still run along the
        // donut's circles.
        var sharp = DonutGeometry.Wedge(C, inner, outer, mid, span, gap);
        Assert.DoesNotContain(OutlinePoints(outline), p => Near(p, sharp.Start, 0.5));
        Assert.Equal(outer, Math.Sqrt(Math.Pow(outline.Start.X - C.X, 2) + Math.Pow(outline.Start.Y - C.Y, 2)), 6);
        Assert.Contains(outline.Steps, s => s.ArcRadius == outer && s.Clockwise);
        Assert.Contains(outline.Steps, s => s.ArcRadius == inner && !s.Clockwise);
    }

    [Fact]
    public void Thin_or_short_slices_get_only_the_rounding_that_fits()
    {
        // A crowded donut: at the hole a slice is narrower than two corners of 14.
        var thin = DonutGeometry.Wedge(C, 40, 150, 0, DonutGeometry.Span(16), 3, 14);
        Assert.InRange(thin.CornerRadius, 1, 14 - 0.01);
        Assert.Equal(8, thin.Steps.Count);

        // A thin band (a big hole): the corners are at most half its depth.
        var shallow = DonutGeometry.Wedge(C, 135, 150, 0, DonutGeometry.Span(4), 3, 14);
        Assert.InRange(shallow.CornerRadius, 1, 7.5);

        // A slice that is barely there (the edit preview growing in): sharp, not a broken shape.
        var sliver = DonutGeometry.Wedge(C, 63, 150, 0, 0.02, 3, 14);
        Assert.Equal(0, sliver.CornerRadius);
        Assert.Equal(3, sliver.Steps.Count);

        // Larger slices get more of it, never more than asked.
        var wider = DonutGeometry.Wedge(C, 40, 150, 0, DonutGeometry.Span(12), 3, 14);
        Assert.True(wider.CornerRadius > thin.CornerRadius);
    }

    /// <summary>The outline's corners plus the middle of each rounded corner's arc.</summary>
    private static List<Point> OutlinePoints(WedgeOutline outline)
    {
        var points = new List<Point> { outline.Start };
        var from = outline.Start;
        foreach (var step in outline.Steps)
        {
            if (step.IsArc && step.ArcRadius == outline.CornerRadius)
            {
                // Small clockwise arc: its middle is on the circle through both ends, on the right of the chord.
                var chord = new Vector(step.To.X - from.X, step.To.Y - from.Y);
                var half = chord.Length / 2;
                var normal = new Vector(-chord.Y, chord.X) / chord.Length;
                var depth = Math.Sqrt(Math.Max(0, step.ArcRadius * step.ArcRadius - half * half));
                var middle = from + chord / 2;
                points.Add(middle - normal * (step.ArcRadius - depth));
            }
            points.Add(step.To);
            from = step.To;
        }
        return points;
    }

    private static bool Near(Point a, Point b, double tolerance = 1e-6) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private static void AssertNear(Point expected, Point actual) =>
        Assert.True(Near(expected, actual), $"expected {expected}, got {actual}");

    private static void AssertStep(OutlineStep expected, OutlineStep actual)
    {
        AssertNear(expected.To, actual.To);
        Assert.Equal(expected.ArcRadius, actual.ArcRadius, 6);
        Assert.Equal(expected.IsLargeArc, actual.IsLargeArc);
        Assert.Equal(expected.Clockwise, actual.Clockwise);
    }
}
