// Integrity levels of Filee and Explorer, and starting a program with Explorer's rights instead of Filee's.
//
// Filee runs as administrator when something elevated started it: an installer the user ran "as administrator" starts
// Filee with its own token at the end, and the in-app updater inherits that. Explorer then can't drop files on Filee
// or hand files to it (see ElevationState). Starting Filee again "as the user" goes through Explorer itself: the
// desktop's shell view gives out its Shell.Application object, and ShellExecute on that object runs inside Explorer,
// so the new process gets Explorer's token. This is the documented way back down (no API lowers a running process).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Platform;

namespace Filee.Platform.Windows;

/// <summary>Elevation checks and the de-elevated restart. Every member is safe to call on any OS.</summary>
public static class Elevation
{
    /// <summary>Reads the integrity levels of this process and of Explorer. <see cref="ElevationState.Unknown"/> off Windows.</summary>
    public static ElevationState Probe()
    {
        if (!OperatingSystem.IsWindows())
            return ElevationState.Unknown;
        try
        {
            var process = LevelOfProcess(NativeMethods.GetCurrentProcess(), out var elevated);
            return new ElevationState(process, ShellLevel(), elevated);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ElevationState.Unknown;
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> through Explorer, so it gets Explorer's rights instead of this process's.
    /// Returns false when there is no Explorer desktop to ask (another shell, Explorer restarting) or it refused;
    /// <paramref name="error"/> then says why, in English.
    /// </summary>
    public static bool RelaunchThroughShell(string executable, string arguments, out string? error)
    {
        if (OperatingSystem.IsWindows())
            return RelaunchOnWindows(executable, arguments, out error);
        error = "Not supported on this operating system.";
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool RelaunchOnWindows(string executable, string arguments, out string? error)
    {
        // Explorer's COM objects want a single-threaded apartment; a thread of its own keeps the caller's apartment
        // out of it and bounds the wait if Explorer doesn't answer.
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                ShellExecuteFromDesktop(executable, arguments);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything Explorer or the COM binder throws: report it, never take Filee down from this thread.
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }
        })
        { IsBackground = true, Name = "Filee shell relaunch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            error = "Explorer did not answer.";
            return false;
        }
        error = failure;
        return failure is null;
    }

    [SupportedOSPlatform("windows")]
    private static void ShellExecuteFromDesktop(string executable, string arguments) =>
        DesktopShellView.Use(background =>
        {
            // IShellFolderViewDual.Application is Explorer's IShellDispatch2: its ShellExecute runs in Explorer.
            dynamic folderView = background;
            dynamic application = folderView.Application;
            try
            {
                application.ShellExecute(executable, arguments, Path.GetDirectoryName(executable) ?? "", "", SW_SHOWNORMAL);
            }
            finally
            {
                Marshal.FinalReleaseComObject((object)application);
            }
            return true;
        });

    /// <summary>Shows a plain Windows message box (for problems before Filee's own windows can open). No-op off Windows.</summary>
    public static void ShowWarning(string text)
    {
        if (OperatingSystem.IsWindows())
            NativeMethods.MessageBox(0, text, "Filee",
                NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST);
    }

    [SupportedOSPlatform("windows")]
    private static IntegrityLevel ShellLevel()
    {
        var desktop = NativeMethods.GetShellWindow();
        if (desktop == 0)
            return IntegrityLevel.Unknown;
        NativeMethods.GetWindowThreadProcessId(desktop, out var pid);
        if (pid == 0)
            return IntegrityLevel.Unknown;
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return IntegrityLevel.Unknown;
        try
        {
            return LevelOfProcess(process, out _);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe IntegrityLevel LevelOfProcess(nint process, out bool elevated)
    {
        elevated = false;
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
            return IntegrityLevel.Unknown;
        try
        {
            uint isElevated = 0;
            if (NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, &isElevated, sizeof(uint), out _))
                elevated = isElevated != 0;

            // TOKEN_MANDATORY_LABEL: a SID_AND_ATTRIBUTES whose SID points into the same buffer.
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, null, 0, out var length);
            if (length == 0)
                return IntegrityLevel.Unknown;
            var buffer = stackalloc byte[(int)length];
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                return IntegrityLevel.Unknown;
            var sid = *(nint*)buffer;
            var count = *(byte*)NativeMethods.GetSidSubAuthorityCount(sid);
            if (count == 0)
                return IntegrityLevel.Unknown;
            var rid = *(uint*)NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
            return ElevationState.FromRid(rid);
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private const int SW_SHOWNORMAL = 1;
}
