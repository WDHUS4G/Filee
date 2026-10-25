// Image → HEIC. ImageMagick reads the picture and applies the preset (orientation, size, background), FFmpeg's x265
// encodes it as HEVC tiles and HeifFile puts them into the HEIC container with the colour profile and Exif.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Heic;

/// <summary>Writes HEIC files with Filee's copy of FFmpeg (it has the HEVC encoder ImageMagick lacks).</summary>
internal static class HeicWriter
{
    /// <summary>Tile size of larger pictures, as cameras write them: every decoder handles 512 × 512 HEVC pictures.</summary>
    public const int TileSize = 512;

    /// <summary>A prepared picture: an 8-bit RGB PNG in the work folder and the metadata that goes next to it.</summary>
    private sealed record Picture(string Png, int Width, int Height, byte[]? IccProfile, byte[]? Exif);

    public static async Task<IReadOnlyList<string>> ConvertAsync(string ffmpeg, ConversionStep step, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var pictures = await Task.Run(() => Prepare(step, cancellationToken), cancellationToken);
        progress?.Report(0.15);

        var written = new List<string>();
        for (var i = 0; i < pictures.Count; i++)
        {
            var picture = pictures[i];
            var share = 0.85 / pictures.Count;
            var start = 0.15 + i * share;
            byte[] heif;
            using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(5), start, start + share * 0.95))
                heif = await EncodeAsync(ffmpeg, picture.Png, picture.Width, picture.Height, picture.IccProfile, picture.Exif,
                    step.Preset.Image.Quality, step.WorkDirectory, cancellationToken);
            var output = step.Output.Allocate("heic", pictures.Count > 1 ? $"_p{i + 1}" : null);
            if (output is null)
                continue;
            await File.WriteAllBytesAsync(output, heif, cancellationToken);
            written.Add(output);
        }
        progress?.Report(1);
        return written;
    }

    /// <summary>
    /// A HEIC file of the 8-bit RGB PNG <paramref name="png"/> (<paramref name="width"/> × <paramref name="height"/>)
    /// with its colour profile and Exif (a TIFF header, or "Exif\0\0" and one): x265 encodes the tiles into the work
    /// directory, HeifFile puts them together.
    /// </summary>
    internal static async Task<byte[]> EncodeAsync(string ffmpeg, string png, int width, int height, byte[]? iccProfile, byte[]? exif,
        int quality, string workDirectory, CancellationToken cancellationToken)
    {
        var stream = Path.Combine(workDirectory, $"heic-{Guid.NewGuid():N}.hevc");
        var (columns, rows, tileWidth, tileHeight) = Layout(width, height);
        var result = await FfmpegRunner.RunAsync(ffmpeg, Arguments(png, stream, width, height, quality), null, cancellationToken);
        if (result.ExitCode != 0)
        {
            var reason = FfmpegRunner.Summarize(result.StandardError);
            throw new InvalidOperationException($"FFmpeg failed (exit code {result.ExitCode}){(reason.Length > 0 ? ": " + reason : ".")}");
        }
        return HeifFile.Write(new HeifImage(width, height, HevcStream.Parse(await File.ReadAllBytesAsync(stream, cancellationToken)),
            tileWidth, tileHeight, columns, rows, iccProfile, exif));
    }

    /// <summary>
    /// Reads the source with ImageMagick and writes every picture to convert (TIFF pages; the first frame of an
    /// animation) as a PNG, after the preset's orientation, resize and metadata options. HEIC is written without
    /// alpha, so transparency is flattened onto the preset's background colour.
    /// </summary>
    private static List<Picture> Prepare(ConversionStep step, CancellationToken cancellationToken)
    {
        using var images = ImageReader.Read(step.InputPath, step.From, step.Preset);
        var pictures = new List<Picture>();
        foreach (var frame in ImageEncoder.SelectFrames(images, step.From, "heic"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageEncoder.ApplyOptions(frame, step.Preset.Image, "heic");
            var icc = frame.GetColorProfile()?.ToByteArray();
            byte[]? exif = null;
            if (frame.GetExifProfile() is { } profile)
            {
                // The pixels are upright now (AutoOrient), and an old thumbnail would show them as they were.
                profile.SetValue(ExifTag.Orientation, (ushort)1);
                profile.RemoveThumbnail();
                exif = profile.ToByteArray();
            }

            var png = Path.Combine(step.WorkDirectory, $"heic-{Guid.NewGuid():N}.png");
            frame.Strip(); // FFmpeg only needs the pixels; the profiles are stored by HeifFile
            frame.Depth = 8;
            frame.Write(png, MagickFormat.Png24);
            pictures.Add(new Picture(png, (int)frame.Width, (int)frame.Height, icc, exif));
        }
        return pictures;
    }

    /// <summary>
    /// One coded picture for a picture up to 512 × 512, otherwise 512 × 512 tiles. Coded pictures have even sides
    /// (4:2:0); the grid crops the padding away again.
    /// </summary>
    internal static (int Columns, int Rows, int TileWidth, int TileHeight) Layout(int width, int height) =>
        width <= TileSize && height <= TileSize
            ? (1, 1, (width + 1) & ~1, (height + 1) & ~1)
            : ((width + TileSize - 1) / TileSize, (height + TileSize - 1) / TileSize, TileSize, TileSize);

    /// <summary>x265 constant quality for a preset quality of 1-100: 90 → 14, 80 → 18, 50 → 30.</summary>
    internal static int Crf(int quality) => Math.Clamp((int)Math.Round(50 - 0.4 * Math.Clamp(quality, 1, 100)), 0, 51);

    /// <summary>
    /// FFmpeg turns the PNG into raw HEVC: padded to whole tiles with its own edge pixels (black padding would bleed
    /// into the picture), BT.601 full-range 4:2:0 like JPEG and phone cameras, cut into tiles (row by row) and encoded
    /// by x265 as independent IDR pictures, 8-bit Main profile, which every HEIC decoder plays.
    /// </summary>
    internal static List<string> Arguments(string png, string output, int width, int height, int quality)
    {
        var (columns, rows, tileWidth, tileHeight) = Layout(width, height);
        int paddedWidth = columns * tileWidth, paddedHeight = rows * tileHeight;
        var filters = new List<string>();
        if (paddedWidth != width || paddedHeight != height)
        {
            filters.Add($"pad={paddedWidth}:{paddedHeight}:0:0");
            filters.Add($"fillborders=right={paddedWidth - width}:bottom={paddedHeight - height}:mode=smear");
        }
        filters.Add("scale=out_color_matrix=bt601:out_range=pc");
        filters.Add("format=yuv420p");
        if (columns * rows > 1)
            filters.Add($"untile={columns}x{rows}");

        return
        [
            .. MediaEncoding.Common, "-i", png, "-vf", string.Join(',', filters),
            // Every tile a forced IDR picture rather than keyint=1: with keyint=1 x265 signals "Main Intra", a range
            // extensions profile the "heic" brand does not allow and Windows' decoder gets wrong.
            "-c:v", "libx265", "-preset", "medium", "-crf", Crf(quality).ToString(CultureInfo.InvariantCulture),
            "-force_key_frames", "expr:1", "-forced-idr", "1",
            "-x265-params", "bframes=0:open-gop=0:info=0:range=full:colormatrix=smpte170m:log-level=error",
            "-f", "hevc", output,
        ];
    }
}
