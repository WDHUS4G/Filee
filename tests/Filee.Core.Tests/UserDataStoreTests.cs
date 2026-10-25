using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class UserDataStoreTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public void Previous_versions_learn_reported_extensions_without_taking_custom_claims(int schema)
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = schema;
        if (schema == 30)
        {
            store.Presets.RemoveAll(p => p.Id is "live-photo-iphone" or "motion-photo-android");
            store.Profiles.Single(p => p.Id == "video").BackPresetIds!
                .RemoveAll(id => id is "live-photo-iphone" or "motion-photo-android");
        }
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => e is "log" or "py" or "pac" or "bat" or "command" or "dat" or "wbpz");
        store.Profiles.Insert(0, new ToolbarProfile { Id = "my-python", Name = "My code", Extensions = ["py"] });
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var text = upgraded.Profiles.Single(p => p.Id == "text");
        Assert.Contains("log", text.Extensions);
        Assert.Contains("pac", text.Extensions);
        Assert.Contains("bat", text.Extensions);
        Assert.Contains("command", text.Extensions);
        Assert.DoesNotContain("py", text.Extensions);
        Assert.Contains("py", upgraded.Profiles.Single(p => p.Id == "my-python").Extensions);
        Assert.Contains("dat", upgraded.Profiles.Single(p => p.Id == "video").Extensions);
        Assert.Contains("wbpz", upgraded.Profiles.Single(p => p.Id == "archives").Extensions);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);
        Assert.Equal(LivePhotoKind.Apple, upgraded.FindPreset("live-photo-iphone")!.Media.LivePhoto);
        Assert.Equal(LivePhotoKind.Android, upgraded.FindPreset("motion-photo-android")!.Media.LivePhoto);

        // After upgrading, deliberately removing an extension must survive another load.
        text.Extensions.Remove("log");
        upgraded.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Single(reloaded.Profiles, p => p.Extensions.Contains("py"));
        Assert.Single(reloaded.Profiles, p => p.Extensions.Contains("dat"));
        Assert.DoesNotContain("log", reloaded.Profiles.Single(p => p.Id == "text").Extensions);
    }

    [Fact]
    public void Groups_keep_only_conversions_once_and_at_most_six()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "other", Name = "Other", Kind = PresetKind.Group, SubPresetIds = ["to-pdf"] });
        store.Presets.Add(new Preset
        {
            Id = "mine",
            Name = "Mine",
            Kind = PresetKind.Group,
            SubPresetIds = ["to-png", "gone", "to-png", "other", "to-jpg", "to-webp", "to-bmp", "to-gif", "to-ico", "to-tiff"],
        });
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        // A deleted preset and a group in the group are dropped, a repeat counts once, the rest is cut at six.
        var group = reloaded.FindPreset("mine")!;
        Assert.True(group.IsGroup);
        Assert.Equal(["to-png", "to-jpg", "to-webp", "to-bmp", "to-gif", "to-ico"], group.SubPresetIds);
        // No target format a Filee from before groups (which reads it as a conversion) could run it with.
        Assert.Equal(Preset.GroupTarget, group.TargetFormat);
        Assert.Null(Filee.Core.Formats.FormatRegistry.FindById(Preset.GroupTarget));
    }

    [Fact]
    public void Only_a_conversion_is_found_to_run()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "web", Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png"] });

        Assert.NotNull(store.FindPreset("web")); // to edit it
        Assert.Null(store.FindConversion("web")); // never to run it (a watch folder pointing at it converts nothing)
        Assert.Equal("to-png", store.FindConversion("to-png")!.Id);
    }

    [Fact]
    public void A_kind_from_a_newer_Filee_reads_as_a_group_and_the_other_presets_still_load()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "future", Name = "Future", Kind = PresetKind.Group, SubPresetIds = ["to-png"] });
        store.SaveLibrary();
        var path = Path.Combine(dir.Path, "presets.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"kind\": \"Group\"", json);
        File.WriteAllText(path, json.Replace("\"kind\": \"Group\"", "\"kind\": \"Chain\""));

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.True(reloaded.FindPreset("future")!.IsGroup); // never run as a conversion
        Assert.NotNull(reloaded.FindPreset("to-png")); // and not every preset reset to the defaults
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void A_group_saved_by_a_Filee_from_before_groups_is_still_a_group()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "old-group", Name = "Rotate", Kind = PresetKind.Group, SubPresetIds = ["to-png"] });
        store.SaveLibrary();

        // An older Filee knows neither "kind" nor "subPresetIds" and leaves them out when it saves.
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var saved = presets.Single(p => (string?)p!["id"] == "old-group")!.AsObject();
        Assert.True(saved.Remove("kind"));
        Assert.True(saved.Remove("subPresetIds"));
        File.WriteAllText(path, presets.ToJsonString());

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        var group = reloaded.FindPreset("old-group")!;
        Assert.True(group.IsGroup); // not a conversion to "group" in the preset list, watch folders and the CLI
        Assert.Empty(group.SubPresetIds);
        Assert.Null(reloaded.FindConversion("old-group"));
    }

    [Fact]
    public void Round_trips_settings_presets_and_profiles()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Language = "ko";
        store.Settings.Theme.Accent = "#1D9E75";
        store.Settings.Triggers[0].Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        store.Presets.Add(new Preset { Id = "mine", Name = "Mine", TargetFormat = "webp", Image = { Quality = 42 } });
        store.Profiles[0].PresetIds.Insert(0, "mine");
        store.SaveSettings();
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.Equal("ko", reloaded.Settings.Language);
        Assert.Equal("#1D9E75", reloaded.Settings.Theme.Accent);
        Assert.Equal(ModifierKeys.Alt | ModifierKeys.Shift, reloaded.Settings.Triggers[0].Modifiers);
        Assert.Equal(42, reloaded.FindPreset("mine")!.Image.Quality);
        Assert.Equal("mine", reloaded.Profiles[0].PresetIds[0]);
    }

    [Fact]
    public void Dock_and_menu_bar_preferences_round_trip_independently()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.True(store.Settings.ShowInDock);
        Assert.True(store.Settings.ShowMenuBarIcon);

        store.Settings.ShowInDock = false;
        store.SaveSettings();
        var dockOff = new UserDataStore(dir.Path);
        dockOff.Load();
        Assert.False(dockOff.Settings.ShowInDock);
        Assert.True(dockOff.Settings.ShowMenuBarIcon);

        dockOff.Settings.ShowInDock = true;
        dockOff.Settings.ShowMenuBarIcon = false;
        dockOff.SaveSettings();
        var menuBarOff = new UserDataStore(dir.Path);
        menuBarOff.Load();
        Assert.True(menuBarOff.Settings.ShowInDock);
        Assert.False(menuBarOff.Settings.ShowMenuBarIcon);

        menuBarOff.Settings.ShowInDock = false;
        menuBarOff.SaveSettings();
        var bothOff = new UserDataStore(dir.Path);
        bothOff.Load();
        Assert.False(bothOff.Settings.ShowInDock);
        Assert.False(bothOff.Settings.ShowMenuBarIcon);
    }

    [Fact]
    public void Version_20_settings_upgrade_preserves_other_values_and_icon_defaults()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 20, "language": "ko" }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
        Assert.Equal("ko", store.Settings.Language);
        Assert.True(store.Settings.ShowInDock);
        Assert.True(store.Settings.ShowMenuBarIcon);
        var rewritten = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(AppSettings.CurrentSchemaVersion, (int?)rewritten["schemaVersion"]);
        Assert.True((bool?)rewritten["showInDock"]);
        Assert.True((bool?)rewritten["showMenuBarIcon"]);
        Assert.Equal("ko", (string?)rewritten["language"]);

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);
        Assert.Equal("ko", reloaded.Settings.Language);
        Assert.True(reloaded.Settings.ShowInDock);
        Assert.True(reloaded.Settings.ShowMenuBarIcon);
    }

    [Fact]
    public void Older_Filee_round_trip_restores_missing_icon_defaults()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Language = "ko";
        store.Settings.ShowInDock = false;
        store.Settings.ShowMenuBarIcon = false;
        store.SaveSettings();

        var path = Path.Combine(dir.Path, "settings.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["schemaVersion"] = 20;
        Assert.True(json.Remove("showInDock"));
        Assert.True(json.Remove("showMenuBarIcon"));
        File.WriteAllText(path, json.ToJsonString());

        var migrated = new UserDataStore(dir.Path);
        migrated.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.Settings.SchemaVersion);
        Assert.True(migrated.Settings.ShowInDock);
        Assert.True(migrated.Settings.ShowMenuBarIcon);
        Assert.Equal("ko", migrated.Settings.Language);
        Assert.False(File.Exists(path + ".broken"));

        var rewritten = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(AppSettings.CurrentSchemaVersion, (int?)rewritten["schemaVersion"]);
        Assert.True((bool?)rewritten["showInDock"]);
        Assert.True((bool?)rewritten["showMenuBarIcon"]);
        Assert.Equal("ko", (string?)rewritten["language"]);

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);
        Assert.Equal("ko", reloaded.Settings.Language);
        Assert.True(reloaded.Settings.ShowInDock);
        Assert.True(reloaded.Settings.ShowMenuBarIcon);
    }

    [Theory]
    [InlineData("showInDock", "null")]
    [InlineData("showInDock", "\"future\"")]
    [InlineData("showInDock", "42")]
    [InlineData("showMenuBarIcon", "null")]
    [InlineData("showMenuBarIcon", "\"future\"")]
    [InlineData("showMenuBarIcon", "42")]
    public void Unexpected_icon_preference_values_default_on_without_losing_settings(string field, string value)
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Language = "ko";
        store.Settings.ShowInDock = field == "showInDock";
        store.Settings.ShowMenuBarIcon = field == "showMenuBarIcon";
        store.SaveSettings();

        var path = Path.Combine(dir.Path, "settings.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        File.WriteAllText(path, json.ToJsonString());

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.Equal("ko", reloaded.Settings.Language);
        Assert.True(field == "showInDock" ? reloaded.Settings.ShowInDock : reloaded.Settings.ShowMenuBarIcon);
        Assert.False(field == "showInDock" ? reloaded.Settings.ShowMenuBarIcon : reloaded.Settings.ShowInDock);
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Hardware_encoding_is_saved_and_stays_off_for_presets_from_before_it()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.All(store.Presets, p => Assert.False(p.Media.HardwareEncoding)); // no built-in preset turns it on
        store.Presets.Add(new Preset { Id = "fast", Name = "Fast", TargetFormat = "mp4", Media = { HardwareEncoding = true, VideoCodec = VideoCodec.Hevc } });
        store.Presets.Add(new Preset { Id = "old", Name = "Old", TargetFormat = "mp4" });
        store.SaveLibrary();

        // A Filee from before the option saved no "hardwareEncoding".
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        Assert.True((bool)presets.Single(p => (string?)p!["id"] == "fast")!["media"]!["hardwareEncoding"]!);
        Assert.True(presets.Single(p => (string?)p!["id"] == "old")!["media"]!.AsObject().Remove("hardwareEncoding"));
        File.WriteAllText(path, presets.ToJsonString());

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        var fast = reloaded.FindPreset("fast")!;
        Assert.True(fast.Media.HardwareEncoding);
        Assert.Equal(VideoCodec.Hevc, fast.Media.VideoCodec);
        Assert.True(fast.Clone().Media.HardwareEncoding);
        Assert.False(reloaded.FindPreset("old")!.Media.HardwareEncoding);
    }

    [Fact]
    public void Animation_speed_is_saved_and_is_the_original_speed_for_presets_from_before_it()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.All(store.Presets, p => Assert.Equal(1, p.Image.AnimationSpeed)); // no built-in preset changes it
        store.Presets.Add(new Preset { Id = "slow", Name = "Slow", TargetFormat = "gif", Image = { AnimationSpeed = 0.5 } });
        store.Presets.Add(new Preset { Id = "old", Name = "Old", TargetFormat = "gif" });
        store.Presets.Add(new Preset { Id = "odd", Name = "Odd", TargetFormat = "gif" });
        store.SaveLibrary();

        // A Filee from before the option saved no "animationSpeed"; a hand-edited file may hold anything.
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        Assert.Equal(0.5, (double)presets.Single(p => (string?)p!["id"] == "slow")!["image"]!["animationSpeed"]!);
        Assert.True(presets.Single(p => (string?)p!["id"] == "old")!["image"]!.AsObject().Remove("animationSpeed"));
        presets.Single(p => (string?)p!["id"] == "odd")!["image"]!["animationSpeed"] = -3;
        File.WriteAllText(path, presets.ToJsonString());

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        var slow = reloaded.FindPreset("slow")!;
        Assert.Equal(0.5, slow.Image.AnimationSpeed);
        Assert.Equal(0.5, slow.Clone().Image.AnimationSpeed);
        Assert.True(slow.Image.ChangesSpeed);
        Assert.Equal(1, reloaded.FindPreset("old")!.Image.AnimationSpeed);
        Assert.False(reloaded.FindPreset("old")!.Image.ChangesSpeed);
        Assert.Equal(1, reloaded.FindPreset("odd")!.Image.PlaybackSpeed); // a speed that makes no sense plays as made
    }

    [Fact]
    public void History_is_kept_until_turned_off_and_can_be_cleared()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();

        store.AddHistory(new Filee.Core.History.HistoryEntry { JobId = "a" });
        Assert.Single(store.History);

        store.Settings.KeepHistory = false;
        store.AddHistory(new Filee.Core.History.HistoryEntry { JobId = "b" });
        Assert.Single(store.History); // not recorded

        store.ClearHistory();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Empty(reloaded.History);
    }

    [Fact]
    public void History_keeps_the_newest_entries_up_to_the_limit()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.Equal(200, store.Settings.HistoryLimit);

        store.Settings.HistoryLimit = 60;
        for (var i = 0; i < 70; i++)
            store.AddHistory(new Filee.Core.History.HistoryEntry { JobId = $"{i}" });
        Assert.Equal(60, store.History.Count);
        Assert.Equal("69", store.History[0].JobId);
        Assert.Equal("10", store.History[^1].JobId);

        // Lowered below the smallest limit: the oldest go with the next conversion, down to that smallest limit.
        store.Settings.HistoryLimit = 0;
        store.SaveSettings();
        store.AddHistory(new Filee.Core.History.HistoryEntry { JobId = "70" });
        Assert.Equal(AppSettings.MinHistoryLimit, store.History.Count);

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(0, reloaded.Settings.HistoryLimit);
        Assert.Equal(AppSettings.MinHistoryLimit, reloaded.Settings.EffectiveHistoryLimit);
        Assert.Equal(AppSettings.MinHistoryLimit, reloaded.History.Count);
    }

    [Fact]
    public void Version_1_settings_are_upgraded_to_the_current_engine_list()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 1, "enginePriority": ["magick", "word", "hancom", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "pdf-text", "pdf-slides", "email", "data", "vector", "icns", "font", "cad", "model3d", "geo", "archive", "ebook", "subtitle", "wic", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_3_libraries_get_the_text_profile_once()
    {
        using var dir = new TempDir();
        var old = new UserDataStore(dir.Path);
        old.Load();
        old.Profiles.RemoveAll(p => p.Id == "text"); // as written by 1.0.0
        old.Settings.SchemaVersion = 3;
        old.Settings.EnginePriority.Remove("markdown");
        old.SaveSettings();
        old.SaveLibrary();

        var store = new UserDataStore(dir.Path);
        store.Load();

        var ids = store.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("text")); // right before the fallback
        Assert.Contains("md", store.Profiles.Single(p => p.Id == "text").Extensions);
        Assert.Equal(store.Settings.EnginePriority.IndexOf("hwpx-writer") - 1, store.Settings.EnginePriority.IndexOf("markdown"));

        // Saved right away: the next start neither migrates again nor brings back a profile the user deleted.
        store.Profiles.RemoveAll(p => p.Id == "text");
        store.SaveLibrary();
        var next = new UserDataStore(dir.Path);
        next.Load();
        Assert.DoesNotContain(next.Profiles, p => p.Id == "text");
        Assert.Equal(AppSettings.CurrentSchemaVersion, next.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_4_libraries_get_spreadsheet_and_presentation_profiles()
    {
        using var dir = new TempDir();
        var old = new UserDataStore(dir.Path);
        old.Load();
        // As written by 1.0.1: one documents donut for Word, Excel and PowerPoint files, Word-only PDF targets.
        old.Profiles.RemoveAll(p => p.Id is "spreadsheets" or "presentations");
        old.Profiles.Single(p => p.Id == "office").Extensions.AddRange(["xlsx", "xls", "ods", "csv", "pptx", "ppt", "odp"]);
        old.Profiles.Single(p => p.Id == "pdf").PresetIds.AddRange(["to-docx", "to-hwpx"]);
        old.Presets.RemoveAll(p => p.Id is "to-xlsx" or "to-csv");
        old.Settings.SchemaVersion = 4;
        old.Settings.EnginePriority = ["magick", "word", "markdown", "hwpx-writer", "pandoc", "libreoffice"];
        old.SaveSettings();
        old.SaveLibrary();

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "pdf-text", "pdf-slides", "email", "data", "vector", "icns", "font", "cad", "model3d", "geo", "archive", "ebook", "subtitle", "wic", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
        var ids = store.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("office") + 1, ids.IndexOf("spreadsheets"));
        Assert.Equal(ids.IndexOf("office") + 2, ids.IndexOf("presentations"));
        Assert.DoesNotContain("xlsx", store.Profiles.Single(p => p.Id == "office").Extensions);
        Assert.Contains("to-csv", store.Profiles.Single(p => p.Id == "spreadsheets").PresetIds);
        Assert.NotNull(store.FindPreset("to-xlsx"));
        Assert.DoesNotContain("to-hwpx", store.Profiles.Single(p => p.Id == "pdf").PresetIds); // Word-only target removed by v5
    }

    [Fact]
    public void Version_5_libraries_get_the_format_catalog()
    {
        // As written by 1.0.x with schema 5: no catalog presets, donuts or extensions.
        var presets = BuiltInData.CreatePresets()
            .Where(p => BuiltInData.CatalogPresets().Concat(BuiltInData.CodecAndSubtitlePresets()).Concat(BuiltInData.ModelPresets()).Concat(BuiltInData.GeoPresets()).All(c => c.Id != p.Id)).ToList();
        var catalogIds = BuiltInData.CatalogProfiles().Select(p => p.Id).Append("subtitles").Append("models").Append("geo").ToHashSet();
        var profiles = BuiltInData.CreateProfiles().Where(p => !catalogIds.Contains(p.Id)).ToList();
        profiles.Single(p => p.Id == "images").Extensions = ["png", "jpg"];
        profiles.Single(p => p.Id == "pdf").PresetIds.Remove("to-docx");
        profiles.Single(p => p.Id == "mixed").PresetIds.Remove("zip-all");
        // A donut of the user already handles MP3.
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["mp3"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(5, presets, profiles));

        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("text") + 1, ids.IndexOf("ebooks"));
        Assert.Equal(ids.IndexOf("fonts") + 1, ids.IndexOf("data")); // schema 14
        Assert.Equal(ids.IndexOf("data") + 1, ids.IndexOf("models")); // schema 15
        Assert.Equal(ids.IndexOf("models") + 1, ids.IndexOf("geo")); // schema 25
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("geo"));
        Assert.Equal(ids.IndexOf("audio") + 1, ids.IndexOf("subtitles"));
        Assert.Contains("cr2", profiles.Single(p => p.Id == "images").Extensions);
        Assert.DoesNotContain("mp3", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.Contains("flac", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.Contains(presets, p => p.Id == "to-mp4");
        Assert.Equal(["to-png", "to-jpg", "to-docx"], profiles.Single(p => p.Id == "pdf").PresetIds.Take(3));
        Assert.Contains("zip-all", profiles.Single(p => p.Id == "mixed").PresetIds);
        // Every extension belongs to one donut only.
        var all = profiles.SelectMany(p => p.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Version_6_libraries_get_the_conversions_that_were_on_no_donut()
    {
        // As written by 1.3.0: no HWP preset, and the donuts below without the targets 1.3.1 added.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-hwp");
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.PresetIds.Remove("to-hwp");
        profiles.Single(p => p.Id == "pdf").PresetIds.RemoveAll(id => id is "to-md" or "to-epub");
        profiles.Single(p => p.Id == "office").PresetIds.Remove("to-png");
        profiles.Single(p => p.Id == "hwp").PresetIds.RemoveAll(id => id is "to-jpg" or "to-epub");

        Assert.True(SettingsMigrations.ApplyToLibrary(6, presets, profiles));

        // The same donuts as a new install.
        Assert.NotNull(presets.SingleOrDefault(p => p.Id == "to-hwp"));
        foreach (var expected in BuiltInData.CreateProfiles())
            Assert.Equal(expected.PresetIds, profiles.Single(p => p.Id == expected.Id).PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(6, presets, profiles)); // once only
    }

    [Fact]
    public void Version_8_libraries_get_the_long_image_preset_after_jpg_and_a_heic_preset()
    {
        // As written by 1.9.0: no "Long image" or HEIC preset, and the PDF donut without the long image.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "pdf-long-image" or "to-heic");
        var profiles = BuiltInData.CreateProfiles();
        var pdf = profiles.Single(p => p.Id == "pdf");
        pdf.PresetIds.Remove("pdf-long-image");

        Assert.True(SettingsMigrations.ApplyToLibrary(8, presets, profiles));

        var longImage = presets.Single(p => p.Id == "pdf-long-image");
        Assert.True(longImage.Pdf.CombinePages);
        Assert.Equal(pdf.PresetIds.IndexOf("to-jpg") + 1, pdf.PresetIds.IndexOf("pdf-long-image"));
        Assert.Equal("heic", presets.Single(p => p.Id == "to-heic").TargetFormat);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("to-heic"));
        Assert.False(SettingsMigrations.ApplyToLibrary(8, presets, profiles)); // once only
    }

    [Fact]
    public void Version_9_libraries_get_the_remove_background_preset_on_no_donut()
    {
        // As written by 1.10.x: no "Remove background" preset.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "remove-background");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(9, presets, profiles));

        var preset = presets.Single(p => p.Id == "remove-background");
        Assert.True(preset.Image.RemoveBackground);
        Assert.Equal("png", preset.TargetFormat);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("remove-background"));
        Assert.False(SettingsMigrations.ApplyToLibrary(9, presets, profiles)); // once only
    }

    [Fact]
    public void Version_18_libraries_get_the_extract_frames_presets_after_the_pdf_presets_on_no_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "extract-frames-png" or "extract-frames-jpg");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(18, presets, profiles));

        var pngPreset = presets.Single(p => p.Id == "extract-frames-png");
        Assert.True(pngPreset.Media.SaveEveryFrame);
        Assert.Equal("png", pngPreset.TargetFormat);

        var jpgPreset = presets.Single(p => p.Id == "extract-frames-jpg");
        Assert.True(jpgPreset.Media.SaveEveryFrame);
        Assert.Equal("jpg", jpgPreset.TargetFormat);

        // Where a new library has them.
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        var slides = presets.FindIndex(p => p.Id == "pdf-slides");
        Assert.Equal(["extract-frames-png", "extract-frames-jpg"], presets.Skip(slides + 1).Take(2).Select(p => p.Id));

        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("extract-frames-png"));
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("extract-frames-jpg"));
        Assert.False(SettingsMigrations.ApplyToLibrary(18, presets, profiles)); // once only
    }

    [Fact]
    public void Version_13_libraries_get_the_data_donut_msg_files_and_json_for_spreadsheets()
    {
        // As written with schema 13: no data presets or donut, no MSG on the documents donut, no JSON on the spreadsheets
        // donut; and a donut of the user that already takes YAML.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => BuiltInData.DataPresets().Any(d => d.Id == p.Id));
        var profiles = BuiltInData.CreateProfiles();
        profiles.RemoveAll(p => p.Id == "data");
        profiles.Single(p => p.Id == "office").Extensions.Remove("msg");
        profiles.Single(p => p.Id == "spreadsheets").PresetIds.Remove("to-json");
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["yml"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(13, presets, profiles));

        Assert.All(BuiltInData.DataPresets(), d => Assert.Contains(presets, p => p.Id == d.Id));
        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("fonts") + 1, ids.IndexOf("data"));
        var data = profiles.Single(p => p.Id == "data");
        Assert.Contains("json", data.Extensions);
        Assert.Contains("yaml", data.Extensions);
        Assert.DoesNotContain("yml", data.Extensions); // the user's donut keeps it
        Assert.Equal(["to-json", "to-yaml", "to-toml", "to-xml", "to-csv", "to-xlsx"], data.PresetIds);
        Assert.Contains("msg", profiles.Single(p => p.Id == "office").Extensions);
        var sheets = profiles.Single(p => p.Id == "spreadsheets").PresetIds;
        Assert.Equal(sheets.IndexOf("to-csv") + 1, sheets.IndexOf("to-json"));
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("to-eml") || p.PresetIds.Contains("to-vcf"));
        Assert.False(SettingsMigrations.ApplyToLibrary(13, presets, profiles)); // once only

        var all = profiles.SelectMany(p => p.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Version_13_settings_get_the_data_engine_after_email()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 13, "enginePriority": ["magick", "email", "vector", "pandoc", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        // ... and, from schema 15, the 3D model engine before Pandoc (no CAD or archive engine in this list), from
        // schema 18 the PDF slides engine there too (no PDF text engine), from schema 21 the WIC engine (no FFmpeg),
        // from schema 25 the geospatial engine after the 3D models engine.
        Assert.Equal(["magick", "email", "data", "vector", "model3d", "geo", "pdf-slides", "wic", "pandoc", "libreoffice"], store.Settings.EnginePriority);
    }

    [Fact]
    public void Version_12_libraries_open_the_new_image_formats_on_the_images_donut()
    {
        // As written with schema 12: the images donut knows no APNG, QOI, DICOM or .hif; a donut of the user takes DDS.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "apng" or "qoi" or "dcm" or "hif" or "dds");
        profiles.Insert(0, new ToolbarProfile { Id = "game", Name = "Game textures", Extensions = ["dds"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(12, presets, profiles));

        Assert.Contains("apng", images.Extensions);
        Assert.Contains("qoi", images.Extensions);
        Assert.Contains("dcm", images.Extensions);
        Assert.Contains("hif", images.Extensions);
        Assert.DoesNotContain("dds", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(12, presets, profiles)); // once only
    }

    [Fact]
    public void Version_11_libraries_get_codecs_new_audio_formats_and_a_subtitles_donut()
    {
        // As written with schema 11: no HEVC / AV1 / ALAC / subtitle presets, no subtitles donut, the video donut and
        // the audio extensions of that time, and a donut of the user that already takes .ape.
        var added = BuiltInData.CodecAndSubtitlePresets().Select(p => p.Id).ToHashSet();
        var presets = BuiltInData.CreatePresets().Where(p => !added.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "subtitles").ToList();
        foreach (var profile in profiles)
            profile.PresetIds.RemoveAll(added.Contains);
        var audio = profiles.Single(p => p.Id == "audio");
        audio.Extensions.RemoveAll(e => e is "wv" or "tta" or "w64" or "eac3" or "ec3" or "ape" or "mpc" or "mpp" or "mp+" or "tak"
            or "dsf" or "dff" or "dts" or "spx");
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["ape"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(11, presets, profiles));

        Assert.Contains("wv", audio.Extensions);
        Assert.Contains("dsf", audio.Extensions);
        Assert.DoesNotContain("ape", audio.Extensions); // the user's donut keeps it
        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("audio") + 1, ids.IndexOf("subtitles"));
        Assert.Equal(BuiltInData.SubtitleProfile().PresetIds, profiles.Single(p => p.Id == "subtitles").PresetIds);
        Assert.Contains("smi", profiles.Single(p => p.Id == "subtitles").Extensions);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "video").PresetIds, profiles.Single(p => p.Id == "video").PresetIds);
        Assert.Equal(VideoCodec.Hevc, presets.Single(p => p.Id == "mp4-hevc").Media.VideoCodec);
        Assert.Equal(AudioCodec.Alac, presets.Single(p => p.Id == "to-alac").Media.AudioCodec);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("to-alac") || p.PresetIds.Contains("mp4-av1"));
        var all = profiles.SelectMany(p => p.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.False(SettingsMigrations.ApplyToLibrary(11, presets, profiles)); // once only
    }

    [Fact]
    public void Version_10_libraries_get_kepub_and_the_new_document_extensions()
    {
        // As written by 1.11.x: no KEPUB preset, and the donuts without the formats added in schema 11.
        string[] added = ["org", "adoc", "typ", "mediawiki", "wiki", "dbk", "ipynb", "fodt", "ott", "cwk", "602", "ots", "fods", "otp", "fodp", "fodg", "kepub.epub"];
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-kepub");
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
        {
            profile.PresetIds.Remove("to-kepub");
            profile.Extensions.RemoveAll(added.Contains);
        }
        // A donut of the user already handles Org files.
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["org"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(10, presets, profiles));

        Assert.Equal("kepub", presets.Single(p => p.Id == "to-kepub").TargetFormat);
        var ebooks = profiles.Single(p => p.Id == "ebooks");
        Assert.Equal(ebooks.PresetIds.IndexOf("to-mobi") + 1, ebooks.PresetIds.IndexOf("to-kepub"));
        Assert.Contains("kepub.epub", ebooks.Extensions);
        Assert.Contains("typ", profiles.Single(p => p.Id == "text").Extensions);
        Assert.DoesNotContain("org", profiles.Single(p => p.Id == "text").Extensions);
        Assert.Contains("fodt", profiles.Single(p => p.Id == "office").Extensions);
        Assert.Contains("fods", profiles.Single(p => p.Id == "spreadsheets").Extensions);
        Assert.Contains("otp", profiles.Single(p => p.Id == "presentations").Extensions);
        Assert.Contains("fodg", profiles.Single(p => p.Id == "vector").Extensions);
        foreach (var expected in BuiltInData.CreateProfiles())
            Assert.Equal(expected.PresetIds, profiles.Single(p => p.Id == expected.Id).PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(10, presets, profiles)); // once only
    }

    [Fact]
    public void Version_14_libraries_get_3d_model_presets_and_their_donut_after_data()
    {
        // As written with schema 14: no 3D formats.
        var modelIds = BuiltInData.ModelPresets().Select(p => p.Id).ToHashSet();
        var geoIds = BuiltInData.GeoPresets().Select(p => p.Id).ToHashSet();
        var presets = BuiltInData.CreatePresets().Where(p => !modelIds.Contains(p.Id) && !geoIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id is not ("models" or "geo")).ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(14, presets, profiles));

        Assert.Equal(["to-glb", "to-stl", "to-obj", "to-3mf", "to-ply", "to-gltf"], presets.Where(p => modelIds.Contains(p.Id)).Select(p => p.Id));
        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("data") + 1, ids.IndexOf("models"));
        Assert.Equal(ids.IndexOf("models") + 1, ids.IndexOf("geo")); // and, from schema 25, the geospatial donut
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("geo"));
        var models = profiles.Single(p => p.Id == "models");
        Assert.Equal(["glb", "gltf", "stl", "obj", "3mf", "ply", "fbx"], models.Extensions);
        Assert.Equal(BuiltInData.ModelProfile().PresetIds, models.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(14, presets, profiles)); // once only
    }

    [Fact]
    public void The_3d_donut_leaves_out_extensions_a_profile_of_the_user_takes()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id is not ("models" or "geo")).ToList();
        profiles.Insert(0, new ToolbarProfile { Id = "printing", Name = "Printing", Extensions = ["stl", "3mf"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(14, presets, profiles));

        Assert.Equal(["glb", "gltf", "obj", "ply", "fbx"], profiles.Single(p => p.Id == "models").Extensions);
    }

    [Fact]
    public void Version_21_libraries_learn_fbx_on_the_3d_donut_and_nothing_else()
    {
        // As written with schema 21: no FBX, and the user took PLY off the 3D donut.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var models = profiles.Single(p => p.Id == "models");
        models.Extensions.RemoveAll(e => e is "fbx" or "ply");

        Assert.True(SettingsMigrations.ApplyToLibrary(21, presets, profiles));

        Assert.Equal(["glb", "gltf", "stl", "obj", "3mf", "fbx"], models.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(21, presets, profiles)); // once only

        // A donut of the user that already opens FBX files keeps them.
        profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "models").Extensions.Remove("fbx");
        profiles.Insert(0, new ToolbarProfile { Id = "games", Name = "Games", Extensions = ["fbx"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(21, presets, profiles));
        Assert.DoesNotContain("fbx", profiles.Single(p => p.Id == "models").Extensions);
    }

    [Fact]
    public void Version_22_libraries_learn_mhtml_on_the_text_donut_and_nothing_else()
    {
        // As written with schema 22: no MHTML, and the user took Markdown off the text donut.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var text = profiles.Single(p => p.Id == "text");
        text.Extensions.RemoveAll(e => e is "mhtml" or "mht" or "md" or "markdown");

        Assert.True(SettingsMigrations.ApplyToLibrary(22, presets, profiles));

        Assert.Contains("mhtml", text.Extensions);
        Assert.Contains("mht", text.Extensions);
        Assert.DoesNotContain("md", text.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(22, presets, profiles)); // once only

        // A donut of the user that already opens .mht files keeps them; .mhtml still goes to the text donut.
        profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "text").Extensions.RemoveAll(e => e is "mhtml" or "mht");
        profiles.Insert(0, new ToolbarProfile { Id = "web", Name = "Web", Extensions = ["mht"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(22, presets, profiles));
        Assert.Equal(["mhtml"], profiles.Single(p => p.Id == "text").Extensions.Where(e => e.StartsWith("mht", StringComparison.Ordinal)));
    }

    [Fact]
    public void Version_23_libraries_learn_potm_on_the_presentation_donut_and_nothing_else()
    {
        // As written with schema 23: no POTM, and the user took ODP off the presentation donut.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var slides = profiles.Single(p => p.Id == "presentations");
        slides.Extensions.RemoveAll(e => e is "potm" or "odp");

        Assert.True(SettingsMigrations.ApplyToLibrary(23, presets, profiles));

        Assert.Contains("potm", slides.Extensions);
        Assert.DoesNotContain("odp", slides.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(23, presets, profiles)); // once only

        // A donut of the user that already opens .potm files keeps them.
        profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "presentations").Extensions.Remove("potm");
        profiles.Insert(0, new ToolbarProfile { Id = "templates", Name = "Templates", Extensions = ["potm"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(23, presets, profiles));
        Assert.DoesNotContain("potm", profiles.Single(p => p.Id == "presentations").Extensions);
    }

    [Fact]
    public void Version_26_libraries_learn_ofd_on_the_office_donut_and_nothing_else()
    {
        // As written with schema 26: no OFD, and the user took RTF off the office donut.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var office = profiles.Single(p => p.Id == "office");
        office.Extensions.RemoveAll(e => e is "ofd" or "rtf");

        Assert.True(SettingsMigrations.ApplyToLibrary(26, presets, profiles));

        Assert.Contains("ofd", office.Extensions);
        Assert.DoesNotContain("rtf", office.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(26, presets, profiles)); // once only

        // A donut of the user that already opens .ofd files keeps them.
        profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "office").Extensions.Remove("ofd");
        profiles.Insert(0, new ToolbarProfile { Id = "invoices", Name = "Invoices", Extensions = ["ofd"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(26, presets, profiles));
        Assert.DoesNotContain("ofd", profiles.Single(p => p.Id == "office").Extensions);
    }

    [Fact]
    public void Version_24_libraries_get_geospatial_presets_and_their_donut_after_models()
    {
        // As written with schema 24: no geospatial formats.
        var geoIds = BuiltInData.GeoPresets().Select(p => p.Id).ToHashSet();
        var presets = BuiltInData.CreatePresets().Where(p => !geoIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "geo").ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(24, presets, profiles));

        Assert.Equal(["to-geojson", "to-shp", "to-kml", "to-gpx", "to-kmz"], presets.Where(p => geoIds.Contains(p.Id)).Select(p => p.Id));
        Assert.Equal(["geojson", "shp", "kml", "gpx"], presets.Where(p => geoIds.Contains(p.Id)).Select(p => p.TargetFormat).Take(4));
        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("models") + 1, ids.IndexOf("geo"));
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("geo"));
        var geo = profiles.Single(p => p.Id == "geo");
        Assert.Equal(["geojson", "kml", "kmz", "gpx", "shp"], geo.Extensions);
        Assert.Equal(BuiltInData.GeoProfile().PresetIds, geo.PresetIds);
        Assert.All(geo.PresetIds, id => Assert.Contains(presets, p => p.Id == id));
        Assert.False(SettingsMigrations.ApplyToLibrary(24, presets, profiles)); // once only
    }

    [Fact]
    public void The_geospatial_donut_leaves_out_extensions_a_profile_of_the_user_takes_and_is_skipped_when_all_are_taken()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "geo").ToList();
        profiles.Insert(0, new ToolbarProfile { Id = "earth", Name = "Earth", Extensions = ["kml", "kmz"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(24, presets, profiles));
        Assert.Equal(["geojson", "gpx", "shp"], profiles.Single(p => p.Id == "geo").Extensions);

        var taken = BuiltInData.CreateProfiles().Where(p => p.Id != "geo").ToList();
        taken.Insert(0, new ToolbarProfile { Id = "all", Name = "All", Extensions = ["geojson", "kml", "kmz", "gpx", "shp"] });
        var geoIds = BuiltInData.GeoPresets().Select(p => p.Id).ToHashSet();
        var withoutPresets = BuiltInData.CreatePresets().Where(p => !geoIds.Contains(p.Id)).ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(24, withoutPresets, taken)); // the presets are still added
        Assert.DoesNotContain(taken, p => p.Id == "geo");
        Assert.Contains(withoutPresets, p => p.Id == "to-shp");
    }

    [Fact]
    public void A_geospatial_donut_of_the_user_is_not_replaced()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var mine = profiles.Single(p => p.Id == "geo");
        mine.PresetIds = ["to-kml"];
        mine.Extensions = ["kml"];

        SettingsMigrations.ApplyToLibrary(24, presets, profiles);

        var geo = profiles.Single(p => p.Id == "geo");
        Assert.Equal(["to-kml"], geo.PresetIds);
        // The extensions of the default donut that no donut takes yet are added to it, after the user's own.
        Assert.Equal(["kml", "geojson", "kmz", "gpx", "shp"], geo.Extensions);
    }

    [Fact]
    public void Version_24_settings_get_the_geo_engine_after_the_3d_models_engine()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 24, "enginePriority": ["magick", "cad", "model3d", "archive", "pandoc", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "cad", "model3d", "geo", "archive", "pandoc", "libreoffice"], store.Settings.EnginePriority);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void A_new_library_has_the_geospatial_donut_with_presets_that_exist()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();

        var ids = store.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("models") + 1, ids.IndexOf("geo"));
        var geo = store.Profiles.Single(p => p.Id == "geo");
        Assert.All(geo.PresetIds, id => Assert.NotNull(store.FindPreset(id)));
        Assert.Equal("shp", store.FindPreset("to-shp")!.TargetFormat);
        Assert.Equal("csv", store.FindPreset("to-csv")!.TargetFormat);
    }

    [Fact]
    public void Version_27_libraries_get_one_zip_on_the_archives_donut_after_extract()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.PresetIds.Remove("zip-all");

        Assert.True(SettingsMigrations.ApplyToLibrary(27, presets, profiles));
        Assert.Equal(["extract", "zip-all", "to-zip", "to-7z", "to-tgz", "to-tar"], archives.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(27, presets, profiles)); // once only

        // A library without the preset (the user deleted it) doesn't get it back, and a full donut stays full.
        presets.RemoveAll(p => p.Id == "zip-all");
        archives.PresetIds.Remove("zip-all");
        Assert.False(SettingsMigrations.ApplyToLibrary(27, presets, profiles));
        Assert.DoesNotContain("zip-all", archives.PresetIds);

        presets = BuiltInData.CreatePresets();
        archives.PresetIds = [.. presets.Where(p => p.Id != "zip-all").Take(ToolbarProfile.MaxSlices).Select(p => p.Id)];
        Assert.False(SettingsMigrations.ApplyToLibrary(27, presets, profiles));
        Assert.DoesNotContain("zip-all", archives.PresetIds);
    }

    [Fact]
    public void Version_28_libraries_get_extract_live_video_on_no_donut_and_learn_livp()
    {
        // As written with schema 28: no "Extract Live Photo video", no .livp on the images donut, and the user took
        // HEIC off it.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "extract-motion");
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "livp" or "heic");

        Assert.True(SettingsMigrations.ApplyToLibrary(28, presets, profiles));

        var motion = presets.Single(p => p.Id == "extract-motion");
        Assert.True(motion.Media.ExtractMotionPhoto);
        Assert.Equal("mp4", motion.TargetFormat);
        Assert.Equal("extract-frames-jpg", presets[presets.IndexOf(motion) - 1].Id);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("extract-motion"));
        Assert.Contains("livp", images.Extensions);
        Assert.DoesNotContain("heic", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(28, presets, profiles)); // once only

        // Back from an older Filee, which dropped the option it didn't know: the preset extracts again, its name stays.
        motion.Media.ExtractMotionPhoto = false;
        motion.Name = "Live video";
        Assert.True(SettingsMigrations.ApplyToLibrary(28, presets, profiles));
        Assert.True(motion.Media.ExtractMotionPhoto);
        Assert.Equal("Live video", motion.Name);
        Assert.Single(presets, p => p.Id == "extract-motion");
    }

    [Fact]
    public void Version_30_libraries_get_the_live_photo_presets_after_extract_live_video_and_behind_the_video_donut()
    {
        // As written with schema 30: no Live / Motion Photo presets, and the video donut's back face without them.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "live-photo-iphone" or "motion-photo-android");
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.BackPresetIds!.RemoveAll(id => id is "live-photo-iphone" or "motion-photo-android");

        Assert.True(SettingsMigrations.ApplyToLibrary(30, presets, profiles));

        var iphone = presets.Single(p => p.Id == "live-photo-iphone");
        var android = presets.Single(p => p.Id == "motion-photo-android");
        Assert.Equal((LivePhotoKind.Apple, "jpg"), (iphone.Media.LivePhoto, iphone.TargetFormat));
        Assert.Equal((LivePhotoKind.Android, "jpg"), (android.Media.LivePhoto, android.TargetFormat));
        // Where a new library has them, on no donut.
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.Equal("extract-motion", presets[presets.IndexOf(iphone) - 1].Id);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("live-photo-iphone") || p.PresetIds.Contains("motion-photo-android"));
        // Behind the video donut as in a new one, after "Extract frames".
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "video").BackPresetIds, video.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(30, presets, profiles)); // once only

        // A back face the user filled up stays as it is.
        var full = BuiltInData.CreateProfiles();
        var fullBack = full.Single(p => p.Id == "video").BackPresetIds!;
        fullBack.RemoveAll(id => id is "live-photo-iphone" or "motion-photo-android");
        while (fullBack.Count < ToolbarProfile.MaxSlices)
            fullBack.Add("to-mp3");
        var before = fullBack.ToList();
        SettingsMigrations.ApplyToLibrary(30, BuiltInData.CreatePresets(), full);
        Assert.Equal(before, fullBack);

        // Back from an older Filee, which dropped the option it didn't know: the preset makes Live Photos again.
        iphone.Media.LivePhoto = LivePhotoKind.None;
        iphone.Name = "My Live Photo";
        Assert.True(SettingsMigrations.ApplyToLibrary(30, presets, profiles));
        Assert.Equal(LivePhotoKind.Apple, iphone.Media.LivePhoto);
        Assert.Equal("My Live Photo", iphone.Name);
        Assert.Single(presets, p => p.Id == "live-photo-iphone");
    }

    [Fact]
    public void Version_35_libraries_learn_icalendar_extensions_and_preset_without_overriding_user_choices()
    {
        using var fullDir = new TempDir();
        var full = new UserDataStore(fullDir.Path);
        full.Load();
        full.Settings.SchemaVersion = 35;
        var data = full.Profiles.Single(p => p.Id == "data");
        data.Extensions.RemoveAll(e => e is "ics" or "ical");
        full.Profiles.Insert(0, new ToolbarProfile { Id = "my-calendar", Name = "My calendar", Extensions = ["ics"] });
        var fullBack = full.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!;
        fullBack.Remove("to-ics");
        fullBack.AddRange(full.Presets.Select(p => p.Id).Where(id => !fullBack.Contains(id))
            .Take(ToolbarProfile.MaxSlices - fullBack.Count));
        var fullBackBeforeMigration = fullBack.ToList();
        full.Presets.RemoveAll(p => p.Id == "to-ics");
        full.SaveSettings();
        full.SaveLibrary();

        var upgradedFull = new UserDataStore(fullDir.Path);
        upgradedFull.Load();
        Assert.Contains("ical", upgradedFull.Profiles.Single(p => p.Id == "data").Extensions);
        Assert.DoesNotContain("ics", upgradedFull.Profiles.Single(p => p.Id == "data").Extensions);
        Assert.Contains("ics", upgradedFull.Profiles.Single(p => p.Id == "my-calendar").Extensions);
        Assert.Equal(fullBackBeforeMigration, upgradedFull.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds);
        Assert.Single(upgradedFull.Presets, p => p.Id == "to-ics");

        using var roomDir = new TempDir();
        var room = new UserDataStore(roomDir.Path);
        room.Load();
        room.Settings.SchemaVersion = 35;
        foreach (var profile in room.Profiles)
            profile.Extensions.RemoveAll(e => e is "ics" or "ical");
        room.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!.Remove("to-ics");
        room.Presets.RemoveAll(p => p.Id == "to-ics");
        room.SaveSettings();
        room.SaveLibrary();

        var upgradedRoom = new UserDataStore(roomDir.Path);
        upgradedRoom.Load();
        var sheets = upgradedRoom.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!;
        Assert.Equal(sheets.IndexOf("to-vcf") + 1, sheets.IndexOf("to-ics"));
        Assert.Single(upgradedRoom.Presets, p => p.Id == "to-ics");
        upgradedRoom.Profiles.Single(p => p.Id == "data").Extensions.Remove("ics");
        upgradedRoom.SaveLibrary();

        var reloadedRoom = new UserDataStore(roomDir.Path);
        reloadedRoom.Load();
        Assert.DoesNotContain("ics", reloadedRoom.Profiles.Single(p => p.Id == "data").Extensions);
        Assert.Equal(1, reloadedRoom.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!.Count(id => id == "to-ics"));
        Assert.Single(reloadedRoom.Presets, p => p.Id == "to-ics");

        using var deletedDir = new TempDir();
        var deleted = new UserDataStore(deletedDir.Path);
        deleted.Load();
        deleted.Settings.SchemaVersion = 35;
        deleted.Profiles.RemoveAll(p => p.Id == "data");
        foreach (var profile in deleted.Profiles)
            profile.Extensions.RemoveAll(e => e is "ics" or "ical");
        deleted.Profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!.Remove("to-ics");
        deleted.Presets.RemoveAll(p => p.Id == "to-ics");
        deleted.SaveSettings();
        deleted.SaveLibrary();

        var upgradedDeleted = new UserDataStore(deletedDir.Path);
        upgradedDeleted.Load();
        Assert.DoesNotContain(upgradedDeleted.Profiles, p => p.Id == "data");
        Assert.DoesNotContain(upgradedDeleted.Profiles.SelectMany(p => p.Extensions), e => e is "ics" or "ical");
        Assert.Single(upgradedDeleted.Presets, p => p.Id == "to-ics");
    }

    [Fact]
    public void Live_photo_options_survive_saving_and_unknown_kinds_read_as_none()
    {
        var preset = new Preset
        {
            TargetFormat = "heic",
            Media = { LivePhoto = LivePhotoKind.Apple, LivePhotoStart = 2.5, LivePhotoLength = 2, LivePhotoCover = 0.5 },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(preset, FileeJsonContext.Default.Preset);
        Assert.Contains("\"livePhoto\": \"Apple\"", json);
        var read = System.Text.Json.JsonSerializer.Deserialize(json, FileeJsonContext.Default.Preset)!;
        Assert.Equal((LivePhotoKind.Apple, 2.5, 2.0, 0.5),
            (read.Media.LivePhoto, read.Media.LivePhotoStart, read.Media.LivePhotoLength, read.Media.LivePhotoCover));

        // A kind of a newer Filee: still one preset that loads, as a plain conversion.
        var future = System.Text.Json.JsonSerializer.Deserialize(json.Replace("\"Apple\"", "\"Hologram\""), FileeJsonContext.Default.Preset)!;
        Assert.Equal(LivePhotoKind.None, future.Media.LivePhoto);
    }

    [Fact]
    public void Version_15_libraries_get_the_tracing_presets_on_the_images_donut_after_pdf()
    {
        // As written before tracing: no trace presets, and the images donut without them.
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "trace-svg" or "trace-line-art");
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.PresetIds.RemoveAll(id => id is "trace-svg" or "trace-line-art");

        Assert.True(SettingsMigrations.ApplyToLibrary(15, presets, profiles));

        Assert.Equal(TracingMode.Color, presets.Single(p => p.Id == "trace-svg").Trace.Mode);
        Assert.Equal(TracingMode.BlackAndWhite, presets.Single(p => p.Id == "trace-line-art").Trace.Mode);
        Assert.All(presets.Where(p => p.Id.StartsWith("trace-", StringComparison.Ordinal)), p => Assert.Equal("svg", p.TargetFormat));
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").PresetIds, images.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(15, presets, profiles)); // once only
    }

    [Fact]
    public void Trace_options_survive_a_save_and_load()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = store.Presets.Single(p => p.Id == "trace-svg");
        preset.Trace.Colors = 6;
        preset.Trace.Detail = 80;
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var trace = reloaded.Presets.Single(p => p.Id == "trace-svg").Trace;
        Assert.Equal((TracingMode.Color, 6, 80), (trace.Mode, trace.Colors, trace.Detail));
        Assert.Equal(TracingMode.BlackAndWhite, reloaded.Presets.Single(p => p.Id == "trace-line-art").Trace.Mode);
    }

    [Fact]
    public void Version_16_libraries_get_rotate_and_flip_presets_and_groups_on_no_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "rotate-left" or "rotate-right" or "rotate-180" or "flip-horizontal" or "flip-vertical" or "rotate" or "flip");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(16, presets, profiles));

        var rotate = presets.Single(p => p.Id == "rotate");
        Assert.True(rotate.IsGroup);
        Assert.Equal(["rotate-left", "rotate-right", "rotate-180"], rotate.SubPresetIds);
        Assert.Equal(ImageRotation.Left90, presets.Single(p => p.Id == "rotate-left").Image.Rotation);
        Assert.Equal(ImageFlip.Horizontal, presets.Single(p => p.Id == "flip-horizontal").Image.Flip);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Any(id => id.StartsWith("rotate") || id.StartsWith("flip")));
        Assert.False(SettingsMigrations.ApplyToLibrary(16, presets, profiles)); // once only
        // Where a new library has them: after "Remove background".
        var background = presets.FindIndex(p => p.Id == "remove-background");
        Assert.Equal("rotate-left", presets[background + 1].Id);
    }

    /// <summary>Presets and donuts as a Filee before PDF → PPTX wrote them (schema 17).</summary>
    private static (List<Preset> Presets, List<ToolbarProfile> Profiles) Version17Library()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "pdf-slides");
        var profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "pdf").PresetIds = [.. BuiltInData.PdfDonutBefore18];
        return (presets, profiles);
    }

    [Fact]
    public void Version_17_libraries_get_pptx_slides_on_the_unchanged_pdf_donut_in_place_of_tiff()
    {
        var (presets, profiles) = Version17Library();

        Assert.True(SettingsMigrations.ApplyToLibrary(17, presets, profiles));

        var slides = presets.Single(p => p.Id == "pdf-slides");
        Assert.Equal(("pptx", "preset.pdf_slides"), (slides.TargetFormat, slides.NameKey));
        Assert.Equal("pdf-split", presets[presets.IndexOf(slides) - 1].Id); // where a new library has it
        var pdf = profiles.Single(p => p.Id == "pdf").PresetIds;
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "pdf").PresetIds, pdf);
        Assert.Equal(pdf.IndexOf("to-docx") + 1, pdf.IndexOf("pdf-slides"));
        Assert.DoesNotContain("to-tiff", pdf);
        Assert.NotNull(presets.SingleOrDefault(p => p.Id == "to-tiff")); // still a preset to add again
        Assert.False(SettingsMigrations.ApplyToLibrary(17, presets, profiles)); // once only
    }

    [Fact]
    public void A_pdf_donut_the_user_changed_gets_pptx_slides_only_when_it_has_room()
    {
        // Full, but not the default: the user's choice of twelve stays.
        var (presets, profiles) = Version17Library();
        var pdf = profiles.Single(p => p.Id == "pdf");
        pdf.PresetIds[pdf.PresetIds.IndexOf("to-tiff")] = "to-webp";

        Assert.True(SettingsMigrations.ApplyToLibrary(17, presets, profiles)); // the preset is added
        Assert.DoesNotContain("pdf-slides", pdf.PresetIds);
        Assert.Contains("to-webp", pdf.PresetIds);

        // With room: after DOCX.
        (presets, profiles) = Version17Library();
        pdf = profiles.Single(p => p.Id == "pdf");
        pdf.PresetIds.RemoveAll(id => id is "to-md" or "to-epub");

        Assert.True(SettingsMigrations.ApplyToLibrary(17, presets, profiles));
        Assert.Equal(pdf.PresetIds.IndexOf("to-docx") + 1, pdf.PresetIds.IndexOf("pdf-slides"));
        Assert.Contains("to-tiff", pdf.PresetIds);
    }

    [Fact]
    public void Version_17_settings_get_the_pdf_slides_engine_after_pdf_text()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 17, "enginePriority": ["magick", "pdfium", "docx-writer", "pdf-text", "email", "pandoc", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "pdfium", "docx-writer", "pdf-text", "pdf-slides", "email", "geo", "wic", "pandoc", "libreoffice"], store.Settings.EnginePriority);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_20_settings_get_the_wic_engine_after_the_other_built_in_engines()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 20, "enginePriority": ["magick", "pdfium", "hwpx-writer", "subtitle", "ffmpeg", "pandoc", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        // Not near the top: the HWPX writer stays among the ranks that win ties (see EbookTests).
        Assert.Equal(["magick", "pdfium", "hwpx-writer", "subtitle", "wic", "ffmpeg", "geo", "pandoc", "libreoffice"], store.Settings.EnginePriority);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_20_libraries_open_jpeg_xr_on_the_images_donut()
    {
        // As written with schema 20: no JPEG XR yet; the user took PSD off the images donut and has a donut for .wdp.
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "jxr" or "wdp" or "hdp" or "psd");
        profiles.Insert(0, new ToolbarProfile { Id = "photos", Name = "Windows Photo Gallery", Extensions = ["wdp"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(20, presets, profiles));

        Assert.Contains("jxr", images.Extensions);
        Assert.Contains("hdp", images.Extensions);
        Assert.DoesNotContain("wdp", images.Extensions);
        Assert.DoesNotContain("psd", images.Extensions); // only the new extensions are learnt
        Assert.False(SettingsMigrations.ApplyToLibrary(20, presets, profiles)); // once only
    }

    [Fact]
    public void The_close_action_is_saved_and_one_from_a_newer_Filee_reads_as_ask()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.Equal(CloseAction.Ask, store.Settings.CloseAction);
        store.Settings.CloseAction = CloseAction.Quit;
        store.SaveSettings();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(CloseAction.Quit, reloaded.Settings.CloseAction);

        var path = Path.Combine(dir.Path, "settings.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"closeAction\": \"Quit\"", json);
        File.WriteAllText(path, json.Replace("\"closeAction\": \"Quit\"", "\"closeAction\": \"Minimize\""));
        var newer = new UserDataStore(dir.Path);
        newer.Load();
        Assert.Equal(CloseAction.Ask, newer.Settings.CloseAction);
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void A_rotation_from_a_newer_Filee_reads_as_none_and_the_other_presets_still_load()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveLibrary();
        var path = Path.Combine(dir.Path, "presets.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"rotation\": \"Left90\"", json);
        File.WriteAllText(path, json.Replace("\"rotation\": \"Left90\"", "\"rotation\": \"Transpose\"")
            .Replace("\"flip\": \"Vertical\"", "\"flip\": 7"));

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.Equal(ImageRotation.None, reloaded.FindPreset("rotate-left")!.Image.Rotation);
        Assert.Equal(ImageFlip.None, reloaded.FindPreset("flip-vertical")!.Image.Flip);
        Assert.Equal(ImageRotation.Right90, reloaded.FindPreset("rotate-right")!.Image.Rotation); // the rest as saved
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Back_from_an_older_Filee_rotate_and_flip_work_again_and_keep_the_users_changes()
    {
        // What a Filee before 17 leaves after saving: kind, members and rotation dropped, the rest as it was.
        var presets = BuiltInData.CreatePresets();
        var left = presets.Single(p => p.Id == "rotate-left");
        left.Image.Rotation = ImageRotation.None;
        left.Name = "Turn left";
        var rotate = presets.Single(p => p.Id == "rotate");
        rotate.Kind = PresetKind.Convert;
        rotate.SubPresetIds = [];
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(16, presets, profiles));

        Assert.Equal(ImageRotation.Left90, left.Image.Rotation);
        Assert.Equal("Turn left", left.Name);
        Assert.True(rotate.IsGroup);
        Assert.Equal(["rotate-left", "rotate-right", "rotate-180"], rotate.SubPresetIds);
        Assert.Equal(Preset.GroupTarget, rotate.TargetFormat);
        Assert.Single(presets, p => p.Id == "rotate-left"); // repaired, not added twice
    }

    [Fact]
    public void Version_17_libraries_get_compress_pdf_after_split_pdf_on_no_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "compress-pdf");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(17, presets, profiles));

        var compress = presets.Single(p => p.Id == "compress-pdf");
        Assert.Equal(("pdf", PdfCompression.Ebook, false), (compress.TargetFormat, compress.Pdf.Compression, compress.Pdf.CompressWithGhostscript));
        Assert.Equal("pdf-split", presets[presets.IndexOf(compress) - 1].Id); // where a new library has it
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("compress-pdf"));
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "pdf").PresetIds, profiles.Single(p => p.Id == "pdf").PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(17, presets, profiles)); // once only
    }

    [Fact]
    public void Version_25_libraries_get_crop_tool_preset_after_remove_background_on_no_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "tool-crop");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(25, presets, profiles));

        var crop = presets.Single(p => p.Id == "tool-crop");
        Assert.Equal(PresetKind.Tool, crop.Kind);
        Assert.Equal("remove-background", presets[presets.IndexOf(crop) - 1].Id);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains("tool-crop"));
        Assert.False(SettingsMigrations.ApplyToLibrary(25, presets, profiles)); // once only
    }

    [Fact]
    public void Version_41_libraries_get_the_spreadsheet_print_studio_after_crop_and_on_the_back()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "tool-sheet-studio");
        var profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "spreadsheets").BackPresetIds!.Remove("tool-sheet-studio");

        Assert.True(SettingsMigrations.ApplyToLibrary(41, presets, profiles));

        var studio = presets.Single(p => p.Id == "tool-sheet-studio");
        Assert.Equal((PresetKind.Tool, "pdf"), (studio.Kind, studio.TargetFormat));
        Assert.Equal(presets.FindIndex(p => p.Id == "tool-crop") + 1, presets.IndexOf(studio)); // as in a new library
        Assert.Equal("tool-sheet-studio", profiles.Single(p => p.Id == "spreadsheets").BackPresetIds![0]);
        Assert.False(SettingsMigrations.ApplyToLibrary(41, presets, profiles));
    }

    [Fact]
    public void Version_41_libraries_keep_the_print_studio_off_the_back_when_the_front_has_it()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var spreadsheets = profiles.Single(p => p.Id == "spreadsheets");
        spreadsheets.BackPresetIds!.Remove("tool-sheet-studio");
        spreadsheets.PresetIds.Add("tool-sheet-studio"); // the user put it on the front

        SettingsMigrations.ApplyToLibrary(41, presets, profiles);

        Assert.DoesNotContain("tool-sheet-studio", spreadsheets.BackPresetIds);
        Assert.Single(presets, p => p.Id == "tool-sheet-studio");
    }

    [Fact]
    public void Back_from_an_older_Filee_tool_crop_works_again_and_keeps_the_users_changes()
    {
        // What a Filee before 26 leaves after saving: kind read as Group, TargetFormat dropped, SubPresetIds empty, the rest as it was.
        var presets = BuiltInData.CreatePresets();
        var crop = presets.Single(p => p.Id == "tool-crop");
        crop.Kind = PresetKind.Group;
        crop.TargetFormat = Preset.GroupTarget;
        crop.SubPresetIds = [];
        crop.Name = "Trim";

        Assert.True(SettingsMigrations.ApplyToLibrary(25, presets, BuiltInData.CreateProfiles()));

        Assert.Equal(PresetKind.Tool, crop.Kind);
        Assert.Equal(BuiltInData.SameAsSource, crop.TargetFormat);
        Assert.Empty(crop.SubPresetIds);
        Assert.Equal("Trim", crop.Name);
        Assert.Single(presets, p => p.Id == "tool-crop");
    }

    [Fact]
    public void Back_from_an_older_Filee_compress_pdf_compresses_again_and_keeps_the_users_changes()
    {
        // What a Filee before 20 leaves after saving: the compression level dropped, the rest as it was.
        var presets = BuiltInData.CreatePresets();
        var compress = presets.Single(p => p.Id == "compress-pdf");
        compress.Pdf.Compression = PdfCompression.None;
        compress.Name = "Shrink";

        Assert.True(SettingsMigrations.ApplyToLibrary(17, presets, BuiltInData.CreateProfiles()));

        Assert.Equal(PdfCompression.Ebook, compress.Pdf.Compression);
        Assert.Equal("Shrink", compress.Name);
        Assert.Single(presets, p => p.Id == "compress-pdf");
    }

    [Fact]
    public void Pdf_compression_survives_a_save_and_load_and_a_newer_level_reads_as_none()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = store.Presets.Single(p => p.Id == "compress-pdf");
        preset.Pdf.Compression = PdfCompression.Print;
        preset.Pdf.CompressWithGhostscript = true;
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var pdf = reloaded.Presets.Single(p => p.Id == "compress-pdf").Pdf;
        Assert.Equal((PdfCompression.Print, true), (pdf.Compression, pdf.CompressWithGhostscript));

        var path = Path.Combine(dir.Path, "presets.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"compression\": \"Print\"", "\"compression\": \"Extreme\""));
        var newer = new UserDataStore(dir.Path);
        newer.Load();
        Assert.Equal(PdfCompression.None, newer.FindPreset("compress-pdf")!.Pdf.Compression);
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Catalog_migration_ignores_extensions_checked_on_mixed_profiles()
    {
        // Schema-5 data where the user already limited a mixed-files donut to MP3 and JPG: those checks must not
        // count as "taken" when the catalog donuts and extensions are added.
        var presets = BuiltInData.CreatePresets().Where(p => BuiltInData.CatalogPresets().All(c => c.Id != p.Id)).ToList();
        var catalogIds = BuiltInData.CatalogProfiles().Select(p => p.Id).Append("models").Append("geo").ToHashSet();
        var profiles = BuiltInData.CreateProfiles().Where(p => !catalogIds.Contains(p.Id)).ToList();
        profiles.Single(p => p.Id == "images").Extensions = ["png"];
        profiles.Add(new ToolbarProfile { Id = "music-mix", Name = "Music mix", IsFallback = true, Extensions = ["mp3", "jpg"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(5, presets, profiles));

        Assert.Contains("mp3", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.Contains("jpg", profiles.Single(p => p.Id == "images").Extensions);
        Assert.Equal(["mp3", "jpg"], profiles.Single(p => p.Id == "music-mix").Extensions);
    }

    [Fact]
    public void Several_mixed_profiles_are_kept_and_one_is_added_when_none_is_left()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Profiles.Add(new ToolbarProfile { Id = "scans", Name = "Scans", IsFallback = true, Extensions = ["jpg", "pdf"] });
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(["mixed", "scans"], reloaded.Profiles.Where(p => p.IsFallback).Select(p => p.Id));

        reloaded.Profiles.RemoveAll(p => p.IsFallback);
        reloaded.SaveLibrary();
        Assert.Equal("mixed", Assert.Single(reloaded.Profiles, p => p.IsFallback).Id);
    }

    [Fact]
    public void Imported_mixed_profiles_stay_mixed()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Profiles.Add(new ToolbarProfile { Id = "scans", Name = "Scans", IsFallback = true, Extensions = ["jpg", "pdf"] });
        var bundle = Path.Combine(dir.Path, "bundle.json");
        store.Export(bundle);

        using var other = new TempDir();
        var target = new UserDataStore(other.Path);
        target.Load();
        target.Import(bundle);

        Assert.True(target.Profiles.Single(p => p.Id == "scans").IsFallback);
        Assert.Single(target.Profiles, p => p.Id == "mixed");
        Assert.Equal("scans", ProfileSelector.Select(target.Profiles, ["a.jpg", "b.pdf"])!.Id);
        Assert.Equal("mixed", ProfileSelector.Select(target.Profiles, ["a.jpg", "b.docx"])!.Id);
    }

    [Fact]
    public void Spreadsheet_profile_is_not_added_when_a_user_profile_takes_spreadsheets()
    {
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id is not ("spreadsheets" or "presentations")).ToList();
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["xlsx"] });

        SettingsMigrations.ApplyToLibrary(4, BuiltInData.CreatePresets(), profiles);

        Assert.DoesNotContain(profiles, p => p.Id == "spreadsheets");
        Assert.Contains(profiles, p => p.Id == "presentations");
        Assert.Equal(["xlsx"], profiles[0].Extensions);
    }

    [Fact]
    public void Text_profile_is_not_added_when_a_profile_already_takes_markdown()
    {
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "text").ToList();
        profiles[0].Extensions.Add("md");

        SettingsMigrations.ApplyToLibrary(3, BuiltInData.CreatePresets(), profiles);
        Assert.DoesNotContain(profiles, p => p.Id == "text");
    }

    [Fact]
    public void Enums_are_stored_as_strings()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();

        var json = File.ReadAllText(Path.Combine(dir.Path, "settings.json"));
        Assert.Contains("\"Drag\"", json);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "presets.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{ not json");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.NotEmpty(store.Presets);
        Assert.True(store.Settings.ShowInDock);
        Assert.True(store.Settings.ShowMenuBarIcon);
        Assert.True(File.Exists(Path.Combine(dir.Path, "presets.json.broken")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "settings.json.broken")));
    }

    [Fact]
    public void Dangling_preset_references_are_removed()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Profiles[0].PresetIds.Add("does-not-exist");
        store.SaveLibrary();

        Assert.DoesNotContain("does-not-exist", store.Profiles[0].PresetIds);
    }

    [Fact]
    public void Export_then_import_restores_presets()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "shared", Name = "Shared", TargetFormat = "png" });
        var bundle = Path.Combine(dir.Path, "bundle.json");
        store.Export(bundle);

        using var other = new TempDir();
        var target = new UserDataStore(other.Path);
        target.Load();
        target.Import(bundle);

        Assert.NotNull(target.FindPreset("shared"));
        Assert.Single(target.Profiles, p => p.IsFallback);
    }
}
