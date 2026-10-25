using System.Runtime.InteropServices;
using Filee.Setup.Installation;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

public class UninstallerTests
{
    private static (SetupBench Bench, InstallRecord Record) Installed(SetupTasks? tasks = null)
    {
        var bench = new SetupBench();
        Assert.True(bench.Install(bench.Choices(tasks)).Succeeded);
        return (bench, InstallRecord.Read(bench.InstallFolder)!);
    }

    [Fact]
    public void Uninstalling_removes_filee_and_keeps_the_settings()
    {
        var (bench, record) = Installed(SetupTasks.Defaults with { DesktopIcon = true });
        using var _ = bench;
        var settings = bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, bench.Paths.SettingsFolder), "settings.json"), "{}");
        bench.Registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path",
            @"%SystemRoot%\system32;" + Path.Combine(bench.InstallFolder, "cli") + @";C:\Tools", expandable: true);
        bench.System.ExplorerMenuSupported = true;
        bench.System.MenuRegistered = true;

        var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Empty(later);
        // Filee cleaned up after itself for the user first (Explorer menu, startup entry, downloaded engines).
        Assert.Equal("run Filee.exe --uninstall-cleanup", bench.System.Calls.First(c => c.StartsWith("run ", StringComparison.Ordinal)));
        Assert.Contains("unregister-menu", bench.System.Calls);
        Assert.False(Directory.Exists(bench.InstallFolder));
        Assert.False(File.Exists(Path.Combine(bench.Paths.CommonStartMenu, "Filee.lnk")));
        Assert.False(File.Exists(Path.Combine(bench.Paths.CommonDesktop, "Filee.lnk")));
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        var path = bench.Registry.Get(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path")!;
        Assert.Equal(@"%SystemRoot%\system32;C:\Tools", path.Text);
        Assert.True(path.Expandable);
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public void The_settings_go_when_the_user_asks()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, bench.Paths.SettingsFolder), @"logs\filee.log"));
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: true, bench.Temp.Sub("trash"));
        Assert.False(Directory.Exists(bench.Paths.SettingsFolder));
        // Only Filee's folder: %APPDATA% itself stays.
        Assert.True(Directory.Exists(bench.Paths.AppData));
    }

    [Fact]
    public void Files_that_are_not_filees_stay()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var mine = Path.Combine(bench.InstallFolder, "my notes.txt");
        File.WriteAllText(mine, "mine");
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));
        Assert.True(File.Exists(mine));
        Assert.Equal([mine], Directory.EnumerateFileSystemEntries(bench.InstallFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_dll_explorer_has_loaded_is_moved_out_and_deleted_at_the_next_restart()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // Stand-in for FileeExplorerMenu.dll loaded by Explorer: a real DLL loaded into this process, which Windows
        // won't let anyone delete (but rename).
        var dll = Path.Combine(bench.InstallFolder, "FileeExplorerMenu.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "msimg32.dll"), dll, overwrite: true);
        var handle = NativeLibrary.Load(dll);
        try
        {
            var trash = bench.Temp.Sub("trash");
            var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, trash);
            var moved = Assert.Single(later);
            Assert.StartsWith(trash, moved, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"delete-at-restart {moved}", bench.System.Calls);
            // So the install folder itself is gone now, not at the next restart.
            Assert.False(Directory.Exists(bench.InstallFolder));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void The_waiting_uninstaller_is_released_last_before_its_file_goes()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var uninstaller = Path.Combine(bench.InstallFolder, "uninstall.exe");
        var seen = new List<string>();
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"),
            releaseUninstaller: () =>
            {
                // Everything else is gone by now, the uninstall entry too; only the uninstaller is left.
                seen.AddRange(Directory.EnumerateFiles(bench.InstallFolder, "*", SearchOption.AllDirectories));
                Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
            });
        Assert.Equal([uninstaller], seen);
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    [Fact]
    public void Programs_left_running_are_ended_but_not_the_uninstaller_that_waits()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.System.Running.Add(new RunningProgram(11, Path.Combine(bench.InstallFolder, "filee-cli.exe")));
        bench.System.Running.Add(new RunningProgram(12, Path.Combine(bench.InstallFolder, "uninstall.exe")));
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));
        Assert.Contains("kill 11", bench.System.Calls);
        Assert.DoesNotContain("kill 12", bench.System.Calls);
    }

    [Fact]
    public void Install_record_round_trip_and_bad_records()
    {
        using var temp = new TempFolder();
        var record = new InstallRecord("2.0.0", InstallScope.User, ["Filee.exe", @"cli\filee.cmd"]);
        File.WriteAllBytes(InstallRecord.PathIn(temp.Path), record.ToJson());
        var read = InstallRecord.Read(temp.Path)!;
        Assert.Equal(record.Version, read.Version);
        Assert.Equal(record.Scope, read.Scope);
        Assert.Equal(record.Files, read.Files);

        // A record that points outside the folder is no record (the uninstaller then refuses).
        File.WriteAllText(InstallRecord.PathIn(temp.Path), """{"version":"2.0.0","scope":"machine","files":["..\\..\\Windows\\notepad.exe"]}""");
        Assert.Null(InstallRecord.Read(temp.Path));
        File.WriteAllText(InstallRecord.PathIn(temp.Path), "not json");
        Assert.Null(InstallRecord.Read(temp.Path));
        Assert.Null(InstallRecord.Read(temp.Sub("missing")));
    }
}

