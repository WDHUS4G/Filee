// Finds out which hardware video encoder works on this computer. FFmpeg lists every encoder it was built with, also
// NVENC without an NVIDIA card or Quick Sync without an Intel GPU, so each candidate gets a tiny test encode first. The
// result is kept per FFmpeg executable, encoder and bit depth for as long as Filee runs.

using System.Collections.Concurrent;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Media;

/// <summary>Picks the first hardware encoder whose test encode succeeds.</summary>
internal static class HardwareProbe
{
    /// <summary>
    /// A test encode takes 0.1–2 s (Quick Sync loads its runtime on the first one); a driver that hangs is given up on
    /// after this long and the software encoder is used.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Results = new();

    /// <summary>
    /// The hardware encoder to write <paramref name="codec"/> ("h264", "hevc" or "av1") with, or null when none works
    /// here (then x264 / x265 / SVT-AV1 encode as usual).
    /// </summary>
    /// <param name="tenBit">The video is encoded in 10 bits (HEVC Main 10, 10-bit AV1): an encoder must handle that too.</param>
    /// <param name="encoders">The encoders <paramref name="ffmpeg"/> lists; null when the list could not be read.</param>
    public static async Task<HardwareEncoder?> SelectAsync(string ffmpeg, string codec, bool tenBit, IReadOnlySet<string>? encoders,
        ILogger logger, CancellationToken cancellationToken)
    {
        var vaapi = HardwareVideo.Current == HostPlatform.Linux && File.Exists(HardwareVideo.VaapiDevice);
        foreach (var candidate in HardwareVideo.Candidates(codec, HardwareVideo.Current, encoders, vaapi))
        {
            // The probe itself is not cancelled with the conversion: other conversions may be waiting for its result.
            if (await WorksAsync(ffmpeg, candidate, tenBit, logger).WaitAsync(cancellationToken))
                return candidate;
        }
        return null;
    }

    /// <summary>The cached result of the test encode of <paramref name="encoder"/> (run on first use).</summary>
    internal static Task<bool> WorksAsync(string ffmpeg, HardwareEncoder encoder, bool tenBit, ILogger logger) =>
        Results.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}|{encoder.Name}|{encoder.RateControl}|{(tenBit ? 10 : 8)}",
            _ => new Lazy<Task<bool>>(() => ProbeAsync(ffmpeg, encoder, tenBit, logger))).Value;

    private static async Task<bool> ProbeAsync(string ffmpeg, HardwareEncoder encoder, bool tenBit, ILogger logger)
    {
        var bits = tenBit ? 10 : 8;
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, HardwareVideo.ProbeArguments(encoder, tenBit), Timeout, CancellationToken.None);
            if (result.ExitCode == 0)
            {
                logger.LogInformation("Hardware encoder {Encoder} ({RateControl}, {Bits}-bit) works", encoder.Name, encoder.RateControl, bits);
                return true;
            }
            logger.LogInformation("Hardware encoder {Encoder} ({RateControl}, {Bits}-bit) is not usable: {Reason}", encoder.Name,
                encoder.RateControl, bits, FfmpegRunner.Summarize(result.StandardError));
            return false;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogInformation("Hardware encoder {Encoder} ({RateControl}, {Bits}-bit) is not usable: {Reason}", encoder.Name,
                encoder.RateControl, bits, ex.Message);
            return false;
        }
    }
}
