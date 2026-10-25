// Extensions added in settings schema 33: HAR network logs (issue #328) and raw H.265 / H.264 camera
// streams (issue #301).

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class NewExtensionsTests
{
    [Theory]
    [InlineData("requests.har", "har")]
    [InlineData("camera1.h265", "h265")]
    [InlineData("camera1.265", "h265")]
    [InlineData("camera1.hevc", "h265")]
    [InlineData("camera1.h264", "h264")]
    [InlineData("camera1.264", "h264")]
    public void Files_are_recognised_by_their_extension(string file, string format) =>
        Assert.Equal(format, FormatRegistry.Detect(file)?.Id);

    [Fact]
    public void New_formats_are_read_only()
    {
        foreach (var id in new[] { "h265", "h264", "har" })
        {
            var format = FormatRegistry.Get(id);
            Assert.Equal(id == "har" ? FormatCategory.Data : FormatCategory.Video, format.Category);
            Assert.False(format.Writable);
        }
    }

    [Fact]
    public void Version_32_libraries_learn_the_extensions_without_taking_custom_claims()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 32;
        string[] added = ["har", "h265", "265", "hevc", "h264", "264"];
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(added.Contains);
        store.Profiles.Insert(0, new ToolbarProfile { Id = "my-camera", Name = "Camera", Extensions = ["h264"] });
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();

        Assert.Contains("har", upgraded.Profiles.Single(p => p.Id == "data").Extensions);
        var video = upgraded.Profiles.Single(p => p.Id == "video").Extensions;
        Assert.Contains("h265", video);
        Assert.Contains("hevc", video);
        Assert.DoesNotContain("h264", video); // the user's own donut has it
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_32_libraries_get_trim_transparent_borders_after_remove_background()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != "trim-transparent").ToList();
        var profiles = BuiltInData.CreateProfiles();
        var back = profiles.Single(p => p.Id == "images").BackPresetIds!;
        back.Remove("trim-transparent");

        Assert.True(SettingsMigrations.ApplyToLibrary(32, presets, profiles));

        Assert.Equal("trim-transparent", presets[presets.FindIndex(p => p.Id == "remove-background") + 1].Id);
        Assert.Equal("trim-transparent", back[back.IndexOf("remove-background") + 1]);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds, back);
        Assert.False(SettingsMigrations.ApplyToLibrary(32, presets, profiles)); // once only
    }
}
