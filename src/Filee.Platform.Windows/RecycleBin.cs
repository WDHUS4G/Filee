// Moves a file to the Recycle Bin (OutputRule.ReplaceOriginal, #332), and never deletes it for good. SHFileOperation
// and IFileOperation both delete a file outright where it can't be recycled (a subst or network drive, a drive whose
// Recycle Bin is off, a file larger than the bin holds), without failing. So this uses IFileOperation with a progress
// sink that refuses every delete Windows says it can't recycle (PreDeleteItem without TSF_DELETE_RECYCLE_IF_POSSIBLE):
// the file then stays where it is. IFileOperation needs an STA thread.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Platform;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    private const uint FOF_SILENT = 0x4;
    private const uint FOF_NOCONFIRMATION = 0x10;
    private const uint FOF_ALLOWUNDO = 0x40;
    private const uint FOF_NOERRORUI = 0x400;
    private const uint FOFX_RECYCLEONDELETE = 0x80000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>Null when <paramref name="path"/> is in the Recycle Bin, otherwise why it was left where it is.</summary>
    public static TrashRefusal? Move(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return new TrashRefusal("trash.failed");
        if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            return new TrashRefusal("trash.read_only");
        try
        {
            using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }
        catch (IOException)
        {
            return new TrashRefusal("trash.in_use");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TrashRefusal("trash.failed", ex.Message);
        }

        TrashRefusal? refusal = null;
        var thread = new Thread(() => refusal = Recycle(full)) { IsBackground = true, Name = "Filee Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return refusal ?? (File.Exists(full) ? new TrashRefusal("trash.failed") : null);
    }

    private static TrashRefusal? Recycle(string path)
    {
        var sink = new RecycleOnly();
        IFileOperation? operation = null;
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation, throwOnError: true)!)!;
            operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOFX_RECYCLEONDELETE);
            var item = SHCreateItemFromParsingName(path, 0, typeof(IShellItem).GUID);
            operation.DeleteItem(item, sink);
            var result = operation.PerformOperations();
            if (sink.Refused)
                return new TrashRefusal("trash.no_recycle_bin");
            return result == 0 && !operation.GetAnyOperationsAborted() ? null : new TrashRefusal("trash.failed", $"0x{result:X8}");
        }
        catch (COMException ex)
        {
            return sink.Refused ? new TrashRefusal("trash.no_recycle_bin") : new TrashRefusal("trash.failed", ex.Message);
        }
        finally
        {
            if (operation is not null)
                Marshal.ReleaseComObject(operation);
        }
    }

    /// <summary>Lets a delete go ahead only when Windows will recycle it.</summary>
    private sealed class RecycleOnly : IFileOperationProgressSink
    {
        public bool Refused { get; private set; }

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0)
                return 0;
            Refused = true;
            return ERROR_CANCELLED;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => 0;
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string? newName) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string? newName, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)]
    private static extern IShellItem SHCreateItemFromParsingName(string path, nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

    /// <summary>Passed through to IFileOperation only; its own methods are never called.</summary>
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
    }

    /// <summary>IFileOperation in vtable order; only the methods Filee calls are typed, the others are never called.</summary>
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems();
        void NewItem();

        [PreserveSig]
        int PerformOperations();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig]
        int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
