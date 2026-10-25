// Video → every frame as a picture ("Save every frame", MediaOptions.SaveEveryFrame): numbered PNG or JPG files in
// one folder, for focus stacking or picking the sharpest shot. The pictures are written into a hidden folder next to
// the output and moved into place when FFmpeg is done, so a cancelled or failed run leaves nothing behind.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;

namespace Filee.Engines.Media;

/// <summary>Writes the frames of a video as numbered pictures into a folder.</summary>
internal static class FrameSequenceWriter
{
    /// <summary>Name suffix of the output folder: "clip.mp4" → "clip_frames".</summary>
    public const string FolderSuffix = "_frames";

    /// <summary>
    /// Saves the frames of <paramref name="input"/> as <see cref="ConversionStep.To"/> pictures (frame_000001.png, …)
    /// into the folder the output rule names: "clip_frames", "clip_frames (2)" when that exists, or skipped, like any
    /// other output.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ConvertAsync(string ffmpeg, ConversionStep step, string input, MediaInfo info,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var extension = FormatRegistry.Get(step.To).PrimaryExtension;
        var interval = Math.Max(1, step.Preset.Media.FrameInterval);
        var frameCount = EstimatedFrames(info, interval);
        if (frameCount > MediaOptions.MaxFrames)
        {
            throw new ConversionRefusedException("error.too_many_frames",
                string.Create(CultureInfo.InvariantCulture, $"{frameCount:N0} > {MediaOptions.MaxFrames:N0}"));
        }

        var output = step.Output.Allocate("", FolderSuffix);
        if (output is null)
            return [];

        if (frameCount is { } count && FreeSpace(output) is { } free && EstimatedBytes(video, extension, count) is var needed && needed > free)
            throw new ConversionRefusedException("error.not_enough_space", $"{Gigabytes(needed)} > {Gigabytes(free)}");

        // Same folder as the output (same drive, so the move is instant); the frames go into a visible folder inside
        // it, which becomes the output.
        var staging = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.filee-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging).Attributes |= FileAttributes.Hidden;
        try
        {
            var frames = Directory.CreateDirectory(Path.Combine(staging, "frames")).FullName;
            // FFmpeg reads % in the whole path as part of the number pattern: a literal one is written %%.
            var pattern = Path.Combine(frames.Replace("%", "%%", StringComparison.Ordinal), $"frame_%06d.{extension}");
            var arguments = MediaEncoding.FrameSequence(input, pattern, extension, video, step.Preset.Image.Quality, interval);

            await FfmpegConverter.RunPassAsync(ffmpeg, new FfmpegPass(arguments, 1), info.Duration, progress, 0, cancellationToken);
            if (!Directory.EnumerateFiles(frames).Any())
                throw new InvalidOperationException("FFmpeg did not write any frames.");

            ArchiveFiles.MoveFolder(frames, output);
            progress?.Report(1);
            return [output];
        }
        finally
        {
            // FFmpeg has exited by now (FfmpegRunner waits for it after killing it), so nothing holds these files.
            ArchiveFiles.TryDeleteFolder(staging);
        }
    }

    /// <summary>Pictures the video gives: duration × frame rate ÷ interval; null when the file doesn't say.</summary>
    internal static long? EstimatedFrames(MediaInfo info, int interval) =>
        info.Duration is { TotalSeconds: > 0 } duration && info.Video is { FrameRate: > 0 } video
            ? (long)Math.Ceiling(duration.TotalSeconds * video.FrameRate / Math.Max(1, interval))
            : null;

    /// <summary>
    /// A generous guess of the pictures' size on disk: PNG about half of raw RGB (twice that for 10-bit video, which
    /// becomes 16-bit PNG), JPG about a quarter byte per pixel at the usual qualities.
    /// </summary>
    internal static long EstimatedBytes(VideoStreamInfo video, string extension, long frames)
    {
        var pixels = (double)Math.Max(1, video.Width * video.SampleAspect) * Math.Max(1, video.Height);
        var perPicture = extension == "png" ? pixels * 1.5 * (video.IsHighBitDepth ? 2 : 1) : pixels * 0.25;
        return (long)Math.Min(perPicture * frames, long.MaxValue);
    }

    /// <summary>Free bytes on the drive of <paramref name="path"/>; null where that can't be read (some network shares).</summary>
    private static long? FreeSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Gigabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB");
}
