// Loads and saves settings.json, presets.json, profiles.json and history.json.
// Writes are atomic (temp file + replace) so a crash never leaves a half-written file.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Settings;

/// <summary>
/// In-memory copy of all user data plus persistence. The app keeps exactly one instance.
/// </summary>
public sealed class UserDataStore
{
    private const int MaxHistory = 200;
    private readonly ILogger _log;
    private readonly object _saveLock = new();

    public UserDataStore(string directory, ILogger<UserDataStore>? log = null)
    {
        Directory = directory;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Default data directory: %APPDATA%\Filee on Windows, ~/Library/Application Support/Filee on macOS.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");

    public string Directory { get; }

    public AppSettings Settings { get; private set; } = new();
    public List<Preset> Presets { get; private set; } = BuiltInData.CreatePresets();
    public List<ToolbarProfile> Profiles { get; private set; } = BuiltInData.CreateProfiles();
    public List<HistoryEntry> History { get; private set; } = [];

    /// <summary>Raised after settings were saved.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised after presets or profiles were saved.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised after a history entry was added.</summary>
    public event EventHandler? HistoryChanged;

    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string PresetsPath => Path.Combine(Directory, "presets.json");
    private string ProfilesPath => Path.Combine(Directory, "profiles.json");
    private string HistoryPath => Path.Combine(Directory, "history.json");

    /// <summary>Loads everything from disk, falling back to defaults for missing or corrupt files.</summary>
    public void Load()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Settings = Read(SettingsPath, FileeJsonContext.Default.AppSettings) ?? new AppSettings();
        Presets = Read(PresetsPath, FileeJsonContext.Default.ListPreset) ?? BuiltInData.CreatePresets();
        Profiles = Read(ProfilesPath, FileeJsonContext.Default.ListToolbarProfile) ?? BuiltInData.CreateProfiles();
        History = Read(HistoryPath, FileeJsonContext.Default.ListHistoryEntry) ?? [];

        var schema = Settings.SchemaVersion;
        SettingsMigrations.Apply(Settings);
        var libraryChanged = File.Exists(ProfilesPath) && SettingsMigrations.ApplyToLibrary(schema, Presets, Profiles);
        EnsureConsistency();

        // Persist upgrades right away so they run once, even if the user never changes a setting.
        if (schema < AppSettings.CurrentSchemaVersion && File.Exists(SettingsPath))
            Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
        if (libraryChanged)
        {
            Write(PresetsPath, Presets, FileeJsonContext.Default.ListPreset);
            Write(ProfilesPath, Profiles, FileeJsonContext.Default.ListToolbarProfile);
        }
    }

