// Holds every registered converter, caches their availability and builds route planners.

namespace Filee.Core.Conversion;

/// <summary>Registry of all converters plus the user's engine priority.</summary>
public sealed class ConverterCatalog
{
    private readonly IReadOnlyList<IConverter> _all;
    private volatile Dictionary<string, EngineStatus>? _statusCache;
    private int _generation;
    private readonly Lock _statusLock = new();
    private IReadOnlyList<string> _priority = [];

    public ConverterCatalog(IEnumerable<IConverter> converters) => _all = converters.ToList();

    /// <summary>Every registered converter, available or not.</summary>
    public IReadOnlyList<IConverter> All => _all;

    /// <summary>Raised after <see cref="Refresh"/> or a priority change.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Engine ids in preferred order. Engines earlier in the list win ties.
    /// Engines not listed keep their registration order after the listed ones.
    /// </summary>
    public IReadOnlyList<string> Priority
    {
        get => _priority;
        set
        {
            _priority = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Converters ordered by priority (for display in settings).</summary>
    public IReadOnlyList<IConverter> Ordered =>
        _all.OrderBy(c => PriorityIndex(c)).ThenBy(c => _all.ToList().IndexOf(c)).ToList();

    /// <summary>Cached availability of a converter.</summary>
    public EngineStatus StatusOf(IConverter converter) => Statuses()[converter.Id];

    /// <summary>Re-checks every engine (e.g. after the user changed an engine path).</summary>
    public void Refresh()
    {
        Interlocked.Increment(ref _generation);
        _statusCache = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Checks every engine on a background thread, so the first conversion menu doesn't wait for them (some start a
    /// program to read its version). A caller that needs a status meanwhile waits for this check instead of starting
    /// its own.
    /// </summary>
    public Task CheckInBackgroundAsync() => Task.Run(() => Statuses());

    /// <summary>
    /// The statuses, checked once and shared between threads. A <see cref="Refresh"/> during a check makes it check
    /// again, so a result from before a settings change is never kept.
    /// </summary>
    private Dictionary<string, EngineStatus> Statuses()
    {
        if (_statusCache is { } cached)
            return cached;
        lock (_statusLock)
        {
            while (true)
            {
                if (_statusCache is { } done)
                    return done;
                var generation = Volatile.Read(ref _generation);
                var statuses = _all.ToDictionary(c => c.Id, SafeStatus);
                if (generation == Volatile.Read(ref _generation))
                {
                    _statusCache = statuses;
                    return statuses;
                }
            }
        }
    }

    /// <summary>Creates a planner over the currently available converters.</summary>
    public RoutePlanner CreatePlanner() =>
        new(_all.Where(c => StatusOf(c).IsAvailable), c => Math.Min(PriorityIndex(c), 9));

    /// <summary>
    /// Creates a planner that also treats the engines in <paramref name="assumeInstalled"/> as available, to tell
    /// which conversions an optional engine download would enable.
    /// </summary>
    public RoutePlanner CreatePlanner(IReadOnlyCollection<string> assumeInstalled) =>
        new(_all.Where(c => StatusOf(c).IsAvailable || assumeInstalled.Contains(c.Id)), c => Math.Min(PriorityIndex(c), 9));

    private int PriorityIndex(IConverter c)
    {
        for (var i = 0; i < _priority.Count; i++)
            if (string.Equals(_priority[i], c.Id, StringComparison.OrdinalIgnoreCase))
                return i;
        return _priority.Count;
    }

    private static EngineStatus SafeStatus(IConverter c)
    {
        try
        {
            return c.GetStatus();
        }
        catch (Exception ex)
        {
            return EngineStatus.Unavailable("engine.reason.error", ex.Message);
        }
    }
}
