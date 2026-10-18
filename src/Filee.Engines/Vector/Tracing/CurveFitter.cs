// Step 2 of tracing: a pixel outline becomes a smooth closed path of straight lines and cubic Béziers.
//
//   1. Sampling: the staircase of pixel edges is replaced by points on it. Short runs (the steps of a slanted or
//      curved edge) contribute their midpoint, which lies on the edge the pixels approximate; long runs contribute
//      points along themselves. A pixel corner between two long runs (a rectangle), and the two corners of a short
//      run between long ones that both turn the same way (the end of a thin line), stay corners.
//   2. Corners: a point where the direction turns sharply within about two pixels, or turns less but all at once
//      (most of the turn seen four pixels either side already happens within two), which a small circle doesn't.
//   3. Smoothing: points between corners are averaged with their neighbours (1-2-1) to calm the remaining jaggies.
//   4. Straight stretches: runs of points that stay within half a pixel of a line and don't bend become lines.
//   5. Fitting: the rest between corners and straight stretches becomes cubic Béziers fitted by least squares with
//      Newton–Raphson reparameterization, split where the error is largest (Philip J. Schneider, "An Algorithm for
//      Automatically Fitting Digitized Curves", Graphics Gems, 1990). Curves leave and join straight stretches in
//      their direction, and meet each other with matching tangents, except at corners.

namespace Filee.Engines.Vector.Tracing;

/// <summary>A 2D point or vector in pixels.</summary>
internal readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator -(Vec a) => new(-a.X, -a.Y);
    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s);
    public static Vec operator *(double s, Vec a) => new(a.X * s, a.Y * s);

    public double Dot(Vec other) => X * other.X + Y * other.Y;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public Vec Normalized()
    {
        var length = Length;
        return length < 1e-12 ? new Vec(0, 0) : new Vec(X / length, Y / length);
    }
}

/// <summary>One piece of a traced path: a line to <see cref="End"/>, or a cubic Bézier with two control points.</summary>
internal readonly record struct PathSegment(bool IsLine, Vec Control1, Vec Control2, Vec End)
{
    public static PathSegment Line(Vec end) => new(true, end, end, end);
    public static PathSegment Cubic(Vec c1, Vec c2, Vec end) => new(false, c1, c2, end);
}

/// <summary>A closed path starting at <see cref="Start"/>.</summary>
internal sealed record TracedPath(Vec Start, List<PathSegment> Segments);

/// <summary>Fits smooth paths to pixel outlines.</summary>
internal static class CurveFitter
{
    /// <summary>Distance in pixels over which a turn counts as one corner.</summary>
    private const double CornerReach = 2.0;

    /// <summary>Points sampled along a long run are at most this far apart (close enough to be smoothed).</summary>
    private const double SampleSpacing = 2.0;

    private const int MaxReparameterizations = 4;

    public static TracedPath Fit(PixelOutline outline, TraceSettings settings)
    {
        var (points, hard) = Sample(outline, settings.CornerRun);
        var corners = FindCorners(points, hard, settings.CornerAngle);
        Smooth(points, corners, settings.SmoothingPasses);

        var tolerance = settings.CurveTolerance;
        var segments = new List<PathSegment>();
        var count = points.Count;
        var first = Array.IndexOf(corners, true);

        if (first < 0)
        {
            // A smooth closed curve. With a straight stretch, start at it and go round back into it.
            var loop = Rotated(points, 0);
            var stretches = FindStretches(loop, 0, count);
            if (stretches.Count > 0)
            {
                loop = Rotated(points, stretches[0].Start);
                var direction = (loop[stretches[0].End - stretches[0].Start] - loop[0]).Normalized();
                FitBetween(loop, 0, count, direction, -direction, FindStretches(loop, 0, count), tolerance, segments);
                return new TracedPath(loop[0], segments);
            }

            // Otherwise two halves that meet with matching tangents at both ends.
            var half = count / 2;
            var atStart = CenterTangent(loop, 0, 0, count - 1, closed: true);
            var atHalf = CenterTangent(loop, half, 0, count);
            FitCubic(loop, 0, half, -atStart, atHalf, tolerance, segments);
            FitCubic(loop, half, count, -atHalf, atStart, tolerance, segments);
            return new TracedPath(loop[0], segments);
        }

        // Start at the first corner and go round once, back to it.
        var path = Rotated(points, first);
        var from = 0;
        for (var i = 1; i <= count; i++)
        {
            if (i < count && !corners[(first + i) % count])
                continue;
            FitPiece(path, from, i, tolerance, segments);
            from = i;
        }
        return new TracedPath(path[0], segments);
    }

