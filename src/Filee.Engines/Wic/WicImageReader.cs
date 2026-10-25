// Reads JPEG XR (.jxr, .wdp, .hdp) with Windows' own codec through WIC. ImageMagick has no JPEG XR coder on any
// platform, so this is the only reader. The picture becomes an ordinary 8-bit sRGB MagickImage; resizing, rotating
// and encoding are then ImageMagick's, as for every other image.
//
// Pixel formats with integer channels (8 or 16 bits, grey, RGB, CMYK) are converted by WIC to 8-bit BGRA. Fixed-point
// and float ones are linear scRGB with values above 1.0 (HDR screenshots are 64-bit half floats): WIC's converter would
// clip them, so they are read as 128-bit floats and tone-mapped by HdrToneMap.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ImageMagick;
using static Filee.Engines.Wic.WicNative;

namespace Filee.Engines.Wic;

[SupportedOSPlatform("windows")]
internal static class WicImageReader
{
    /// <summary>Rows read at once from an HDR picture: about 4 MB of floats.</summary>
    private const int BandBytes = 4 << 20;

    /// <summary>Decodes the first frame of <paramref name="path"/> (a JPEG XR file has one picture).</summary>
    public static MagickImage Read(string path)
    {
        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICComponentInfo? info = null;
        IWICFormatConverter? converter = null;
        try
        {
            factory = CreateFactory();
            decoder = factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, DecodeMetadataCacheOnDemand);
            if (decoder.GetFrameCount() == 0)
                throw new InvalidDataException("The file holds no picture.");
            frame = decoder.GetFrame(0);
            frame.GetSize(out var width, out var height);
            if (width == 0 || height == 0)
                throw new InvalidDataException("The picture is empty.");
            if ((long)width * height * 4 > Array.MaxLength)
                throw new InvalidDataException($"A {width} × {height} picture is too large to convert.");

            var format = frame.GetPixelFormat();
            info = factory.CreateComponentInfo(ref format);
            var pixelFormat = (IWICPixelFormatInfo2)info;
            var hdr = pixelFormat.GetNumericRepresentation() is NumericRepresentationFloat or NumericRepresentationFixed;

            converter = factory.CreateFormatConverter();
            var target = hdr ? PixelFormat128bppRgbaFloat : PixelFormat32bppBgra;
            converter.Initialize((IWICBitmapSource)frame, ref target, BitmapDitherTypeNone, IntPtr.Zero, 0, BitmapPaletteTypeCustom);

            var pixels = hdr ? ToneMapped(converter, (int)width, (int)height) : Bgra(converter, (int)width, (int)height);
            var image = new MagickImage();
            image.ReadPixels(pixels, new PixelReadSettings(width, height, StorageType.Char, hdr ? PixelMapping.RGBA : PixelMapping.BGRA));
            // HDR screenshots carry an alpha channel that is opaque everywhere: a PNG of them needs none.
            if (!pixelFormat.SupportsTransparency() || IsOpaque(pixels))
                image.Alpha(AlphaOption.Off);

            frame.GetResolution(out var dpiX, out var dpiY);
            if (dpiX is > 0 and < 100_000 && dpiY is > 0 and < 100_000)
                image.Density = new Density(dpiX, dpiY, DensityUnit.PixelsPerInch);
            return image;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(ex.HResult == ComponentNotFound
                ? "Windows can't read this file: it is not a JPEG XR picture, or Windows' JPEG XR codec is missing."
                : $"Windows couldn't decode this JPEG XR picture (0x{ex.HResult:X8}): {ex.Message}", ex);
        }
        finally
        {
            // The decoder keeps the file open until it is released.
            Release(converter);
            Release(info);
            Release(frame);
            Release(decoder);
            Release(factory);
        }
    }

    /// <summary>True when every pixel of 8-bit RGBA or BGRA <paramref name="pixels"/> is opaque.</summary>
    private static bool IsOpaque(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
                return false;
        }
        return true;
    }

    /// <summary>The whole picture as 8-bit BGRA.</summary>
    private static byte[] Bgra(IWICFormatConverter converter, int width, int height)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
        var rect = new WicRect { Width = width, Height = height };
        converter.CopyPixels(ref rect, (uint)width * 4, (uint)pixels.Length, Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0));
        return pixels;
    }

    /// <summary>
    /// The picture as tone-mapped 8-bit RGBA. Read top to bottom in bands of floats, kept as half floats (the precision
    /// HDR screenshots have) while the reference white and the peak are found, then mapped.
    /// </summary>
    private static byte[] ToneMapped(IWICFormatConverter converter, int width, int height)
    {
        var stride = width * 4;
        var bandRows = Math.Clamp(BandBytes / (stride * sizeof(float)), 1, height);
        var band = GC.AllocateUninitializedArray<float>(bandRows * stride, pinned: true);
        var linear = GC.AllocateUninitializedArray<Half>(stride * height);
        var sampleStep = HdrToneMap.SampleStep((long)width * height);
        var luminances = new List<float>();
        var peak = 0f;
        long pixel = 0;

        for (var y = 0; y < height; y += bandRows)
        {
            var rows = Math.Min(bandRows, height - y);
            var rect = new WicRect { Y = y, Width = width, Height = rows };
            converter.CopyPixels(ref rect, (uint)(stride * sizeof(float)), (uint)(rows * stride * sizeof(float)),
                Marshal.UnsafeAddrOfPinnedArrayElement(band, 0));
            var offset = y * stride;
            for (var i = 0; i < rows * stride; i += 4, pixel++)
            {
                float r = band[i], g = band[i + 1], b = band[i + 2];
                peak = Math.Max(peak, HdrToneMap.MaxChannel(r, g, b));
                if (pixel % sampleStep == 0)
                    luminances.Add(HdrToneMap.Luminance(r, g, b));
                linear[offset + i] = (Half)r;
                linear[offset + i + 1] = (Half)g;
                linear[offset + i + 2] = (Half)b;
                linear[offset + i + 3] = (Half)band[i + 3];
            }
        }

        var toneMap = new HdrToneMap(HdrToneMap.ReferenceWhite(luminances), peak);
        var rgba = GC.AllocateUninitializedArray<byte>(linear.Length);
        for (var i = 0; i < linear.Length; i += 4)
            toneMap.Map((float)linear[i], (float)linear[i + 1], (float)linear[i + 2], (float)linear[i + 3], rgba.AsSpan(i, 4));
        return rgba;
    }
}
