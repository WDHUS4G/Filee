// "Cut a clip" runs as an ordinary conversion once the clip window has its part of the video: the tool preset becomes
// a conversion of that part into the video's own format (or MP4), so the job queue, the toast, cancelling, the output
// rules and the history work as for any other file.

using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Turns the "Cut a clip" tool into the conversion that writes one clip.</summary>
public static class ClipTool
{
    /// <summary>Where a clip goes when the video's own format can't be written: plays everywhere.</summary>
    public const string FallbackTarget = "mp4";

    /// <summary>
    /// The format a clip of a <paramref name="sourceFormat"/> video is written in: its own when one engine writes it
    /// straight from the video (MP4 → MP4, MKV → MKV), else <see cref="FallbackTarget"/> (RealVideo, WTV, …).
    /// </summary>
    public static string TargetFor(string sourceFormat, RoutePlanner planner) =>
        planner.Plan(sourceFormat, sourceFormat) is { Steps.Count: 1 } ? sourceFormat : FallbackTarget;

    /// <summary>True when a clip can be cut of a <paramref name="sourceFormat"/> file: it is a video that can be written.</summary>
    public static bool CanCut(string sourceFormat, RoutePlanner planner) =>
        FormatRegistry.FindById(sourceFormat)?.Category == FormatCategory.Video
        && planner.Plan(sourceFormat, TargetFor(sourceFormat, planner)) is { Steps.Count: 1 };

    /// <summary>The conversion that writes one stretch of a video, as recorded (see the other overload).</summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, ClipRange clip) =>
        ConversionOf(tool, targetFormat, ClipEdit.Of(clip));

    /// <summary>
    /// The conversion that writes <paramref name="clip"/> of a video as <paramref name="targetFormat"/> with the
    /// tool's name, output rule and media options (which an encoded clip is written with). The tool itself is left as
    /// it is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, ClipEdit clip)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = targetFormat;
        // Only the clip: none of the other things a video preset can make of a video.
        preset.Media.SaveEveryFrame = false;
        preset.Media.ExtractMotionPhoto = false;
        preset.Media.LivePhoto = LivePhotoKind.None;
        preset.Media.Clip = clip.Clone();
        return preset;
    }
}
