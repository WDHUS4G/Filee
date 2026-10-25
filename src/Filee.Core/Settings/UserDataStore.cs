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
    private readonly ILogger _log;
    private readonly object _saveLock = new();

    public UserDataStore(string directory, ILogger<UserDataStore>? log = null)
    {
        Directory = directory;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Environment variable that moves all of Filee's per-user data (settings, logs, downloaded engines) to another
    /// folder and gives that copy its own single instance: a development or test copy then never touches, or talks
    /// to, the Filee the user runs. A copy started with it also leaves the system integration alone.
    /// </summary>
    public const string DirectoryVariable = "FILEE_DATA_DIR";

    /// <summary>The folder <see cref="DirectoryVariable"/> points to (full path), or null when it is not set.</summary>
    public static string? OverrideDirectory => DirectoryFrom(Environment.GetEnvironmentVariable(DirectoryVariable));

    /// <summary>
    /// Default data directory: %APPDATA%\Filee on Windows, ~/Library/Application Support/Filee on macOS, or the
    /// folder in <see cref="DirectoryVariable"/>.
    /// </summary>
    public static string DefaultDirectory =>
        OverrideDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");

    internal static string? DirectoryFrom(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));

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

    /// <summary>
    /// Remembers the window zoom (<see cref="AppSettings.UiScale"/>) without raising <see cref="SettingsChanged"/>:
    /// the zoom is already applied, and nothing else changed that would have to be applied again.
    /// </summary>
    public void SaveUiScale(double scale)
    {
        Settings.UiScale = scale;
        Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
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
        // A lowered limit drops the oldest entries here, with the next conversion.
        var limit = Settings.EffectiveHistoryLimit;
        lock (_saveLock)
        {
            History.Insert(0, entry);
            if (History.Count > limit)
                History.RemoveRange(limit, History.Count - limit);
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

    /// <summary>
    /// Whether <see cref="ResetProfile"/> can put <paramref name="profile"/> back: only a built-in donut has a default
    /// state, and not when the reset would leave no profile for mixed selections (a built-in normal donut the user made
    /// the only mixed one).
    /// </summary>
    public bool CanResetProfile(ToolbarProfile profile) =>
        Profiles.Contains(profile)
        && BuiltInData.CreateProfiles().FirstOrDefault(p => p.Id == profile.Id) is { } builtIn
        && (builtIn.IsFallback || !profile.IsFallback || Profiles.Count(p => p.IsFallback) > 1);

    /// <summary>
    /// Puts one built-in donut back as a new library has it (name, slices of both faces and their order, extensions,
    /// normal or mixed) and leaves the other donuts alone. So no file type opens two donuts, an extension another normal donut takes
    /// now stays there. Presets are shared by every donut: the ones this donut drops stay in the library, edited ones
    /// keep their settings, and a built-in preset of its default slices that the user deleted comes back. A donut that
    /// changes kind moves to the border between the normal profiles and the mixed ones, the built-in mixed donut back
    /// to the head of the mixed ones (the catch-all).
    /// </summary>
    /// <returns>False when the profile can't be reset (<see cref="CanResetProfile"/>).</returns>
    public bool ResetProfile(ToolbarProfile profile)
    {
        if (!CanResetProfile(profile))
            return false;
        var builtIn = BuiltInData.CreateProfiles().Single(p => p.Id == profile.Id);
        RestoreBuiltInPresets([.. builtIn.PresetIds, .. builtIn.BackPresetIds ?? []]);

        // Extensions checked on a mixed-selection profile only limit that profile; they don't claim files.
        var claimed = Profiles.Where(p => p != profile && !p.IsFallback)
            .SelectMany(p => p.Extensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        profile.Name = "";
        profile.NameKey = builtIn.NameKey;
        profile.Extensions = builtIn.IsFallback ? builtIn.Extensions : builtIn.Extensions.Where(e => !claimed.Contains(e)).ToList();
        profile.PresetIds = builtIn.PresetIds;
        profile.BackPresetIds = builtIn.BackPresetIds ?? [];
        if (profile.IsFallback != builtIn.IsFallback)
        {
            profile.IsFallback = builtIn.IsFallback;
            Profiles.Remove(profile);
            var index = Profiles.FindIndex(p => p.IsFallback);
            Profiles.Insert(index < 0 ? Profiles.Count : index, profile);
        }
        SaveLibrary();
        return true;
    }

    /// <summary>Adds back the built-in presets among <paramref name="ids"/> that the library no longer has, and those a
    /// built-in group among them fans out, each after the built-in preset before it (as a new library orders them).</summary>
    private void RestoreBuiltInPresets(IEnumerable<string> ids)
    {
        var defaults = BuiltInData.CreatePresets();
        var members = ids.SelectMany(id => defaults.FirstOrDefault(p => p.Id == id) is { IsGroup: true } group ? group.SubPresetIds : []);
        foreach (var id in ids.Concat(members).Distinct().Where(id => FindPreset(id) is null).ToList())
        {
            var at = defaults.FindIndex(p => p.Id == id);
            if (at < 0)
                continue;
            var after = defaults.Take(at).Select(p => Presets.FindIndex(existing => existing.Id == p.Id)).LastOrDefault(i => i >= 0, -1);
            Presets.Insert(after + 1, defaults[at]);
        }
    }

    public Preset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>The preset <paramref name="id"/> when it converts (a group or tool doesn't), for every place that runs one.</summary>
    public Preset? FindConversion(string id) => Presets.FirstOrDefault(p => p.Id == id && !p.IsGroup && !p.IsTool);

    /// <summary>Exports presets and profiles to a shareable JSON file, without PDF passwords.</summary>
    public void Export(string path) =>
        Write(path, new PresetBundle { Presets = [.. Presets.Select(p => p.WithoutPassword(mark: false))], Profiles = Profiles },
            FileeJsonContext.Default.PresetBundle);

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
        // A bundle from a Filee before back faces has none: a built-in donut gets its default one, as on loading.
        SettingsMigrations.AddBackFaces(Presets, bundle.Profiles);
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
            // No back face known by now (a donut the user made, read from an older file): it has none.
            profile.BackPresetIds = (profile.BackPresetIds ?? []).Where(ids.Contains).Distinct().Take(ToolbarProfile.MaxSlices).ToList();
        }
        // A Filee from before groups drops "kind" (and the presets) when it saves a group, which comes back as a
        // conversion to "group": still a group, now an empty one, rather than a conversion that can never run.
        foreach (var preset in Presets.Where(p => !p.IsGroup && p.TargetFormat == Preset.GroupTarget))
            preset.Kind = PresetKind.Group;
        // A group fans out conversions and tools (but never another group), each once, at most MaxSubPresets, and has no
        // target format an older Filee could run it with.
        var actions = Presets.Where(p => !p.IsGroup).Select(p => p.Id).ToHashSet();
        foreach (var group in Presets.Where(p => p.IsGroup))
        {
            group.SubPresetIds = group.SubPresetIds.Where(actions.Contains).Distinct().Take(Preset.MaxSubPresets).ToList();
            group.TargetFormat = Preset.GroupTarget;
        }

        // Several mixed profiles are fine (each limited to the extensions checked on it); none is not.
        if (!Profiles.Any(p => p.IsFallback))
        {
            var mixed = BuiltInData.CreateProfiles().First(p => p.IsFallback);
            mixed.PresetIds = mixed.PresetIds.Where(ids.Contains).ToList();
            mixed.BackPresetIds = (mixed.BackPresetIds ?? []).Where(ids.Contains).ToList();
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

        // v25: built-in "geo" engine (GeoJSON, KML, KMZ, GPX, Shapefile and CSV points), after the 3D models engine (else
        //      before the archive engine, else before Pandoc).
        if (settings.SchemaVersion < 25 && !settings.EnginePriority.Contains("geo"))
        {
            var models = settings.EnginePriority.IndexOf("model3d");
            if (models >= 0)
                settings.EnginePriority.Insert(models + 1, "geo");
            else
                InsertBefore(settings.EnginePriority, "geo", settings.EnginePriority.Contains("archive") ? "archive" : "pandoc");
        }

        // v18: built-in "pdf-slides" engine (PDF → PPTX, a picture of each page per slide), after "pdf-text".
        if (settings.SchemaVersion < 18 && !settings.EnginePriority.Contains("pdf-slides"))
        {
            var text = settings.EnginePriority.IndexOf("pdf-text");
            if (text >= 0)
                settings.EnginePriority.Insert(text + 1, "pdf-slides");
            else
                InsertBefore(settings.EnginePriority, "pdf-slides", "pandoc");
        }

        // v21: built-in "wic" engine (JPEG XR through the Windows Imaging Component), after the other built-in engines:
        //      no other engine reads JPEG XR, and higher up it would push the HWPX writer out of the ranks that win
        //      ties. (Schema 21 may be renumbered if another change takes it first.)
        if (settings.SchemaVersion < 21)
            InsertBefore(settings.EnginePriority, "wic", settings.EnginePriority.Contains("ffmpeg") ? "ffmpeg" : "pandoc");

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

        // v18: PDF → PPTX ("PPTX (slides)", a picture of each page per slide), on the PDF donut after DOCX. The default
        //      PDF donut is full: one the user never changed takes it in place of TIFF, as a new library has it; a donut
        //      the user changed gets it when it has room.
        if (from < 18)
            changed |= AddPdfSlides(presets, profiles);
        // v19: "Extract frames" (every frame of a video as PNG or JPG pictures in a folder), on no donut, after "PPTX
        //      (slides)" or "Split PDF" as in a new library: a long video gives thousands of files, so users add them in
        //      Settings → Toolbar.
        if (from < 19)
        {
            var at = presets.FindIndex(p => p.Id == "pdf-slides");
            if (at < 0)
                at = presets.FindIndex(p => p.Id == "pdf-split");
            at = at < 0 ? presets.Count : at + 1;
            foreach (var preset in BuiltInData.FramePresets())
            {
                if (presets.Any(p => p.Id == preset.Id))
                    continue;
                presets.Insert(Math.Min(at++, presets.Count), preset);
                changed = true;
            }
        }
        // v20: "Compress PDF" (PDF → a smaller PDF), after "Split PDF" as in a new library, on no donut: the PDF donut
        //      is full. Present already but saved since by a Filee before 20 (back and forth between versions): that
        //      one dropped the compression level and left a preset that copies the pages, so the level is put back.
        if (from < 20)
        {
            foreach (var compress in BuiltInData.PdfCompressionPresets())
            {
                if (presets.FirstOrDefault(p => p.Id == compress.Id) is { } existing)
                {
                    if (existing.Pdf.Compression == PdfCompression.None && existing.TargetFormat == compress.TargetFormat)
                    {
                        existing.Pdf.Compression = compress.Pdf.Compression;
                        changed = true;
                    }
                    continue;
                }
                var split = presets.FindIndex(p => p.Id == "pdf-split");
                presets.Insert(split < 0 ? presets.Count : split + 1, compress);
                changed = true;
            }
        }
        // v21: JPEG XR (.jxr, .wdp, .hdp; HDR screenshots of Windows): the images donut learns the extensions unless
        //      another donut already has them.
        if (from < 21)
            changed |= LearnExtensions(profiles, out _, ["jxr", "wdp", "hdp"]);
        // v22: FBX models (read only) open from the 3D donut: it learns ".fbx" unless a donut of the user already
        //      takes it. Other extensions the user removed from a built-in donut stay removed.
        if (from < 22)
            changed |= LearnExtensions(profiles, out _, ["fbx"]);
        // v23: MHTML web archives (.mhtml, .mht; read only) open from the text donut next to HTML, unless a donut of
        //      the user already takes them.
        if (from < 23)
            changed |= LearnExtensions(profiles, out _, ["mhtml", "mht"]);
        // v24: macro-enabled PowerPoint templates (.potm) open from the presentation donut next to POTX, unless a donut
        //      of the user already takes them.
        if (from < 24)
            changed |= LearnExtensions(profiles, out _, ["potm"]);
        // v25: geospatial vector data (Shapefile, GeoJSON, KML, KMZ, GPX) gets presets and its own donut after "models"
        //      (else after "data", else after "fonts"), with the extensions no donut of the user takes yet.
        if (from < 25)
            changed |= AddGeo(presets, profiles);
        // v26: interactive "Crop" tool, after "Remove background" as in a new library, on no donut: users add it in
        //      Settings → Toolbar. Present already but saved since by a Filee before 26: that one read the unknown
        //      kind as a Group, dropped the target format and left an empty group. Kind and target format are put back,
        //      the user's own changes (name, file name pattern) stay.
        if (from < 26)
        {
            if (BuiltInData.CreatePresets().FirstOrDefault(p => p.Id == "tool-crop") is { } crop)
            {
                if (presets.FirstOrDefault(p => p.Id == "tool-crop") is not { } existing)
                {
                    var at = presets.FindIndex(p => p.Id == "remove-background");
                    presets.Insert(at < 0 ? presets.Count : at + 1, crop);
                    changed = true;
                }
                else if (existing.Kind != PresetKind.Tool)
                {
                    existing.Kind = PresetKind.Tool;
                    existing.TargetFormat = crop.TargetFormat;
                    existing.SubPresetIds = [];
                    changed = true;
                }
            }
        }
        // v27: OFD documents (.ofd; read only: e-invoices, official documents) open from the office donut, unless a
        //      donut of the user already takes them.
        if (from < 27)
            changed |= LearnExtensions(profiles, out _, ["ofd"]);
        // v28: "One ZIP" on the archives donut after "Extract", to pack several archives into one. A donut
        //      the user filled up, or a library without the preset, stays as it is.
        if (from < 28)
            changed |= Offer(presets, profiles, "archives", "zip-all", "extract");
        // v29: "Extract live video" (the video of a Live / Motion Photo as MP4), after "Extract frames" as in a new
        //      library, on no donut: the images donut is full. Present already but saved since by a Filee before 29:
        //      that one dropped the option and left a plain MP4 preset, so it is put back. The images donut learns
        //      ".livp" (iPhone Live Photos downloaded from iCloud) unless a donut of the user already takes it.
        if (from < 29)
        {
            foreach (var motion in BuiltInData.MotionPhotoPresets())
            {
                if (presets.FirstOrDefault(p => p.Id == motion.Id) is { } existing)
                {
                    if (!existing.Media.ExtractMotionPhoto && existing.TargetFormat == motion.TargetFormat)
                    {
                        existing.Media.ExtractMotionPhoto = true;
                        changed = true;
                    }
                    continue;
                }
                var at = presets.FindIndex(p => p.Id == "extract-frames-jpg");
                presets.Insert(at < 0 ? presets.Count : at + 1, motion);
                changed = true;
            }
            changed |= LearnExtensions(profiles, out _, ["livp"]);
        }
        // v30: every donut gets a back face, which Tab (or the mouse wheel) flips the open donut to. A built-in donut
        //      gets its default one (with the presets the library still has), a donut the user made none; no front
        //      changes. Also at a later schema when a donut has no back face at all, not even an empty one: a Filee
        //      before 30 saved the library since and dropped what it didn't know, so the built-in back faces come
        //      back. One the user emptied is saved empty and stays so.
        if (from < 30 || profiles.Any(p => p.BackPresetIds is null))
            changed |= AddBackFaces(presets, profiles);
        // v31: "Live Photo (iPhone)" and "Motion Photo (Android)" (a clip of a video as a moving photo), after "Extract
        //      Live Photo video" as in a new library, and on the back of the video donut after "Extract frames" when it
        //      has room and the user hasn't put them elsewhere on it. Present already but saved since by a Filee before
        //      31: that one dropped the option and left a plain JPG preset, so it is put back.
        if (from < 31)
        {
            if (profiles.FirstOrDefault(p => p.Id == "video") is { BackPresetIds: { } back })
            {
                var missing = BuiltInData.LivePhotoPresets().Select(p => p.Id).Where(id => !back.Contains(id)).ToList();
                if (missing.Count > 0 && back.Count + missing.Count <= ToolbarProfile.MaxSlices)
                {
                    var after = back.IndexOf("extract-frames-jpg");
                    back.InsertRange(after < 0 ? back.Count(id => id != "zip-all") : after + 1, missing);
                    changed = true;
                }
            }
            foreach (var live in BuiltInData.LivePhotoPresets())
            {
                if (presets.FirstOrDefault(p => p.Id == live.Id) is { } existing)
                {
                    if (existing.Media.LivePhoto == LivePhotoKind.None && existing.TargetFormat == live.TargetFormat)
                    {
                        existing.Media.LivePhoto = live.Media.LivePhoto;
                        changed = true;
                    }
                    continue;
                }
                var at = presets.FindLastIndex(p => p.Id is "extract-motion" or "live-photo-iphone" or "extract-frames-jpg");
                presets.Insert(at < 0 ? presets.Count : at + 1, live);
                changed = true;
            }
        }
        // v32: logs and script source, Video CD DAT and Workbench archives learn their donuts; custom claims stay put.
        if (from < 32)
            changed |= LearnExtensions(profiles, out _, ["log", "py", "pac", "bat", "command", "dat", "wbpz"]);
        // v33: HAR network logs and raw H.265 / H.264 camera streams learn their donuts (custom claims stay put), and
        //      "Trim transparent borders" comes in after "Remove background".
        if (from < 33)
        {
            changed |= LearnExtensions(profiles, out _, ["har", "h265", "265", "hevc", "h264", "264"]);
            // "Trim transparent borders" after "Remove background", in the library and on the images donut's back face
            // when it has room and the user hasn't put it there.
            if (BuiltInData.TrimPresets().Single() is var trim && presets.All(p => p.Id != trim.Id))
            {
                var at = presets.FindIndex(p => p.Id == "remove-background");
                presets.Insert(at < 0 ? presets.Count : at + 1, trim);
                changed = true;
            }
            if (profiles.FirstOrDefault(p => p.Id == "images") is { BackPresetIds: { } back } images
                && !back.Contains("trim-transparent") && !images.PresetIds.Contains("trim-transparent")
                && back.Count < ToolbarProfile.MaxSlices)
            {
                var after = back.IndexOf("remove-background");
                back.Insert(after < 0 ? back.Count(id => id != "zip-all") : after + 1, "trim-transparent");
                changed = true;
            }
        }
        // v34: Lua scripts and CSS style sheets learn the text donut; custom claims stay put.
        if (from < 34)
            changed |= LearnExtensions(profiles, out _, ["lua", "css"]);
        // v35: HP-GL plot files learn their donut; custom claims stay put.
        if (from < 35)
            changed |= LearnExtensions(profiles, out _, ["plt", "hpgl", "hpg", "hgl"]);
        // v36: iCalendar (.ics, .ical) on the data donut, and "iCalendar" (CSV → ICS) after "vCard" on the back of the
        //      spreadsheets donut when it has room; custom claims stay put.
        if (from < 36)
        {
            changed |= LearnExtensions(profiles, out _, ["ics", "ical"]);
            changed |= AddCalendarPreset(presets, profiles);
        }
        // v37: everyday tools (compress image / video / audio, video from images, extract audio, merge videos / audio,
        //      remove metadata), after "Motion Photo (Android)" as in a new library, and on the back faces of the
        //      built-in donuts before "One ZIP" where they have room and the user hasn't put them there already.
        //      "Extract audio" replaces the plain M4A slice on the video donut's back.
        if (from < 37)
            changed |= AddMediaTools(presets, profiles);
        // v38: Tajima embroidery designs (.dst) learn the vector donut; custom claims stay put.
        if (from < 38)
            changed |= LearnExtensions(profiles, out _, ["dst"]);
        // v39: PowerShell scripts (.ps1) learn the text donut; custom claims stay put.
        if (from < 39)
            changed |= LearnExtensions(profiles, out _, ["ps1"]);
        // v40: "Searchable PDF" (text recognition), after "Compress PDF" as in a new library, on no donut.
        if (from < 40 && !presets.Any(p => p.Id == "searchable-pdf"))
        {
            var at = presets.FindIndex(p => p.Id == "compress-pdf");
            presets.Insert(at < 0 ? presets.Count : at + 1, BuiltInData.OcrPresets().Single());
            changed = true;
        }
        // v41: PNG/JPEG download aliases learn the images donut; custom claims stay put.
        if (from < 41)
            changed |= LearnExtensions(profiles, out _, ["png_large", "jpg_large"]);
        // v42: the print studio for XLSX → PDF (a tool), after "Crop" as in a new library, and first on the back of the
        //      spreadsheets donut when it has room and the user hasn't put it on either face.
        if (from < 42)
        {
            const string id = "tool-sheet-studio";
            if (presets.All(p => p.Id != id))
            {
                var at = presets.FindIndex(p => p.Id == "tool-crop");
                presets.Insert(at < 0 ? presets.Count : at + 1, BuiltInData.CreatePresets().Single(p => p.Id == id));
                changed = true;
            }
            if (profiles.FirstOrDefault(p => p.Id == "spreadsheets") is { BackPresetIds: { } back } spreadsheets
                && !back.Contains(id) && !spreadsheets.PresetIds.Contains(id) && back.Count < ToolbarProfile.MaxSlices)
            {
                back.Insert(0, id);
                changed = true;
            }
        }
        // v43: FITS astronomy pictures (.fits, .fts) learn the images donut; custom claims stay put. (Schema 43 may be
        //      renumbered if another change takes it first.)
        if (from < 43)
            changed |= LearnExtensions(profiles, out _, ["fits", "fts"]);
        // v44: CRI USM game videos (.usm) learn the video donut; custom claims stay put.
        if (from < 44)
            changed |= LearnExtensions(profiles, out _, ["usm"]);
        // v45: "Cut a clip" (a part of a video as a file of its own), after "Motion Photo (Android)" as in a new library,
        //      and first on the back of the video donut in place of FLAC (which stays in the library), when the user
        //      hasn't put it elsewhere on the donut and the face has room.
        //      Present already but saved since by a Filee before 45: that one dropped the tool's name, which made it a
        //      crop, so it is put back. The user's own changes (name, file name pattern) stay.
        if (from < 45)
            changed |= AddClipTool(presets, profiles);
        // v46: one "Edit" slice on the back of the PDF donut fans out the common lossless page tools. Existing
        //      customised backs keep their layout; the group is offered at the start when there is room.
        if (from < 46)
        {
            var ids = new[] { "tool-pdf-crop", "tool-pdf-pages", "tool-pdf-add", "pdf-rotate-left", "pdf-rotate-right", "pdf-edit" };
            var defaults = BuiltInData.CreatePresets();
            foreach (var id in ids.Where(id => presets.All(p => p.Id != id)))
            {
                var item = defaults.Single(p => p.Id == id);
                var at = defaults.FindIndex(p => p.Id == id);
                var after = defaults.Take(at).Select(p => presets.FindIndex(existing => existing.Id == p.Id)).LastOrDefault(i => i >= 0, -1);
                presets.Insert(after + 1, item);
                changed = true;
            }
            if (profiles.FirstOrDefault(p => p.Id == "pdf") is { BackPresetIds: { } back } pdf
                && !pdf.PresetIds.Contains("pdf-edit") && !back.Contains("pdf-edit")
                && back.Count < ToolbarProfile.MaxSlices)
            {
                back.Insert(0, "pdf-edit");
                changed = true;
            }
        }
        return changed;
    }

    private static bool AddCalendarPreset(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        if (presets.All(p => p.Id != "to-ics"))
        {
            var preset = BuiltInData.DataPresets().Single(p => p.Id == "to-ics");
            var after = presets.FindIndex(p => p.Id == "to-vcf");
            presets.Insert(after < 0 ? presets.Count : after + 1, preset);
            changed = true;
        }

        if (profiles.FirstOrDefault(p => p.Id == "spreadsheets") is { BackPresetIds: { } back }
            && !back.Contains("to-ics") && back.Count < ToolbarProfile.MaxSlices)
        {
            var after = back.IndexOf("to-vcf");
            var beforeZip = back.IndexOf("zip-all");
            back.Insert(after >= 0 ? after + 1 : beforeZip >= 0 ? beforeZip : back.Count, "to-ics");
            changed = true;
        }
        return changed;
    }

    /// <summary>Where schema 37 offers its tools: (donut, presets for its back face).</summary>
    private static readonly (string Profile, string[] Presets)[] Version37Offers =
    [
        ("images", ["compress-image", "remove-metadata"]),
        ("pdf", ["remove-metadata"]),
        ("video", ["compress-video", "merge-videos", "extract-audio"]),
        ("audio", ["compress-audio", "merge-audio", "remove-metadata"]),
    ];

    private static bool AddMediaTools(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        var at = presets.FindLastIndex(p => p.Id is "motion-photo-android" or "live-photo-iphone" or "extract-motion");
        at = at < 0 ? presets.Count : at + 1;
        foreach (var tool in BuiltInData.MediaToolPresets())
        {
            if (presets.Any(p => p.Id == tool.Id))
                continue;
            presets.Insert(Math.Min(at++, presets.Count), tool);
            changed = true;
        }
        foreach (var (profileId, ids) in Version37Offers)
        {
            if (profiles.FirstOrDefault(p => p.Id == profileId) is not { BackPresetIds: { } back } profile)
                continue;
            // "Extract audio" writes the same M4A as the plain M4A slice, copying the sound where it can: it takes that
            // slice's place on the video donut's back, or comes in with the other tools when there is room.
            if (profileId == "video" && !back.Contains("extract-audio") && !profile.PresetIds.Contains("extract-audio")
                && presets.Any(p => p.Id == "extract-audio"))
            {
                var m4a = back.IndexOf("to-m4a");
                if (m4a >= 0)
                {
                    back[m4a] = "extract-audio";
                    changed = true;
                }
            }
            foreach (var id in ids)
            {
                if (back.Contains(id) || profile.PresetIds.Contains(id) || back.Count >= ToolbarProfile.MaxSlices
                    || presets.All(p => p.Id != id))
                    continue;
                InsertBefore(back, id, "zip-all");
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Gives each built-in donut without a back face (<see cref="ToolbarProfile.BackPresetIds"/> null) its default one,
    /// with only the presets in <paramref name="presets"/>. Donuts the user made are left for
    /// <see cref="UserDataStore"/> to give an empty one. Returns true when one was added.
    /// </summary>
    internal static bool AddBackFaces(List<Preset> presets, IEnumerable<ToolbarProfile> profiles)
    {
        var defaults = BuiltInData.CreateProfiles().ToDictionary(p => p.Id);
        var ids = presets.Select(p => p.Id).ToHashSet();
        var changed = false;
        foreach (var profile in profiles.Where(p => p.BackPresetIds is null && defaults.ContainsKey(p.Id)))
        {
            profile.BackPresetIds = (defaults[profile.Id].BackPresetIds ?? []).Where(ids.Contains).ToList();
            changed = true;
        }
        return changed;
    }

    private static bool AddClipTool(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        var clip = BuiltInData.ClipPresets().Single();
        if (presets.FirstOrDefault(p => p.Id == clip.Id) is { } existing)
        {
            if (!existing.IsClipTool)
            {
                existing.Kind = PresetKind.Tool;
                existing.Tool = Preset.ClipTool;
                existing.TargetFormat = clip.TargetFormat;
                existing.SubPresetIds = [];
                changed = true;
            }
        }
        else
        {
            var at = presets.FindLastIndex(p => p.Id is "motion-photo-android" or "live-photo-iphone" or "extract-motion");
            presets.Insert(at < 0 ? presets.Count : at + 1, clip);
            changed = true;
        }
        if (profiles.FirstOrDefault(p => p.Id == "video") is { BackPresetIds: { } back } video
            && !back.Contains(clip.Id) && !video.PresetIds.Contains(clip.Id))
        {
            changed |= back.Remove("to-flac");
            if (back.Count < ToolbarProfile.MaxSlices)
            {
                back.Insert(0, clip.Id);
                changed = true;
            }
        }
        return changed;
    }

    private static bool AddGeo(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        foreach (var preset in BuiltInData.GeoPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
        {
            presets.Add(preset);
            changed = true;
        }
        // Only the geospatial extensions: others a user removed from a built-in donut stay removed.
        changed |= LearnExtensions(profiles, out var claimed, BuiltInData.GeoProfile().Extensions);
        if (profiles.Any(p => p.Id == "geo"))
            return changed;

        var geo = BuiltInData.GeoProfile();
        // Only the extensions no donut of the user takes yet.
        geo.Extensions = geo.Extensions.Where(claimed.Add).ToList();
        if (geo.Extensions.Count == 0)
            return changed;
        var ids = presets.Select(p => p.Id).ToHashSet();
        geo.PresetIds = geo.PresetIds.Where(ids.Contains).ToList();
        var after = profiles.FindIndex(p => p.Id == "models");
        if (after < 0)
            after = profiles.FindIndex(p => p.Id == "data");
        if (after < 0)
            after = profiles.FindIndex(p => p.Id == "fonts");
        var insertAt = after >= 0 ? after + 1 : profiles.FindIndex(p => p.IsFallback);
        profiles.Insert(insertAt < 0 ? profiles.Count : insertAt, geo);
        return true;
    }

    private static bool AddPdfSlides(List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        if (presets.All(p => p.Id != "pdf-slides"))
        {
            var at = presets.FindIndex(p => p.Id == "pdf-split");
            presets.Insert(at < 0 ? presets.Count : at + 1, BuiltInData.PdfSlidesPresets().Single());
            changed = true;
        }
        if (profiles.FirstOrDefault(p => p.Id == "pdf") is { } pdf && pdf.PresetIds.SequenceEqual(BuiltInData.PdfDonutBefore18))
        {
            pdf.PresetIds = [.. BuiltInData.PdfDonut];
            return true;
        }
        return Offer(presets, profiles, "pdf", "pdf-slides", "to-docx") || changed;
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
    /// <param name="only">Learn only these (the ones a version added), not others the user removed from a donut since.</param>
    private static bool LearnExtensions(List<ToolbarProfile> profiles, out HashSet<string> claimed, IReadOnlyCollection<string>? only = null)
    {
        var changed = false;
        // Extensions checked on a mixed-selection profile only limit that profile; they don't claim files.
        claimed = profiles.Where(p => !p.IsFallback).SelectMany(p => p.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaults = BuiltInData.CreateProfiles().ToDictionary(p => p.Id);
        foreach (var profile in profiles.Where(p => !p.IsFallback && defaults.ContainsKey(p.Id)))
        {
            foreach (var extension in defaults[profile.Id].Extensions.Where(e => only is null || only.Contains(e)).Where(claimed.Add))
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
