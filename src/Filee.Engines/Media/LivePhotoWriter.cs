// Video → a Live / Motion Photo of a short clip: an iPhone pair (a JPG or HEIC and a MOV with the same name and content
// identifier) or an Android Motion Photo (one JPG with the clip appended). Everything is written in the work directory
// and moved into place at the end, the pair together.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Heic;
using ImageMagick;

namespace Filee.Engines.Media;

/// <summary>A clip of a video: its start and length in seconds, and the frame (at 30 fps) that becomes the photo.</summary>
internal readonly record struct LivePhotoClip(double Start, double Length, int CoverFrame);

internal static class LivePhotoWriter
{
    /// <summary>Frames per second of the clip: what phones record Live and Motion Photos at.</summary>
    public const int Fps = 30;

    /// <summary>The shorter side of the clip unless the preset asks for less: an iPhone's Live Photo video is 1440 × 1920.</summary>
    public const int ShortSide = 1440;

    /// <summary>
    /// HDR (PQ or HLG) to SDR BT.709: phones show a Live / Motion Photo's video as SDR next to an SDR photo. Needs
    /// FFmpeg's zscale filter (zimg).
    /// </summary>
    internal const string ToneMap =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,";

    public static async Task<IReadOnlyList<string>> ConvertAsync(string ffmpeg, string ffprobe, ConversionStep step,
        IReadOnlySet<string>? encoders, Func<string, bool> hasFilter, IProgress<double>? progress, CancellationToken ct)
    {
        var options = step.Preset.Media;
        var apple = options.LivePhoto == LivePhotoKind.Apple;
        var input = Path.GetFullPath(step.InputPath);
        var info = await MediaInfo.ProbeAsync(ffprobe, input, ct);
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var clip = Clip(options, info.Duration?.TotalSeconds);
        if (video.IsHdr && !hasFilter("zscale"))
            throw new ConversionRefusedException("error.live_photo_hdr");
        var toneMap = video.IsHdr ? ToneMap : "";
        progress?.Report(0.02);

        // The names first: a file the output rules skip costs no encoding.
        var photoExtension = step.To == "heic" ? "heic" : "jpg";
        var targets = apple
            ? AllocatePair(step.Output, photoExtension, input)
            : step.Output.Allocate("jpg", ".MP") is { } single ? [single] : null;
        if (targets is null)
            return [];

        var token = Guid.NewGuid().ToString("N");
        var movie = Path.Combine(step.WorkDirectory, token + (apple ? ".mov" : ".mp4"));
        var png = Path.Combine(step.WorkDirectory, token + ".png");
        var photo = Path.Combine(step.WorkDirectory, token + "." + photoExtension);

        var encoder = MediaEncoding.VideoEncoder("mov", "libx264", options.VideoCodec, encoders);
        var audio = options.RemoveAudio ? null : info.Audio;
        await FfmpegConverter.RunPassAsync(ffmpeg, new FfmpegPass(ClipArguments(input, movie, apple ? "mov" : "mp4", clip, video, audio,
            options, encoder, toneMap), 0.8), TimeSpan.FromSeconds(clip.Length), progress, 0, ct);

        // The frame of the photo, within the frames the clip really has (a video may end sooner than it says).
        uint frames;
        await using (var written = File.OpenRead(movie))
            frames = MovieBoxes.VideoSampleCount(MovieBoxes.ReadMovieHeader(written).Payload);
        if (frames == 0)
            throw new InvalidOperationException("FFmpeg wrote a clip without frames.");
        var cover = Math.Min(clip.CoverFrame, (int)frames - 1);
        await FfmpegConverter.RunPassAsync(ffmpeg, new FfmpegPass(CoverArguments(input, png, clip.Start, cover, video, toneMap), 0.1),
            null, progress, 0.8, ct);
        if (!File.Exists(png))
            throw new InvalidOperationException("FFmpeg did not write the photo's frame.");

        var quality = Math.Clamp(step.Preset.Image.Quality, 1, 100);
        var coverSeconds = (double)cover / Fps;
        if (apple)
        {
            var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.MakerNote, AppleLivePhoto.MakerNote(id));
            if (photoExtension == "heic")
            {
                var (width, height) = PictureSize(png);
                await File.WriteAllBytesAsync(photo, await HeicWriter.EncodeAsync(ffmpeg, png, width, height, null, exif.ToByteArray(),
                    quality, step.WorkDirectory, ct), ct);
            }
            else
                await File.WriteAllBytesAsync(photo, Jpeg(png, quality, exif), ct);
            AppleLivePhoto.TagMovie(movie, id, coverSeconds);
            Commit([(movie, targets[1]), (photo, targets[0])]);
            step.Notice?.Invoke("notice.live_photo_pair");
        }
        else
        {
            await MotionPhoto.WriteAsync(Jpeg(png, quality, null), movie, photo, (long)Math.Round(coverSeconds * 1_000_000), ct);
            Commit([(photo, targets[0])]);
        }
        progress?.Report(1);
        return targets;
    }

    /// <summary>
    /// The clip the preset asks for in a video of <paramref name="duration"/> seconds (null when unknown): the start,
    /// a length of <see cref="MediaOptions.MinLivePhotoLength"/> to <see cref="MediaOptions.MaxLivePhotoLength"/>
    /// seconds (shorter when the video ends sooner) and the photo's frame within it (its last frame at most).
    /// </summary>
    /// <exception cref="ConversionRefusedException"><c>error.live_photo_start</c>: the clip starts after the video ends.</exception>
    internal static LivePhotoClip Clip(MediaOptions options, double? duration)
    {
        static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        var start = Math.Max(0, Finite(options.LivePhotoStart, 0));
        var length = Math.Clamp(Finite(options.LivePhotoLength, 3), MediaOptions.MinLivePhotoLength, MediaOptions.MaxLivePhotoLength);
        if (duration is > 0 and var total)
        {
            // At least two frames, or there is nothing to move.
            if (start > 0 && total - start < 2.0 / Fps)
                throw new ConversionRefusedException("error.live_photo_start");
            length = Math.Min(length, total - start);
        }
        var lastFrame = Math.Max(0, (int)Math.Ceiling(length * Fps - 1e-6) - 1);
        var cover = Math.Max(0, Finite(options.LivePhotoCover, length / 2));
        return new(start, length, Math.Min((int)Math.Floor(cover * Fps + 1e-6), lastFrame));
    }

    /// <summary>
    /// The size of the clip: the picture as it is shown (pixel aspect and rotation applied) scaled down so that its
    /// shorter side is at most <paramref name="maxShortSide"/>, with even sides.
    /// </summary>
    internal static (int Width, int Height) ClipSize(VideoStreamInfo video, int maxShortSide)
    {
        var width = video.Width * (video.SampleAspect > 0 ? video.SampleAspect : 1);
        double height = video.Height;
        if (video.IsRotated)
            (width, height) = (height, width);
        var scale = Math.Min(1, maxShortSide / Math.Max(1, Math.Min(width, height)));
        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
        return (Even(width * scale), Even(height * scale));
    }

    /// <summary>
    /// The clip as a QuickTime movie (iPhone) or MP4 (Android): 30 fps, 8-bit 4:2:0 H.264 (HEVC when the preset asks,
    /// tagged hvc1 for Apple), AAC in stereo or mono. FFmpeg turns the picture upright, so the orientation is kept
    /// in the pixels. The movie header stays at the end, where <see cref="AppleLivePhoto.TagMovie"/> rewrites it.
    /// </summary>
    internal static List<string> ClipArguments(string input, string output, string muxer, LivePhotoClip clip, VideoStreamInfo video,
        AudioStreamInfo? audio, MediaOptions options, string encoder, string toneMap)
    {
        var (width, height) = ClipSize(video, options.MaxHeight > 0 ? Math.Min(options.MaxHeight, ShortSide) : ShortSide);
        List<string> args =
        [
            .. MediaEncoding.Common, "-ss", Seconds(clip.Start), "-i", input, "-map", "0:V:0",
            .. audio is null ? Array.Empty<string>() : ["-map", "0:a:0"],
            "-t", Seconds(clip.Length),
            "-vf", $"{toneMap}fps={Fps},scale={width}:{height},setsar=1,format=yuv420p",
        ];
        var crf = Choose(options.Quality, encoder == "libx265" ? [22, 26, 30] : [18, 23, 28]);
        args.AddRange(encoder == "libx265"
            ? ["-c:v", "libx265", "-preset", "medium", "-crf", crf, "-x265-params", "log-level=error", "-tag:v", "hvc1"]
            : ["-c:v", "libx264", "-preset", "medium", "-crf", crf, "-profile:v", "high"]);
        args.AddRange(["-pix_fmt", "yuv420p"]);
        if (toneMap.Length > 0)
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        if (audio is null)
            args.Add("-an");
        else
        {
            var bitrate = options.AudioBitrateKbps > 0 ? options.AudioBitrateKbps : options.Quality == MediaQuality.Small ? 96 : 160;
            args.AddRange(["-c:a", "aac", "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) + "k",
                "-ac", audio.Channels == 1 ? "1" : "2", "-ar", audio.SampleRate is 44100 or 48000 ? audio.SampleRate.ToString(CultureInfo.InvariantCulture) : "48000"]);
        }
        args.AddRange(["-sn", "-dn", "-map_chapters", "-1", "-map_metadata", "0", "-metadata", "major_brand=", "-metadata", "minor_version=",
            "-metadata", "compatible_brands=", "-max_muxing_queue_size", "4096", "-f", muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// The photo: frame <paramref name="frame"/> of the clip at the video's full size, picked by the same 30 fps
    /// timing as the clip so it is exactly the frame the still image time points at.
    /// </summary>
    internal static List<string> CoverArguments(string input, string output, double start, int frame, VideoStreamInfo video, string toneMap)
    {
        var square = Math.Abs(video.SampleAspect - 1) > 0.01 && video.SampleAspect > 0 ? "scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1," : "";
        return
        [
            .. MediaEncoding.Common, "-ss", Seconds(start), "-i", input, "-map", "0:V:0",
            "-vf", $"{toneMap}fps={Fps},select=eq(n\\,{frame.ToString(CultureInfo.InvariantCulture)}),{square}format=rgb24",
            "-frames:v", "1", "-fps_mode", "passthrough", "-update", "1", "-c:v", "png", "-f", "image2", "-progress", "pipe:1", output,
        ];
    }

    /// <summary>
    /// The paths of an iPhone pair: the photo where the output rules put it, and the MOV next to it with the same name.
    /// When that MOV is taken (another file, or the video being converted) both get the next free number, so the two
    /// always keep the same name. A MOV is only written over with the photo that is overwritten with it. Null when the
    /// rules skip the photo.
    /// </summary>
    internal static string[]? AllocatePair(IOutputAllocator output, string photoExtension, string input)
    {
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var photo = output.Allocate(photoExtension, attempt == 1 ? null : $" ({attempt})");
            if (photo is null)
                return null;
            var movie = Path.ChangeExtension(photo, "mov");
            if (SamePath(movie, input) || Directory.Exists(movie))
                continue;
            if (File.Exists(photo) || !File.Exists(movie))
                return [photo, movie];
        }
        throw new IOException("No free name for the Live Photo's photo and video.");
    }

    /// <summary>
    /// Moves the finished files from the work directory into place: first next to their targets under a hidden name
    /// (a move to another drive copies, which can fail without touching anything of the user's), then each renamed
    /// over its target, in the order given. Hidden copies a failure leaves are removed.
    /// </summary>
    internal static void Commit(IReadOnlyList<(string Source, string Target)> files)
    {
        var staged = new List<(string Staged, string Target)>();
        try
        {
            foreach (var (source, target) in files)
            {
                var stage = Path.Combine(Path.GetDirectoryName(target)!, $".filee-{Guid.NewGuid():N}{Path.GetExtension(target)}");
                staged.Add((stage, target));
                File.Move(source, stage);
            }
            foreach (var (stage, target) in staged)
                File.Move(stage, target, overwrite: true);
        }
        finally
        {
            foreach (var (stage, _) in staged)
                TryDelete(stage);
        }
    }

    private static byte[] Jpeg(string png, int quality, ExifProfile? exif)
    {
        using var image = new MagickImage(png);
        image.Quality = (uint)quality;
        if (exif is not null)
            image.SetProfile(exif);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private static (int Width, int Height) PictureSize(string png)
    {
        var info = new MagickImageInfo(png);
        return ((int)info.Width, (int)info.Height);
    }

    private static string Choose(MediaQuality quality, int[] values) =>
        values[quality switch { MediaQuality.High => 0, MediaQuality.Small => 2, _ => 1 }].ToString(CultureInfo.InvariantCulture);

    private static string Seconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
