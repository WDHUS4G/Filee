// A part of a video for "Cut a clip": where it starts and ends, kept inside the video, and the times as the clip
// window shows them (1:05.3) and as FFmpeg reads them (65.300).

using System.Globalization;

namespace Filee.Core.Presets;

/// <summary>A clip of a video, in seconds from its start: <see cref="Start"/> before <see cref="End"/>.</summary>
public readonly record struct ClipRange(double Start, double End)
{
    /// <summary>The shortest clip, in seconds: about three frames, so a clip is never empty.</summary>
    public const double MinLength = 0.1;

    /// <summary>How long the clip is, in seconds.</summary>
    public double Length => End - Start;

    /// <summary>
    /// The clip from <paramref name="start"/> to <paramref name="end"/> inside a video of <paramref name="duration"/>
    /// seconds (null or 0 when unknown: only the start is kept at 0 or later). The ends are swapped when given the wrong
    /// way round, and a clip shorter than <see cref="MinLength"/> grows to it, towards the end while there is room.
    /// Values that are no numbers count as 0 (start) and the whole video (end).
    /// </summary>
    public static ClipRange Clamp(double start, double end, double? duration)
    {
        var total = duration is > 0 and var d && double.IsFinite(d) ? d : (double?)null;
        start = double.IsFinite(start) ? start : 0;
        end = double.IsFinite(end) ? end : total ?? start + MinLength;
        if (end < start)
            (start, end) = (end, start);
        start = Math.Max(0, start);
        end = Math.Max(0, end);
        if (total is { } length)
        {
            start = Math.Min(start, length);
            end = Math.Min(end, length);
        }
        if (end - start < MinLength)
        {
            end = start + MinLength;
            if (total is { } length2 && end > length2)
            {
                end = length2;
                start = Math.Max(0, end - MinLength);
            }
        }
        return new ClipRange(start, end);
    }

    /// <summary>
    /// A time as the clip window shows it, to a tenth of a second: <c>0:07.5</c>, <c>4:05.0</c>, <c>1:02:05.3</c>
    /// (hours only when there are any). Tenths are rounded, so 59.96 s is <c>1:00.0</c>; a negative or unknown time is 0.
    /// </summary>
    public static string Display(double seconds)
    {
        var tenths = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds * 10, MidpointRounding.AwayFromZero) : 0;
        var hours = tenths / 36_000;
        var minutes = tenths / 600 % 60;
        var secs = tenths / 10 % 60;
        var tenth = tenths % 10;
        var invariant = CultureInfo.InvariantCulture;
        return hours > 0
            ? string.Create(invariant, $"{hours}:{minutes:00}:{secs:00}.{tenth}")
            : string.Create(invariant, $"{minutes}:{secs:00}.{tenth}");
    }

    /// <summary>A time as FFmpeg's <c>-ss</c> and <c>-to</c> read it: seconds with three decimals, <c>65.300</c>.</summary>
    public static string Ffmpeg(double seconds) =>
        Math.Max(0, double.IsFinite(seconds) ? seconds : 0).ToString("0.000", CultureInfo.InvariantCulture);
}
