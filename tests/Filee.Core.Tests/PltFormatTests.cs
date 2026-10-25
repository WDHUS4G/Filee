// HP-GL plot files (.plt, issue #410) are a read-only vector format, learnt by existing vector donuts.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PltFormatTests
{
    [Theory]
    [InlineData("floor.plt")]
    [InlineData("cut.hpgl")]
    public void Plot_files_are_a_read_only_vector_format(string name)
    {
        var format = FormatRegistry.Detect(name)!;
        Assert.Equal(("plt", FormatCategory.Vector, false), (format.Id, format.Category, format.Writable));
    }

    [Fact]
    public void Version_34_vector_donuts_learn_them()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(e => e is "plt" or "hpgl" or "hpg" or "hgl");

        Assert.True(SettingsMigrations.ApplyToLibrary(34, presets, profiles));

        Assert.Contains("plt", profiles.Single(p => p.Id == "vector").Extensions);
    }
}
