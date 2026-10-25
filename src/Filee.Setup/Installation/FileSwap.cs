// Puts the unpacked files into the install folder as one step that can be undone. The new files were unpacked and
// checked in a work folder inside the install folder (same volume, so every move is a rename). Each file that is
// replaced or no longer needed is first renamed into the work folder's backup: Windows lets a program in use, or a DLL
// Explorer has loaded (FileeExplorerMenu.dll), be renamed, just not overwritten or deleted. On failure everything is
// moved back; on success the backup is deleted, and what is still in use goes at the next restart.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public sealed class FileSwap(string installFolder, string workFolder, ISetupSystem system, SetupLog log)
{
    /// <summary>Prefix of the work folders inside the install folder. Left-overs of an interrupted Setup are removed.</summary>
    public const string WorkFolderPrefix = "~setup-";

    private readonly List<(string Target, string? Backup)> _done = [];
    private readonly string _backup = Path.Combine(workFolder, "old");
    private int _backups;

    /// <summary>Where the new files are unpacked.</summary>
    public string Staging { get; } = Path.Combine(workFolder, "new");

    /// <summary>Moves the staged file <paramref name="relative"/> into place, keeping the file it replaces.</summary>
    public void Replace(string relative)
    {
        var target = Path.Combine(installFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var backup = File.Exists(target) ? MoveToBackup(target) : null;
        _done.Add((target, backup));
        File.Move(Path.Combine(Staging, relative), target);
    }

    /// <summary>Moves an old file out of the install folder (deleted on <see cref="Commit"/>).</summary>
    public void Remove(string relative)
    {
        var target = Path.Combine(installFolder, relative);
        if (!File.Exists(target))
            return;
        _done.Add((target, MoveToBackup(target)));
    }

    private string MoveToBackup(string target)
    {
        // A flat, numbered backup: no folder structure to recreate, and names can't collide.
        Directory.CreateDirectory(_backup);
        var backup = Path.Combine(_backup, $"{_backups++:D5}-{Path.GetFileName(target)}");
        File.SetAttributes(target, FileAttributes.Normal);
        File.Move(target, backup);
        return backup;
    }

    /// <summary>Puts every file back as it was. Best effort: a file that can't be moved back is logged.</summary>
    public void Rollback()
    {
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var (target, backup) = _done[i];
            try
            {
                if (backup is null)
                {
                    // A new file where there was none: take it away again (into the backup if it is in use).
                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Delete(target);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            MoveToBackup(target);
                        }
                    }
                    continue;
                }
                if (File.Exists(target))
                    File.Move(target, Path.Combine(_backup, $"{_backups++:D5}-new-{Path.GetFileName(target)}"));
                File.Move(backup, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Rollback: could not restore {target}: {ex.Message}");
            }
        }
        _done.Clear();
        RemoveEmptyFolders(installFolder, keep: workFolder);
        Cleanup();
    }

    /// <summary>Deletes the replaced files and the work folder.</summary>
    public void Commit()
    {
        _done.Clear();
        RemoveEmptyFolders(installFolder, keep: workFolder);
        Cleanup();
    }

    /// <summary>Deletes the work folder; what is still in use goes at the next restart.</summary>
    public void Cleanup() => DeleteTree(workFolder, system, log);

    /// <summary>Deletes a folder tree; files still in use are deleted at the next restart.</summary>
    public static void DeleteTree(string folder, ISetupSystem system, SetupLog log)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Info($"In use, deleted at the next restart: {file}");
                system.DeleteAtRestart(file);
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).Append(folder))
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                system.DeleteAtRestart(dir);
            }
        }
    }

    /// <summary>Removes empty folders below <paramref name="root"/> (not the root itself).</summary>
    public static void RemoveEmptyFolders(string root, string? keep = null)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (keep is not null && (dir.Equals(keep, StringComparison.OrdinalIgnoreCase)
                                     || dir.StartsWith(keep + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