public class PreviousInstallTests
{
    [Fact]
    public void An_entry_without_filee_in_its_folder_is_no_installation()
    {
        using var temp = new TempFolder();
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "InstallLocation", temp.Sub("gone") + "\\");
        Assert.Null(PreviousInstall.Find(registry, InstallScope.Machine));
        Assert.Null(PreviousInstall.Find(new MemoryRegistry(), InstallScope.Machine));
    }

    [Fact]
    public void Inno_setups_app_path_is_enough_and_the_scope_is_kept_apart()
    {
        using var temp = new TempFolder();
        temp.Write(@"Filee\Filee.exe");
        temp.Write(@"Filee\unins000.exe");
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "Inno Setup: App Path", temp.Sub("Filee"));
        var previous = PreviousInstall.Find(registry, InstallScope.Machine)!;
        Assert.Equal(temp.Sub("Filee"), previous.Folder);
        Assert.True(previous.ByInno);
        Assert.Null(previous.RecordedTasks);
        Assert.Contains("unins000.exe", previous.OwnedFiles());
        Assert.Null(PreviousInstall.Find(registry, InstallScope.User));
    }

    [Fact]
    public void Current_tasks_come_from_what_is_registered_now()
    {
        using var temp = new TempFolder();
        var paths = SetupPaths.Sandbox(temp.Sub("pc"));
        var folder = Path.GetDirectoryName(temp.Write(@"pc\Program Files\Filee\Filee.exe"))!;
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "InstallLocation", folder);
        // 1.x recorded the desktop icon and PATH, but the user deleted the shortcut since.
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, SetupNames.InnoTasksValue, @"contextmenu,startup,cli,desktopicon");
        registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path", folder.ToUpperInvariant() + @"\CLI\");
        var system = new FakeSystem { ExplorerMenuSupported = true, MenuRegistered = false };

        var tasks = PreviousInstall.Find(registry, InstallScope.Machine)!.CurrentTasks(registry, system, paths);
        Assert.Equal(new SetupTasks(ContextMenu: true, ModernMenu: false, Startup: true, Cli: true, DesktopIcon: false), tasks);
    }
}

public class PathVariableTests
{
    [Theory]
    [InlineData(null, @"C:\F\cli", @"C:\F\cli")]
    [InlineData("", @"C:\F\cli", @"C:\F\cli")]
    [InlineData(@"C:\a", @"C:\F\cli", @"C:\a;C:\F\cli")]
    [InlineData(@"C:\a;", @"C:\F\cli", @"C:\a;C:\F\cli")]
    [InlineData(@"C:\a;c:\f\CLI\", @"C:\F\cli", @"C:\a;c:\f\CLI\")]
    public void Add(string? paths, string folder, string expected) => Assert.Equal(expected, PathVariable.Add(paths, folder));

    [Theory]
    [InlineData(@"C:\a;C:\F\cli;C:\b", @"C:\a;C:\b")]
    [InlineData(@"C:\F\cli\;C:\a", @"C:\a")]
    [InlineData(@"C:\a;;C:\F\CLI;C:\b;", @"C:\a;;C:\b;")]
    [InlineData(@"C:\F\cli", "")]
    [InlineData(@"C:\a;C:\F\cli2", @"C:\a;C:\F\cli2")]
    public void Remove(string paths, string expected) => Assert.Equal(expected, PathVariable.Remove(paths, @"C:\F\cli"));

    [Fact]
    public void Setting_keeps_the_kind_and_reports_changes()
    {
        var registry = new MemoryRegistry();
        registry.SetString(Hive.CurrentUser, SetupNames.UserEnvironmentKey, "Path", @"C:\a", expandable: false);
        Assert.True(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: true));
        Assert.False(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: true));
        var entry = registry.Get(Hive.CurrentUser, SetupNames.UserEnvironmentKey, "Path")!;
        Assert.Equal(@"C:\a;C:\F\cli", entry.Text);
        Assert.False(entry.Expandable);
        Assert.True(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: false));
        Assert.False(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: false));
        // Never touched the machine PATH.
        Assert.False(registry.KeyExists(Hive.LocalMachine, SetupNames.MachineEnvironmentKey));
    }

    [Fact]
    public void The_sandbox_registry_survives_a_restart_of_setup()
    {
        using var temp = new TempFolder();
        var file = temp.Sub("registry.json");
        var registry = new MemoryRegistry(file);
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "DisplayName", "Filee");
        registry.SetNumber(Hive.LocalMachine, SetupNames.UninstallKey, "NoModify", 1);
        registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path", "%SystemRoot%", expandable: true);
        var again = new MemoryRegistry(file);
        Assert.Equal("Filee", again.Get(Hive.LocalMachine, SetupNames.UninstallKey, "DisplayName")?.Text);
        Assert.Equal(1, again.Get(Hive.LocalMachine, SetupNames.UninstallKey, "NoModify")?.Value);
        Assert.True(again.Get(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path")!.Expandable);
        Assert.True(again.KeyExists(Hive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"));
        again.DeleteKey(Hive.LocalMachine, @"Software\Microsoft");
        Assert.False(new MemoryRegistry(file).KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
    }
}
