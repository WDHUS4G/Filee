// Writes JPEG XR test pictures with Windows' own encoder (WIC), the codec that saves HDR screenshots on Windows.

using System.Runtime.InteropServices;

namespace Filee.Engines.Tests;

internal static class JpegXrBuilder
{
    /// <summary>GUID_WICPixelFormat24bppBGR: 8-bit SDR pixels.</summary>
    public static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");

    /// <summary>GUID_WICPixelFormat64bppRGBAHalf: linear scRGB half floats, what Windows writes for HDR screenshots.</summary>
    public static readonly Guid RgbaHalf64 = new("6fddc324-4e03-4bfe-b185-3d77768dc93a");

    private static readonly Guid ImagingFactoryClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid ContainerFormatWmp = new("57a37caa-367a-4540-916b-f183c5093a4b");
    private const uint GenericWrite = 0x40000000;
    private const int EncoderNoCache = 2;

    /// <summary>Writes a <paramref name="width"/> × <paramref name="height"/> picture of 8-bit B, G, R pixels.</summary>
    public static void WriteBgr(string path, int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * width + x) * 3;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (b, g, r);
            }
        }
        Write(path, width, height, Bgr24, pixels, width * 3);
    }

    /// <summary>Writes an HDR picture of opaque linear scRGB values (1.0 = 80 nits) as half floats.</summary>
    public static void WriteHdr(string path, int width, int height, Func<int, int, (float R, float G, float B)> pixel)
    {
        var halves = new Half[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * width + x) * 4;
                (halves[i], halves[i + 1], halves[i + 2], halves[i + 3]) = ((Half)r, (Half)g, (Half)b, (Half)1f);
            }
        }
        Write(path, width, height, RgbaHalf64, MemoryMarshal.AsBytes(halves.AsSpan()).ToArray(), width * 8);
    }

    private static void Write(string path, int width, int height, Guid pixelFormat, byte[] pixels, int stride)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WIC is part of Windows.");
        var factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingFactoryClsid, throwOnError: true)!)!;
        IWICStream? stream = null;
        IWICBitmapEncoder? encoder = null;
        IWICBitmapFrameEncode? frame = null;
        try
        {
            stream = factory.CreateStream();
            stream.InitializeFromFilename(path, GenericWrite);
            var container = ContainerFormatWmp;
            encoder = factory.CreateEncoder(ref container, IntPtr.Zero);
            encoder.Initialize(stream, EncoderNoCache);
            encoder.CreateNewFrame(out frame, IntPtr.Zero);
            frame.Initialize(IntPtr.Zero);
            frame.SetSize((uint)width, (uint)height);
            var format = pixelFormat;
            frame.SetPixelFormat(ref format);
            Assert.Equal(pixelFormat, format); // the encoder takes these formats as they are
            frame.WritePixels((uint)height, (uint)stride, (uint)pixels.Length, pixels);
            frame.Commit();
            encoder.Commit();
        }
        finally
        {
            foreach (var comObject in new object?[] { frame, encoder, stream, factory })
            {
                if (comObject is not null)
                    Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    // Only the methods the tests call are typed; the slots before them keep the vtable order and are never called.

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        IWICBitmapEncoder CreateEncoder(ref Guid containerFormat, IntPtr vendor);
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        IWICStream CreateStream();
    }

    /// <summary>IStream's eleven slots, then IWICStream's own.</summary>
    [ComImport, Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICStream
    {
        void Read();
        void Write();
        void Seek();
        void SetSize();
        void CopyTo();
        void Commit();
        void Revert();
        void LockRegion();
        void UnlockRegion();
        void Stat();
        void Clone();
        void InitializeFromIStream();
        void InitializeFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint desiredAccess);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapEncoder
    {
        void Initialize(IWICStream stream, int cacheOption);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        void CreateNewFrame(out IWICBitmapFrameEncode frame, IntPtr encoderOptions);
        void Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameEncode
    {
        void Initialize(IntPtr encoderOptions);
        void SetSize(uint width, uint height);
        void SetResolution();
        void SetPixelFormat(ref Guid pixelFormat);
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void WritePixels(uint lineCount, uint stride, uint bufferSize, [MarshalAs(UnmanagedType.LPArray)] byte[] pixels);
        void WriteSource();
        void Commit();
    }
}
