// Notices when the UI thread stops answering. A report said every gesture, shortcut and drop did nothing while the log
// stayed empty; a line saying the UI thread was stuck (and for how long) tells a hang from events that never arrived.
// A background timer posts a ping to the UI thread every second. While a ping waits longer than the threshold, one
// warning is written for that episode; when the ping finally runs, one line says how long the UI thread was gone.

using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

/// <summary>Logs UI thread stalls longer than a threshold, once per episode.</summary>
internal sealed class UiWatchdog : IDisposable
{
    /// <summary>How often the UI thread is pinged.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Stalls shorter than this aren't worth a line (a big folder list, a first window).</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(5);

    private readonly Action<Action> _post;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _threshold;
    private readonly Lock _lock = new();
    private DateTimeOffset? _pingSentAt;
    private bool _reported;
    private ITimer? _timer;

    /// <param name="post">Queues an action on the UI thread (Dispatcher.UIThread.Post in the app).</param>
    public UiWatchdog(Action<Action> post, ILogger log, TimeProvider? time = null, TimeSpan? threshold = null)
    {
        _post = post;
        _log = log;
        _time = time ?? TimeProvider.System;
        _threshold = threshold ?? DefaultThreshold;
    }

    /// <summary>Starts pinging every <see cref="Interval"/> on a thread-pool timer.</summary>
    public void Start() => _timer ??= _time.CreateTimer(_ => Tick(), null, Interval, Interval);

    /// <summary>One timer tick: sends a ping, or checks how long the last one has been waiting.</summary>
    internal void Tick()
    {
        lock (_lock)
        {
            if (_pingSentAt is { } sent)
            {
                var waited = _time.GetUtcNow() - sent;
                if (!_reported && waited > _threshold)
                {
                    _reported = true;
                    _log.LogWarning("UI thread blocked for {Seconds:0} s", waited.TotalSeconds);
                }
                return;
            }
            _pingSentAt = _time.GetUtcNow();
        }
        try
        {
            _post(Pong);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The dispatcher is shutting down: nothing left to watch.
            lock (_lock)
                _pingSentAt = null;
        }
    }

    /// <summary>Runs on the UI thread when it got to the ping.</summary>
    private void Pong()
    {
        lock (_lock)
        {
            if (_reported && _pingSentAt is { } sent)
                _log.LogInformation("UI thread responsive again after {Seconds:0.0} s", (_time.GetUtcNow() - sent).TotalSeconds);
            _pingSentAt = null;
            _reported = false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
