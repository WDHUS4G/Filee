// What a FITS picture (Magick/Fits.cs) becomes: a picture for the eye or the data for further processing.
//
// - PNG, JPG and every other picture: stretched automatically, the way capture programs show a preview. The values are
//   scaled to 0–1 over their range; per channel, the shadows are clipped 2.8 normalized MADs (median absolute deviation
//   × 1.4826) below the median, and a midtones transfer function takes the median to 0.2 of the output. A raw frame of
//   a colour camera is debayered first (bilinear, from the BAYERPAT pattern and XBAYROFF / YBAYROFF), unless the preset
//   keeps it mono; its channels are stretched one by one, so the sky comes out neutral.
// - TIFF (Filee's own writer, as ImageMagick's Q8 build stores 8 bits): the data as it is, or stretched, at the file's
//   own depth. 8- and 16-bit data stay so (BZERO / BSCALE applied: unsigned 16-bit is written as unsigned 16-bit),
//   32-bit integer and floating-point data become 32-bit floating point. Never debayered: the mosaic stays for the
//   program that processes it.
// Files that say ROWORDER = 'BOTTOM-UP' are turned upright; others are taken as stored top-down, as capture programs
// write them.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Stretches, debayers and writes FITS pictures.</summary>
internal static class FitsRendering
{
    /// <summary>Where the median (the sky background) lands after stretching.</summary>
    public const double TargetBackground = 0.2;

    /// <summary>Shadows are clipped this many normalized MADs from the median.</summary>
    public const double ShadowClipping = -2.8;

    private const int HistogramBins = 65536;

