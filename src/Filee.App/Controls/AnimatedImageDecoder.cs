// Frames of an animated GIF or WebP (or a still picture) for AnimatedImage, decoded by Skia's codec, and the clock
// that says which frame is due. Kept free of app services: Filee.Setup links this file too (its progress slideshow).
//
// Native memory only: the encoded file is copied into an SKData (memory Skia owns) and frames are decoded straight into
// the caller's pixel buffer (a WriteableBitmap's framebuffer, also native). No managed array is ever handed to Skia.

using SkiaSharp;

namespace Filee.App.Controls;

/// <summary>
/// Decodes the frames of one picture into a caller-owned BGRA (premultiplied) buffer. Skia's frame info says which
/// earlier frame a frame is drawn over (<see cref="SKCodecFrameInfo.RequiredFrame"/>, which already accounts for the
/// previous frame's disposal); the decoder keeps track of the frame the buffer holds and decodes the missing ones first.
/// </summary>
public sealed class AnimatedImageDecoder : IDisposable
{
    /// <summary>Larger pictures are refused (a slide's demo is a few hundred pixels wide).</summary>
    public const long MaxPixels = 4096L * 4096L;

    private SKData? _data;
    private SKCodec? _codec;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly int[] _durations;

    /// <summary>The frame the caller's buffer holds, -1 when unknown.</summary>
    private int _inBuffer = -1;

    private AnimatedImageDecoder(SKData data, SKCodec codec)
    {
        _data = data;
        _codec = codec;
        Width = codec.Info.Width;
        Height = codec.Info.Height;
        // A still picture has no frame info: it is one frame.
        _frames = codec.FrameCount > 1 ? codec.FrameInfo : [];
        _durations = _frames.Length == 0 ? [0] : _frames.Select(f => NormalizeDuration(f.Duration)).ToArray();
        RepetitionCount = _frames.Length > 1 ? codec.RepetitionCount : 0;
    }

    /// <summary>
    /// Reads <paramref name="stream"/> (GIF, WebP, PNG, JPEG, …). Null when Skia can't read it or it is larger than
    /// <see cref="MaxPixels"/>.
    /// </summary>
    public static AnimatedImageDecoder? Open(Stream stream)
    {
        // SKData.Create copies the stream into memory Skia allocates and frees.
        var data = SKData.Create(stream);
        if (data is null)
            return null;
        var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            codec?.Dispose();
            data.Dispose();
            return null;
        }
        return new AnimatedImageDecoder(data, codec);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Number of frames; 1 for a still picture.</summary>
    public int FrameCount => _durations.Length;

    public bool IsAnimated => FrameCount > 1;

    /// <summary>How often the animation repeats after the first play: -1 forever, 0 plays once (Skia's meaning).</summary>
    public int RepetitionCount { get; }

    /// <summary>How long each frame shows, in milliseconds (see <see cref="NormalizeDuration"/>).</summary>
    public IReadOnlyList<int> Durations => _durations;

