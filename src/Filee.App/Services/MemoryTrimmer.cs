// Gives memory back while Filee waits in the tray (issue #119): a while after the last conversion finished, and after
// the main window was released. Large images grow the managed heap and the engines' caches, and the garbage collector
// keeps that memory committed for the next allocation, so Filee stayed hundreds of MB above its idle size.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class MemoryTrimmer : IDisposable
{
    /// <summary>How long no conversion may have run before memory is trimmed.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly Func<bool> _busy;
    private readonly Action _trim;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private bool _requested;
    private bool _disposed;
    private int _trims;

    public MemoryTrimmer(JobQueue queue, ILogger<MemoryTrimmer> log)
        : this(() => queue.IsBusy, () => Trim(log), IdleDelay)
    {
        queue.JobFinished += (_, _) => Request();
    }

    internal MemoryTrimmer(Func<bool> busy, Action trim, TimeSpan delay)
    {
        _busy = busy;
        _trim = trim;
        _delay = delay;
        _timer = new Timer(_ => OnQuiet());
    }

    /// <summary>How often memory was trimmed.</summary>
    internal int Trims => Volatile.Read(ref _trims);

    /// <summary>
    /// Memory was freed (a job finished, the main window closed): trim once <see cref="IdleDelay"/> has passed without
    /// another request and no conversion runs. A request meanwhile moves the time on; nothing happens without one.
    /// </summary>
    public void Request()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _requested = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnQuiet()
    {
        lock (_lock)
        {
            if (!_requested || _disposed)
                return;
            if (_busy())
            {
                // A conversion started meanwhile: never during one. Look again later (its end requests too).
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
                return;
            }
            _requested = false;
        }
        _trim();
        Interlocked.Increment(ref _trims);
    }

    /// <summary>Drops the engines' caches, collects every generation, then trims the working set (Windows).</summary>
    internal static void Trim(ILogger log)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var before = PrivateMegabytes();
            EngineCaches.Release();
            // Without compacting: garbage is freed and empty heap regions are decommitted all the same. Compaction was
            // once blamed for a CI crash in Skia (sk_pngencoder_encode) after a trim; the cause was tests that let a
            // captured frame be collected while Avalonia encoded it (WriteableBitmap.Save encodes a snapshot that
            // doesn't own the pixels, and the finalizers this trim runs freed them). Nothing in Filee leaves native
            // code with a pointer into managed memory: NativeMemoryStressTests converts under constant compaction.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            ProcessMemory.TrimWorkingSet();
            log.LogInformation("Trimmed memory: private {Before} MB → {After} MB (managed heap {Heap} MB) in {Elapsed} ms",
                before, PrivateMegabytes(), GC.GetTotalMemory(false) / (1024 * 1024), watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Could not trim memory");
        }
    }

    private static long PrivateMegabytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
