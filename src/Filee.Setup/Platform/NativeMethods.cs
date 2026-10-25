// Win32 functions Setup calls (source-generated P/Invoke, trim- and AOT-safe).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Setup.Platform;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int TokenElevation = 20;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint MB_OK = 0x0;
    public const uint MB_YESNO = 0x4;
    public const uint MB_ICONERROR = 0x10;
    public const uint MB_ICONQUESTION = 0x20;
    public const uint MB_ICONWARNING = 0x30;
    public const uint MB_ICONINFORMATION = 0x40;
    public const uint MB_SETFOREGROUND = 0x10000;
    public const int IDYES = 6;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string existing, string? replacement, uint flags);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    /// <summary>No system error boxes (a missing drive, a program that can't start): Setup reports problems itself.</summary>
    public static void QuietErrors() => SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessWithToken(nint token, uint logonFlags, string? application, char* commandLine,
        uint creationFlags, nint environment, string? currentDirectory, StartupInfo* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint owner, string text, string caption, uint type);

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserDefaultLocaleName", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetUserDefaultLocaleName(char* name, int length);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("kernel32.dll", EntryPoint = "LCIDToLocaleName")]
    private static unsafe partial int LCIDToLocaleName(uint locale, char* name, int length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    /// <summary>The full path of a process's program, or null when it can't be read.</summary>
    public static unsafe string? ImagePathOf(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
            return null;
        try
        {
            var buffer = stackalloc char[4096];
            uint size = 4096;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>True when this process has administrator rights (an elevated token).</summary>
    public static unsafe bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token))
            return false;
        try
        {
            uint elevated = 0;
            return GetTokenInformation(token, TokenElevation, &elevated, sizeof(uint), out _) && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Starts a program with Explorer's rights: with the token of the desktop's process (the signed-in user, not
    /// elevated). For an elevated Setup that nobody unelevated started (run "as administrator", a package manager).
    /// </summary>
    public static unsafe bool StartAsShellUser(string executable, string arguments, string workingFolder, out string? error)
    {
        error = null;
        var desktop = GetShellWindow();
        if (desktop == 0 || GetWindowThreadProcessId(desktop, out var shellId) == 0 || shellId == 0)
        {
            error = "There is no Explorer desktop.";
            return false;
        }
        var shell = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellId);
        if (shell == 0)
        {
            error = $"OpenProcess: {Marshal.GetLastPInvokeError()}";
            return false;
        }
        nint shellToken = 0, token = 0;
        try
        {
            if (!OpenProcessToken(shell, TOKEN_DUPLICATE, out shellToken)
                || !DuplicateTokenEx(shellToken, TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    0, SecurityImpersonation, TokenPrimary, out token))
            {
                error = $"Token: {Marshal.GetLastPInvokeError()}";
                return false;
            }
            var commandLine = (SetupOptions.Quote(executable) + " " + arguments).TrimEnd() + "\0";
            var startup = new StartupInfo { Size = sizeof(StartupInfo) };
            ProcessInformation info;
            fixed (char* line = commandLine)
            {
                if (!CreateProcessWithToken(token, 0, executable, line, 0, 0, workingFolder, &startup, &info))
                {
                    error = $"CreateProcessWithTokenW: {Marshal.GetLastPInvokeError()}";
                    return false;
                }
            }
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
            return true;
        }
        finally
        {
            if (token != 0)
                CloseHandle(token);
            if (shellToken != 0)
                CloseHandle(shellToken);
            CloseHandle(shell);
        }
    }

    public static void BroadcastEnvironmentChange() =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "Environment", SMTO_ABORTIFHUNG, 5000, out _);

    /// <summary>The Windows display language, e.g. "ko-KR" (with InvariantGlobalization .NET can't tell).</summary>
    public static unsafe string UiLanguage()
    {
        var buffer = stackalloc char[85];
        var length = LCIDToLocaleName(GetUserDefaultUILanguage(), buffer, 85, 0);
        if (length > 1)
            return new string(buffer, 0, length - 1);
        length = GetUserDefaultLocaleName(buffer, 85);
        return length > 1 ? new string(buffer, 0, length - 1) : "en-US";
    }
}
