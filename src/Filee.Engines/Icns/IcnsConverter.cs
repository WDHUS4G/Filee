// macOS icons (.icns) without macOS: writes them from any raster image and reads them back, built in.
// The container is handled by IcnsFile; ImageMagick resizes and encodes/decodes the PNG and JPEG 2000 entries.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Icns;

/// <summary>Raster image → ICNS and ICNS → raster image.</summary>
public sealed class IcnsConverter : IConverter, IImageTransformer
{
    public string Id => "icns";
    public string DisplayName => "ICNS (built-in)";
    public int MaxParallelism => 0;

    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. ImageEncoder.Sources.Select(source => new ConversionEdge(source, "icns")),
        .. ImageEncoder.Writable.Select(target => ImageEncoder.Edge("icns", target)),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => step.From == "icns"
            ? ReadIcon(step, progress, cancellationToken)
            : WriteIcon(step, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Makes an icon with every size iconutil produces (16 to 1024 px, PNG encoded). The picture is centred on a
    /// transparent square first, so nothing is stretched or cut off.
    /// </summary>
    private static IReadOnlyList<string> WriteIcon(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        using var images = ImageReader.Read(step.InputPath, step.From, step.Preset);
        // An icon holds one picture: the first page/frame, or the largest icon of an .ico.
        var master = ImageEncoder.SelectFrames(images, step.From, "icns")[0];
        ImageEncoder.ApplyOptions(master, step.Preset.Image, "icns");
        MakeSquare(master);
        progress?.Report(0.2);

        var entries = new List<IcnsEntry>();
        var encoded = new Dictionary<int, byte[]>();
        foreach (var (type, size) in IcnsFile.WrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (!encoded.TryGetValue(size, out var png))
            {
                using var icon = master.Clone();
                icon.FilterType = FilterType.Lanczos;
                icon.Resize(new MagickGeometry((uint)size, (uint)size) { IgnoreAspectRatio = true });
                icon.Strip(); // EXIF/XMP would be repeated in all ten entries
                png = icon.ToByteArray(MagickFormat.Png32);
                encoded[size] = png;
            }
            entries.Add(new IcnsEntry(type, png));
            progress?.Report(0.2 + 0.7 * entries.Count / IcnsFile.WrittenTypes.Length);
        }

        var path = step.Output.Allocate("icns");
        if (path is null)
            return [];
        File.WriteAllBytes(path, IcnsFile.Write(entries));
        progress?.Report(1);
        return [path];
    }

    /// <summary>Converts the largest image of an .icns file to <see cref="ConversionStep.To"/>.</summary>
    private static IReadOnlyList<string> ReadIcon(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var entries = IcnsFile.Parse(File.ReadAllBytes(step.InputPath));
        var candidates = IcnsFile.Candidates(entries);
        if (candidates.Count == 0)
            throw new InvalidDataException("The ICNS file contains no image Filee can read (only 1-bit or 8-bit palette icons).");
        progress?.Report(0.2);

        using var image = DecodeLargest(candidates, ct);
        ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
        progress?.Report(0.7);
        var written = ImageEncoder.Write([image], step.To, step.Output, step.Preset.Image);
        progress?.Report(1);
        return written;
    }

    /// <summary>Decodes the largest image; a broken entry falls back to the next smaller one.</summary>
    private static MagickImage DecodeLargest(List<IcnsFile.Candidate> candidates, CancellationToken ct)
    {
        Exception? first = null;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (candidate.Encoded is not null)
                    return new MagickImage(candidate.Encoded);
                var bitmap = candidate.Decode!();
                var image = new MagickImage();
                image.ReadPixels(bitmap.Rgba, new PixelReadSettings((uint)bitmap.Width, (uint)bitmap.Height, StorageType.Char, PixelMapping.RGBA));
                return image;
            }
            catch (Exception ex) when (ex is MagickException or InvalidDataException or ArgumentException)
            {
                first ??= ex;
            }
        }
        throw new InvalidDataException("No image in the ICNS file could be decoded.", first);
    }

    /// <summary>Pads the shorter side with transparency so the picture becomes a centred square.</summary>
    private static void MakeSquare(IMagickImage<byte> image)
    {
        image.Alpha(AlphaOption.Set);
        if (image.Width == image.Height)
            return;
        var side = Math.Max(image.Width, image.Height);
        image.BackgroundColor = MagickColors.Transparent;
        image.Extent(side, side, Gravity.Center);
    }
}
