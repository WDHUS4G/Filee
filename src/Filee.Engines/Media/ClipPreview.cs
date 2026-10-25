// The clip window's preview: plays a clip as it will be written (its parts, speeds, volumes, pauses and fades), the
// pictures as raw frames from FFmpeg, the sound through ffplay (FFmpeg's player, part of Filee's FFmpeg download;
// without it the preview plays silent). Also the sound's loudness over the video for the timeline, and where it is
// silent, for "Remove silence".

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>Plays clips and reads a video's sound for the clip window.</summary>
public static partial class ClipPreview
{
    /// <summary>Frames per second of the preview.</summary>
    public const int Fps = 25;

    /// <summary>Samples per second the loudness is read at: enough for a timeline, little to read.</summary>
    private const int PeakRate = 2000;

    /// <summary>Quieter than this is silence, for "Remove silence".</summary>
    public const double SilenceThreshold = -35;

    /// <summary>
    /// How long the sound takes to start once ffplay has it (opening the sound device): the pictures wait as long, so
    /// both start together.
    /// </summary>
    private static readonly TimeSpan SoundStart = TimeSpan.FromMilliseconds(180);

    /// <summary>The player's volume (0–100) instead of full; tests play silently with 0.</summary>
    internal static int? PlayerVolume { get; set; }

    /// <summary>Told how many bytes of sound reached the player once a clip has played (tests).</summary>
    internal static Action<long>? SoundPlayed { get; set; }

    /// <summary>True when the preview plays sound: ffplay is next to Filee's FFmpeg.</summary>
    public static bool HasSound => Player() is not null;

    private static string? Player() =>
        FfmpegConverter.Locate() is { } tools
        && Path.Combine(Path.GetDirectoryName(tools.Ffmpeg) ?? "", EngineEnvironment.ProgramName("ffplay.exe")) is var ffplay
        && File.Exists(ffplay) ? ffplay : null;

