// Several files into one with FFmpeg: video clips or audio files back to back ("Merge videos", "Merge audio") and
// pictures shown one after the other, with crossfades if the preset asks ("Video from images"). Pure apart from the
// filter graph file of a long graph, like MediaEncoding: the command lines are unit tested without FFmpeg.
// FfmpegConverter runs them.

using System.Globalization;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>Command lines that join files.</summary>
internal static class MediaJoining
{
    /// <summary>Frames per second of a slideshow: smooth fades, and what every player plays.</summary>
    public const int SlideshowFps = 30;

    /// <summary>The rate every clip's sound is brought to before the clips are joined (what video uses most).</summary>
    private const int JoinSampleRate = 48000;

    /// <summary>
    /// A filter graph longer than this goes into a file (<c>-/filter_complex</c>): Windows limits a whole command line
    /// to 32,767 characters, and every clip adds a chain to the graph.
    /// </summary>
    internal const int MaxInlineGraph = 8000;

    // ───────────────────────── Videos ─────────────────────────

    /// <summary>
    /// True when the clips can be joined without an encode: every one is a video MP4 takes as it is, all with the same
    /// codec setup (parameter sets: the MP4 keeps only the first clip's), picture size, pixel format, frame rate, turn
    /// and pixel shape, and the same kind of sound (or none at all). Videos from one phone usually are.
    /// </summary>
    public static bool CanCopyVideos(IReadOnlyList<MediaInfo> clips, MediaOptions options)
    {
        if (clips.Count == 0 || clips[0].Video is not { } first)
            return false;
        var sameVideo = clips.All(c => c.Video is { } v && MediaEncoding.CanCopyVideo("mp4", v, options)
            && v.Codec == first.Codec && v.Width == first.Width && v.Height == first.Height && v.PixelFormat == first.PixelFormat
            && v.Rotation == first.Rotation && Math.Abs(v.SampleAspect - first.SampleAspect) < 0.01
            && Math.Abs(v.FrameRate - first.FrameRate) < 0.01 && v.HeaderHash is not null && v.HeaderHash == first.HeaderHash);
        if (!sameVideo)
            return false;
        if (clips[0].Audio is not { } sound)
            return clips.All(c => c.Audio is null);
        return clips.All(c => c.Audio is { } a && MediaEncoding.CanCopyAudio("mp4", a, options)
            && a.Codec == sound.Codec && a.SampleRate == sound.SampleRate && a.Channels == sound.Channels);
    }

    /// <summary>The list of the concat demuxer: one <c>file '…'</c> line per clip, quotes escaped.</summary>
    public static string ConcatList(IEnumerable<string> paths) =>
        string.Concat(paths.Select(path => "file '" + Path.GetFullPath(path).Replace("'", @"'\''", StringComparison.Ordinal) + "'\n"));

