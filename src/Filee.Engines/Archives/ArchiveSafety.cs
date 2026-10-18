// Guards for unpacking archives from anywhere: entries that would land outside the target folder ("zip slip"),
// symbolic links, encrypted content and archives that would not fit on the disk. Also the error messages for them.

using System.Globalization;

namespace Filee.Engines.Archives;

/// <summary>Checks and path helpers shared by the 7-Zip and the built-in archive readers.</summary>
internal static class ArchiveSafety
{
    /// <summary>More entries than this are refused: real archives stay far below, "entry bombs" do not.</summary>
    public const int MaxEntries = 1_000_000;

    /// <summary>Space that must stay free on the drive after unpacking.</summary>
    private const long Reserve = 64L * 1024 * 1024;

    private static readonly HashSet<char> InvalidNameChars =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True if an entry path is absolute, starts with a drive, or climbs out of the target with "..".</summary>
    public static bool IsUnsafePath(string entryPath)
    {
        if (entryPath.Length == 0)
            return false;
        if (entryPath[0] is '/' or '\\')
            return true;
        if (entryPath.Length >= 2 && entryPath[1] == ':' && char.IsAsciiLetter(entryPath[0]))
            return true;
        return entryPath.Split('/', '\\').Any(part => part == "..");
    }

    /// <summary>
    /// Refuses a 7-Zip listing with encrypted entries, entries or hard links pointing outside the target folder,
    /// too many entries, or more data than the drive of <paramref name="targetDirectory"/> can hold.
    /// </summary>
    public static void CheckListing(IReadOnlyList<SevenZipEntry> entries, string archiveName, string targetDirectory)
    {
        if (entries.Count > MaxEntries)
            throw new InvalidDataException($"{archiveName} has more than {MaxEntries:N0} entries; Filee does not unpack it.");
        foreach (var entry in entries)
        {
            if (entry.IsEncrypted)
                throw Encrypted(archiveName);
            if (IsUnsafePath(entry.Path) || entry.HardLink is { Length: > 0 } link && IsUnsafePath(link))
                throw Unsafe(archiveName, entry.Path);
        }
        EnsureRoom(entries.Where(e => !e.IsFolder).Sum(e => e.Size), targetDirectory, archiveName);
    }

    /// <summary>Throws when <paramref name="bytes"/> would not fit on the drive of <paramref name="directory"/>.</summary>
    public static void EnsureRoom(long bytes, string directory, string archiveName)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root) || bytes <= 0)
            return;
        long free;
        try
        {
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // network shares have no DriveInfo; the write itself fails if space runs out
        }
        if (bytes > free - Reserve)
            throw new IOException($"{archiveName} unpacks to {FormatSize(bytes)}, but only {FormatSize(free)} is free on {root}.");
    }

    /// <summary>
    /// Turns an entry name read by Filee's own readers into a path inside <paramref name="root"/>: separators are
    /// normalized, characters Windows does not allow are replaced, and anything escaping the root is refused.
    /// </summary>
    public static string ResolveEntryPath(string root, string entryName, string archiveName)
    {
        if (IsUnsafePath(entryName))
            throw Unsafe(archiveName, entryName);
        var parts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .Select(SanitizeSegment)
            .ToList();
        if (parts.Count == 0)
            throw Damaged(archiveName, "an entry has no name");

        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine([fullRoot, .. parts]));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Unsafe(archiveName, entryName);
        return full;
    }

    /// <summary>
    /// Deletes symbolic links and junctions below <paramref name="root"/> without following them, so nothing packed or
    /// moved later can reach outside the unpacked folder.
    /// </summary>
    public static void RemoveLinks(string root)
    {
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (info is DirectoryInfo linkedDir)
                    linkedDir.Delete(recursive: false); // removes the link, not its target
                else
                    info.Delete();
            }
            else if (info is DirectoryInfo dir)
            {
                RemoveLinks(dir.FullName);
            }
        }
    }

    public static Exception Encrypted(string archiveName) =>
        new InvalidDataException($"{archiveName} is password-protected. Filee cannot unpack encrypted archives.");

    public static Exception Unsafe(string archiveName, string entry) =>
        new InvalidDataException($"{archiveName} was not unpacked: the entry \"{entry}\" points outside the target folder.");

    public static Exception Damaged(string archiveName, string detail) =>
        new InvalidDataException($"{archiveName} is damaged or incomplete ({detail}).");

    /// <summary>"1.4 GB" style size for messages.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>One path segment made valid on Windows: invalid characters become "_", device names get a prefix.</summary>
    private static string SanitizeSegment(string segment)
    {
        var chars = segment.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0)
            return "_";
        var stem = name.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }
}
