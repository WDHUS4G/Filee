// The freedesktop.org Trash (Nautilus, Dolphin, Thunar, Nemo and `gio trash` all use it): the file moves into
// Trash/files and a Trash/info/<name>.trashinfo records where it came from, so the file manager can restore it
// (OutputRule.ReplaceOriginal, #332). Only the home Trash is used, and only for files on the same filesystem; others
// are left where they are.

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Filee.Core.Platform;

namespace Filee.Platform.Linux;

[UnsupportedOSPlatform("windows")]
internal static class FreedesktopTrash
{
    /// <summary>The spec asks for Trash folders only their owner can open.</summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Null when <paramref name="path"/> is in <paramref name="trash"/>, otherwise why it was left where it is.</summary>
    public static TrashRefusal? Move(string path, string trash)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return new TrashRefusal("trash.failed");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        try
        {
            foreach (var folder in new[] { trash, files, info })
                Directory.CreateDirectory(folder, OwnerOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TrashRefusal("trash.no_folder", ex.Message);
        }
        if (MountOf(full) != MountOf(files))
            return new TrashRefusal("trash.other_drive");

        // Free in both: the Trash holds folders too.
        var name = Path.GetFileName(full);
        for (var n = 2; Path.Exists(Path.Combine(files, name)) || Path.Exists(Path.Combine(info, name + ".trashinfo")); n++)
            name = $"{Path.GetFileNameWithoutExtension(full)} {n}{Path.GetExtension(full)}";
        var record = Path.Combine(info, name + ".trashinfo");
        var created = false;
        try
        {
            // The record first, as the spec asks: a crash in between leaves no file in the Trash without one.
            using (var stream = new FileStream(record, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write($"[Trash Info]\nPath={Escape(full)}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
            }
            File.Move(full, Path.Combine(files, name));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a record this call wrote: another program may have just made one of the same name.
            if (created)
            {
                try { File.Delete(record); } catch (IOException) { }
            }
            return File.Exists(full) ? new TrashRefusal("trash.failed", ex.Message) : null;
        }
    }

    /// <summary>The path in the record: each part URL-encoded, the slashes kept.</summary>
    internal static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>The mount point a path is on: the longest mounted root that holds it (<c>/home</c> doesn't hold <c>/home2</c>).</summary>
    internal static string MountOf(string path, IEnumerable<string>? roots = null)
    {
        try
        {
            return (roots ?? DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName))
                .Where(r => Holds(r, path))
                .OrderByDescending(r => r.Length).FirstOrDefault() ?? "/";
        }
        catch (IOException)
        {
            return "/";
        }

        static bool Holds(string root, string path)
        {
            var folder = root.EndsWith('/') ? root : root + "/";
            return path.StartsWith(folder, StringComparison.Ordinal) || path == root;
        }
    }
}
