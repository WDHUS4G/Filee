// What the clip window ("Cut a clip") needs of a video before anything is cut: how long it is and pictures of single
// frames for its timeline and its large preview, taken with Filee's FFmpeg into the window's own work folder.

namespace Filee.Engines.Media;

/// <summary>A video as the clip window shows it.</summary>
/// <param name="Duration">Playing time in seconds, 0 when the file doesn't say.</param>
/// <param name="Width">Width of the picture as shown (pixel aspect and rotation applied).</param>
/// <param name="Height">Height of the picture as shown.</param>
/// <param name="FrameRate">Frames per second, 0 when unknown.</param>
public sealed record VideoSummary(double Duration, int Width, int Height, double FrameRate)
{
    /// <summary>One frame's length in seconds (a 30 fps one when the rate is unknown).</summary>
    public double FrameLength => FrameRate is > 1 and < 1000 ? 1 / FrameRate : 1 / 30.0;

    /// <summary>
    /// The last time a frame can be taken at: a frame before the end, as FFmpeg finds nothing to show at the end
    /// itself.
    /// </summary>
    public double LastFrameTime => Math.Max(0, Duration - FrameLength);
}

/// <summary>Reads a video's length and takes pictures of single frames with Filee's FFmpeg.</summary>
public static class VideoFrames
{
    /// <summary>How long one frame may take: FFmpeg seeks to the key frame before it and decodes from there.</summary>
    private static readonly TimeSpan FrameStall = TimeSpan.FromSeconds(30);

    /// <summary>True when FFmpeg is installed (Settings › Engines).</summary>
    public static bool IsAvailable => FfmpegConverter.Locate() is { } tools && File.Exists(tools.Ffmpeg) && File.Exists(tools.Ffprobe);

    /// <summary>The length and size of the video at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidOperationException">FFmpeg is missing, the file can't be read or has no video.</exception>
    public static async Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var (_, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var info = await MediaInfo.ProbeAsync(ffprobe, Path.GetFullPath(path), cancellationToken);
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var height = video.IsRotated ? video.Width : video.Height;
        return new VideoSummary(info.Duration?.TotalSeconds ?? 0, (int)Math.Round(height * video.DisplayAspect), height, video.FrameRate);
    }

    /// <summary>
    /// Writes the frame at <paramref name="seconds"/> as a JPEG at most <paramref name="maxHeight"/> pixels high to
    /// <paramref name="output"/>. False when FFmpeg found no frame there (past the end) or failed.
    /// </summary>
    public static async Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken)
    {
        var (ffmpeg, _) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var arguments = MediaEncoding.FrameAt(Path.GetFullPath(path), Math.Max(0, seconds), output, maxHeight, null);
        var result = await FfmpegRunner.RunAsync(ffmpeg, arguments, null, cancellationToken, FrameStall, background: true);
        return result.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
    }

    /// <summary>The time of the timeline's picture <paramref name="index"/> of <paramref name="count"/>: the middle of its part.</summary>
    public static double ThumbnailTime(int index, int count, double duration) =>
        count <= 0 || duration <= 0 ? 0 : duration * (index + 0.5) / count;
}