    /// <summary>Clips that share codecs and size, copied one after the other into one MP4 (<see cref="CanCopyVideos"/>).</summary>
    public static List<string> CopyVideos(string listFile, string output, MediaInfo first)
    {
        List<string> args = [.. MediaEncoding.Common, "-f", "concat", "-safe", "0", "-i", listFile, "-map", "0:V:0"];
        if (first.Audio is not null)
            args.AddRange(["-map", "0:a:0"]);
        args.AddRange(["-c", "copy"]);
        if (first.Video?.Codec == "hevc")
            args.AddRange(["-tag:v", "hvc1"]);
        args.AddRange(MediaEncoding.MetadataArguments());
        args.AddRange(MediaEncoding.ContainerArguments("mp4"));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", "mp4", "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// The frame every clip is fitted into: the first clip's picture as it is shown (turned, square pixels), even, and
    /// no taller than <see cref="MediaOptions.MaxHeight"/>.
    /// </summary>
    internal static (int Width, int Height) JoinFrame(VideoStreamInfo first, int maxHeight)
    {
        var height = (double)Math.Max(2, first.DisplayHeight);
        if (maxHeight > 0)
            height = Math.Min(height, Math.Max(16, maxHeight));
        return (MediaEncoding.Even(height * first.DisplayAspect), MediaEncoding.Even(height));
    }

    /// <summary>
    /// Clips of any codec and size encoded into one MP4 (H.264, or the preset's codec, and AAC): each is fitted into the
    /// first clip's frame with black bars, at its frame rate. A clip without sound is silent for its length when
    /// others have sound.
    /// </summary>
    /// <param name="encoder">The video encoder (<see cref="MediaEncoding.VideoEncoder"/>).</param>
    /// <param name="workDirectory">Where a long filter graph is written (<see cref="MaxInlineGraph"/>).</param>
    /// <exception cref="InvalidOperationException">A clip has no video, or FFmpeg can't tell how long a silent one is.</exception>
    public static List<string> EncodeVideos(IReadOnlyList<(string Path, MediaInfo Info)> clips, string output, MediaOptions options,
        string encoder, string workDirectory)
    {
        var first = clips[0].Info.Video ?? throw new InvalidOperationException($"{Path.GetFileName(clips[0].Path)} has no video stream.");
        var (width, height) = JoinFrame(first, options.MaxHeight);
        var fps = MediaEncoding.NearestRate(first.FrameRate) ?? "30";
        var withSound = clips.Any(c => c.Info.Audio is not null);

        var graph = new List<string>();
        var segments = "";
        for (var i = 0; i < clips.Count; i++)
        {
            var (path, info) = clips[i];
            var video = info.Video ?? throw new InvalidOperationException($"{Path.GetFileName(path)} has no video stream.");
            graph.Add($"[{i}:V:0]{MediaEncoding.SquarePixels(video)}{Fit(width, height)},fps={fps},format=yuv420p[v{i}]");
            segments += $"[v{i}]";
            if (!withSound)
                continue;
            graph.Add(info.Audio is not null ? $"[{i}:a:0]{Resample("stereo")}[a{i}]" : $"{Silence(path, info, "stereo")}[a{i}]");
            segments += $"[a{i}]";
        }
        graph.Add($"{segments}concat=n={clips.Count}:v=1:a={(withSound ? 1 : 0)}[v]{(withSound ? "[a]" : "")}");

        List<string> args = [.. MediaEncoding.Common];
        foreach (var (path, _) in clips)
            args.AddRange(["-i", path]);
        args.AddRange(GraphArguments(string.Join(';', graph), workDirectory));
        args.AddRange(["-map", "[v]"]);
        args.AddRange(MediaEncoding.VideoCodecArguments("mp4", encoder, options.Quality, first, pal: false));
        if (withSound)
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", Text(AacKbps(options, 2)) + "k"]);
        else
            args.Add("-an");
        args.AddRange(MediaEncoding.MetadataArguments());
        args.AddRange(MediaEncoding.ContainerArguments("mp4"));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", "mp4", "-progress", "pipe:1", output]);
        return args;
    }

    // ───────────────────────── Audio ─────────────────────────

    /// <summary>
    /// Audio files (or the sound of videos) decoded and joined into one M4A, sample after sample: no gap and no
    /// encoder padding between them, which copying the clips' AAC would leave. Mono when every clip is mono, else
    /// stereo; AAC at the preset's quality or bitrate, or Apple Lossless when the preset asks for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file has no sound and FFmpeg can't tell how long it is.</exception>
    public static List<string> EncodeAudio(IReadOnlyList<(string Path, MediaInfo Info)> clips, string output, MediaOptions options,
        string workDirectory)
    {
        var mono = clips.All(c => c.Info.Audio is { Channels: 1 });
        var layout = mono ? "mono" : "stereo";
        var graph = new List<string>();
        var segments = "";
        for (var i = 0; i < clips.Count; i++)
        {
            var (path, info) = clips[i];
            graph.Add(info.Audio is not null ? $"[{i}:a:0]{Resample(layout)}[a{i}]" : $"{Silence(path, info, layout)}[a{i}]");
            segments += $"[a{i}]";
        }
        graph.Add($"{segments}concat=n={clips.Count}:v=0:a=1[a]");

        List<string> args = [.. MediaEncoding.Common];
        foreach (var (path, _) in clips)
            args.AddRange(["-i", path]);
        args.AddRange(GraphArguments(string.Join(';', graph), workDirectory));
        args.AddRange(["-map", "[a]", "-vn"]);
        if (MediaCodecs.Effective("m4a", options.AudioCodec) == AudioCodec.Alac)
            args.AddRange(["-c:a", "alac"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", Text(AacKbps(options, mono ? 1 : 2)) + "k"]);
        args.AddRange(MediaEncoding.MetadataArguments());
        args.AddRange(MediaEncoding.ContainerArguments("m4a"));
        args.AddRange(["-f", "ipod", "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>The preset's AAC bitrate, else 256 / 192 / 128 kbit/s per stereo pair by quality (half for mono).</summary>
    private static int AacKbps(MediaOptions options, int channels) =>
        options.AudioBitrateKbps > 0
            ? Math.Clamp(options.AudioBitrateKbps, 16, 512)
            : MediaEncoding.Choose(options.Quality, 256, 192, 128) * channels / 2;

    private static string Resample(string layout) =>
        $"aresample={JoinSampleRate},aformat=sample_fmts=fltp:sample_rates={JoinSampleRate}:channel_layouts={layout}";

    /// <summary>Silence as long as a clip without sound, so the sound of the clips after it stays in step.</summary>
    private static string Silence(string path, MediaInfo info, string layout)
    {
        var seconds = info.Duration?.TotalSeconds ?? 0;
        if (seconds <= 0)
            throw new InvalidOperationException($"{Path.GetFileName(path)} has no sound, and FFmpeg can't tell how long it is.");
        return $"aevalsrc=0:c={layout}:s={JoinSampleRate}:d={Seconds(seconds)},{Resample(layout)}";
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>The frame every slideshow picture is fitted into: 1080p.</summary>
    public const int SlideshowWidth = 1920, SlideshowHeight = 1080;

    /// <summary>How long a slideshow of <paramref name="count"/> pictures plays: the fades overlap the pictures.</summary>
    public static TimeSpan SlideshowDuration(int count, MediaOptions options) => count <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(count * options.SlideDuration - (count - 1) * options.FadeDuration);

    /// <summary>
    /// One picture fitted into the slideshow frame (black bars, transparency on black) as a JPEG at FFmpeg's best
    /// quality: the slideshow then holds a small 1080p picture per slide instead of a full-size photo.
    /// </summary>
    public static List<string> PrepareSlide(string picture, string output, int width, int height) =>
    [
        .. MediaEncoding.Common, "-i", picture, "-map", "0:V:0",
        "-vf", $"premultiply=inplace=1,{Fit(width, height, ":flags=lanczos")}",
        "-frames:v", "1", "-update", "1", "-c:v", "mjpeg", "-q:v", "2", "-f", "image2", output,
    ];

    /// <summary>
    /// The prepared slides (<see cref="PrepareSlide"/>) as one MP4: each shown for <see cref="MediaOptions.SlideDuration"/>,
    /// fading into the next over <see cref="MediaOptions.FadeDuration"/> (a cut without crossfades). BT.709 in TV range,
    /// 30 frames per second, no sound.
    /// </summary>
    /// <param name="encoder">The video encoder (<see cref="MediaEncoding.VideoEncoder"/>).</param>
    /// <param name="workDirectory">Where a long filter graph is written (<see cref="MaxInlineGraph"/>).</param>
    public static List<string> Slideshow(IReadOnlyList<string> slides, string output, MediaOptions options, string encoder, string workDirectory)
    {
        var (width, height) = (SlideshowWidth, SlideshowHeight);
        var show = Seconds(options.SlideDuration);
        var fade = options.FadeDuration;
        var graph = new List<string>();
        for (var i = 0; i < slides.Count; i++)
        {
            // One decoded frame per picture, repeated for its time: no picture is read more than once.
            graph.Add($"[{i}:v]scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setsar=1,fps={SlideshowFps}," +
                      $"tpad=stop_mode=clone:stop_duration={show},trim=duration={show},setpts=PTS-STARTPTS[s{i}]");
        }
        if (slides.Count == 1)
        {
            graph.Add("[s0]null[out]");
        }
        else if (fade > 0)
        {
            // Each fade starts where the previous output ends minus the fade: k × (show − fade).
            var previous = "s0";
            for (var k = 1; k < slides.Count; k++)
            {
                var label = k == slides.Count - 1 ? "out" : $"x{k}";
                graph.Add($"[{previous}][s{k}]xfade=transition=fade:duration={Seconds(fade)}:offset={Seconds(k * (options.SlideDuration - fade))}[{label}]");
                previous = label;
            }
        }
        else
        {
            graph.Add($"{string.Concat(Enumerable.Range(0, slides.Count).Select(i => $"[s{i}]"))}concat=n={slides.Count}:v=1:a=0[out]");
        }

        List<string> args = [.. MediaEncoding.Common];
        foreach (var slide in slides)
            args.AddRange(["-i", slide]);
        args.AddRange(GraphArguments(string.Join(';', graph), workDirectory));
        args.AddRange(["-map", "[out]"]);
        // Pictures cost an encoder little: one step above the preset's quality, as a single picture's video does.
        var quality = options.Quality == MediaQuality.Small ? MediaQuality.Balanced : MediaQuality.High;
        var frame = new VideoStreamInfo(width, height, 1, 0, SlideshowFps, PixelFormat: "yuv420p");
        args.AddRange(MediaEncoding.VideoCodecArguments("mp4", encoder, quality, frame, pal: false));
        args.AddRange(["-r", Text(SlideshowFps), "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-an"]);
        args.AddRange(MediaEncoding.ContainerArguments("mp4"));
        args.AddRange(["-f", "mp4", "-progress", "pipe:1", output]);
        return args;
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Fits a picture into <paramref name="width"/> × <paramref name="height"/>, centred, with black bars.</summary>
    /// <param name="scaleOptions">More options of the scale filter, e.g. <c>":flags=lanczos"</c>.</param>
    private static string Fit(int width, int height, string scaleOptions = "") =>
        $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2{scaleOptions}," +
        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1";

    /// <summary>
    /// <c>-filter_complex</c> with the graph, or <c>-/filter_complex</c> with a file holding it when it is long
    /// (<see cref="MaxInlineGraph"/>).
    /// </summary>
    internal static string[] GraphArguments(string graph, string workDirectory)
    {
        if (graph.Length <= MaxInlineGraph)
            return ["-filter_complex", graph];
        Directory.CreateDirectory(workDirectory);
        var file = Path.Combine(workDirectory, $"graph-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, graph);
        return ["-/filter_complex", file];
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
