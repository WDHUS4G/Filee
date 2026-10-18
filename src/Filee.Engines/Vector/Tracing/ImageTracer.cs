// Raster → vector tracing, written for Filee from the published descriptions of the steps (see PixelOutlines,
// CurveFitter and ColorRegions); no code of Potrace, AutoTrace or VTracer is used.
//
// Black and white: pixels darker than Otsu's threshold (or, on a transparent background, the opaque pixels) are
// traced as one black shape. Colour: the picture is reduced to N colours and each colour is traced as the union of
// itself and every colour stacked above it, bottom (largest) colour first. A lower shape therefore runs underneath
// the shapes above it instead of meeting them edge to edge, so no background shows through between them.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Filee.Core.Presets;

namespace Filee.Engines.Vector.Tracing;

/// <summary>One colour of a traced picture.</summary>
internal sealed record TracedLayer(int Color, List<TracedPath> Paths);

/// <summary>A traced picture: its size in pixels and its shapes, bottom first.</summary>
internal sealed record TraceResult(int Width, int Height, List<TracedLayer> Layers);

internal static class ImageTracer
{
    /// <summary>
    /// Traces RGBA pixels (straight alpha, 4 bytes per pixel, rows top to bottom).
    /// </summary>
    public static TraceResult Trace(byte[] rgba, int width, int height, TraceSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var layers = settings.Mode == TracingMode.BlackAndWhite
            ? TraceBlackAndWhite(rgba, width, height, settings, progress, ct)
            : TraceColors(rgba, width, height, settings, progress, ct);
        return new TraceResult(width, height, layers);
    }

    // ───────────────────────── Black and white ─────────────────────────