    public void SaveSettings()
    {
        Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveLibrary()
    {
        EnsureConsistency();
        Write(PresetsPath, Presets, FileeJsonContext.Default.ListPreset);
        Write(ProfilesPath, Profiles, FileeJsonContext.Default.ListToolbarProfile);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a finished conversion, unless the user turned the history off (<see cref="AppSettings.KeepHistory"/>).</summary>
    public void AddHistory(HistoryEntry entry)
    {
        if (!Settings.KeepHistory)
            return;
        lock (_saveLock)
        {
            History.Insert(0, entry);
            if (History.Count > MaxHistory)
                History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        }
        Write(HistoryPath, History, FileeJsonContext.Default.ListHistoryEntry);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        lock (_saveLock)
            History.Clear();
        Write(HistoryPath, History, FileeJsonContext.Default.ListHistoryEntry);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces presets and profiles with factory defaults.</summary>
    public void ResetLibrary()
    {
        Presets = BuiltInData.CreatePresets();
        Profiles = BuiltInData.CreateProfiles();
        SaveLibrary();
    }

    public Preset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>The preset <paramref name="id"/> when it converts (a group doesn't), for every place that runs one.</summary>
    public Preset? FindConversion(string id) => Presets.FirstOrDefault(p => p.Id == id && !p.IsGroup);

    /// <summary>Exports presets and profiles to a shareable JSON file.</summary>
    public void Export(string path) =>
        Write(path, new PresetBundle { Presets = Presets, Profiles = Profiles }, FileeJsonContext.Default.PresetBundle);

    /// <summary>
    /// Imports presets and profiles from a bundle. Items with an existing id are replaced, new ones added.
    /// </summary>
    /// <returns>Number of imported presets + profiles.</returns>
    public int Import(string path)
    {
        using var stream = File.OpenRead(path);
        var bundle = JsonSerializer.Deserialize(stream, FileeJsonContext.Default.PresetBundle)
                     ?? throw new InvalidDataException("Empty preset bundle.");

        foreach (var preset in bundle.Presets)
        {
            Presets.RemoveAll(p => p.Id == preset.Id);
            Presets.Add(preset);
        }
        // Mixed-selection profiles stay mixed: several may exist, and the catch-all is the first one with nothing
        // checked, so an imported one added at the end never replaces ours.
        foreach (var profile in bundle.Profiles)
        {
            Profiles.RemoveAll(p => p.Id == profile.Id);
            Profiles.Add(profile);
        }
        SaveLibrary();
        return bundle.Presets.Count + bundle.Profiles.Count;
    }

    /// <summary>Removes dangling preset references and guarantees at least one mixed-selection profile.</summary>
    private void EnsureConsistency()
    {
        var ids = Presets.Select(p => p.Id).ToHashSet();
        foreach (var profile in Profiles)
        {
            profile.PresetIds = profile.PresetIds.Where(ids.Contains).Distinct().Take(ToolbarProfile.MaxSlices).ToList();
        }
        // A Filee from before groups drops "kind" (and the presets) when it saves a group, which comes back as a
        // conversion to "group": still a group, now an empty one, rather than a conversion that can never run.
        foreach (var preset in Presets.Where(p => !p.IsGroup && p.TargetFormat == Preset.GroupTarget))
            preset.Kind = PresetKind.Group;
        // A group fans out conversions only (no group in a group), each once, at most MaxSubPresets, and has no
        // target format an older Filee could run it with.
        var conversions = Presets.Where(p => !p.IsGroup).Select(p => p.Id).ToHashSet();
        foreach (var group in Presets.Where(p => p.IsGroup))
        {
            group.SubPresetIds = group.SubPresetIds.Where(conversions.Contains).Distinct().Take(Preset.MaxSubPresets).ToList();
            group.TargetFormat = Preset.GroupTarget;
        }

        // Several mixed profiles are fine (each limited to the extensions checked on it); none is not.
        if (!Profiles.Any(p => p.IsFallback))
        {
            var mixed = BuiltInData.CreateProfiles().First(p => p.IsFallback);
            mixed.PresetIds = mixed.PresetIds.Where(ids.Contains).ToList();
            Profiles.Add(mixed);
        }
    }

    private T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // Keep the broken file for inspection and continue with defaults.
            _log.LogWarning(ex, "Could not read {Path}; using defaults", path);
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
            }
            catch (IOException) { }
            return null;
        }
    }

    private void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        lock (_saveLock)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, value, type);
            File.Move(temp, path, overwrite: true);
        }
    }
}

