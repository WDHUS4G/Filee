// Remembers generated results when a watch rule writes back into its input tree. Without this, restarting
// an inbox watcher would convert yesterday's outputs again. Entries identify a file version, not just a name:
// replacing an output with a new incoming file makes it eligible again.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Filee.Core.Platform;

namespace Filee.Core.Watching;

internal sealed class GeneratedWatchOutputs(WatchRule rule)
{
    internal sealed record Entry(string Path, long Length, long LastWriteTicks, bool IsDirectory = false);

    private readonly Dictionary<string, Entry> _entries = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _directories = new(FileSystemPaths.Comparer);
    private string StateFile => System.IO.Path.Combine(rule.Folder, ".filee-watch-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rule.Id)))[..16].ToLowerInvariant() + ".json");

    public void Load()
    {
        if (!File.Exists(StateFile)) return;
        var entries = JsonSerializer.Deserialize<List<Entry?>>(File.ReadAllText(StateFile)) ?? [];
        foreach (var entry in entries)
        {
            if (entry is null || !IsCurrent(entry)) continue;
            _entries[entry.Path] = entry;
            if (entry.IsDirectory) _directories.Add(entry.Path);
        }
    }

    public bool Contains(string path)
    {
        if (_entries.TryGetValue(path, out var entry) && IsCurrent(entry)) return true;
        return _directories.Any(directory => IsInside(path, directory) && Directory.Exists(directory));
    }

    public bool Remember(string path)
    {
        if (!IsInside(path, rule.Folder)) return false;
        if (Directory.Exists(path))
        {
            _entries[path] = new(path, 0, 0, true);
            _directories.Add(path);
        }
        else
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            _entries[path] = new(path, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        return true;
    }

    public void Save()
    {
        foreach (var stale in _entries.Values.Where(e => !IsCurrent(e)).ToArray())
        {
            _entries.Remove(stale.Path);
            _directories.Remove(stale.Path);
        }
        var temporary = StateFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries.Values));
            File.Move(temporary, StateFile, overwrite: true);
            if (OperatingSystem.IsWindows())
                File.SetAttributes(StateFile, File.GetAttributes(StateFile) | FileAttributes.Hidden);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private bool IsCurrent(Entry entry)
    {
        // A copied or edited state file cannot exclude the root itself or files outside this rule.
        if (string.IsNullOrWhiteSpace(entry.Path) || !IsInside(entry.Path, rule.Folder)) return false;
        if (entry.IsDirectory) return Directory.Exists(entry.Path);
        var info = new FileInfo(entry.Path);
        return info.Exists && info.Length == entry.Length && info.LastWriteTimeUtc.Ticks == entry.LastWriteTicks;
    }

    private static bool IsInside(string path, string folder) => System.IO.Path.GetFullPath(path).StartsWith(
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar,
        FileSystemPaths.Comparison);
}