    /// <summary>The picture for ImageMagick: stretched, debayered if asked, 8 bits per channel, upright.</summary>
    public static MagickImage ToMagick(FitsImage image, bool debayer, CancellationToken cancellationToken = default)
    {
        var planes = Stretch(image, debayer, cancellationToken);
        if (image.BottomUp)
            planes = [.. planes.Select(p => FlipVertically(p, image.Width, image.Height))];
        var pixels = image.Width * image.Height;
        var rgb = new byte[pixels * 3];
        ForChunks(pixels, (start, end) =>
        {
            for (var i = start; i < end; i++)
            {
                for (var c = 0; c < 3; c++)
                    rgb[i * 3 + c] = (byte)(planes[planes.Length == 3 ? c : 0][i] * 255 + 0.5f);
            }
        });
        var result = new MagickImage();
        result.ReadPixels(rgb, new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Char, PixelMapping.RGB));
        if (planes.Length == 1)
            result.ColorType = ColorType.Grayscale;
        return result;
    }

    /// <summary>
    /// FITS → TIFF: the data as it is or stretched (<see cref="ImageOptions.FitsTiff"/>) at the file's own depth, turned
    /// and mirrored as the preset asks; compressed as the preset asks.
    /// </summary>
    public static IReadOnlyList<string> WriteTiff(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var image = Fits.Read(step.InputPath, cancellationToken);
        progress?.Report(0.3);
        float[][] planes;
        TiffSampleKind kind;
        if (step.Preset.Image.FitsTiff == FitsTiffMode.Stretched)
        {
            kind = image.Bitpix switch { 8 => TiffSampleKind.UInt8, 16 => TiffSampleKind.UInt16, _ => TiffSampleKind.Float32 };
            planes = Stretch(image, debayer: false, cancellationToken);
            var full = kind switch { TiffSampleKind.UInt8 => 255f, TiffSampleKind.UInt16 => 65535f, _ => 1f };
            if (full != 1)
            {
                foreach (var plane in planes)
                {
                    for (var i = 0; i < plane.Length; i++)
                        plane[i] *= full;
                }
            }
        }
        else
        {
            kind = LinearKind(image);
            planes = image.Planes;
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        var (width, height) = (image.Width, image.Height);
        if (image.BottomUp)
            planes = [.. planes.Select(p => FlipVertically(p, width, height))];
        var turned = new float[planes.Length][];
        var (turnedWidth, turnedHeight) = (width, height);
        for (var p = 0; p < planes.Length; p++)
            (turned[p], turnedWidth, turnedHeight) = Turn(planes[p], width, height, step.Preset.Image.Rotation, step.Preset.Image.Flip);

        var output = step.Output.Allocate("tiff");
        if (output is null)
            return [];
        TiffWriter.Write(output, turned, turnedWidth, turnedHeight, kind, step.Preset.Image.TiffCompression,
            step.Preset.Image.Dpi ?? 72, cancellationToken);
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// The TIFF sample type that holds the data as it is: 8-bit data of whole numbers 0–255 stays 8-bit, 16-bit data
    /// of whole numbers 0–65535 (unsigned 16-bit through BZERO 32768) stays 16-bit, anything else is 32-bit floating
    /// point so nothing is cut (signed or scaled values, 32-bit integers, floating point).
    /// </summary>
    public static TiffSampleKind LinearKind(FitsImage image) => image.Bitpix switch
    {
        8 when image.Integral && image.Min >= 0 && image.Max <= 255 => TiffSampleKind.UInt8,
        16 when image.Integral && image.Min >= 0 && image.Max <= 65535 => TiffSampleKind.UInt16,
        _ => TiffSampleKind.Float32,
    };

    /// <summary>
    /// The planes scaled to 0–1 over the picture's range and stretched channel by channel; a colour camera's mosaic is
    /// debayered first when <paramref name="debayer"/> is set. Rows stay in the stored order.
    /// </summary>
    public static float[][] Stretch(FitsImage image, bool debayer, CancellationToken cancellationToken = default)
    {
        var (low, high) = (image.Min, image.Max);
        var scale = high > low ? 1.0 / ((double)high - low) : 0;
        var planes = new float[image.Planes.Length][];
        for (var p = 0; p < planes.Length; p++)
        {
            var source = image.Planes[p];
            var plane = planes[p] = new float[source.Length];
            ForChunks(plane.Length, (start, end) =>
            {
                for (var i = start; i < end; i++)
                    plane[i] = float.IsFinite(source[i]) ? (float)Math.Clamp((source[i] - low) * scale, 0, 1) : 0;
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (debayer && planes.Length == 1 && image.BayerPattern is { } pattern && image.Width >= 2 && image.Height >= 2)
            planes = Debayer(planes[0], image.Width, image.Height, pattern, image.BayerOffsetX, image.BayerOffsetY);
        cancellationToken.ThrowIfCancellationRequested();
        Parallel.ForEach(planes, plane =>
        {
            var (shadows, midtones) = StretchParameters(plane);
            var range = 1 - shadows;
            ForChunks(plane.Length, (start, end) =>
            {
                for (var i = start; i < end; i++)
                    plane[i] = (float)Midtones(midtones, Math.Clamp((plane[i] - shadows) / range, 0, 1));
            });
        });
        return planes;
    }

    /// <summary>Runs <paramref name="body"/> over 0 … <paramref name="length"/> in chunks, on several cores.</summary>
    private static void ForChunks(int length, Action<int, int> body)
    {
        const int Chunk = 1 << 16;
        Parallel.For(0, (length + Chunk - 1) / Chunk, c => body(c * Chunk, Math.Min(length, (c + 1) * Chunk)));
    }

    /// <summary>The shadows clipping point and the midtones balance of one channel's values (0–1).</summary>
    internal static (double Shadows, double Midtones) StretchParameters(float[] values)
    {
        var histogram = new int[HistogramBins];
        foreach (var value in values)
            histogram[Bin(value)]++;
        var median = Median(histogram, values.LongLength);
        Array.Clear(histogram);
        foreach (var value in values)
            histogram[Bin(Math.Abs(value - median))]++;
        var deviation = 1.4826 * Median(histogram, values.LongLength);
        var shadows = deviation > 0 ? Math.Clamp(median + ShadowClipping * deviation, 0, 0.99) : 0;
        var background = (median - shadows) / (1 - shadows);
        // The balance m with Midtones(m, background) = TargetBackground is Midtones(TargetBackground, background).
        var midtones = background > 0 ? Midtones(TargetBackground, background) : 0.5;
        return (shadows, midtones is > 0 and < 1 ? midtones : 0.5);
    }

    /// <summary>The midtones transfer function: 0 → 0, 1 → 1, <paramref name="balance"/> → 0.5.</summary>
    internal static double Midtones(double balance, double x) =>
        x <= 0 ? 0 : x >= 1 ? 1 : (balance - 1) * x / ((2 * balance - 1) * x - balance);

    private static int Bin(double value) => (int)(Math.Clamp(value, 0, 1) * (HistogramBins - 1) + 0.5);

    /// <summary>The median of <paramref name="count"/> values in 0–1 from their histogram (65 536 bins).</summary>
    private static double Median(int[] histogram, long count)
    {
        var half = (count + 1) / 2;
        long seen = 0;
        for (var bin = 0; bin < HistogramBins; bin++)
        {
            seen += histogram[bin];
            if (seen >= half)
                return (double)bin / (HistogramBins - 1);
        }
        return 0;
    }

    /// <summary>
    /// Bilinear debayering: each pixel keeps its own colour and takes the others from the average of its eight
    /// neighbours of that colour (two or four of them). <paramref name="pattern"/> is the colour of the stored pixels
    /// (0, 0), (1, 0), (0, 1) and (1, 1), shifted by the offsets.
    /// </summary>
    internal static float[][] Debayer(float[] mosaic, int width, int height, string pattern, int offsetX, int offsetY)
    {
        var colours = pattern.Select(c => c switch { 'R' => 0, 'G' => 1, _ => 2 }).ToArray();
        var planes = new[] { new float[mosaic.Length], new float[mosaic.Length], new float[mosaic.Length] };
        Parallel.For(0, height, y =>
        {
            Span<double> sum = stackalloc double[3];
            Span<int> count = stackalloc int[3];
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                count.Clear();
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                        continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if ((dx == 0 && dy == 0) || nx < 0 || nx >= width)
                            continue;
                        var colour = colours[((ny + offsetY) & 1) * 2 + ((nx + offsetX) & 1)];
                        sum[colour] += mosaic[ny * width + nx];
                        count[colour]++;
                    }
                }
                var index = y * width + x;
                var own = colours[((y + offsetY) & 1) * 2 + ((x + offsetX) & 1)];
                for (var c = 0; c < 3; c++)
                    planes[c][index] = c == own ? mosaic[index] : count[c] > 0 ? (float)(sum[c] / count[c]) : 0;
            }
        });
        return planes;
    }

    private static float[] FlipVertically(float[] plane, int width, int height)
    {
        var flipped = new float[plane.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(plane, y * width, flipped, (height - 1 - y) * width, width);
        return flipped;
    }

    /// <summary>Turns, then mirrors, one plane (what ImageMagick does for the other targets, see ImageEncoder).</summary>
    internal static (float[] Plane, int Width, int Height) Turn(float[] plane, int width, int height, ImageRotation rotation, ImageFlip flip)
    {
        if (rotation == ImageRotation.None && flip == ImageFlip.None)
            return (plane, width, height);
        var (w, h) = rotation is ImageRotation.Right90 or ImageRotation.Left90 ? (height, width) : (width, height);
        var result = new float[plane.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // The output pixel (x, y) after the flip is (fx, fy) after the rotation alone.
                var fx = flip == ImageFlip.Horizontal ? w - 1 - x : x;
                var fy = flip == ImageFlip.Vertical ? h - 1 - y : y;
                var (sx, sy) = rotation switch
                {
                    ImageRotation.Right90 => (fy, height - 1 - fx),
                    ImageRotation.Left90 => (width - 1 - fy, fx),
                    ImageRotation.Rotate180 => (width - 1 - fx, height - 1 - fy),
                    _ => (fx, fy),
                };
                result[y * w + x] = plane[sy * width + sx];
            }
        }
        return (result, w, h);
    }
}
