// Raster image → SVG step of the vector engine: reads the picture with ImageMagick (first frame or page, turned
// upright, in sRGB), reduces very large pictures, traces them and writes the SVG at the picture's own pixel size.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Vector.Tracing;

internal static class RasterTracing
{
    /// <summary>
    /// Pictures larger than this are reduced before tracing (time and memory grow with the pixel count, and outlines
    /// of a 24-megapixel photo would not get better). The SVG keeps the original size.
    /// </summary>
    public const int MaxPixels = 6_000_000;

    /// <summary>Longest side traced; longer pictures are reduced like <see cref="MaxPixels"/>.</summary>
    public const int MaxSide = 4096;

    public static IReadOnlyList<string> Run(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        int width, height, tracedWidth, tracedHeight;
        byte[] pixels;
        using (var images = ImageReader.Read(step.InputPath, step.From, step.Preset))
        {
            if (images.Count == 0)
                throw new InvalidDataException("The image has no pictures to trace.");
            var frame = ImageEncoder.SelectFrames(images, step.From, "png")[0];
            frame.AutoOrient();
            width = (int)frame.Width;
            height = (int)frame.Height;
            (tracedWidth, tracedHeight) = TracedSize(width, height);
            if (tracedWidth != width || tracedHeight != height)
            {
                frame.Resize(new MagickGeometry((uint)tracedWidth, (uint)tracedHeight) { IgnoreAspectRatio = true });
                (tracedWidth, tracedHeight) = ((int)frame.Width, (int)frame.Height);
            }
            if (frame.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray or ColorSpace.LinearGray))
                frame.ColorSpace = ColorSpace.sRGB;
            using var collection = frame.GetPixels();
            pixels = collection.ToByteArray(PixelMapping.RGBA)
                     ?? throw new InvalidDataException("The image's pixels could not be read.");
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.1);

        var settings = TraceSettings.From(step.Preset.Trace, Math.Max(tracedWidth, tracedHeight));
        var result = ImageTracer.Trace(pixels, tracedWidth, tracedHeight, settings,
            progress is null ? null : new Progress<double>(p => progress.Report(0.1 + 0.85 * p)), ct);

        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        var comment = tracedWidth == width && tracedHeight == height
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"Traced by Filee from a {width} x {height} px image reduced to {tracedWidth} x {tracedHeight} px.");
        using (var stream = File.Create(output))
            ImageTracer.WriteSvg(result, stream, width, height, comment);
        return [output];
    }

    /// <summary>The size a <paramref name="width"/> × <paramref name="height"/> picture is traced at.</summary>
    internal static (int Width, int Height) TracedSize(int width, int height)
    {
        var scale = Math.Min(1, Math.Min(MaxSide / (double)Math.Max(width, height), Math.Sqrt(MaxPixels / ((double)width * height))));
        if (scale >= 1)
            return (width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