    /// <summary>The closed list of points starting at <paramref name="start"/>, with the first point repeated at the end.</summary>
    private static Vec[] Rotated(List<Vec> points, int start)
    {
        var count = points.Count;
        var result = new Vec[count + 1];
        for (var i = 0; i <= count; i++)
            result[i] = points[(start + i) % count];
        return result;
    }

    // ───────────────────────── 1. Sampling ─────────────────────────

    /// <summary>Points along the outline and whether each is a corner that must stay sharp.</summary>
    internal static (List<Vec> Points, List<bool> Hard) Sample(PixelOutline outline, int cornerRun)
    {
        var n = outline.Count;
        var lengths = new int[n];
        var turns = new int[n]; // at corner i: +1 turns clockwise on screen, -1 counter-clockwise
        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            lengths[i] = Math.Abs(outline.Xs[next] - outline.Xs[i]) + Math.Abs(outline.Ys[next] - outline.Ys[i]);
        }
        for (var i = 0; i < n; i++)
        {
            var previous = (i + n - 1) % n;
            var next = (i + 1) % n;
            long inX = outline.Xs[i] - outline.Xs[previous], inY = outline.Ys[i] - outline.Ys[previous];
            long outX = outline.Xs[next] - outline.Xs[i], outY = outline.Ys[next] - outline.Ys[i];
            turns[i] = Math.Sign(inX * outY - inY * outX);
        }

        // Corner i lies between run i-1 (arriving) and run i (leaving).
        var hardCorner = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var before = lengths[(i + n - 1) % n];
            var after = lengths[i];
            if (before >= cornerRun && after >= cornerRun)
                hardCorner[i] = true;

