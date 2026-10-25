// Installs or updates Filee, in this order (installer/Filee.iss did the same in 1.x):
//  1. checks free space and the payload's checksum: nothing has changed yet;
//  2. closes a running Filee ("Filee.exe --quit", which saves and exits), then ends what still runs from the folder;
//  3. removes Filee 1.1's per-user Velopack copy (settings and engines stay);
//  4. unpacks into a work folder inside the install folder, checking every file, with the uninstaller and its list;
//  5. swaps the files in (FileSwap: replaced and obsolete files go to a backup, Inno Setup's unins000.* too);
//  6. writes the uninstall entry (the same key Inno Setup used, so Apps & Features keeps one Filee), the shortcuts and
//     PATH; on Windows 11 registers the top-level Explorer menu package;
//  7. deletes the backup, or on any failure in 4-6 puts everything back.
// Filee's own settings (Explorer menu entry, start at sign-in, engines, language) are handed to Filee when Setup
// starts it, as before: Filee applies them for the signed-in user. After an update Filee goes back to the tray and
// shows the new version's "What's new" slides (--background --whats-new).

using Filee.Platform.Windows;
using Filee.Setup.Payload;
using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public sealed class Installer(SetupContext context, SetupPayload payload)
{
    private ISetupRegistry Registry => context.Registry;
    private ISetupSystem System => context.System;
    private SetupPaths Paths => context.Paths;
    private SetupLog Log => context.Log;

    /// <summary>How long Filee may take to save and exit when asked (its own limit is 20 s).</summary>
    public static TimeSpan QuitTimeout { get; } = TimeSpan.FromSeconds(40);

    /// <summary>Space needed besides the files themselves (the uninstaller, Windows' own bookkeeping).</summary>
    private const long SpareSpace = 64L << 20;

    public InstallResult Run(InstallChoices choices, PreviousInstall? previous, IProgress<SetupProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(choices.Folder));
        var update = previous is not null && string.Equals(previous.Folder, folder, StringComparison.OrdinalIgnoreCase);
        Log.Info($"{(update ? "Update" : choices.Portable ? "Portable copy" : "Install")} of Filee {payload.Manifest.Version} " +
                 $"into {folder} for {(choices.Scope == InstallScope.Machine ? "all users" : "this user")}; " +
                 $"previous: {(previous is null ? "none" : $"{previous.Version} in {previous.Folder}{(previous.ByInno ? " (Inno Setup)" : "")}")}; " +
                 $"options: {choices.Tasks.ToInnoString()}; engines: {string.Join(',', choices.Engines)}; language: {choices.Language}");
        var warnings = new List<SetupWarning>();
        FileSwap? swap = null;
        var undo = new Stack<(string What, Action Action)>();
        var changing = false;
        var createdFolder = false;
        try
        {
            progress?.Report(new SetupProgress(SetupStep.Verifying, 0));
            CheckSpace(folder);
            payload.Verify(Scaled(progress, SetupStep.Verifying, 0, 0.08), cancellation);
            Log.Info("Payload checksum OK");

            var menuWasRegistered = !choices.Portable && System.ExplorerMenuSupported && System.IsExplorerMenuRegistered();
            var velopack = !choices.Portable && VelopackInstall.Exists(Paths);

            progress?.Report(new SetupProgress(SetupStep.Closing, 0.08));
            CloseFilee(folder, choices.ForceClose, velopack);
            cancellation.ThrowIfCancellationRequested();

            if (velopack)
            {
                progress?.Report(new SetupProgress(SetupStep.RemovingOld, 0.1));
                VelopackInstall.Remove(Paths, Registry, System, Log);
            }

            // 4. Unpack and check.
            createdFolder = !Directory.Exists(folder);
            Directory.CreateDirectory(folder);
            RemoveLeftoverWorkFolders(folder);
            var work = Path.Combine(folder, FileSwap.WorkFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
            swap = new FileSwap(folder, work, System, Log);
            progress?.Report(new SetupProgress(SetupStep.Copying, 0.12));
            var total = Math.Max(1, payload.Manifest.TotalSize);
            payload.ExtractTo(swap.Staging, new SyncProgress<long>(bytes =>
                progress?.Report(new SetupProgress(SetupStep.Copying, 0.12 + 0.68 * bytes / total))), cancellation);
            Log.Info($"Unpacked and checked {payload.Manifest.Files.Count} files ({payload.Manifest.TotalSize / 1_000_000} MB)");
            var newFiles = payload.Manifest.Files.Select(f => f.Path).ToList();
            if (!choices.Portable)
            {
                payload.CopyProgramTo(Path.Combine(swap.Staging, SetupNames.UninstallerExe));
                newFiles.Add(SetupNames.UninstallerExe);
                newFiles.Add(SetupNames.InstallRecordFile);
                File.WriteAllBytes(Path.Combine(swap.Staging, SetupNames.InstallRecordFile),
                    new InstallRecord(payload.Manifest.Version, choices.Scope, newFiles).ToJson());
            }
            var missing = RuntimeCheck.FindMissing(swap.Staging, Paths.SystemFolder);
            Log.Info($"Microsoft Visual C++ runtime check: {(missing.Count == 0 ? "nothing missing" : $"{missing.Count} missing")}");
            if (missing.Count > 0)
            {
                Log.Warn("Missing Microsoft Visual C++ runtime: " + string.Join(", ", missing.Select(m => $"{m.Program} needs {m.Dll}")));
                warnings.Add(new RuntimeWarning(missing));
            }
            cancellation.ThrowIfCancellationRequested();

            // 5. Swap. From here on, a failure puts everything back.
            changing = true;
            progress?.Report(new SetupProgress(SetupStep.Copying, 0.82));
            foreach (var file in newFiles)
                swap.Replace(file);
            var obsolete = (previous?.OwnedFiles() ?? []).Concat(SetupNames.InnoUninstallerFiles)
                .Except(newFiles, StringComparer.OrdinalIgnoreCase)
                .Where(f => !f.StartsWith(FileSwap.WorkFolderPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(f => File.Exists(Path.Combine(folder, f)))
                .ToList();
            foreach (var file in obsolete)
                swap.Remove(file);
            Log.Info($"Installed {newFiles.Count} files; {obsolete.Count} files of the previous version are no longer needed");

            // 6. Register.
            progress?.Report(new SetupProgress(SetupStep.Finishing, 0.88));
            if (!choices.Portable)
                Register(folder, choices, update, undo);

            if (!choices.Portable && System.ExplorerMenuSupported)
            {
                progress?.Report(new SetupProgress(SetupStep.ExplorerMenu, 0.92));
                if (UpdateExplorerMenu(folder, choices, update, menuWasRegistered) is { } error)
                    warnings.Add(new ExplorerMenuWarning(error));
            }

            // 7. Done: the old files go.
            progress?.Report(new SetupProgress(SetupStep.Finishing, 0.97));
            swap.Commit();
            progress?.Report(new SetupProgress(SetupStep.Finishing, 1));
            Log.Info("Done");
            var launch = choices.Portable ? null : new LaunchRequest(Path.Combine(folder, SetupNames.AppExe), LaunchArguments(choices, update, updatedFrom: previous?.Version));
            return new InstallResult(launch, warnings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Install failed: {ex}");
            if (changing)
                progress?.Report(new SetupProgress(SetupStep.RollingBack, 1));
            while (undo.TryPop(out var step))
            {
                try
                {
                    step.Action();
                }
                catch (Exception undoError) when (undoError is not OutOfMemoryException)
                {
                    Log.Error($"Undo {step.What} failed: {undoError.Message}");
                }
            }
            if (swap is not null)
            {
                if (changing)
                    swap.Rollback();
                else
                    swap.Cleanup();
            }
            if (createdFolder)
                DeleteIfEmpty(folder);
            return new InstallResult(null, warnings, ex, RolledBack: changing);
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The arguments Filee starts with: the choices of a fresh install, or after an update the new version's "What's new"
    /// slides, back in the tray (/SILENT, the app's own updater) or over the main window (<paramref name="shown"/>: the
    /// user clicked "Launch Filee"). <paramref name="updatedFrom"/>, the version updated from, tells Filee what is new.
    /// </summary>
    public static string LaunchArguments(InstallChoices choices, bool update, bool shown = false, string? updatedFrom = null) => update
        ? (shown ? "" : "--background ") + "--whats-new" + (IsVersion(updatedFrom) ? "=" + updatedFrom : "")
        : $"--install-engines={string.Join(',', choices.Engines)} " +
          $"--start-with-windows={OnOff(choices.Tasks.Startup)} --context-menu={OnOff(choices.Tasks.ContextMenu)}" +
          (choices.LanguagePicked ? $" --language={choices.Language}" : "");

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>Digits and dots only: safe on a command line without quoting.</summary>
    private static bool IsVersion(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= 32 && text.All(c => char.IsAsciiDigit(c) || c == '.');

    private void CheckSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return;
            var drive = new DriveInfo(root);
            var needed = payload.Manifest.TotalSize + payload.Offset + SpareSpace;
            Log.Info($"Space on {root}: {drive.AvailableFreeSpace / 1_000_000} MB free, {needed / 1_000_000} MB needed");
            if (drive.AvailableFreeSpace < needed)
                throw new SetupException("installer.error.space", $"Not enough space on {root}",
                    root, FormatSize(needed), FormatSize(drive.AvailableFreeSpace));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read the free space: {ex.Message}");
        }
    }

    /// <summary>"1.3 GB", "420 MB" (as the app writes sizes).</summary>
    public static string FormatSize(long bytes) => bytes >= 1_000_000_000
        ? string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.0} GB")
        : string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $"{Math.Max(1, bytes / 1_000_000)} MB");

    /// <summary>
    /// Asks Filee to exit (it saves first and waits for running conversions to stop), then ends whatever still runs
    /// from the install folder (filee-cli, an engine), like Inno Setup's CloseApplications did.
    /// </summary>
    private void CloseFilee(string folder, bool force, bool velopack)
    {
        var exe = Path.Combine(folder, SetupNames.AppExe);
        if (File.Exists(exe))
        {
            var code = System.RunAndWait(exe, "--quit", QuitTimeout);
            Log.Info($"Filee --quit: exit code {code}");
        }
        var running = System.ProgramsIn(folder).ToList();
        if (velopack)
            running.AddRange(System.ProgramsIn(VelopackInstall.Root(Paths)));
        if (running.Count == 0)
            return;
        if (!force)
        {
            Log.Warn($"Still running, left alone (/NOCLOSEAPPLICATIONS): {string.Join(", ", running.Select(p => p.Path))}");
            return;
        }
        foreach (var program in running)
        {
            Log.Info($"Ending {program.Path} ({program.Id})");
            System.Kill(program.Id);
        }
    }

    private void RemoveLeftoverWorkFolders(string folder)
    {
        foreach (var leftover in Directory.EnumerateDirectories(folder, FileSwap.WorkFolderPrefix + "*"))
        {
            Log.Info($"Removing {leftover} (left by an interrupted Setup)");
            FileSwap.DeleteTree(leftover, System, Log);
        }
    }

    private void Register(string folder, InstallChoices choices, bool update, Stack<(string, Action)> undo)
    {
        var hive = PreviousInstall.HiveOf(choices.Scope);

        // The uninstall entry: the Inno Setup key, rewritten (its "Inno Setup: …" values and unins000.exe are gone).
        var before = Snapshot(hive, SetupNames.UninstallKey);
        undo.Push(("uninstall entry", () => Restore(hive, SetupNames.UninstallKey, before)));
        WriteUninstallEntry(hive, folder, choices);

        // Shortcuts.
        var startMenu = Path.Combine(Paths.StartMenu(choices.Scope), SetupNames.ShortcutName);
        var desktop = Path.Combine(Paths.Desktop(choices.Scope), SetupNames.ShortcutName);
        var exe = Path.Combine(folder, SetupNames.AppExe);
        if (!choices.NoIcons)
        {
            if (!File.Exists(startMenu))
                undo.Push(("Start menu shortcut", () => File.Delete(startMenu)));
            System.CreateShortcut(startMenu, exe, folder, "Filee");
        }
        if (choices.Tasks.DesktopIcon && !choices.NoIcons)
        {
            if (!File.Exists(desktop))
                undo.Push(("desktop shortcut", () => File.Delete(desktop)));
            System.CreateShortcut(desktop, exe, folder, "Filee");
        }
        else if (update && File.Exists(desktop))
        {
            // Unticked on an update: the shortcut goes (ticking it again brings it back).
            File.Delete(desktop);
        }

        // PATH.
        var cli = Path.Combine(folder, SetupNames.CliFolder);
        if (PathVariable.Set(Registry, choices.Scope, cli, choices.Tasks.Cli))
        {
            undo.Push(("PATH", UndoPath));
            System.BroadcastEnvironmentChange();
            Log.Info($"{(choices.Tasks.Cli ? "Added" : "Removed")} {cli} {(choices.Tasks.Cli ? "to" : "from")} PATH");
        }

        void UndoPath()
        {
            PathVariable.Set(Registry, choices.Scope, cli, !choices.Tasks.Cli);
            System.BroadcastEnvironmentChange();
        }
    }

    private void WriteUninstallEntry(Hive hive, string folder, InstallChoices choices)
    {
        const string key = SetupNames.UninstallKey;
        var version = payload.Manifest.Version;
        var uninstaller = Path.Combine(folder, SetupNames.UninstallerExe);
        var repository = SetupInfo.RepositoryUrl.TrimEnd('/');
        Registry.DeleteKey(hive, key);
        Registry.SetString(hive, key, "DisplayName", SetupNames.AppName);
        Registry.SetString(hive, key, "DisplayVersion", version);
        Registry.SetString(hive, key, "Publisher", SetupNames.Publisher);
        Registry.SetString(hive, key, "DisplayIcon", Path.Combine(folder, SetupNames.AppExe));
        Registry.SetString(hive, key, "InstallLocation", folder + Path.DirectorySeparatorChar);
        Registry.SetString(hive, key, "UninstallString", SetupOptions.Quote(uninstaller));
        Registry.SetString(hive, key, "QuietUninstallString", SetupOptions.Quote(uninstaller) + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        Registry.SetString(hive, key, "URLInfoAbout", repository);
        Registry.SetString(hive, key, "HelpLink", repository + "/issues");
        Registry.SetString(hive, key, "URLUpdateInfo", repository + "/releases");
        Registry.SetString(hive, key, "InstallDate", DateTime.Now.ToString("yyyyMMdd", global::System.Globalization.CultureInfo.InvariantCulture));
        Registry.SetNumber(hive, key, "NoModify", 1);
        Registry.SetNumber(hive, key, "NoRepair", 1);
        Registry.SetNumber(hive, key, "EstimatedSize", (int)Math.Min(int.MaxValue, (payload.Manifest.TotalSize + payload.Offset) / 1024));
        if (Version.TryParse(version.Split('-', '+')[0], out var parsed))
        {
            Registry.SetNumber(hive, key, "VersionMajor", parsed.Major);
            Registry.SetNumber(hive, key, "VersionMinor", parsed.Minor);
        }
        Registry.SetString(hive, key, SetupNames.TasksValue, choices.Tasks.ToInnoString());
        Registry.SetString(hive, key, SetupNames.LanguageValue, choices.Language);
        Registry.SetString(hive, key, SetupNames.ScopeValue, choices.Scope == InstallScope.Machine ? "machine" : "user");
        Log.Info($"Uninstall entry written ({(hive == Hive.LocalMachine ? "HKLM" : "HKCU")}\\{key})");
    }

    private Dictionary<string, RegistryEntry>? Snapshot(Hive hive, string key)
    {
        if (!Registry.KeyExists(hive, key))
            return null;
        return Registry.ValueNames(hive, key)
            .Select(name => (name, entry: Registry.Get(hive, key, name)))
            .Where(v => v.entry is not null)
            .ToDictionary(v => v.name, v => v.entry!, StringComparer.OrdinalIgnoreCase);
    }

    private void Restore(Hive hive, string key, Dictionary<string, RegistryEntry>? values)
    {
        Registry.DeleteKey(hive, key);
        foreach (var (name, entry) in values ?? [])
        {
            if (entry.Value is int number)
                Registry.SetNumber(hive, key, name, number);
            else
                Registry.SetString(hive, key, name, (string)entry.Value, entry.Expandable);
        }
    }

    /// <summary>
    /// A fresh install registers the top-level menu when ticked (removing an older registration first, which may point
    /// to another folder) and removes it when not; an update keeps what was chosen in Filee and refreshes it, unless
    /// it is registered for this folder at this version already.
    /// Returns Windows' error when registering failed.
    /// </summary>
    private string? UpdateExplorerMenu(string folder, InstallChoices choices, bool update, bool wasRegistered)
    {
        if (!OperatingSystem.IsWindows() || !ExplorerMenuRegistration.HasFiles(folder))
        {
            Log.Warn("The Explorer menu files are not in this build");
            return null;
        }
        string? error = null;
        if (update && wasRegistered && System.IsExplorerMenuCurrent(folder))
        {
            Log.Info("The Windows 11 Explorer menu package is registered already; Explorer loads the new menu by itself");
        }
        else if (update ? wasRegistered : choices.Tasks.ContextMenu && choices.Tasks.ModernMenu)
        {
            Log.Info("Registering the Windows 11 Explorer menu package");
            error = System.RegisterExplorerMenu(folder, removeFirst: !update);
        }
        else if (!update && System.IsExplorerMenuRegistered())
        {
            System.UnregisterExplorerMenu();
        }
        if (error is not null)
            Log.Warn($"Windows did not register the Explorer menu: {error}");
        return error;
    }

    private static IProgress<double>? Scaled(IProgress<SetupProgress>? progress, SetupStep step, double from, double to) =>
        progress is null ? null : new SyncProgress<double>(f => progress.Report(new SetupProgress(step, from + (to - from) * f)));
}

/// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to a synchronization context).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
