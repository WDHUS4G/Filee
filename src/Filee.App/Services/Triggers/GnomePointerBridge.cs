using System.Net.Sockets;
using System.Text.Json;
using Filee.Core.Settings;

namespace Filee.App.Services.Triggers;

/// <summary>
/// Connects to the per-user GNOME Shell extension. It sends pointer state only, never typed keys or file contents.
/// No native global hook is loaded on this path. Reconnection also covers enabling the extension after Filee starts.
/// </summary>
internal sealed class GnomePointerBridge(Action<GnomePointerState> received, Action<GnomeBridgeStatus> status) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader = Task.CompletedTask;

    public GnomePointerBridge(string socketPath, Action<GnomePointerState> received, Action<GnomeBridgeStatus> status)
        : this(received, status) => _reader = ReadAsync(socketPath);

    // The name has no version in it: every line carries one, so a Shell still running an older or newer extension
    // is reported as a mismatch instead of looking like a missing extension.
    public static string? SocketPath => Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { } directory
        && Path.IsPathFullyQualified(directory) ? Path.Combine(directory, "filee-gnome", "pointer.sock") : null;

    private async Task ReadAsync(string path)
    {
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            var mismatch = false;
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel);
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream);
                var connected = false;
                while (!cancel.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3)); // also detects a stalled Shell connection
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line is null)
                        break;
                    var parsed = GnomePointerState.Parse(line, out var state);
                    if (parsed == GnomePointerParse.OtherVersion)
                    {
                        // GNOME Shell keeps running the extension it loaded at sign-in, even after an update.
                        mismatch = true;
                        break;
                    }
                    if (parsed != GnomePointerParse.Valid)
                        throw new IOException("Invalid GNOME pointer protocol.");
                    if (!connected)
                    {
                        status(GnomeBridgeStatus.Connected);
                        connected = true;
                    }
                    received(state!);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // Disabled extension, sign-out, or update. Keep the drop zone and the rest of Filee usable.
            }
            finally
            {
                status(mismatch ? GnomeBridgeStatus.VersionMismatch : GnomeBridgeStatus.Waiting);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(mismatch ? 5 : 1), cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The reader owns its socket; cancellation closes it after pending I/O unwinds, without blocking the UI.
        _ = _reader.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}

internal enum GnomeBridgeStatus { Waiting, Connected, VersionMismatch }

internal enum GnomePointerParse { Valid, OtherVersion, Invalid }

internal sealed record GnomePointerState(int Version, int X, int Y, int Modifiers, int Buttons,
    string? Process, bool Desktop, bool Blocked)
{
    /// <summary>The protocol version this Filee reads; the extension sends it as <c>PROTOCOL_VERSION</c>.</summary>
    public const int ProtocolVersion = 1;

    public static bool TryParse(string json, out GnomePointerState? state) => Parse(json, out state) == GnomePointerParse.Valid;

    public static GnomePointerParse Parse(string json, out GnomePointerState? state)
    {
        state = null;
        if (json.Length > 2048)
            return GnomePointerParse.Invalid;
        try
        {
            // A missing version never turns into input; another version is reported, never read.
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                return GnomePointerParse.Invalid;
            if (number != ProtocolVersion)
                return GnomePointerParse.OtherVersion;
            // Deserialize only the fixed protocol fields.
            var parsed = document.RootElement.Deserialize<GnomePointerState>();
            if (parsed is not { Modifiers: >= 0 and <= 15, Buttons: >= 0 and <= 31 }
                || Math.Abs((long)parsed.X) > 1_000_000 || Math.Abs((long)parsed.Y) > 1_000_000
                || (parsed.Process?.Length ?? 0) > 256)
                return GnomePointerParse.Invalid;
            state = parsed;
            return GnomePointerParse.Valid;
        }
        catch (JsonException) { return GnomePointerParse.Invalid; }
    }
}

/// <summary>Turns Shell snapshots into the same detector events as the existing hooks.</summary>
internal sealed class GnomePointerInput(GestureDetector detector)
{
    private int _buttons;
    private bool _ready;

    public void Reset(int x, int y)
    {
        detector.MouseUp(TriggerMouseButton.None, x, y);
        _buttons = 0;
        _ready = false;
    }

    public void Apply(GnomePointerState state)
    {
        if (state.Blocked)
        {
            Reset(state.X, state.Y);
            return;
        }
        // Never capture a drag already held when connecting, unlocking, or reconnecting.
        if (!_ready)
        {
            _ready = state.Buttons == 0;
            return;
        }
        var modifiers = (ModifierKeys)state.Modifiers;
        for (var bit = 0; bit < 5; bit++)
        {
            var mask = 1 << bit;
            if ((_buttons & mask) != 0 && (state.Buttons & mask) == 0)
                detector.MouseUp((TriggerMouseButton)(bit + 1), state.X, state.Y);
            if ((_buttons & mask) == 0 && (state.Buttons & mask) != 0)
                detector.MouseDown((TriggerMouseButton)(bit + 1), state.X, state.Y, modifiers);
        }
        _buttons = state.Buttons;
        var held = Enumerable.Range(0, 5).FirstOrDefault(bit => (_buttons & (1 << bit)) != 0, -1);
        detector.MouseMove(state.X, state.Y, modifiers, held < 0 ? TriggerMouseButton.None : (TriggerMouseButton)(held + 1));
    }
}