    private static List<TracedLayer> TraceBlackAndWhite(byte[] rgba, int width, int height, TraceSettings settings,
        IProgress<double>? progress, CancellationToken ct)
    {
        var mask = InkMask(rgba, width, height);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.2);
        var outlines = PixelOutlines.Trace(mask, width, height, settings.SpeckleArea, ct);
        progress?.Report(0.5);
        var paths = FitAll(outlines, settings, ct);
        return paths.Count == 0 ? [] : [new TracedLayer(0x000000, paths)];
    }

    /// <summary>
    /// 1 for ink. On an opaque picture: darker than Otsu's threshold (transparent pixels count as white paper). On a
    /// picture with a transparent background: the opaque pixels, or only their darker ones when the opaque part itself
    /// has light and dark areas (a black logo on a white plate).
    /// </summary>
    internal static byte[] InkMask(byte[] rgba, int width, int height)
    {
        var count = width * height;
        var luminance = new byte[count];
        var histogram = new long[256];
        var opaqueHistogram = new long[256];
        long transparent = 0;
        for (var p = 0; p < count; p++)
        {
            var o = p * 4;
            var alpha = rgba[o + 3];
            var y = (rgba[o] * 299 + rgba[o + 1] * 587 + rgba[o + 2] * 114) / 1000;
            // Over white paper.
            var onWhite = (byte)(255 - (255 - y) * alpha / 255);
            luminance[p] = onWhite;
            histogram[onWhite]++;
            if (alpha >= ColorRegions.OpaqueAlpha)
                opaqueHistogram[y]++;
            else
                transparent++;
        }

        var mask = new byte[count];
        if (transparent >= count / 100 + 1)
        {
            // Transparent background: decide by the opaque pixels alone.
            var spread = Percentile(opaqueHistogram, 0.95) - Percentile(opaqueHistogram, 0.05);
            var threshold = spread < 64 ? 256 : Otsu(opaqueHistogram);
            for (var p = 0; p < count; p++)
            {
                var o = p * 4;
                if (rgba[o + 3] < ColorRegions.OpaqueAlpha)
                    continue;
                var y = (rgba[o] * 299 + rgba[o + 1] * 587 + rgba[o + 2] * 114) / 1000;
                if (y < threshold)
                    mask[p] = 1;
            }
            return mask;
        }

        var cut = Percentile(histogram, 0.99) - Percentile(histogram, 0.01) < 32 ? 128 : Otsu(histogram);
        for (var p = 0; p < count; p++)
            if (luminance[p] < cut)
                mask[p] = 1;
        return mask;
    }

    /// <summary>Otsu's threshold: values below it are one class, the rest the other.</summary>
    internal static int Otsu(long[] histogram)
    {
        long total = 0;
        double sum = 0;
        for (var i = 0; i < 256; i++)
        {
            total += histogram[i];
            sum += i * (double)histogram[i];
        }
        if (total == 0)
            return 128;
        double backgroundSum = 0, best = -1;
        long background = 0;
        var threshold = 128;
        for (var t = 0; t < 255; t++)
        {
            background += histogram[t];
            if (background == 0)
                continue;
            var foreground = total - background;
            if (foreground == 0)
                break;
            backgroundSum += t * (double)histogram[t];
            var meanBackground = backgroundSum / background;
            var meanForeground = (sum - backgroundSum) / foreground;
            var between = background * (double)foreground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (between > best)
            {
                best = between;
                threshold = t + 1;
            }
        }
        return threshold;
    }

    private static int Percentile(long[] histogram, double fraction)
    {
        long total = 0;
        foreach (var h in histogram)
            total += h;
        if (total == 0)
            return 0;
        var target = (long)(total * fraction);
        long running = 0;
        for (var i = 0; i < 256; i++)
        {
            running += histogram[i];
            if (running > target)
                return i;
        }
        return 255;
    }

    // ───────────────────────── Colour ─────────────────────────

    private static List<TracedLayer> TraceColors(byte[] rgba, int width, int height, TraceSettings settings,
        IProgress<double>? progress, CancellationToken ct)
    {
        var (palette, labels) = ColorRegions.Quantize(rgba, width, height, settings.Colors, ct);
        if (palette.Length == 0)
            return [];
        progress?.Report(0.15);
        if (settings.MergeEdgeBands)
            ColorRegions.MergeEdgeBands(labels, rgba, palette, width, height);
        ColorRegions.MergeSmallRegions(labels, palette, width, height, settings.MinRegion, ct);
        progress?.Report(0.3);

        // Bottom to top by area: the background first, small details last.
        var areas = new long[palette.Length];
        foreach (var label in labels)
            if (label != ColorRegions.Transparent)
                areas[label]++;
        var order = Enumerable.Range(0, palette.Length).Where(c => areas[c] > 0).OrderByDescending(c => areas[c]).ThenBy(c => c).ToArray();
        var rank = new int[256];
        Array.Fill(rank, -1);
        for (var i = 0; i < order.Length; i++)
            rank[order[i]] = i;

        var results = new TracedLayer[order.Length];
        var done = 0;
        Parallel.For(0, order.Length, new ParallelOptions { CancellationToken = ct }, layer =>
        {
            // This colour and every colour above it.
            var mask = new byte[labels.Length];
            for (var p = 0; p < labels.Length; p++)
                mask[p] = rank[labels[p]] >= layer ? (byte)1 : (byte)0;
            var outlines = PixelOutlines.Trace(mask, width, height, settings.SpeckleArea, ct);
            var paths = new List<TracedPath>(outlines.Count);
            foreach (var outline in outlines)
                paths.Add(CurveFitter.Fit(outline, settings));
            results[layer] = new TracedLayer(palette[order[layer]], paths);
            progress?.Report(0.3 + 0.65 * Interlocked.Increment(ref done) / order.Length);
        });
        return results.Where(l => l.Paths.Count > 0).ToList();
    }

    private static List<TracedPath> FitAll(List<PixelOutline> outlines, TraceSettings settings, CancellationToken ct)
    {
        var paths = new TracedPath[outlines.Count];
        Parallel.ForEach(Partitioner.Create(0, outlines.Count), new ParallelOptions { CancellationToken = ct }, range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
                paths[i] = CurveFitter.Fit(outlines[i], settings);
        });
        return [.. paths];
    }

    // ───────────────────────── SVG ─────────────────────────

    /// <summary>
    /// Writes the traced picture as SVG, <paramref name="outputWidth"/> × <paramref name="outputHeight"/> pixels
    /// (the source image's size; the traced pixels are scaled up to it when the picture was reduced for tracing).
    /// </summary>
    public static void WriteSvg(TraceResult result, Stream stream, int outputWidth, int outputHeight, string? comment = null)
    {
        var scaleX = outputWidth / (double)result.Width;
        var scaleY = outputHeight / (double)result.Height;
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        writer.Write(string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"{outputWidth}\" height=\"{outputHeight}\" viewBox=\"0 0 {outputWidth} {outputHeight}\">\n"));
        if (!string.IsNullOrEmpty(comment))
            writer.Write($"<!-- {comment.Replace("--", "- -", StringComparison.Ordinal)} -->\n");
        var data = new StringBuilder();
        foreach (var layer in result.Layers)
        {
            data.Clear();
            foreach (var path in layer.Paths)
                AppendPath(data, path, scaleX, scaleY);
            writer.Write($"<path fill=\"#{layer.Color:x6}\" d=\"");
            writer.Write(data);
            writer.Write("\"/>\n");
        }
        writer.Write("</svg>\n");
    }

    internal static void AppendPath(StringBuilder data, TracedPath path, double scaleX, double scaleY)
    {
        data.Append('M');
        Point(data, path.Start, scaleX, scaleY);
        foreach (var segment in path.Segments)
        {
            if (segment.IsLine)
            {
                data.Append('L');
            }
            else
            {
                data.Append('C');
                Point(data, segment.Control1, scaleX, scaleY);
                data.Append(' ');
                Point(data, segment.Control2, scaleX, scaleY);
                data.Append(' ');
            }
            Point(data, segment.End, scaleX, scaleY);
        }
        data.Append('Z');
    }

    private static void Point(StringBuilder data, Vec point, double scaleX, double scaleY)
    {
        Number(data, point.X * scaleX);
        data.Append(' ');
        Number(data, point.Y * scaleY);
    }

    private static void Number(StringBuilder data, double value)
    {
        var rounded = Math.Round(value, 2);
        data.Append((rounded == 0 ? 0 : rounded).ToString("0.##", CultureInfo.InvariantCulture));
    }
}
