// What "Cut a clip" writes: parts of one video one after the other, each with its own volume and speed (its sound
// muted or not), pauses (black and silence) where the user removed a part without closing the gap, music from another
// file under it, and a fade in and out. The clip window
// edits it with ClipCutter; the conversion writes it with FFmpeg (MediaEncoding.ClipGraph).

using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>One part of a clip: a stretch of the video, or a pause of the same length.</summary>
public sealed class ClipPart
{
    /// <summary>Slowest and fastest speed of a part.</summary>
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    /// <summary>Loudest volume of a part: twice as loud (+6 dB), as far as the volume slider and line go. 0 is silent.</summary>
    public const double MaxVolume = 2;

    /// <summary>The speeds the clip window offers.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>Where the part starts in the video, in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Where the part ends in the video, in seconds.</summary>
    public double End { get; set; }

    /// <summary>
    /// A part removed with the magnet off: black and silence for as long as it played, so what follows stays where
    /// it was.
    /// </summary>
    public bool Blank { get; set; }

    /// <summary>How loud, 1 as recorded (0 silent, <see cref="MaxVolume"/> loudest).</summary>
    public double Volume { get; set; } = 1;

    /// <summary>The part's own sound is cut out (its picture plays): silence, or only the music.</summary>
    public bool Muted { get; set; }

    /// <summary>How fast, 1 as recorded (<see cref="MinSpeed"/> to <see cref="MaxSpeed"/>); the sound keeps its pitch.</summary>
    public double Speed { get; set; } = 1;

    /// <summary><see cref="Speed"/> as written: within its limits, 1 for a value that makes no sense.</summary>
    [JsonIgnore]
    public double PlaySpeed => double.IsFinite(Speed) && Speed > 0 ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1;

    /// <summary><see cref="Volume"/> as written: within its limits, 1 for a value that makes no sense, 0 when muted.</summary>
    [JsonIgnore]
    public double PlayVolume => Muted ? 0 : double.IsFinite(Volume) && Volume >= 0 ? Math.Min(Volume, MaxVolume) : 1;

    /// <summary>How long the part plays in the clip, in seconds (its stretch of the video at its speed).</summary>
    [JsonIgnore]
    public double Length => Math.Max(0, End - Start) / PlaySpeed;

    /// <summary>True when the part plays as recorded: full volume, normal speed, not a pause.</summary>
    [JsonIgnore]
    public bool IsPlain => !Blank && !Muted && PlayVolume == 1 && PlaySpeed == 1;

    public ClipPart Clone() => (ClipPart)MemberwiseClone();
}

/// <summary>Music (or any sound) from another file played under a clip, its own track.</summary>
public sealed class ClipMusic
{
    /// <summary>How loud the music is to start with: under the clip's own sound, not over it.</summary>
    public const double DefaultVolume = 0.6;

    /// <summary>Seconds the music fades in and out at its ends, so it doesn't start or stop with a jump.</summary>
    public const double FadeSeconds = 1;

    /// <summary>The file the music comes from (any audio, or the sound of a video).</summary>
    public string Path { get; set; } = "";

    /// <summary>How far into the clip the music starts, in seconds.</summary>
    public double Offset { get; set; }

    /// <summary>Where in the file it starts playing, in seconds.</summary>
    public double From { get; set; }

    /// <summary>How long the file is, in seconds.</summary>
    public double Length { get; set; }

    /// <summary>How loud, 1 as recorded (0 to 2).</summary>
    public double Volume { get; set; } = DefaultVolume;

    /// <summary>Fade in and out at its ends (<see cref="FadeSeconds"/>).</summary>
    public bool Fade { get; set; } = true;

    /// <summary><see cref="Volume"/> as written: 0 to 2, the default for a value that makes no sense.</summary>
    [JsonIgnore]
    public double PlayVolume => double.IsFinite(Volume) && Volume >= 0 ? Math.Min(Volume, 2) : DefaultVolume;

    /// <summary>How long the music plays in a clip of <paramref name="clipLength"/> seconds: until it or the clip ends.</summary>
    public double PlayLength(double clipLength) => Math.Max(0, Math.Min(Length - From, clipLength - Offset));

    public ClipMusic Clone() => (ClipMusic)MemberwiseClone();
}

/// <summary>A clip of a video: its parts in the order they play, music under them, and how it fades in and out.</summary>
public sealed class ClipEdit
{
    /// <summary>The longest fade, in seconds.</summary>
    public const double MaxFade = 10;

    /// <summary>The parts, in the order they play (also their order in the video).</summary>
    public List<ClipPart> Parts { get; set; } = [];

    /// <summary>Seconds the clip fades in from black and silence; 0 for none.</summary>
    public double FadeIn { get; set; }

    /// <summary>Seconds the clip fades out to black and silence; 0 for none.</summary>
    public double FadeOut { get; set; }

    /// <summary>Music under the clip, or null.</summary>
    public ClipMusic? Music { get; set; }

    /// <summary>How long the clip plays, in seconds.</summary>
    [JsonIgnore]
    public double Length => Parts.Sum(p => p.Length);

    /// <summary>Where the clip starts in the video (its first part that isn't a pause).</summary>
    [JsonIgnore]
    public double Start => Parts.FirstOrDefault(p => !p.Blank)?.Start ?? 0;

    /// <summary>Where the clip ends in the video (its last part that isn't a pause).</summary>
    [JsonIgnore]
    public double End => Parts.LastOrDefault(p => !p.Blank)?.End ?? 0;

