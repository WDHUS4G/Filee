// "Cut a clip" with parts: one FFmpeg filter graph plays the parts of the video one after the other, each at its
// speed and volume, with black and silence for the pauses and the fades at the ends. The same graph writes the clip
// (encoded with the preset's options) and plays it in the clip window (small pictures and the sound).

using System.Globalization;
using System.Text;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>The inputs and the filter graph of a clip: its picture comes out of <c>[v]</c>, its sound of <c>[a]</c>.</summary>
/// <param name="Inputs">
/// The input options: the video from the first part's start to the last one's end, and the music's file when there is one.
/// </param>
/// <param name="Filter">The <c>-filter_complex</c> graph.</param>
/// <param name="HasAudio">True when the graph has <c>[a]</c> (the video's sound, the music, or both).</param>
/// <param name="HasVideo">True when the graph has <c>[v]</c> (false for the preview's sound alone).</param>
/// <param name="Length">How long the clip plays, in seconds.</param>
internal sealed record ClipGraph(IReadOnlyList<string> Inputs, string Filter, bool HasAudio, double Length, bool HasVideo = true);

internal static partial class MediaEncoding
{
    /// <summary>Sample rate and layout every part's sound is brought to, so the parts join.</summary>
    private const string ClipAudioFormat = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";

    /// <summary>
    /// The graph that plays <paramref name="edit"/> of <paramref name="input"/>: its picture <paramref name="width"/> ×
    /// <paramref name="height"/> at <paramref name="fps"/> frames per second, its sound 48 kHz stereo: the video's own
    /// (when <paramref name="audio"/>; muted parts silent) with the clip's music mixed under it. The video is read once,
    /// from the first part to the last: <c>-ss</c> / <c>-to</c> before <c>-i</c>, the parts cut out of it with
    /// <c>trim</c>, so a clip late in a long video starts at once. The music is the second input, read from where it
    /// starts playing.
    /// </summary>
    /// <param name="edit">The clip, already <see cref="ClipEdit.Clamped"/> to the video.</param>
    /// <param name="pixelFormat">The pictures' format, the same for every part (pauses included) so they join.</param>
    /// <param name="video">
    /// False for the sound alone (the preview's): FFmpeg refuses a graph with a picture output nothing takes.
    /// </param>
    /// <param name="audio">The video's own sound is used (it has sound, and the preset keeps it).</param>
    /// <param name="music">The clip's music is mixed in (the preset keeps sound).</param>
    internal static ClipGraph ClipGraphOf(string input, ClipEdit edit, bool audio, int width, int height, string fps, string pixelFormat = "yuv420p",
        bool video = true, bool music = true)
    {
        var song = music ? edit.Music : null;
        var sound = audio || song is not null;
        if (!video && !sound)
            throw new ArgumentException("A clip's graph needs pictures or sound.", nameof(audio));
        var sourceParts = edit.Parts.Where(p => !p.Blank).ToList();
        if (sourceParts.Count == 0)
            throw new InvalidOperationException("The clip has no part of the video.");
        var from = sourceParts[0].Start;
        var to = sourceParts[^1].End;
        List<string> inputs = ["-ss", ClipRange.Ffmpeg(from), "-to", ClipRange.Ffmpeg(to), "-i", input];
        if (song is not null)
            inputs.AddRange(["-ss", ClipRange.Ffmpeg(song.From), "-i", song.Path]);

        var graph = new StringBuilder();
        var n = sourceParts.Count;
        if (video)
        {
            graph.Append("[0:V:0]").Append(n > 1 ? $"split={n}" : "null");
            for (var k = 0; k < n; k++)
                graph.Append($"[vs{k}]");
            graph.Append(';');
        }
        if (audio)
        {
            graph.Append("[0:a:0]").Append(n > 1 ? $"asplit={n}" : "anull");
            for (var k = 0; k < n; k++)
                graph.Append($"[as{k}]");
            graph.Append(';');
        }

        var picture = $"fps={fps},scale={width}:{height},setsar=1,format={pixelFormat}";
        var source = 0;
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            var part = edit.Parts[i];
            var length = Seconds(part.Length);
            if (part.Blank)
            {
                if (video)
                    graph.Append($"color=c=black:s={width}x{height}:r={fps}:d={length},setsar=1,format={pixelFormat}[v{i}];");
                if (audio)
                    graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={length}[a{i}];");
                continue;
            }
            var start = Seconds(part.Start - from);
            var end = Seconds(part.End - from);
            var speed = part.PlaySpeed;
            var setpts = speed == 1 ? "setpts=PTS-STARTPTS" : $"setpts=(PTS-STARTPTS)/{Seconds(speed)}";
            if (video)
                graph.Append($"[vs{source}]trim=start={start}:end={end},{setpts},{picture}[v{i}];");
            if (audio)
            {
                graph.Append($"[as{source}]atrim=start={start}:end={end},asetpts=PTS-STARTPTS");
                foreach (var tempo in Tempo(speed))
                    graph.Append($",atempo={Seconds(tempo)}");
                if (part.PlayVolume != 1)
                    graph.Append($",volume={Seconds(part.PlayVolume)}");
                graph.Append($",{ClipAudioFormat}[a{i}];");
            }
            source++;
        }

