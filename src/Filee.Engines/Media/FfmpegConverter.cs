// Video and audio conversion with FFmpeg (GPL-3.0 build, an optional download run as a separate program in
// engines/ffmpeg): video ↔ video (H.264, HEVC, AV1, …), video → animated GIF / APNG, a still frame or every frame
// (FrameSequenceWriter), audio extraction, subtitle extraction (the first text track as SRT / ASS / WebVTT), audio ↔
// audio and GIF, APNG or a still picture → video, and images → HEIC (FFmpeg's HEVC encoder, Filee's own container
// writer). Container and codec choices live in MediaEncoding, the process handling in FfmpegRunner, hardware encoders
// in HardwareVideo (options) and HardwareProbe (which one works here).
// Live / Motion Photos (Android, iPhone) give their video when a preset asks for it (LivePhotoSource, MotionPhoto), and
// a clip of a video becomes one (LivePhotoWriter). Clips and pictures are joined into one file by IMediaJoiner
// (MediaJoining), and "Remove metadata" copies a file's streams without its tags. "Cut a clip" copies or encodes a part
// of a video (CutClipAsync).

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Heic;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Media;

/// <summary>Converts video and audio files with Filee's own copy of FFmpeg.</summary>
/// <param name="logger">Receives which hardware encoders work and why a hardware encode was redone in software.</param>
public sealed partial class FfmpegConverter(ILogger? logger = null) : IConverter, IImageTransformer, IMediaJoiner
{
    private static readonly ConcurrentDictionary<string, string> Versions = new();

    /// <summary><c>ffmpeg -version</c> and <c>-encoders</c> answer at once; a copy that does not is broken or hangs.</summary>
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private (string Ffmpeg, string Ffprobe)? _tools;

    /// <summary>
    /// Picks the hardware encoder for (ffmpeg, codec, 10-bit, encoders) instead of <see cref="HardwareProbe.SelectAsync"/>.
    /// Tests put an encoder here that FFmpeg does not have, to see the software retry.
    /// </summary>
    internal Func<string, string, bool, IReadOnlySet<string>?, CancellationToken, Task<HardwareEncoder?>>? SelectHardware { get; init; }

    public string Id => "ffmpeg";
    public string DisplayName => "FFmpeg";

    /// <summary>FFmpeg's encoders use every core themselves; two runs keep the CPU busy between files.</summary>
    public int MaxParallelism => 2;

    /// <summary>Only HEIC, which ImageMagick reads and turns before x265 encodes it (Heic/HeicWriter); never video.</summary>
    public bool Transforms(string from, string to) => to == "heic" && FormatRegistry.FindById(from)?.Category != FormatCategory.Video;

    public IReadOnlyList<ConversionEdge> Edges { get; } = BuildEdges();

