// The clip window's timeline: a ruler for the playhead over a strip of small pictures of the video with the loudness
// of its sound, the part outside the clip dimmed and the clip framed in the accent colour between two handles. Inside
// the clip its pieces show: cuts as thin lines, pieces cut out hatched (dark when they leave a pause), the selected
// piece outlined, a changed volume or speed written on it, fades as ramps at the ends.
// Dragging a handle moves that end, dragging inside the clip moves all of it, a click (or the ruler) puts the playhead
// there. With the magnet on, the handles and the playhead snap to cuts, the clip's ends and each other. Time always
// runs left to right, also in right-to-left languages.

using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.App.ViewModels;
using Filee.Core.Presets;

namespace Filee.App.Controls;

public sealed class ClipTimeline : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(Start));

    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(End));

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(Playhead));

    public static readonly StyledProperty<ClipHandle> ActiveHandleProperty =
        AvaloniaProperty.Register<ClipTimeline, ClipHandle>(nameof(ActiveHandle));

    public static readonly StyledProperty<IReadOnlyList<ClipPiece>?> PiecesProperty =
        AvaloniaProperty.Register<ClipTimeline, IReadOnlyList<ClipPiece>?>(nameof(Pieces));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ClipTimeline, int>(nameof(SelectedIndex), -1);

    public static readonly StyledProperty<IReadOnlyList<float>?> LoudnessProperty =
        AvaloniaProperty.Register<ClipTimeline, IReadOnlyList<float>?>(nameof(Loudness));

    public static readonly StyledProperty<bool> FadeInProperty =
        AvaloniaProperty.Register<ClipTimeline, bool>(nameof(FadeIn));

    public static readonly StyledProperty<bool> FadeOutProperty =
        AvaloniaProperty.Register<ClipTimeline, bool>(nameof(FadeOut));

    /// <summary>Snap the handles and the playhead to cuts, the clip's ends and each other (the magnet).</summary>
    public static readonly StyledProperty<bool> SnapProperty =
        AvaloniaProperty.Register<ClipTimeline, bool>(nameof(Snap), true);

    /// <summary>
    /// The stretch of the video shown, in seconds (zoomed in on the clip); the whole video when
    /// <see cref="ViewEnd"/> isn't after <see cref="ViewStart"/>.
    /// </summary>
    public static readonly StyledProperty<double> ViewStartProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(ViewStart));

    public static readonly StyledProperty<double> ViewEndProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(ViewEnd));

    public static readonly StyledProperty<IReadOnlyList<Bitmap?>?> ThumbnailsProperty =
        AvaloniaProperty.Register<ClipTimeline, IReadOnlyList<Bitmap?>?>(nameof(Thumbnails));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    /// <summary>The colour on the handles' grips (on the accent).</summary>
    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(OnAccentBrush), Brushes.White);

    /// <summary>Behind the pictures, until they are there.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(TrackBrush), Brushes.LightGray);

    /// <summary>Laid over the video outside the clip, mostly opaque (the colour of the card the timeline is on).</summary>
    public static readonly StyledProperty<IBrush?> StageBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(StageBrush), Brushes.White);

    /// <summary>The ruler's marks and the playhead.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(Foreground), Brushes.Black);

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public ClipHandle ActiveHandle { get => GetValue(ActiveHandleProperty); set => SetValue(ActiveHandleProperty, value); }
    public IReadOnlyList<ClipPiece>? Pieces { get => GetValue(PiecesProperty); set => SetValue(PiecesProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public IReadOnlyList<float>? Loudness { get => GetValue(LoudnessProperty); set => SetValue(LoudnessProperty, value); }
    public bool FadeIn { get => GetValue(FadeInProperty); set => SetValue(FadeInProperty, value); }
    public bool FadeOut { get => GetValue(FadeOutProperty); set => SetValue(FadeOutProperty, value); }
    public bool Snap { get => GetValue(SnapProperty); set => SetValue(SnapProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public IReadOnlyList<Bitmap?>? Thumbnails { get => GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? StageBrush { get => GetValue(StageBrushProperty); set => SetValue(StageBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>A handle was dragged (or the strip pressed beside the clip): the end it moves and where to.</summary>
    public event Action<ClipHandle, double>? HandleMoved;

    /// <summary>The clip was dragged by its middle: where it starts now.</summary>
    public event Action<double>? ClipMoved;

    /// <summary>The playhead was put somewhere: a click in the clip, or the ruler.</summary>
    public event Action<double>? PlayheadMoved;

    /// <summary>Width of a handle, outside the clip on either side.</summary>
    internal const double HandleWidth = 14;

    /// <summary>Height of the ruler above the pictures, where the playhead's knob is.</summary>
    internal const double RulerHeight = 16;

    private const double StripRadius = 9;
    private const double FrameThickness = 3;
    private const double DimOpacity = 0.68;

    /// <summary>How far beside a handle a press still takes it (thin clips, touch).</summary>
    private const double HandleSlop = 8;

    /// <summary>How far a drag moves before it moves the clip (less is a click: the playhead).</summary>
    private const double DragThreshold = 4;

    /// <summary>How near (in pixels) a cut or end has to be to snap to it.</summary>
    private const double SnapDistance = 8;

    private enum Drag { None, Start, End, Clip, Playhead, Pending }

    // Made once: a new cursor on every pointer move would ask the system for one each time.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor IbeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);

    private Drag _drag;
    private double _grabOffset; // seconds between the pointer and what it drags (the handle, or the clip's start)
    private Point _pressedAt;
    private INotifyCollectionChanged? _watched;

    static ClipTimeline()
    {
        AffectsRender<ClipTimeline>(DurationProperty, StartProperty, EndProperty, PlayheadProperty, ActiveHandleProperty, PiecesProperty,
            SelectedIndexProperty, LoudnessProperty, FadeInProperty, FadeOutProperty, ViewStartProperty, ViewEndProperty, ThumbnailsProperty, AccentBrushProperty,
            OnAccentBrushProperty, TrackBrushProperty, StageBrushProperty, ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<ClipTimeline>(false);
    }

    public ClipTimeline()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = false;
    }

    /// <summary>The strip of pictures: below the ruler, room for the handles at its sides.</summary>
    internal Rect Strip => new(HandleWidth, RulerHeight, Math.Max(1, Bounds.Width - 2 * HandleWidth), Math.Max(1, Bounds.Height - RulerHeight));

    /// <summary>The stretch of the video shown: the view, or all of it.</summary>
    private (double From, double To) View =>
        ViewEnd > ViewStart && Duration > 0
            ? (Math.Clamp(ViewStart, 0, Duration), Math.Clamp(ViewEnd, ViewStart, Duration))
            : (0, Math.Max(Duration, 1e-9));

    /// <summary>The x of <paramref name="seconds"/> on the strip (at its edge when outside the view).</summary>
    internal double XOf(double seconds) => Math.Clamp(RawX(seconds), Strip.Left, Strip.Right);

    /// <summary>The x of <paramref name="seconds"/>, also beyond the strip.</summary>
    private double RawX(double seconds)
    {
        var (from, to) = View;
        return Strip.X + (Duration > 0 ? (seconds - from) / Math.Max(1e-9, to - from) : 0) * Strip.Width;
    }

    /// <summary>The time at <paramref name="x"/> on the strip.</summary>
    internal double TimeAt(double x)
    {
        if (Duration <= 0)
            return 0;
        var (from, to) = View;
        return from + Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * (to - from);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailsProperty)
        {
            if (_watched is not null)
                _watched.CollectionChanged -= OnThumbnailsChanged;
            _watched = change.GetNewValue<IReadOnlyList<Bitmap?>?>() as INotifyCollectionChanged;
            if (_watched is not null)
                _watched.CollectionChanged += OnThumbnailsChanged;
        }
    }

    private void OnThumbnailsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var strip = Strip;
        if (strip.Width <= 2 || strip.Height <= 2)
            return;
        var accent = AccentBrush ?? Brushes.MediumPurple;
        DrawRuler(context);

        using (context.PushClip(new RoundedRect(strip, StripRadius)))
        {
            context.FillRectangle(TrackBrush ?? Brushes.LightGray, strip);
            DrawPictures(context, strip);
            DrawLoudness(context, strip);
            if (Duration > 0)
            {
                var dim = StageBrush ?? Brushes.White;
                var x0 = XOf(Start);
                var x1 = XOf(End);
                using (context.PushOpacity(DimOpacity))
                {
                    if (x0 > strip.Left)
                        context.FillRectangle(dim, new Rect(strip.Left, strip.Top, x0 - strip.Left, strip.Height));
                    if (x1 < strip.Right)
                        context.FillRectangle(dim, new Rect(x1, strip.Top, strip.Right - x1, strip.Height));
                }
                DrawPieces(context, strip, accent);
                DrawFades(context, strip);
            }
        }

        if (Duration <= 0)
            return;
        DrawClipFrame(context, strip, accent);
        DrawPlayhead(context, strip);
    }

    /// <summary>Ticks along the top: a short one every step, a taller one every fifth.</summary>
    private void DrawRuler(DrawingContext context)
    {
        if (Duration <= 0)
            return;
        var strip = Strip;
        var pen = new Pen(Foreground ?? Brushes.Gray, 1);
        // A step that leaves at least 8 pixels between ticks: 1, 2, 5, 10, 15, 30 s, 1, 2, 5, 10 min.
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200];
        var (from, to) = View;
        double[] fine = [0.1, 0.2, 0.5];
        var step = fine.Concat(steps).FirstOrDefault(s => s / (to - from) * strip.Width >= 8, steps[^1]);
        using (context.PushOpacity(0.45))
        {
            for (var n = (int)Math.Ceiling(from / step - 1e-6); n * step <= to + 1e-6; n++)
            {
                var x = Math.Round(XOf(n * step)) + 0.5;
                var tall = n % 5 == 0;
                context.DrawLine(pen, new Point(x, RulerHeight - (tall ? 8 : 4)), new Point(x, RulerHeight - 1));
            }
        }
    }

    /// <summary>Each picture fills its part of the strip (cut to fit, from the middle).</summary>
    private void DrawPictures(DrawingContext context, Rect strip)
    {
        if (Thumbnails is not { Count: > 0 } pictures)
            return;
        for (var i = 0; i < pictures.Count; i++)
        {
            if (pictures[i] is not { } bitmap)
                continue;
            // Each picture stands for its share of the video, wherever the view puts it.
            var left = RawX(Duration * i / pictures.Count);
            var right = RawX(Duration * (i + 1) / pictures.Count);
            if (right < strip.Left || left > strip.Right)
                continue;
            var target = new Rect(left, strip.Y, right - left + 0.5, strip.Height); // overlap: no seams
            var size = bitmap.Size;
            if (size.Width <= 0 || size.Height <= 0)
                continue;
            var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
            var w = target.Width / scale;
            var h = target.Height / scale;
            var source = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
            context.DrawImage(bitmap, source, target);
        }
    }

    /// <summary>
    /// The loudness as bars along the bottom of the strip, each as loud as its piece makes it (a quieter piece's bars
    /// lower).
    /// </summary>
    private void DrawLoudness(DrawingContext context, Rect strip)
    {
        if (Loudness is not { Count: > 0 } levels || Duration <= 0)
            return;
        var area = strip.Height * 0.45;
        var bottom = strip.Bottom - 2;
        var width = RawX(Duration / levels.Count) - RawX(0);
        var bars = new StreamGeometry();
        using (var g = bars.Open())
        {
            for (var i = 0; i < levels.Count; i++)
            {
                var x = RawX(Duration * i / levels.Count);
                if (x + width < strip.Left || x > strip.Right)
                    continue;
                var time = (i + 0.5) / levels.Count * Duration;
                var volume = VolumeAt(time);
                var height = Math.Min(area, Math.Max(0.5, levels[i] * volume * area));
                g.BeginFigure(new Point(x, bottom), true);
                g.LineTo(new Point(x + Math.Max(0.6, width - 0.6), bottom));
                g.LineTo(new Point(x + Math.Max(0.6, width - 0.6), bottom - height));
                g.LineTo(new Point(x, bottom - height));
                g.EndFigure(true);
            }
        }
        using (context.PushOpacity(0.35))
            context.DrawGeometry(Brushes.Black, new Pen(Brushes.Black, 1.5), bars);
        using (context.PushOpacity(0.85))
            context.DrawGeometry(Brushes.White, null, bars);
    }

    /// <summary>How loud the clip makes the video at <paramref name="seconds"/>: its piece's volume (0 when cut out), 1 outside.</summary>
    private double VolumeAt(double seconds)
    {
        if (Pieces is not { Count: > 0 } pieces || seconds < Start || seconds > End)
            return 1;
        foreach (var piece in pieces)
        {
            if (seconds <= piece.End)
                return piece.IsKept ? Math.Min(piece.Volume, 2) : 0;
        }
        return 1;
    }

    /// <summary>Cuts, pieces cut out, the selected piece, and a changed volume or speed.</summary>
    private void DrawPieces(DrawingContext context, Rect strip, IBrush accent)
    {
        if (Pieces is not { Count: > 0 } pieces)
            return;
        var hatch = new Pen(Brushes.White, 1.2);
        var cut = new Pen(Brushes.White, 2);
        var cutShadow = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 4);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var rect = new Rect(XOf(piece.Start), strip.Top, Math.Max(1, XOf(piece.End) - XOf(piece.Start)), strip.Height);
            if (!piece.IsKept)
            {
                var blank = piece.State == ClipPieceState.Blank;
                using (context.PushOpacity(blank ? 0.78 : 0.62))
                    context.FillRectangle(blank ? Brushes.Black : StageBrush ?? Brushes.White, rect);
                using (context.PushClip(rect))
                using (context.PushOpacity(blank ? 0.35 : 0.5))
                {
                    for (var x = rect.Left - rect.Height; x < rect.Right; x += 9)
                        context.DrawLine(blank ? hatch : new Pen(accent, 1.2), new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                }
            }
            if (i > 0)
            {
                var x = Math.Round(rect.Left) + 0.5;
                context.DrawLine(cutShadow, new Point(x, strip.Top), new Point(x, strip.Bottom));
                context.DrawLine(cut, new Point(x, strip.Top), new Point(x, strip.Bottom));
            }
            if (i == SelectedIndex && pieces.Count > 1)
                context.DrawRectangle(null, new Pen(accent, 2.5), rect.Deflate(new Thickness(1.5, FrameThickness + 1)), 4, 4);
            if (piece.IsKept && (piece.Volume != 1 || piece.Speed != 1) && rect.Width > 34)
                DrawBadge(context, piece, rect);
        }
    }

    /// <summary>"×2 · 50 %" in the corner of a piece whose speed or volume changed.</summary>
    private static void DrawBadge(DrawingContext context, ClipPiece piece, Rect rect)
    {
        var parts = new List<string>();
        if (piece.Speed != 1)
            parts.Add("×" + piece.Speed.ToString("0.##", CultureInfo.CurrentCulture));
        if (piece.Volume != 1)
            parts.Add(Math.Round(piece.Volume * 100).ToString(CultureInfo.CurrentCulture) + "%");
        var text = new FormattedText(string.Join(" · ", parts), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10.5, Brushes.White);
        var box = new Rect(rect.Left + 5, rect.Top + 5, text.Width + 10, text.Height + 2);
        if (box.Right > rect.Right - 3)
            return;
        using (context.PushOpacity(0.72))
            context.DrawRectangle(Brushes.Black, null, box, 5, 5);
        context.DrawText(text, new Point(box.Left + 5, box.Top + 1));
    }

    /// <summary>A fade in or out as a ramp: a shaded triangle at the clip's end.</summary>
    private void DrawFades(DrawingContext context, Rect strip)
    {
        if (!FadeIn && !FadeOut)
            return;
        var width = Math.Min(RawX(ClipWindowViewModel.FadeSeconds) - RawX(0), (XOf(End) - XOf(Start)) / 2);
        width = Math.Max(width, 10);
        var shade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        var line = new Pen(Brushes.White, 1.5);
        void Ramp(double from, double to)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(from, strip.Top), true);
                g.LineTo(new Point(to, strip.Top));
                g.LineTo(new Point(from, strip.Bottom));
                g.EndFigure(true);
            }
            context.DrawGeometry(shade, null, geometry);
            context.DrawLine(line, new Point(from, strip.Bottom), new Point(to, strip.Top));
        }
        if (FadeIn)
            Ramp(XOf(Start), XOf(Start) + width);
        if (FadeOut)
            Ramp(XOf(End), XOf(End) - width);
    }

    private void DrawClipFrame(DrawingContext context, Rect strip, IBrush accent)
    {
        var top = strip.Top;
        var bottom = strip.Bottom;
        var x0 = XOf(Start);
        var x1 = XOf(End);

        // Top and bottom of the frame, between the handles.
        context.FillRectangle(accent, new Rect(x0, top, Math.Max(0, x1 - x0), FrameThickness));
        context.FillRectangle(accent, new Rect(x0, bottom - FrameThickness, Math.Max(0, x1 - x0), FrameThickness));

        DrawHandle(context, accent, new Rect(x0 - HandleWidth, top, HandleWidth, bottom - top), left: true,
            ActiveHandle == ClipHandle.Start, _drag == Drag.Start);
        DrawHandle(context, accent, new Rect(x1, top, HandleWidth, bottom - top), left: false,
            ActiveHandle == ClipHandle.End, _drag == Drag.End);
    }

    private void DrawHandle(DrawingContext context, IBrush accent, Rect rect, bool left, bool active, bool dragging)
    {
        // Rounded on the outside, square where it meets the clip.
        var r = StripRadius;
        var corners = left
            ? new RoundedRect(rect, new CornerRadius(r, 0, 0, r))
            : new RoundedRect(rect, new CornerRadius(0, r, r, 0));
        if (active)
        {
            // A soft glow round the handle the arrow keys move and the large picture shows.
            var glow = new Pen(accent, dragging ? 7 : 5) { LineJoin = PenLineJoin.Round };
            using (context.PushOpacity(0.28))
                context.DrawRectangle(null, glow, corners.Rect, r, r);
        }
        using (context.PushOpacity(active ? 1 : 0.78))
            context.DrawRectangle(accent, null, corners);

        // Two short grip lines in the middle.
        var grip = new Pen(OnAccentBrush ?? Brushes.White, 1.6, lineCap: PenLineCap.Round);
        var cx = rect.Center.X;
        var cy = rect.Center.Y;
        var half = Math.Min(8, rect.Height / 5);
        context.DrawLine(grip, new Point(cx - 2, cy - half), new Point(cx - 2, cy + half));
        context.DrawLine(grip, new Point(cx + 2, cy - half), new Point(cx + 2, cy + half));
    }

    /// <summary>The playhead: a line over the strip with a knob on the ruler.</summary>
    private void DrawPlayhead(DrawingContext context, Rect strip)
    {
        var x = Math.Round(XOf(Playhead)) + 0.5;
        var color = Foreground ?? Brushes.Black;
        var active = ActiveHandle == ClipHandle.Playhead;
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 4), new Point(x, strip.Top), new Point(x, strip.Bottom));
        context.DrawLine(new Pen(color, active ? 2 : 1.5), new Point(x, 2), new Point(x, strip.Bottom));
        var knob = new StreamGeometry();
        using (var g = knob.Open())
        {
            g.BeginFigure(new Point(x - 6, 1), true);
            g.LineTo(new Point(x + 6, 1));
            g.LineTo(new Point(x + 6, 8));
            g.LineTo(new Point(x, RulerHeight - 1));
            g.LineTo(new Point(x - 6, 8));
            g.EndFigure(true);
        }
        context.DrawGeometry(active ? AccentBrush ?? color : color, null, knob);
    }

    // ───────────────────────── Dragging ─────────────────────────

    /// <summary>What a press at <paramref name="point"/> takes: the ruler, a handle, the clip, or beside it the nearer handle.</summary>
    private Drag HitTest(Point point)
    {
        if (point.Y < RulerHeight)
            return Drag.Playhead;
        var x = point.X;
        var x0 = XOf(Start);
        var x1 = XOf(End);
        var onStart = x >= x0 - HandleWidth - HandleSlop && x <= x0 + HandleSlop;
        var onEnd = x >= x1 - HandleSlop && x <= x1 + HandleWidth + HandleSlop;
        if (onStart && onEnd)
            return Math.Abs(x - (x0 - HandleWidth / 2)) <= Math.Abs(x - (x1 + HandleWidth / 2)) ? Drag.Start : Drag.End;
        if (onStart)
            return Drag.Start;
        if (onEnd)
            return Drag.End;
        if (x > x0 && x < x1)
            return Drag.Clip;
        return x <= x0 ? Drag.Start : Drag.End;
    }

    /// <summary>
    /// <paramref name="seconds"/>, or with the magnet on the cut, end or playhead within <see cref="SnapDistance"/>
    /// pixels of it (never <paramref name="moving"/>'s own position).
    /// </summary>
    private double SnapTime(double seconds, Drag moving)
    {
        if (!Snap || Duration <= 0)
            return seconds;
        var points = new List<double>();
        if (Pieces is { } pieces)
            points.AddRange(pieces.Skip(1).Select(p => p.Start));
        if (moving != Drag.Start)
            points.Add(Start);
        if (moving != Drag.End)
            points.Add(End);
        if (moving != Drag.Playhead)
            points.Add(Playhead);
        var x = XOf(seconds);
        var snapped = seconds;
        var nearest = SnapDistance;
        foreach (var point in points)
        {
            var distance = Math.Abs(XOf(point) - x);
            if (distance <= nearest)
            {
                nearest = distance;
                snapped = point;
            }
        }
        return snapped;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        _pressedAt = point;
        var hit = HitTest(point);
        var time = TimeAt(point.X);
        _drag = hit;
        switch (hit)
        {
            case Drag.Playhead:
                PlayheadMoved?.Invoke(SnapTime(time, Drag.Playhead));
                break;
            case Drag.Start:
                // On the handle: it keeps where it was grabbed. Beside the clip: the handle jumps there.
                _grabOffset = point.X <= XOf(Start) + HandleSlop && point.X >= XOf(Start) - HandleWidth - HandleSlop ? Start - time : 0;
                HandleMoved?.Invoke(ClipHandle.Start, SnapTime(time + _grabOffset, Drag.Start));
                break;
            case Drag.End:
                _grabOffset = point.X >= XOf(End) - HandleSlop && point.X <= XOf(End) + HandleWidth + HandleSlop ? End - time : 0;
                HandleMoved?.Invoke(ClipHandle.End, SnapTime(time + _grabOffset, Drag.End));
                break;
            case Drag.Clip:
                // A click puts the playhead here; a drag moves the clip.
                _grabOffset = Start - time;
                _drag = Drag.Pending;
                break;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = Duration <= 0 ? Cursor.Default
                : HitTest(point) switch
                {
                    Drag.Clip => HandCursor,
                    Drag.Playhead => IbeamCursor,
                    _ => SizeWestEastCursor,
                };
            return;
        }
        if (_drag == Drag.Pending)
        {
            if (Math.Abs(point.X - _pressedAt.X) < DragThreshold)
                return;
            _drag = Drag.Clip;
        }
        var time = TimeAt(point.X);
        switch (_drag)
        {
            case Drag.Clip:
                var start = time + _grabOffset;
                // The magnet: the clip's start (or end) snaps.
                var snappedStart = SnapTime(start, Drag.Start);
                var snappedEnd = SnapTime(start + (End - Start), Drag.End) - (End - Start);
                ClipMoved?.Invoke(snappedStart != start ? snappedStart : snappedEnd);
                break;
            case Drag.Playhead:
                PlayheadMoved?.Invoke(SnapTime(time, Drag.Playhead));
                break;
            default:
                var handle = _drag == Drag.Start ? ClipHandle.Start : ClipHandle.End;
                HandleMoved?.Invoke(handle, SnapTime(time + _grabOffset, _drag));
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.Pending)
            PlayheadMoved?.Invoke(SnapTime(TimeAt(e.GetPosition(this).X), Drag.Playhead));
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
        InvalidateVisual();
    }

    private void EndDrag(IPointer pointer)
    {
        if (_drag == Drag.None)
            return;
        _drag = Drag.None;
        pointer.Capture(null);
        InvalidateVisual();
    }
}
