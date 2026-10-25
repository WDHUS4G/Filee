// Command lines of the everyday media tools (no FFmpeg needed): videos and audio files joined into one, pictures as a
// slideshow, a video's sound copied into M4A, and a copy of a file without its tags.

using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class MediaJoiningTests
{
    private static readonly VideoStreamInfo PhoneVideo = new(1920, 1080, 1, 0, 30, "h264", "yuv420p", HeaderHash: "MD5:1");

    private static MediaInfo Clip(VideoStreamInfo? video = null, AudioStreamInfo? audio = null, double seconds = 10) =>
        new(TimeSpan.FromSeconds(seconds), video, audio, null);

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    [Fact]
    public void Clips_from_one_phone_are_copied()
    {
        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"));
        Assert.True(MediaJoining.CanCopyVideos([clip, clip], new MediaOptions()));

        var args = MediaJoining.CopyVideos("clips.txt", "out.mp4", clip);
        Assert.Equal("concat", After(args, "-f"));
        Assert.Equal("copy", After(args, "-c"));
        Assert.Contains("0:a:0", args);
    }

    [Fact]
    public void Clips_of_another_size_codec_or_sound_are_encoded()
    {
        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"));
        var options = new MediaOptions();
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { Width = 1280, Height = 720 } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { Codec = "hevc" } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Audio = new AudioStreamInfo(2, 44100, "aac") }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Audio = null }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip], new MediaOptions { Quality = MediaQuality.Small }));
        // Same size and codec from another encoder (other parameter sets), another frame rate, or no setup data to compare.
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { HeaderHash = "MD5:2" } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { FrameRate = 60 } }], options));
        var unknown = clip with { Video = PhoneVideo with { HeaderHash = null } };
        Assert.False(MediaJoining.CanCopyVideos([unknown, unknown], options));
    }

    [Fact]
    public void Encoded_clips_fit_the_first_ones_frame_and_a_silent_one_gets_silence()
    {
        var portrait = PhoneVideo with { Rotation = -90 };
        var withSound = Clip(portrait, new AudioStreamInfo(2, 44100, "aac"));
        var silent = Clip(PhoneVideo, seconds: 4.5);

        var args = MediaJoining.EncodeVideos([("a.mov", withSound), ("b.mp4", silent)], "out.mp4", new MediaOptions(), "libx264", "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("[0:V:0]scale=1080:1920:force_original_aspect_ratio=decrease", graph); // the first clip stands upright
        Assert.Contains("pad=1080:1920", graph);
        Assert.Contains("aevalsrc=0:c=stereo:s=48000:d=4.5", graph);
        Assert.Contains("concat=n=2:v=1:a=1[v][a]", graph);
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void Clips_without_any_sound_join_without_an_audio_track()
    {
        var args = MediaJoining.EncodeVideos([("a.mp4", Clip(PhoneVideo)), ("b.mp4", Clip(PhoneVideo))], "out.mp4", new MediaOptions(),
            "libx264", "work");

        Assert.Contains("concat=n=2:v=1:a=0[v]", After(args, "-filter_complex"));
        Assert.Contains("-an", args);
    }

    [Fact]
    public void A_frame_never_gets_taller_than_the_preset_allows()
    {
        var uhd = PhoneVideo with { Width = 3840, Height = 2160 };
        Assert.Equal((1920, 1080), MediaJoining.JoinFrame(uhd, 1080));
        Assert.Equal((3840, 2160), MediaJoining.JoinFrame(uhd, 0));
    }

    [Fact]
    public void Audio_is_decoded_and_joined_without_gaps()
    {
        var song = Clip(audio: new AudioStreamInfo(2, 44100, "mp3"));
        var memo = Clip(audio: new AudioStreamInfo(1, 16000, "aac"));

        var args = MediaJoining.EncodeAudio([("a.mp3", song), ("b.m4a", memo)], "out.m4a", new MediaOptions(), "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("[0:a:0]aresample=48000", graph);
        Assert.Contains("channel_layouts=stereo", graph);
        Assert.Contains("concat=n=2:v=0:a=1[a]", graph);
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("192k", After(args, "-b:a"));
        Assert.Equal("ipod", After(args, "-f"));
    }

    [Fact]
    public void Mono_recordings_stay_mono_and_apple_lossless_is_kept()
    {
        var memo = Clip(audio: new AudioStreamInfo(1, 16000, "aac"));
        var mono = MediaJoining.EncodeAudio([("a.m4a", memo), ("b.m4a", memo)], "out.m4a", new MediaOptions(), "work");
        Assert.Contains("channel_layouts=mono", After(mono, "-filter_complex"));
        Assert.Equal("96k", After(mono, "-b:a"));

        var alac = MediaJoining.EncodeAudio([("a.m4a", memo)], "out.m4a", new MediaOptions { AudioCodec = AudioCodec.Alac }, "work");
        Assert.Equal("alac", After(alac, "-c:a"));
    }

    [Fact]
    public void Slides_fade_into_each_other_at_the_right_times()
    {
        var options = new MediaOptions { Slideshow = true, SlideSeconds = 3 };

        var args = MediaJoining.Slideshow(["1.jpg", "2.jpg", "3.jpg"], "out.mp4", options, "libx264", "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("tpad=stop_mode=clone:stop_duration=3,trim=duration=3", graph);
        Assert.Contains("[s0][s1]xfade=transition=fade:duration=1:offset=2[x1]", graph);
        Assert.Contains("[x1][s2]xfade=transition=fade:duration=1:offset=4[out]", graph);
        Assert.Equal(TimeSpan.FromSeconds(7), MediaJoining.SlideshowDuration(3, options));
        Assert.Contains("-an", args);
    }

    [Fact]
    public void Without_crossfades_the_slides_are_cut()
    {
        var options = new MediaOptions { Slideshow = true, SlideSeconds = 2, Crossfade = false };

        var graph = After(MediaJoining.Slideshow(["1.jpg", "2.jpg"], "out.mp4", options, "libx264", "work"), "-filter_complex");

        Assert.Contains("[s0][s1]concat=n=2:v=1:a=0[out]", graph);
        Assert.DoesNotContain("xfade", graph);
        Assert.Equal(TimeSpan.FromSeconds(4), MediaJoining.SlideshowDuration(2, options));
    }

    [Fact]
    public void A_picture_is_fitted_into_the_1080p_frame()
    {
        var args = MediaJoining.PrepareSlide("photo.png", "slide.jpg", 1920, 1080);

        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,pad=1920:1080",
            After(args, "-vf"));
        Assert.Equal("mjpeg", After(args, "-c:v"));
    }

    [Fact]
    public void A_long_graph_goes_into_a_file()
    {
        var work = Path.Combine(Path.GetTempPath(), "filee-tests", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var slides = Enumerable.Range(1, 100).Select(i => $"{i}.jpg").ToList();
            var args = MediaJoining.Slideshow(slides, "out.mp4", new MediaOptions { Slideshow = true }, "libx264", work);

            Assert.DoesNotContain("-filter_complex", args);
            var file = After(args, "-/filter_complex");
            Assert.Contains("[x98][s99]xfade", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Concat_lists_quote_every_path()
    {
        var list = MediaJoining.ConcatList(["it's.mp4"]);
        Assert.StartsWith("file '", list);
        Assert.EndsWith(@"it'\''s.mp4'" + "\n", list);
    }

    [Fact]
    public void Extract_audio_copies_aac_into_m4a_and_encodes_what_it_cannot_copy()
    {
        var copy = new MediaOptions { CopyAudioWhenPossible = true };
        var aac = new AudioStreamInfo(2, 48000, "aac");
        Assert.True(MediaEncoding.CanCopyIntoAudio("m4a", aac, copy));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", new AudioStreamInfo(2, 48000, "opus"), copy));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions())); // only when the preset asks
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions { CopyAudioWhenPossible = true, AudioBitrateKbps = 96 }));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions { CopyAudioWhenPossible = true, AudioCodec = AudioCodec.Alac }));

        var phone = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo, aac, null);
        var args = MediaEncoding.Plan("in.mov", "out.m4a", "m4a", copy, phone, "work").Single().Arguments;
        Assert.Equal("copy", After(args, "-c:a"));
    }

    [Fact]
    public void Remove_metadata_copies_the_streams_without_tags_chapters_or_data_tracks()
    {
        var phone = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo with { Codec = "hevc" }, new AudioStreamInfo(2, 48000, "aac"), null);

        var args = MediaEncoding.WithoutMetadata("in.mov", "out.mov", "mov", phone);

        Assert.Equal("copy", After(args, "-c"));
        Assert.Equal("-1", After(args, "-map_metadata"));
        Assert.Equal("-1", After(args, "-map_metadata:s"));
        Assert.Equal("-1", After(args, "-map_chapters"));
        Assert.Contains("0:V?", args);
        Assert.DoesNotContain("0", args.Where((a, i) => i > 0 && args[i - 1] == "-map")); // never every stream (data tracks)
        Assert.Equal("hvc1", After(args, "-tag:v"));
        Assert.Equal("mov", After(args, "-f"));
        Assert.DoesNotContain("0:t", args); // FFmpeg refuses an attachment specifier that matches nothing

        var episode = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo, null, null, HasAttachments: true);
        var mkv = MediaEncoding.WithoutMetadata("in.mkv", "out.mkv", "mkv", episode);
        Assert.Contains("0:t", mkv); // subtitle fonts stay, with their file name and type
        Assert.Equal("0:s:t", After(mkv, "-map_metadata:s:t"));

        var song = new MediaInfo(TimeSpan.FromSeconds(10), null, new AudioStreamInfo(2, 44100, "mp3"), 1);
        var audio = MediaEncoding.WithoutMetadata("in.mp3", "out.mp3", "mp3", song);
        Assert.Contains("0:v?", audio); // the album art stays
        Assert.Equal("mp3", After(audio, "-f"));
    }
}
