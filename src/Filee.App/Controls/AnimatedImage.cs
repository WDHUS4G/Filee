// Shows an animated GIF or WebP (Avalonia's Image shows only the first frame) or a still picture. Used by the
// "What's new" slides; Filee.Setup links this file for the slideshow on its progress page, so it needs nothing but
// Avalonia and SkiaSharp.
//
// Frames are decoded on demand into one WriteableBitmap (native memory), so a long animation costs one frame of memory.
// The animation runs only while the control is in a visible window, and everything is released when it leaves the
// visual tree (the window closed or the slide changed) or its source changes.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Filee.App.Controls;

public sealed class AnimatedImage : Control
{
    /// <summary>The picture: an <c>avares://</c> resource or a <c>file://</c> path.</summary>
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<AnimatedImage, Uri?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<AnimatedImage, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>False shows <see cref="PosterFrame"/> only (reduced motion).</summary>
    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<AnimatedImage, bool>(nameof(AutoPlay), true);

    /// <summary>The frame shown while not playing: one that explains the animation on its own (0 = the first).</summary>
    public static readonly StyledProperty<int> PosterFrameProperty =
        AvaloniaProperty.Register<AnimatedImage, int>(nameof(PosterFrame));

    /// <summary>How often a paused animation looks whether it became visible again.</summary>
    private static readonly TimeSpan PausedPoll = TimeSpan.FromMilliseconds(400);

    private AnimatedImageDecoder? _decoder;
    private WriteableBitmap? _bitmap;
    private FrameTimeline? _timeline;
    private DispatcherTimer? _timer;
    private TimeSpan _lastTick;
    private bool _attached;
    private int _shown = -1;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    static AnimatedImage()
    {
        AffectsRender<AnimatedImage>(StretchProperty);
        AffectsMeasure<AnimatedImage>(StretchProperty);
    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public int PosterFrame
    {
        get => GetValue(PosterFrameProperty);
        set => SetValue(PosterFrameProperty, value);
    }

    /// <summary>The clock the animation runs on (tests replace it).</summary>
    internal Func<TimeSpan> Clock { get; set; }

    /// <summary>The frame shown, -1 when nothing is loaded.</summary>
    public int CurrentFrame => _decoder is null ? -1 : _shown;

    /// <summary>Frames of the loaded picture, 0 when nothing is loaded.</summary>
    public int FrameCount => _decoder?.FrameCount ?? 0;

    /// <summary>Whether a picture is decoded and held in memory.</summary>
    public bool HasPicture => _decoder is not null;

    /// <summary>Whether the animation is running (not paused, finished or a still picture).</summary>
    public bool IsPlaying => _timer is { IsEnabled: true } && ShouldPlay();

    /// <summary>Pixel size of the picture.</summary>
    public PixelSize PixelSize => _decoder is null ? default : new PixelSize(_decoder.Width, _decoder.Height);

    public AnimatedImage()
    {
        Clock = () => _stopwatch.Elapsed;
        // Screenshots and demos are never mirrored, also in a right-to-left UI.
        FlowDirection = FlowDirection.LeftToRight;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Release();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            Release();
            if (_attached)
                Load();
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == AutoPlayProperty || change.Property == PosterFrameProperty)
        {
            // Reduced motion shows the poster, not wherever the animation happened to be; playing starts from the top.
            if (_timeline is { } timeline)
            {
                timeline.Reset();
                ShowFrame(AutoPlay ? 0 : Poster);
                _lastTick = Clock();
            }
            Schedule();
        }
        else if (change.Property == IsVisibleProperty)
            Schedule();
    }

