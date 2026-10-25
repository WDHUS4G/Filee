// Shared context for engines: where Filee's own engine copies live and where engines keep their state.
// Built-in engines never use software installed on the system (Microsoft Office, Hancom Office). The optional
// engines (FFmpeg, calibre, Pandoc, Ghostscript, LibreOffice, Tesseract) are looked up in this order: a copy the user chose in
// Settings → Engines (OwnCopies), Filee's own copy (installer or download, the tested version), then one already on
// the PC (on the PATH or where its installer puts it), so nobody has to download a second FFmpeg they already have.

using System.Collections.Concurrent;
using Filee.Core.Settings;

namespace Filee.Engines.Infrastructure;

/// <summary>Environment information passed to every engine.</summary>
/// <param name="dataDirectory">Filee's writable data directory (profiles, caches).</param>
public sealed class EngineEnvironment(string dataDirectory)
{
    /// <summary>Writable directory for engine state (e.g. LibreOffice profiles).</summary>
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>
    /// Per-user folder for engines downloaded on demand (see <see cref="EngineInstaller"/>):
    /// %LOCALAPPDATA%\Filee\engines. It is outside the app folder, so app updates keep it, and inside Filee's
    /// install root, so uninstalling removes it. With FILEE_DATA_DIR set (<see cref="UserDataStore.DirectoryVariable"/>)
    /// it is the engines folder in there.
    /// </summary>
    public static string DownloadRoot { get; set; } = DefaultDownloadRoot(UserDataStore.OverrideDirectory);

    internal static string DefaultDownloadRoot(string? dataDirectoryOverride) =>
        dataDirectoryOverride is not null
            ? Path.Combine(dataDirectoryOverride, "engines")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines");

