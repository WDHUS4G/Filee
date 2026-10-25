// Every audio track of a video is kept when it is converted (issue #396): a film with three languages keeps all three,
// each copied where the target takes its codec or encoded on its own, with its language tag.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class AudioTracksTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static readonly VideoStreamInfo Hd = new(1920, 1080, 1, 0, 23.976, "h264", "yuv420p");

    private static List<string> Plan(string target, MediaInfo info, MediaOptions? options = null) =>
        [.. MediaEncoding.Plan("in.mkv", "out." + target, target, options ?? new MediaOptions(), info, "work").Single().Arguments];

    private static string After(List<string> args, string option) => args[args.IndexOf(option) + 1];

    [Fact]
    public void Every_track_is_mapped_and_each_gets_its_own_options()
    {
        var info = new MediaInfo(TimeSpan.FromMinutes(24), Hd, new AudioStreamInfo(2, 48000, "aac"), null,
            AudioStreams: [new(2, 48000, "aac"), new(6, 48000, "flac"), new(2, 48000, "opus")]);

        var args = Plan("mp4", info, new MediaOptions { MaxHeight = 720 });

        Assert.Equal(["0:V:0", "0:a:0", "0:a:1", "0:a:2"], args.Where((a, i) => i > 0 && args[i - 1] == "-map"));
        Assert.Equal("copy", After(args, "-c:a:0")); // AAC goes into MP4 as it is
        Assert.Equal("aac", After(args, "-c:a:1")); // FLAC doesn't: encoded, at a bitrate for 5.1
        Assert.Equal("576k", After(args, "-b:a:1"));
        Assert.Equal("aac", After(args, "-c:a:2"));
        Assert.Equal("192k", After(args, "-b:a:2"));
        Assert.DoesNotContain("-c:a", args); // nothing left unaddressed
    }

    [Fact]
    public void One_track_keeps_the_plain_options_and_single_track_containers_take_the_first()
    {
        var one = new MediaInfo(TimeSpan.FromMinutes(1), Hd, new AudioStreamInfo(2, 48000, "opus"), null);
        Assert.Equal("aac", After(Plan("mp4", one), "-c:a"));

        var three = one with { AudioStreams = [new(2, 48000, "opus"), new(2, 48000, "opus"), new(2, 48000, "opus")] };
        var flv = Plan("flv", three);
        Assert.Equal(["0:V:0", "0:a:0"], flv.Where((a, i) => i > 0 && flv[i - 1] == "-map"));
        Assert.Contains("-an", Plan("mp4", three, new MediaOptions { RemoveAudio = true }));
    }

    [Fact]
    public void Option_values_are_never_renamed()
    {
        Assert.Equal(["-c:a:1", "wmav2", "-b:a:1", "128k", "-ac:a:1", "2"],
            MediaEncoding.ForAudioTrack(["-c:a", "wmav2", "-b:a", "128k", "-ac", "2"], 1));
    }

    [Fact]
    public async Task A_three_language_mkv_keeps_its_languages_as_a_720p_mp4()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var folder = fx.NewFolder();
        var film = Path.Combine(folder, "episode.mkv");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=330:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=550:duration=2",
            "-map", "0", "-map", "1", "-map", "2", "-map", "3",
            "-c:v", "libx264", "-c:a:0", "aac", "-c:a:1", "libopus", "-c:a:2", "flac",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng", "-metadata:s:a:2", "language=kor", film,
        ], TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(made.ExitCode == 0, made.StandardError);

        var job = await fx.ConvertAsync([film], BuiltInData.CreatePresets().Single(p => p.Id == "mp4-720"));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var probe = await ProcessRunner.RunAsync(Tools.Value.Ffprobe,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name:stream_tags=language", "-of", "json", job.Outputs.Single()],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        using var json = JsonDocument.Parse(probe.StandardOutput);
        var tracks = json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (s.GetProperty("codec_name").GetString(), s.GetProperty("tags").GetProperty("language").GetString())).ToList();
        Assert.Equal([("aac", "jpn"), ("aac", "eng"), ("aac", "kor")], tracks);
    }
}
