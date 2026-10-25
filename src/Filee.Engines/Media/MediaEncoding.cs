// Which container and codecs Filee writes for every video and audio format, and the FFmpeg command lines for them.
// Pure (no processes), so the choices are unit tested without FFmpeg. Every encoder and muxer used here was checked
// against the pinned FFmpeg build (engines.json: `ffmpeg -encoders` / `ffmpeg -muxers`).

using System.Globalization;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>How one target format is written.</summary>
/// <param name="Muxer">FFmpeg container name (<c>-f</c>), always given explicitly: some extensions are ambiguous.</param>
/// <param name="VideoCodec">FFmpeg video encoder, null for audio-only formats.</param>
/// <param name="AudioCodec">FFmpeg audio encoder.</param>
internal sealed record MediaTarget(string Muxer, string? VideoCodec, string AudioCodec);

/// <summary>One run of ffmpeg.</summary>
/// <param name="Arguments">Command line after the executable.</param>
/// <param name="Weight">Share of the whole conversion's progress (the passes of one conversion add up to 1).</param>
internal sealed record FfmpegPass(IReadOnlyList<string> Arguments, double Weight);

/// <summary>Encoding choices and command lines.</summary>
internal static partial class MediaEncoding
{
    /// <summary>
    /// Video formats Filee writes. H.264 + AAC wherever the container allows it: it plays on every browser, phone,
    /// TV and editor. The other containers get the codecs their players expect.
    /// </summary>
    public static IReadOnlyDictionary<string, MediaTarget> VideoTargets { get; } = new Dictionary<string, MediaTarget>
    {
        ["mp4"] = new("mp4", "libx264", "aac"),
        // Apple's M4V is MP4 (FFmpeg's "m4v" muxer would write a raw MPEG-4 part 2 stream instead).
        ["m4v"] = new("mp4", "libx264", "aac"),
        ["mov"] = new("mov", "libx264", "aac"),
        ["mkv"] = new("matroska", "libx264", "aac"),
        ["flv"] = new("flv", "libx264", "aac"),
        ["ts"] = new("mpegts", "libx264", "aac"),
        ["m2ts"] = new("mpegts", "libx264", "aac"), // + 192-byte Blu-ray packets, see ContainerArguments
        ["3gp"] = new("3gp", "libx264", "aac"),
        ["3g2"] = new("3g2", "libx264", "aac"),
        // The WebM specification allows only VP8/VP9/AV1 with Vorbis/Opus.
        ["webm"] = new("webm", "libvpx-vp9", "libopus"),
        // MPEG-4 part 2 tagged XVID + CBR MP3: what DVD players, old TVs and Windows' built-in codecs read in AVI.
        ["avi"] = new("avi", "mpeg4", "libmp3lame"),
        ["wmv"] = new("asf", "wmv2", "wmav2"),
        // MPEG-2 program stream ("vob" is FFmpeg's plain MPEG-2 PS muxer; "mpeg" would write an MPEG-1 system stream).
        ["mpg"] = new("vob", "mpeg2video", "mp2"),
        // DVD-Video compliant: 720×480 NTSC / 720×576 PAL, AC-3, DVD mux rate and navigation packets.
        ["vob"] = new("dvd", "mpeg2video", "ac3"),
        ["ogv"] = new("ogg", "libtheora", "libvorbis"),
        // DV has fixed frame sizes and rates (see DvdOrDvFilter) and 48 kHz PCM.
        ["dv"] = new("dv", "dvvideo", "pcm_s16le"),
        // MXF OP1a with long-GOP MPEG-2 (XDCAM style) and 48 kHz PCM: what broadcast and editing tools import.
        ["mxf"] = new("mxf", "mpeg2video", "pcm_s16le"),
    };

    /// <summary>Audio formats Filee writes (also the targets of audio extraction from video).</summary>
    public static IReadOnlyDictionary<string, MediaTarget> AudioTargets { get; } = new Dictionary<string, MediaTarget>
    {
        ["mp3"] = new("mp3", null, "libmp3lame"),
        // The iPod muxer writes the M4A/M4B brands iTunes and phones expect.
        ["m4a"] = new("ipod", null, "aac"),
        ["m4b"] = new("ipod", null, "aac"),
        ["aac"] = new("adts", null, "aac"),
        ["wav"] = new("wav", null, "pcm_s16le"),
        ["flac"] = new("flac", null, "flac"),
        ["ogg"] = new("ogg", null, "libvorbis"),
        ["opus"] = new("opus", null, "libopus"),
        ["wma"] = new("asf", null, "wmav2"),
        ["aiff"] = new("aiff", null, "pcm_s16be"),
        ["ac3"] = new("ac3", null, "ac3"),
        // AMR-NB (OpenCORE): 8 kHz mono speech, what phones record voice memos in.
        ["amr"] = new("amr", null, "libopencore_amrnb"),
        ["au"] = new("au", null, "pcm_s16be"),
        // CAF with big-endian linear PCM, the variant Apple's own tools write by default.
        ["caf"] = new("caf", null, "pcm_s16be"),
        ["weba"] = new("webm", null, "libopus"),
        ["mka"] = new("matroska", null, "libopus"),
        ["mp2"] = new("mp2", null, "mp2"),
        // 16-bit PCM in an extended block: exact sample rate (8-bit blocks can only approximate it).
        ["voc"] = new("voc", null, "pcm_s16le"),
        // Lossless: WavPack and TTA (FFmpeg's own encoders); Sony Wave64 is WAV without the 4 GB limit.
        ["wv"] = new("wv", null, "wavpack"),
        ["tta"] = new("tta", null, "tta"),
        ["w64"] = new("w64", null, "pcm_s16le"),
        ["eac3"] = new("eac3", null, "eac3"),
    };

    /// <summary>Subtitle formats a video's first text subtitle track can be taken out as: (muxer, encoder).</summary>
    public static IReadOnlyDictionary<string, (string Muxer, string Encoder)> SubtitleTargets { get; } = new Dictionary<string, (string, string)>
    {
        ["srt"] = ("srt", "srt"),
        ["ass"] = ("ass", "ass"),
        ["vtt"] = ("webvtt", "webvtt"),
    };

    /// <summary>
    /// Subtitle codecs that are text (FFmpeg decodes them to text; CEA-608 captions too). Blu-ray PGS, DVD VobSub and
    /// DVB subtitles are pictures.
    /// </summary>
    private static readonly HashSet<string> TextSubtitleCodecs =
    [
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "sami", "realtext", "subviewer", "subviewer1",
        "jacosub", "mpl2", "pjs", "vplayer", "stl", "eia_608", "ttml",
    ];

    /// <summary>Formats an animated GIF or a still picture (PNG) can be turned into.</summary>
    public static IReadOnlyList<string> PictureToVideoTargets { get; } = ["mp4", "webm", "mov"];