        for (var i = 0; i < edit.Parts.Count; i++)
            graph.Append(video ? $"[v{i}]" : "").Append(audio ? $"[a{i}]" : "");
        graph.Append($"concat=n={edit.Parts.Count}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}{(video ? "[vc]" : "")}{(audio ? "[ac]" : "")};");

        var total = edit.Length;
        var fadeIn = Math.Min(ClipEdit.Fade(edit.FadeIn), total / 2);
        var fadeOut = Math.Min(ClipEdit.Fade(edit.FadeOut), total / 2);
        var outputs = new List<string>();
        if (video)
            outputs.Add("[vc]" + Fades("fade", fadeIn, fadeOut, total) + "[v]");
        if (song is not null)
        {
            // The music: as long as it plays, at its volume, faded at its ends, delayed to its place, under the sound.
            if (!audio)
                outputs.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={Seconds(total)}[ac]");
            var length = song.PlayLength(total);
            var fade = song.Fade ? Math.Min(ClipMusic.FadeSeconds, length / 2) : 0;
            var delay = (long)Math.Round(song.Offset * 1000);
            var chain = $"[1:a:0]atrim=duration={Seconds(length)},asetpts=PTS-STARTPTS";
            if (song.PlayVolume != 1)
                chain += $",volume={Seconds(song.PlayVolume)}";
            if (fade > 0)
                chain += "," + Fades("afade", fade, fade, length);
            if (delay > 0)
                chain += $",adelay={delay}:all=1";
            outputs.Add($"{chain},{ClipAudioFormat}[m]");
            outputs.Add("[ac][m]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[am]");
            outputs.Add("[am]" + Fades("afade", fadeIn, fadeOut, total) + "[a]");
        }
        else if (audio)
            outputs.Add("[ac]" + Fades("afade", fadeIn, fadeOut, total) + "[a]");
        graph.Append(string.Join(';', outputs));
        return new ClipGraph(inputs, graph.ToString(), sound, total, video);
    }

    /// <summary>
    /// The arguments that write <paramref name="edit"/> of <paramref name="input"/> as <paramref name="target"/>,
    /// encoded with the preset's quality, largest height and codec, at the video's size and frame rate.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has no video stream.</exception>
    /// <exception cref="NotSupportedException">FFmpeg cannot write <paramref name="target"/>.</exception>
    internal static List<string> ClipEditArguments(string input, string output, string target, ClipEdit edit, MediaOptions options,
        MediaInfo info, IReadOnlySet<string>? encoders = null)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        if (!VideoTargets.TryGetValue(target, out var spec))
            throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
        var (width, height) = ClipSize(video, options.MaxHeight);
        var fps = NearestRate(video.FrameRate) ?? "30";
        var encoder = VideoEncoder(target, spec.VideoCodec!, options.VideoCodec, encoders);
        var tenBit = video.IsHighBitDepth && encoder is "libx265" or "libsvtav1" or "libaom-av1";
        // "Remove audio" leaves the clip silent, music and all.
        var graph = ClipGraphOf(input, edit, !options.RemoveAudio && info.Audio is not null, width, height, fps, tenBit ? "yuv420p10le" : "yuv420p",
            music: !options.RemoveAudio);
        var audio = graph.HasAudio;
        var pal = IsPal(video.FrameRate);
        var filter = target is "vob" or "dv"
            ? graph.Filter.Replace("[v]", "[vd]", StringComparison.Ordinal) + $";[vd]{DvdOrDvFilter(video, pal)}[v]"
            : graph.Filter;

        List<string> args = [.. Common, .. graph.Inputs, "-filter_complex", filter, "-map", "[v]"];
        args.AddRange(VideoCodecArguments(target, encoder, options.Quality, video, pal));
        if (audio)
        {
            args.AddRange(["-map", "[a]"]);
            args.AddRange(AudioCodecArguments(target, spec.AudioCodec, options, new AudioStreamInfo(2, 48000), inVideo: true));
        }
        else
            args.Add("-an");
        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// The arguments that play <paramref name="edit"/> in the clip window: its pictures <paramref name="height"/>
    /// pixels high as raw BGRA frames at <paramref name="fps"/> on stdout.
    /// </summary>
    internal static List<string> ClipPreviewVideo(string input, ClipEdit edit, VideoStreamInfo video, int height, int fps,
        out int width, out int frameHeight)
    {
        (width, frameHeight) = ClipSize(video, height);
        var graph = ClipGraphOf(input, edit, false, width, frameHeight, fps.ToString(CultureInfo.InvariantCulture), "bgra");
        return [.. Common, .. graph.Inputs, "-filter_complex", graph.Filter, "-map", "[v]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"];
    }

    /// <summary>The arguments that play the sound of <paramref name="edit"/>: a WAV stream on stdout (for ffplay).</summary>
    /// <param name="hasAudio">The video has sound of its own (else only the music plays).</param>
    internal static List<string> ClipPreviewAudio(string input, ClipEdit edit, VideoStreamInfo video, bool hasAudio = true)
    {
        var (width, height) = ClipSize(video, 90);
        var graph = ClipGraphOf(input, edit, hasAudio, width, height, "10", video: false);
        return [.. Common, .. graph.Inputs, "-filter_complex", graph.Filter, "-map", "[a]", "-c:a", "pcm_s16le", "-f", "wav", "pipe:1"];
    }

    /// <summary>
    /// The size of a clip's pictures: as the video is shown (pixel aspect and rotation applied), scaled down to
    /// <paramref name="maxHeight"/> (0: kept), even sides.
    /// </summary>
    internal static (int Width, int Height) ClipSize(VideoStreamInfo video, int maxHeight)
    {
        var width = video.Width * (video.SampleAspect > 0 ? video.SampleAspect : 1);
        double height = video.Height;
        if (video.IsRotated)
            (width, height) = (height, width);
        var scale = maxHeight > 0 && height > maxHeight ? maxHeight / height : 1;
        return (Even(Math.Max(16, width * scale)), Even(Math.Max(16, height * scale)));
    }

    /// <summary>
    /// The <c>atempo</c> steps for <paramref name="speed"/>: each between 0.5 and 2, which every FFmpeg takes (0.25 is
    /// 0.5 twice); none for 1.
    /// </summary>
    internal static IReadOnlyList<double> Tempo(double speed)
    {
        var steps = new List<double>();
        var rest = speed;
        while (rest > 2 + 1e-9)
        {
            steps.Add(2);
            rest /= 2;
        }
        while (rest < 0.5 - 1e-9)
        {
            steps.Add(0.5);
            rest /= 0.5;
        }
        if (Math.Abs(rest - 1) > 1e-9)
            steps.Add(rest);
        return steps;
    }

    /// <summary>The fade in and out of <paramref name="filter"/> (fade or afade), or a pass-through when there is none.</summary>
    private static string Fades(string filter, double fadeIn, double fadeOut, double total)
    {
        var steps = new List<string>();
        if (fadeIn > 0)
            steps.Add($"{filter}=t=in:st=0:d={Seconds(fadeIn)}");
        if (fadeOut > 0)
            steps.Add($"{filter}=t=out:st={Seconds(Math.Max(0, total - fadeOut))}:d={Seconds(fadeOut)}");
        return steps.Count > 0 ? string.Join(',', steps) : filter == "fade" ? "null" : "anull";
    }

    /// <summary>A number as FFmpeg's filters read it: up to three decimals, a point, no thousands separator.</summary>
    private static string Seconds(double value) => Math.Round(Math.Max(0, value), 3).ToString("0.###", CultureInfo.InvariantCulture);
}
