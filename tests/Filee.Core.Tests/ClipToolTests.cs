// "Cut a clip": the range kept inside the video, the times as shown and as FFmpeg reads them, the conversion the tool
// becomes, and the preset arriving in libraries from before it (settings schema 45).

using System.Globalization;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ClipToolTests
{
    // ───────────────────────── Times ─────────────────────────

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(7.5, "0:07.5")]
    [InlineData(7.54, "0:07.5")]
    [InlineData(7.55, "0:07.6")]
    [InlineData(59.96, "1:00.0")]
    [InlineData(245, "4:05.0")]
    [InlineData(599.9, "9:59.9")]
    [InlineData(3725.3, "1:02:05.3")]
    [InlineData(-3, "0:00.0")]
    [InlineData(double.NaN, "0:00.0")]
    public void Times_show_as_minutes_seconds_and_tenths(double seconds, string expected) =>
        Assert.Equal(expected, ClipRange.Display(seconds));

    [Theory]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("de")]
    public void Times_never_take_the_languages_decimal_comma(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("1:05.3", ClipRange.Display(65.3));
            Assert.Equal("65.300", ClipRange.Ffmpeg(65.3));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(0, "0.000")]
    [InlineData(12.3456, "12.346")]
    [InlineData(600, "600.000")]
    [InlineData(-1, "0.000")]
    [InlineData(double.PositiveInfinity, "0.000")]
    public void Ffmpeg_reads_seconds_with_three_decimals(double seconds, string expected) =>
        Assert.Equal(expected, ClipRange.Ffmpeg(seconds));

    // ───────────────────────── The range ─────────────────────────

    [Fact]
    public void A_clip_inside_the_video_stays_as_it_is() =>
        Assert.Equal(new ClipRange(12.5, 42.5), ClipRange.Clamp(12.5, 42.5, 600));

    [Fact]
    public void The_end_never_goes_past_the_video()
    {
        var clip = ClipRange.Clamp(580, 640, 600);
        Assert.Equal(new ClipRange(580, 600), clip);
        Assert.Equal(20, clip.Length);
    }

    [Fact]
    public void The_start_is_never_before_the_video_or_after_the_end()
    {
        Assert.Equal(new ClipRange(0, 10), ClipRange.Clamp(-5, 10, 600));
        // Given the wrong way round: swapped.
        Assert.Equal(new ClipRange(10, 30), ClipRange.Clamp(30, 10, 600));
    }

    [Fact]
    public void A_clip_is_never_shorter_than_the_least_length()
    {
        Assert.Equal(new ClipRange(5, 5 + ClipRange.MinLength), ClipRange.Clamp(5, 5, 600));
        // At the very end it grows backwards.
        var atEnd = ClipRange.Clamp(600, 600, 600);
        Assert.Equal(600, atEnd.End);
        Assert.Equal(600 - ClipRange.MinLength, atEnd.Start, 6);
        // Past the end altogether: the video's last moment.
        var past = ClipRange.Clamp(700, 800, 600);
        Assert.Equal(600, past.End);
        Assert.True(past.Start < past.End);
    }

    [Fact]
    public void Without_a_known_duration_only_the_start_is_kept_at_zero()
    {
        Assert.Equal(new ClipRange(0, 30), ClipRange.Clamp(-1, 30, null));
        Assert.Equal(new ClipRange(100, 130), ClipRange.Clamp(100, 130, 0));
        Assert.Equal(new ClipRange(4, 4 + ClipRange.MinLength), ClipRange.Clamp(4, double.NaN, null));
        Assert.Equal(new ClipRange(0, 600), ClipRange.Clamp(double.NaN, double.NaN, 600));
    }

    // ───────────────────────── The conversion ─────────────────────────

    private static Preset Tool() => BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");

    [Fact]
    public void The_built_in_tool_names_its_clips_name_clip_and_copies_by_default()
    {
        var tool = Tool();
        Assert.True(tool.IsTool);
        Assert.True(tool.IsClipTool);
        Assert.False(tool.IsCropTool);
        Assert.Equal("preset.clip", tool.NameKey);
        Assert.Equal("{name}_clip", tool.Output.FileNamePattern);
        Assert.False(tool.Media.ExactCut);
        Assert.Null(tool.Media.Clip);
        // The crop tool stays the crop, also as saved before tools had names.
        var crop = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
        Assert.True(crop.IsCropTool);
        Assert.False(crop.IsClipTool);
        Assert.True(new Preset { Kind = PresetKind.Tool, Tool = "crop" }.IsCropTool);
        // A tool from a newer Filee is neither.
        var unknown = new Preset { Kind = PresetKind.Tool, Tool = "stabilize" };
        Assert.False(unknown.IsCropTool || unknown.IsClipTool);
    }

    [Fact]
    public void The_tool_becomes_a_conversion_of_the_clip_into_the_target()
    {
        var tool = Tool();
        tool.Name = "Highlights";
        tool.Media.ExactCut = true;
        tool.Media.Quality = MediaQuality.Small;
        tool.Media.LivePhoto = LivePhotoKind.Apple; // a hand-edited file: never a Live Photo of the clip

        var conversion = ClipTool.ConversionOf(tool, "mkv", new ClipRange(12.5, 42.5));

        Assert.Equal(PresetKind.Convert, conversion.Kind);
        Assert.False(conversion.IsTool);
        Assert.Null(conversion.Tool);
        Assert.Equal("mkv", conversion.TargetFormat);
        var part = Assert.Single(conversion.Media.Clip!.Parts);
        Assert.Equal((12.5, 42.5), (part.Start, part.End));
        Assert.True(conversion.Media.ExactCut);
        Assert.Equal(MediaQuality.Small, conversion.Media.Quality);
        Assert.Equal(LivePhotoKind.None, conversion.Media.LivePhoto);
        Assert.Equal(("tool-clip", "Highlights", "{name}_clip"), (conversion.Id, conversion.Name, conversion.Output.FileNamePattern));
        Assert.True(conversion.CutsClipOf("mp4"));
        Assert.False(conversion.CutsClipOf("mp3")); // only videos
        // The tool itself is untouched.
        Assert.True(tool.IsClipTool);
        Assert.Null(tool.Media.Clip);
        Assert.False(new Preset { TargetFormat = "mp4" }.CutsClipOf("mp4"));
    }

    [Fact]
    public void A_clip_keeps_the_videos_format_where_one_engine_writes_it_else_mp4()
    {
        var videos = new FakeConverter("ffmpeg", new ConversionEdge("mp4", "mp4"), new ConversionEdge("mkv", "mkv"), new ConversionEdge("mkv", "mp4"),
            new ConversionEdge("rm", "mp4"));
        var planner = new ConverterCatalog([videos]).CreatePlanner();

        Assert.Equal("mp4", ClipTool.TargetFor("mp4", planner));
        Assert.Equal("mkv", ClipTool.TargetFor("mkv", planner));
        Assert.Equal("mp4", ClipTool.TargetFor("rm", planner)); // RealVideo can't be written
        Assert.True(ClipTool.CanCut("rm", planner));
        Assert.False(ClipTool.CanCut("mov", planner)); // no engine reads it here
        Assert.False(ClipTool.CanCut("mp3", planner)); // not a video
    }

    [Fact]
    public void Clip_options_survive_saving_and_the_times_never_reach_a_saved_tool()
    {
        var tool = Tool();
        tool.Media.ExactCut = true;
        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"tool\": \"clip\"", json);
        Assert.DoesNotContain("\"clip\":", json); // the times of a clip, never on the tool

        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.True(read.IsClipTool);
        Assert.True(read.Media.ExactCut);

        // A conversion of a clip (kept in the history for "Retry") keeps its times.
        var conversion = ClipTool.ConversionOf(read, "mp4", new ClipRange(1, 2));
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<Preset> { conversion }, FileeJsonContext.Default.ListPreset),
            FileeJsonContext.Default.ListPreset)!.Single();
        Assert.Equal((1.0, 2.0), (again.Media.Clip!.Parts[0].Start, again.Media.Clip.Parts[0].End));
        // Other presets don't write a tool at all.
        Assert.DoesNotContain("\"tool\"", JsonSerializer.Serialize(new List<Preset> { new() { TargetFormat = "png" } }, FileeJsonContext.Default.ListPreset));
    }

    // ───────────────────────── Settings schema 45 ─────────────────────────

    [Fact]
    public void A_new_library_has_the_clip_tool_first_behind_the_video_donut()
    {
        var presets = BuiltInData.CreatePresets();
        var clip = presets.Single(p => p.Id == "tool-clip");
        Assert.Equal("motion-photo-android", presets[presets.IndexOf(clip) - 1].Id);
        var video = BuiltInData.CreateProfiles().Single(p => p.Id == "video");
        Assert.Equal("tool-clip", video.BackPresetIds![0]);
        Assert.DoesNotContain("tool-clip", video.PresetIds);
        Assert.DoesNotContain("to-flac", video.BackPresetIds);
        Assert.Contains(presets, p => p.Id == "to-flac");
        Assert.InRange(video.BackPresetIds.Count, 1, ToolbarProfile.MaxSlices);
    }

    [Fact]
    public void Version_44_libraries_get_the_clip_tool_and_it_goes_behind_the_video_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "tool-clip");
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        // The back face of 44: FLAC after WAV, no clip tool.
        video.BackPresetIds!.Remove("tool-clip");
        video.BackPresetIds.Insert(video.BackPresetIds.IndexOf("to-wav") + 1, "to-flac");

        Assert.True(SettingsMigrations.ApplyToLibrary(44, presets, profiles));

        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.True(presets.Single(p => p.Id == "tool-clip").IsClipTool);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "video").BackPresetIds, video.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(44, presets, profiles)); // once only

        // A full back face makes room by giving up FLAC; one the user filled up without FLAC, or with the tool on the
        // front, stays as it is.
        var withFlac = BuiltInData.CreateProfiles();
        var withFlacBack = withFlac.Single(p => p.Id == "video").BackPresetIds!;
        withFlacBack.Remove("tool-clip");
        withFlacBack.Add("to-flac");
        while (withFlacBack.Count < ToolbarProfile.MaxSlices)
            withFlacBack.Add("to-mp3");
        SettingsMigrations.ApplyToLibrary(44, BuiltInData.CreatePresets(), withFlac);
        Assert.Equal("tool-clip", withFlacBack[0]);
        Assert.DoesNotContain("to-flac", withFlacBack);
        Assert.Equal(ToolbarProfile.MaxSlices, withFlacBack.Count);

        var full = BuiltInData.CreateProfiles();
        var fullBack = full.Single(p => p.Id == "video").BackPresetIds!;
        fullBack.Remove("tool-clip");
        while (fullBack.Count < ToolbarProfile.MaxSlices)
            fullBack.Add("to-mp3");
        var before = fullBack.ToList();
        SettingsMigrations.ApplyToLibrary(44, BuiltInData.CreatePresets(), full);
        Assert.Equal(before, fullBack);

        var front = BuiltInData.CreateProfiles();
        var frontVideo = front.Single(p => p.Id == "video");
        frontVideo.BackPresetIds!.Remove("tool-clip");
        frontVideo.PresetIds[^1] = "tool-clip";
        SettingsMigrations.ApplyToLibrary(44, BuiltInData.CreatePresets(), front);
        Assert.DoesNotContain("tool-clip", frontVideo.BackPresetIds);
    }

    [Fact]
    public void Back_from_an_older_Filee_the_clip_tool_cuts_clips_again_and_keeps_the_users_changes()
    {
        // A Filee from 26 to 44 reads the unknown "tool" as nothing (a crop) and saves it so.
        var presets = BuiltInData.CreatePresets();
        var clip = presets.Single(p => p.Id == "tool-clip");
        clip.Tool = null;
        clip.Name = "Highlights";
        clip.Output.FileNamePattern = "{name}_best";
        Assert.True(clip.IsCropTool);

        Assert.True(SettingsMigrations.ApplyToLibrary(44, presets, BuiltInData.CreateProfiles()));

        Assert.True(clip.IsClipTool);
        Assert.Equal(("Highlights", "{name}_best"), (clip.Name, clip.Output.FileNamePattern));
        Assert.Single(presets, p => p.Id == "tool-clip");

        // A Filee before 26 read it as a group.
        clip.Kind = PresetKind.Group;
        clip.TargetFormat = Preset.GroupTarget;
        Assert.True(SettingsMigrations.ApplyToLibrary(44, presets, BuiltInData.CreateProfiles()));
        Assert.True(clip.IsClipTool);
        Assert.Equal(BuiltInData.SameAsSource, clip.TargetFormat);
    }
}