    /// <summary>The pixel layout <see cref="Decode"/> writes: BGRA, premultiplied alpha.</summary>
    public SKImageInfo Info => new(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

    public bool IsDisposed => _codec is null;

    /// <summary>
    /// Browsers show frames that claim 0 or 10 ms for 100 ms (old GIFs rely on it); anything else shows at least 20 ms.
    /// </summary>
    public static int NormalizeDuration(int milliseconds) => milliseconds <= 10 ? 100 : Math.Max(20, milliseconds);

    /// <summary>
    /// Decodes <paramref name="frame"/> into <paramref name="pixels"/> (<see cref="Info"/>, <paramref name="rowBytes"/>
    /// per row), which must stay valid for the call only. The buffer is expected to hold what the last call left in it;
    /// pass <paramref name="freshBuffer"/> for a new or changed one. False when the data is broken.
    /// </summary>
    public bool Decode(int frame, IntPtr pixels, int rowBytes, bool freshBuffer = false)
    {
        ObjectDisposedException.ThrowIf(_codec is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        if (pixels == IntPtr.Zero || rowBytes < Width * 4)
            throw new ArgumentException("The buffer is too small for the picture.", nameof(rowBytes));
        if (freshBuffer)
            _inBuffer = -1;
        if (frame == _inBuffer)
            return true;

        if (_frames.Length == 0)
            return Draw(frame: 0, prior: -1, pixels, rowBytes);

        // The frames to draw, oldest first: back through the required frames to one drawn on a clear canvas or the
        // one already in the buffer.
        var chain = new Stack<int>();
        for (var f = frame; ;)
        {
            chain.Push(f);
            var required = _frames[f].RequiredFrame;
            if (required < 0 || required == _inBuffer)
                break;
            f = required;
        }
        while (chain.TryPop(out var f))
        {
            if (!Draw(f, _frames[f].RequiredFrame, pixels, rowBytes))
                return false;
        }
        return true;
    }

    private bool Draw(int frame, int prior, IntPtr pixels, int rowBytes)
    {
        if (prior < 0)
        {
            // A frame of its own starts from a transparent canvas (it may cover only part of it).
            using var pixmap = new SKPixmap(Info, pixels, rowBytes);
            pixmap.Erase(SKColors.Transparent);
        }
        var options = _frames.Length == 0 ? new SKCodecOptions() : new SKCodecOptions(frame, prior);
        var result = _codec!.GetPixels(Info, pixels, rowBytes, options);
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            _inBuffer = -1;
            return false;
        }
        _inBuffer = frame;
        return true;
    }

    public void Dispose()
    {
        _codec?.Dispose();
        _codec = null;
        _data?.Dispose();
        _data = null;
        _inBuffer = -1;
    }
}

/// <summary>
/// Which frame of an animation is due: frame durations, looping (<see cref="AnimatedImageDecoder.RepetitionCount"/>)
/// and the last frame staying once the plays are over.
/// </summary>
public sealed class FrameTimeline
{
    private readonly IReadOnlyList<int> _durations;
    private readonly int _plays;
    private readonly double _loopMilliseconds;
    private double _inFrame;
    private int _played;

    /// <param name="durations">Milliseconds per frame.</param>
    /// <param name="repetitionCount">Repeats after the first play; negative repeats forever.</param>
    public FrameTimeline(IReadOnlyList<int> durations, int repetitionCount)
    {
        if (durations.Count == 0)
            throw new ArgumentException("An animation has at least one frame.", nameof(durations));
        _durations = durations;
        _plays = repetitionCount < 0 ? 0 : repetitionCount + 1;
        _loopMilliseconds = durations.Sum(d => (double)Math.Max(1, d));
    }

    /// <summary>The frame to show.</summary>
    public int Frame { get; private set; }

    /// <summary>True once every play is over (never for an endless animation or a single frame that never changes).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Time left until the next frame is due; <see cref="Timeout.InfiniteTimeSpan"/> when none will be.</summary>
    public TimeSpan UntilNextFrame => IsFinished || _durations.Count < 2
        ? Timeout.InfiniteTimeSpan
        : TimeSpan.FromMilliseconds(Math.Max(0, Math.Max(1, _durations[Frame]) - _inFrame));

    /// <summary>Moves the clock on; true when another frame is due.</summary>
    public bool Advance(TimeSpan elapsed)
    {
        if (IsFinished || _durations.Count < 2 || elapsed <= TimeSpan.Zero)
            return false;
        // A long pause (the PC slept) doesn't spin through many loops: at most one loop is caught up.
        _inFrame += Math.Min(elapsed.TotalMilliseconds, _loopMilliseconds);
        var before = Frame;
        while (_inFrame >= Math.Max(1, _durations[Frame]))
        {
            _inFrame -= Math.Max(1, _durations[Frame]);
            if (Frame + 1 < _durations.Count)
                Frame++;
            else if (_plays == 0 || ++_played < _plays)
                Frame = 0;
            else
            {
                IsFinished = true;
                _inFrame = 0;
                break;
            }
        }
        return Frame != before;
    }

    /// <summary>Back to the first frame, every play still to come.</summary>
    public void Reset()
    {
        Frame = 0;
        _inFrame = 0;
        _played = 0;
        IsFinished = false;
    }
}
