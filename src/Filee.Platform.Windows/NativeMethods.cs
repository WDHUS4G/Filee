// Win32 P/Invoke declarations used by the Windows platform layer. Keep this list minimal.

using System.Runtime.InteropServices;

namespace Filee.Platform.Windows;

internal static partial class NativeMethods
{
    public const int SM_CXDRAG = 68;
    public const uint GA_ROOT = 2;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    public const uint ASFW_ANY = uint.MaxValue;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    public const nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hwnd, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(nint process, nint minimumSize, nint maximumSize);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    // Package queries (appmodel.h, Windows 8+ / PackagePathType_EffectiveExternal: Windows 10 2004+).
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int PackagePathType_EffectiveExternal = 5;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct PACKAGE_ID
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        public char* Name;
        public char* Publisher;
        public char* ResourceId;
        public char* PublisherId;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, char** packageFullNames, ref uint bufferLength, char* buffer);

    // Not exported by kernel32.dll (only by kernelbase.dll): import it from its API set, Windows 10 1903 and later.
    [LibraryImport("api-ms-win-appmodel-runtime-l1-1-3.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetPackagePathByFullName2(string packageFullName, int packagePathType, ref uint pathLength, char* path);

    [LibraryImport("kernel32.dll")]
    public static unsafe partial int PackageFamilyNameFromId(PACKAGE_ID* packageId, ref uint packageFamilyNameLength, char* packageFamilyName);

    // Tokens and integrity levels (Elevation).
    public const uint TOKEN_QUERY = 0x0008;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returnLength);

    /// <summary>Pointer to the sub-authority count (a byte) inside a SID.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthorityCount(nint sid);

    /// <summary>Pointer to sub-authority <paramref name="index"/> (a DWORD) inside a SID.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthority(nint sid, uint index);

    /// <summary>The desktop window of the shell (Explorer's Progman), or 0 when no shell runs.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    public const uint MB_OK = 0x0, MB_ICONWARNING = 0x30, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint owner, string text, string caption, uint type);

    /// <summary>OPENASINFO for <see cref="SHOpenWithDialog"/>: the file (a UTF-16 string) and what the dialog may do.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OPENASINFO
    {
        public nint File;
        public nint Class;
        public uint Flags;
    }

    /// <summary>Lets the dialog offer "always use this app"; registers the choice; opens the file with the app picked.</summary>
    public const uint OAIF_ALLOW_REGISTRATION = 0x1, OAIF_REGISTER_EXT = 0x2, OAIF_EXEC = 0x4;

    /// <summary>The shell's "Open with" dialog. Blocks until it closes; returns an HRESULT (cancelled is an error).</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHOpenWithDialog(nint owner, in OPENASINFO info);

    /// <summary>Returns the Win32 class name of a window ("CabinetWClass" for Explorer, ...).</summary>
    public static string ClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
}