    private void Load()
    {
        if (Source is not { } source || _decoder is not null)
            return;
        try
        {
            using var stream = Open(source);
            _decoder = stream is null ? null : AnimatedImageDecoder.Open(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // A missing or unreadable picture leaves the control empty; the slide still has its text.
            _decoder = null;
        }
        if (_decoder is null)
            return;
        _bitmap = new WriteableBitmap(new PixelSize(_decoder.Width, _decoder.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        _timeline = new FrameTimeline(_decoder.Durations, _decoder.RepetitionCount);
        if (!ShowFrame(0, freshBuffer: true) || !AutoPlay && Poster > 0 && !ShowFrame(Poster))
        {
            Release();
            return;
        }
        _lastTick = Clock();
        Schedule();
    }

    private static Stream? Open(Uri source) =>
        source.Scheme switch
        {
            "avares" => AssetLoader.Exists(source) ? AssetLoader.Open(source) : null,
            "file" => File.OpenRead(source.LocalPath),
            _ => null,
        };

    /// <summary>Gives back the decoder and the frame memory; the next attach (or source) loads again.</summary>
    private void Release()
    {
        _timer?.Stop();
        _timer = null;
        _timeline = null;
        _decoder?.Dispose();
        _decoder = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _shown = -1;
    }

    /// <summary><see cref="PosterFrame"/> within the picture's frames.</summary>
    private int Poster => _decoder is null ? 0 : Math.Clamp(PosterFrame, 0, _decoder.FrameCount - 1);

    private bool ShowFrame(int frame, bool freshBuffer = false)
    {
        if (_decoder is null || _bitmap is null)
            return false;
        if (frame == _shown && !freshBuffer)
            return true;
        bool decoded;
        using (var buffer = _bitmap.Lock())
            decoded = _decoder.Decode(frame, buffer.Address, buffer.RowBytes, freshBuffer);
        _shown = decoded ? frame : -1;
        InvalidateVisual();
        return decoded;
    }

    /// <summary>
    /// Whether the animation should run now: playing is allowed, there is something left to play, and the control is
    /// on screen (visible, in a window that is shown and not minimized).
    /// </summary>
    private bool ShouldPlay() =>
        AutoPlay && _decoder is { IsAnimated: true } && _timeline is { IsFinished: false } && IsOnScreen();

    private bool IsOnScreen() =>
        IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { } top
                             && top.IsVisible && top is not Window { WindowState: WindowState.Minimized };

    /// <summary>Starts, re-times or stops the timer for the next frame.</summary>
    private void Schedule()
    {
        var animated = AutoPlay && _decoder is { IsAnimated: true } && _timeline is { IsFinished: false };
        if (!animated)
        {
            _timer?.Stop();
            return;
        }
        var wait = IsOnScreen() ? _timeline!.UntilNextFrame : PausedPoll;
        if (wait == Timeout.InfiniteTimeSpan)
        {
            _timer?.Stop();
            return;
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Interval = wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait;
        _timer.Start();
    }

    /// <summary>One step of the animation: the time since the last step moves the clock on, unless it is paused.</summary>
    internal void Tick()
    {
        var now = Clock();
        var elapsed = now - _lastTick;
        _lastTick = now;
        // Paused (hidden or minimized): the time passes without moving the animation, so it goes on where it stopped.
        if (_timeline is not null && ShouldPlay() && _timeline.Advance(elapsed))
            ShowFrame(_timeline.Frame);
        Schedule();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _bitmap is null ? default : Stretch.CalculateSize(availableSize, _bitmap.Size);

    protected override Size ArrangeOverride(Size finalSize) =>
        _bitmap is null ? finalSize : Stretch.CalculateSize(finalSize, _bitmap.Size);

    public override void Render(DrawingContext context)
    {
        if (_bitmap is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var viewport = new Rect(Bounds.Size);
        var scale = Stretch.CalculateScaling(Bounds.Size, bitmap.Size);
        var scaled = bitmap.Size * scale;
        var destination = viewport.CenterRect(new Rect(scaled)).Intersect(viewport);
        var source = new Rect(bitmap.Size).CenterRect(new Rect(destination.Size / scale));
        context.DrawImage(bitmap, source, destination);
    }
}