    /// <summary>How long a video made of one still picture shows it.</summary>
    public static TimeSpan StillDuration { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The largest picture H.264 and VP9 at level 6.2 (their highest) allow: 8192 × 4352, about 35.6 megapixels.
    /// Larger photos (48 MP phone cameras) are scaled down to it, or many players refuse the video.
    /// </summary>
    private const long MaxVideoPixels = 8192L * 4352;

    /// <summary>The longest side VP9 level 6.2 allows (H.264 allows 16880), for panoramas within the pixel limit.</summary>
    private const int MaxVideoSide = 16384;

    /// <summary>Audio targets that can carry a cover picture (album art) copied from the source.</summary>
    private static readonly HashSet<string> CoverTargets = ["mp3", "m4a", "m4b", "flac"];

    /// <summary>
    /// Frame rates MPEG-2 (and MXF, which only knows broadcast rates) can signal exactly. Other rates are
    /// converted to the nearest one by dropping or repeating frames.
    /// </summary>
    private static readonly (int Num, int Den)[] BroadcastRates =
        [(24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1)];

    /// <summary>Container metadata that describes the source file, not the content: not copied.</summary>
    private static readonly string[] SourceOnlyTags = ["major_brand", "minor_version", "compatible_brands"];

    /// <summary>Global arguments of every run: no banner, no keyboard input, only errors on stderr, overwrite.</summary>
    internal static readonly string[] Common = ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y"];

    /// <summary>
    /// The ffmpeg runs that convert <paramref name="input"/> into <paramref name="output"/> in format
    /// <paramref name="target"/>.
    /// </summary>
    /// <param name="workDirectory">Scratch folder (the GIF palette goes there).</param>
    /// <param name="cover">The source's cover for targets FFmpeg can't put a picture stream into (<see cref="CoverArt"/>).</param>
    /// <param name="encoders">The encoders the FFmpeg in use has (<c>ffmpeg -encoders</c>); null assumes the pinned build's.</param>
    /// <param name="hardware">
    /// The hardware encoder that passed its probe for <see cref="HardwareCodec"/> of this file; null encodes with x264 /
    /// x265 / SVT-AV1 (also the retry after a hardware encode failed).
    /// </param>
    /// <param name="animationSpeed">Playback speed of an animated GIF or APNG (ImageOptions.PlaybackSpeed); 1 keeps the video's.</param>
    /// <param name="clip">
    /// A video target: only this part of the video, cut exactly (the video is always encoded; <see cref="ClipCopy"/>
    /// copies it instead, cut at key frames).
    /// </param>
    /// <exception cref="InvalidOperationException">The file lacks the stream the target needs (e.g. no audio track).</exception>
    public static IReadOnlyList<FfmpegPass> Plan(string input, string output, string target, MediaOptions options, MediaInfo info, string workDirectory,
        CoverExtras? cover = null, IReadOnlySet<string>? encoders = null, HardwareEncoder? hardware = null, double animationSpeed = 1,
        ClipRange? clip = null)
    {
        if (clip is { } part && VideoTargets.TryGetValue(target, out var clipTarget))
            return [new FfmpegPass(Video(input, output, target, clipTarget, options, info, encoders, hardware, part), 1)];
        if (target == "gif")
            return AnimatedGif(input, output, options, info, workDirectory, animationSpeed);
        if (target == "apng")
            return [new FfmpegPass(AnimatedPng(input, output, options, info, animationSpeed), 1)];
        if (target == "png")
            return [new FfmpegPass(PosterFrame(input, output, info), 1)];
        if (VideoTargets.TryGetValue(target, out var video))
            return [new FfmpegPass(Video(input, output, target, video, options, info, encoders, hardware), 1)];
        if (AudioTargets.TryGetValue(target, out var audio))
            return [new FfmpegPass(Audio(input, output, target, audio, options, info, cover), 1)];
        if (SubtitleTargets.ContainsKey(target))
            return [new FfmpegPass(Subtitles(input, output, target, info), 1)];
        throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
    }

    /// <summary>
    /// Maps preset image quality (1..100, 100 best) to FFmpeg's mjpeg -q:v parameter (1..31, 1 best, 2..3 visually lossless).
    /// </summary>
    public static int QualityToQv(int quality)
    {
        var clamped = Math.Clamp(quality, 1, 100);
        return Math.Clamp((int)Math.Round(2.0 + (100 - clamped) * 29.0 / 99.0), 1, 31);
    }

    /// <summary>
    /// Every frame (or every <paramref name="frameInterval"/>th) of the first video stream as numbered pictures in
    /// <paramref name="outputPattern"/> (an image2 pattern such as <c>frame_%06d.png</c>), for focus stacking and
    /// picking a shot.
    /// </summary>
    /// <remarks>
    /// Each decoded frame is written once. The image2 muxer would otherwise pace frames to a constant rate and
    /// duplicate (or drop) frames of a variable frame rate video (phones), so frames pass through with their own
    /// timestamps; with an interval the select filter leaves gaps, which <c>vfr</c> keeps as they are.
    /// </remarks>
    public static List<string> FrameSequence(
        string input,
        string outputPattern,
        string imageExt,
        VideoStreamInfo? video = null,
        int quality = 90,
        int frameInterval = 1)
    {
        var select = frameInterval > 1 ? $"select='not(mod(n\\,{frameInterval}))'" : "null";
        List<string> args =
        [
            .. Common, "-i", input, "-map", "0:V:0", "-vf", (video is null ? "" : SquarePixels(video)) + select,
            "-fps_mode", frameInterval > 1 ? "vfr" : "passthrough",
        ];

        if (imageExt is "jpg" or "jpeg")
            args.AddRange(["-c:v", "mjpeg", "-q:v", QualityToQv(quality).ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["-c:v", "png"]);

        args.AddRange(["-f", "image2", "-progress", "pipe:1", outputPattern]);
        return args;
    }

    // ───────────────────────── Video ─────────────────────────

    /// <summary>
    /// The codec a hardware encoder would have to write for this file ("h264", "hevc" or "av1"), or null when the preset
    /// does not ask for hardware encoding, the target is written with another codec (VP9, MPEG-2, …), or the video is
    /// copied anyway.
    /// </summary>
    /// <param name="encode">The video is encoded even where it could be copied (an exact clip).</param>
    internal static string? HardwareCodec(string target, MediaOptions options, MediaInfo info, bool encode = false) =>
        options.HardwareEncoding && info.Video is { } video && VideoTargets.ContainsKey(target)
        && MediaCodecs.TakesHardwareEncoding(target, options.VideoCodec) && (encode || info.RawStream || !CanCopyVideo(target, video, options))
            ? MediaCodecs.Effective(target, options.VideoCodec) switch
            {
                VideoCodec.Hevc => "hevc",
                VideoCodec.Av1 => "av1",
                _ => "h264",
            }
            : null;

    private static List<string> Video(string input, string output, string target, MediaTarget spec, MediaOptions options, MediaInfo info,
        IReadOnlySet<string>? encoders, HardwareEncoder? hardware, ClipRange? clip = null)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        // An input without sound is fine: the output simply has no audio track. Every track is kept (languages,
        // commentary, issue #396), except in containers that hold one and in a clip, which keeps the first one as a
        // copied or edited clip does.
        IReadOnlyList<AudioStreamInfo> tracks = options.RemoveAudio ? []
            : clip is not null || SingleAudioTargets.Contains(target) ? [.. info.AudioTracks.Take(1)]
            : info.AudioTracks;

        // A clip: FFmpeg seeks the input and decodes from the key frame before the start, so the first frame is exact.
        List<string> args = [.. Common, .. clip is { } part ? ClipInput(part) : [], "-i", input, "-map", "0:V:0"];
        for (var i = 0; i < tracks.Count; i++)
            args.AddRange(["-map", $"0:a:{i}"]);

        if (clip is null && !info.RawStream && CanCopyVideo(target, video, options))
        {
            args.AddRange(["-c:v", "copy"]);
            // Apple plays HEVC in MP4/MOV only under the "hvc1" tag (FFmpeg keeps "hev1" from some sources).
            if (video.Codec == "hevc" && target is "mp4" or "m4v" or "mov")
                args.AddRange(["-tag:v", "hvc1"]);
        }
        else if (hardware is not null && HardwareCodec(target, options, info, encode: clip is not null) == hardware.Codec)
        {
            // VA-API's device is a global option, so it goes before the input.
            args.InsertRange(Common.Length, HardwareVideo.InputArguments(hardware));
            args.AddRange(["-vf", ScaleFilter(options.MaxHeight) + HardwareVideo.FilterSuffix(hardware, HardwareVideo.TenBit(hardware.Codec, video))]);
            args.AddRange(HardwareVideo.CodecArguments(hardware, target, options.Quality, video, options.MaxHeight));
        }
        else
        {
            var pal = IsPal(video.FrameRate);
            args.AddRange(["-vf", target is "vob" or "dv" ? DvdOrDvFilter(video, pal) : ScaleFilter(options.MaxHeight)]);
            args.AddRange(VideoCodecArguments(target, VideoEncoder(target, spec.VideoCodec!, options.VideoCodec, encoders), options.Quality, video, pal));
        }

        if (tracks.Count == 0)
            args.Add("-an");
        for (var i = 0; i < tracks.Count; i++)
        {
            // Each track on its own: copied where the container takes it, else encoded for its own channel count. A
            // clip's sound is encoded too: copied, it would start with the packets before the seek point (a second early).
            var trackArgs = clip is null && CanCopyAudio(target, tracks[i], options)
                ? ["-c:a", "copy"]
                : AudioCodecArguments(target, spec.AudioCodec, options, tracks[i], inVideo: true);
            args.AddRange(tracks.Count == 1 ? trackArgs : ForAudioTrack(trackArgs, i));
        }

        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        // Sparse or late-starting streams (subtitles-like audio) otherwise fail with "too many packets buffered".
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>Containers that hold a single audio track: the first one is kept.</summary>
    private static readonly HashSet<string> SingleAudioTargets = ["flv", "dv"];

    /// <summary>Audio options addressed to one output track (<c>-b:a</c> → <c>-b:a:2</c>, <c>-ac</c> → <c>-ac:a:2</c>).</summary>
    internal static IEnumerable<string> ForAudioTrack(IReadOnlyList<string> args, int track) =>
        args.Select((arg, i) => arg switch
        {
            // Values are never options: only the names are addressed.
            _ when i > 0 && args[i - 1].StartsWith('-') && !arg.StartsWith('-') => arg,
            "-c:a" or "-b:a" or "-q:a" => $"{arg}:{track}",
            "-ac" or "-ar" => $"{arg}:a:{track}",
            _ => arg,
        });

    /// <summary>
    /// A part of a video copied as it is (<c>-c copy</c>): done in a moment and nothing is lost, but a copy can only
    /// start at a key frame, so the clip starts at the last one before <see cref="ClipRange.Start"/> (up to a few
    /// seconds early). The first video stream and the first audio track are kept (none with
    /// <see cref="MediaOptions.RemoveAudio"/>), as in an exact or edited clip; the
    /// other options only apply to an exact clip, which is encoded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has no video stream.</exception>
    /// <exception cref="NotSupportedException">FFmpeg cannot write <paramref name="target"/>.</exception>
    internal static List<string> ClipCopy(string input, string output, string target, ClipRange clip, MediaOptions options, MediaInfo info)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        if (!VideoTargets.TryGetValue(target, out var spec))
            throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
        var audio = !options.RemoveAudio && info.Audio is not null;

        List<string> args = [.. Common, .. ClipInput(clip), "-i", input, "-map", "0:V:0"];
        if (audio)
            args.AddRange(["-map", "0:a:0"]);
        args.AddRange(["-c", "copy"]);
        // Apple plays HEVC in MP4/MOV only under the "hvc1" tag (FFmpeg keeps "hev1" from some sources).
        if (video.Codec == "hevc" && target is "mp4" or "m4v" or "mov")
            args.AddRange(["-tag:v", "hvc1"]);
        if (!audio)
            args.Add("-an");
        // The copied packets keep their timestamps from the key frame on: shifted so the clip starts at 0.
        args.AddRange(["-avoid_negative_ts", "make_zero"]);
        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// Input options that read only <paramref name="clip"/> of the file: <c>-ss</c> and <c>-to</c> before <c>-i</c>,
    /// both positions in the source.
    /// </summary>
    internal static string[] ClipInput(ClipRange clip) => ["-ss", ClipRange.Ffmpeg(clip.Start), "-to", ClipRange.Ffmpeg(clip.End)];

    /// <summary>
    /// One frame at <paramref name="seconds"/> as a JPEG at most <paramref name="maxHeight"/> pixels high (square
    /// pixels, turned upright): the timeline and the large picture of the clip window.
    /// </summary>
    internal static List<string> FrameAt(string input, double seconds, string output, int maxHeight, VideoStreamInfo? video) =>
    [
        .. Common, "-ss", ClipRange.Ffmpeg(seconds), "-i", input, "-map", "0:V:0",
        "-vf", (video is null ? "" : SquarePixels(video)) + $"scale=w=-2:h='trunc(min(ih,{Math.Max(16, maxHeight)})/2)*2'",
        "-frames:v", "1", "-update", "1", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2", output,
    ];

    /// <summary>
    /// Codecs a container takes as they are. A video whose streams are listed for the target is only rewrapped
    /// (stream copy): no quality loss, HDR and 10-bit kept, seconds instead of minutes. iPhone videos (HEVC or H.264
    /// with AAC in MOV) become MP4 this way. Only codecs the target's usual players handle are listed: MP4/MOV keep to
    /// what Apple devices and browsers play, WebM to what the WebM specification allows.
    /// </summary>
    internal static readonly Dictionary<string, (HashSet<string> Video, HashSet<string> Audio)> Remuxable = new()
    {
        ["mp4"] = (["h264", "hevc"], ["aac", "alac", "ac3", "eac3", "mp3"]),
        ["m4v"] = (["h264", "hevc"], ["aac", "alac", "ac3", "eac3", "mp3"]),
        ["mov"] = (["h264", "hevc", "prores"], ["aac", "alac", "ac3", "eac3", "mp3", "pcm_s16le", "pcm_s24le"]),
        ["mkv"] = (["h264", "hevc", "vp9", "av1"], ["aac", "alac", "ac3", "eac3", "mp3", "opus", "vorbis", "flac"]),
        ["webm"] = (["vp8", "vp9", "av1"], ["opus", "vorbis"]),
        ["ts"] = (["h264", "hevc"], ["aac", "ac3", "eac3", "mp3"]),
        ["m2ts"] = (["h264", "hevc"], ["aac", "ac3", "eac3"]),
    };

    /// <summary>Pixel formats players decode in hardware: 4:2:0 at 8 or 10 bits (phones record HDR video as 10-bit).</summary>
    private static readonly HashSet<string> PlayablePixelFormats = ["yuv420p", "yuvj420p", "yuv420p10le"];

    /// <summary>
    /// True when the video stream can be copied: the target container takes its codec in a pixel format players
    /// decode, it needs no scaling down, and the preset does not ask for a small file (Small always re-encodes; High
    /// and Balanced only describe how a video that has to be encoded anyway is encoded). A preset that asks for HEVC
    /// or AV1 copies only a video in that codec (an AV1 video goes into MP4 then too).
    /// </summary>
    internal static bool CanCopyVideo(string target, VideoStreamInfo video, MediaOptions options)
    {
        if (options.Quality == MediaQuality.Small || options.MaxHeight > 0 && video.DisplayHeight > Math.Max(16, options.MaxHeight)
            || video.Codec is not { } codec || !Remuxable.TryGetValue(target, out var codecs))
            return false;
        var takes = MediaCodecs.Effective(target, options.VideoCodec) switch
        {
            VideoCodec.Hevc => codec == "hevc",
            VideoCodec.Av1 => codec == "av1",
            _ => codecs.Video.Contains(codec),
        };
        return takes && (codec == "prores" || video.PixelFormat is { } format && PlayablePixelFormats.Contains(format));
    }

    /// <summary>
    /// The encoder for a video that has to be encoded: the preset's codec where the target takes it (x265 for HEVC;
    /// SVT-AV1 for AV1, libaom where an FFmpeg lacks SVT-AV1), else the target's usual one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The FFmpeg in use has no encoder for the chosen codec.</exception>
    internal static string VideoEncoder(string target, string defaultEncoder, VideoCodec codec, IReadOnlySet<string>? encoders)
    {
        bool Has(string encoder) => encoders is null || encoders.Contains(encoder);
        switch (MediaCodecs.Effective(target, codec))
        {
            case VideoCodec.Hevc when Has("libx265"):
                return "libx265";
            case VideoCodec.Hevc:
                throw new InvalidOperationException("This FFmpeg has no HEVC encoder (libx265). Filee's FFmpeg download has one.");
            case VideoCodec.Av1 when Has("libsvtav1"):
                return "libsvtav1";
            case VideoCodec.Av1 when Has("libaom-av1"):
                return "libaom-av1";
            case VideoCodec.Av1:
                throw new InvalidOperationException("This FFmpeg has no AV1 encoder (libsvtav1 or libaom-av1). Filee's FFmpeg download has one.");
            default:
                return defaultEncoder;
        }
    }

    /// <summary>
    /// True when the audio stream can be copied: the container takes it, no bitrate is set and the channels stay
    /// as they are.
    /// </summary>
    internal static bool CanCopyAudio(string target, AudioStreamInfo audio, MediaOptions options) =>
        options.Quality != MediaQuality.Small && options.AudioBitrateKbps <= 0
        && (options.AudioChannels is not (1 or 2) || options.AudioChannels == audio.Channels)
        && Remuxable.TryGetValue(target, out var codecs) && audio.Codec is { } codec && codecs.Audio.Contains(codec);

    /// <summary>
    /// Scales down to <paramref name="maxHeight"/> (never up) keeping the aspect ratio, and makes both sides even,
    /// which 4:2:0 encoders require (an odd-sized GIF or phone video would otherwise fail).
    /// </summary>
    internal static string ScaleFilter(int maxHeight) => maxHeight > 0
        ? $"scale=w=-2:h='trunc(min(ih,{Math.Max(16, maxHeight)})/2)*2'"
        : "scale=w='trunc(iw/2)*2':h='trunc(ih/2)*2'";

    /// <summary>
    /// DVD and DV frames are always 720×480 (NTSC) or 720×576 (PAL), shown at 4:3 or 16:9 through the pixel aspect.
    /// The picture is fitted into whichever of the two is closer to its own shape, with black bars for the rest.
    /// </summary>
    internal static string DvdOrDvFilter(VideoStreamInfo video, bool pal)
    {
        var height = pal ? 576 : 480;
        var aspect = video.DisplayAspect;
        var wide = aspect >= 1.55; // between 4:3 (1.33) and 16:9 (1.78)
        var frameAspect = wide ? 16 / 9.0 : 4 / 3.0;
        var (w, h) = aspect > frameAspect
            ? (720, Even(height * frameAspect / aspect))
            : (Even(720 * aspect / frameAspect), height);
        return $"scale={w}:{h},pad=720:{height}:(ow-iw)/2:(oh-ih)/2:black,setdar={(wide ? "16/9" : "4/3")}";
    }

    /// <summary>PAL (25 fps) for 25/50 fps sources, NTSC (29.97 fps) for everything else.</summary>
    internal static bool IsPal(double fps) => Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 1 || Math.Abs(fps - 12.5) < 0.3;

    internal static IEnumerable<string> VideoCodecArguments(string target, string codec, MediaQuality quality, VideoStreamInfo video, bool pal)
    {
        // Constant quantizer for the MPEG-style codecs: 2 is the best useful value, 4 good, 7 visibly compressed.
        var quantizer = Text(Choose(quality, 2, 4, 7));
        switch (codec)
        {
            case "libx264":
                // CRF 18 is visually lossless, 23 is x264's default, 28 is clearly smaller. Preset medium is x264's
                // default speed/size balance; yuv420p (8-bit 4:2:0) is the only format every H.264 decoder plays.
                return ["-c:v", "libx264", "-preset", "medium", "-crf", Text(Choose(quality, 18, 23, 28)), "-pix_fmt", "yuv420p"];
            case "libx265":
                // x265's CRF 28 looks like x264's 23 at about half the size; 22 / 26 / 30 keep High clean and Small
                // clearly smaller than H.264 Small. Apple plays HEVC in MP4 / MOV only under the "hvc1" tag.
                return ["-c:v", "libx265", "-preset", "medium", "-crf", Text(Choose(quality, 22, 26, 30)),
                        .. HighBitDepthArguments(video), "-x265-params", "log-level=error",
                        .. target is "mp4" or "m4v" or "mov" ? ["-tag:v", "hvc1"] : Array.Empty<string>()];
            case "libsvtav1":
                // SVT-AV1 preset 8: fast enough on a desktop, a little larger than the slow presets.
                return ["-c:v", "libsvtav1", "-preset", "8", "-crf", Text(Choose(quality, 26, 32, 40)), .. HighBitDepthArguments(video)];
            case "libaom-av1":
                // Constant quality (-b:v 0 + CRF); cpu-used 6 with row multithreading and tiles keeps libaom bearable.
                return ["-c:v", "libaom-av1", "-b:v", "0", "-crf", Text(Choose(quality, 26, 32, 40)), "-cpu-used", "6", "-row-mt", "1",
                        "-tiles", "2x2", .. HighBitDepthArguments(video)];
            case "libvpx-vp9":
                // Constant quality mode (-b:v 0 + CRF). Row multithreading and speed 4 keep VP9 usable on a desktop:
                // the slower speeds gain little size for several times the encoding time.
                return ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", Text(Choose(quality, 24, 32, 38)), "-row-mt", "1",
                        "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"];
            case "mpeg4":
                // GOP 250 because the encoder's default of 12 bloats files. The frame rate is fixed because MPEG-4
                // part 2 cannot store the fine time bases of phone videos.
                return ["-c:v", "mpeg4", "-tag:v", "XVID", "-q:v", quantizer, "-g", "250", "-bf", "2",
                        "-pix_fmt", "yuv420p", .. FrameRate(NearestRate(video.FrameRate))];
            case "wmv2":
                return ["-c:v", "wmv2", "-q:v", quantizer, "-g", "250", "-pix_fmt", "yuv420p",
                        .. FrameRate(NearestRate(video.FrameRate))];
            case "libtheora":
                // Theora quality 0-10.
                return ["-c:v", "libtheora", "-q:v", Text(Choose(quality, 8, 6, 4)), "-pix_fmt", "yuv420p",
                        .. FrameRate(NearestRate(video.FrameRate))];
            case "mpeg2video" when target == "vob":
                // DVD-Video limits: peak 9.8 Mbit/s for video + audio, 1.8 Mbit VBV buffer, GOP of 15 (PAL) / 18 (NTSC).
                return ["-c:v", "mpeg2video", "-b:v", Text(Choose(quality, 8000, 6000, 4000)) + "k", "-maxrate", "9000k",
                        "-minrate", "0", "-bufsize", "1835k", "-g", pal ? "15" : "18", "-bf", "2", "-pix_fmt", "yuv420p",
                        "-r", pal ? "25" : "30000/1001"];
            case "mpeg2video":
                // mpg and mxf: constant quantizer, broadcast GOP of 15 and a frame rate MPEG-2 can signal.
                return ["-c:v", "mpeg2video", "-q:v", quantizer, "-g", "15", "-bf", "2", "-pix_fmt", "yuv420p",
                        "-r", BroadcastRate(video.FrameRate)];
            case "dvvideo":
                // DV is fixed at 25 Mbit/s: NTSC samples colour 4:1:1, PAL 4:2:0.
                return ["-c:v", "dvvideo", "-pix_fmt", pal ? "yuv420p" : "yuv411p", "-r", pal ? "25" : "30000/1001"];
            default:
                throw new NotSupportedException(codec);
        }
    }

    /// <summary>
    /// HEVC and AV1 keep 10 bits for 10-bit and HDR sources (Main 10; 8 bits would band and break HDR) and 8-bit 4:2:0
    /// for everything else, which every player decodes. HDR colour tags (BT.2020 primaries, PQ or HLG transfer) are
    /// written as the source has them.
    /// </summary>
    internal static string[] HighBitDepthArguments(VideoStreamInfo video)
    {
        var tenBit = video.IsHighBitDepth || video.IsHdr;
        return ["-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p", .. tenBit ? ColorArguments(video) : []];
    }

    /// <summary>The source's colour tags (primaries, transfer, matrix), for 10-bit and HDR encodes.</summary>
    internal static List<string> ColorArguments(VideoStreamInfo video)
    {
        List<string> args = [];
        if (video.Color is { } color)
        {
            if (color.Primaries is { } primaries)
                args.AddRange(["-color_primaries", primaries]);
            if (color.Transfer is { } transfer)
                args.AddRange(["-color_trc", transfer]);
            if (color.Matrix is { } matrix)
                args.AddRange(["-colorspace", matrix]);
        }
        return args;
    }

    /// <summary><c>-r</c> for a rate, nothing when the source rate is unknown (FFmpeg then keeps its own guess).</summary>
    private static string[] FrameRate(string? rate) => rate is null ? [] : ["-r", rate];

    /// <summary>
    /// The source rate as a simple fraction: exact NTSC/film/PAL rates are kept, anything else (variable frame rate
    /// phone videos report averages like 29.87) is rounded to whole frames per second.
    /// </summary>
    internal static string? NearestRate(double fps)
    {
        if (fps <= 0)
            return null;
        foreach (var (num, den) in BroadcastRates)
        {
            if (Math.Abs(fps - (double)num / den) < 0.01)
                return den == 1 ? num.ToString(CultureInfo.InvariantCulture) : $"{num}/{den}";
        }
        return Math.Clamp((int)Math.Round(fps), 1, 120).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The broadcast rate closest to <paramref name="fps"/> (PAL's 25 when unknown).</summary>
    internal static string BroadcastRate(double fps)
    {
        if (fps <= 0)
            return "25";
        var (num, den) = BroadcastRates.MinBy(r => Math.Abs(fps - (double)r.Num / r.Den));
        return den == 1 ? num.ToString(CultureInfo.InvariantCulture) : $"{num}/{den}";
    }

    internal static IEnumerable<string> ContainerArguments(string target) => target switch
    {
        // Moves the index to the front so playback (and web streaming) can start before the file is loaded.
        "mp4" or "m4v" or "mov" or "3gp" or "3g2" or "m4a" or "m4b" => ["-movflags", "+faststart"],
        // Blu-ray / AVCHD transport stream: 192-byte packets with timestamps.
        "m2ts" => ["-mpegts_m2ts_mode", "1"],
        // DVD program stream: 10.08 Mbit/s mux rate and 2 KB sectors.
        "vob" => ["-muxrate", "10080000", "-packetsize", "2048"],
        // ID3v2.3 is what Windows Explorer and most car stereos read (FFmpeg writes 2.4 by default).
        "mp3" => ["-id3v2_version", "3"],
        // WAV is limited to 4 GB; long recordings switch to RF64 automatically.
        "wav" => ["-rf64", "auto"],
        _ => [],
    };

    internal static IEnumerable<string> MetadataArguments(int from = 0)
    {
        // Keep title, artist, dates, … (chapters are kept by default); an empty value removes a tag.
        yield return "-map_metadata";
        yield return from.ToString(CultureInfo.InvariantCulture);
        foreach (var tag in SourceOnlyTags)
        {
            yield return "-metadata";
            yield return tag + "=";
        }
    }

    // ───────────────────────── Audio ─────────────────────────

    private static List<string> Audio(string input, string output, string target, MediaTarget spec, MediaOptions options, MediaInfo info,
        CoverExtras? extras)
    {
        var audio = info.Audio ?? throw new InvalidOperationException("The file has no audio track.");
        List<string> args = [.. Common, "-i", input];
        // Ogg: the tags, and the picture as a comment, come from a metadata file (input 1) instead of the source.
        if (extras?.MetadataFile is { } metadata)
            args.AddRange(["-i", metadata]);
        args.AddRange(["-map", "0:a:0"]);
        if (info.CoverStreamIndex is { } cover && CoverTargets.Contains(target))
            args.AddRange(["-map", $"0:{cover}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        if (extras is { AttachmentFile: { } picture, AttachmentMime: { } mime })
            args.AddRange(["-attach", picture, "-metadata:s:t", "mimetype=" + mime, "-metadata:s:t", "filename=" + Path.GetFileName(picture)]);
        if (CanCopyIntoAudio(target, audio, options))
        {
            // "Extract audio": the phone video's AAC goes into the M4A as it is, without a second encode.
            args.AddRange(["-c:a", "copy"]);
        }
        else
        {
            var codec = AudioEncoder(target, spec.AudioCodec, options);
            args.AddRange(AudioCodecArguments(target, codec, options, audio, inVideo: false));
            args.AddRange(ChannelAndRateArguments(target, codec, options, audio.SampleRate));
        }
        args.AddRange(MetadataArguments(extras?.MetadataFile is null ? 0 : 1));
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>Audio codecs an audio target takes as they are (<see cref="MediaOptions.CopyAudioWhenPossible"/>).</summary>
    private static readonly Dictionary<string, HashSet<string>> CopyableAudio = new()
    {
        ["m4a"] = ["aac", "alac"],
        ["m4b"] = ["aac", "alac"],
        ["aac"] = ["aac"],
        ["mp3"] = ["mp3"],
        ["flac"] = ["flac"],
        ["opus"] = ["opus"],
        ["ogg"] = ["vorbis"],
    };

    /// <summary>
    /// True when the preset copies sound it can (<see cref="MediaOptions.CopyAudioWhenPossible"/>) and this sound can be
    /// copied into <paramref name="target"/>: the target takes its codec (only ALAC when the preset asks for Apple
    /// Lossless), and no bitrate, channel count or Small quality asks for an encode.
    /// </summary>
    internal static bool CanCopyIntoAudio(string target, AudioStreamInfo audio, MediaOptions options)
    {
        if (!options.CopyAudioWhenPossible || options.Quality == MediaQuality.Small || options.AudioBitrateKbps > 0
            || options.AudioChannels is 1 or 2 && options.AudioChannels != audio.Channels
            || audio.Codec is not { } codec || !CopyableAudio.TryGetValue(target, out var codecs) || !codecs.Contains(codec))
            return false;
        return MediaCodecs.Effective(target, options.AudioCodec) != Core.Presets.AudioCodec.Alac || codec == "alac";
    }

    // ───────────────────────── Without metadata ─────────────────────────

    /// <summary>
    /// "Remove metadata": the file's video, audio and subtitle streams copied as they are into the same format, with no
    /// tags (title, artist, dates, location, camera, encoder), per-stream tags or chapters. Data tracks (an iPhone's
    /// timed location and camera metadata) are left out; album art stays.
    /// </summary>
    public static List<string> WithoutMetadata(string input, string output, string target, MediaInfo info)
    {
        var spec = VideoTargets.TryGetValue(target, out var video) ? video
            : AudioTargets.TryGetValue(target, out var audio) ? audio
            : throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
        List<string> args = [.. Common, "-i", input];
        // A video's pictures without cover art (0:V); an audio file's cover art (0:v) stays with it.
        args.AddRange(["-map", info.Video is not null ? "0:V?" : "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy"]);
        if (info.Video?.Codec == "hevc" && target is "mp4" or "m4v" or "mov")
            args.AddRange(["-tag:v", "hvc1"]);
        args.AddRange(["-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1", "-fflags", "+bitexact"]);
        // Attachments are an MKV's subtitle fonts (without them styled subtitles fall back to another font); each needs
        // its own file name and type tags. Only when there are any: FFmpeg refuses a specifier that matches nothing.
        if (info.HasAttachments)
            args.AddRange(["-map", "0:t", "-map_metadata:s:t", "0:s:t"]);
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// The audio encoder of a target: the PCM format the preset picks for WAV and W64, ALAC for M4A / M4B when the
    /// preset asks for Apple Lossless, else the target's usual one.
    /// </summary>
    internal static string AudioEncoder(string target, string defaultEncoder, MediaOptions options) =>
        target is "wav" or "w64" ? WavCodec(options.WavFormat)
        : MediaCodecs.Effective(target, options.AudioCodec) == Core.Presets.AudioCodec.Alac ? "alac"
        : defaultEncoder;

    /// <summary>The PCM codec of a WAV sample format (little-endian, 8-bit is unsigned as WAV defines it).</summary>
    internal static string WavCodec(WavSampleFormat format) => format switch
    {
        WavSampleFormat.Pcm8 => "pcm_u8",
        WavSampleFormat.Pcm24 => "pcm_s24le",
        WavSampleFormat.Pcm32 => "pcm_s32le",
        WavSampleFormat.Float32 => "pcm_f32le",
        _ => "pcm_s16le",
    };

    /// <summary>
    /// Mono or stereo for any audio target (after the codec's own limits, so the user's choice wins), and the sample
    /// rate for WAV and W64, whose PCM takes any rate. Codecs with a fixed layout (AMR is mono, 8 kHz) are left alone.
    /// </summary>
    /// <param name="sourceSampleRate">
    /// The source's rate. DSD (DSF, DFF) decodes at 352.8 kHz or more: lossless and PCM targets get 88.2 kHz (the
    /// usual rate for DSD as PCM), Vorbis and WMA 44.1 kHz; the other lossy encoders pick a rate they support.
    /// </param>
    internal static IEnumerable<string> ChannelAndRateArguments(string target, string codec, MediaOptions options, int sourceSampleRate = 0)
    {
        if (options.AudioChannels is 1 or 2 && codec != "libopencore_amrnb")
        {
            yield return "-ac";
            yield return options.AudioChannels.ToString(CultureInfo.InvariantCulture);
        }
        if (target is "wav" or "w64" && options.AudioSampleRate is >= 8000 and <= 192000)
        {
            yield return "-ar";
            yield return options.AudioSampleRate.ToString(CultureInfo.InvariantCulture);
        }
        else if (sourceSampleRate > 192000 && (LosslessEncoders.Contains(codec) || codec.StartsWith("pcm_", StringComparison.Ordinal)))
        {
            yield return "-ar";
            yield return "88200";
        }
        else if (sourceSampleRate > 192000 && codec is "libvorbis" or "wmav2")
        {
            yield return "-ar";
            yield return "44100";
        }
    }

    private static readonly HashSet<string> LosslessEncoders = ["flac", "alac", "wavpack", "tta"];

    /// <summary>
    /// Encoder options for one audio codec. <see cref="MediaOptions.AudioBitrateKbps"/> wins when set (clamped to
    /// what the codec accepts); otherwise the quality picks a bitrate or VBR level. Lossless and PCM codecs ignore
    /// both. Channel counts the codec cannot store are mixed down.
    /// </summary>
    private static List<string> AudioCodecArguments(string target, string codec, MediaOptions options, AudioStreamInfo audio, bool inVideo)
    {
        var quality = options.Quality;
        var requested = options.AudioBitrateKbps;
        var channels = audio.Channels > 0 ? audio.Channels : 2;
        List<string> args = ["-c:a", codec];

        switch (codec)
        {
            case "aac":
                // Per stereo pair: 256 kbit/s is transparent, 192 excellent, 128 fine for speech and small files.
                args.AddRange(Bitrate(requested > 0 ? requested : Scaled(Choose(quality, 256, 192, 128), channels), 16, 256 * channels));
                break;
            case "libopus":
                // Opus is transparent at about 128 kbit/s for stereo; 64 is still good for music.
                args.AddRange(Bitrate(requested > 0 ? requested : Scaled(Choose(quality, 160, 128, 64), channels), 6, 256 * channels));
                break;
            case "libmp3lame" when requested > 0 || inVideo:
                // Constant bitrate (variable-bitrate MP3 confuses AVI players' audio sync) from the MP3 bitrate table.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : Choose(quality, 256, 192, 128), Mp3Bitrates), 32, 320));
                break;
            case "libmp3lame":
                // LAME's VBR presets: V0 ≈ 245, V2 ≈ 190, V5 ≈ 130 kbit/s.
                args.AddRange(["-q:a", Text(Choose(quality, 0, 2, 5))]);
                break;
            case "libvorbis" when requested > 0:
                args.AddRange(Bitrate(requested, 32, 500));
                break;
            case "libvorbis":
                // Vorbis quality 6 ≈ 192, 4 ≈ 128, 2 ≈ 96 kbit/s.
                args.AddRange(["-q:a", Text(Choose(quality, 6, 4, 2))]);
                break;
            case "wmav2":
                // WMA 2 stores at most two channels.
                args.AddRange(Bitrate(requested > 0 ? requested : Choose(quality, 192, 128, 96), 32, 320));
                if (channels > 2)
                    args.AddRange(["-ac", "2"]);
                break;
            case "mp2":
                // Layer II allows only the bitrates of its table, and those only at 32 kHz or more.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : Choose(quality, 320, 224, 160), Mp2Bitrates), 32, 384));
                if (audio.SampleRate is > 0 and < 32000)
                    args.AddRange(["-ar", "32000"]);
                break;
            case "ac3" when target == "vob":
                // DVD audio is 48 kHz; 448 kbit/s is the DVD maximum for 5.1, 192 the usual stereo rate.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : channels > 2 ? 448 : 192, Ac3Bitrates), 64, 448));
                args.AddRange(["-ar", "48000"]);
                break;
            case "ac3":
                // Without a bitrate the encoder picks the standard rate for the channel layout (192k stereo, 448k 5.1).
                if (requested > 0)
                    args.AddRange(Bitrate(Snap(requested, Ac3Bitrates), 32, 640));
                break;
            case "eac3":
                // E-AC-3 takes any bitrate; per stereo pair 256 / 192 / 128 kbit/s like AAC.
                args.AddRange(Bitrate(requested > 0 ? requested : Scaled(Choose(quality, 256, 192, 128), channels), 32, 6144));
                break;
            case "libopencore_amrnb":
                // AMR-NB is defined for 8 kHz mono only; 12.2 kbit/s is its best mode.
                args.AddRange(["-ar", "8000", "-ac", "1", "-b:a", "12.2k"]);
                break;
            case "pcm_s16le" when target is "dv":
                args.AddRange(["-ar", "48000", "-ac", "2"]);
                break;
            case "pcm_s16le" when target is "mxf":
                // MXF (SMPTE 377) carries 48 kHz audio only.
                args.AddRange(["-ar", "48000"]);
                break;
            case "pcm_s16le" when target is "voc" && channels > 2:
                args.AddRange(["-ac", "2"]);
                break;
        }
        return args;
    }

    /// <summary>Bitrate for a stereo pair scaled to the channel count (mono gets half, 5.1 three times as much).</summary>
    private static int Scaled(int stereoKbps, int channels) => stereoKbps * Math.Clamp(channels, 1, 8) / 2;

    private static string[] Bitrate(int kbps, int min, int max) => ["-b:a", Text(Math.Clamp(kbps, min, Math.Max(min, max))) + "k"];

    /// <summary>The largest allowed bitrate not above <paramref name="kbps"/> (the smallest one if all are above).</summary>
    internal static int Snap(int kbps, int[] allowed) => allowed.Where(a => a <= kbps).DefaultIfEmpty(allowed[0]).Max();

    private static readonly int[] Mp3Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mp2Bitrates = [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Ac3Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    // ───────────────────────── Subtitles ─────────────────────────

    /// <summary>
    /// The first text subtitle track of a video as SRT, ASS or WebVTT (UTF-8). A track already in the target's format is
    /// copied as it is (an ASS track keeps its styles); others are converted by FFmpeg.
    /// </summary>
    /// <exception cref="InvalidOperationException">The video has no subtitle track, or only picture subtitles.</exception>
    private static List<string> Subtitles(string input, string output, string target, MediaInfo info)
    {
        var track = info.Subtitles.FirstOrDefault(s => s.Codec is { } codec && TextSubtitleCodecs.Contains(codec))
            ?? throw new InvalidOperationException(info.Subtitles.Count == 0
                ? "The video has no subtitle track."
                : "The video's subtitles are pictures (Blu-ray PGS, DVD or DVB subtitles), not text. Picture subtitles can't be turned into text without OCR.");
        var (muxer, encoder) = SubtitleTargets[target];
        var same = (target, track.Codec) is ("srt", "subrip" or "srt") or ("ass", "ass") or ("vtt", "webvtt");
        return [.. Common, "-i", input, "-map", $"0:{track.Index}", "-c:s", same ? "copy" : encoder, "-f", muxer, "-progress", "pipe:1", output];
    }

    // ───────────────────────── Animated GIF ─────────────────────────

    /// <summary>
    /// Animated GIF in two passes: the first finds the 256 colours that suit the whole clip best (palettegen), the
    /// second maps every frame to them (paletteuse). A single pass with a fixed palette looks banded; generating
    /// the palette in the same run would keep every frame in memory until the end. Frame rate and width are limited
    /// by the quality setting because GIF stores every frame uncompressed-ish and grows fast.
    /// </summary>
    private static List<FfmpegPass> AnimatedGif(string input, string output, MediaOptions options, MediaInfo info, string workDirectory, double speed)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var quality = options.Quality;
        var chain = GifFrames(video, options.MaxHeight, fps: Choose(quality, 15, 12, 10), maxWidth: Choose(quality, 640, 480, 320), speed);
        var palette = Path.Combine(workDirectory, $"palette-{Guid.NewGuid():N}.png");
        var colors = Choose(quality, 256, 256, 128);
        // Error diffusion looks best; ordered (Bayer) dithering compresses much better because it does not flicker.
        var dither = Choose(quality, "sierra2_4a", "bayer:bayer_scale=4", "bayer:bayer_scale=5");

        // Pass 1 reports no output time until the palette is written at the very end, so a second, discarded copy
        // of the frames goes to a null output whose per-frame stats (frame_time=<seconds>) serve as progress.
        List<string> first =
        [
            .. Common, "-i", input,
            "-filter_complex", $"[0:V:0]{chain},split[a][b];[a]palettegen=max_colors={colors}[palette]",
            "-map", "[palette]", "-frames:v", "1", "-update", "1", "-f", "image2", palette,
            "-map", "[b]", "-stats_enc_pre", "pipe:1", "-stats_enc_pre_fmt", FfmpegRunner.FrameTimeKey + "={t}", "-f", "null", "-",
        ];
        List<string> second =
        [
            .. Common, "-i", input, "-i", palette,
            "-filter_complex", $"[0:V:0]{chain}[frames];[frames][1:v]paletteuse=dither={dither}:diff_mode=rectangle[gif]",
            "-map", "[gif]", "-loop", "0", "-f", "gif", "-progress", "pipe:1", output,
        ];
        return [new FfmpegPass(first, 0.4), new FfmpegPass(second, 0.6)];
    }

    /// <summary>
    /// Animated PNG with FFmpeg's APNG encoder and muxer: full colour (24-bit RGB, no palette pass), looping, with the
    /// frame rate and width limits of GIF, since every frame is stored losslessly.
    /// </summary>
    private static List<string> AnimatedPng(string input, string output, MediaOptions options, MediaInfo info, double speed)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var quality = options.Quality;
        var chain = GifFrames(video, options.MaxHeight, fps: Choose(quality, 15, 12, 10), maxWidth: Choose(quality, 640, 480, 320), speed);
        return
        [
            .. Common, "-i", input, "-map", "0:V:0", "-vf", chain, "-an",
            "-c:v", "apng", "-pix_fmt", "rgb24", "-pred", "mixed", "-plays", "0", "-f", "apng", "-progress", "pipe:1", output,
        ];
    }

    /// <summary>
    /// Playback speed, frame rate, square pixels and size of the GIF frames (never upscaled). The speed changes the
    /// timestamps before the frame rate is taken, so a faster clip keeps the frame rate by leaving frames out, and a
    /// slower one repeats frames where the video has too few.
    /// </summary>
    private static string GifFrames(VideoStreamInfo video, int maxHeight, int fps, int maxWidth, double speed)
    {
        var height = maxHeight > 0 ? $"'min(ih,{Math.Max(16, maxHeight)})'" : "ih";
        var timing = double.IsFinite(speed) && speed is > 0 and not 1
            ? $"setpts=PTS/{speed.ToString("0.###", CultureInfo.InvariantCulture)},"
            : "";
        return $"{timing}fps={fps},{SquarePixels(video)}scale=w='min(iw,{maxWidth})':h={height}:force_original_aspect_ratio=decrease:flags=lanczos";
    }

    /// <summary>Images have no pixel aspect ratio: anamorphic video (DV, DVD) is stretched to square pixels first.</summary>
    internal static string SquarePixels(VideoStreamInfo video) =>
        Math.Abs(video.SampleAspect - 1) > 0.01 ? "scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1," : "";

    // ───────────────────────── Still frame ─────────────────────────

    /// <summary>
    /// One full-size PNG frame from a tenth into the video (the first frames are often black), the start for video →
    /// image conversions (the image engines take it from there). Without this edge the route planner would reach
    /// images through an animated GIF of the whole video.
    /// </summary>
    private static List<string> PosterFrame(string input, string output, MediaInfo info)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var seconds = (info.Duration?.TotalSeconds ?? 0) / 10;
        return
        [
            .. Common, "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input, "-map", "0:V:0",
            "-vf", SquarePixels(video) + "null", "-frames:v", "1", "-update", "1", "-c:v", "png", "-f", "image2",
            "-progress", "pipe:1", output,
        ];
    }

    // ───────────────────────── Still picture → video ─────────────────────────

    /// <summary>
    /// A video that shows the PNG <paramref name="input"/> for <see cref="StillDuration"/>. The image engines write
    /// that PNG at full size and colour, so pictures never pass through a 256-colour GIF on their way to video.
    /// </summary>
    /// <remarks>
    /// The PNG's ICC profile or colour chunks give the primaries and transfer (a Display P3 photo stays P3 in the
    /// video); RGB becomes BT.709 YUV in TV range, tagged as such. Transparent areas turn black, like the bars around
    /// a video. 30 frames per second play everywhere; identical frames cost x264 (tuned for stills) and VP9 almost
    /// nothing, so the quality is one step above the preset's (Balanced encodes like High): the file is about the
    /// size of one picture either way.
    /// </remarks>
    public static IReadOnlyList<FfmpegPass> PlanStill(string input, string output, string target, MediaOptions options, MediaInfo info)
    {
        var picture = info.Video ?? throw new InvalidOperationException("The file has no picture.");
        if (!PictureToVideoTargets.Contains(target))
            throw new NotSupportedException($"FFmpeg does not turn pictures into '{target}'.");
        var spec = VideoTargets[target];
        var quality = options.Quality == MediaQuality.Small ? MediaQuality.Balanced : MediaQuality.High;

        List<string> args =
        [
            .. Common, "-flags2", "+icc_profiles", "-loop", "1", "-framerate", "30", "-i", input, "-map", "0:V:0",
            "-t", StillDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            "-vf", $"premultiply=inplace=1,{StillScale(picture, options.MaxHeight)}:out_color_matrix=bt709:out_range=tv,format=yuv420p",
            .. VideoCodecArguments(target, spec.VideoCodec!, quality, picture, pal: false),
        ];
        if (spec.VideoCodec == "libx264")
            args.AddRange(["-tune", "stillimage"]);
        args.AddRange(["-colorspace", "bt709", "-an", .. ContainerArguments(target), "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return [new FfmpegPass(args, 1)];
    }

    /// <summary>
    /// The picture's own size, made even (4:2:0) and scaled down only to <paramref name="maxHeight"/> or to what video
    /// players decode (<see cref="MaxVideoPixels"/>, <see cref="MaxVideoSide"/>), keeping the aspect ratio.
    /// </summary>
    internal static string StillScale(VideoStreamInfo picture, int maxHeight)
    {
        double width = picture.Width, height = picture.Height;
        if (width <= 0 || height <= 0)
            return ScaleFilter(maxHeight) + ":flags=lanczos";
        var factor = Math.Min(1, Math.Min(Math.Sqrt(MaxVideoPixels / (width * height)), MaxVideoSide / Math.Max(width, height)));
        if (maxHeight > 0)
            factor = Math.Min(factor, Math.Max(16, maxHeight) / height);
        return $"scale={EvenDown(width * factor)}:{EvenDown(height * factor)}:flags=lanczos";
    }

    // ───────────────────────── Helpers ─────────────────────────

    internal static T Choose<T>(MediaQuality quality, T high, T balanced, T small) => quality switch
    {
        MediaQuality.High => high,
        MediaQuality.Small => small,
        _ => balanced,
    };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    /// <summary>The even number at or below <paramref name="value"/> (a tiny rounding margin keeps 1080.0000001 at 1080).</summary>
    private static int EvenDown(double value) => Math.Max(2, (int)Math.Floor(value / 2 + 1e-6) * 2);
}
