// CRI USM game videos (#485): a read-only video source on the video donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class UsmFormatTests
{
    [Fact]
    public void Usm_is_a_video_filee_reads_but_never_writes()
    {
        var usm = FormatRegistry.Detect("cutscene.USM");
        Assert.NotNull(usm);
        Assert.Equal(("usm", FormatCategory.Video, false), (usm.Id, usm.Category, usm.Writable));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["movie01.usm"])?.Id);
    }

    [Fact]
    public void Version_43_libraries_learn_usm_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.Extensions.Remove("usm");

        Assert.True(SettingsMigrations.ApplyToLibrary(43, presets, profiles));

        Assert.Contains("usm", video.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(43, presets, profiles));
    }
}
