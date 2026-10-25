// Folders the shell knows by id: they can be moved (Downloads on another drive), so their path is asked, not assumed.

using System.Runtime.InteropServices;

namespace Filee.Platform.Windows;

internal static partial class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder, or null when the shell can't tell.</summary>
    public static string? Downloads => Path(DownloadsId);

    private static string? Path(Guid id)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var result = SHGetKnownFolderPath(id, 0, 0, out var pointer);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);
}
