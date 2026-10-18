// Watches one folder (WatchRule) and converts files that arrive in it. Used by the tray app (Settings → Watch folders)
// and by "filee watch". A file is converted only once it is complete: it must keep the same size and time for
// SettleTime and open without sharing (on Windows; macOS and Linux don't lock files being written, so there only
// the longer wait and Finder's copy-in-progress date count), so downloads and copies still in progress are left alone. Temporary files,
// the output folder and the originals folder are never picked up, so converted files can't loop back in.

using System.Collections.Concurrent;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Watching;

/// <summary>Converts files that land in a watched folder.</summary>
public sealed class FolderWatcher : IAsyncDisposable
{
    /// <summary>Converts a batch of complete files and returns the finished job (null when it could not start).</summary>
    public delegate Task<ConversionJob?> ConvertFiles(IReadOnlyList<string> files, CancellationToken cancellationToken);

    /// <summary>
    /// Default time a file must stay unchanged before it is converted. Longer on macOS and Linux: there a file
    /// still being written can be opened exclusively all the same, so only the wait tells a paused copy from a
    /// finished one.
    /// </summary>
    public static readonly TimeSpan DefaultSettleTime = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 2 : 5);

    /// <summary>The creation date Finder gives a file while it is still copying it (24 January 1984).</summary>
    private static readonly DateTime FinderCopyInProgress = new(1984, 1, 24);

    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut", ".filepart",
    };

    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db" };

    private readonly ConvertFiles _convert;
    private readonly ILogger _log;
    private readonly TimeSpan _settleTime;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Candidate> _pending = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _handled = new(FileSystemPaths.Comparer);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _timer;

    /// <param name="rule">The folder and its options; the watcher keeps its own copy.</param>
    /// <param name="convert">Runs the conversion (the app's queue or the command line's).</param>
    /// <param name="settleTime">How long a file must stay unchanged; <see cref="DefaultSettleTime"/> if null.</param>
    /// <param name="time">Clock (tests use a fake one).</param>
    public FolderWatcher(WatchRule rule, ConvertFiles convert, ILogger? log = null, TimeSpan? settleTime = null, TimeProvider? time = null)
    {
        Rule = rule.Clone();
        _convert = convert;
        _log = log ?? NullLogger.Instance;
        _settleTime = settleTime ?? DefaultSettleTime;
        _time = time ?? TimeProvider.System;
    }

    public WatchRule Rule { get; }

    /// <summary>Why the folder is not being watched (it does not exist, access denied), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised after each converted batch (on a background thread).</summary>
    public event EventHandler<ConversionJob>? Converted;

    /// <summary>
    /// Starts watching. With <see cref="AfterConversion.MoveToOriginals"/> the files already in the folder are
    /// converted too (they are what arrived while nobody was watching); with <see cref="AfterConversion.Keep"/>
    /// only new files are, because the old ones were converted before. Returns false when the folder can't be watched.
    /// </summary>
    public bool Start()
    {
        if (!Directory.Exists(Rule.Folder))
        {
            Error = $"Folder not found: {Rule.Folder}";
            return false;
        }
        try
        {
            _watcher = new FileSystemWatcher(Rule.Folder)
            {
                IncludeSubdirectories = Rule.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Notice(e.FullPath);
            _watcher.Changed += (_, e) => Notice(e.FullPath);
            _watcher.Renamed += (_, e) => Notice(e.FullPath);
            _watcher.Error += (_, e) =>
            {
                // The change buffer overflowed (thousands of files at once): look at the folder itself.
                _log.LogWarning(e.GetException(), "Watching {Folder} missed changes; rescanning", Rule.Folder);
                ScanExisting();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = ex.Message;
            return false;
        }

        if (Rule.Originals == AfterConversion.MoveToOriginals)
            ScanExisting();
        _timer = _time.CreateTimer(_ => _ = ProcessPendingAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Error = null;
        return true;
    }

    /// <summary>Registers a file that appeared or changed; it is converted once it has settled.</summary>
    public void Notice(string path)
    {
        if (ShouldIgnore(path, Rule))
            return;
        var now = _time.GetUtcNow();
        _pending.AddOrUpdate(path, _ => Candidate.Of(path, now), (_, old) => old.Refresh(path, now));
    }

    /// <summary>
    /// Converts the pending files that are complete. Called every second by the timer; tests call it directly.
    /// Returns how many files were handed to the conversion.
    /// </summary>
    public async Task<int> ProcessPendingAsync()
    {
        if (!await _busy.WaitAsync(0))
            return 0; // the previous batch is still converting; new files wait for the next tick
        try
        {
            var ready = new List<string>();
            var now = _time.GetUtcNow();
            foreach (var (path, candidate) in _pending)
            {
                var current = candidate.Refresh(path, now);
                if (!current.Exists)
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }
                _pending[path] = current;
                if (now - current.StableSince < _settleTime || !CanOpenExclusively(path) || IsBeingCopiedByFinder(path))
                    continue;
                _pending.TryRemove(path, out _);
                // A file that was converted before (same size and time) is not converted again.
                if (_handled.Add(current.Key(path)))
                    ready.Add(path);
            }
            if (ready.Count == 0 || _stop.IsCancellationRequested)
                return 0;

            ready.Sort(FileSystemPaths.Comparer);
            _log.LogInformation("Watch folder {Folder}: converting {Count} file(s)", Rule.Folder, ready.Count);
            var job = await _convert(ready, _stop.Token);
            if (job is not null)
            {
                if (Rule.Originals == AfterConversion.MoveToOriginals)
                    foreach (var file in job.Files.Where(f => f.State == FileState.Done))
                        MoveToOriginals(Rule, file.SourcePath, _log);
                Converted?.Invoke(this, job);
            }
            return ready.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Watch folder {Folder} failed", Rule.Folder);
            return 0;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>True for files a watch folder never converts: temporary, hidden or system files, unknown formats,
    /// anything inside the output or originals folder, and names the rule's <see cref="WatchRule.Include"/> leaves out.</summary>
    public static bool ShouldIgnore(string path, WatchRule rule)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.')
            || SystemFiles.Contains(name) || TemporaryExtensions.Contains(Path.GetExtension(name)))
            return true;
        if (IsInside(path, rule.ResolvedOutputFolder) || IsInside(path, rule.OriginalsFolder))
            return true;
        if (!rule.IncludeSubfolders && !SameFolder(Path.GetDirectoryName(path), rule.Folder))
            return true;
        if (FormatRegistry.Detect(path) is null)
            return true;
        if (!FileNameFilter.Of(rule.Include).Matches(name))
            return true;
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // gone already, or not ours to read
        }
    }

    private void ScanExisting()
    {
        try
        {
            var option = Rule.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(Rule.Folder, "*", option))
                Notice(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list {Folder}", Rule.Folder);
        }
    }

    /// <summary>
    /// Moves a converted source into the rule's originals folder (keeping its subfolder), numbering it when the name is
    /// taken. A file outside the watched folder stays where it is.
    /// </summary>
    public static void MoveToOriginals(WatchRule rule, string source, ILogger log)
    {
        if (!IsInside(source, rule.Folder) || IsInside(source, rule.OriginalsFolder))
            return;
        try
        {
            var target = Path.Combine(rule.OriginalsFolder, Path.GetRelativePath(rule.Folder, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var name = Path.GetFileNameWithoutExtension(target);
            var extension = Path.GetExtension(target);
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(Path.GetDirectoryName(target)!, $"{name} ({n}){extension}");
            File.Move(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not move {File} to the originals folder", source);
        }
    }

    /// <summary>A writer still holding the file (download, copy) makes an exclusive open fail.</summary>
    private static bool IsBeingCopiedByFinder(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        try
        {
            return File.GetCreationTime(path).Date == FinderCopyInProgress;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, FileSystemPaths.Comparison);
    }

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), FileSystemPaths.Comparison);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _watcher?.Dispose();
        if (_timer is not null)
            await _timer.DisposeAsync();
        // Let a running batch finish moving its originals.
        await _busy.WaitAsync();
        _busy.Release();
    }

    /// <summary>Size and time of a pending file, and since when they have not changed.</summary>
    private readonly record struct Candidate(bool Exists, long Length, DateTime LastWriteUtc, DateTimeOffset StableSince)
    {
        public static Candidate Of(string path, DateTimeOffset now)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new Candidate(true, info.Length, info.LastWriteTimeUtc, now)
                : new Candidate(false, 0, default, now);
        }

        public Candidate Refresh(string path, DateTimeOffset now)
        {
            var fresh = Of(path, now);
            return fresh.Exists && fresh.Length == Length && fresh.LastWriteUtc == LastWriteUtc ? this : fresh;
        }

        public string Key(string path) => $"{path}|{Length}|{LastWriteUtc.Ticks}";
    }
}
