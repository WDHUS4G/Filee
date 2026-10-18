// Raster image ↔ raster image conversion with ImageMagick (Magick.NET, Apache-2.0).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>
/// Converts between PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, APNG, MNG, QOI,
/// DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster and MIFF, and reads HEIC, XCF, camera RAW, DICOM,
/// Photo CD, OpenEXR, Radiance HDR and (on Windows) EMF/WMF.
/// </summary>
public sealed class MagickImageConverter : IConverter, ITiffMerger, IImageTransformer
{
    public string Id => "magick";
    public string DisplayName => "ImageMagick";
    public int MaxParallelism => 0;

    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in ImageEncoder.Sources
         from target in ImageEncoder.Writable
         select ImageEncoder.Edge(source, target)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(MagickNET.ImageMagickVersion,
            $"{EngineVersions.Library("Magick.NET", typeof(MagickImage))} · ImageMagick {ImageMagickNumber()}");

    /// <summary>"7.1.2-5" out of "ImageMagick 7.1.2-5 Q8 x64 …".</summary>
    private static string ImageMagickNumber() =>
        MagickNET.ImageMagickVersion.Split(' ').FirstOrDefault(part => part.Length > 0 && char.IsAsciiDigit(part[0])) ?? "?";

    /// <summary>
    /// Writes the pages into one multi-page TIFF. Each page is flattened onto the preset's background (no alpha) at 8 bits
    /// and marked as a page of a multi-page document (SubfileType 2), so older viewers (Microsoft Office Document
    /// Imaging, fax software) show it page by page; compression is the preset's.
    /// </summary>
    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, Filee.Core.Presets.ImageOptions options,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var compression = options.TiffCompression switch
            {
                Filee.Core.Presets.TiffCompression.Lzw => CompressionMethod.LZW,
                Filee.Core.Presets.TiffCompression.Zip => CompressionMethod.Zip,
                _ => CompressionMethod.NoCompression,
            };
            using var pages = new MagickImageCollection();
            foreach (var path in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new MagickImageCollection(path);
                foreach (var frame in source)
                {
                    var page = frame.Clone();
                    page.BackgroundColor = new MagickColor(string.IsNullOrWhiteSpace(options.Background) ? "#FFFFFF" : options.Background);
                    page.Alpha(AlphaOption.Remove);
                    page.Depth = 8;
                    page.Settings.Compression = compression;
                    page.SetAttribute("tiff:subfiletype", "2");
                    pages.Add(page);
                }
            }
            pages.Write(outputPath, MagickFormat.Tiff);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            // Animated PNGs are often saved as .png: read them as APNG, so they stay animated as GIF or WebP.
            var from = step.From == "png" && Apng.IsAnimated(step.InputPath) ? "apng" : step.From;
            using var images = ImageReader.Read(step.InputPath, from, step.Preset);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.3);

            var frames = ImageEncoder.SelectFrames(images, from, step.To);
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);
            }
            progress?.Report(0.7);

            var written = ImageEncoder.Write(frames, step.To, step.Output, step.Preset.Image, step.InputPath,
                ImageEncoder.KeepsAnimation(from, step.To));
            progress?.Report(1);
            return written;
        }, cancellationToken);
}
