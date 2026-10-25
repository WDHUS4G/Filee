// Removes Filee:
//  1. "Filee.exe --uninstall-cleanup": Filee quits and removes what it registered for the user (Explorer menu entries,
//     the Send To shortcut, the startup entry, the Windows 11 package) and the engines it downloaded;
//  2. ends what still runs from the install folder, removes the Explorer menu package if it is still there;
//  3. removes the shortcuts, the PATH entry, the files of uninstall.json, the install folder and the uninstall entry;
//  4. deletes the settings (%APPDATA%\Filee) only when the user asked for it: by default they stay for a reinstall.
// A file Windows keeps in use (FileeExplorerMenu.dll loaded by Explorer, the running uninstall.exe) is moved out of
// the folder into the trash folder and deleted at the next restart, so the folder itself goes now.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public sealed class Uninstaller(SetupContext context)
{
    private ISetupRegistry Registry => context.Registry;
    private ISetupSystem System => context.System;
    private SetupLog Log => context.Log;

    /// <summary>Filee's cleanup also removes the Explorer menu package through PowerShell, which takes a while.</summary>
    public static TimeSpan CleanupTimeout { get; } = TimeSpan.FromSeconds(90);

    /// <param name="trash">A folder outside the install folder (same volume if possible) for files still in use.</param>
    /// <param name="releaseUninstaller">
    /// Called when only uninstall.exe and the folder are left: the uninstaller that started this one from the install
    /// folder is told it worked and ends, so its uninstall.exe can be deleted (a single-file program loads parts of
    /// itself from its file while it runs: that file must not move away under it).
    /// </param>
    /// <returns>Files and folders that are deleted at the next restart.</returns>
    public IReadOnlyList<string> Run(string folder, InstallRecord record, bool deleteSettings, string trash,
        IProgress<SetupProgress>? progress = null, Action? releaseUninstaller = null)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Log.Info($"Uninstalling Filee {record.Version} from {folder} ({record.Scope}); delete settings: {deleteSettings}");
        var later = new List<string>();
        progress?.Report(new SetupProgress(SetupStep.Closing, 0));

        var exe = Path.Combine(folder, SetupNames.AppExe);
        if (File.Exists(exe))
            Log.Info($"Filee --uninstall-cleanup: exit code {System.RunAndWait(exe, "--uninstall-cleanup", CleanupTimeout)}");
        foreach (var program in System.ProgramsIn(folder).Where(p => p.Id != Environment.ProcessId))
        {
            // Not the uninstaller that started this one: it waits for this one and ends by itself.
            if (Path.GetFileName(program.Path).Equals(SetupNames.UninstallerExe, StringComparison.OrdinalIgnoreCase))
                continue;
            Log.Info($"Ending {program.Path} ({program.Id})");
            System.Kill(program.Id);
        }
        progress?.Report(new SetupProgress(SetupStep.Removing, 0.3));
        if (System.ExplorerMenuSupported)
            System.UnregisterExplorerMenu();

        var hive = PreviousInstall.HiveOf(record.Scope);
        foreach (var shortcut in new[] { context.Paths.StartMenu(record.Scope), context.Paths.Desktop(record.Scope) })
            DeleteFile(Path.Combine(shortcut, SetupNames.ShortcutName), trash, later);
        if (PathVariable.Set(Registry, record.Scope, Path.Combine(folder, SetupNames.CliFolder), present: false))
        {
            System.BroadcastEnvironmentChange();
            Log.Info("Removed the filee command from PATH");
        }

        progress?.Report(new SetupProgress(SetupStep.Removing, 0.5));
        var files = record.Files.Concat([SetupNames.InstallRecordFile]).Concat(SetupNames.InnoUninstallerFiles)
            .Where(f => !f.Equals(SetupNames.UninstallerExe, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            DeleteFile(Path.Combine(folder, files[i]), trash, later);
            progress?.Report(new SetupProgress(SetupStep.Removing, 0.5 + 0.4 * (i + 1) / files.Count));
        }
        // Left by an interrupted Setup, and FileeExplorerMenu.dll copies an update moved aside.
        if (Directory.Exists(folder))
        {
            foreach (var work in Directory.EnumerateDirectories(folder, FileSwap.WorkFolderPrefix + "*"))
                FileSwap.DeleteTree(work, System, Log);
        }

        Registry.DeleteKey(hive, SetupNames.UninstallKey);
        progress?.Report(new SetupProgress(SetupStep.Removing, 0.95));
        if (deleteSettings)
        {
            Log.Info($"Deleting the settings in {context.Paths.SettingsFolder}");
            FileSwap.DeleteTree(context.Paths.SettingsFolder, System, Log);
        }

        releaseUninstaller?.Invoke();
        DeleteFile(Path.Combine(folder, SetupNames.UninstallerExe), trash, later);
        FileSwap.RemoveEmptyFolders(folder);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something that isn't Filee's is still in it (a folder the user chose), or a file is in use.
            Log.Warn($"{folder} stays: {ex.Message}");
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                System.DeleteAtRestart(folder);
                later.Add(folder);
            }
        }
        progress?.Report(new SetupProgress(SetupStep.Removing, 1));
        Log.Info("Uninstalled");
        return later;
    }

    /// <summary>Deletes a file; one in use is moved into <paramref name="trash"/> (or renamed in place) and deleted at the next restart.</summary>
    private void DeleteFile(string path, string trash, List<string> later)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"In use: {path} ({ex.Message})");
        }
        var aside = Path.Combine(trash, $"{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        try
        {
            Directory.CreateDirectory(trash);
            File.Move(path, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another volume: renaming works only on the same one. Stay in the folder under another name.
            aside = path + "." + Guid.NewGuid().ToString("N")[..8] + ".old";
            try
            {
                File.Move(path, aside);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not move {path} aside: {again.Message} ({ex.Message})");
                aside = path;
            }
        }
        System.DeleteAtRestart(aside);
        later.Add(aside);
    }
}