    /// <summary>
    /// Finds an engine directory (<c>engines/&lt;name&gt;</c>): next to the executable (bundled with the installer),
    /// in <see cref="DownloadRoot"/> (downloaded later), then in the Filee repository checkout the app runs from
    /// (a folder with Filee.slnx), so a developer running from <c>bin/Debug</c> uses the engines fetched there.
    /// </summary>
    public static string? FindBundled(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "engines", name);
        if (Directory.Exists(local))
            return local;
        var downloaded = Path.Combine(DownloadRoot, name);
        if (Directory.Exists(downloaded))
            return downloaded;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                continue;
            var candidate = Path.Combine(dir.FullName, "engines", name);
            return Directory.Exists(candidate) ? candidate : null;
        }
        return null;
    }

    /// <summary>
    /// Programs the user chose for an optional engine package instead of Filee's download: package id → the program
    /// they picked (or its folder). Set by the app and the command line from the settings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OwnCopies { get; set; } = new Dictionary<string, string>();

    /// <summary>LibreOffice converts presentations to PDF (Settings → Engines). Re-check the engines after a change.</summary>
    public static bool LibreOfficeForSlides { get; set; } = true;

    /// <summary>LibreOffice converts HWP / HWPX to PDF instead of rhwp (Settings → Engines). Re-check after a change.</summary>
    public static bool LibreOfficeForHwp { get; set; }

    /// <summary>LibreOffice converts Word documents to PDF (Settings → Engines). Re-check after a change.</summary>
    public static bool LibreOfficeForDocuments { get; set; }

    /// <summary>LibreOffice converts spreadsheets to PDF (Settings → Engines). Re-check after a change.</summary>
    public static bool LibreOfficeForSheets { get; set; }

    /// <summary>Programs required by each engine package on the current operating system.</summary>
    public static IReadOnlyDictionary<string, string[]> OwnCopyPrograms { get; } = Programs(OperatingSystem.IsWindows());

    private static IReadOnlyDictionary<string, string[]> Programs(bool windows) => new Dictionary<string, string[]>
    {
        ["libreoffice"] = [windows ? "soffice.exe" : "soffice"],
        ["pandoc"] = [windows ? "pandoc.exe" : "pandoc"],
        ["ghostscript"] = [windows ? "gswin64c.exe" : "gs"],
        ["ffmpeg"] = windows ? ["ffmpeg.exe", "ffprobe.exe"] : ["ffmpeg", "ffprobe"],
        ["calibre"] = [windows ? "ebook-convert.exe" : "ebook-convert"],
        ["rhwp"] = [windows ? "rhwp.exe" : "rhwp"],
        ["7zip"] = [windows ? "7z.exe" : "7zz"],
        ["tesseract"] = [windows ? "tesseract.exe" : "tesseract"],
    };

    public static string ProgramName(string name) => OperatingSystem.IsWindows() ? name
        : name == "gswin64c.exe" ? "gs" : name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;

    /// <summary>The folder of the user's own copy of package <paramref name="id"/>, when it has every program.</summary>
    public static string? OwnCopyFolder(string id) =>
        OwnCopies.TryGetValue(id, out var path) ? FolderWithPrograms(id, path) : null;

    /// <summary>A program of the user's own copy of package <paramref name="id"/>, or null.</summary>
    public static string? OwnProgram(string id, string program) =>
        OwnCopyFolder(id) is { } folder ? Path.Combine(folder, ProgramName(program)) : null;

    public static string? BundledProgram(string id, string program) =>
        FindBundled(id) is { } root && FolderWithPrograms(id, root) is { } folder
            ? Path.Combine(folder, ProgramName(program)) : null;

    /// <summary>
    /// The folder with all programs of package <paramref name="id"/>, given a program the user picked or a folder:
    /// that folder itself, or its bin\ or program\ sub folder (FFmpeg builds, LibreOffice). Null when they are missing.
    /// </summary>
    public static string? FolderWithPrograms(string id, string path) =>
        FolderWithPrograms(id, path, OperatingSystem.IsWindows());

    internal static string? FolderWithPrograms(string id, string path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path) || !Programs(windows).TryGetValue(id, out var programs))
            return null;
        var start = File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null;
        if (start is null)
            return null;
        var app = id == "libreoffice" ? "LibreOffice.app" : id == "calibre" ? "calibre.app" : null;
        var candidates = new List<string> { start, Path.Combine(start, "bin"), Path.Combine(start, "program"), Path.Combine(start, "Contents", "MacOS") };
        if (app is not null)
            candidates.Add(Path.Combine(start, app, "Contents", "MacOS"));
        foreach (var folder in candidates)
            if (programs.All(p => File.Exists(Path.Combine(folder, p))))
                return folder;
        return null;
    }

    /// <summary>True when package <paramref name="id"/> runs from Filee's own copy (not the user's or the PC's).</summary>
    public static bool UsesFileesCopy(string id) => OwnCopyFolder(id) is null
        && FindBundled(id) is { } folder && FolderWithPrograms(id, folder) is not null;

    /// <summary>Searching the PC for engines; tests turn it off so the machine's tools don't change results.</summary>
    public static bool SearchSystem { get; set; } = true;

    private static readonly ConcurrentDictionary<string, string?> SystemCopies = new();

    /// <summary>
    /// The folder of a copy of package <paramref name="id"/> already on the PC: on the PATH (also the user's and the
    /// machine's PATH as saved, so a tool installed after Filee started is found, e.g. Scoop's shims) or where its
    /// installer puts it (LibreOffice, calibre, Ghostscript under Program Files). Cached until
    /// <see cref="ForgetSystemCopies"/>.
    /// </summary>
    public static string? SystemCopyFolder(string id) =>
        SearchSystem ? SystemCopies.GetOrAdd(id, FindOnSystem) : null;

    /// <summary>A program of the copy of package <paramref name="id"/> on the PC, or null.</summary>
    public static string? SystemProgram(string id, string program) =>
        SystemCopyFolder(id) is { } folder ? Path.Combine(folder, ProgramName(program)) : null;

    /// <summary>Searches the PC again (Settings → Engines → Check again, settings changes).</summary>
    public static void ForgetSystemCopies()
    {
        SystemCopies.Clear();
        ReachableShares.Clear();
    }

    private static string? FindOnSystem(string id) =>
        SystemFolders(id).Where(IsReachable).Select(folder => FolderWithPrograms(id, folder)).FirstOrDefault(found => found is not null);

    /// <summary>How long a network share on the PATH may take to answer before the search leaves it out.</summary>
    private static readonly TimeSpan ShareTimeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, bool> ReachableShares = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// False for a folder on a network share that doesn't answer in time. A PATH entry on a server that is offline
    /// (a mapped work share at home) made every file check wait for the network timeout, many seconds each, and the
    /// search runs for every engine. Each share is asked once, until <see cref="ForgetSystemCopies"/>.
    /// </summary>
    private static bool IsReachable(string folder)
    {
        if (ShareOf(folder) is not { } share)
            return true;
        return ReachableShares.GetOrAdd(share, s =>
        {
            var probe = Task.Run(() => Directory.Exists(s));
            return probe.Wait(ShareTimeout) && probe.Result;
        });
    }

    /// <summary>
    /// The share root of a UNC path (<c>\\server\share</c>), or null for a local path. Device paths (<c>\\?\</c>,
    /// <c>\\.\</c>) are local. Plain string work, the same on every OS.
    /// </summary>
    internal static string? ShareOf(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : parts.Length == 1 ? $@"\\{parts[0]}" : null;
    }

    private static IEnumerable<string> SystemFolders(string id)
    {
        var path = OperatingSystem.IsWindows() ? string.Join(Path.PathSeparator,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        var folders = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => Environment.ExpandEnvironmentVariables(entry.Trim('"')))
            .ToList();
        if (!OperatingSystem.IsWindows())
        {
            folders.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/opt/local/bin"]);
            if (id == "libreoffice")
            {
                folders.AddRange(["/Applications/LibreOffice.app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "LibreOffice.app"), "/usr/lib/libreoffice/program", "/usr/lib64/libreoffice/program"]);
                try
                {
                    if (Directory.Exists("/opt"))
                        folders.AddRange(Directory.EnumerateDirectories("/opt", "libreoffice*")
                            .OrderDescending(StringComparer.Ordinal).Select(p => Path.Combine(p, "program")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (id == "calibre")
                folders.AddRange(["/Applications/calibre.app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "calibre.app"), "/opt/calibre"]);
        }
        foreach (var root in (OperatingSystem.IsWindows()
                     ? new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                     : [])
                     .Select(Environment.GetFolderPath).Where(r => r.Length > 0))
        {
            switch (id)
            {
                case "libreoffice":
                    folders.Add(Path.Combine(root, "LibreOffice", "program"));
                    break;
                case "calibre":
                    folders.Add(Path.Combine(root, "Calibre2"));
                    break;
                case "tesseract":
                    folders.Add(Path.Combine(root, "Tesseract-OCR"));
                    break;
                case "ghostscript":
                    // C:\Program Files\gs\gs10.04.0\bin, newest first
                    try
                    {
                        var gs = Path.Combine(root, "gs");
                        if (Directory.Exists(gs))
                            folders.AddRange(Directory.EnumerateDirectories(gs).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                                .Select(d => Path.Combine(d, "bin")));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                    break;
            }
        }
        // Never Filee's own folders: those are its own copy, found before.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return folders.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(f => !IsWithin(f, DownloadRoot, comparison) && !IsWithin(f, AppContext.BaseDirectory, comparison));
    }

    private static bool IsWithin(string path, string root, StringComparison comparison) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), comparison)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);

    /// <summary>Returns the first path that exists as a file.</summary>
    public static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
}
