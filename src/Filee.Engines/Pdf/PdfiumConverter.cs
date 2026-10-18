// Renders PDF pages to raster images with PDFium (via PDFtoImage, MIT / PDFium BSD-3): one image per page, one
// multi-page TIFF or GIF, or (PdfOptions.CombinePages) the pages stacked into one tall image.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for (Windows, macOS, Linux, mobile).
// Filee only targets desktop platforms that are all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>PDF → every raster format ImageMagick writes (PNG, JPG, WEBP, TIFF, …, PSD, TGA, PPM).</summary>
public sealed class PdfiumConverter : IConverter, IImageTransformer
{
    private static readonly HashSet<string> MultiPageTargets = ["tiff", "gif", "apng", "mng"];

    /// <summary>A tall image beyond this many pixels is scaled down (a 300-page PDF at 300 dpi would need gigabytes).</summary>
    internal const long MaxStackedPixels = 200_000_000;

    public string Id => "pdfium";
    public string DisplayName => "PDFium";

    /// <summary>PDFium is not thread-safe.</summary>
    public int MaxParallelism => 1;

    /// <summary>Renders pages to images, then applies the image options to them.</summary>
    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        ImageEncoder.Writable.Select(target => new ConversionEdge("pdf", target)).ToList();

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.Library("PDFtoImage", typeof(Conversion)));

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var pdf = File.ReadAllBytes(step.InputPath);
            var pageCount = Conversion.GetPageCount(pdf, null);
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, pageCount);
            var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
            var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
            var written = new List<string>();
            var collected = new List<IMagickImage<byte>>();
            var (format, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
            var stack = step.Preset.Pdf.CombinePages && pages.Count > 1 && !MultiPageTargets.Contains(step.To);

            try
            {
                var index = 0;
                foreach (var bitmap in Conversion.ToImages(pdf, pages, null, options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageNumber = pages[index] + 1;
                    index++;

                    var frame = ToMagick(bitmap, dpi);
                    if (stack)
                    {
                        // Each page turns, then they stack: a turned long image would lie on its side instead.
                        ImageEncoder.Turn(frame, step.Preset.Image);
                        if (step.Preset.Pdf.TrimPageMargins)
                            TrimVertically(frame, dpi);
                        collected.Add(frame); // stacked after the loop; the image options apply to the tall image
                        progress?.Report((double)index / pages.Count * 0.8);
                        continue;
                    }
                    ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);

                    if (MultiPageTargets.Contains(step.To))
                    {
                        collected.Add(frame); // one multi-page file, written after the loop
                    }
                    else
                    {
                        using (frame)
                        {
                            var path = step.Output.Allocate(extension, pages.Count > 1 ? $"_p{pageNumber}" : null);
                            if (path is not null)
                            {
                                frame.Write(path, format);
                                written.Add(path);
                            }
                        }
                    }
                    progress?.Report((double)index / pages.Count);
                }

                if (stack)
                {
                    var unturned = step.Preset.Image.Clone(); // the pages are turned already
                    unturned.Rotation = ImageRotation.None;
                    unturned.Flip = ImageFlip.None;
                    var parts = step.Preset.Pdf.SplitTallImage ? Split(collected, step.To, unturned) : [collected];
                    for (var part = 0; part < parts.Count; part++)
                    {
                        using var tall = Stack(parts[part]);
                        ImageEncoder.ApplyOptions(tall, unturned, step.To); // also scales it to fit the format
                        if (step.Output.Allocate(extension, parts.Count > 1 ? $"_{part + 1}" : null) is { } path)
                        {
                            tall.Write(path, format);
                            written.Add(path);
                        }
                    }
                }
                else if (collected.Count > 0)
                {
                    written.AddRange(ImageEncoder.Write(collected, step.To, step.Output, step.Preset.Image));
                }
            }
            finally
            {
                foreach (var frame in collected)
                    frame.Dispose();
            }
            return written;
        }, cancellationToken);

    /// <summary>
    /// The pages one under another, centred on a white strip as wide as the widest page. Scaled down when larger than
    /// <see cref="MaxStackedPixels"/>; <see cref="ImageEncoder.ApplyOptions"/> then fits it to what the format stores
    /// (JPEG 65,500 px, WebP 16,383 px).
    /// </summary>
    internal static IMagickImage<byte> Stack(IReadOnlyList<IMagickImage<byte>> pages)
    {
        double width = pages.Max(p => p.Width);
        double height = pages.Sum(p => (double)p.Height);
        var scale = Math.Min(1, Math.Sqrt(MaxStackedPixels / (width * height)));
        var stripWidth = (uint)Math.Max(1, Math.Floor(width * scale));
        using var strip = new MagickImageCollection();
        foreach (var page in pages)
        {
            var copy = page.Clone();
            if (scale < 1)
                copy.Resize((uint)Math.Max(1, Math.Floor(copy.Width * scale)), (uint)Math.Max(1, Math.Floor(copy.Height * scale)));
            copy.BackgroundColor = MagickColors.White;
            copy.Extent(stripWidth, copy.Height, Gravity.North);
            strip.Add(copy);
        }
        var tall = strip.AppendVertically();
        tall.Density = pages[0].Density;
        return tall;
    }

    /// <summary>
    /// Pages in runs short enough for one image of the format after the preset's resize (issue #50: a 44-page
    /// certificate at 1200 px wide is 74,673 px tall, more than a JPG holds). Runs break between pages; a single page
    /// taller than the format is a run of its own and is scaled down.
    /// </summary>
    internal static List<IReadOnlyList<IMagickImage<byte>>> Split(IReadOnlyList<IMagickImage<byte>> pages, string target, ImageOptions options)
    {
        double width = pages.Max(p => p.Width);
        double height = pages.Sum(p => (double)p.Height);
        var scale = ImageEncoder.ResizeScale(width, height, options) * Math.Min(1, Math.Sqrt(MaxStackedPixels / (width * height)));
        var limit = ImageEncoder.MaxSide(target) / scale;
        var parts = new List<IReadOnlyList<IMagickImage<byte>>>();
        var run = new List<IMagickImage<byte>>();
        double runHeight = 0;
        foreach (var page in pages)
        {
            if (run.Count > 0 && runHeight + page.Height > limit)
            {
                parts.Add(run);
                run = [];
                runHeight = 0;
            }
            run.Add(page);
            runHeight += page.Height;
        }
        parts.Add(run);
        return parts;
    }

    /// <summary>
    /// Cuts the blank rows above and below a page's content, keeping about 4 mm so text does not touch the next page.
    /// A blank page shrinks to that margin.
    /// </summary>
    internal static void TrimVertically(IMagickImage<byte> page, int dpi)
    {
        var margin = Math.Max(4, dpi / 6);
        uint top, bottom;
        using (var probe = page.Clone())
        {
            probe.ColorFuzz = new Percentage(8); // scanned pages are not pure white
            var box = probe.BoundingBox;
            if (box is null || box.Width == 0 || box.Height == 0 || box.Height >= page.Height)
            {
                if (box is not null && box.Height >= page.Height)
                    return;
                top = 0;
                bottom = (uint)Math.Min(page.Height, margin * 2);
            }
            else
            {
                top = (uint)Math.Max(0, box.Y - margin);
                bottom = (uint)Math.Min(page.Height, box.Y + box.Height + margin);
            }
        }
        page.Crop(new MagickGeometry(0, (int)top, page.Width, bottom - top));
        page.ResetPage();
    }

    private static MagickImage ToMagick(SKBitmap bitmap, int dpi)
    {
        using (bitmap)
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        {
            var image = new MagickImage(data.ToArray());
            image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            return image;
        }
    }
}
