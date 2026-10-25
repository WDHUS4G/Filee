// Windows shortcuts (.lnk): the file or folder one points to, so a dropped shortcut converts its target (issue #159).
//
// The shell's own ShellLink object reads the link, the way Explorer does. Should it be unavailable or fail, the target
// is read from the file itself ([MS-SHLLINK] LinkInfo: the local base path, or the share name, and the path suffix).
// Neither searches for a target that has moved (IShellLink.Resolve would, and can show UI or take seconds).

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ShellLinks
{
    private const int MaxPath = 32768;
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    /// <summary>True for a path ending in ".lnk".</summary>
    public static bool IsShortcut(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The existing file or folder the shortcut at <paramref name="path"/> points to; null when it isn't a shortcut,
    /// points to something that isn't a file (Control Panel, an installer-advertised app) or to nothing any more.
    /// </summary>
    public static string? TargetOf(string path)
    {
        if (!IsShortcut(path) || !File.Exists(path))
            return null;
        var target = ReadWithShell(path) ?? ReadFromFile(path);
        try
        {
            return target is not null && (File.Exists(target) || Directory.Exists(target)) ? Path.GetFullPath(target) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Writes a shortcut to <paramref name="target"/> (the tests make their shortcuts with it).</summary>
    internal static void Create(string path, string target)
    {
        var link = NewShellLink();
        try
        {
            ((IShellLinkW)link).SetPath(target);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>The target as the shell reads it; null when it has no file system path or the shell failed.</summary>
    internal static string? ReadWithShell(string path)
    {
        object? link = null;
        try
        {
            link = NewShellLink();
            ((IPersistFile)link).Load(path, 0); // STGM_READ
            var buffer = new StringBuilder(MaxPath);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, 0, 0);
            return buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException
                                       or IOException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
                Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>The target as the .lnk file stores it in its LinkInfo; null when unreadable or not stored.</summary>
    internal static string? ReadFromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // A shortcut is a few KB; anything far bigger isn't one.
            return info.Length is > 0 and < 1 << 20 ? ParseTarget(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>[MS-SHLLINK]: the path in LinkInfo (local base path or share name, then the common path suffix).</summary>
    internal static string? ParseTarget(ReadOnlySpan<byte> data)
    {
        const int HeaderSize = 0x4C;
        const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, ForceNoLinkInfo = 0x100;
        const uint VolumeIdAndLocalBasePath = 0x1, CommonNetworkRelativeLinkAndPathSuffix = 0x2;

        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize
            || new Guid(data.Slice(4, 16)) != CLSID_ShellLink)
            return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if ((flags & HasLinkInfo) == 0 || (flags & ForceNoLinkInfo) != 0)
            return null;

        var offset = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (offset + 2 > data.Length)
                return null;
            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        }
        if (offset + 0x1C > data.Length)
            return null;
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]), (uint)(data.Length - offset));
        var linkInfo = data.Slice(offset, size);
        if (linkInfo.Length < 0x1C)
            return null;

        var headerLength = Field(linkInfo, 4);
        var linkInfoFlags = Field(linkInfo, 8);
        var unicode = headerLength >= 0x24;
        // Unicode copies follow a longer header; Windows writes them when the path doesn't fit the ANSI code page.
        var suffix = (unicode ? Unicode(linkInfo, Field(linkInfo, 0x20)) : null) ?? Ansi(linkInfo, Field(linkInfo, 0x18));

        if ((linkInfoFlags & VolumeIdAndLocalBasePath) != 0)
        {
            var basePath = (unicode ? Unicode(linkInfo, Field(linkInfo, 0x1C)) : null) ?? Ansi(linkInfo, Field(linkInfo, 0x10));
            return string.IsNullOrEmpty(basePath) ? null : basePath + suffix;
        }
        if ((linkInfoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0)
        {
            var at = (int)Field(linkInfo, 0x14);
            if (at <= 0 || at + 0x14 > linkInfo.Length)
                return null;
            var network = linkInfo[at..];
            var netNameOffset = BinaryPrimitives.ReadUInt32LittleEndian(network[8..]);
            var netName = netNameOffset > 0x14 && network.Length >= 0x1C
                ? Unicode(network, BinaryPrimitives.ReadUInt32LittleEndian(network[0x14..]))
                : Ansi(network, netNameOffset);
            return string.IsNullOrEmpty(netName) ? null
                : string.IsNullOrEmpty(suffix) ? netName : netName.TrimEnd('\\') + "\\" + suffix;
        }
        return null;
    }

    /// <summary>A little-endian 32-bit field; 0 past the end.</summary>
    private static uint Field(ReadOnlySpan<byte> data, int at) =>
        at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : 0;

    /// <summary>A NUL-terminated string in the system's ANSI code page, as Windows writes it.</summary>
    private static unsafe string? Ansi(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
            return null;
        var text = data[(int)offset..];
        var end = text.IndexOf((byte)0);
        if (end < 0)
            return null;
        fixed (byte* start = text)
            return Marshal.PtrToStringAnsi((nint)start, end); // CP_ACP on Windows
    }

    /// <summary>A NUL-terminated UTF-16 string.</summary>
    private static string? Unicode(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
            return null;
        var text = MemoryMarshal.Cast<byte, char>(data[(int)offset..][..((data.Length - (int)offset) & ~1)]);
        var end = text.IndexOf('\0');
        return end < 0 ? null : new string(text[..end]);
    }

    private static object NewShellLink() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellLink, throwOnError: true)!)!;

    // Only the methods Filee calls are typed; the slots between them keep the vtable order and are never called.

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, nint findData, uint flags);
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription();
        void GetWorkingDirectory();
        void SetWorkingDirectory();
        void GetArguments();
        void SetArguments();
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation();
        void SetRelativePath();
        void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
