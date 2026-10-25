// "Reset to default" of one donut (Settings → Toolbar): the built-in state of that profile only.

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ProfileResetTests
{
    private static ToolbarProfile BuiltIn(string id) => BuiltInData.CreateProfiles().Single(p => p.Id == id);

    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    private static ToolbarProfile Profile(UserDataStore store, string id) => store.Profiles.Single(p => p.Id == id);

    [Fact]
    public void A_built_in_donut_gets_its_name_slices_and_extensions_back_and_the_others_stay()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        images.Name = "My pictures";
        images.PresetIds = ["to-webp", "to-png"];
        images.Extensions.Remove("png");
        images.Extensions.Add("xyz");
        var pdf = Profile(store, "pdf");
        pdf.Name = "Papers";
        pdf.PresetIds.RemoveAt(0);
        pdf.Extensions.Add("abc");
        store.SaveLibrary();
        var before = store.Profiles.Where(p => p.Id != "images").Select(p => p.Clone()).ToList();
        var order = store.Profiles.Select(p => p.Id).ToList();

        Assert.True(store.CanResetProfile(images));
        Assert.True(store.ResetProfile(images));

        var builtIn = BuiltIn("images");
        Assert.Equal("", images.Name);
        Assert.Equal(builtIn.NameKey, images.NameKey);
        Assert.Equal(builtIn.PresetIds, images.PresetIds);
        Assert.Equal(builtIn.Extensions, images.Extensions);
        Assert.False(images.IsFallback);
        // Every other donut as it was, the list in the same order.
        Assert.Equal(order, store.Profiles.Select(p => p.Id));
        foreach (var other in before)
        {
            var now = Profile(store, other.Id);
            Assert.Equal(other.Name, now.Name);
            Assert.Equal(other.PresetIds, now.PresetIds);
            Assert.Equal(other.Extensions, now.Extensions);
            Assert.Equal(other.IsFallback, now.IsFallback);
        }

        // Saved.
        var reloaded = NewStore(dir);
        Assert.Equal(builtIn.PresetIds, Profile(reloaded, "images").PresetIds);
        Assert.Equal("", Profile(reloaded, "images").Name);
        Assert.Equal("Papers", Profile(reloaded, "pdf").Name);
    }

    [Fact]
    public void An_extension_another_donut_took_stays_there()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        images.Extensions.Remove("png");
        var mine = new ToolbarProfile { Id = "mine", Name = "Screenshots", Extensions = ["PNG"], PresetIds = ["to-jpg"] };
        store.Profiles.Insert(0, mine);
        // Checked on a mixed profile, an extension only limits that profile: the reset still takes it.
        Profile(store, "mixed").Extensions.Add("jpg");
        store.SaveLibrary();

        Assert.True(store.ResetProfile(images));

        Assert.DoesNotContain("png", images.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("jpg", images.Extensions);
        Assert.Equal(BuiltIn("images").Extensions.Where(e => e != "png"), images.Extensions);
        Assert.Equal(["PNG"], mine.Extensions);
        Assert.Equal(["jpg"], Profile(store, "mixed").Extensions);
        // No file type opens two normal donuts.
        var normal = store.Profiles.Where(p => !p.IsFallback).SelectMany(p => p.Extensions).ToList();
        Assert.Equal(normal.Count, normal.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Presets_stay_in_the_library_and_a_deleted_built_in_one_comes_back()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.Presets.Add(new Preset { Id = "mine", Name = "Mine", TargetFormat = "webp" });
        store.FindPreset("to-png")!.Image.Quality = 42;
        var images = Profile(store, "images");
        images.PresetIds = ["mine", "to-png"];
        // Deleted from the library, and so from every donut.
        var tiff = store.Presets.FindIndex(p => p.Id == "to-tiff");
        var after = store.Presets[tiff - 1].Id;
        store.Presets.RemoveAt(tiff);
        store.SaveLibrary();
        Assert.DoesNotContain("to-tiff", images.PresetIds);

        Assert.True(store.ResetProfile(images));

        Assert.Equal(BuiltIn("images").PresetIds, images.PresetIds);
        Assert.NotNull(store.FindPreset("mine")); // off this donut, still in the library
        Assert.Equal(42, store.FindPreset("to-png")!.Image.Quality); // an edited preset keeps its settings
        Assert.Equal(store.Presets.FindIndex(p => p.Id == after) + 1, store.Presets.FindIndex(p => p.Id == "to-tiff"));
        Assert.Equal(store.Presets.Count, store.Presets.Select(p => p.Id).Distinct().Count());
        Assert.Contains("to-tiff", NewStore(dir).Profiles.Single(p => p.Id == "images").PresetIds);
    }

    [Fact]
    public void The_geospatial_donut_resets_like_the_other_built_in_ones()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var geo = Profile(store, "geo");
        geo.Name = "Maps";
        geo.PresetIds = ["to-kml"];
        geo.Extensions.Remove("kml");
        store.Profiles.Insert(0, new ToolbarProfile { Id = "earth", Name = "Earth", Extensions = ["kml"], PresetIds = ["to-geojson"] });
        store.Presets.RemoveAll(p => p.Id == "to-kmz");
        store.SaveLibrary();

        Assert.True(store.CanResetProfile(geo));
        Assert.True(store.ResetProfile(geo));

        var builtIn = BuiltIn("geo");
        Assert.Equal("", geo.Name);
        Assert.Equal("profile.geo", geo.NameKey);
        Assert.Equal(builtIn.PresetIds, geo.PresetIds);
        Assert.Equal(builtIn.Extensions.Where(e => e != "kml"), geo.Extensions); // .kml stays on the user's donut
        Assert.Equal(["kml"], Profile(store, "earth").Extensions);
        Assert.Equal("kmz", store.FindPreset("to-kmz")!.TargetFormat); // the deleted slice's preset is back
        Assert.All(geo.PresetIds, id => Assert.NotNull(store.FindPreset(id)));
    }

    [Fact]
    public void A_profile_the_user_made_has_no_default_to_go_back_to()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var mine = new ToolbarProfile { Name = "Mine", Extensions = ["png"], PresetIds = ["to-jpg"] };
        store.Profiles.Insert(0, mine);
        store.SaveLibrary();

        Assert.False(store.CanResetProfile(mine));
        Assert.False(store.ResetProfile(mine));
        Assert.Equal("Mine", mine.Name);
        Assert.Equal(["png"], mine.Extensions);
        Assert.Equal(["to-jpg"], mine.PresetIds);
        // Nor one that isn't in the library (any more).
        Assert.False(store.CanResetProfile(BuiltIn("images")));
    }

    [Fact]
    public void A_donut_that_changed_kind_goes_back_to_its_place()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        var mixed = Profile(store, "mixed");
        // "Images" made mixed (it moves behind "Mixed files"), "Mixed files" made normal.
        images.IsFallback = true;
        store.Profiles.Remove(images);
        store.Profiles.Add(images);
        mixed.IsFallback = false;
        mixed.Extensions = ["png"];
        store.SaveLibrary();

        Assert.True(store.ResetProfile(mixed));
        Assert.True(mixed.IsFallback);
        Assert.Empty(mixed.Extensions);
        Assert.Same(mixed, ProfileSelector.CatchAll(store.Profiles)); // the head of the mixed ones again

        Assert.True(store.ResetProfile(images));
        Assert.False(images.IsFallback);
        // Normal profiles first: right before the mixed ones.
        Assert.Equal(store.Profiles.FindIndex(p => p.IsFallback) - 1, store.Profiles.IndexOf(images));
        Assert.All(store.Profiles.SkipWhile(p => !p.IsFallback), p => Assert.True(p.IsFallback));
    }

    [Fact]
    public void The_only_mixed_profile_is_not_reset_into_a_normal_one()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        images.IsFallback = true;
        store.Profiles.Remove(Profile(store, "mixed"));
        store.SaveLibrary();
        Assert.Single(store.Profiles, p => p.IsFallback);

        Assert.False(store.CanResetProfile(images));
        Assert.False(store.ResetProfile(images));
        Assert.True(images.IsFallback);
    }
}
