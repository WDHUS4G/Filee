// What Setup does to Windows besides files and the registry: shortcuts, the Windows 11 Explorer menu package, running
// Filee's own installer commands, closing programs, telling Windows about a new PATH. Tests replace it with a fake;
// a /SANDBOX run with SandboxSystem, which leaves the PC alone.

using System.Diagnostics;
using System.Runtime.Versioning;
using Filee.Core.Platform;
using Filee.Platform.Windows;

namespace Filee.Setup.Platform;

/// <summary>A process started from a file in the install folder.</summary>
public sealed record RunningProgram(int Id, string Path);

public interface ISetupSystem
{
    /// <summary>Windows 11 on x64: the top-level Explorer menu exists and Setup may offer it.</summary>
    bool ExplorerMenuSupported { get; }

    bool IsExplorerMenuRegistered();

    /// <summary>
    /// The package is registered for <paramref name="installFolder"/> at the version this build ships. Explorer then
    /// picks up the new DLL by itself; registering it again fails while Explorer has the old one loaded (0x80073D02).
    /// </summary>
    bool IsExplorerMenuCurrent(string installFolder);

    /// <summary>Registers the sparse package next to Filee.exe; returns why not, or null when it worked.</summary>
    string? RegisterExplorerMenu(string installFolder, bool removeFirst);

    void UnregisterExplorerMenu();

    void CreateShortcut(string path, string target, string workingFolder, string description);

    /// <summary>Tells running programs (new terminals) that PATH changed.</summary>
    void BroadcastEnvironmentChange();

    /// <summary>Runs a program without a window and waits; the exit code, or -1 when it failed or took too long.</summary>
    int RunAndWait(string executable, string arguments, TimeSpan timeout);

    IReadOnlyList<RunningProgram> ProgramsIn(string folder);

    void Kill(int processId);

    /// <summary>Has Windows delete a file or an empty folder at the next restart (what is still in use now).</summary>
    void DeleteAtRestart(string path);
}

/// <summary>The real thing.</summary>
[SupportedOSPlatform("windows")]
public class WindowsSetupSystem(SetupLog log) : ISetupSystem
{
    protected SetupLog Log { get; } = log;

    public virtual bool ExplorerMenuSupported => ExplorerMenuRegistration.IsSupportedOs;

    public virtual bool IsExplorerMenuRegistered()
    {
        try
        {
            return ExplorerMenuRegistration.Find() is not null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public virtual bool IsExplorerMenuCurrent(string installFolder)
    {
        try
        {
            return ExplorerMenuRegistration.GetState(installFolder) == ModernContextMenuState.On;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public virtual string? RegisterExplorerMenu(string installFolder, bool removeFirst)
    {
        if (removeFirst)
            UnregisterExplorerMenu();
        var package = Path.Combine(installFolder, ExplorerMenuPackage.PackageFileName);
        return RunPowerShell(errorFile => ExplorerMenuRegistration.BuildAddScript(package, installFolder, errorFile));
    }

    public virtual void UnregisterExplorerMenu()
    {
        if (!IsExplorerMenuRegistered())
            return;
        if (RunPowerShell(ExplorerMenuRegistration.BuildRemoveScript) is { } error)
            Log.Warn($"Removing the Explorer menu package failed: {error}");
    }

    /// <summary>Windows PowerShell with Setup's rights (the Appx cmdlets); the error message, or null.</summary>
    private string? RunPowerShell(Func<string, string> buildScript)
    {
        var errorFile = Path.Combine(Path.GetTempPath(), $"Filee-Setup-powershell-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(ExplorerMenuRegistration.CreateStartInfo(buildScript(errorFile), elevated: false));
            if (process is null)
                return "PowerShell did not start.";
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill();
                return "Windows did not finish within 3 minutes.";
            }
            if (process.ExitCode == 0)
                return null;
            var message = File.Exists(errorFile) ? ExplorerMenuRegistration.ShortenDeploymentError(File.ReadAllText(errorFile)) : "";
            return message.Length > 0 ? message : $"PowerShell exit code {process.ExitCode}.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            TryDelete(errorFile);
        }
    }

    public virtual void CreateShortcut(string path, string target, string workingFolder, string description) =>
        ShellLink.Create(path, target, workingFolder, description);

    public virtual void BroadcastEnvironmentChange() => NativeMethods.BroadcastEnvironmentChange();

    public virtual int RunAndWait(string executable, string arguments, TimeSpan timeout)
    {
        // Something that isn't a program (a damaged or half-written Filee.exe) makes Windows show "This app can't run
        // on your PC" and wait for an answer, even with Setup's error mode: never start one.
        if (!IsProgram(executable))
        {
            Log.Warn($"Not running {executable}: it is not a Windows program");
            return -1;
        }
        try
        {
            using var process = Process.Start(CreateStartInfo(executable, arguments));
            if (process is null)
                return -1;
            if (process.WaitForExit(timeout))
                return process.ExitCode;
            Log.Warn($"{Path.GetFileName(executable)} {arguments} did not finish within {timeout.TotalSeconds:0} s");
            process.Kill();
            return -1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not run {executable} {arguments}: {ex.Message}");
            return -1;
        }
    }

    private static bool IsProgram(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            return file.Read(header) == 2 && header[0] == 'M' && header[1] == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected virtual ProcessStartInfo CreateStartInfo(string executable, string arguments) =>
        new(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };

    public virtual IReadOnlyList<RunningProgram> ProgramsIn(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var found = new List<RunningProgram>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                var path = NativeMethods.ImagePathOf(process.Id);
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    found.Add(new RunningProgram(process.Id, path));
            }
        }
        return found;
    }

    public virtual void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: false);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ended meanwhile, or not ours to end: the file swap copes with files in use.
        }
    }

    public virtual void DeleteAtRestart(string path)
    {
        if (!NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
            Log.Warn($"Could not schedule {path} for deletion at the next restart");
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// /SANDBOX: no Explorer menu, no PATH broadcast, nothing scheduled for the next restart, and Filee's installer
/// commands run with FILEE_DATA_DIR in the sandbox, which gives that Filee its own data and single instance: it never
/// reaches (or quits) the Filee the user runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SandboxSetupSystem(SetupLog log, string dataFolder) : WindowsSetupSystem(log)
{
    public override bool ExplorerMenuSupported => false;

    public override bool IsExplorerMenuRegistered() => false;

    public override bool IsExplorerMenuCurrent(string installFolder) => false;

    public override string? RegisterExplorerMenu(string installFolder, bool removeFirst) => "Not in a sandbox.";

    public override void UnregisterExplorerMenu()
    {
    }

    public override void BroadcastEnvironmentChange()
    {
    }

    public override void DeleteAtRestart(string path) => Log.Info($"Sandbox: would delete {path} at the next restart");

    protected override ProcessStartInfo CreateStartInfo(string executable, string arguments)
    {
        var info = base.CreateStartInfo(executable, arguments);
        info.Environment["FILEE_DATA_DIR"] = dataFolder;
        return info;
    }
}
