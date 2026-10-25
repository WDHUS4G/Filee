// Windows Imaging Component (WIC) through COM, the part Filee needs to decode a picture: a decoder for a file, its
// first frame, the frame's pixel format and a format converter to read the pixels in a known layout.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Engines.Wic;

[SupportedOSPlatform("windows")]
internal static class WicNative
{
    /// <summary>CLSID_WICImagingFactory.</summary>
    private static readonly Guid ImagingFactoryClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");

    /// <summary>CLSID_WICWmpDecoder: the JPEG XR (Windows Media Photo) decoder every Windows 10 and 11 has.</summary>
    public static readonly Guid JpegXrDecoderClsid = new("a26cec36-234c-4950-ae16-e34aace71d0d");

    /// <summary>GUID_WICPixelFormat32bppBGRA: 8 bits per channel, sRGB, straight alpha.</summary>
    public static readonly Guid PixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    /// <summary>GUID_WICPixelFormat128bppRGBAFloat: linear scRGB floats (1.0 is SDR white), straight alpha.</summary>
    public static readonly Guid PixelFormat128bppRgbaFloat = new("6fddc324-4e03-4bfe-b185-3d77768dc919");

    public const uint GenericRead = 0x80000000;
    public const int DecodeMetadataCacheOnDemand = 0;
    public const int BitmapDitherTypeNone = 0;
    public const int BitmapPaletteTypeCustom = 0;

    /// <summary>WICPixelFormatNumericRepresentationFixed: fixed-point channels, linear like the float ones.</summary>
    public const int NumericRepresentationFixed = 4;

    /// <summary>WICPixelFormatNumericRepresentationFloat: half or full floats (scRGB), or RGBE.</summary>
    public const int NumericRepresentationFloat = 5;

    /// <summary>WINCODEC_ERR_COMPONENTNOTFOUND: no installed decoder takes the file.</summary>
    public const int ComponentNotFound = unchecked((int)0x88982F50);

    public static IWICImagingFactory CreateFactory() =>
        (IWICImagingFactory)(Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingFactoryClsid, throwOnError: true)!)
                             ?? throw new InvalidOperationException("The Windows Imaging Component is not available."));

    /// <summary>
    /// Releases this caller's reference to a COM object now instead of when the garbage collector gets to it (a decoder
    /// keeps its file open). Once, not FinalReleaseComObject: WIC hands every caller the same cached component info
    /// for a pixel format, and .NET one wrapper per COM object, so two conversions at once share it; a final release
    /// on one thread cut the other's wrapper off in the middle of its calls ("COM object that has been separated from
    /// its underlying RCW").
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WicRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    // Only the methods Filee calls are typed; the slots before them keep the vtable order and are never called.

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICImagingFactory
    {
        IWICBitmapDecoder CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName, IntPtr vendor,
            uint desiredAccess, int metadataOptions);

        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        IWICComponentInfo CreateComponentInfo(ref Guid component);
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        IWICFormatConverter CreateFormatConverter();
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        uint GetFrameCount();
        IWICBitmapFrameDecode GetFrame(uint index);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        Guid GetPixelFormat();
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette();
        void CopyPixels(ref WicRect rect, uint stride, uint bufferSize, IntPtr buffer);
    }

    /// <summary>A frame is a bitmap source: these are IWICBitmapSource's slots.</summary>
    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapFrameDecode
    {
        void GetSize(out uint width, out uint height);
        Guid GetPixelFormat();
        void GetResolution(out double dpiX, out double dpiY);
    }

    /// <summary>A converter is a bitmap source too: its own methods follow IWICBitmapSource's.</summary>
    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICFormatConverter
    {
        void GetSize(out uint width, out uint height);
        Guid GetPixelFormat();
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette();
        void CopyPixels(ref WicRect rect, uint stride, uint bufferSize, IntPtr buffer);

        void Initialize(IWICBitmapSource source, ref Guid destinationFormat, int dither, IntPtr palette,
            double alphaThresholdPercent, int paletteTranslate);
    }

    [ComImport, Guid("23bc3f0a-698b-4357-886b-f24d50671334"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICComponentInfo;

    /// <summary>What a pixel format holds; the first eight slots are IWICComponentInfo's, the next five IWICPixelFormatInfo's.</summary>
    [ComImport, Guid("a9db33a2-af5f-43c7-b679-74f5984b5aa4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICPixelFormatInfo2
    {
        void GetComponentType();
        void GetCLSID();
        void GetSigningStatus();
        void GetAuthor();
        void GetVendorGUID();
        void GetVersion();
        void GetSpecVersion();
        void GetFriendlyName();
        void GetFormatGUID();
        void GetColorContext();
        void GetBitsPerPixel();
        void GetChannelCount();
        void GetChannelMask();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool SupportsTransparency();

        int GetNumericRepresentation();
    }
}
