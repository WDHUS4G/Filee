// Keeps diagnostics that can come in bursts to a few log lines: Explorer starts one Filee per selected file, and a
// gesture or shortcut can repeat many times a minute. The first event of a kind is logged, then at most one per
// interval, saying how many were held back in between.

namespace Filee.App.Services;

/// <summary>Rate limit for log lines, per kind of event. Thread-safe.</summary>
internal sealed class LogThrottle(TimeSpan interval, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (DateTimeOffset Last, int Held)> _kinds = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// True when an event of kind <paramref name="kind"/> may be logged now. <paramref name="held"/> is how many of
    /// that kind were held back since the last one logged (0 when it returns false).
    /// </summary>
    public bool TryEnter(string kind, out int held)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_kinds.TryGetValue(kind, out var entry) && now - entry.Last < interval)
            {
                _kinds[kind] = (entry.Last, entry.Held + 1);
                held = 0;
                return false;
            }
            held = entry.Held;
            _kinds[kind] = (now, 0);
            return true;
        }
    }

    /// <summary>" (3 more since the last one)", or "" when none were held back.</summary>
    public static string HeldText(int held) => held > 0 ? $" ({held} more since the last one)" : "";
}
