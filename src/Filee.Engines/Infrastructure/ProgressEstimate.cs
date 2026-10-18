// Estimated progress for engines that report nothing while they work (LibreOffice, Pandoc, rhwp, Word): the value
// rises quickly at first and ever slower towards an upper bound, so the progress ring never sits still at 0 %.

using System.Diagnostics;

namespace Filee.Engines.Infrastructure;

/// <summary>Reports a rising progress estimate until disposed. Does nothing without a progress sink.</summary>
public sealed class ProgressEstimate : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);

    private readonly Timer? _timer;
    private int _disposed;

    private ProgressEstimate(IProgress<double>? progress, TimeSpan typical, double from, double to)
    {
        if (progress is null)
            return;
        var started = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.1, typical.TotalSeconds);
        _timer = new Timer(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                progress.Report(Value(Stopwatch.GetElapsedTime(started).TotalSeconds, seconds, from, to));
        }, null, Interval, Interval);
    }

    /// <summary>Starts reporting.</summary>
    /// <param name="progress">Progress sink of the conversion step (may be null).</param>
    /// <param name="typical">Usual duration of the work: after it the estimate has covered about 63 % of the way.</param>
    /// <param name="from">Value at the start (what the engine reported before starting the work).</param>
    /// <param name="to">Upper bound, reached only asymptotically; the engine reports 1 itself when done.</param>
    public static ProgressEstimate Start(IProgress<double>? progress, TimeSpan typical, double from = 0.1, double to = 0.9) =>
        new(progress, typical, from, to);

    /// <summary>The estimate after <paramref name="elapsed"/> seconds.</summary>
    internal static double Value(double elapsed, double typical, double from, double to) =>
        from + (to - from) * (1 - Math.Exp(-Math.Max(0, elapsed) / typical));

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer?.Dispose();
    }
}
