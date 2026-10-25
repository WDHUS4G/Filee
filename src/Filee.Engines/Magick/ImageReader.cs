// Reads source images with ImageMagick, handling the formats that need more than "open the file": camera RAW
// (LibRaw settings), layered PSD/PSB/XCF (one flattened picture instead of one file per layer) and Windows
// metafiles (rendered at the preset's DPI), APNG (Filee's own reader composes the frames) and uncompressed DICOM
// (Filee's own reader, as ImageMagick's Q8 build reads only 8-bit DICOM), FITS (Filee's own reader, stretched and
// debayered as the preset says) and the photo of an iPhone LIVP. Used by every engine that takes a raster image as input.

using System.Buffers.Binary;
using Filee.Core.Presets;
using ImageMagick;
using ImageMagick.Formats;

namespace Filee.Engines.Magick;

/// <summary>Opens images for conversion.</summary>
internal static class ImageReader
{
    /// <summary>Smallest density metafiles are rendered at, so EMF/WMF output is never blurry.</summary>
    private const int MinimumMetafileDpi = 150;

    /// <summary>
    /// Reads <paramref name="path"/> as format <paramref name="formatId"/>. Layered formats come back as a single,
    /// flattened image; other multi-frame formats (TIFF pages, GIF frames, ICO sizes) keep all frames.
    /// </summary>
    public static MagickImageCollection Read(string path, string formatId, Preset preset)
    {
        if (formatId == "apng")
            return Apng.Read(path);
        if (formatId == "dcm" && Dicom.TryRead(path) is { } scan)
            return scan;
        if (formatId == "livp")
            return ReadLivePhoto(path, preset);
        if (formatId == "fits")
            return [FitsRendering.ToMagick(Fits.Read(path), preset.Image.FitsDebayer)];

        var images = new MagickImageCollection();
        var settings = Settings(formatId, preset);
        try
        {
            images.Read(path, settings);
        }
        catch (MagickException ex) when (formatId == "dcm")
        {
            images.Dispose();
            throw new InvalidOperationException(
                "This DICOM image is compressed in a way Filee can't read (more than 8 bits per sample, or an unusual " +
                "compression). Export it uncompressed from the viewing software and try again. (" + ex.Message + ")", ex);
        }
        catch (MagickException ex) when (formatId == "xcf")
        {
            images.Dispose();
            throw new InvalidOperationException(
                "This GIMP file can't be read. Filee reads 8-bit XCF files saved without \"better but slower compression\"; " +
                "in GIMP, export the picture as PNG or PSD instead. (" + ex.Message + ")", ex);
        }
        catch
        {
            images.Dispose();
            throw;
        }

        // The metafile was rendered at this density, but ImageMagick doesn't record it on the image: without it,
        // an EMF placed in a PDF would come out at 96 dpi instead of its real size.
        if (formatId is "emf" or "wmf" && settings.Density is { } density)
        {
            foreach (var image in images)
                image.Density = density;
        }

        // A MIFF palette image keeps its transparency per pixel while its palette says opaque, so writers that store a
        // palette (PNG) would lose it: make it direct colour with the pixels' own alpha.
        if (formatId == "miff")
        {
            foreach (var image in images.Where(i => i.ClassType == ClassType.Pseudo && i.HasAlpha))
            {
                var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
                image.ClassType = ClassType.Direct;
                image.ImportPixels(rgba, new PixelImportSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGBA));
            }
        }

        if (formatId == "xcf" && images.Count > 0)
        {
            var flat = FlattenXcf(images, XcfCanvasSize(path));
            images.Dispose();
            return [flat];
        }
        return images;
    }

    /// <summary>The photo (JPEG or HEIC) of a LIVP, read from the package without unpacking it.</summary>
    private static MagickImageCollection ReadLivePhoto(string path, Preset preset)
    {
        var (data, format) = Media.LivePhotoSource.ReadStill(path);
        var settings = Settings(format, preset);
        settings.Format = ImageEncoder.ToMagickFormat(format);
        var images = new MagickImageCollection();
        try
        {
            images.Read(data, settings);
            return images;
        }
        catch
        {
            images.Dispose();
            throw;
        }
    }

    /// <summary>Read settings per source format.</summary>
    internal static MagickReadSettings Settings(string formatId, Preset preset)
    {
        var settings = new MagickReadSettings();
        if (ImageEncoder.ExplicitCoder.Contains(formatId))
            settings.Format = ImageEncoder.ToMagickFormat(formatId);
        switch (formatId)
        {
            case "raw":
                // LibRaw decodes every camera format; forcing the DNG coder also covers extensions ImageMagick doesn't
                // register (.srw) and ".raw", which ImageMagick would otherwise read as headerless pixel data.
                // LibRaw doesn't rotate (ImageMagick records the orientation), so ApplyOptions' AutoOrient turns it.
                settings.Format = MagickFormat.Dng;
                settings.SetDefines(new DngReadDefines
                {
                    UseCameraWhiteBalance = true,
                    UseAutoWhiteBalance = false,
                    OutputColor = DngOutputColor.SRGB,
                    InterpolationQuality = DngInterpolation.Ahd,
                    DisableAutoBrightness = false,
                });
                break;
            case "psd":
            case "psb":
                // Frame 0 is Photoshop's composite of all visible layers (ImageMagick builds it from the layers when a
                // file was saved without "maximize compatibility"); asking for that frame only skips decoding layers.
                settings.FrameIndex = 0;
                settings.FrameCount = 1;
                break;
            case "emf":
            case "wmf":
                var dpi = Math.Max(MinimumMetafileDpi, Math.Clamp(preset.Pdf.RenderDpi, 36, 1200));
                settings.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
                settings.BackgroundColor = MagickColors.Transparent;
                break;
        }
        return settings;
    }

    /// <summary>
    /// Composites GIMP layers onto a canvas of the document's size. ImageMagick returns the layers bottom-up with
    /// their offsets, blend mode as compose operator and hidden layers marked <see cref="CompositeOperator.No"/>, but
    /// not the canvas size, which comes from the file header.
    /// </summary>
    private static MagickImage FlattenXcf(MagickImageCollection layers, (uint Width, uint Height)? canvasSize)
    {
        var (width, height) = canvasSize ?? (layers[0].Page.Width is > 0 ? layers[0].Page.Width : layers[0].Width,
                                             layers[0].Page.Height is > 0 ? layers[0].Page.Height : layers[0].Height);
        var canvas = new MagickImage(MagickColors.Transparent, width, height);
        foreach (var layer in layers)
        {
            if (layer.Compose == CompositeOperator.No)
                continue; // hidden layer
            var compose = layer.Compose == CompositeOperator.Undefined ? CompositeOperator.Over : layer.Compose;
            canvas.Composite(layer, layer.Page.X, layer.Page.Y, compose);
        }
        canvas.Page = new MagickGeometry(0, 0, width, height);
        if (layers[0].ColorType is ColorType.Grayscale or ColorType.GrayscaleAlpha)
            canvas.ColorType = ColorType.GrayscaleAlpha;
        return canvas;
    }

    /// <summary>Width and height of an XCF document ("gimp xcf " magic, version, NUL, then two big-endian uint32).</summary>
    internal static (uint Width, uint Height)? XcfCanvasSize(string path)
    {
        Span<byte> header = stackalloc byte[22];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || !header[..9].SequenceEqual("gimp xcf "u8))
            return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[14..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
        return width is > 0 and <= 524_288 && height is > 0 and <= 524_288 ? (width, height) : null;
    }
}
