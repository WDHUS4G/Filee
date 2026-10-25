// PNG/JPEG download aliases are recognised and learnt by image profiles without taking custom claims.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ImageExtensionAliasTests
{
    [Theory]
    [InlineData("photo.png_large", "png")]
    [InlineData("photo.jpg_large", "jpg")]
    [InlineData("photo.PnG_LaRgE", "png")]
    [InlineData("photo.JpG_LaRgE", "jpg")]
    public void Large_image_extensions_resolve_to_the_existing_format(string file, string format) =>
        Assert.Same(FormatRegistry.Get(format), FormatRegistry.Detect(file));

    [Fact]
    public void An_unknown_large_extension_is_not_detected() =>
        Assert.Null(FormatRegistry.Detect("file.unknown_large"));

    [Fact]
    public void Fresh_image_profiles_take_both_aliases()
    {
        var profiles = BuiltInData.CreateProfiles();

        Assert.Equal("images", ProfileSelector.Select(profiles, ["photo.png_large", "photo.JpG_LaRgE"])?.Id);
    }

    [Fact]
    public void Version_40_libraries_learn_both_aliases_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "png_large" or "jpg_large" or "png");

        Assert.True(SettingsMigrations.ApplyToLibrary(40, presets, profiles));

        Assert.Contains("png_large", images.Extensions);
        Assert.Contains("jpg_large", images.Extensions);
        Assert.DoesNotContain("png", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(40, presets, profiles));
    }

    [Theory]
    [InlineData("PNG_LARGE", "jpg_large")]
    [InlineData("JPG_LARGE", "png_large")]
    public void Version_40_libraries_preserve_custom_alias_claims(string claimed, string learned)
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 40;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => e is "png_large" or "jpg_large");
        store.Profiles.Add(new ToolbarProfile { Id = "my-photos", Extensions = [claimed] });
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();

        var images = upgraded.Profiles.Single(p => p.Id == "images");
        Assert.Contains(learned, images.Extensions);
        Assert.False(images.Matches(claimed));
        Assert.Equal("my-photos", ProfileSelector.Select(upgraded.Profiles, ["photo." + claimed])?.Id);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Contains(learned, reloaded.Profiles.Single(p => p.Id == "images").Extensions);
        Assert.Equal("my-photos", Assert.Single(reloaded.Profiles, p => p.Matches(claimed)).Id);
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);
    }
}
