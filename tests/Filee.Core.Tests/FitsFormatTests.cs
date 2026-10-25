// FITS astronomy pictures (issue #460): .fits and .fts are FITS, ".fit" is left for Garmin's FIT; the images donut
// learns the extensions in schema 43 without taking them from a donut of the user's.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FitsFormatTests
{
    [Theory]
    [InlineData("m31.fits")]
    [InlineData("Light_001.FTS")]
    public void Fits_extensions_are_read_only_images(string file)
    {
        var format = FormatRegistry.Detect(file);

        Assert.Equal("fits", format?.Id);
        Assert.Equal(FormatCategory.Image, format!.Category);
        Assert.False(format.Writable);
    }

    [Fact]
    public void Fit_is_not_taken_for_fits() =>
        Assert.NotEqual("fits", FormatRegistry.Detect("ride.fit")?.Id);

    [Fact]
    public void Fresh_profiles_open_the_images_donut_for_fits() =>
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["a.fits", "b.fts"])?.Id);

    [Fact]
    public void Version_42_libraries_learn_fits_once_and_keep_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "fits" or "fts");
        profiles.Add(new ToolbarProfile { Id = "astro", Extensions = ["fts"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(42, presets, profiles));

        Assert.Contains("fits", images.Extensions);
        Assert.DoesNotContain("fts", images.Extensions);
        Assert.Equal("astro", ProfileSelector.Select(profiles, ["light.fts"])?.Id);
        Assert.False(SettingsMigrations.ApplyToLibrary(42, presets, profiles));
    }

    [Fact]
    public void Presets_saved_before_the_options_debayer_and_keep_tiffs_linear()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"TargetFormat":"png","Image":{"Quality":80}}""",
            FileeJsonContext.Default.Preset)!;
        var newer = System.Text.Json.JsonSerializer.Deserialize("""{"TargetFormat":"tiff","Image":{"FitsTiff":"Sharpened"}}""",
            FileeJsonContext.Default.Preset)!;

        Assert.True(old.Image.FitsDebayer);
        Assert.Equal(FitsTiffMode.Linear, old.Image.FitsTiff);
        Assert.Equal(FitsTiffMode.Linear, newer.Image.FitsTiff); // a mode from a newer Filee
        var stretched = new ImageOptions { FitsTiff = FitsTiffMode.Stretched, FitsDebayer = false }.Clone();
        Assert.Equal((FitsTiffMode.Stretched, false), (stretched.FitsTiff, stretched.FitsDebayer));
    }
}
