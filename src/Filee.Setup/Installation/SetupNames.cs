// Names Setup shares with Filee 1.x's Inno Setup installer, the app and Windows. Changing one breaks updates.

namespace Filee.Setup.Installation;

public static class SetupNames
{
    /// <summary>
    /// Inno Setup's AppId of Filee (installer/Filee.iss, 1.x). Apps &amp; Features lists Filee under the uninstall key
    /// "{id}_is1"; Setup keeps that key, so an update from 1.x leaves one entry.
    /// </summary>
    public const string AppGuid = "C8686E06-8327-40A2-B0F7-3DEDD61D9248";

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppGuid + "}_is1";

    /// <summary>Filee 1.1 and earlier: Velopack installed it per user and registered it here (HKCU).</summary>
    public const string VelopackUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Filee";

    public const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    public const string UserEnvironmentKey = "Environment";

    public const string AppName = "Filee";
    public const string Publisher = "Filee contributors";
    public const string AppExe = "Filee.exe";

    /// <summary>The uninstaller next to Filee.exe (UpdateService.IsInstalled looks for it).</summary>
    public const string UninstallerExe = "uninstall.exe";

    /// <summary>The list of installed files next to Filee.exe: what the uninstaller removes and an update replaces.</summary>
    public const string InstallRecordFile = "uninstall.json";

    /// <summary>The "filee" command (cli\filee.cmd), the folder Setup puts on PATH.</summary>
    public const string CliFolder = "cli";

    /// <summary>Inno Setup's uninstaller files, removed when Setup takes over a 1.x installation.</summary>
    public static readonly IReadOnlyList<string> InnoUninstallerFiles = ["unins000.exe", "unins000.dat", "unins000.msg"];

    /// <summary>Start menu and desktop shortcut name.</summary>
    public const string ShortcutName = "Filee.lnk";

    /// <summary>Registry values Setup adds to the uninstall key (Inno Setup kept the same in "Inno Setup: …").</summary>
    public const string TasksValue = "Filee Setup: Tasks";
    public const string LanguageValue = "Filee Setup: Language";
    public const string ScopeValue = "Filee Setup: Scope";
    public const string InnoTasksValue = "Inno Setup: Selected Tasks";
    public const string InnoAppPathValue = "Inno Setup: App Path";
    public const string InnoVersionValue = "Inno Setup: Setup Version";
    public const string InnoLanguageValue = "Inno Setup: Language";

    /// <summary>Windows 10 1809: the oldest Windows Filee runs on (Inno Setup's MinVersion).</summary>
    public const int MinimumBuild = 17763;

    /// <summary>Where Microsoft offers the Visual C++ Redistributable (x64).</summary>
    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
}
