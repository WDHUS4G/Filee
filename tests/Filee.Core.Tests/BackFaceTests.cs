// The back face of a donut (Tab flips the open donut to it): the built-in ones, settings schema 30 that gives existing
// libraries theirs, a round trip through a Filee from before back faces, "Reset to default" and import.

using System.Text.Json.Nodes;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class BackFaceTests
{
    private static ToolbarProfile BuiltIn(string id) => BuiltInData.CreateProfiles().Single(p => p.Id == id);

    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    private static ToolbarProfile Profile(UserDataStore store, string id) => store.Profiles.Single(p => p.Id == id);

    /// <summary>A library as a Filee from before back faces wrote it: no "backPresetIds" anywhere.</summary>
    private static void DropBackFaces(TempDir dir, int? schema)
    {
        var path = Path.Combine(dir.Path, "profiles.json");
        var profiles = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var profile in profiles)
            profile!.AsObject().Remove("backPresetIds");
        File.WriteAllText(path, profiles.ToJsonString());
        if (schema is null)
            return;
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["schemaVersion"] = schema;
        File.WriteAllText(settingsPath, settings.ToJsonString());
    }

    [Fact]
    public void Every_built_in_donut_has_a_back_face_of_other_built_in_presets()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        foreach (var profile in BuiltInData.CreateProfiles())
        {
            var back = profile.BackPresetIds;
            Assert.NotNull(back);
            Assert.True(back.Count <= ToolbarProfile.MaxSlices, profile.Id);
            Assert.Equal(back.Count, back.Distinct().Count());
            Assert.All(back, id => Assert.True(presets.ContainsKey(id), $"{profile.Id}: {id}"));
            // Nothing moved off the front, nothing shown twice.
            Assert.Empty(back.Intersect(profile.PresetIds));
        }
        // Only archives have nothing more to offer ("One ZIP" is on their front since settings schema 28).
        Assert.Equal(["archives"], BuiltInData.CreateProfiles().Where(p => p.BackPresetIds!.Count == 0).Select(p => p.Id));
    }

    [Fact]
    public void Pdf_edit_is_one_back_slice_with_six_real_actions()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var pdf = BuiltIn("pdf");

        Assert.Equal("pdf-edit", pdf.BackPresetIds![0]);
        var edit = presets["pdf-edit"];
        Assert.True(edit.IsGroup);
        Assert.Equal([
            "tool-pdf-crop", "pdf-rotate-left", "pdf-rotate-right",
            "tool-pdf-pages", "pdf-split", "tool-pdf-add",
        ], edit.SubPresetIds);
        Assert.Equal(Preset.MaxSubPresets, edit.SubPresetIds.Count);
        Assert.All(edit.SubPresetIds, id => Assert.False(presets[id].IsGroup));
        Assert.All(edit.SubPresetIds.Where(id => id.StartsWith("tool-pdf-")), id => Assert.True(presets[id].IsTool));
    }

    [Fact]
    public void Version_45_library_gets_pdf_edit_once_without_replacing_custom_back_slices()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "tool-pdf-crop" or "tool-pdf-pages" or "tool-pdf-add"
            or "pdf-rotate-left" or "pdf-rotate-right" or "pdf-edit");
        var profiles = BuiltInData.CreateProfiles();
        var pdf = profiles.Single(p => p.Id == "pdf");
        pdf.BackPresetIds = ["to-html", "zip-all"];

        Assert.True(SettingsMigrations.ApplyToLibrary(45, presets, profiles));

        Assert.Equal(["pdf-edit", "to-html", "zip-all"], pdf.BackPresetIds);
        Assert.All(new[] { "tool-pdf-crop", "tool-pdf-pages", "tool-pdf-add", "pdf-rotate-left", "pdf-rotate-right", "pdf-edit" },
            id => Assert.Single(presets, p => p.Id == id));
        Assert.False(SettingsMigrations.ApplyToLibrary(45, presets, profiles));
    }

    [Fact]
    public void The_fronts_stay_as_they_were_before_back_faces()
    {
        Assert.Equal(["to-png", "to-jpg", "to-webp", "to-pdf", "trace-svg", "trace-line-art", "to-tiff", "to-bmp", "to-ico", "half-size"],
            BuiltIn("images").PresetIds);
        Assert.Equal(BuiltInData.PdfDonut, BuiltIn("pdf").PresetIds);
        Assert.Equal(["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-odt", "to-txt", "to-html", "to-epub", "to-png", "merge-pdf"],
            BuiltIn("office").PresetIds);
        Assert.Equal(["to-pdf", "merge-pdf", "zip-all", "to-png", "to-txt"], BuiltIn("mixed").PresetIds);
        Assert.Equal(["to-mp4", "mp4-720", "mp4-hevc", "to-webm", "to-mov", "to-gif", "to-mp3", "extract-subtitles", "to-mkv", "to-avi"],
            BuiltIn("video").PresetIds);
    }

    [Fact]
    public void A_new_library_saves_and_loads_the_back_faces()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        Assert.Equal(BuiltIn("images").BackPresetIds, images.BackPresetIds);
        images.BackPresetIds = ["to-gif", "zip-all"];
        store.SaveLibrary();

        Assert.Contains("\"backPresetIds\"", File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));
        var reloaded = NewStore(dir);
        Assert.Equal(["to-gif", "zip-all"], Profile(reloaded, "images").BackPresetIds);
        Assert.Equal(BuiltIn("pdf").BackPresetIds, Profile(reloaded, "pdf").BackPresetIds);
    }

    [Fact]
    public void Version_29_libraries_get_the_built_in_back_faces_and_keep_their_fronts()
    {
        // As written with schema 29: no back faces, the user's own front on the images donut and a donut of their own.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var p in profiles)
            p.BackPresetIds = null;
        var images = profiles.Single(p => p.Id == "images");
        images.PresetIds = ["to-webp", "to-png"];
        var mine = new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["xyz"], PresetIds = ["to-pdf"] };
        profiles.Insert(0, mine);
        // A preset of the default back face the user deleted stays deleted.
        presets.RemoveAll(p => p.Id == "to-gif");
        var fronts = profiles.ToDictionary(p => p.Id, p => p.PresetIds.ToList());

        Assert.True(SettingsMigrations.ApplyToLibrary(29, presets, profiles));

        foreach (var profile in profiles.Where(p => p != mine))
        {
            var expected = BuiltIn(profile.Id).BackPresetIds!.Where(id => id != "to-gif");
            Assert.Equal(expected, profile.BackPresetIds);
        }
        Assert.Null(mine.BackPresetIds); // the store gives it an empty one (below)
        Assert.All(profiles, p => Assert.Equal(fronts[p.Id], p.PresetIds));
        Assert.False(SettingsMigrations.ApplyToLibrary(29, presets, profiles)); // once only
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void An_existing_user_gets_the_back_faces_and_their_own_donuts_none()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        Profile(store, "images").PresetIds = ["to-webp", "to-png"];
        store.Profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["xyz"], PresetIds = ["to-pdf"] });
        store.SaveLibrary();
        store.SaveSettings();
        DropBackFaces(dir, schema: 29);

        var upgraded = NewStore(dir);

        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);
        Assert.Equal(["to-webp", "to-png"], Profile(upgraded, "images").PresetIds);
        Assert.Equal(BuiltIn("images").BackPresetIds, Profile(upgraded, "images").BackPresetIds);
        Assert.Equal(BuiltIn("mixed").BackPresetIds, Profile(upgraded, "mixed").BackPresetIds);
        Assert.Equal([], Profile(upgraded, "mine").BackPresetIds);
        // Written at once, so it runs once.
        Assert.Contains("\"backPresetIds\"", File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));
        var again = NewStore(dir);
        Assert.Equal(BuiltIn("images").BackPresetIds, Profile(again, "images").BackPresetIds);
        Assert.Equal([], Profile(again, "mine").BackPresetIds);
    }

    [Theory]
    [InlineData(29)]    // the older Filee also saved its settings, with its own schema
    [InlineData(null)]  // it only saved the library: the settings still say 30
    public void Back_from_an_older_Filee_the_built_in_back_faces_return_and_an_emptied_one_stays_empty(int? schema)
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        Profile(store, "images").BackPresetIds = ["to-gif"];
        Profile(store, "pdf").BackPresetIds = [];
        store.SaveLibrary();
        store.SaveSettings();

        // Opened with this Filee again, the user's back faces are as they left them, the emptied one too.
        var same = NewStore(dir);
        Assert.Equal(["to-gif"], Profile(same, "images").BackPresetIds);
        Assert.Equal([], Profile(same, "pdf").BackPresetIds);

        // An older Filee saved the library in between: it dropped the back faces it didn't know.
        DropBackFaces(dir, schema);
        var back = NewStore(dir);

        Assert.Equal(BuiltIn("images").BackPresetIds, Profile(back, "images").BackPresetIds);
        Assert.Equal(BuiltIn("pdf").BackPresetIds, Profile(back, "pdf").BackPresetIds);
        Assert.Equal(BuiltIn("images").PresetIds, Profile(back, "images").PresetIds);
        Assert.Equal(AppSettings.CurrentSchemaVersion, back.Settings.SchemaVersion);
        Assert.False(File.Exists(Path.Combine(dir.Path, "profiles.json.broken")));
    }

    [Fact]
    public void Dangling_and_extra_back_slices_are_dropped()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        images.BackPresetIds = ["does-not-exist", "to-gif", "to-gif", .. store.Presets.Select(p => p.Id).Take(20)];
        store.SaveLibrary();

        Assert.DoesNotContain("does-not-exist", images.BackPresetIds);
        Assert.Equal(ToolbarProfile.MaxSlices, images.BackPresetIds.Count);
        Assert.Single(images.BackPresetIds, "to-gif");
    }

    [Fact]
    public void Reset_to_default_restores_the_back_face_too()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var images = Profile(store, "images");
        images.BackPresetIds = ["to-pdf"];
        // A preset of the default back face and one the "Rotate" group fans out, both deleted.
        store.Presets.RemoveAll(p => p.Id is "to-avif" or "rotate-180");
        store.SaveLibrary();
        Assert.DoesNotContain("rotate-180", store.FindPreset("rotate")!.SubPresetIds);

        Assert.True(store.ResetProfile(images));

        Assert.Equal(BuiltIn("images").BackPresetIds, images.BackPresetIds);
        Assert.Equal(BuiltIn("images").PresetIds, images.PresetIds);
        Assert.NotNull(store.FindPreset("to-avif"));
        Assert.NotNull(store.FindPreset("rotate-180"));
        Assert.Equal(store.Presets.Count, store.Presets.Select(p => p.Id).Distinct().Count());
        Assert.Equal(BuiltIn("images").BackPresetIds, Profile(NewStore(dir), "images").BackPresetIds);
    }

    [Fact]
    public void Reset_to_default_leaves_the_other_back_faces_alone()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        Profile(store, "pdf").BackPresetIds = ["to-gif"];
        Profile(store, "images").BackPresetIds = [];
        store.SaveLibrary();

        Assert.True(store.ResetProfile(Profile(store, "images")));

        Assert.Equal(["to-gif"], Profile(store, "pdf").BackPresetIds);
    }

    [Fact]
    public void The_flip_key_and_the_wheel_are_saved_and_older_settings_get_tab_and_the_wheel()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 24, "donut": { "outerRadius": 180 } }""");
        var store = NewStore(dir);
        Assert.Equal("Tab", store.Settings.Donut.FlipKey);
        Assert.True(store.Settings.Donut.FlipWithWheel);
        Assert.Equal(180, store.Settings.Donut.OuterRadius);

        store.Settings.Donut.FlipKey = "F2";
        store.Settings.Donut.FlipWithWheel = false;
        store.SaveSettings();
        var reloaded = NewStore(dir);
        Assert.Equal("F2", reloaded.Settings.Donut.FlipKey);
        Assert.False(reloaded.Settings.Donut.FlipWithWheel);

        // Cleared: no key, saved as such (not back to Tab).
        reloaded.Settings.Donut.FlipKey = "";
        reloaded.SaveSettings();
        Assert.Null(NewStore(dir).Settings.Donut.EffectiveFlipKey([]));
    }

    [Theory]
    [InlineData("Tab", true)]
    [InlineData("F2", true)]
    [InlineData("Q", true)]
    [InlineData("Escape", false)]   // closes the donut
    [InlineData("Enter", false)]    // converts / opens a group
    [InlineData("Space", false)]
    [InlineData("Left", false)]     // moves between slices
    [InlineData("Backspace", false)] // folds a group's ring
    [InlineData("K", false)]        // the key of a keyboard shortcut below
    [InlineData("", false)]
    public void Keys_the_donut_or_a_shortcut_uses_do_not_flip(string key, bool allowed)
    {
        TriggerGesture[] gestures =
        [
            new() { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl },
            new() { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift, Key = "K" },
        ];
        Assert.Equal(allowed, DonutSettings.IsFlipKeyAllowed(key, gestures));
    }

    [Fact]
    public void A_flip_key_that_is_not_allowed_falls_back_to_tab()
    {
        TriggerGesture[] chordOnF2 = [new() { Kind = TriggerKind.KeyChord, Key = "F2" }];
        Assert.Equal("Tab", new DonutSettings { FlipKey = "Escape" }.EffectiveFlipKey([]));
        Assert.Equal("Tab", new DonutSettings { FlipKey = "F2" }.EffectiveFlipKey(chordOnF2)); // a shortcut took it since
        Assert.Equal("F2", new DonutSettings { FlipKey = "F2" }.EffectiveFlipKey([]));
        Assert.Null(new DonutSettings { FlipKey = "Escape" }.EffectiveFlipKey([new() { Kind = TriggerKind.KeyChord, Key = "Tab" }]));
    }

    [Fact]
    public void A_bundle_from_before_back_faces_imports_built_in_donuts_with_theirs()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var bundle = Path.Combine(dir.Path, "bundle.json");
        store.Export(bundle);
        var json = JsonNode.Parse(File.ReadAllText(bundle))!.AsObject();
        foreach (var profile in json["profiles"]!.AsArray())
            profile!.AsObject().Remove("backPresetIds");
        json["profiles"]!.AsArray().Add(new JsonObject { ["id"] = "theirs", ["name"] = "Theirs", ["presetIds"] = new JsonArray("to-png") });
        File.WriteAllText(bundle, json.ToJsonString());

        using var other = new TempDir();
        var target = NewStore(other);
        Profile(target, "images").BackPresetIds = ["to-gif"];
        target.Import(bundle);

        Assert.Equal(BuiltIn("images").BackPresetIds, Profile(target, "images").BackPresetIds);
        Assert.Equal([], Profile(target, "theirs").BackPresetIds);
    }
}
