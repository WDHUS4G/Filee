// JPEG XR → images with the Windows Imaging Component, part of Windows (nothing to install or ship). WIC decodes,
// ImageMagick does the rest as for any other picture (preset options, every image format it writes). PDF, ICNS,
// HEIC and traced SVG follow through PNG.

using Filee.Core.Conversion;
using Filee.Engines.Magick;

namespace Filee.Engines.Wic;

/// <summary>Reads JPEG XR (HDR screenshots of Windows included) on Windows.</summary>
public sealed class WicImageConverter : IConverter, IImageTransformer
{
    public string Id => "wic";
    public string DisplayName => "Windows Imaging Component";
    public int MaxParallelism => 0;

    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        ImageEncoder.Writable.Select(target => ImageEncoder.Edge("jxr", target)).ToList();

    public EngineStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return EngineStatus.Unavailable("engine.reason.unsupported_platform");
        try
        {
            // Every Windows 10 and 11 has the JPEG XR codec; a stripped-down installation might not.
            WicNative.Release(Activator.CreateInstance(Type.GetTypeFromCLSID(WicNative.JpegXrDecoderClsid, throwOnError: true)!));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            return EngineStatus.Unavailable("engine.reason.not_installed", "JPEG XR codec: " + ex.Message);
        }
        return EngineStatus.Available("JPEG XR (JXR, WDP, HDP) → images", $"Windows {Environment.OSVersion.Version}");
    }

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("JPEG XR is read with the Windows Imaging Component, on Windows only.");
            using var images = new ImageMagick.MagickImageCollection { WicImageReader.Read(step.InputPath) };
            return MagickImageConverter.Write(images, step.From, step, progress, cancellationToken);
        }, cancellationToken);
}
