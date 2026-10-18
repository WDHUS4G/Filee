// Registers and removes the Windows 11 File Explorer menu package (ExplorerMenuPackage) and reports its state.
// Windows accepts an unsigned package that contains a COM server only when it is added with administrator rights
// (it is then installed for all users), so adding goes through one UAC prompt; state queries are kernel32 calls.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.Platform.Windows;

/// <summary>Adds, removes and inspects the Explorer menu package for the current user.</summary>
[SupportedOSPlatform("windows")]
public static class ExplorerMenuRegistration
{
    /// <summary>Win32 ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>Add-AppxPackage usually takes a few seconds; after this the attempt counts as failed.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>A registered copy of the package.</summary>
    /// <param name="FullName">Package full name, e.g. <c>Filee.ExplorerMenu_1.0.0.0_x64__c73vxh346rtay</c>.</param>
    /// <param name="Version">Identity version from the full name.</param>
    /// <param name="ExternalLocation">Folder the package points at (null when Windows does not say).</param>
    public sealed record RegisteredPackage(string FullName, Version Version, string? ExternalLocation);

    /// <summary>Windows 11 on x64: the new context menu exists and Explorer can load the x64 DLL.</summary>
    public static bool IsSupportedOs =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ExplorerMenuPackage.MinimumBuild)
        && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>The title file the DLL reads (see <see cref="ExplorerMenuPackage.TitleFileName"/>).</summary>
    public static string TitleFilePath => Path.Combine(UserDataStore.DefaultDirectory, ExplorerMenuPackage.TitleFileName);

    /// <summary>True when the DLL and the package were shipped next to Filee.exe (installed builds).</summary>
    public static bool HasFiles(string installFolder) =>
        File.Exists(Path.Combine(installFolder, ExplorerMenuPackage.DllFileName))
        && File.Exists(Path.Combine(installFolder, ExplorerMenuPackage.PackageFileName));

    /// <summary>State of the top-level menu entry for the Filee in <paramref name="installFolder"/>.</summary>
    public static ModernContextMenuState GetState(string installFolder)
    {
        if (!IsSupportedOs || !HasFiles(installFolder))
            return ModernContextMenuState.Unsupported;
        var package = Find();
        if (package is null)
            return ModernContextMenuState.Off;
        var sameFolder = package.ExternalLocation is null || SamePath(package.ExternalLocation, installFolder);
        return sameFolder && package.Version >= ExplorerMenuPackage.ManifestVersion
            ? ModernContextMenuState.On
            : ModernContextMenuState.Outdated;
    }

    /// <summary>The package as registered for the current user, or null.</summary>
    public static unsafe RegisteredPackage? Find()
    {
        uint count = 0, length = 0;
        var rc = NativeMethods.GetPackagesByPackageFamily(ExplorerMenuPackage.FamilyName, ref count, null, ref length, null);
        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER || count == 0)
            return null;

        var names = new nint[count];
        var buffer = new char[length];
        string fullName;
        fixed (nint* pNames = names)
        fixed (char* pBuffer = buffer)
        {
            if (NativeMethods.GetPackagesByPackageFamily(ExplorerMenuPackage.FamilyName, ref count, (char**)pNames, ref length, pBuffer) != 0
                || count == 0)
                return null;
            fullName = new string((char*)pNames[0]);
        }

        // Full name = Name_Version_Architecture_ResourceId_PublisherId.
        var parts = fullName.Split('_');
        var version = parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
        return new RegisteredPackage(fullName, version, ExternalLocationOf(fullName));
    }

    /// <summary>
    /// Shows the entry with this title (the UI language), or hides it (<c>null</c>). The DLL reads the file on every
    /// right-click, so this takes effect immediately and needs no administrator rights.
    /// </summary>
    public static void SetTitle(string? title)
    {
        var path = TitleFilePath;
        if (title is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        var content = title.ReplaceLineEndings(" ").Trim() + "\r\n";
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>The current title, or null when the entry is switched off.</summary>
    public static string? ReadTitle()
    {
        try
        {
            var path = TitleFilePath;
            return File.Exists(path) ? File.ReadLines(path, Encoding.UTF8).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Registers the package that ships next to Filee.exe, with the install folder as external location.</summary>
    public static async Task<ModernContextMenuResult> AddAsync(string installFolder)
    {
        if (!IsSupportedOs)
            return new ModernContextMenuResult(false, Error: "The top-level Explorer menu needs Windows 11 (x64).");
        if (!HasFiles(installFolder))
            return new ModernContextMenuResult(false, Error: $"{ExplorerMenuPackage.PackageFileName} or {ExplorerMenuPackage.DllFileName} is missing in {installFolder}.");

        var package = Path.Combine(installFolder, ExplorerMenuPackage.PackageFileName);
        var result = await RunPowerShellAsync(errorFile => BuildAddScript(package, installFolder, errorFile), elevated: true);
        if (result.Succeeded && GetState(installFolder) != ModernContextMenuState.On)
        {
            return new ModernContextMenuResult(false, Error:
                "Windows accepted the package, but it is not registered for this user account (was another administrator account used?).");
        }
        return result;
    }

    /// <summary>
    /// Removes the package for the current user. Normally no administrator rights are needed; with
    /// <paramref name="allowElevation"/> a failed attempt is repeated through a UAC prompt.
    /// </summary>
    public static async Task<ModernContextMenuResult> RemoveAsync(bool allowElevation)
    {
        if (Find() is null)
            return new ModernContextMenuResult(true);
        var result = await RunPowerShellAsync(BuildRemoveScript, elevated: false);
        if (!result.Succeeded && allowElevation && Find() is not null)
            result = await RunPowerShellAsync(BuildRemoveScript, elevated: true);
        return result.Succeeded && Find() is not null
            ? new ModernContextMenuResult(false, Error: "The package is still registered.")
            : result;
    }

    /// <summary>PowerShell that registers the package and writes the error message to <paramref name="errorFile"/>.</summary>
    public static string BuildAddScript(string packagePath, string externalLocation, string errorFile) =>
        WrapScript(
            $"Add-AppxPackage -Path {Quote(packagePath)} -ExternalLocation {Quote(externalLocation)} -AllowUnsigned -ForceUpdateFromAnyVersion",
            errorFile);

    /// <summary>PowerShell that removes every registered version of the package.</summary>
    public static string BuildRemoveScript(string errorFile) =>
        WrapScript($"Get-AppxPackage -Name {Quote(ExplorerMenuPackage.PackageName)} | Remove-AppxPackage", errorFile);

    /// <summary>
    /// Start info for Windows PowerShell (part of Windows; the Appx cmdlets are not in PowerShell 7 everywhere).
    /// The script goes in as plain <c>-Command</c> text rather than <c>-EncodedCommand</c>, which security software
    /// tends to distrust; it only contains single-quoted strings, so wrapping it in double quotes is safe.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string script, bool elevated)
    {
        if (script.Contains('"'))
            throw new ArgumentException("The script must not contain double quotes.", nameof(script));
        var info = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            $"-NoLogo -NoProfile -NonInteractive -Command \"{script}\"");
        if (elevated)
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
        }
        return info;
    }

    /// <summary>
    /// A PowerShell single-quoted string literal. PowerShell also treats the typographic quotes ‘ ’ ‚ ‛ as single
    /// quotes (a folder may be called "Kim’s PC"), so those are doubled as well.
    /// </summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                builder.Append(c);
        }
        return builder.Append('\'').ToString();
    }

    /// <summary>Family name computed by Windows from the package name and publisher (to check <see cref="ExplorerMenuPackage.FamilyName"/>).</summary>
    public static unsafe string ComputeFamilyName()
    {
        fixed (char* name = ExplorerMenuPackage.PackageName)
        fixed (char* publisher = ExplorerMenuPackage.Publisher)
        {
            var id = new NativeMethods.PACKAGE_ID { Name = name, Publisher = publisher };
            uint length = 0;
            var rc = NativeMethods.PackageFamilyNameFromId(&id, ref length, null);
            if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                throw new Win32Exception(rc);
            var buffer = new char[length];
            fixed (char* pBuffer = buffer)
            {
                rc = NativeMethods.PackageFamilyNameFromId(&id, ref length, pBuffer);
                if (rc != 0)
                    throw new Win32Exception(rc);
                return new string(pBuffer);
            }
        }
    }

    private static string WrapScript(string command, string errorFile) =>
        $"$ErrorActionPreference = 'Stop'; try {{ {command}; exit 0 }} " +
        $"catch {{ [IO.File]::WriteAllText({Quote(errorFile)}, $_.Exception.Message); exit 1 }}";

    private static async Task<ModernContextMenuResult> RunPowerShellAsync(Func<string, string> buildScript, bool elevated)
    {
        var errorFile = Path.Combine(Path.GetTempPath(), $"Filee-explorer-menu-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(CreateStartInfo(buildScript(errorFile), elevated));
            if (process is null)
                return new ModernContextMenuResult(false, Error: "PowerShell did not start.");
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!elevated)
                    process.Kill();
                return new ModernContextMenuResult(false, Error: "Windows did not finish within " + Timeout.TotalMinutes + " minutes.");
            }
            if (process.ExitCode == 0)
                return new ModernContextMenuResult(true);
            var message = File.Exists(errorFile) ? ShortenDeploymentError(File.ReadAllText(errorFile)) : "";
            return new ModernContextMenuResult(false, Error: message.Length > 0 ? message : $"PowerShell exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ModernContextMenuResult(false, Cancelled: true);
        }
        catch (Win32Exception ex)
        {
            return new ModernContextMenuResult(false, Error: ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(errorFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Deployment errors end with "NOTE: For additional information, look for [ActivityId] ..." and span several
    /// lines; the UI shows one paragraph without that tail.
    /// </summary>
    public static string ShortenDeploymentError(string message)
    {
        var note = message.IndexOf("NOTE: For additional information", StringComparison.Ordinal);
        if (note >= 0)
            message = message[..note];
        return string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>The install folder the package points to, or null when Windows can't tell.</summary>
    public static unsafe string? ExternalLocationOf(string fullName)
    {
        try
        {
            return ExternalLocationOfCore(fullName);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // older Windows without the API: the folder is then not compared
        }
    }

    private static unsafe string? ExternalLocationOfCore(string fullName)
    {
        uint length = 0;
        if (NativeMethods.GetPackagePathByFullName2(fullName, NativeMethods.PackagePathType_EffectiveExternal, ref length, null)
            != NativeMethods.ERROR_INSUFFICIENT_BUFFER || length == 0)
            return null;
        var buffer = new char[length];
        fixed (char* pBuffer = buffer)
        {
            return NativeMethods.GetPackagePathByFullName2(fullName, NativeMethods.PackagePathType_EffectiveExternal, ref length, pBuffer) == 0
                ? new string(pBuffer)
                : null;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
