// Shared ImageMagick logic: applies preset ImageOptions and writes frames in the requested format.
// Used by MagickImageConverter and by PdfiumConverter (PDF pages → images).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class ImageEncoder
{
    /// <summary>
    /// Image formats ImageMagick can read (see <see cref="ImageReader"/> for RAW, layered and metafile sources).
    /// Camera RAW is decoded by LibRaw; XCF, HEIC, DICOM, Photo CD, OpenEXR and Radiance HDR are read only. EMF/WMF
    /// are Windows metafiles, which ImageMagick renders through GDI+ on Windows only (<see cref="Metafiles"/>). APNG is
    /// read and written by Filee's own <see cref="Apng"/> (its frames decoded and encoded by ImageMagick).
    /// </summary>
    public static readonly string[] Readable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif", "heic",
        "jxl", "jp2", "psd", "psb", "tga", "ppm", "xcf", "raw",
        "apng", "mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff",
        "dcm", "pcd", "exr", "hdr",
    ];

    /// <summary>Vector metafiles ImageMagick rasterizes (Windows only: it uses GDI+).</summary>
    public static readonly string[] Metafiles = ["emf", "wmf"];

    /// <summary>
    /// Image formats ImageMagick can write. HEIC is missing: the bundled build has no HEVC encoder (patent
    /// licensing), so HEIC is written with the optional FFmpeg download instead (Heic/HeicWriter).
    /// </summary>
    public static readonly string[] Writable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif",
        "jxl", "jp2", "psd", "psb", "tga", "ppm",
        "apng", "mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff",
    ];

    /// <summary>Formats that store several frames/pages in one file.</summary>
    private static readonly HashSet<string> MultiFrame = ["tiff", "gif", "apng", "mng"];

    /// <summary>
    /// Formats whose frames are an animation. Between these, all frames are kept with their timing (an animated GIF
    /// becomes an animated WebP and the other way round); any other target gets the first frame.
    /// </summary>
    private static readonly HashSet<string> Animated = ["gif", "webp", "apng", "mng"];

    /// <summary>
    /// The edge an image engine offers from a picture in <paramref name="source"/> to <paramref name="target"/>. GIF
    /// holds 256 colours, so a still picture costs more there: it reaches video through a full-colour PNG instead
    /// (FFmpeg), while animations and multi-page files still go through GIF and keep their frames. Still picture → APNG
    /// costs as much, so photos never reach video as a one-frame animation.
    /// </summary>
    public static ConversionEdge Edge(string source, string target) =>
        new(source, target, target is "gif" or "apng" && !MultiFrame.Contains(source) && !Animated.Contains(source) ? 12 : 10);

    /// <summary>True when frames of <paramref name="sourceFormat"/> are written as one animation in <paramref name="targetFormat"/>.</summary>
    public static bool KeepsAnimation(string sourceFormat, string targetFormat) =>
        Animated.Contains(sourceFormat) && Animated.Contains(targetFormat);

    /// <summary>
    /// Formats written without an alpha channel: transparent pixels are flattened onto the background colour. HEIC
    /// could carry alpha as an auxiliary picture; Filee's writer (Heic/HeicWriter) does not. XBM and WBMP are 1-bit, PFM
    /// has no alpha, and the alpha planes of PCX and Sun Raster are an ImageMagick extension other programs ignore (its
    /// palette writers drop them altogether).
    /// </summary>
    private static readonly HashSet<string> NoAlpha = ["jpg", "ppm", "heic", "xbm", "wbmp", "pfm", "pcx", "ras"];

    /// <summary>
    /// Formats read with their ImageMagick coder named instead of guessed from the file: some extensions mean another
    /// format to ImageMagick (.rgb and .bw are headerless pixel data there, SGI here) and WBMP has no signature.
    /// </summary>
    public static readonly HashSet<string> ExplicitCoder =
        ["mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff", "dcm", "pcd", "exr", "hdr"];

    /// <summary>Formats holding one picture in several sizes (icons) or mipmaps: the largest is converted.</summary>
    private static readonly HashSet<string> SizeVariants = ["ico", "cur", "dds"];

    /// <summary>Every format ImageMagick reads as input: the raster formats plus, on Windows, the metafiles.</summary>
    public static IEnumerable<string> Sources =>
        OperatingSystem.IsWindows() ? [.. Readable, .. Metafiles] : Readable;

    public static MagickFormat ToMagickFormat(string formatId) => formatId switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "tiff" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Icon,
        "avif" => MagickFormat.Avif,
        "heic" => MagickFormat.Heic,
        "jxl" => MagickFormat.Jxl,
        "jp2" => MagickFormat.Jp2,
        "psd" => MagickFormat.Psd,
        "psb" => MagickFormat.Psb,
        "tga" => MagickFormat.Tga,
        "ppm" => MagickFormat.Ppm,
        "mng" => MagickFormat.Mng,
        "qoi" => MagickFormat.Qoi,
        "dds" => MagickFormat.Dds,
        "pcx" => MagickFormat.Pcx,
        "cur" => MagickFormat.Cur,
        "xpm" => MagickFormat.Xpm,
        "xbm" => MagickFormat.Xbm,
        "wbmp" => MagickFormat.Wbmp,
        "pam" => MagickFormat.Pam,
        "pfm" => MagickFormat.Pfm,
        "sgi" => MagickFormat.Sgi,
        "ras" => MagickFormat.Sun,
        "miff" => MagickFormat.Miff,
        "dcm" => MagickFormat.Dcm,
        "pcd" => MagickFormat.Pcd,
        "exr" => MagickFormat.Exr,
        "hdr" => MagickFormat.Hdr,
        // Every frame is a PNG; Apng assembles the animation (ImageMagick would need FFmpeg for it).
        "apng" => MagickFormat.Png,
        _ => throw new NotSupportedException($"ImageMagick cannot handle '{formatId}'."),
    };

    /// <summary>
    /// ImageMagick format and file extension for writing <paramref name="targetFormat"/>. The "ppm" format covers the
    /// Netpbm family: a grayscale result, or a .pgm/.pbm source kept as such, is written as PGM/PBM with its own extension.
    /// </summary>
    /// <param name="sourcePath">Input file of the step, used to keep the Netpbm sub-format of .pgm/.pbm sources.</param>
    public static (MagickFormat Format, string Extension) OutputFormat(string targetFormat, ImageOptions options, string? sourcePath = null)
    {
        if (targetFormat != "ppm")
            return (ToMagickFormat(targetFormat), targetFormat);
        var sourceExtension = sourcePath is null ? "" : Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        return sourceExtension switch
        {
            "pbm" => (MagickFormat.Pbm, "pbm"),
            "pgm" => (MagickFormat.Pgm, "pgm"),
            _ when options.Grayscale => (MagickFormat.Pgm, "pgm"),
            _ => (MagickFormat.Ppm, "ppm"),
        };
    }

    /// <summary>
    /// Chooses which frames of a (possibly multi-frame) source are converted.
    /// ICO, CUR, DDS: the largest image. Animations (GIF, WebP, APNG, MNG): every frame for an animated or TIFF
    /// target, else the first frame.
    /// PSD/PSB: the composite (frame 0; any further frames are layers). Otherwise: all frames/pages.
    /// </summary>
    public static List<IMagickImage<byte>> SelectFrames(MagickImageCollection images, string sourceFormat, string targetFormat)
    {
        if (Animated.Contains(sourceFormat) && images.Count > 1)
            images.Coalesce(); // animation frames can be partial; coalescing makes every frame a full image

        var frames = images.ToList();
        if (frames.Count <= 1)
            return frames;
        if (sourceFormat is "psd" or "psb")
            return [frames[0]];
        if (SizeVariants.Contains(sourceFormat))
            return [frames.OrderByDescending(f => (long)f.Width * f.Height).First()];
        if (Animated.Contains(sourceFormat) && !MultiFrame.Contains(targetFormat) && !KeepsAnimation(sourceFormat, targetFormat))
            return [frames[0]];
        return frames;
    }

    /// <summary>
    /// The longest side a format stores: JPEG 65,535 px (65,500 keeps clear of encoder rounding), WebP 16,383 px,
    /// AVIF 16,384 px. Other formats take more than any image Filee makes.
    /// </summary>
    public static double MaxSide(string targetFormat) => targetFormat switch
    {
        "jpg" or "jpeg" => 65_500,
        "webp" => 16_383,
        "avif" => 16_384,
        _ => double.PositiveInfinity,
    };

    /// <summary>
    /// Scales an image down until both sides fit <see cref="MaxSide"/>. A 1200 × 74,673 px long image used to fail as
    /// JPG with "could not write the file" (issue #50).
    /// </summary>
    private static void FitFormat(IMagickImage<byte> image, string targetFormat)
    {
        var scale = Math.Min(1, MaxSide(targetFormat) / Math.Max(image.Width, image.Height));
        if (scale < 1)
            image.Resize((uint)Math.Max(1, Math.Floor(image.Width * scale)), (uint)Math.Max(1, Math.Floor(image.Height * scale)));
    }

    /// <summary>
    /// How much <see cref="ApplyOptions"/> resizes a <paramref name="width"/> × <paramref name="height"/> image before
    /// <see cref="FitFormat"/> (1 when it does not).
    /// </summary>
    public static double ResizeScale(double width, double height, ImageOptions options) => options.Resize switch
    {
        ResizeMode.Percent when options.ResizePercent > 0 => options.ResizePercent / 100.0,
        ResizeMode.LongEdge when options.LongEdge > 0 => Math.Min(1, options.LongEdge / Math.Max(width, height)),
        ResizeMode.Width when options.Width > 0 => Math.Min(1, options.Width / width),
        _ => 1,
    };

    /// <summary>Turns and mirrors an image as <paramref name="options"/> say (part of <see cref="ApplyOptions"/>).</summary>
    public static void Turn(IMagickImage<byte> image, ImageOptions options)
    {
        switch (options.Rotation)
        {
            case ImageRotation.Right90:
                image.Rotate(90);
                break;
            case ImageRotation.Left90:
                image.Rotate(-90);
                break;
            case ImageRotation.Rotate180:
                image.Rotate(180);
                break;
        }
        // ImageMagick's Flop mirrors left ↔ right, Flip top ↔ bottom.
        if (options.Flip == ImageFlip.Horizontal)
            image.Flop();
        else if (options.Flip == ImageFlip.Vertical)
            image.Flip();
    }

    /// <summary>Applies resize / colour / metadata options to one frame.</summary>
    public static void ApplyOptions(IMagickImage<byte> image, ImageOptions options, string targetFormat)
    {
        var turned = options.TurnsOrMirrors || image.Orientation is not (OrientationType.TopLeft or OrientationType.Undefined);
        image.AutoOrient();

        // After AutoOrient, so a phone photo turns the way it looks, not the way its pixels are stored.
        Turn(image, options);
        // The small preview kept in the EXIF data still shows the picture as it was: Explorer and photo apps that
        // use it would show it unturned. As HeicWriter does, drop it once the pixels turned.
        if (turned && image.GetExifProfile() is { } exif && exif.ThumbnailLength > 0)
        {
            exif.RemoveThumbnail();
            image.SetProfile(exif);
        }

        switch (options.Resize)
        {
            case ResizeMode.Percent when options.ResizePercent is > 0 and not 100:
                image.Resize(new Percentage(options.ResizePercent));
                break;
            case ResizeMode.LongEdge when options.LongEdge > 0:
                // Greater = only shrink images that are larger than the box, never upscale.
                image.Resize(new MagickGeometry((uint)options.LongEdge, (uint)options.LongEdge) { Greater = true });
                break;
            case ResizeMode.Width when options.Width > 0 && image.Width > options.Width:
                image.Resize((uint)options.Width, (uint)Math.Max(1, Math.Round(image.Height * (double)options.Width / image.Width)));
                break;
        }
        FitFormat(image, targetFormat);

        // Before grayscale (a blue signature stays blue until then) and before alpha is flattened for JPG and co.
        if (options.RemoveBackground)
            BackgroundRemover.Apply(image, options.BackgroundTolerance, options.KeepEnclosedBackground);

        if (options.Grayscale)
            image.Grayscale();

        if (options.Dpi is > 0)
            image.Density = new Density(options.Dpi.Value, options.Dpi.Value, DensityUnit.PixelsPerInch);

        if (!options.KeepMetadata)
        {
            var colorProfile = options.KeepColorProfile ? image.GetColorProfile() : null;
            image.Strip();
            if (colorProfile is not null)
                image.SetProfile(colorProfile);
        }

        if (NoAlpha.Contains(targetFormat) && image.HasAlpha)
        {
            image.BackgroundColor = ParseColor(options.Background);
            image.Alpha(AlphaOption.Remove);
        }

        var quality = (uint)Math.Clamp(options.Quality, 1, 100);
        // Turning a JPEG has to re-encode it: at the photo's own quality (Magick's estimate when it read the file), not
        // above it, or a phone photo saved at 75 comes back at 95, bigger and no better.
        if (options.TurnsOrMirrors && targetFormat == "jpg" && image.Format == MagickFormat.Jpeg && image.Quality is > 0 and <= 100)
            quality = Math.Min(quality, image.Quality);
        switch (targetFormat)
        {
            case "jpg":
            case "avif":
                image.Quality = quality;
                break;
            case "jxl":
            case "jp2":
                // 100 means lossless for both encoders (JPEG XL distance 0, JPEG 2000 reversible 5/3 wavelet).
                image.Quality = quality;
                break;
            case "webp":
                image.Quality = quality;
                if (options.WebpLossless)
                    image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                break;
            case "tiff":
                image.Settings.Compression = options.TiffCompression switch
                {
                    TiffCompression.Lzw => CompressionMethod.LZW,
                    TiffCompression.Zip => CompressionMethod.Zip,
                    _ => CompressionMethod.NoCompression,
                };
                break;
            case "ico":
                PrepareIcon(image, options);
                break;
            case "cur":
                // A cursor stores at most 256 × 256 pixels.
                if (image.Width > 256 || image.Height > 256)
                    image.Resize(new MagickGeometry(256, 256));
                break;
            case "psd":
            case "psb":
                // ImageMagick writes a broken layer section for palette images with transparency (as read from
                // PNG or GIF): store them as direct colour.
                image.ClassType = ClassType.Direct;
                break;
        }
    }

    /// <summary>
    /// Writes frames using the allocator. Multi-frame targets (and animations, see <see cref="KeepsAnimation"/>) get
    /// one file; other targets get one file per frame with a <c>_p{n}</c> suffix when there is more than one frame.
    /// </summary>
    /// <param name="options">Options of the preset (the Netpbm sub-format depends on them).</param>
    /// <param name="sourcePath">Input file of the step, see <see cref="OutputFormat"/>.</param>
    /// <param name="animation">The frames are an animation: write them into one file even for a WebP target.</param>
    public static List<string> Write(IReadOnlyList<IMagickImage<byte>> frames, string targetFormat, IOutputAllocator output,
        ImageOptions? options = null, string? sourcePath = null, bool animation = false)
    {
        var written = new List<string>();
        var (format, extension) = OutputFormat(targetFormat, options ?? new ImageOptions(), sourcePath);

        if (targetFormat == "apng")
        {
            // One file, also for a single frame (a one-frame animation).
            var animationPath = output.Allocate(extension);
            if (animationPath is null)
                return written;
            Apng.Write(frames, animationPath);
            written.Add(animationPath);
            return written;
        }

        if (frames.Count > 1 && (MultiFrame.Contains(targetFormat) || animation))
        {
            var path = output.Allocate(extension);
            if (path is null)
                return written;
            using var collection = new MagickImageCollection();
            foreach (var frame in frames)
                collection.Add(frame.Clone());
            collection.Write(path, format);
            written.Add(path);
            return written;
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var path = output.Allocate(extension, frames.Count > 1 ? $"_p{i + 1}" : null);
            if (path is null)
                continue;
            frames[i].Write(path, format);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Writes one image as <paramref name="targetFormat"/> (APNG through <see cref="Apng"/>).</summary>
    public static void WriteImage(IMagickImage<byte> image, string path, string targetFormat)
    {
        if (targetFormat == "apng")
            Apng.Write([image], path);
        else
            image.Write(path, ToMagickFormat(targetFormat));
    }

    private static void PrepareIcon(IMagickImage<byte> image, ImageOptions options)
    {
        // Icons must be square: pad the shorter side with transparency, then let ImageMagick
        // generate every requested size inside one .ico file.
        var side = Math.Max(image.Width, image.Height);
        if (image.Width != image.Height)
        {
            image.BackgroundColor = MagickColors.Transparent;
            image.Extent(side, side, Gravity.Center);
        }
        if (side > 256)
            image.Resize(256, 256);

        var sizes = options.IcoSizes.Where(s => s is > 0 and <= 256).Distinct().OrderDescending().ToList();
        if (sizes.Count == 0)
            sizes = [256, 48, 32, 16];
        image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", string.Join(',', sizes));
    }

    private static MagickColor ParseColor(string hex)
    {
        try
        {
            return new MagickColor(string.IsNullOrWhiteSpace(hex) ? "#FFFFFF" : hex);
        }
        catch (ArgumentException)
        {
            return new MagickColor("#FFFFFF");
        }
    }
}