    /// <summary>
    /// Every video format (read by FFmpeg's demuxers) → every video target, animated GIF or APNG, a still PNG frame,
    /// every frame as PNG or JPG pictures (only when a preset asks for it, see <see cref="ConversionEdge.FrameSequence"/>),
    /// every audio target and SRT / ASS / WebVTT (its subtitles); every audio format → every audio target (including
    /// itself: re-encode with another bitrate); GIF, APNG and PNG → MP4, WEBM, MOV; every image ImageMagick reads →
    /// HEIC. Only formats the pinned build can write are targets (see MediaEncoding).
    /// </summary>
    /// <remarks>
    /// The GIF and APNG edges cost more (slow, large), so video → JPG / PDF / TIFF … goes through the still frame
    /// (video → PNG → image engines) and never renders an animation of a whole film just to take one frame of it.
    /// The other way, a photo (HEIC, JPG, …) reaches video as a full-colour PNG: the image engines' still → GIF and
    /// still → APNG edges cost 12 (<see cref="Magick.ImageEncoder.Edge"/>), so 10 + 11 beats 12 + 10 and 12 + 11.
    /// Animated GIF/WebP and PDF or TIFF pages keep the GIF route (10 + 10, APNG costs 10 + 11), which keeps their
    /// frames; an APNG goes straight to video.
    /// </remarks>
    private static List<ConversionEdge> BuildEdges()
    {
        var videos = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Video).Select(f => f.Id).ToList();
        var audios = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Audio).Select(f => f.Id);
        var videoTargets = MediaEncoding.VideoTargets.Keys.Append("png").ToList();
        return
        [
            .. from source in videos from target in videoTargets select new ConversionEdge(source, target),
            // A picture turned into a video has no sound: picture → video → audio would only make a silent file.
            .. from source in videos from target in MediaEncoding.AudioTargets.Keys
               select new ConversionEdge(source, target, NotForImageSources: true),
            // "Save every frame": a folder of numbered pictures, only when a preset asks for it (never in another route).
            .. from source in videos from target in MediaOptions.FrameTargets select new ConversionEdge(source, target, FrameSequence: true),
            // Subtitles out of a video. SRT is the hub to the other subtitle formats (the subtitle engine takes it on),
            // so ASS and WebVTT cost a little more and are only taken when they are the target.
            .. from source in videos from target in MediaEncoding.SubtitleTargets.Keys
               select new ConversionEdge(source, target, target == "srt" ? 10 : 11, NotForImageSources: true),
            .. videos.Select(source => new ConversionEdge(source, "gif", 15)),
            .. videos.Select(source => new ConversionEdge(source, "apng", 15)),
            .. from source in audios from target in MediaEncoding.AudioTargets.Keys select new ConversionEdge(source, target),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("gif", target)),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("apng", target, 11)),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("png", target, 11)),
            // HEIC: ImageMagick reads, x265 encodes, Filee writes the container (see Heic/HeicWriter).
            .. ImageEncoder.Sources.Select(source => new ConversionEdge(source, "heic")),
            // The video of a Live / Motion Photo, only when a preset asks for it (see ConversionEdge.MotionPhoto).
            .. from source in MediaOptions.MotionPhotoSources from target in MediaOptions.MotionPhotoTargets
               select new ConversionEdge(source, target, MotionPhoto: true),
            // A clip of a video as a Live / Motion Photo, only when a preset asks for it (see ConversionEdge.LivePhoto).
            .. from source in videos
               from target in MediaOptions.LivePhotoTargets(LivePhotoKind.Apple).Union(MediaOptions.LivePhotoTargets(LivePhotoKind.Android))
               select new ConversionEdge(source, target, LivePhoto: true),
        ];
    }

    public EngineStatus GetStatus()
    {
        _tools = Locate();
        if (_tools is not { } tools)
            return EngineStatus.Unavailable("engine.reason.not_installed");
        try
        {
            return EngineStatus.Available(tools.Ffmpeg, $"FFmpeg {VersionOf(tools.Ffmpeg)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Present but broken, e.g. a DLL next to ffmpeg.exe is missing.
            return EngineStatus.Unavailable("engine.reason.error", ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = _tools ?? Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        if (step.MotionPhoto)
            return await ExtractMotionAsync(step, progress, cancellationToken);
        if (step.LivePhoto)
            return await LivePhotoWriter.ConvertAsync(ffmpeg, ffprobe, step, EncodersOf(ffmpeg), filter => HasFilter(ffmpeg, filter),
                progress, cancellationToken);
        if (step.To == "heic")
            return await HeicWriter.ConvertAsync(ffmpeg, step, progress, cancellationToken);
        var input = Path.GetFullPath(step.InputPath);
        var info = await MediaInfo.ProbeAsync(ffprobe, input, cancellationToken);
        progress?.Report(0.02);

        if (step.FrameSequence)
            return await FrameSequenceWriter.ConvertAsync(ffmpeg, step, input, info, progress, cancellationToken);
        if (step.Preset.CutsClipOf(step.From) && MediaEncoding.VideoTargets.ContainsKey(step.To))
            return await CutClipAsync(ffmpeg, step, input, info, progress, cancellationToken);

        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];

        // A PNG is a still picture (from the image engines), shown for a few seconds.
        var still = step.From == "png";

        // Album art for targets FFmpeg can't put a picture stream into (Ogg, Matroska): prepared on the side.
        var cover = !still && info.CoverStreamIndex is { } coverStream && MediaEncoding.AudioTargets.ContainsKey(step.To)
            ? await CoverArt.PrepareAsync(ffmpeg, input, coverStream, step.To, step.WorkDirectory, cancellationToken)
            : null;

        var media = step.Preset.Media;
        // "Remove metadata": the streams as they are, in the same format, without tags.
        if (media.RemoveMetadata && step.From == step.To && !still
            && (MediaEncoding.VideoTargets.ContainsKey(step.To) || MediaEncoding.AudioTargets.ContainsKey(step.To)))
        {
            try
            {
                await RunPassesAsync(ffmpeg, [new FfmpegPass(MediaEncoding.WithoutMetadata(input, output, step.To, info), 1)], output,
                    info.Duration, progress, cancellationToken);
            }
            catch
            {
                TryDelete(output);
                throw;
            }
            progress?.Report(1);
            return [output];
        }

        var encoders = still ? null : EncodersOf(ffmpeg);
        // Hardware encoding (preset option): the first GPU encoder that passes its test encode, if any.
        HardwareEncoder? hardware = null;
        if (!still && MediaEncoding.HardwareCodec(step.To, media, info) is { } codec)
        {
            var tenBit = HardwareVideo.TenBit(codec, info.Video!);
            hardware = SelectHardware is { } select
                ? await select(ffmpeg, codec, tenBit, encoders, cancellationToken)
                : await HardwareProbe.SelectAsync(ffmpeg, codec, tenBit, encoders, _logger, cancellationToken);
        }

        // Video → animated GIF / APNG can play faster or slower (the image options' playback speed).
        var speed = step.To is "gif" or "apng" ? step.Preset.Image.PlaybackSpeed : 1;
        IReadOnlyList<FfmpegPass> Plan(HardwareEncoder? encoder) => still
            ? MediaEncoding.PlanStill(input, output, step.To, media, info)
            : MediaEncoding.Plan(input, output, step.To, media, info, step.WorkDirectory, cover, encoders, encoder, speed);
        // Throws for a missing stream before anything is written (an existing file of that name stays untouched).
        var passes = Plan(hardware);
        // FFmpeg reports the position in the output, which a speed change makes longer or shorter.
        var duration = still ? MediaEncoding.StillDuration : info.Duration / speed;
        try
        {
            await RunWithFallbackAsync(passes, hardware, Plan,
                planned => RunPassesAsync(ffmpeg, planned, output, duration, progress, cancellationToken), output, _logger, cancellationToken);
        }
        catch
        {
            // Never leave a half-written file where the user expects a finished one.
            TryDelete(output);
            throw;
        }

        progress?.Report(1);
        return [output];
    }

    /// <remarks>
    /// Everything that can fail before FFmpeg writes (probing, the picture limit, preparing the slides) happens first, and
    /// nothing here deletes <paramref name="outputPath"/>: with "Overwrite" it may be the user's own file. A join that
    /// fails or is cancelled is undone by the queue, which deletes only paths that were the job's own.
    /// </remarks>
    public async Task JoinAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = _tools ?? Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var scratch = Path.Combine(workDirectory, "join-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var media = preset.Media;

        List<string> args;
        TimeSpan duration;
        double start;
        if (media.Slideshow)
        {
            var slides = await PrepareSlidesAsync(ffmpeg, inputPaths, scratch, progress, cancellationToken);
            var encoder = MediaEncoding.VideoEncoder("mp4", "libx264", media.VideoCodec, EncodersOf(ffmpeg));
            args = MediaJoining.Slideshow(slides, outputPath, media, encoder, scratch);
            duration = MediaJoining.SlideshowDuration(slides.Count, media);
            start = 0.2;
        }
        else
        {
            // Probing takes the first few percent.
            var clips = new List<(string Path, MediaInfo Info)>();
            for (var i = 0; i < inputPaths.Count; i++)
            {
                var path = Path.GetFullPath(inputPaths[i]);
                clips.Add((path, await MediaInfo.ProbeAsync(ffprobe, path, cancellationToken)));
                progress?.Report(0.05 * (i + 1) / inputPaths.Count);
            }
            duration = TimeSpan.FromSeconds(clips.Sum(c => c.Info.Duration?.TotalSeconds ?? 0));
            start = 0.05;
            if (format == "m4a")
            {
                if (clips.FirstOrDefault(c => c.Info.Audio is null && c.Info.Video is null) is { Path: { } silent })
                    throw new InvalidOperationException($"{Path.GetFileName(silent)} has no sound.");
                args = MediaJoining.EncodeAudio(clips, outputPath, media, scratch);
            }
            else if (MediaJoining.CanCopyVideos([.. clips.Select(c => c.Info)], media))
            {
                var list = Path.Combine(scratch, "clips.txt");
                await File.WriteAllTextAsync(list, MediaJoining.ConcatList(clips.Select(c => c.Path)), cancellationToken);
                args = MediaJoining.CopyVideos(list, outputPath, clips[0].Info);
            }
            else
            {
                var encoder = MediaEncoding.VideoEncoder("mp4", "libx264", media.VideoCodec, EncodersOf(ffmpeg));
                args = MediaJoining.EncodeVideos(clips, outputPath, media, encoder, scratch);
            }
        }

        await RunJoinPassAsync(ffmpeg, args, duration, progress, start, 1, cancellationToken);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("FFmpeg did not write any output.");
        progress?.Report(1);
    }

    /// <summary>Every picture fitted into the slideshow frame as a small JPEG in the work folder (a fifth of the progress).</summary>
    private static async Task<List<string>> PrepareSlidesAsync(string ffmpeg, IReadOnlyList<string> pictures, string scratch,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The queue checks the number of files; a multi-page picture can still give more slides than that.
        if (pictures.Count > MediaOptions.MaxSlides)
            throw new ConversionRefusedException("error.too_many_pictures", $"{pictures.Count} > {MediaOptions.MaxSlides}");
        var slides = new List<string>();
        for (var i = 0; i < pictures.Count; i++)
        {
            var slide = Path.Combine(scratch, $"slide-{i + 1:D4}.jpg");
            await RunJoinPassAsync(ffmpeg, MediaJoining.PrepareSlide(Path.GetFullPath(pictures[i]), slide,
                MediaJoining.SlideshowWidth, MediaJoining.SlideshowHeight), null, null, 0, 0, cancellationToken);
            slides.Add(slide);
            progress?.Report(0.2 * (i + 1) / pictures.Count);
        }
        return slides;
    }

    /// <summary>Runs one join pass, mapping FFmpeg's position onto <paramref name="from"/> … <paramref name="to"/> of the progress.</summary>
    private static async Task RunJoinPassAsync(string ffmpeg, IReadOnlyList<string> arguments, TimeSpan? duration, IProgress<double>? progress,
        double from, double to, CancellationToken cancellationToken)
    {
        var total = duration?.TotalSeconds ?? 0;
        Action<double>? onPosition = progress is null || total <= 0
            ? null
            : seconds => progress.Report(from + (to - from) * Math.Clamp(seconds / total, 0, 1));
        var result = await FfmpegRunner.RunAsync(ffmpeg, arguments, onPosition, cancellationToken);
        if (result.ExitCode != 0)
        {
            var reason = FfmpegRunner.Summarize(result.StandardError);
            throw new InvalidOperationException($"FFmpeg failed (exit code {result.ExitCode}){(reason.Length > 0 ? ": " + reason : ".")}");
        }
    }

    /// <summary>
    /// "Cut a clip": the clip the window made (<see cref="MediaOptions.Clip"/>, kept inside the video). One stretch as
    /// recorded is copied as it is unless the preset asks for an exact cut (<see cref="MediaOptions.ExactCut"/>); a copy
    /// the target can't take (a codec its container doesn't hold) is encoded exactly instead, like the preset's other
    /// videos. Several parts, speeds, volumes, pauses or fades are encoded through one filter graph.
    /// </summary>
    private async Task<IReadOnlyList<string>> CutClipAsync(string ffmpeg, ConversionStep step, string input, MediaInfo info,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (info.Video is null)
            throw new InvalidOperationException("The file has no video stream.");
        var media = step.Preset.Media;
        var edit = media.Clip!.Clamped(info.Duration?.TotalSeconds);
        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];
        var duration = TimeSpan.FromSeconds(edit.Length);
        if (!edit.IsPlainCut)
        {
            // Parts, speeds, volumes, pauses or fades: one filter graph, always encoded.
            try
            {
                var pass = new FfmpegPass(MediaEncoding.ClipEditArguments(input, output, step.To, edit, media, info, EncodersOf(ffmpeg)), 1);
                await RunPassesAsync(ffmpeg, [pass], output, duration, progress, cancellationToken);
            }
            catch
            {
                TryDelete(output);
                throw;
            }
            progress?.Report(1);
            return [output];
        }

        var clip = new ClipRange(edit.Parts[0].Start, edit.Parts[0].End);
        try
        {
            if (!media.ExactCut)
            {
                try
                {
                    var copy = new FfmpegPass(MediaEncoding.ClipCopy(input, output, step.To, clip, media, info), 1);
                    await RunPassesAsync(ffmpeg, [copy], output, duration, progress, cancellationToken);
                    progress?.Report(1);
                    return [output];
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{File}: copying the clip failed ({Reason}); encoding it instead", input, ex.Message);
                    TryDelete(output);
                }
            }

            var encoders = EncodersOf(ffmpeg);
            HardwareEncoder? hardware = null;
            if (MediaEncoding.HardwareCodec(step.To, media, info, encode: true) is { } codec)
            {
                var tenBit = HardwareVideo.TenBit(codec, info.Video);
                hardware = SelectHardware is { } select
                    ? await select(ffmpeg, codec, tenBit, encoders, cancellationToken)
                    : await HardwareProbe.SelectAsync(ffmpeg, codec, tenBit, encoders, _logger, cancellationToken);
            }
            IReadOnlyList<FfmpegPass> Plan(HardwareEncoder? encoder) =>
                MediaEncoding.Plan(input, output, step.To, media, info, step.WorkDirectory, encoders: encoders, hardware: encoder, clip: clip);
            await RunWithFallbackAsync(Plan(hardware), hardware, Plan,
                planned => RunPassesAsync(ffmpeg, planned, output, duration, progress, cancellationToken), output, _logger, cancellationToken);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// The video of a Live / Motion Photo as <see cref="ConversionStep.To"/>: copied out of the photo (or from next to
    /// it) into the work directory, converted there like any video and moved into place once finished, so a failure
    /// never touches a file the output would replace.
    /// </summary>
    private async Task<IReadOnlyList<string>> ExtractMotionAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (video, companion) = await LivePhotoSource.ExtractAsync(step.InputPath, step.From, step.WorkDirectory, _logger, cancellationToken);
        var staged = new StagedOutput(step.Output, step.WorkDirectory, companion);
        var written = await ConvertAsync(step with { InputPath = video, From = "mov", MotionPhoto = false, Output = staged },
            progress, cancellationToken);
        return staged.Commit(written);
    }

    /// <summary>
    /// Hands out paths in the work directory for outputs whose real path comes from <paramref name="final"/>. That one
    /// is never <paramref name="companion"/>: with "overwrite", IMG_0001.HEIC → MOV would otherwise be written over
    /// IMG_0001.MOV, the Live Photo's own video, so it gets a numbered name instead.
    /// </summary>
    private sealed class StagedOutput(IOutputAllocator final, string workDirectory, string? companion) : IOutputAllocator
    {
        private readonly Dictionary<string, string> _paths = [];

        public string? Allocate(string extension, string? suffix = null)
        {
            var path = final.Allocate(extension, suffix);
            for (var n = 2; path is not null && companion is not null && SamePath(path, companion); n++)
                path = final.Allocate(extension, $"{suffix} ({n})");
            if (path is null)
                return null;
            var temp = Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + "." + extension.TrimStart('.'));
            _paths[temp] = path;
            return temp;
        }

        public IReadOnlyList<string> Commit(IReadOnlyList<string> written) =>
            written.Select(temp =>
            {
                File.Move(temp, _paths[temp], overwrite: true);
                return _paths[temp];
            }).ToList();

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs <paramref name="passes"/>, planned with <paramref name="hardware"/>. When a hardware encode fails (a driver
    /// error, a picture larger than the chip encodes, …) the file is encoded again as <paramref name="plan"/> plans it
    /// without hardware: an encoder that passed its test encode can still refuse one particular video, and the user
    /// asked for a converted file, not for the GPU. The failure is logged, not reported. Cancellation is never retried.
    /// </summary>
    internal static async Task RunWithFallbackAsync(IReadOnlyList<FfmpegPass> passes, HardwareEncoder? hardware,
        Func<HardwareEncoder?, IReadOnlyList<FfmpegPass>> plan, Func<IReadOnlyList<FfmpegPass>, Task> run, string output, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (hardware is null)
        {
            await run(passes);
            return;
        }
        try
        {
            await run(passes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{File}: hardware encoder {Encoder} failed ({Reason}); encoding it again with the software encoder",
                output, hardware.Name, ex.Message);
            TryDelete(output);
            // An FFmpeg without x265 or an AV1 encoder says so here: the hardware encoder was its only way to write it.
            await run(plan(null));
        }
    }

    /// <summary>Runs every pass, then checks that FFmpeg wrote something.</summary>
    private static async Task RunPassesAsync(string ffmpeg, IReadOnlyList<FfmpegPass> passes, string output, TimeSpan? duration,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var done = 0.0;
        foreach (var pass in passes)
        {
            await RunPassAsync(ffmpeg, pass, duration, progress, done, cancellationToken);
            done += pass.Weight;
        }
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException("FFmpeg did not write any output.");
    }

    /// <summary>Runs one pass and maps FFmpeg's position (seconds) onto this pass' share of the progress.</summary>
    internal static async Task RunPassAsync(string ffmpeg, FfmpegPass pass, TimeSpan? duration, IProgress<double>? progress, double done, CancellationToken cancellationToken)
    {
        var total = duration?.TotalSeconds ?? 0;
        Action<double>? onPosition = progress is null || total <= 0
            ? null
            : seconds => progress.Report(0.02 + 0.97 * (done + pass.Weight * Math.Clamp(seconds / total, 0, 1)));

        ProcessResult result;
        // Without a duration (raw streams) there is nothing to measure against: show an estimate instead.
        using (total <= 0 ? ProgressEstimate.Start(progress, TimeSpan.FromSeconds(20), 0.02 + 0.97 * done, 0.02 + 0.97 * (done + pass.Weight)) : null)
            result = await FfmpegRunner.RunAsync(ffmpeg, pass.Arguments, onPosition, cancellationToken);

        if (result.ExitCode != 0)
        {
            var reason = FfmpegRunner.Summarize(result.StandardError);
            throw new InvalidOperationException($"FFmpeg failed (exit code {result.ExitCode}){(reason.Length > 0 ? ": " + reason : ".")}");
        }
    }

    /// <summary>Both tools must come from the same installation.</summary>
    internal static (string Ffmpeg, string Ffprobe)? Locate()
    {
        var folder = EngineEnvironment.OwnCopyFolder("ffmpeg")
            ?? (EngineEnvironment.FindBundled("ffmpeg") is { } bundled ? EngineEnvironment.FolderWithPrograms("ffmpeg", bundled) : null)
            ?? EngineEnvironment.SystemCopyFolder("ffmpeg");
        return folder is null ? null : (Path.Combine(folder, EngineEnvironment.ProgramName("ffmpeg.exe")),
            Path.Combine(folder, EngineEnvironment.ProgramName("ffprobe.exe")));
    }

    /// <summary>"9.0.2" from <c>ffmpeg -version</c>; cached per executable so re-checking the engines stays cheap.</summary>
    /// <exception cref="InvalidOperationException">FFmpeg does not start, fails or does not answer within <paramref name="timeout"/>.</exception>
    internal static string VersionOf(string ffmpeg, TimeSpan? timeout = null) =>
        Versions.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}", _ =>
        {
            ProcessResult result;
            try
            {
                result = FfmpegRunner.RunBriefly(ffmpeg, ["-hide_banner", "-version"], timeout ?? QueryTimeout);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("FFmpeg did not start in time.");
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg does not start (exit code {result.ExitCode}).");
            // "ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright …" → "9.0.2"
            var firstLine = result.StandardOutput.Split('\n', 2)[0];
            var match = VersionPattern().Match(firstLine);
            return match.Success ? match.Groups[1].Value : firstLine.Trim();
        });

    [GeneratedRegex(@"version\s+n?(\d+(?:\.\d+)+|\S+)")]
    private static partial Regex VersionPattern();

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>?> Encoders = new();

    /// <summary>
    /// The encoders of <paramref name="ffmpeg"/> (<c>ffmpeg -encoders</c>), cached per executable: a copy the user
    /// picked may lack x265 or SVT-AV1. Null (assume Filee's build) when the list can't be read.
    /// </summary>
    internal static IReadOnlySet<string>? EncodersOf(string ffmpeg, TimeSpan? timeout = null) =>
        Encoders.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}", _ =>
        {
            try
            {
                var result = FfmpegRunner.RunBriefly(ffmpeg, ["-hide_banner", "-encoders"], timeout ?? QueryTimeout);
                return result.ExitCode == 0 ? ParseEncoders(result.StandardOutput) : null;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return null;
            }
        });

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>?> Filters = new();

    /// <summary>
    /// True when <paramref name="ffmpeg"/> has the filter <paramref name="name"/> (<c>ffmpeg -filters</c>, cached per
    /// executable): a copy the user picked may lack zscale. Assumed when the list can't be read.
    /// </summary>
    internal static bool HasFilter(string ffmpeg, string name) =>
        Filters.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}", _ =>
        {
            try
            {
                var result = FfmpegRunner.RunBriefly(ffmpeg, ["-hide_banner", "-filters"], QueryTimeout);
                return result.ExitCode == 0 ? ParseFilters(result.StandardOutput) : null;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return null;
            }
        }) is not { } filters || filters.Contains(name);

    /// <summary>The filter names in <c>ffmpeg -filters</c> output (" .S zscale  V->V  Apply resizing, …").</summary>
    internal static IReadOnlySet<string> ParseFilters(string list) =>
        list.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[0].Length is 2 or 3 && parts[0].All(c => c is '.' or 'T' or 'S' or 'C')
                            && parts[2].Contains("->", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The encoder names in <c>ffmpeg -encoders</c> output.</summary>
    internal static IReadOnlySet<string> ParseEncoders(string list) =>
        // " V....D libx265              libx265 H.265 / HEVC (codec hevc)"
        list.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S')
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

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
