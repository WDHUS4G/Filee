// Finds the Filee that is installed already: one installed by this Setup, by Filee 1.x's Inno Setup installer (same
// uninstall key, "Inno Setup: …" values, unins000.exe), or Filee 1.1's per-user Velopack copy. An update keeps the
// install folder and the user's choices, which come from what is registered now (the desktop shortcut, PATH, the
// Explorer menu package) rather than from what was ticked once.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

/// <param name="Folder">The install folder (has Filee.exe).</param>
/// <param name="Version">DisplayVersion of the uninstall entry.</param>
/// <param name="ByInno">Installed by Filee 1.x's Inno Setup installer.</param>
/// <param name="RecordedTasks">The options of the last install ("Inno Setup: Selected Tasks" or ours).</param>
/// <param name="Record">uninstall.json of an install by this Setup.</param>
public sealed record PreviousInstall(
    string Folder,
    string? Version,
    InstallScope Scope,
    bool ByInno,
    SetupTasks? RecordedTasks,
    string? Language,
    InstallRecord? Record)
{
    public static Hive HiveOf(InstallScope scope) => scope == InstallScope.Machine ? Hive.LocalMachine : Hive.CurrentUser;

    /// <summary>The installed Filee for <paramref name="scope"/>, or null (also when its entry points to nothing).</summary>
    public static PreviousInstall? Find(ISetupRegistry registry, InstallScope scope)
    {
        var hive = HiveOf(scope);
        const string key = SetupNames.UninstallKey;
        if (!registry.KeyExists(hive, key))
            return null;
        var folder = registry.Get(hive, key, "InstallLocation")?.Text;
        if (string.IsNullOrWhiteSpace(folder))
            folder = registry.Get(hive, key, SetupNames.InnoAppPathValue)?.Text;
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        folder = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));
        if (!File.Exists(Path.Combine(folder, SetupNames.AppExe)))
            return null;
        var byInno = registry.Get(hive, key, SetupNames.InnoVersionValue) is not null
                     || File.Exists(Path.Combine(folder, SetupNames.InnoUninstallerFiles[0]));
        var tasks = registry.Get(hive, key, SetupNames.TasksValue)?.Text ?? registry.Get(hive, key, SetupNames.InnoTasksValue)?.Text;
        var language = registry.Get(hive, key, SetupNames.LanguageValue)?.Text ?? registry.Get(hive, key, SetupNames.InnoLanguageValue)?.Text;
        // Inno Setup leaves the task list empty after an update that showed no options: unknown, not "none".
        return new PreviousInstall(
            folder,
            registry.Get(hive, key, "DisplayVersion")?.Text,
            scope,
            byInno,
            string.IsNullOrWhiteSpace(tasks) ? null : SetupTasks.FromInnoString(tasks),
            language,
            InstallRecord.Read(folder));
    }

    /// <summary>
    /// The files of this installation that an update replaces or removes: the recorded list, or for an Inno Setup
    /// install (which copied the whole published app recursively) everything in the folder, Inno's uninstaller files
    /// included. Paths are relative to <see cref="Folder"/>.
    /// </summary>
    public IReadOnlyList<string> OwnedFiles()
    {
        if (Record is not null)
            return Record.Files;
        if (!ByInno)
            return [];
        return Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Folder, f))
            .Where(f => !f.StartsWith(FileSwap.WorkFolderPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// The options as they are now: the desktop shortcut and PATH entry exist or not, the Explorer menu package is
    /// registered or not. The Explorer menu entry and start at sign-in are Filee settings, which an update leaves to
    /// Filee: those come from the last install.
    /// </summary>
    public SetupTasks CurrentTasks(ISetupRegistry registry, ISetupSystem system, SetupPaths paths) => new(
        RecordedTasks?.ContextMenu ?? true,
        system.ExplorerMenuSupported && system.IsExplorerMenuRegistered(),
        RecordedTasks?.Startup ?? true,
        PathVariable.IsOnPath(registry, Scope, Path.Combine(Folder, SetupNames.CliFolder)),
        File.Exists(Path.Combine(paths.Desktop(Scope), SetupNames.ShortcutName)));
}

/// <summary>Filee 1.1 and earlier, installed by Velopack into %LocalAppData%\Filee for one user.</summary>
public static class VelopackInstall
{
    public static string Root(SetupPaths paths) => Path.Combine(paths.LocalAppData, "Filee");

    public static bool Exists(SetupPaths paths) =>
        File.Exists(Path.Combine(Root(paths), "Update.exe")) && Directory.Exists(Path.Combine(Root(paths), "current"));

    /// <summary>
    /// Removes it without running its uninstaller, which would also delete the downloaded engines (and the settings
    /// are in %APPDATA%). Its registry entries (context menu, startup) are rewritten by the new Filee.
    /// </summary>
    public static void Remove(SetupPaths paths, ISetupRegistry registry, ISetupSystem system, SetupLog log)
    {
        var root = Root(paths);
        log.Info($"Removing Filee 1.1 (Velopack) from {root}; settings and engines stay");
        // Its Explorer menu entry points into the folder that goes next.
        system.UnregisterExplorerMenu();
        foreach (var file in new[] { "Update.exe", SetupNames.AppExe })
            DeleteFile(Path.Combine(root, file), system, log);
        FileSwap.DeleteTree(Path.Combine(root, "current"), system, log);
        FileSwap.DeleteTree(Path.Combine(root, "packages"), system, log);
        DeleteFile(Path.Combine(paths.UserStartMenu, SetupNames.ShortcutName), system, log);
        DeleteFile(Path.Combine(paths.UserDesktop, SetupNames.ShortcutName), system, log);
        registry.DeleteKey(Hive.CurrentUser, SetupNames.VelopackUninstallKey);
    }

    private static void DeleteFile(string path, ISetupSystem system, SetupLog log)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Info($"In use, deleted at the next restart: {path}");
            system.DeleteAtRestart(path);
        }
    }
}
