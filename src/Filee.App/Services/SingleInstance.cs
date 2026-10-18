// Keeps one Filee per Windows session, or per user across Unix launcher/terminal sessions.
// Later launches forward arguments through a named pipe.

using System.IO.Pipes;
using System.Text.Json;

namespace Filee.App.Services;

/// <summary>Named mutex + named pipe single-instance helper.</summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode() is randomized per process, so a stable hash is required for both processes to agree.
    private static readonly string DefaultName = "Filee-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string name, bool isFirst)
    {
        _mutex = mutex;
        _name = name;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the arguments of a later launch.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public static SingleInstance Acquire() => Acquire(DefaultName);

    /// <summary>The instance under another name (tests, which must not reach a Filee that is running).</summary>
    internal static SingleInstance Acquire(string name)
    {
        bool createdNew;
        var mutex = OperatingSystem.IsWindows()
            ? new Mutex(initiallyOwned: true, @"Local\" + name, out createdNew)
            : new Mutex(initiallyOwned: true, name,
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);
        return new SingleInstance(mutex, name, createdNew);
    }

    /// <summary>Starts listening for later launches (first instance only).</summary>
    public void StartListening()
    {
        if (!IsFirst)
            return;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    var args = await JsonSerializer.DeserializeAsync(server, AppJsonContext.Default.StringArray, _cts.Token);
                    if (args is not null)
                        ArgumentsReceived?.Invoke(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // A client disconnected half-way; keep serving.
                }
            }
        });
    }

    /// <summary>
    /// Sends this process's arguments to the first instance. It only listens once its services are up, which can take
    /// over ten seconds on a slow or busy computer: a second launch (or the installer's --quit) right after the first
    /// lost its arguments after one 3-second try. Tries again until <paramref name="wait"/> has passed; false when the
    /// first instance never answered (it is shutting down, or hung).
    /// </summary>
    public bool ForwardToFirst(string[] args, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            try
            {
                using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect((int)Math.Clamp(left.TotalMilliseconds, 100, 1000));
                Filee.Platform.Windows.Foreground.AllowOtherProcess(); // its donut takes the focus (Esc, click away)
                JsonSerializer.Serialize(client, args, AppJsonContext.Default.StringArray);
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(100); // the pipe is busy with another launch, or not there yet
            }
        }
    }

    /// <summary>
    /// Waits until the first instance has exited (it releases the mutex then), e.g. after forwarding <c>--quit</c>.
    /// Returns false on timeout.
    /// </summary>
    public bool WaitForFirstToExit(TimeSpan timeout)
    {
        if (IsFirst)
            return true;
        try
        {
            if (!_mutex.WaitOne(timeout))
                return false;
        }
        catch (AbandonedMutexException)
        {
            // The first instance ended without releasing the mutex (killed): it is gone all the same.
        }
        _mutex.ReleaseMutex();
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
