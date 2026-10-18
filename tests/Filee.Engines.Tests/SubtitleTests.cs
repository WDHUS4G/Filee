// Text subtitles with the built-in subtitle engine: every reader and writer, Korean CP949 / EUC-KR detection, and
// subtitle extraction from videos with FFmpeg (only when Filee's FFmpeg is installed). Inputs are written by the tests.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Subtitles;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class SubtitleTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly Encoding Cp949;
    private static readonly Encoding Gbk;

    static SubtitleTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
        Gbk = Encoding.GetEncoding(936);
    }

    private static SubtitleDocument Read(string format, string text) => SubtitleReader.Read(format, new UTF8Encoding(false).GetBytes(text));

    private static string Write(SubtitleDocument document, string format)
    {
        var bytes = SubtitleWriter.Write(document, format);
        return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("srt", "smi")]
    [InlineData("smi", "srt")]
    [InlineData("smi", "smi")] // CP949 → UTF-8
    [InlineData("ass", "vtt")]
    [InlineData("ssa", "srt")]
    [InlineData("vtt", "ttml")]
    [InlineData("lrc", "srt")]
    [InlineData("ttml", "srt")]
    [InlineData("sub", "srt")]
    [InlineData("scc", "vtt")]
    public void Subtitles_convert_in_one_built_in_step(string from, string to)
    {
        var route = fx.Catalog.CreatePlanner([]).Plan(from, to);
        Assert.NotNull(route);
        Assert.Equal("subtitle", Assert.Single(route.Steps).Converter.Id);
    }

    [Fact]
    public void Video_subtitles_come_out_through_ffmpeg_and_srt()
    {
        var planner = fx.Catalog.CreatePlanner(["ffmpeg"]);
        Assert.Equal([("ffmpeg", "mkv", "srt")], planner.Plan("mkv", "srt")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
        Assert.Equal([("ffmpeg", "mp4", "ass")], planner.Plan("mp4", "ass")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
        Assert.Equal([("ffmpeg", "mkv", "srt"), ("subtitle", "srt", "smi")],
            planner.Plan("mkv", "smi")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
        // Subtitles never become video, and audio has no subtitles.
        Assert.Null(planner.Plan("srt", "mp4"));
        Assert.Null(planner.Plan("mp3", "srt"));
    }

    // ───────────────────────── Encodings ─────────────────────────

    [Fact]
    public void Korean_cp949_text_is_recognised_and_other_code_pages_are_not_taken_for_it()
    {
        const string korean = "안녕하세요. 오늘 날씨가 좋네요! 똠방각하";
        Assert.True(SubtitleEncoding.LooksKorean(Cp949.GetBytes(korean)));
        Assert.Equal(korean, SubtitleEncoding.Decode(Cp949.GetBytes(korean), ansiCodePage: 1252));
        Assert.Equal(korean, SubtitleEncoding.Decode(Cp949.GetBytes(korean), ansiCodePage: 936)); // also on Chinese Windows

        const string chinese = "我是一个学生，这是我的字幕文件。你好吗？";
        Assert.False(SubtitleEncoding.LooksKorean(Gbk.GetBytes(chinese)));
        Assert.Equal(chinese, SubtitleEncoding.Decode(Gbk.GetBytes(chinese), ansiCodePage: 936));

        const string french = "Café, déjà vu à l'été";
        var latin1 = Encoding.GetEncoding(1252).GetBytes(french);
        Assert.False(SubtitleEncoding.LooksKorean(latin1));
        Assert.Equal(french, SubtitleEncoding.Decode(latin1, ansiCodePage: 1252));
        Assert.Equal(french, SubtitleEncoding.Decode(latin1, ansiCodePage: 949)); // Korean Windows: still Western

        // Byte order marks and UTF-16.
        Assert.Equal("가나", SubtitleEncoding.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("가나")]));
        Assert.Equal("가나", SubtitleEncoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("가나")]));
        Assert.Equal("Hi there", SubtitleEncoding.Decode(Encoding.Unicode.GetBytes("Hi there")));
    }

    [Fact]
    public async Task A_cp949_sami_file_becomes_a_utf8_srt_with_the_korean_class()
    {
        var folder = fx.NewFolder();
        var smi = Path.Combine(folder, "영화.smi");
        // As Korean SAMI files usually are: CP949, unquoted attributes, unclosed tags, two languages, &nbsp; clears.
        File.WriteAllBytes(smi, Cp949.GetBytes("""
            <SAMI>
            <HEAD>
            <TITLE>영화 자막</TITLE>
            <STYLE TYPE="text/css">
            <!--
            P { margin-left:8pt; margin-right:8pt; text-align:center; }
            .KRCC { Name:한국어; lang:ko-KR; SAMIType:CC; }
            .ENCC { Name:English; lang:en-US; SAMIType:CC; }
            -->
            </STYLE>
            </HEAD>
            <BODY>
            <SYNC Start=1000><P Class=KRCC>안녕하세요,<br><font color="#ffff00">반가워요</font>
            <SYNC Start=1000><P Class=ENCC>Hello
            <SYNC Start=2500><P Class=KRCC>&nbsp;
            <SYNC Start=3000><P Class=KRCC><i>두 번째
            줄입니다</i>
            <SYNC Start=4200><P Class=KRCC>&nbsp
            <SYNC Start=5000><P Class=KRCC>세 번째 &amp; 마지막
            </BODY>
            </SAMI>
            """));

        var job = await fx.ConvertAsync([smi], new Preset { TargetFormat = "srt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var bytes = File.ReadAllBytes(job.Outputs.Single());
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var srt = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("""
            1
            00:00:01,000 --> 00:00:02,500
            안녕하세요,
            <font color="#FFFF00">반가워요</font>

            2
            00:00:03,000 --> 00:00:04,200
            <i>두 번째 줄입니다</i>

            3
            00:00:05,000 --> 00:00:09,000
            세 번째 & 마지막


            """.ReplaceLineEndings("\r\n"), srt);
    }

    // ───────────────────────── Readers ─────────────────────────

    [Fact]
    public void Srt_is_read_leniently()
    {
        // A blank line inside a cue, a missing blank line, a dot before the milliseconds, a {\an8} code, stray tags.
        var document = Read("srt", "﻿1\r\n00:00:01,000 --> 00:00:02,000\r\n<i>First</i> line\r\n\r\nstill the first\r\n\r\n2\r\n" +
                                   "00:00:03.5 --> 00:00:04,250\r\n{\\an8}<b>Second</b> <span>cue</span>\r\n3\r\n00:00:05,000 --> 00:00:06,000\r\nThird 2024\r\n");
        Assert.Equal(
        [
            new SubtitleCue(Ms(1000), Ms(2000), "<i>First</i> line\nstill the first"),
            new SubtitleCue(Ms(3500), Ms(4250), "<b>Second</b> cue"),
            new SubtitleCue(Ms(5000), Ms(6000), "Third 2024"),
        ], document.Cues);
    }

    [Fact]
    public void WebVtt_cues_settings_entities_and_voices_are_read()
    {
        var document = Read("vtt", """
            WEBVTT
            Language: ko

            NOTE This is a comment
            with two lines

            STYLE
            ::cue { color: yellow }

            intro
            00:01.000 --> 00:02.500 align:start position:10%
            <v Mary>Hi &amp; <i>welcome</i></v>
            <c.yellow>&lt;3</c>

            01:00:00.000 --> 01:00:01.000
            <00:00:01.500>Karaoke
            """);
        Assert.Equal("ko", document.Language);
        Assert.Equal(
        [
            new SubtitleCue(Ms(1000), Ms(2500), "Hi & <i>welcome</i>\n<font color=\"#FFFF00\"><3</font>"),
            new SubtitleCue(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + Ms(1000), "Karaoke"),
        ], document.Cues);
    }

    [Fact]
    public void Ass_dialogue_override_codes_and_styles_are_read()
    {
        var document = Read("ass", """
            [Script Info]
            Title: Test show
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1
            Style: Thoughts,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,-1,0,0,100,100,0,0,1,2,2,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.00,0:00:03.50,Default,,0,0,0,,{\an8\pos(10,10)}Hello, {\i1}world{\i0}!\NSecond {\c&H00FFFF&}yellow{\c} line
            Comment: 0,0:00:02.00,0:00:03.50,Default,,0,0,0,,Not shown
            Dialogue: 0,0:00:01.00,0:00:01.50,Thoughts,,0,0,0,,I think\h so
            Dialogue: 1,0:00:01.00,0:00:01.50,Thoughts,,0,0,0,,I think\h so
            Dialogue: 0,0:00:04.00,0:00:05.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100 0 100{\p0}
            """);
        Assert.Equal("Test show", document.Title);
        Assert.Equal(
        [
            new SubtitleCue(Ms(1000), Ms(1500), "<i>I think so</i>"), // the italic style, shadow layer merged
            new SubtitleCue(Ms(2000), Ms(3500), "Hello, <i>world</i>!\nSecond <font color=\"#FFFF00\">yellow</font> line"),
        ], document.Cues);
    }

    [Fact]
    public void Lrc_lines_last_until_the_next_one()
    {
        var document = Read("lrc", """
            [ti:Song]
            [ar:Singer]
            [offset:+500]
            [00:10.00]First <00:10.50>line
            [00:12.30][00:20.00]Chorus
            [00:15.00]
            [00:25.5]Last
            """);
        Assert.Equal("Song", document.Title);
        Assert.Equal(
        [
            new SubtitleCue(Ms(9500), Ms(11800), "First line"),
            new SubtitleCue(Ms(11800), Ms(14500), "Chorus"),
            new SubtitleCue(Ms(19500), Ms(25000), "Chorus"),
            new SubtitleCue(Ms(25000), Ms(29000), "Last"),
        ], document.Cues);
    }

    [Fact]
    public void Ttml_times_spans_and_styles_are_read()
    {
        var document = Read("ttml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling"
                xmlns:ttp="http://www.w3.org/ns/ttml#parameter" xml:lang="en" ttp:frameRate="25" ttp:tickRate="10000000">
              <head><styling><style xml:id="it" tts:fontStyle="italic"/></styling></head>
              <body>
                <div begin="10s">
                  <p begin="00:00:01.000" end="00:00:02.000">Line one<br/>line <span tts:fontWeight="bold">two</span></p>
                  <p begin="00:00:03:05" dur="25f" style="it">Frames</p>
                  <p begin="50000000t" end="60000000t"><span tts:color="#ff0000">Red</span> ticks</p>
                </div>
              </body>
            </tt>
            """);
        Assert.Equal("en", document.Language);
        Assert.Equal(
        [
            new SubtitleCue(Ms(11000), Ms(12000), "Line one\nline <b>two</b>"),
            new SubtitleCue(Ms(13200), Ms(14200), "<i>Frames</i>"),
            new SubtitleCue(Ms(15000), Ms(16000), "<font color=\"#FF0000\">Red</font> ticks"),
        ], document.Cues);
    }

    [Fact]
    public void MicroDvd_frames_use_the_rate_in_the_first_line()
    {
        var document = Read("sub", "{1}{1}25\n{25}{50}{y:i}Hello|world\n{75}{100}{c:$0000FF}Red\n");
        Assert.Equal(
        [
            new SubtitleCue(Ms(1000), Ms(2000), "<i>Hello</i>\nworld"),
            new SubtitleCue(Ms(3000), Ms(4000), "<font color=\"#FF0000\">Red</font>"),
        ], document.Cues);

        var vobsub = Assert.Throws<InvalidDataException>(() => SubtitleReader.Read("sub", [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00]));
        Assert.Contains("VobSub", vobsub.Message);
        Assert.Contains("MicroDVD", Assert.Throws<InvalidDataException>(() => Read("sub", "[INFORMATION]\n00:00:01.00,00:00:02.00\nSubViewer")).Message);
    }

    [Fact]
    public void Scc_pop_on_and_roll_up_captions_are_decoded()
    {
        // Pop-on: load "Hello" (with parity bits) and "Café" off screen, show at End Of Caption, erase two seconds later.
        // Then roll-up with two rows. Control codes are sent twice, as broadcasters do.
        var document = Read("scc", """
            Scenarist_SCC V1.0

            00:00:01:00	9420 9420 94ae 94ae 9452 9452 c8e5 ecec ef80 9470 9470 4361 665c 942f 942f

            00:00:03:00	942c 942c

            00:00:05:00	9425 9425 9470 9470 4f6e 6521 94ad 94ad 5477 6f21

            00:00:07:00	942c 942c
            """);
        var frame = 1001.0 / 30000;
        Assert.Equal(3, document.Cues.Count);
        Assert.Equal("Hello\nCafé", document.Cues[0].Text);
        Assert.Equal(30 * frame + 13 * frame, document.Cues[0].Start.TotalSeconds, 3); // the End Of Caption pair
        Assert.Equal(90 * frame, document.Cues[0].End.TotalSeconds, 3);
        Assert.Equal("One!", document.Cues[1].Text);
        Assert.Equal("One!\nTwo!", document.Cues[2].Text); // the first row scrolled up
        Assert.Equal(210 * frame, document.Cues[^1].End.TotalSeconds, 3);
        Assert.Contains("SCC", Assert.Throws<InvalidDataException>(() => Read("scc", "00:00:01:00 9420")).Message);
    }

    [Fact]
    public void Empty_files_fail_with_a_clear_message() =>
        Assert.Contains("No subtitles", Assert.Throws<InvalidDataException>(() => Read("srt", "\n\n")).Message);

    // ───────────────────────── Writers ─────────────────────────

    private static SubtitleDocument Sample()
    {
        var document = new SubtitleDocument { Title = "Sample" };
        document.Add(Ms(1000), Ms(2500), "<i>Hello</i> & <b>bye</b>\n<font color=\"#FFFF00\">yellow</font> 1 < 2");
        document.Add(Ms(2500), Ms(4000), "다음 줄");
        document.Add(Ms(6000), Ms(7000), "Last one");
        return document.Finish();
    }

    [Fact]
    public void Every_format_writes_the_cues_with_its_own_markup()
    {
        var document = Sample();
        Assert.Contains("00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> & <b>bye</b>\r\n<font color=\"#FFFF00\">yellow</font> 1 < 2", Write(document, "srt"));

        var vtt = Write(document, "vtt");
        Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n<i>Hello</i> &amp; <b>bye</b>\n<c.yellow>yellow</c> 1 &lt; 2\n\n", vtt);

        var ass = Write(document, "ass");
        Assert.Contains("Title: Sample", ass);
        Assert.Contains(@"Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\i1}Hello{\i0} & {\b1}bye{\b0}\N{\c&H00FFFF&}yellow{\c} 1 < 2", ass);
        Assert.Equal("a (b)", SubtitleWriter.AssText("a {b}")); // a brace would start an override block

        var sami = Write(document, "smi");
        Assert.Contains(".KRCC { Name: Korean; lang: ko-KR; SAMIType: CC; }", sami);
        Assert.Contains("<SYNC Start=1000><P Class=KRCC><i>Hello</i> &amp; <b>bye</b><br><font color=\"#FFFF00\">yellow</font> 1 &lt; 2\r\n<SYNC Start=2500>", sami);
        Assert.Contains("<SYNC Start=4000><P Class=KRCC>&nbsp;\r\n<SYNC Start=6000>", sami); // gap: the screen is cleared

        Assert.Equal("[ti:Sample]\r\n[00:01.00]Hello & bye yellow 1 < 2\r\n[00:02.50]다음 줄\r\n[00:04.00]\r\n[00:06.00]Last one\r\n[00:07.00]\r\n",
            Write(document, "lrc"));

        var ttml = Write(document, "ttml");
        Assert.Contains("""<p begin="00:00:01.000" end="00:00:02.500"><span tts:fontStyle="italic">Hello</span> &amp; <span tts:fontWeight="bold">bye</span><br /><span tts:color="#FFFF00">yellow</span> 1 &lt; 2</p>""", ttml);

        Assert.StartsWith("{1}{1}23.976\r\n{24}{60}Hello & bye|yellow 1 < 2\r\n", Write(document, "sub"));
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("ass")]
    [InlineData("smi")]
    [InlineData("ttml")]
    public void Formats_with_styles_read_back_what_they_write(string format)
    {
        var document = Sample();
        var back = SubtitleReader.Read(format, SubtitleWriter.Write(document, format));
        // WebVTT keeps only the basic colours, which "yellow" is.
        Assert.Equal(document.Cues, back.Cues);
    }

    [Theory]
    [InlineData("lrc")]
    [InlineData("sub")]
    public void Plain_formats_read_back_the_text_and_times(string format)
    {
        var document = Sample();
        var back = SubtitleReader.Read(format, SubtitleWriter.Write(document, format));
        Assert.Equal(document.Cues.Select(c => SubtitleMarkup.PlainText(c.Text).Replace("\n", format == "lrc" ? " " : "\n")),
            back.Cues.Select(c => c.Text));
        var tolerance = format == "sub" ? 45 : 10; // a frame at 23.976 fps, a hundredth of a second
        Assert.All(document.Cues.Zip(back.Cues), pair => Assert.InRange(Math.Abs((pair.First.Start - pair.Second.Start).TotalMilliseconds), 0, tolerance));
    }

    // ───────────────────────── Extraction from video (FFmpeg) ─────────────────────────

    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static void SkipUnlessFfmpeg() =>
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

    /// <summary>A 2-second clip with the given subtitle file muxed in (or none).</summary>
    private static async Task<string> MakeVideoAsync(string folder, string name, string? subtitles, params string[] codec)
    {
        var path = Path.Combine(folder, name);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=2"];
        if (subtitles is not null)
            args.AddRange(["-i", subtitles, "-map", "0:v", "-map", "1:s"]);
        args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", .. codec, path]);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, args, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    [Fact]
    public async Task The_first_text_subtitle_track_of_a_video_is_extracted()
    {
        SkipUnlessFfmpeg();
        var folder = fx.NewFolder();
        var srt = Path.Combine(folder, "track.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\n<i>자막</i> 하나\n\n2\n00:00:01,600 --> 00:00:01,900\nTwo\n", new UTF8Encoding(false));
        var mkv = await MakeVideoAsync(folder, "movie.mkv", srt, "-c:s", "srt");
        var mp4 = await MakeVideoAsync(folder, "movie.mp4", srt, "-c:s", "mov_text");
        var silent = await MakeVideoAsync(folder, "plain.mp4", null);

        var job = await fx.ConvertAsync([mkv], new Preset { TargetFormat = "srt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var extracted = SubtitleReader.Read("srt", File.ReadAllBytes(job.Outputs.Single()));
        Assert.Equal([new SubtitleCue(Ms(500), Ms(1500), "<i>자막</i> 하나"), new SubtitleCue(Ms(1600), Ms(1900), "Two")], extracted.Cues);

        // MP4 timed text → WebVTT, and a video → SAMI through SRT.
        job = await fx.ConvertAsync([mp4], new Preset { TargetFormat = "vtt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Equal(["자막 하나", "Two"], SubtitleReader.Read("vtt", File.ReadAllBytes(job.Outputs.Single())).Cues.Select(c => SubtitleMarkup.PlainText(c.Text)));
        job = await fx.ConvertAsync([mkv], new Preset { TargetFormat = "smi" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Contains("<P Class=KRCC><i>자막</i> 하나", File.ReadAllText(job.Outputs.Single()));

        // A video without subtitles says so.
        job = await fx.ConvertAsync([silent], new Preset { TargetFormat = "srt" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no subtitle track", job.Files.Single().ErrorDetail);
        Assert.Empty(Directory.GetFiles(folder, "plain*.srt"));
    }

    [Fact]
    public void Picture_subtitles_are_refused_with_a_clear_message()
    {
        var info = new MediaInfo(TimeSpan.FromSeconds(1), new VideoStreamInfo(1920, 1080, 1, 0, 24), null, null,
            [new SubtitleStreamInfo(2, "hdmv_pgs_subtitle", "kor")]);
        var error = Assert.Throws<InvalidOperationException>(() => MediaEncoding.Plan("in.mkv", "out.srt", "srt", new MediaOptions(), info, "work"));
        Assert.Contains("pictures", error.Message);

        var both = info with { SubtitleStreams = [new SubtitleStreamInfo(2, "hdmv_pgs_subtitle", "kor"), new SubtitleStreamInfo(3, "ass", "eng")] };
        var args = MediaEncoding.Plan("in.mkv", "out.ass", "ass", new MediaOptions(), both, "work").Single().Arguments;
        Assert.Equal(["-map", "0:3", "-c:s", "copy", "-f", "ass"], args.SkipWhile(a => a != "-map").Take(6));
        args = MediaEncoding.Plan("in.mkv", "out.srt", "srt", new MediaOptions(), both, "work").Single().Arguments;
        Assert.Equal(["-map", "0:3", "-c:s", "srt", "-f", "srt"], args.SkipWhile(a => a != "-map").Take(6));
    }
}