    /// <summary>
    /// True for one stretch of the video as recorded, without fades: it can be copied as it is (cut at key frames).
    /// Anything else is encoded.
    /// </summary>
    [JsonIgnore]
    public bool IsPlainCut => Parts.Count == 1 && Parts[0].IsPlain && Fade(FadeIn) == 0 && Fade(FadeOut) == 0 && Music is null;

    /// <summary>A fade as written: 0 to <see cref="MaxFade"/> seconds.</summary>
    public static double Fade(double seconds) => double.IsFinite(seconds) ? Math.Clamp(seconds, 0, MaxFade) : 0;

    /// <summary>One stretch of the video, as recorded.</summary>
    public static ClipEdit Of(ClipRange range) => new() { Parts = [new ClipPart { Start = range.Start, End = range.End }] };

    public ClipEdit Clone() =>
        new() { Parts = Parts.Select(p => p.Clone()).ToList(), FadeIn = FadeIn, FadeOut = FadeOut, Music = Music?.Clone() };

    /// <summary>
    /// The clip as it can be written from a video of <paramref name="duration"/> seconds (null when unknown): every part
    /// inside the video and at least <see cref="ClipRange.MinLength"/> long, in order and not overlapping (a part that
    /// would be left empty is dropped), speeds and volumes within their limits, and fades no longer than half the clip.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing of the video is left to play.</exception>
    public ClipEdit Clamped(double? duration)
    {
        var total = duration is > 0 and var d && double.IsFinite(d) ? d : (double?)null;
        var parts = new List<ClipPart>();
        var last = 0.0;
        foreach (var part in Parts)
        {
            if (!double.IsFinite(part.Start) || !double.IsFinite(part.End))
                continue;
            var start = Math.Max(part.Start, part.Blank ? part.Start : last);
            var end = part.End;
            if (total is { } length)
            {
                start = Math.Min(start, length);
                end = Math.Min(end, length);
            }
            start = Math.Max(0, start);
            if (end - start < ClipRange.MinLength)
                continue;
            parts.Add(new ClipPart
            {
                Start = start,
                End = end,
                Blank = part.Blank,
                Volume = part.Muted ? part.Volume : part.PlayVolume,
                Muted = part.Muted,
                Speed = part.PlaySpeed,
            });
            if (!part.Blank)
                last = end;
        }
        if (!parts.Any(p => !p.Blank))
            throw new InvalidOperationException("The clip has no part of the video left.");
        // Pauses only between parts that play: at either end they would only be black.
        while (parts[0].Blank)
            parts.RemoveAt(0);
        while (parts[^1].Blank)
            parts.RemoveAt(parts.Count - 1);
        var clip = new ClipEdit { Parts = parts };
        var half = clip.Length / 2;
        clip.FadeIn = Math.Min(Fade(FadeIn), half);
        clip.FadeOut = Math.Min(Fade(FadeOut), half);
        // Music only where it plays: from 0 into the clip, from a place in its file, for some time.
        if (Music is { Path.Length: > 0 } music && double.IsFinite(music.Offset) && double.IsFinite(music.From) && double.IsFinite(music.Length))
        {
            var kept = music.Clone();
            kept.Offset = Math.Max(0, kept.Offset);
            kept.From = Math.Clamp(kept.From, 0, Math.Max(0, kept.Length));
            kept.Volume = kept.PlayVolume;
            if (kept.PlayLength(clip.Length) >= ClipRange.MinLength)
                clip.Music = kept;
        }
        return clip;
    }

    /// <summary>
    /// The rest of the clip from <paramref name="seconds"/> into it on, for playing it from there: the part playing
    /// then starts where it is, the parts before it are left out, the fade in only where it is still running.
    /// </summary>
    public ClipEdit From(double seconds)
    {
        var rest = new ClipEdit { FadeOut = FadeOut };
        if (Music?.Clone() is { } music)
        {
            // The music keeps its place against the pictures: what has played of it is left out.
            var played = Math.Max(0, seconds - music.Offset);
            music.From += played;
            music.Offset = Math.Max(0, music.Offset - Math.Max(0, seconds));
            rest.Music = music;
        }
        var at = 0.0;
        var skip = Math.Max(0, seconds);
        foreach (var part in Parts)
        {
            var length = part.Length;
            if (at + length <= skip)
            {
                at += length;
                continue;
            }
            var copy = part.Clone();
            if (at < skip)
                copy.Start += (skip - at) * part.PlaySpeed;
            rest.Parts.Add(copy);
            at += length;
        }
        rest.FadeIn = Math.Max(0, Fade(FadeIn) - skip);
        return rest;
    }

    /// <summary>
    /// Where in the video the clip is at <paramref name="seconds"/> into it, for the playhead on the timeline; in a
    /// pause, where the pause stands.
    /// </summary>
    public double SourceTimeAt(double seconds)
    {
        var at = 0.0;
        foreach (var part in Parts)
        {
            var length = part.Length;
            if (seconds <= at + length)
                return part.Start + Math.Max(0, seconds - at) * part.PlaySpeed;
            at += length;
        }
        return Parts.Count > 0 ? Parts[^1].End : 0;
    }

    /// <summary>How far into the clip the video at <paramref name="sourceTime"/> plays (the next part's start when it is cut out).</summary>
    public double ClipTimeAt(double sourceTime)
    {
        var at = 0.0;
        foreach (var part in Parts)
        {
            if (sourceTime < part.Start)
                return at;
            if (sourceTime <= part.End)
                return at + (sourceTime - part.Start) / part.PlaySpeed;
            at += part.Length;
        }
        return at;
    }
}
