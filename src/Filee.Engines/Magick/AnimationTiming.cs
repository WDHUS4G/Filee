// Playback speed of animations (preset option ImageOptions.AnimationSpeed): rewrites the frame delays of an animation
// written as GIF, WebP, APNG or MNG, and drops frames where a delay would get shorter than players show it.

using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Plays animation frames faster or slower by changing their delays.</summary>
internal static class AnimationTiming
{
    /// <summary>
    /// Shortest delay written, in milliseconds (2 hundredths of a second, 50 frames a second). Browsers show a frame
    /// that asks for 10 ms or less for 100 ms (GIF, APNG and WebP alike), so a shorter delay would play slower, not faster.
    /// </summary>
    public const int MinDelayMs = 20;

    /// <summary>How long players show a frame whose delay is 10 ms or less (<see cref="MinDelayMs"/>).</summary>
    private const int ShortDelayShownMs = 100;

    /// <summary>The largest delay GIF and MNG store (16 bits of hundredths of a second, about 11 minutes).</summary>
    private const int MaxHundredths = ushort.MaxValue;

    /// <summary>The ticks per second delays are written in: hundredths for GIF and MNG, milliseconds for WebP and APNG.</summary>
    public static int TicksPerSecond(string targetFormat) => targetFormat is "webp" or "apng" ? 1000 : 100;

    /// <summary>
    /// The frames to write, playing <paramref name="speed"/> times as fast: every delay divided by
    /// <paramref name="speed"/> in the units of <paramref name="targetFormat"/>, rounded on the running total so the
    /// whole animation keeps its new length. A frame that would start less than <see cref="MinDelayMs"/> after the one
    /// before is dropped (its time goes to the frame before it): fast animations sped up lose frames evenly instead of
    /// getting delays that players would stretch to 100 ms. Delays of 10 ms or less count as the 100 ms players show
    /// them for. Loop count and disposal of the frames stay as they were.
    /// </summary>
    /// <param name="frames">Coalesced frames (every frame a whole picture), so any of them can be dropped.</param>
    public static List<IMagickImage<byte>> ChangeSpeed(IReadOnlyList<IMagickImage<byte>> frames, double speed, string targetFormat)
    {
        if (frames.Count < 2 || !double.IsFinite(speed) || speed <= 0 || speed == 1)
            return [.. frames];

        var ticks = TicksPerSecond(targetFormat);
        var minTicks = MinDelayMs * ticks / 1000;
        var starts = new double[frames.Count + 1];
        for (var i = 0; i < frames.Count; i++)
            starts[i + 1] = starts[i] + DelayMs(frames[i]) * ticks / 1000.0 / speed;

        var kept = new List<(int Index, long Start)>();
        for (var i = 0; i < frames.Count; i++)
        {
            var start = (long)Math.Round(starts[i], MidpointRounding.AwayFromZero);
            if (kept.Count == 0 || start - kept[^1].Start >= minTicks)
                kept.Add((i, start));
        }
        var end = (long)Math.Round(starts[^1], MidpointRounding.AwayFromZero);
        // The last frame needs its time too: when it is too short, the frame before it stays up until the end instead.
        if (kept.Count > 1 && end - kept[^1].Start < minTicks)
            kept.RemoveAt(kept.Count - 1);

        var result = new List<IMagickImage<byte>>(kept.Count);
        for (var k = 0; k < kept.Count; k++)
        {
            var frame = frames[kept[k].Index];
            var next = k + 1 < kept.Count ? kept[k + 1].Start : end;
            var delay = Math.Max(minTicks, next - kept[k].Start);
            frame.AnimationTicksPerSecond = ticks;
            frame.AnimationDelay = (uint)(ticks == 100 ? Math.Min(delay, MaxHundredths) : Math.Min(delay, int.MaxValue));
            result.Add(frame);
        }
        return result;
    }

    /// <summary>A frame's delay in milliseconds as players show it.</summary>
    private static double DelayMs(IMagickImage<byte> frame)
    {
        var ticks = frame.AnimationTicksPerSecond > 0 ? frame.AnimationTicksPerSecond : 100;
        var ms = frame.AnimationDelay * 1000.0 / ticks;
        return ms <= 10 ? ShortDelayShownMs : ms;
    }
}
