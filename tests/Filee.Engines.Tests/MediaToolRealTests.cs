// The everyday media tools run for real: videos and audio files merged, pictures made into a slideshow, a video's
// sound extracted, and copies without metadata (videos and audio need Filee's FFmpeg, `pwsh build/fetch-engines.ps1
// -Only ffmpeg`; pictures and PDFs always run). Inputs are generated; every output is checked with ffprobe or by
// reading it back.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class MediaToolRealTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    private async Task<string> RunAsync(IReadOnlyList<string> inputs, Preset preset)
    {
        var job = await fx.ConvertAsync(inputs, preset);
        Assert.True(job.State == JobState.Completed, $"{job.State}: " + string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    // ───────────────────────── Inputs ─────────────────────────

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A test pattern clip, with a 440 Hz tone unless <paramref name="audio"/> is false.</summary>
    private static async Task<string> MakeClipAsync(string folder, string name, double seconds = 2, string size = "320x240",
        int fps = 25, bool audio = true, params string[] extra)
    {
        var path = Path.Combine(folder, name);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={fps}:duration={Text(seconds)}"];
        if (audio)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={Text(seconds)}", "-ac", "2"]);
        args.AddRange(extra);
        args.Add(path);
        await FfmpegAsync(args);
        return path;
    }

    /// <summary>A tone of <paramref name="channels"/> channels, in the format the name's extension says.</summary>
    private static async Task<string> MakeToneAsync(string folder, string name, double seconds, int channels = 2, int rate = 44100,
        params string[] extra)
    {
        var path = Path.Combine(folder, name);
        await FfmpegAsync(["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi",
            "-i", $"sine=frequency=330:sample_rate={rate}:duration={Text(seconds)}", "-ac", channels.ToString(CultureInfo.InvariantCulture),
            .. extra, path]);
        return path;
    }

    private static async Task<ProcessResult> FfmpegAsync(IEnumerable<string> args)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, args, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    // ───────────────────────── What ffprobe sees ─────────────────────────

    private sealed record Track(string Type, string Codec, int Width, int Height, int Channels, Dictionary<string, string> Tags);

    private sealed record Probed(double Duration, List<Track> Streams, Dictionary<string, string> Tags, int Chapters)
    {
        public Track? Video => Streams.FirstOrDefault(s => s.Type == "video");
        public Track? Audio => Streams.FirstOrDefault(s => s.Type == "audio");
    }

    private static async Task<Probed> ProbeAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"ffprobe {path}: {result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
        static int N(JsonElement e, string name) => int.TryParse(S(e, name), out var n) ? n : 0;
        static Dictionary<string, string> Tags(JsonElement e) => e.TryGetProperty("tags", out var tags)
            ? tags.EnumerateObject().ToDictionary(t => t.Name.ToLowerInvariant(), t => t.Value.ToString())
            : [];
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray()
            .Select(s => new Track(S(s, "codec_type"), S(s, "codec_name"), N(s, "width"), N(s, "height"), N(s, "channels"), Tags(s)))
            .ToList();
        var format = root.GetProperty("format");
        var duration = double.Parse(S(format, "duration"), CultureInfo.InvariantCulture);
        var chapters = root.TryGetProperty("chapters", out var c) ? c.GetArrayLength() : 0;
        return new Probed(duration, streams, Tags(format), chapters);
    }

    /// <summary>A hash of the packets of a file's first stream of <paramref name="type"/> ("a" or "v"): equal when copied.</summary>
    private static async Task<string> StreamHashAsync(string path, string type)
    {
        var result = await FfmpegAsync(["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", $"0:{type}:0", "-c", "copy",
            "-f", "streamhash", "-hash", "md5", "-"]);
        return result.StandardOutput.Split('=', 2)[^1].Trim();
    }

    /// <summary>The MD5 of every video packet of a file, in order.</summary>
    private static async Task<List<string>> PacketHashesAsync(string path)
    {
        var result = await FfmpegAsync(["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "framemd5", "-"]);
        return [.. result.StandardOutput.Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim())];
    }

    // ───────────────────────── Merge videos ─────────────────────────

    [Fact]
    public async Task Clips_from_one_camera_are_merged_without_an_encode()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        string[] h264 = ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"];
        var first = await MakeClipAsync(folder, "a.mp4", 2, extra: h264);
        var second = await MakeClipAsync(folder, "b.mp4", 1.5, extra: h264);

        var output = await RunAsync([first, second], BuiltIn("merge-videos"));

        Assert.Equal(Path.Combine(folder, "a_merged.mp4"), output);
        var probed = await ProbeAsync(output);
        Assert.InRange(probed.Duration, 3.3, 3.7);
        // Copied, not encoded again: the merged file starts with the first clip's packets, byte for byte (FFmpeg puts
        // the stream's parameter sets into each clip's first packet, so that one is skipped).
        var firstPackets = await PacketHashesAsync(first);
        Assert.Equal(firstPackets.Skip(1), (await PacketHashesAsync(output)).Take(firstPackets.Count).Skip(1));
        Assert.Equal(("h264", 320, 240), (probed.Video!.Codec, probed.Video.Width, probed.Video.Height));
        Assert.Equal("aac", probed.Audio!.Codec);
    }

    [Fact]
    public async Task Clips_of_one_size_but_other_encoder_settings_still_play_after_merging()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        // Same codec, size and pixel format; another profile and header, as from two different phones.
        var first = await MakeClipAsync(folder, "a.mp4", 2, extra: ["-c:v", "libx264", "-profile:v", "high", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var second = await MakeClipAsync(folder, "b.mp4", 2, extra:
            ["-c:v", "libx264", "-profile:v", "baseline", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var output = await RunAsync([first, second], BuiltIn("merge-videos"));

        var decode = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", output, "-f", "null", "-"], TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(decode.ExitCode == 0 && decode.StandardError.Trim().Length == 0, decode.StandardError);
        Assert.InRange((await ProbeAsync(output)).Duration, 3.8, 4.2);
        // Not copied: the first clip's packets are encoded again.
        Assert.NotEqual((await PacketHashesAsync(first)).Skip(1).Take(10), (await PacketHashesAsync(output)).Skip(1).Take(10));
    }

    [Fact]
    public async Task A_join_that_fails_before_writing_keeps_the_file_it_was_to_overwrite()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var first = await MakeClipAsync(folder, "a.mp4", 1, extra: ["-c:v", "libx264", "-c:a", "aac"]);
        var broken = Path.Combine(folder, "b.mp4");
        await File.WriteAllTextAsync(broken, "not a video", TestContext.Current.CancellationToken);
        var existing = Path.Combine(folder, "a_merged.mp4");
        await File.WriteAllTextAsync(existing, "the user's own file", TestContext.Current.CancellationToken);
        var preset = BuiltIn("merge-videos").Clone();
        preset.Output.Conflict = ConflictPolicy.Overwrite;

        var job = await fx.ConvertAsync([first, broken], preset);

        Assert.Equal(JobState.Failed, job.State); // FFmpeg can't read b.mp4: the join stops while probing
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clips_of_other_sizes_and_without_sound_are_fitted_into_the_first_ones_frame()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var first = await MakeClipAsync(folder, "a.mp4", 2, extra: ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var wide = await MakeClipAsync(folder, "b.mkv", 1, size: "640x360", fps: 30, audio: false, extra: ["-c:v", "libx264"]);
        var webm = await MakeClipAsync(folder, "c.webm", 1, size: "160x120", extra: ["-c:v", "libvpx-vp9", "-c:a", "libopus"]);

        var output = await RunAsync([first, wide, webm], BuiltIn("merge-videos"));

        var probed = await ProbeAsync(output);
        Assert.InRange(probed.Duration, 3.8, 4.3);
        Assert.Equal(("h264", 320, 240), (probed.Video!.Codec, probed.Video.Width, probed.Video.Height));
        Assert.Equal("aac", probed.Audio!.Codec);
    }

    // ───────────────────────── Merge audio ─────────────────────────

    [Fact]
    public async Task Audio_files_are_joined_into_one_m4a_with_no_gap()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var wav = await MakeToneAsync(folder, "a.wav", 2);
        var mp3 = await MakeToneAsync(folder, "b.mp3", 1, channels: 1, rate: 22050);
        var clip = await MakeClipAsync(folder, "c.mp4", 1, extra: ["-c:v", "libx264", "-c:a", "aac"]);

        var output = await RunAsync([wav, mp3, clip], BuiltIn("merge-audio"));

        Assert.Equal(Path.Combine(folder, "a_merged.m4a"), output);
        var probed = await ProbeAsync(output);
        Assert.InRange(probed.Duration, 3.95, 4.08); // 2 + 1 + 1 seconds, nothing in between
        Assert.Null(probed.Video);
        Assert.Equal(("aac", 2), (probed.Audio!.Codec, probed.Audio.Channels));
    }

    // ───────────────────────── Video from images ─────────────────────────

    [Fact]
    public async Task Pictures_become_a_1080p_video_with_crossfades()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var pictures = new[]
        {
            EngineFixture.MakeImage(folder, "1.png", MagickFormat.Png, 320, 200),
            EngineFixture.MakeImage(folder, "2.jpg", MagickFormat.Jpeg, 200, 320),
            EngineFixture.MakeImage(folder, "3.webp", MagickFormat.WebP, 1000, 1000),
        };

        var output = await RunAsync(pictures, BuiltIn("video-from-images"));

        Assert.Equal(Path.Combine(folder, "1_video.mp4"), output);
        var probed = await ProbeAsync(output);
        Assert.InRange(probed.Duration, 6.9, 7.1); // 3 × 3 s, minus two 1 s fades
        Assert.Equal(("h264", 1920, 1080), (probed.Video!.Codec, probed.Video.Width, probed.Video.Height));
        Assert.Null(probed.Audio);
    }

    [Fact]
    public async Task Without_crossfades_each_picture_shows_for_its_whole_time()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var preset = BuiltIn("video-from-images");
        preset.Media.Crossfade = false;
        preset.Media.SlideSeconds = 1.5;

        var output = await RunAsync(
            [EngineFixture.MakeImage(folder, "1.png", MagickFormat.Png), EngineFixture.MakeImage(folder, "2.png", MagickFormat.Png)], preset);

        Assert.InRange((await ProbeAsync(output)).Duration, 2.9, 3.1);
    }

    // ───────────────────────── Extract audio ─────────────────────────

    [Fact]
    public async Task A_phone_videos_aac_sound_is_copied_into_m4a()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var clip = await MakeClipAsync(folder, "IMG_0001.mov", 2, extra: ["-c:v", "libx264", "-c:a", "aac", "-b:a", "96k"]);

        var output = await RunAsync([clip], BuiltIn("extract-audio"));

        Assert.Equal(Path.Combine(folder, "IMG_0001.m4a"), output);
        var probed = await ProbeAsync(output);
        Assert.Null(probed.Video);
        Assert.Equal("aac", probed.Audio!.Codec);
        Assert.Equal(await StreamHashAsync(clip, "a"), await StreamHashAsync(output, "a")); // the same packets
    }

    [Fact]
    public async Task Sound_m4a_cannot_hold_is_encoded_to_aac()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var clip = await MakeClipAsync(folder, "clip.webm", 1, extra: ["-c:v", "libvpx-vp9", "-c:a", "libopus"]);

        var probed = await ProbeAsync(await RunAsync([clip], BuiltIn("extract-audio")));

        Assert.Equal("aac", probed.Audio!.Codec);
    }

    // ───────────────────────── Remove metadata ─────────────────────────

    [Fact]
    public async Task A_video_loses_its_tags_and_chapters_and_keeps_its_streams()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var chapters = Path.Combine(folder, "chapters.txt");
        await File.WriteAllTextAsync(chapters, ";FFMETADATA1\ntitle=Secret trip\nartist=Jane Doe\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Arrival\n", TestContext.Current.CancellationToken);
        var clip = Path.Combine(folder, "trip.mp4");
        await FfmpegAsync(
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "ffmetadata", "-i", chapters, "-map", "0", "-map", "1", "-map_metadata", "2", "-map_chapters", "2",
            "-c:v", "libx264", "-c:a", "aac", "-metadata", "creation_time=2020-05-01T10:00:00Z",
            "-metadata", "location=+37.5665+126.9780/", "-metadata:s:a:0", "handler_name=Jane's phone", clip,
        ]);
        var before = await ProbeAsync(clip);
        Assert.Equal("Secret trip", before.Tags["title"]);
        Assert.Equal(1, before.Chapters);

        var output = await RunAsync([clip], BuiltIn("remove-metadata"));

        Assert.Equal(Path.Combine(folder, "trip_clean.mp4"), output);
        var after = await ProbeAsync(output);
        foreach (var tag in new[] { "title", "artist", "creation_time", "location", "comment" })
            Assert.False(after.Tags.ContainsKey(tag), tag);
        Assert.DoesNotContain(after.Streams, s => s.Tags.Values.Any(v => v.Contains("Jane", StringComparison.Ordinal)));
        Assert.Equal(0, after.Chapters);
        var bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Secret trip", bytes);
        Assert.DoesNotContain("+37.5665", bytes);
        Assert.Equal(await StreamHashAsync(clip, "v"), await StreamHashAsync(output, "v"));
        Assert.Equal(await StreamHashAsync(clip, "a"), await StreamHashAsync(output, "a"));
    }

    [Fact]
    public async Task A_portrait_phone_video_stays_upright()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var plain = await MakeClipAsync(folder, "plain.mov", 1, audio: false, extra: ["-c:v", "libx264"]);
        var clip = Path.Combine(folder, "IMG_0003.mov");
        // Phones store portrait video landscape, with a display matrix that turns it 90° (set on a copy: an encode would
        // turn the pixels instead).
        await FfmpegAsync(["-hide_banner", "-nostdin", "-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy",
            "-metadata", "location=+37.5+127.0/", clip]);
        Assert.Equal(90, Math.Abs((await MediaInfo.ProbeAsync(Tools!.Value.Ffprobe, clip, CancellationToken.None)).Video!.Rotation));

        var output = await RunAsync([clip], BuiltIn("remove-metadata"));

        Assert.Equal(90, Math.Abs((await MediaInfo.ProbeAsync(Tools!.Value.Ffprobe, output, CancellationToken.None)).Video!.Rotation));
        Assert.False((await ProbeAsync(output)).Tags.ContainsKey("location"));
    }

    [Fact]
    public async Task An_mkv_keeps_its_subtitle_fonts()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var font = Path.Combine(folder, "Sub Font.ttf");
        await File.WriteAllBytesAsync(font, Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray(), TestContext.Current.CancellationToken);
        var clip = await MakeClipAsync(folder, "episode.mkv", 1, extra:
            ["-c:v", "libx264", "-c:a", "aac", "-metadata", "title=Episode 1", "-attach", font,
             "-metadata:s:t", "mimetype=application/x-truetype-font"]);

        var after = await ProbeAsync(await RunAsync([clip], BuiltIn("remove-metadata")));

        Assert.EndsWith("Sub Font.ttf", Assert.Single(after.Streams, s => s.Type == "attachment").Tags["filename"]);
        Assert.False(after.Tags.ContainsKey("title"));
    }

    [Fact]
    public async Task A_song_loses_its_tags_and_stays_an_mp3()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var song = await MakeToneAsync(folder, "song.mp3", 2, extra:
            ["-metadata", "title=Private song", "-metadata", "artist=Jane Doe", "-metadata", "date=2019"]);

        var output = await RunAsync([song], BuiltIn("remove-metadata"));

        Assert.Equal(".mp3", Path.GetExtension(output));
        var after = await ProbeAsync(output);
        Assert.Equal("mp3", after.Audio!.Codec);
        Assert.False(after.Tags.ContainsKey("title"));
        Assert.False(after.Tags.ContainsKey("artist"));
        Assert.DoesNotContain("Jane Doe", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken)));
        Assert.Equal(await StreamHashAsync(song, "a"), await StreamHashAsync(output, "a"));
    }

    [Fact]
    public async Task A_photo_loses_its_exif_and_keeps_its_pixels_and_quality()
    {
        var folder = fx.NewFolder();
        var photo = Path.Combine(folder, "IMG_0002.jpg");
        using (var image = new MagickImage("gradient:#7F77DD-#1D9E75", 400, 300))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Filee Camera");
            exif.SetValue(ExifTag.DateTimeOriginal, "2020:05:01 10:00:00");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            image.SetProfile(exif);
            image.Comment = "Jane's holiday";
            image.Quality = 80;
            image.Write(photo, MagickFormat.Jpeg);
        }

        var output = await RunAsync([photo], BuiltIn("remove-metadata"));

        Assert.Equal(Path.Combine(folder, "IMG_0002_clean.jpg"), output);
        using var clean = new MagickImage(output);
        Assert.Null(clean.GetExifProfile());
        Assert.Null(clean.GetXmpProfile());
        Assert.True(string.IsNullOrEmpty(clean.Comment));
        Assert.Equal((400u, 300u), (clean.Width, clean.Height));
        Assert.InRange(clean.Quality, 78u, 82u); // saved at the photo's own quality, not above it
        var bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Filee Camera", bytes);
        Assert.DoesNotContain("Jane", bytes);
    }

    [Fact]
    public async Task A_pdf_loses_its_author_and_title_and_keeps_its_pages()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "contract.pdf");
        using (var document = new PdfDocument())
        {
            document.Info.Author = "Jane Doe";
            document.Info.Title = "Secret contract";
            document.Info.Keywords = "confidential";
            document.AddPage();
            document.AddPage();
            document.Save(path);
        }

        var output = await RunAsync([path], BuiltIn("remove-metadata"));

        Assert.Equal(Path.Combine(folder, "contract_clean.pdf"), output);
        using (var clean = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, clean.PageCount);
            Assert.True(string.IsNullOrEmpty(clean.Info.Author));
            Assert.True(string.IsNullOrEmpty(clean.Info.Title));
        }
        var bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Jane Doe", bytes);
        Assert.DoesNotContain("Secret contract", bytes);
        Assert.DoesNotContain("confidential", bytes);
    }

    // ───────────────────────── Compress ─────────────────────────

    [Fact]
    public async Task Compress_audio_saves_a_smaller_aac_copy()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var wav = await MakeToneAsync(folder, "talk.wav", 5);

        var output = await RunAsync([wav], BuiltIn("compress-audio"));

        Assert.Equal(Path.Combine(folder, "talk_compressed.m4a"), output);
        Assert.Equal("aac", (await ProbeAsync(output)).Audio!.Codec);
        Assert.True(new FileInfo(output).Length < new FileInfo(wav).Length / 5);
    }

    [Fact]
    public async Task Compress_video_saves_a_1080p_h264_mp4()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var clip = await MakeClipAsync(folder, "big.mov", 1, size: "2560x1440", extra: ["-c:v", "libx264", "-crf", "10", "-c:a", "aac"]);

        var output = await RunAsync([clip], BuiltIn("compress-video"));

        Assert.Equal(Path.Combine(folder, "big_compressed.mp4"), output);
        var probed = await ProbeAsync(output);
        Assert.Equal(("h264", 1920, 1080), (probed.Video!.Codec, probed.Video.Width, probed.Video.Height));
        Assert.True(new FileInfo(output).Length < new FileInfo(clip).Length);
    }

    [Fact]
    public async Task Compress_image_saves_a_smaller_png_with_the_same_pixels()
    {
        var folder = fx.NewFolder();
        var png = Path.Combine(folder, "screen.png");
        using (var image = new MagickImage("plasma:fractal", 300, 200))
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            image.Write(png, MagickFormat.Png);
        }

        var output = await RunAsync([png], BuiltIn("compress-image"));

        Assert.Equal(Path.Combine(folder, "screen_compressed.png"), output);
        Assert.True(new FileInfo(output).Length < new FileInfo(png).Length);
        using var original = new MagickImage(png);
        using var compressed = new MagickImage(output);
        Assert.Equal(0, original.Compare(compressed, ErrorMetric.Absolute));
    }
}
