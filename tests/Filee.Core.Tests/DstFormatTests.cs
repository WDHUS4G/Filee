// Tajima embroidery designs (.dst, issue #397) are a read-only vector format, learnt by existing vector donuts.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class DstFormatTests
{
    [Fact]
    public void Dst_is_a_read_only_vector_format()
    {
        var format = FormatRegistry.Detect("badge.dst")!;
        Assert.Equal(("dst", FormatCategory.Vector, false), (format.Id, format.Category, format.Writable));
    }

    [Fact]
    public void Version_37_vector_donuts_learn_it_without_taking_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.Remove("dst");
        Assert.True(SettingsMigrations.ApplyToLibrary(37, presets, profiles));
        Assert.Contains("dst", profiles.Single(p => p.Id == "vector").Extensions);

        profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.Remove("dst");
        profiles.Insert(0, new ToolbarProfile { Id = "my-cad", Name = "Sheet sets", Extensions = ["dst"] });
        SettingsMigrations.ApplyToLibrary(37, presets, profiles);
        Assert.DoesNotContain("dst", profiles.Single(p => p.Id == "vector").Extensions);
    }
}
