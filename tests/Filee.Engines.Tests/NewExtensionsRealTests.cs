// Formats from issues #301 (raw H.265 / H.264 camera streams) and #328 (HAR network logs) converted for real. The
// video tests need Filee's FFmpeg (`pwsh build/fetch-engines.ps1 -Only ffmpeg`).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class NewExtensionsRealTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    [Theory]
    [InlineData("camera.h265", "libx265", "hevc", "avi")]
    [InlineData("camera.h265", "libx265", "hevc", "mp4")]
    [InlineData("camera.264", "libx264", "h264", "mp4")]
    public async Task A_raw_camera_stream_becomes_a_playable_video(string name, string encoder, string muxer, string target)
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var folder = fx.NewFolder();
        var raw = Path.Combine(folder, name);
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
             "-c:v", encoder, "-pix_fmt", "yuv420p", "-f", muxer, raw], TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(made.ExitCode == 0, made.StandardError);

        var output = await ConvertAsync(raw, target);

        var info = await MediaInfo.ProbeAsync(Tools.Value.Ffprobe, output, CancellationToken.None);
        Assert.Equal((320, 240), (info.Video!.Width, info.Video.Height));
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.8, 2.2);
        var decode = await ProcessRunner.RunAsync(Tools.Value.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", output, "-f", "null", "-"],
            TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(decode.ExitCode == 0 && decode.StandardError.Trim().Length == 0, decode.StandardError);
    }

    [Fact]
    public async Task A_har_network_log_converts_like_json()
    {
        var folder = fx.NewFolder();
        var har = Path.Combine(folder, "session.har");
        await File.WriteAllTextAsync(har, """
            {"log": {"version": "1.2", "creator": {"name": "Firefox", "version": "140"},
              "entries": [{"startedDateTime": "2026-10-21T10:00:00Z", "time": 42,
                "request": {"method": "GET", "url": "https://example.com/api/items"},
                "response": {"status": 200, "statusText": "OK"}}]}}
            """, TestContext.Current.CancellationToken);

        // One row per request: method, address and status side by side. CSV is the table step every table route
        // (Markdown, spreadsheets, PDF) starts from; Markdown itself goes through Unhwp, which Linux ARM64 lacks.
        var csv = await File.ReadAllTextAsync(await ConvertAsync(har, "csv"), TestContext.Current.CancellationToken);
        Assert.Contains("GET,https://example.com/api/items,200", csv);
        var json = await File.ReadAllTextAsync(await ConvertAsync(har, "json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"creator\"", json); // the whole log, as it was
    }

    [Fact]
    public void A_raw_stream_is_never_copied_into_a_container()
    {
        var json = """
            {"format": {"format_name": "hevc"}, "streams": [{"codec_type": "video", "codec_name": "hevc", "width": 320,
              "height": 240, "pix_fmt": "yuv420p", "avg_frame_rate": "25/1"}]}
            """;
        var info = MediaInfo.Parse(json);
        Assert.True(info.RawStream);

        var args = MediaEncoding.Plan("in.h265", "out.mp4", "mp4", new MediaOptions(), info, "work").Single().Arguments;
        Assert.Equal("libx264", args[args.ToList().IndexOf("-c:v") + 1]);
        Assert.False(MediaInfo.Parse(json.Replace("\"hevc\"}", "\"mov,mp4,m4a,3gp,3g2,mj2\"}")).RawStream);
    }
}
