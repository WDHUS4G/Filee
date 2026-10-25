// The folders Setup reads and writes besides the install folder. A /SANDBOX run and the tests put all of them into a
// scratch folder.

namespace Filee.Setup.Platform;

/// <param name="ProgramFiles">Default parent of a per-machine install (Inno Setup's {autopf}).</param>
/// <param name="UserPrograms">Default parent of a per-user install (%LocalAppData%\Programs).</param>
/// <param name="CommonStartMenu">All users' Start menu programs folder.</param>
/// <param name="UserStartMenu">This user's Start menu programs folder.</param>
/// <param name="CommonDesktop">All users' desktop.</param>
/// <param name="UserDesktop">This user's desktop.</param>
/// <param name="AppData">%APPDATA%: Filee's settings are in AppData\Filee.</param>
/// <param name="LocalAppData">%LocalAppData%: downloaded engines (LocalAppData\Filee\engines), Filee 1.1's Velopack copy.</param>
/// <param name="Temp">Setup's temporary files.</param>
/// <param name="SystemFolder">System32 (the Microsoft C++ runtime, if installed).</param>
public sealed record SetupPaths(
    string ProgramFiles,
    string UserPrograms,
    string CommonStartMenu,
    string UserStartMenu,
    string CommonDesktop,
    string UserDesktop,
    string AppData,
    string LocalAppData,
    string Temp,
    string SystemFolder)
{
    /// <summary>Filee's settings folder (UserDataStore.DefaultDirectory without FILEE_DATA_DIR).</summary>
    public string SettingsFolder => Path.Combine(AppData, "Filee");

    /// <summary>Downloaded engines (EngineEnvironment.DownloadRoot).</summary>
    public string EnginesFolder => Path.Combine(LocalAppData, "Filee", "engines");

    public string StartMenu(InstallScope scope) => scope == InstallScope.Machine ? CommonStartMenu : UserStartMenu;

    public string Desktop(InstallScope scope) => scope == InstallScope.Machine ? CommonDesktop : UserDesktop;

    public string DefaultInstallFolder(InstallScope scope) =>
        Path.Combine(scope == InstallScope.Machine ? ProgramFiles : UserPrograms, "Filee");

    /// <summary>This PC's folders.</summary>
    public static SetupPaths Real()
    {
        static string Folder(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        var localAppData = Folder(Environment.SpecialFolder.LocalApplicationData);
        // A 64-bit process: ProgramFiles is "C:\Program Files", not the x86 one.
        return new SetupPaths(
            Folder(Environment.SpecialFolder.ProgramFiles),
            Path.Combine(localAppData, "Programs"),
            Folder(Environment.SpecialFolder.CommonPrograms),
            Folder(Environment.SpecialFolder.Programs),
            Folder(Environment.SpecialFolder.CommonDesktopDirectory),
            Folder(Environment.SpecialFolder.DesktopDirectory),
            Folder(Environment.SpecialFolder.ApplicationData),
            localAppData,
            Path.GetTempPath(),
            Environment.SystemDirectory);
    }

    /// <summary>Every folder inside <paramref name="root"/> (System32 stays the real one: it is only read).</summary>
    public static SetupPaths Sandbox(string root)
    {
        root = Path.GetFullPath(root);
        string Sub(string name) => Path.Combine(root, name);
        return new SetupPaths(
            Sub("Program Files"),
            Sub(Path.Combine("LocalAppData", "Programs")),
            Sub(Path.Combine("ProgramData", "Start Menu", "Programs")),
            Sub(Path.Combine("AppData", "Start Menu", "Programs")),
            Sub(Path.Combine("Public", "Desktop")),
            Sub("Desktop"),
            Sub("AppData"),
            Sub("LocalAppData"),
            Sub("Temp"),
            Environment.SystemDirectory);
    }
}
