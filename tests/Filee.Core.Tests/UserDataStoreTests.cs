using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class UserDataStoreTests
{
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
    public void Version_1_settings_are_upgraded_to_the_current_engine_list()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 1, "enginePriority": ["magick", "word", "hancom", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "pdf-text", "email", "data", "vector", "icns", "font", "cad", "model3d", "archive", "ebook", "subtitle", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
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

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "pdf-text", "email", "data", "vector", "icns", "font", "cad", "model3d", "archive", "ebook", "subtitle", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
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
            .Where(p => BuiltInData.CatalogPresets().Concat(BuiltInData.CodecAndSubtitlePresets()).Concat(BuiltInData.ModelPresets()).All(c => c.Id != p.Id)).ToList();
        var catalogIds = BuiltInData.CatalogProfiles().Select(p => p.Id).Append("subtitles").Append("models").ToHashSet();
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
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("models"));
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

        // ... and, from schema 15, the 3D model engine before Pandoc (no CAD or archive engine in this list).
        Assert.Equal(["magick", "email", "data", "vector", "model3d", "pandoc", "libreoffice"], store.Settings.EnginePriority);
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
        var presets = BuiltInData.CreatePresets().Where(p => !modelIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "models").ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(14, presets, profiles));

        Assert.Equal(["to-glb", "to-stl", "to-obj", "to-3mf", "to-ply", "to-gltf"], presets.Where(p => modelIds.Contains(p.Id)).Select(p => p.Id));
        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("data") + 1, ids.IndexOf("models"));
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("models"));
        var models = profiles.Single(p => p.Id == "models");
        Assert.Equal(["glb", "gltf", "stl", "obj", "3mf", "ply"], models.Extensions);
        Assert.Equal(BuiltInData.ModelProfile().PresetIds, models.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(14, presets, profiles)); // once only
    }

    [Fact]
    public void The_3d_donut_leaves_out_extensions_a_profile_of_the_user_takes()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "models").ToList();
        profiles.Insert(0, new ToolbarProfile { Id = "printing", Name = "Printing", Extensions = ["stl", "3mf"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(14, presets, profiles));

        Assert.Equal(["glb", "gltf", "obj", "ply"], profiles.Single(p => p.Id == "models").Extensions);
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
    public void Catalog_migration_ignores_extensions_checked_on_mixed_profiles()
    {
        // Schema-5 data where the user already limited a mixed-files donut to MP3 and JPG: those checks must not
        // count as "taken" when the catalog donuts and extensions are added.
        var presets = BuiltInData.CreatePresets().Where(p => BuiltInData.CatalogPresets().All(c => c.Id != p.Id)).ToList();
        var catalogIds = BuiltInData.CatalogProfiles().Select(p => p.Id).Append("models").ToHashSet();
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

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.NotEmpty(store.Presets);
        Assert.True(File.Exists(Path.Combine(dir.Path, "presets.json.broken")));
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
