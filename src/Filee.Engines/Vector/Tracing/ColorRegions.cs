// Colour mode, before outlines are traced: the picture is reduced to a few colours (k-means) and cleaned up, so each
// colour forms regions worth an outline.
//
//   1. Quantization: every opaque pixel goes into one of 32 768 boxes (5 bits per channel). k-means runs on the boxes'
//      mean colours weighted by their pixel counts, so it sees every pixel at a fraction of the cost; the start
//      colours are picked greedily by weight × squared distance to the colours already picked (deterministic).
//   2. Edge bands: anti-aliasing leaves one-pixel bands of in-between colours along every edge. A pixel of a band
//      whose colour lies between the colours on either side joins the closer of the two; thin lines of a colour of
//      their own are kept.
//   3. Small regions: connected regions (4-connected) smaller than the minimum join the neighbouring region they
//      share the longest border with, smallest first.

namespace Filee.Engines.Vector.Tracing;

/// <summary>Colour quantization and region cleanup for colour tracing.</summary>
internal static class ColorRegions
{
    /// <summary>Label of transparent pixels.</summary>
    public const byte Transparent = 255;

    /// <summary>Pixels less opaque than this are transparent.</summary>
    public const byte OpaqueAlpha = 128;

    private const int MaxIterations = 24;

    /// <summary>
    /// Reduces the RGBA pixels to at most <paramref name="colors"/> colours. Returns the palette (0xRRGGBB) and one
    /// label per pixel: an index into the palette, or <see cref="Transparent"/>.
    /// </summary>
    public static (int[] Palette, byte[] Labels) Quantize(byte[] rgba, int width, int height, int colors, CancellationToken ct)
    {
        var pixelCount = width * height;
        var counts = new long[32768];
        var sums = new long[32768 * 3];
        for (var p = 0; p < pixelCount; p++)
        {
            var o = p * 4;
            if (rgba[o + 3] < OpaqueAlpha)
                continue;
            var box = Box(rgba[o], rgba[o + 1], rgba[o + 2]);
            counts[box]++;
            sums[box * 3] += rgba[o];
            sums[box * 3 + 1] += rgba[o + 1];
            sums[box * 3 + 2] += rgba[o + 2];
        }
        ct.ThrowIfCancellationRequested();

        // The occupied boxes, as weighted points.
        var boxes = new List<int>();
        for (var b = 0; b < counts.Length; b++)
            if (counts[b] > 0)
                boxes.Add(b);
        var labels = new byte[pixelCount];
        if (boxes.Count == 0)
        {
            Array.Fill(labels, Transparent);
            return ([], labels);
        }
        var points = new double[boxes.Count * 3];
        var weights = new double[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            weights[i] = counts[b];
            for (var c = 0; c < 3; c++)
                points[i * 3 + c] = sums[b * 3 + c] / (double)counts[b];
        }

        var k = Math.Min(colors, boxes.Count);
        var centers = InitialCenters(points, weights, k);
        var assignment = new int[boxes.Count];
        Array.Fill(assignment, -1);
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var changed = false;
            for (var i = 0; i < boxes.Count; i++)
            {
                var nearest = Nearest(centers, k, points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
                if (nearest != assignment[i])
                {
                    assignment[i] = nearest;
                    changed = true;
                }
            }
            if (!changed)
                break;
            Recenter(points, weights, assignment, centers, k);
        }
        Recenter(points, weights, assignment, centers, k);

        // Drop colours nothing was assigned to and build the palette.
        var used = new bool[k];
        foreach (var a in assignment)
            used[a] = true;
        var remap = new int[k];
        var palette = new List<int>();
        for (var c = 0; c < k; c++)
        {
            if (!used[c])
                continue;
            remap[c] = palette.Count;
            palette.Add((ToByte(centers[c * 3]) << 16) | (ToByte(centers[c * 3 + 1]) << 8) | ToByte(centers[c * 3 + 2]));
        }

        // Every pixel takes its box's colour.
        var boxLabel = new byte[32768];
        for (var i = 0; i < boxes.Count; i++)
            boxLabel[boxes[i]] = (byte)remap[assignment[i]];
        for (var p = 0; p < pixelCount; p++)
        {
            var o = p * 4;
            labels[p] = rgba[o + 3] < OpaqueAlpha ? Transparent : boxLabel[Box(rgba[o], rgba[o + 1], rgba[o + 2])];
        }
        return ([.. palette], labels);
    }