    /// <summary>
    /// Plays <paramref name="edit"/> of the video at <paramref name="path"/> from its start (use
    /// <see cref="ClipEdit.From"/> to start later): each picture, <paramref name="height"/> pixels high in BGRA, goes to
    /// <paramref name="onFrame"/> (pixels, width, height, seconds into <paramref name="edit"/>) when it is due; the sound
    /// plays along. Ends when the clip has played or <paramref name="cancellationToken"/> is cancelled (then it stops at
    /// once and doesn't throw). <paramref name="onFrame"/> is called on a worker thread, with a buffer it may keep only
    /// until it returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg is missing or the video can't be read.</exception>
    public static async Task PlayAsync(string path, ClipEdit edit, int height, Action<byte[], int, int, double> onFrame,
        CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var input = Path.GetFullPath(path);
        var info = await MediaInfo.ProbeAsync(ffprobe, input, cancellationToken).ConfigureAwait(false);
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        ClipEdit clip;
        try
        {
            clip = edit.Clamped(info.Duration?.TotalSeconds);
        }
        catch (InvalidOperationException)
        {
            return; // nothing left to play
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pictures = Start(ffmpeg, MediaEncoding.ClipPreviewVideo(input, clip, video, height, Fps, out var width, out var frameHeight), stdin: false);
        Process? decoder = null, player = null;
        Task pump = Task.CompletedTask;
        try
        {
            if ((info.Audio is not null || clip.Music is not null) && Player() is { } ffplay)
            {
                decoder = Start(ffmpeg, MediaEncoding.ClipPreviewAudio(input, clip, video, info.Audio is not null), stdin: false);
                player = Start(ffplay, ["-hide_banner", "-loglevel", "error", "-nodisp", "-autoexit",
                    .. PlayerVolume is { } volume ? new[] { "-volume", volume.ToString(CultureInfo.InvariantCulture) } : [], "-i", "-"], stdin: true);
                pump = Pump(decoder, player, stop.Token);
            }

            var frame = new byte[width * frameHeight * 4];
            // The clock starts with the first picture: FFmpeg takes a moment to seek and start, and pictures counted
            // from before that would all be late (and skipped) until it caught up.
            Stopwatch? clock = null;
            var delay = player is null ? TimeSpan.Zero : SoundStart;
            for (var n = 0; !stop.IsCancellationRequested; n++)
            {
                if (!await ReadFully(pictures.StandardOutput.BaseStream, frame, stop.Token).ConfigureAwait(false))
                    break;
                clock ??= Stopwatch.StartNew();
                var due = delay + TimeSpan.FromSeconds((double)n / Fps);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                else if (wait < TimeSpan.FromSeconds(-0.2))
                    continue; // behind: skip a picture rather than run late
                onFrame(frame, width, frameHeight, (double)n / Fps);
            }
            // The sound may still be playing its last moment.
            if (!stop.IsCancellationRequested && player is not null)
                await Task.WhenAny(player.WaitForExitAsync(stop.Token), Task.Delay(TimeSpan.FromSeconds(1), stop.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var process in new[] { pictures, decoder, player })
            {
                if (process is null)
                    continue;
                Kill(process);
                process.Dispose();
            }
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // The player went away first.
            }
        }
    }

    /// <summary>
    /// How loud the sound of the video at <paramref name="path"/> is over its <paramref name="duration"/> seconds, in
    /// <paramref name="count"/> steps from 0 (silent) to 1 (its loudest step); empty when it has no sound.
    /// </summary>
    /// <param name="workFolder">Where the samples are written for a moment.</param>
    public static Task<float[]> ReadLoudnessAsync(string path, double duration, int count, string workFolder, CancellationToken cancellationToken) =>
        ReadLoudnessAsync(path, 0, duration, count, workFolder, cancellationToken);

    /// <summary>
    /// How loud the sound of the file at <paramref name="path"/> is from <paramref name="start"/> to
    /// <paramref name="end"/> seconds, in <paramref name="count"/> steps from 0 to 1 (its loudest step); empty
    /// without sound.
    /// </summary>
    public static async Task<float[]> ReadLoudnessAsync(string path, double start, double end, int count, string workFolder,
        CancellationToken cancellationToken)
    {
        var (ffmpeg, _) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var samples = Path.Combine(workFolder, Guid.NewGuid().ToString("N") + ".f32");
        try
        {
            List<string> args = [.. MediaEncoding.Common];
            if (start > 0)
                args.AddRange(["-ss", ClipRange.Ffmpeg(start)]);
            if (end > start)
                args.AddRange(["-to", ClipRange.Ffmpeg(end)]);
            args.AddRange(["-i", Path.GetFullPath(path), "-map", "0:a:0", "-ac", "1", "-ar", PeakRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_f32le", "-f", "f32le", samples]);
            var result = await FfmpegRunner.RunAsync(ffmpeg, args, null, cancellationToken, background: true);
            if (result.ExitCode != 0 || !File.Exists(samples))
                return []; // no sound
            var bytes = await File.ReadAllBytesAsync(samples, cancellationToken);
            return Loudness(MemoryMarshalFloats(bytes), PeakRate, end > start ? end - start : 0, count);
        }
        finally
        {
            try
            {
                File.Delete(samples);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>How long the sound (or video) at <paramref name="path"/> plays, in seconds; throws when it has no sound.</summary>
    public static async Task<double> ReadSoundLengthAsync(string path, CancellationToken cancellationToken)
    {
        var (_, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var info = await MediaInfo.ProbeAsync(ffprobe, Path.GetFullPath(path), cancellationToken);
        if (info.Audio is null)
            throw new InvalidOperationException("The file has no sound.");
        return info.Duration?.TotalSeconds is > 0 and var seconds ? seconds : throw new InvalidOperationException("The file doesn't say how long it is.");
    }

    /// <summary>The silent stretches of the video at <paramref name="path"/> between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static async Task<IReadOnlyList<ClipRange>> FindSilencesAsync(string path, double start, double end, CancellationToken cancellationToken)
    {
        var (ffmpeg, _) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var result = await ProcessRunner.RunAsync(ffmpeg, SilenceArguments(Path.GetFullPath(path), start, end), TimeSpan.FromMinutes(10), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("FFmpeg failed: " + FfmpegRunner.Summarize(result.StandardError));
        return ParseSilences(result.StandardError, start, end);
    }

    /// <summary>FFmpeg's silencedetect over <paramref name="start"/> to <paramref name="end"/> of the video, reported on stderr.</summary>
    internal static List<string> SilenceArguments(string input, double start, double end) =>
    [
        "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-ss", ClipRange.Ffmpeg(start), "-to", ClipRange.Ffmpeg(end), "-i", input,
        "-map", "0:a:0", "-af", $"silencedetect=noise={SilenceThreshold.ToString(CultureInfo.InvariantCulture)}dB:d={ClipCutter.MinSilence.ToString(CultureInfo.InvariantCulture)}",
        "-f", "null", "-",
    ];

    /// <summary>
    /// The silences silencedetect reported (<c>silence_start: 1.5</c> … <c>silence_end: 3.25</c>), in seconds of the
    /// video: its times count from <paramref name="offset"/>, where it started reading; one still silent at the end
    /// lasts until <paramref name="end"/>.
    /// </summary>
    internal static IReadOnlyList<ClipRange> ParseSilences(string log, double offset, double end)
    {
        var silences = new List<ClipRange>();
        double? open = null;
        foreach (Match match in SilenceLine().Matches(log))
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                continue;
            var at = Math.Max(offset, offset + seconds);
            if (match.Groups[1].Value == "start")
                open = at;
            else if (open is { } from)
            {
                if (Math.Min(at, end) > from)
                    silences.Add(new ClipRange(from, Math.Min(at, end)));
                open = null;
            }
        }
        if (open is { } last && end > last)
            silences.Add(new ClipRange(last, end));
        return silences;
    }

    [GeneratedRegex(@"silence_(start|end):\s*(-?[0-9.]+(?:e-?[0-9]+)?)")]
    private static partial Regex SilenceLine();

    /// <summary>
    /// The loudness of <paramref name="samples"/> (mono, <paramref name="rate"/> a second) in <paramref name="count"/>
    /// equal steps of <paramref name="duration"/> seconds: the root mean square of each step, the loudest step 1.
    /// </summary>
    internal static float[] Loudness(ReadOnlySpan<float> samples, int rate, double duration, int count)
    {
        if (count <= 0 || samples.Length == 0)
            return [];
        var steps = new float[count];
        var total = duration > 0 ? duration * rate : samples.Length;
        for (var i = 0; i < count; i++)
        {
            var from = (int)Math.Min(samples.Length, Math.Floor(total * i / count));
            var to = (int)Math.Min(samples.Length, Math.Floor(total * (i + 1) / count));
            if (to <= from)
                continue;
            double sum = 0;
            for (var s = from; s < to; s++)
                sum += samples[s] * samples[s];
            steps[i] = (float)Math.Sqrt(sum / (to - from));
        }
        var loudest = steps.Max();
        if (loudest > 0)
        {
            for (var i = 0; i < count; i++)
                steps[i] /= loudest;
        }
        return steps;
    }

    private static ReadOnlySpan<float> MemoryMarshalFloats(byte[] bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length / 4 * 4));

    private static Process Start(string executable, IReadOnlyList<string> arguments, bool stdin)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = !stdin,
            RedirectStandardInput = stdin,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        // Nobody reads the messages: drained, so a chatty process never blocks on a full pipe.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Passes the decoded sound on to the player.</summary>
    private static async Task Pump(Process decoder, Process player, CancellationToken cancellationToken)
    {
        long passed = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await decoder.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await player.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                passed += read;
            }
        }
        finally
        {
            SoundPlayed?.Invoke(passed);
            try
            {
                player.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
    }

    private static async Task<bool> ReadFully(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