            // The end of a thin line: a short run whose two corners turn the same way, between two long runs.
            var next = (i + 1) % n;
            if (after <= 2 && turns[i] == turns[next] && before >= cornerRun && lengths[next] >= cornerRun)
            {
                hardCorner[i] = true;
                hardCorner[next] = true;
            }
        }

        var points = new List<Vec>(n * 2);
        var hard = new List<bool>(n * 2);
        for (var i = 0; i < n; i++)
        {
            var start = new Vec(outline.Xs[i], outline.Ys[i]);
            var next = (i + 1) % n;
            var end = new Vec(outline.Xs[next], outline.Ys[next]);
            if (hardCorner[i])
            {
                points.Add(start);
                hard.Add(true);
            }
            var length = lengths[i];
            var direction = (end - start) * (1.0 / length);
            if (length <= 2)
            {
                points.Add(start + direction * (length / 2.0));
                hard.Add(false);
                continue;
            }
            // From one pixel after the start to one before the end: the run's ends may be steps of a slanted edge.
            var samples = Math.Max(2, (int)Math.Ceiling((length - 2) / SampleSpacing) + 1);
            for (var s = 0; s < samples; s++)
            {
                var offset = 1 + (length - 2) * s / (double)(samples - 1);
                points.Add(start + direction * offset);
                hard.Add(false);
            }
        }
        return (points, hard);
    }

    // ───────────────────────── 2. Corners ─────────────────────────

    internal static bool[] FindCorners(List<Vec> points, List<bool> hard, double cornerAngle)
    {
        var count = points.Count;
        var corners = new bool[count];
        for (var i = 0; i < count; i++)
            corners[i] = hard[i];
        if (count < 6)
            return corners;

        var limit = count / 3;
        var angles = new double[count];
        var reachBack = new int[count];
        var reachForward = new int[count];
        var candidate = new bool[count];
        var gentleAngle = cornerAngle - 20;
        for (var i = 0; i < count; i++)
        {
            reachBack[i] = Reach(points, i, -1, CornerReach, limit);
            reachForward[i] = Reach(points, i, +1, CornerReach, limit);
            angles[i] = Turn(points[reachBack[i]], points[i], points[reachForward[i]]);
            if (hard[i] || angles[i] <= gentleAngle)
                continue;
            if (angles[i] > cornerAngle)
            {
                candidate[i] = true;
                continue;
            }
            // A gentler turn is a corner when it happens at once: seen from twice as far it hardly grows (on a circle
            // it would double).
            var wide = Turn(points[Reach(points, i, -1, 2 * CornerReach, limit)], points[i],
                points[Reach(points, i, +1, 2 * CornerReach, limit)]);
            candidate[i] = angles[i] >= 0.65 * wide;
        }

        for (var i = 0; i < count; i++)
        {
            if (!candidate[i])
                continue;
            // Only the sharpest point of a turn, and none right next to a corner that is kept anyway.
            var best = !hard[reachBack[i]] && !hard[reachForward[i]];
            for (var j = reachBack[i]; j != reachForward[i] && best; j = (j + 1) % count)
            {
                if (j == i)
                    continue;
                if (hard[j] || (candidate[j] && (angles[j] > angles[i] || (angles[j] == angles[i] && j < i))))
                    best = false;
            }
            if (best)
                corners[i] = true;
        }
        return corners;
    }

    /// <summary>How many degrees the direction turns at <paramref name="here"/>.</summary>
    private static double Turn(Vec before, Vec here, Vec after)
    {
        var incoming = here - before;
        var outgoing = after - here;
        var denominator = incoming.Length * outgoing.Length;
        return denominator < 1e-12 ? 0 : Math.Acos(Math.Clamp(incoming.Dot(outgoing) / denominator, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>The first point at least <paramref name="distance"/> away from point <paramref name="i"/>.</summary>
    private static int Reach(List<Vec> points, int i, int step, double distance, int limit)
    {
        var count = points.Count;
        var j = i;
        for (var steps = 0; steps < limit; steps++)
        {
            j = (j + step + count) % count;
            if ((points[j] - points[i]).LengthSquared >= distance * distance)
                break;
        }
        return j;
    }

    // ───────────────────────── 3. Smoothing ─────────────────────────

    private static void Smooth(List<Vec> points, bool[] corners, int passes)
    {
        var count = points.Count;
        if (count < 5)
            return;
        var next = new Vec[count];
        for (var pass = 0; pass < passes; pass++)
        {
            for (var i = 0; i < count; i++)
            {
                var before = points[(i + count - 1) % count];
                var after = points[(i + 1) % count];
                var here = points[i];
                // Points sampled along long runs lie on the edge already.
                next[i] = corners[i] || (here - before).LengthSquared > 6.25 || (after - here).LengthSquared > 6.25
                    ? here
                    : (before + here * 2 + after) * 0.25;
            }
            for (var i = 0; i < count; i++)
                points[i] = next[i];
        }
    }

    // ───────────────────────── 4. Fitting ─────────────────────────

    /// <summary>Fits the points from <paramref name="first"/> to <paramref name="last"/>, two corners.</summary>
    private static void FitPiece(Vec[] d, int first, int last, double tolerance, List<PathSegment> output)
    {
        // Straight within a pixel (the jaggies of a thresholded edge) or within the tolerance.
        if (last - first <= 1 || IsStraight(d, first, last, Math.Max(1.0, tolerance)))
        {
            output.Add(PathSegment.Line(d[last]));
            return;
        }
        FitBetween(d, first, last, EndTangent(d, first, last, +1), EndTangent(d, last, first, -1),
            FindStretches(d, first, last), tolerance, output);
    }

    /// <summary>
    /// Fits <paramref name="first"/>..<paramref name="last"/>: the straight stretches as lines, the parts between
    /// them as curves that leave and join the lines in their direction.
    /// </summary>
    private static void FitBetween(Vec[] d, int first, int last, Vec startTangent, Vec endTangent,
        List<(int Start, int End)> stretches, double tolerance, List<PathSegment> output)
    {
        var cursor = first;
        var tangent = startTangent;
        foreach (var (start, end) in stretches)
        {
            var direction = (d[end] - d[start]).Normalized();
            if (start > cursor)
                FitCubic(d, cursor, start, tangent, -direction, tolerance, output);
            output.Add(PathSegment.Line(d[end]));
            cursor = end;
            tangent = direction;
        }
        if (cursor < last)
            FitCubic(d, cursor, last, tangent, endTangent, tolerance, output);
    }

    /// <summary>Length, in pixels, a straight stretch must at least have.</summary>
    private const double MinStretch = 10;

    /// <summary>How far, in pixels, the points of a straight stretch may be from it.</summary>
    private const double StretchTolerance = 0.5;

    /// <summary>The most a straight stretch may bend, in radians (2°): a gentle arc stays a curve.</summary>
    private const double StretchBend = 0.035;

    /// <summary>The straight stretches among the points <paramref name="first"/>..<paramref name="last"/>, in order.</summary>
    private static List<(int Start, int End)> FindStretches(Vec[] d, int first, int last)
    {
        var stretches = new List<(int, int)>();
        var i = first;
        while (i < last - 1)
        {
            var end = LongestStretch(d, i, last);
            if ((d[end] - d[i]).Length >= MinStretch && !Bends(d, i, end))
            {
                stretches.Add((i, end));
                i = end;
            }
            else
            {
                i++;
            }
        }
        return stretches;
    }

    /// <summary>The farthest point such that the points from <paramref name="start"/> to it lie near their chord.</summary>
    private static int LongestStretch(Vec[] d, int start, int last)
    {
        var end = start + 1;
        for (var j = start + 2; j <= last; j++)
        {
            if (!IsStraight(d, start, j, StretchTolerance))
                break;
            end = j;
        }
        return end;
    }

    /// <summary>
    /// True when the points bend like an arc rather than scatter around a line: the least-squares sagitta of a
    /// parabola through the chord's ends, compared with the chord.
    /// </summary>
    private static bool Bends(Vec[] d, int start, int end)
    {
        var chord = d[end] - d[start];
        var length = chord.Length;
        var normal = new Vec(-chord.Y / length, chord.X / length);
        double numerator = 0, denominator = 0;
        for (var i = start + 1; i < end; i++)
        {
            var offset = d[i] - d[start];
            var t = Math.Clamp(offset.Dot(chord) / (length * length), 0, 1);
            var shape = 4 * t * (1 - t);
            numerator += offset.Dot(normal) * shape;
            denominator += shape * shape;
        }
        var sagitta = denominator > 0 ? numerator / denominator : 0;
        // An arc of chord c and sagitta s turns by about 8 s / c.
        return Math.Abs(8 * sagitta / length) > StretchBend;
    }

    private static bool IsStraight(Vec[] d, int first, int last, double tolerance)
    {
        var a = d[first];
        var chord = d[last] - a;
        var length = chord.Length;
        if (length < 1e-9)
            return false;
        var normal = new Vec(-chord.Y / length, chord.X / length);
        for (var i = first + 1; i < last; i++)
        {
            var offset = d[i] - a;
            if (Math.Abs(offset.Dot(normal)) > tolerance)
                return false;
            var along = offset.Dot(chord) / (length * length);
            if (along < -0.05 || along > 1.05)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Unit tangent at end <paramref name="at"/> of the piece, pointing into it: towards the first point a few pixels
    /// along (more stable than the next point alone).
    /// </summary>
    private static Vec EndTangent(Vec[] d, int at, int other, int step)
    {
        var i = at;
        while (i != other)
        {
            i += step;
            if ((d[i] - d[at]).LengthSquared >= 6.25)
                break;
        }
        return (d[i] - d[at]).Normalized();
    }

    /// <summary>
    /// Unit tangent through point <paramref name="center"/>, pointing back (towards earlier points), from the points
    /// about 1.5 pixels before and after it. <paramref name="closed"/> wraps around a closed list of
    /// <paramref name="last"/> + 1 points.
    /// </summary>
    private static Vec CenterTangent(Vec[] d, int center, int first, int last, bool closed = false)
    {
        int Clamp(int i) => closed ? ((i % (last + 1)) + last + 1) % (last + 1) : Math.Clamp(i, first, last);
        var span = closed ? last + 1 : last - first + 1;
        int back = center, forward = center;
        for (var steps = 0; steps < span / 3 + 1; steps++)
        {
            var candidate = Clamp(back - 1);
            if (candidate == back)
                break;
            back = candidate;
            if ((d[back] - d[center]).LengthSquared >= 2.25)
                break;
        }
        for (var steps = 0; steps < span / 3 + 1; steps++)
        {
            var candidate = Clamp(forward + 1);
            if (candidate == forward)
                break;
            forward = candidate;
            if ((d[forward] - d[center]).LengthSquared >= 2.25)
                break;
        }
        var tangent = (d[back] - d[forward]).Normalized();
        return tangent.LengthSquared > 0 ? tangent : (d[Clamp(center - 1)] - d[Clamp(center + 1)]).Normalized();
    }

    /// <summary>
    /// Fits cubic Béziers to the points <paramref name="first"/>..<paramref name="last"/> within
    /// <paramref name="tolerance"/> pixels. <paramref name="startTangent"/> points into the piece,
    /// <paramref name="endTangent"/> points back from its end.
    /// </summary>
    private static void FitCubic(Vec[] d, int first, int last, Vec startTangent, Vec endTangent, double tolerance, List<PathSegment> output)
    {
        var error = tolerance * tolerance;
        // Depth-first with an explicit stack (long outlines would otherwise recurse thousands of levels deep); the
        // left half is handled first so segments come out in order.
        var pending = new Stack<(int First, int Last, Vec T1, Vec T2)>();
        pending.Push((first, last, startTangent, endTangent));
        while (pending.Count > 0)
        {
            var (a, b, t1, t2) = pending.Pop();
            if (b - a == 1)
            {
                var distance = (d[b] - d[a]).Length / 3;
                output.Add(PathSegment.Cubic(d[a] + t1 * distance, d[b] + t2 * distance, d[b]));
                continue;
            }

            var u = ChordLengths(d, a, b);
            var bezier = GenerateBezier(d, a, b, u, t1, t2);
            var (maxError, split) = MaxError(d, a, b, bezier, u);
            if (maxError > error && maxError < error * 4)
            {
                for (var i = 0; i < MaxReparameterizations && maxError > error; i++)
                {
                    u = Reparameterize(d, a, b, u, bezier);
                    bezier = GenerateBezier(d, a, b, u, t1, t2);
                    (maxError, split) = MaxError(d, a, b, bezier, u);
                }
            }
            if (maxError <= error)
            {
                output.Add(PathSegment.Cubic(bezier[1], bezier[2], bezier[3]));
                continue;
            }

            var center = CenterTangent(d, split, a, b);
            pending.Push((split, b, -center, t2));
            pending.Push((a, split, t1, center));
        }
    }

    private static double[] ChordLengths(Vec[] d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = first + 1; i <= last; i++)
            u[i - first] = u[i - first - 1] + (d[i] - d[i - 1]).Length;
        var total = u[^1];
        for (var i = 1; i < u.Length; i++)
            u[i] = total > 0 ? u[i] / total : i / (double)(u.Length - 1);
        return u;
    }

    /// <summary>Least-squares control points for fixed end points and tangent directions.</summary>
    private static Vec[] GenerateBezier(Vec[] d, int first, int last, double[] u, Vec t1, Vec t2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        var p0 = d[first];
        var p3 = d[last];
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var mt = 1 - t;
            var b0 = mt * mt * mt;
            var b1 = 3 * t * mt * mt;
            var b2 = 3 * t * t * mt;
            var b3 = t * t * t;
            var a1 = t1 * b1;
            var a2 = t2 * b2;
            c00 += a1.Dot(a1);
            c01 += a1.Dot(a2);
            c11 += a2.Dot(a2);
            var rest = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += a1.Dot(rest);
            x1 += a2.Dot(rest);
        }

        var determinant = c00 * c11 - c01 * c01;
        double alpha1 = 0, alpha2 = 0;
        if (Math.Abs(determinant) > 1e-12)
        {
            alpha1 = (x0 * c11 - x1 * c01) / determinant;
            alpha2 = (c00 * x1 - c01 * x0) / determinant;
        }

        // Fall back to a third of the chord when the solution is degenerate or would loop.
        var chord = (p3 - p0).Length;
        var epsilon = 1e-6 * chord;
        if (alpha1 < epsilon || alpha2 < epsilon || alpha1 > chord * 2 || alpha2 > chord * 2)
            alpha1 = alpha2 = chord / 3;
        return [p0, p0 + t1 * alpha1, p3 + t2 * alpha2, p3];
    }

    private static (double Error, int Split) MaxError(Vec[] d, int first, int last, Vec[] bezier, double[] u)
    {
        var split = (first + last) / 2;
        var max = 0.0;
        for (var i = first + 1; i < last; i++)
        {
            var error = (Evaluate(bezier, u[i - first]) - d[i]).LengthSquared;
            if (error >= max)
            {
                max = error;
                split = i;
            }
        }
        return (max, split);
    }

    private static double[] Reparameterize(Vec[] d, int first, int last, double[] u, Vec[] bezier)
    {
        var result = new double[u.Length];
        for (var i = 0; i < u.Length; i++)
            result[i] = NewtonRaphson(bezier, d[first + i], u[i]);
        return result;
    }

    /// <summary>One Newton–Raphson step towards the parameter of the curve point nearest <paramref name="point"/>.</summary>
    private static double NewtonRaphson(Vec[] q, Vec point, double u)
    {
        var difference = Evaluate(q, u) - point;
        var mt = 1 - u;
        var first = 3 * (mt * mt * (q[1] - q[0]) + 2 * mt * u * (q[2] - q[1]) + u * u * (q[3] - q[2]));
        var second = 6 * (mt * (q[2] - q[1] * 2 + q[0]) + u * (q[3] - q[2] * 2 + q[1]));
        var numerator = difference.Dot(first);
        var denominator = first.Dot(first) + difference.Dot(second);
        if (Math.Abs(denominator) < 1e-12)
            return u;
        return Math.Clamp(u - numerator / denominator, 0, 1);
    }

    internal static Vec Evaluate(Vec[] q, double t)
    {
        var mt = 1 - t;
        return q[0] * (mt * mt * mt) + q[1] * (3 * t * mt * mt) + q[2] * (3 * t * t * mt) + q[3] * (t * t * t);
    }
}