    private static int Box(byte r, byte g, byte b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    private static int ToByte(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    private static double[] InitialCenters(double[] points, double[] weights, int k)
    {
        var count = weights.Length;
        var centers = new double[k * 3];
        var distance = new double[count];
        Array.Fill(distance, double.MaxValue);
        // The most common colour first, then each time the colour with the largest weight × squared distance.
        var pick = 0;
        for (var i = 1; i < count; i++)
            if (weights[i] > weights[pick])
                pick = i;
        for (var c = 0; c < k; c++)
        {
            for (var j = 0; j < 3; j++)
                centers[c * 3 + j] = points[pick * 3 + j];
            var best = -1.0;
            var next = 0;
            for (var i = 0; i < count; i++)
            {
                var d = Distance(points, i, centers, c);
                if (d < distance[i])
                    distance[i] = d;
                var score = distance[i] * weights[i];
                if (score > best)
                {
                    best = score;
                    next = i;
                }
            }
            pick = next;
        }
        return centers;
    }

    private static void Recenter(double[] points, double[] weights, int[] assignment, double[] centers, int k)
    {
        var sums = new double[k * 3];
        var totals = new double[k];
        for (var i = 0; i < assignment.Length; i++)
        {
            var c = assignment[i];
            totals[c] += weights[i];
            for (var j = 0; j < 3; j++)
                sums[c * 3 + j] += points[i * 3 + j] * weights[i];
        }
        for (var c = 0; c < k; c++)
        {
            if (totals[c] <= 0)
                continue;
            for (var j = 0; j < 3; j++)
                centers[c * 3 + j] = sums[c * 3 + j] / totals[c];
        }
    }

    private static int Nearest(double[] centers, int k, double r, double g, double b)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var c = 0; c < k; c++)
        {
            double dr = centers[c * 3] - r, dg = centers[c * 3 + 1] - g, db = centers[c * 3 + 2] - b;
            var d = dr * dr + dg * dg + db * db;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best;
    }

    private static double Distance(double[] points, int i, double[] centers, int c)
    {
        double dr = points[i * 3] - centers[c * 3], dg = points[i * 3 + 1] - centers[c * 3 + 1], db = points[i * 3 + 2] - centers[c * 3 + 2];
        return dr * dr + dg * dg + db * db;
    }

    // ───────────────────────── Edge bands ─────────────────────────

    /// <summary>
    /// Pixels in one-pixel bands between two other colours join the closer side when their own colour lies between
    /// the two (anti-aliasing), so edges between colours don't get a sliver shape of their own.
    /// </summary>
    public static void MergeEdgeBands(byte[] labels, byte[] rgba, int[] palette, int width, int height)
    {
        var source = (byte[])labels.Clone();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                var own = source[p];
                if (own == Transparent)
                    continue;
                int a = -1, b = -1;
                var up = y > 0 ? source[p - width] : Transparent;
                var down = y < height - 1 ? source[p + width] : Transparent;
                var left = x > 0 ? source[p - 1] : Transparent;
                var right = x < width - 1 ? source[p + 1] : Transparent;
                if (up != own && down != own && up != Transparent && down != Transparent && up != down)
                    (a, b) = (up, down);
                else if (left != own && right != own && left != Transparent && right != Transparent && left != right)
                    (a, b) = (left, right);
                if (a < 0 || !IsBetween(palette[own], palette[a], palette[b]))
                    continue;
                var o = p * 4;
                var pixel = (rgba[o] << 16) | (rgba[o + 1] << 8) | rgba[o + 2];
                labels[p] = (byte)(ColorDistance(pixel, palette[a]) <= ColorDistance(pixel, palette[b]) ? a : b);
            }
        }
    }

    /// <summary>True when colour <paramref name="c"/> lies roughly on the way from <paramref name="a"/> to <paramref name="b"/>.</summary>
    internal static bool IsBetween(int c, int a, int b)
    {
        double ar = a >> 16, ag = (a >> 8) & 255, ab = a & 255;
        double dr = (b >> 16) - ar, dg = ((b >> 8) & 255) - ag, db = (b & 255) - ab;
        double cr = (c >> 16) - ar, cg = ((c >> 8) & 255) - ag, cb = (c & 255) - ab;
        var length2 = dr * dr + dg * dg + db * db;
        if (length2 < 1)
            return false;
        var t = (cr * dr + cg * dg + cb * db) / length2;
        if (t is < 0.08 or > 0.92)
            return false;
        double er = cr - t * dr, eg = cg - t * dg, eb = cb - t * db;
        return er * er + eg * eg + eb * eb < 0.09 * length2;
    }

    private static int ColorDistance(int x, int y)
    {
        int dr = (x >> 16) - (y >> 16), dg = ((x >> 8) & 255) - ((y >> 8) & 255), db = (x & 255) - (y & 255);
        return dr * dr + dg * dg + db * db;
    }

    // ───────────────────────── Small regions ─────────────────────────

    /// <summary>
    /// Regions (4-connected pixels of one label) smaller than <paramref name="minRegion"/> pixels join the neighbour
    /// they share the longest border with (the closer colour on a tie), smallest regions first.
    /// </summary>
    public static void MergeSmallRegions(byte[] labels, int[] palette, int width, int height, int minRegion, CancellationToken ct)
    {
        if (minRegion <= 1)
            return;
        var pixelCount = width * height;
        var region = new int[pixelCount];
        Array.Fill(region, -1);
        var regionLabel = new List<byte>();
        var regionArea = new List<int>();
        var regionSeed = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < pixelCount; start++)
        {
            if (region[start] >= 0)
                continue;
            if ((start & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            var id = regionLabel.Count;
            var label = labels[start];
            regionLabel.Add(label);
            regionSeed.Add(start);
            var area = 0;
            region[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                area++;
                int x = p % width, y = p / width;
                if (x > 0 && region[p - 1] < 0 && labels[p - 1] == label) { region[p - 1] = id; stack.Push(p - 1); }
                if (x < width - 1 && region[p + 1] < 0 && labels[p + 1] == label) { region[p + 1] = id; stack.Push(p + 1); }
                if (y > 0 && region[p - width] < 0 && labels[p - width] == label) { region[p - width] = id; stack.Push(p - width); }
                if (y < height - 1 && region[p + width] < 0 && labels[p + width] == label) { region[p + width] = id; stack.Push(p + width); }
            }
            regionArea.Add(area);
        }

        var count = regionLabel.Count;
        var parent = new int[count];
        for (var i = 0; i < count; i++)
            parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        var small = Enumerable.Range(0, count).Where(i => regionArea[i] < minRegion).OrderBy(i => regionArea[i]).ToList();
        var shared = new Dictionary<int, int>();
        var seen = new HashSet<int>();
        foreach (var id in small)
        {
            ct.ThrowIfCancellationRequested();
            var root = Find(id);
            if (regionArea[root] >= minRegion)
                continue;

            // The region's pixels (it may have taken in smaller ones) and how long a border it shares with each neighbour.
            shared.Clear();
            seen.Clear();
            stack.Push(regionSeed[root]);
            seen.Add(regionSeed[root]);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int x = p % width, y = p / width;
                Span<int> neighbours = [x > 0 ? p - 1 : -1, x < width - 1 ? p + 1 : -1, y > 0 ? p - width : -1, y < height - 1 ? p + width : -1];
                foreach (var q in neighbours)
                {
                    if (q < 0)
                        continue;
                    var other = Find(region[q]);
                    if (other == root)
                    {
                        if (seen.Add(q))
                            stack.Push(q);
                    }
                    else
                    {
                        shared[other] = shared.GetValueOrDefault(other) + 1;
                    }
                }
            }

            var target = -1;
            foreach (var (other, length) in shared)
            {
                if (target < 0 || length > shared[target]
                    || (length == shared[target] && LabelDistance(regionLabel[root], regionLabel[other], palette) < LabelDistance(regionLabel[root], regionLabel[target], palette)))
                    target = other;
            }
            if (target < 0)
                continue; // the whole picture is one region
            parent[root] = target;
            regionArea[target] += regionArea[root];
        }

        for (var p = 0; p < pixelCount; p++)
            labels[p] = regionLabel[Find(region[p])];
    }

    private static int LabelDistance(byte a, byte b, int[] palette) =>
        a == Transparent || b == Transparent ? int.MaxValue : ColorDistance(palette[a], palette[b]);
}
