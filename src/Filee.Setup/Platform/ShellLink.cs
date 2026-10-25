// Writes .lnk shortcuts (Start menu, desktop) through the shell's ShellLink object, with source-generated COM
// interfaces: the trimmed Setup has no built-in COM interop.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Filee.Setup.Platform;

[SupportedOSPlatform("windows")]
internal static partial class ShellLink
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private const uint ClsctxInprocServer = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    /// <summary>Creates (or replaces) <paramref name="path"/>, a shortcut to <paramref name="target"/>.</summary>
    public static void Create(string path, string target, string workingFolder, string description)
    {
        // The shell objects want a single-threaded apartment: a thread of its own keeps the caller's apartment out of it.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CreateOnThisThread(path, target, workingFolder, description);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
        })
        { IsBackground = true, Name = "Filee Setup shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new IOException($"Could not create the shortcut {path}: {failure.Message}", failure);
    }

    private static void CreateOnThisThread(string path, string target, string workingFolder, string description)
    {
        var initialized = CoInitializeEx(0, 0x2 /* COINIT_APARTMENTTHREADED */) >= 0;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidShellLink, 0, ClsctxInprocServer, IidShellLinkW, out var pointer));
            var wrappers = new StrategyBasedComWrappers();
            try
            {
                var link = (IShellLinkW)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
                link.SetPath(target);
                link.SetWorkingDirectory(workingFolder);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                ((IPersistFile)link).Save(path, true);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        finally
        {
            if (initialized)
                CoUninitialize();
        }
    }

    // Only the methods Setup calls are typed; the others keep their vtable slot and are never called.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint file, int length, nint findData, uint flags);
        void GetIDList(nint idList);
        void SetIDList(nint idList);
        void GetDescription(nint name, int length);
        void SetDescription(string name);
        void GetWorkingDirectory(nint folder, int length);
        void SetWorkingDirectory(string folder);
        void GetArguments(nint arguments, int length);
        void SetArguments(string arguments);
        void GetHotkey(nint hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(nint showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(nint iconPath, int length, nint icon);
        void SetIconLocation(string iconPath, int icon);
        void SetRelativePath(string relativePath, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load(string fileName, uint mode);
        void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string fileName);
        void GetCurFile(out nint fileName);
    }
}