/// <summary>Upgrades settings written by older versions. Add one step per schema bump.</summary>
internal static class SettingsMigrations
{
    public static void Apply(AppSettings settings)
    {
        // v2: engines "pandoc-hwpx" (HWPX without Hancom Office, placed before "hancom" so HWPX saving needs
        //     no approval dialog) and "pandoc" (Markdown) were added.
        if (settings.SchemaVersion < 2)
        {
            InsertBefore(settings.EnginePriority, "pandoc-hwpx", "hancom");
            InsertBefore(settings.EnginePriority, "pandoc", "libreoffice");
        }

        // v3: the Hancom Office engine was removed (rhwp converts HWP <-> HWPX without the approval dialog of 한글)
        //     and "pandoc-hwpx" became "hwpx-writer" (DOCX is read without Pandoc).
        if (settings.SchemaVersion < 3)
        {
            settings.EnginePriority.Remove("hancom");
            var index = settings.EnginePriority.IndexOf("pandoc-hwpx");
            if (index >= 0)
                settings.EnginePriority[index] = "hwpx-writer";
        }

        // v4: built-in "markdown" engine (Markdig, no Pandoc needed); the "text" toolbar profile is added by
        //     ApplyToLibrary.
        if (settings.SchemaVersion < 4)
            InsertBefore(settings.EnginePriority, "markdown", "hwpx-writer");

        // v5: Microsoft Word automation was removed (Filee no longer uses software installed on the system) and the
        //     built-in "spreadsheet" engine (XLSX <-> CSV) was added; profiles are split by ApplyToLibrary.
        if (settings.SchemaVersion < 5)
        {
            settings.EnginePriority.Remove("word");
            InsertBefore(settings.EnginePriority, "spreadsheet", "hwpx-writer");
        }

        // v6: engines for the rest of the format catalog. Built-in ones go before Pandoc, optional downloads
        //     before LibreOffice (the fallback that stays last).
        if (settings.SchemaVersion < 6)
        {
            InsertBefore(settings.EnginePriority, "ooxml", "hwpx-writer");
            foreach (var id in new[] { "docx-writer", "pdf-text", "email", "vector", "icns", "font", "cad", "archive", "ebook", "ffmpeg" })
                InsertBefore(settings.EnginePriority, id, "pandoc");
            foreach (var id in new[] { "ghostscript", "calibre" })
                InsertBefore(settings.EnginePriority, id, "libreoffice");
        }

        // v12: built-in "subtitle" engine (SRT, ASS, WebVTT, SAMI, LRC, TTML, ...), before FFmpeg.
        if (settings.SchemaVersion < 12)
            InsertBefore(settings.EnginePriority, "subtitle", "ffmpeg");

        // v14: built-in "data" engine (JSON, YAML, TOML, XML, CSV, vCard), next to the e-mail engine.
        if (settings.SchemaVersion < 14 && !settings.EnginePriority.Contains("data"))
        {
            var email = settings.EnginePriority.IndexOf("email");
            if (email >= 0)
                settings.EnginePriority.Insert(email + 1, "data");
            else
                InsertBefore(settings.EnginePriority, "data", "pandoc");
        }

        // v15: built-in "model3d" engine (3D models: GLB, glTF, STL, OBJ, 3MF, PLY), after the CAD engine (else before
        //      the archive engine, else before Pandoc like the other built-in engines).
        if (settings.SchemaVersion < 15 && !settings.EnginePriority.Contains("model3d"))
        {
            var cad = settings.EnginePriority.IndexOf("cad");
            if (cad >= 0)
                settings.EnginePriority.Insert(cad + 1, "model3d");
            else
                InsertBefore(settings.EnginePriority, "model3d", settings.EnginePriority.Contains("archive") ? "archive" : "pandoc");
        }

        if (settings.Triggers.Count == 0)
            settings.Triggers = TriggerGesture.Defaults();

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    /// <summary>Upgrades presets and profiles written with settings schema <paramref name="from"/>.</summary>
    /// <returns>True when something was added.</returns>
    public static bool ApplyToLibrary(int from, List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        // v4: Markdown / TXT / HTML files get their own donut instead of the "mixed files" fallback. Skipped when
        //     the user already routes Markdown through a profile of their own.
        if (from < 4 && !profiles.Any(p => p.Id == "text" || (!p.IsFallback && p.Extensions.Contains("md", StringComparer.OrdinalIgnoreCase))))
        {
            var text = BuiltInData.TextProfile();
            var ids = presets.Select(p => p.Id).ToHashSet();
            text.PresetIds = text.PresetIds.Where(ids.Contains).ToList();
            var fallback = profiles.FindIndex(p => p.IsFallback);
            profiles.Insert(fallback < 0 ? profiles.Count : fallback, text);
            changed = true;
        }

        // v5: spreadsheets and presentations get their own donuts (with XLSX / CSV targets) instead of sharing the
        //     documents donut, and the PDF donut drops targets only Word could produce (PDF → DOCX / HWPX / TXT).
        if (from < 5)
        {
            foreach (var preset in BuiltInData.SpreadsheetPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
            {
                presets.Add(preset);
                changed = true;
            }

            var office = profiles.FirstOrDefault(p => p.Id == "office");
            var insertAt = office is null ? profiles.FindIndex(p => p.IsFallback) : profiles.IndexOf(office) + 1;
            var ids = presets.Select(p => p.Id).ToHashSet();
            foreach (var split in new[] { BuiltInData.SpreadsheetProfile(), BuiltInData.PresentationProfile() })
            {
                if (profiles.Any(p => p.Id == split.Id))
                    continue;
                split.PresetIds = split.PresetIds.Where(ids.Contains).ToList();
                // The new donut takes over these extensions from the built-in documents donut, but not from
                // profiles the user made.
                changed |= office?.Extensions.RemoveAll(split.Extensions.Contains) > 0;
                if (profiles.Any(p => !p.IsFallback && p.Extensions.Intersect(split.Extensions, StringComparer.OrdinalIgnoreCase).Any()))
                    continue;
                insertAt = insertAt < 0 ? profiles.Count : insertAt;
                profiles.Insert(insertAt++, split);
                changed = true;
            }

            if (profiles.FirstOrDefault(p => p.Id == "pdf") is { } pdf)
                changed |= pdf.PresetIds.RemoveAll(id => id is "to-docx" or "to-hwpx" or "to-txt") > 0;
        }

        // v6: the rest of the format catalog (video, audio, e-books, archives, vector, CAD, fonts, more image and
        //     office formats). Built-in donuts learn their new extensions unless another donut already has them, the
        //     new donuts follow "text", PDF → DOCX is back on the PDF donut (built in now) and the mixed-files donut
        //     gets "compress into one ZIP".
        if (from < 6)
            changed |= AddCatalog(presets, profiles);

        // v7: conversions that already worked through another format but were on no donut. HWP as a target (through
        //     HWPX), PDF → Markdown / EPUB (through DOCX), Word → PNG, HWP → JPG / EPUB (through PDF / DOCX).
        if (from < 7)
        {
            foreach (var preset in BuiltInData.HwpPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
            {
                presets.Add(preset);
                changed = true;
            }
            foreach (var (profile, preset, after) in Version7Offers)
                changed |= Offer(presets, profiles, profile, preset, after);
        }

        // v8: Settings → General has a default save location. "Same folder as the source" was the default until now,
        //     so presets still on it follow the new setting, which starts as the same folder: nothing moves until the
        //     user picks another default.
        if (from < 8)
        {
            foreach (var preset in presets.Where(p => p.Output.Location == OutputLocation.SameFolder))
            {
                preset.Output.Location = OutputLocation.Default;
                changed = true;
            }
        }

        // v9: PDF → one tall image (all pages stacked), on the PDF donut after JPG (issue #50), and a HEIC preset
        //     (written with FFmpeg), on no donut like AVIF.
        if (from < 9)
        {
            foreach (var id in new[] { "pdf-long-image", "to-heic" })
            {
                if (presets.Any(p => p.Id == id))
                    continue;
                presets.Add(BuiltInData.CreatePresets().Single(p => p.Id == id));
                changed = true;
            }
            changed |= Offer(presets, profiles, "pdf", "pdf-long-image", "to-jpg");
        }

        // v10: "Remove background" (signatures, stamps, logos → transparent PNG), on no donut like HEIC: users add it
        //      in Settings → Toolbar.
        if (from < 10 && presets.All(p => p.Id != "remove-background"))
        {
            presets.Add(BuiltInData.CreatePresets().Single(p => p.Id == "remove-background"));
            changed = true;
        }

        // v11: more document formats (Org, AsciiDoc, Typst, ... through Pandoc; flat OpenDocument, templates,
        //      ClarisWorks, ... through LibreOffice; KEPUB built in). Built-in donuts learn the new extensions unless
        //      another donut already has them, and the e-books donut gets KEPUB after MOBI.
        if (from < 11)
        {
            if (presets.All(p => p.Id != "to-kepub"))
            {
                presets.Add(BuiltInData.CreatePresets().Single(p => p.Id == "to-kepub"));
                changed = true;
                changed |= Offer(presets, profiles, "ebooks", "to-kepub", "to-mobi");
            }
            changed |= LearnExtensions(profiles, out _);
        }

        // v12: HEVC / AV1 video, ALAC and the new audio formats (WavPack, TTA, APE, DSD, ...), and a subtitles donut
        //      after the audio donut. The video donut offers "MP4 HEVC" and "Extract subtitles".
        if (from < 12)
            changed |= AddCodecsAndSubtitles(presets, profiles);

        // v13: more image formats (APNG, MNG, QOI, DDS, DICOM, OpenEXR, …, Fujifilm's .hif): the images donut learns
        //      their extensions unless another donut already has them.
        if (from < 13)
            changed |= LearnExtensions(profiles, out _);

        // v14: structured data (JSON, YAML, TOML, XML, vCard) gets its own donut after "fonts", Outlook MSG joins
        //      the documents donut next to EML, and the spreadsheets donut gets JSON after CSV.
        if (from < 14)
            changed |= AddData(presets, profiles);

        // v15: 3D models (GLB, glTF, STL, OBJ, 3MF, PLY) get presets and their own donut after "data" (else after
        //      "fonts", else before the mixed-files one), with the extensions no donut of the user takes yet.
        if (from < 15)
            changed |= AddModels(presets, profiles);

        // v16: raster → SVG tracing ("Trace to SVG" in colour, "Line art to SVG" in black), on the images donut after
        //      PDF.
        if (from < 16)
        {
            foreach (var preset in BuiltInData.TracePresets().Where(p => presets.All(existing => existing.Id != p.Id)))
            {
                presets.Add(preset);
                changed = true;
            }
            changed |= Offer(presets, profiles, "images", "trace-svg", "to-pdf");
            changed |= Offer(presets, profiles, "images", "trace-line-art", "trace-svg");
        }

        // v17: rotate / flip presets and the "Rotate" / "Flip" groups that fan them out, on no donut, after "Remove
        // background" as in a new library. Present already but saved since by a Filee before 17 (it reads and writes
        // an older schema, so back and forth between versions comes here again): that one dropped what it didn't
        // know, the rotation and the group, and left a preset that re-encodes unturned. Those parts are put back, the
        // user's own changes (name, quality, file names) stay.
        if (from < 17)
        {
            var at = presets.FindIndex(p => p.Id == "remove-background");
            at = at < 0 ? presets.Count : at + 1;
            foreach (var tool in BuiltInData.ImageToolPresets())
            {
                if (presets.FirstOrDefault(p => p.Id == tool.Id) is not { } existing)
                {
                    presets.Insert(Math.Min(at++, presets.Count), tool);
                    changed = true;
                }
                else if (tool.IsGroup && !existing.IsGroup)
                {
                    existing.Kind = PresetKind.Group;
                    existing.SubPresetIds = [.. tool.SubPresetIds];
                    existing.TargetFormat = tool.TargetFormat;
                    changed = true;
                }
                else if (!tool.IsGroup && !existing.Image.TurnsOrMirrors)
                {
                    existing.Image.Rotation = tool.Image.Rotation;
                    existing.Image.Flip = tool.Image.Flip;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool AddData(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        foreach (var preset in BuiltInData.DataPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
        {
            presets.Add(preset);
            changed = true;
        }
        changed |= LearnExtensions(profiles, out var claimed);

        if (profiles.All(p => p.Id != "data"))
        {
            var data = BuiltInData.DataProfile();
            // Only the extensions no donut of the user takes yet.
            data.Extensions = data.Extensions.Where(claimed.Add).ToList();
            if (data.Extensions.Count > 0)
            {
                var presetIds = presets.Select(p => p.Id).ToHashSet();
                data.PresetIds = data.PresetIds.Where(presetIds.Contains).ToList();
                var fonts = profiles.FindIndex(p => p.Id == "fonts");
                var insertAt = fonts >= 0 ? fonts + 1 : profiles.FindIndex(p => p.IsFallback);
                profiles.Insert(insertAt < 0 ? profiles.Count : insertAt, data);
                changed = true;
            }
        }

        changed |= Offer(presets, profiles, "spreadsheets", "to-json", "to-csv");
        return changed;
    }

    private static bool AddCodecsAndSubtitles(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        foreach (var preset in BuiltInData.CodecAndSubtitlePresets().Where(p => presets.All(existing => existing.Id != p.Id)))
        {
            presets.Add(preset);
            changed = true;
        }
        changed |= LearnExtensions(profiles, out var claimed);

        if (profiles.All(p => p.Id != "subtitles"))
        {
            var subtitles = BuiltInData.SubtitleProfile();
            subtitles.Extensions = subtitles.Extensions.Where(claimed.Add).ToList();
            if (subtitles.Extensions.Count > 0)
            {
                var ids = presets.Select(p => p.Id).ToHashSet();
                subtitles.PresetIds = subtitles.PresetIds.Where(ids.Contains).ToList();
                var audio = profiles.FindIndex(p => p.Id == "audio");
                var insertAt = audio >= 0 ? audio + 1 : profiles.FindIndex(p => p.IsFallback);
                profiles.Insert(insertAt < 0 ? profiles.Count : insertAt, subtitles);
                changed = true;
            }
        }

        changed |= Offer(presets, profiles, "video", "mp4-hevc", "mp4-720");
        changed |= Offer(presets, profiles, "video", "extract-subtitles", "to-mp3");
        return changed;
    }

    /// <summary>
    /// Adds the default extensions of the built-in donuts that no donut of the user takes yet.
    /// </summary>
    /// <param name="claimed">Every extension a donut takes afterwards.</param>
    private static bool LearnExtensions(List<ToolbarProfile> profiles, out HashSet<string> claimed)
    {
        var changed = false;
        // Extensions checked on a mixed-selection profile only limit that profile; they don't claim files.
        claimed = profiles.Where(p => !p.IsFallback).SelectMany(p => p.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaults = BuiltInData.CreateProfiles().ToDictionary(p => p.Id);
        foreach (var profile in profiles.Where(p => !p.IsFallback && defaults.ContainsKey(p.Id)))
        {
            foreach (var extension in defaults[profile.Id].Extensions.Where(claimed.Add))
            {
                profile.Extensions.Add(extension);
                changed = true;
            }
        }
        return changed;
    }

    private static bool AddModels(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        foreach (var preset in BuiltInData.ModelPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
        {
            presets.Add(preset);
            changed = true;
        }
        changed |= LearnExtensions(profiles, out var claimed);
        if (profiles.Any(p => p.Id == "models"))
            return changed;

        var models = BuiltInData.ModelProfile();
        // Only the extensions no donut of the user takes yet.
        models.Extensions = models.Extensions.Where(claimed.Add).ToList();
        if (models.Extensions.Count == 0)
            return changed;
        var ids = presets.Select(p => p.Id).ToHashSet();
        models.PresetIds = models.PresetIds.Where(ids.Contains).ToList();
        var after = profiles.FindIndex(p => p.Id == "data");
        if (after < 0)
            after = profiles.FindIndex(p => p.Id == "fonts");
        var insertAt = after >= 0 ? after + 1 : profiles.FindIndex(p => p.IsFallback);
        profiles.Insert(insertAt < 0 ? profiles.Count : insertAt, models);
        return true;
    }

    private static bool AddCatalog(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        foreach (var preset in BuiltInData.CatalogPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
        {
            presets.Add(preset);
            changed = true;
        }
        var presetIds = presets.Select(p => p.Id).ToHashSet();
        changed |= LearnExtensions(profiles, out var claimed);

        var text = profiles.FindIndex(p => p.Id == "text");
        var insertAt = text >= 0 ? text + 1 : profiles.FindIndex(p => p.IsFallback);
        insertAt = insertAt < 0 ? profiles.Count : insertAt;
        foreach (var profile in BuiltInData.CatalogProfiles().Where(p => profiles.All(existing => existing.Id != p.Id)))
        {
            // Only the extensions no donut of the user takes yet.
            profile.Extensions = profile.Extensions.Where(claimed.Add).ToList();
            if (profile.Extensions.Count == 0)
                continue;
            profile.PresetIds = profile.PresetIds.Where(presetIds.Contains).ToList();
            profiles.Insert(insertAt++, profile);
            changed = true;
        }

        changed |= Offer(presets, profiles, "pdf", "to-docx", "to-jpg");
        changed |= Offer(presets, profiles, profiles.FirstOrDefault(p => p.IsFallback)?.Id ?? "mixed", "zip-all", "merge-pdf");
        return changed;
    }

    /// <summary>Presets that settings schema 7 puts on the built-in donuts: (donut, preset, after which preset).</summary>
    private static readonly (string Profile, string Preset, string After)[] Version7Offers =
    [
        ("pdf", "to-md", "to-txt"), ("pdf", "to-epub", "to-md"), ("pdf", "to-hwp", "to-hwpx"),
        ("office", "to-hwp", "to-hwpx"), ("office", "to-png", "to-epub"),
        ("hwp", "to-hwp", "to-hwpx"), ("hwp", "to-jpg", "to-png"), ("hwp", "to-epub", "to-md"),
        ("spreadsheets", "to-hwp", "to-hwpx"), ("presentations", "to-hwp", "to-hwpx"),
        ("text", "to-hwp", "to-hwpx"), ("ebooks", "to-hwp", "to-hwpx"),
    ];

    /// <summary>
    /// Puts a preset on a donut after <paramref name="after"/> (or at the end), unless the donut is missing, full or
    /// has it already. Returns true when it was added.
    /// </summary>
    private static bool Offer(List<Preset> presets, List<ToolbarProfile> profiles, string profileId, string presetId, string? after)
    {
        if (profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile
            || profile.PresetIds.Contains(presetId) || profile.PresetIds.Count >= ToolbarProfile.MaxSlices
            || presets.All(p => p.Id != presetId))
            return false;
        var index = after is null ? -1 : profile.PresetIds.IndexOf(after);
        profile.PresetIds.Insert(index < 0 ? profile.PresetIds.Count : index + 1, presetId);
        return true;
    }

    private static void InsertBefore(List<string> list, string id, string before)
    {
        if (list.Contains(id))
            return;
        var index = list.IndexOf(before);
        if (index < 0)
            list.Add(id);
        else
            list.Insert(index, id);
    }
}
