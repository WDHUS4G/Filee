// The clip window's sequence, as in an editor: the clip as it plays, its pieces one after the other on three tracks.
// V1 the pictures, A1 the video's own sound (its loudness, a volume line to drag, muted pieces hatched), A2 the music
// from another file (dragged along, its own volume line). A piece cut out leaves a gap with a button to close it; a
// piece's edges are dragged to trim it (outwards it takes back what was cut next to it). The ruler and a click put the
// playhead; with the magnet on, the playhead, edges and music snap to cuts and to the playhead. Time runs left to right
// in every language.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.App.ViewModels;
using Filee.Core.Presets;

namespace Filee.App.Controls;

/// <summary>The tracks of the sequence.</summary>
public enum ClipLane
{
    Video,
    Audio,
    Music,
}

/// <summary>How a click selects a piece: alone, added or taken out (Ctrl), or a run from the last one (Shift).</summary>
public enum ClipSelect
{
    Only,
    Toggle,
    Range,
}

public sealed class ClipSequence : Control
{
    public static readonly StyledProperty<IReadOnlyList<ClipPiece>?> PiecesProperty =
        AvaloniaProperty.Register<ClipSequence, IReadOnlyList<ClipPiece>?>(nameof(Pieces));

    /// <summary>The playhead, in seconds into the clip.</summary>
    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<ClipSequence, double>(nameof(Playhead));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ClipSequence, int>(nameof(SelectedIndex), -1);

    /// <summary>Every selected piece; empty: just <see cref="SelectedIndex"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<int>?> SelectedIndicesProperty =
        AvaloniaProperty.Register<ClipSequence, IReadOnlyList<int>?>(nameof(SelectedIndices));

    public static readonly StyledProperty<ClipLane> SelectedLaneProperty =
        AvaloniaProperty.Register<ClipSequence, ClipLane>(nameof(SelectedLane));

    /// <summary>Pictures of the video from <see cref="DetailStart"/> to <see cref="DetailEnd"/>, evenly spread.</summary>
    public static readonly StyledProperty<IReadOnlyList<Bitmap?>?> ThumbnailsProperty =
        AvaloniaProperty.Register<ClipSequence, IReadOnlyList<Bitmap?>?>(nameof(Thumbnails));

    /// <summary>The loudness of the video's sound from <see cref="DetailStart"/> to <see cref="DetailEnd"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<float>?> LoudnessProperty =
        AvaloniaProperty.Register<ClipSequence, IReadOnlyList<float>?>(nameof(Loudness));

    public static readonly StyledProperty<double> DetailStartProperty =
        AvaloniaProperty.Register<ClipSequence, double>(nameof(DetailStart));

    public static readonly StyledProperty<double> DetailEndProperty =
        AvaloniaProperty.Register<ClipSequence, double>(nameof(DetailEnd));

    public static readonly StyledProperty<ClipMusic?> MusicProperty =
        AvaloniaProperty.Register<ClipSequence, ClipMusic?>(nameof(Music));

    /// <summary>The loudness of the whole music file.</summary>
    public static readonly StyledProperty<IReadOnlyList<float>?> MusicLoudnessProperty =
        AvaloniaProperty.Register<ClipSequence, IReadOnlyList<float>?>(nameof(MusicLoudness));

    public static readonly StyledProperty<bool> FadeInProperty =
        AvaloniaProperty.Register<ClipSequence, bool>(nameof(FadeIn));

    public static readonly StyledProperty<bool> FadeOutProperty =
        AvaloniaProperty.Register<ClipSequence, bool>(nameof(FadeOut));

    public static readonly StyledProperty<bool> SnapProperty =
        AvaloniaProperty.Register<ClipSequence, bool>(nameof(Snap), true);

    /// <summary>The words on a gap's button ("Close gap").</summary>
    public static readonly StyledProperty<string?> GapTextProperty =
        AvaloniaProperty.Register<ClipSequence, string?>(nameof(GapText), "Close gap");

    /// <summary>Written on the music track when there is none ("Music…: add a file").</summary>
    public static readonly StyledProperty<string?> NoMusicTextProperty =
        AvaloniaProperty.Register<ClipSequence, string?>(nameof(NoMusicText));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(OnAccentBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(TrackBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> StageBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(StageBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(Foreground), Brushes.Black);

    /// <summary>The colour of the sound tracks' pieces.</summary>
    public static readonly StyledProperty<IBrush?> SoundBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(SoundBrush), new SolidColorBrush(Color.FromRgb(46, 160, 140)));

    public static readonly StyledProperty<IBrush?> MusicBrushProperty =
        AvaloniaProperty.Register<ClipSequence, IBrush?>(nameof(MusicBrush), new SolidColorBrush(Color.FromRgb(214, 138, 54)));

    public IReadOnlyList<ClipPiece>? Pieces { get => GetValue(PiecesProperty); set => SetValue(PiecesProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public ClipLane SelectedLane { get => GetValue(SelectedLaneProperty); set => SetValue(SelectedLaneProperty, value); }
    public IReadOnlyList<int>? SelectedIndices { get => GetValue(SelectedIndicesProperty); set => SetValue(SelectedIndicesProperty, value); }
    public IReadOnlyList<Bitmap?>? Thumbnails { get => GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public IReadOnlyList<float>? Loudness { get => GetValue(LoudnessProperty); set => SetValue(LoudnessProperty, value); }
    public double DetailStart { get => GetValue(DetailStartProperty); set => SetValue(DetailStartProperty, value); }
    public double DetailEnd { get => GetValue(DetailEndProperty); set => SetValue(DetailEndProperty, value); }
    public ClipMusic? Music { get => GetValue(MusicProperty); set => SetValue(MusicProperty, value); }
    public IReadOnlyList<float>? MusicLoudness { get => GetValue(MusicLoudnessProperty); set => SetValue(MusicLoudnessProperty, value); }
    public bool FadeIn { get => GetValue(FadeInProperty); set => SetValue(FadeInProperty, value); }
    public bool FadeOut { get => GetValue(FadeOutProperty); set => SetValue(FadeOutProperty, value); }
    public bool Snap { get => GetValue(SnapProperty); set => SetValue(SnapProperty, value); }
    public string? GapText { get => GetValue(GapTextProperty); set => SetValue(GapTextProperty, value); }
    public string? NoMusicText { get => GetValue(NoMusicTextProperty); set => SetValue(NoMusicTextProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? StageBrush { get => GetValue(StageBrushProperty); set => SetValue(StageBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SoundBrush { get => GetValue(SoundBrushProperty); set => SetValue(SoundBrushProperty, value); }
    public IBrush? MusicBrush { get => GetValue(MusicBrushProperty); set => SetValue(MusicBrushProperty, value); }

    /// <summary>The playhead was put somewhere: seconds into the clip.</summary>
    public event Action<double>? PlayheadMoved;

    /// <summary>A piece (or the music, -1) was clicked on a track: alone, with Ctrl or with Shift.</summary>
    public event Action<int, ClipLane, ClipSelect>? PieceSelected;

    /// <summary>Dragged across pieces: from and to (seconds into the clip), the track, and Ctrl (add to the selection).</summary>
    public event Action<double, double, ClipLane, bool>? MarqueeSelected;

    /// <summary>A piece's edge started to be dragged: the piece, and its end (true) or start.</summary>
    public event Action<int, bool>? EdgeDragStarted;

    /// <summary>The dragged edge's place in the video, in seconds.</summary>
    public event Action<double>? EdgeDragged;

    /// <summary>A gap's button was clicked: the piece that left it.</summary>
    public event Action<int>? GapCloseRequested;

    /// <summary>A piece's volume line was dragged: the piece and its volume (0 to 2).</summary>
    public event Action<int, double>? VolumeDragged;

    /// <summary>The music was dragged: where in the clip it starts now.</summary>
    public event Action<double>? MusicMoved;

    /// <summary>The music's volume line was dragged (0 to 2).</summary>
    public event Action<double>? MusicVolumeDragged;

    // Layout: the tracks' names on the left, a ruler on top, then V1, A1 and A2.
    internal const double LabelWidth = 30;
    internal const double RulerHeight = 16;
    internal const double VideoHeight = 54;
    internal const double SoundHeight = 40;
    internal const double MusicHeight = 32;
    private const double LaneGap = 4;
    private const double Radius = 6;
    private const double EdgeGrab = 7;
    private const double SnapDistance = 8;
    private const double DragThreshold = 4;

    private enum Drag { None, Playhead, Edge, Volume, Music, MusicVolume, Pending, Marquee }

    // Made once: a new cursor on every pointer move would ask the system for one each time.
    private static readonly Cursor IbeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor SizeNorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // The pieces' places in the clip and its length, worked out once per change of the pieces (not on every x).
    private IReadOnlyList<(int Index, double From, double To)>? _layout;
    private double _total = ClipRange.MinLength;

    private Drag _drag;
    private Point _pressedAt;
    private double _scaleAtPress;   // pixels a second when the drag started (the sequence changes length as it goes)
    private double _edgeAtPress;    // the dragged edge's place in the video when the drag started
    private double _speedAtPress;
    private bool _dragEnd;
    private int _pendingIndex = -1;
    private ClipLane _pendingLane;
    private double _musicOffsetAtPress;
    private ClipSelect _pendingMode;
    private bool _marqueeAdds;
    private double _marqueeFrom, _marqueeTo;

    static ClipSequence()
    {
        AffectsRender<ClipSequence>(PiecesProperty, PlayheadProperty, SelectedIndexProperty, SelectedIndicesProperty, SelectedLaneProperty, ThumbnailsProperty,
            LoudnessProperty, DetailStartProperty, DetailEndProperty, MusicProperty, MusicLoudnessProperty, FadeInProperty, FadeOutProperty,
            GapTextProperty, NoMusicTextProperty, AccentBrushProperty, TrackBrushProperty, StageBrushProperty, ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<ClipSequence>(false);
    }

    public ClipSequence()
    {
        FlowDirection = FlowDirection.LeftToRight;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PiecesProperty)
            _layout = null;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, RulerHeight + VideoHeight + LaneGap + SoundHeight + LaneGap + MusicHeight);

    internal Rect Content => new(LabelWidth + 6, 0, Math.Max(1, Bounds.Width - LabelWidth - 12), Bounds.Height);
    internal Rect VideoLane => new(Content.X, RulerHeight, Content.Width, VideoHeight);
    internal Rect SoundLane => new(Content.X, RulerHeight + VideoHeight + LaneGap, Content.Width, SoundHeight);
    internal Rect MusicLane => new(Content.X, SoundLane.Bottom + LaneGap, Content.Width, MusicHeight);

    /// <summary>How long the clip plays (at least a moment, so the scale is never infinite).</summary>
    internal double Total
    {
        get
        {
            _ = Layout();
            return _total;
        }
    }

    internal double XOf(double seconds) => Content.X + seconds / Total * Content.Width;

    internal double TimeAt(double x) => Math.Clamp((x - Content.X) / Content.Width, 0, 1) * Total;

    /// <summary>Where each piece that takes time stands in the clip: its index and its seconds into the clip.</summary>
    internal IReadOnlyList<(int Index, double From, double To)> Layout()
    {
        if (_layout is { } cached)
            return cached;
        var spans = new List<(int, double, double)>();
        _layout = spans;
        _total = ClipRange.MinLength;
        if (Pieces is not { } pieces)
            return spans;
        var at = 0.0;
        for (var i = 0; i < pieces.Count; i++)
        {
            var length = pieces[i].Length;
            if (length <= 0)
                continue;
            spans.Add((i, at, at + length));
            at += length;
        }
        _total = Math.Max(ClipRange.MinLength, at);
        return spans;
    }

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < LabelWidth + 20 || Pieces is not { Count: > 0 } pieces)
            return;
        var accent = AccentBrush ?? Brushes.MediumPurple;
        DrawLabels(context);
        DrawRuler(context);
        foreach (var lane in new[] { VideoLane, SoundLane, MusicLane })
            context.DrawRectangle(TrackBrush ?? Brushes.LightGray, null, lane, Radius, Radius);

        foreach (var (index, from, to) in Layout())
        {
            var piece = pieces[index];
            var video = new Rect(XOf(from), VideoLane.Y, Math.Max(1, XOf(to) - XOf(from)), VideoLane.Height);
            var sound = new Rect(video.X, SoundLane.Y, video.Width, SoundLane.Height);
            if (piece.State == ClipPieceState.Blank)
            {
                DrawGap(context, video, sound, accent);
                continue;
            }
            DrawPictures(context, piece, video);
            DrawSound(context, piece, sound);
            if (piece.Speed != 1 || piece.Volume != 1 || piece.Muted)
                DrawBadge(context, piece, video);
            var selected = IsSelected(index);
            var selectedVideo = selected && SelectedLane == ClipLane.Video;
            var selectedSound = selected && SelectedLane == ClipLane.Audio;
            context.DrawRectangle(null, new Pen(selectedVideo ? accent : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), selectedVideo ? 2.5 : 1),
                video.Deflate(selectedVideo ? 1.25 : 0.5), Radius, Radius);
            context.DrawRectangle(null, new Pen(selectedSound ? accent : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), selectedSound ? 2.5 : 1),
                sound.Deflate(selectedSound ? 1.25 : 0.5), Radius, Radius);
        }
        DrawFades(context);
        DrawMusic(context, accent);
        DrawPlayhead(context);
        if (_drag == Drag.Marquee)
        {
            // The stretch being dragged across: the pieces it touches become selected.
            var rect = new Rect(XOf(Math.Min(_marqueeFrom, _marqueeTo)), VideoLane.Top - 2,
                Math.Abs(XOf(_marqueeTo) - XOf(_marqueeFrom)), SoundLane.Bottom - VideoLane.Top + 4);
            using (context.PushOpacity(0.18))
                context.FillRectangle(accent, rect);
            context.DrawRectangle(null, new Pen(accent, 1.2) { DashStyle = new DashStyle([3, 2], 0) }, rect);
        }
    }

    /// <summary>True for a selected piece: one of <see cref="SelectedIndices"/>, or <see cref="SelectedIndex"/> without them.</summary>
    private bool IsSelected(int index) =>
        SelectedIndices is { Count: > 0 } several ? several.Contains(index) : index == SelectedIndex;

    private void DrawLabels(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.Black;
        void Label(string text, Rect lane)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
            using (context.PushOpacity(0.6))
                context.DrawText(formatted, new Point(4, lane.Center.Y - formatted.Height / 2));
        }
        Label("V1", VideoLane);
        Label("A1", SoundLane);
        Label("A2", MusicLane);
    }

    private void DrawRuler(DrawingContext context)
    {
        var pen = new Pen(Foreground ?? Brushes.Gray, 1);
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        var step = steps.FirstOrDefault(s => s / Total * Content.Width >= 8, steps[^1]);
        using (context.PushOpacity(0.45))
        {
            for (var n = 0; n * step <= Total + 1e-6; n++)
            {
                var x = Math.Round(XOf(n * step)) + 0.5;
                var tall = n % 5 == 0;
                context.DrawLine(pen, new Point(x, RulerHeight - (tall ? 8 : 4)), new Point(x, RulerHeight - 1));
            }
        }
    }

    /// <summary>The pictures of the piece's stretch of the video, side by side.</summary>
    private void DrawPictures(DrawingContext context, ClipPiece piece, Rect rect)
    {
        using var clip = context.PushClip(new RoundedRect(rect, Radius));
        context.FillRectangle(TrackBrush ?? Brushes.Gray, rect);
        if (Thumbnails is not { Count: > 0 } pictures || DetailEnd <= DetailStart)
            return;
        var span = (DetailEnd - DetailStart) / pictures.Count;
        // Picture k stands for [DetailStart + k·span, + span) of the video: drawn where that time plays in the piece.
        for (var k = 0; k < pictures.Count; k++)
        {
            if (pictures[k] is not { } bitmap)
                continue;
            var from = DetailStart + k * span;
            var to = from + span;
            if (to <= piece.Start || from >= piece.End)
                continue;
            var left = rect.X + (Math.Max(from, piece.Start) - piece.Start) / (piece.End - piece.Start) * rect.Width;
            var right = rect.X + (Math.Min(to, piece.End) - piece.Start) / (piece.End - piece.Start) * rect.Width;
            var target = new Rect(left, rect.Y, Math.Max(1, right - left + 0.5), rect.Height);
            var size = bitmap.Size;
            var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
            var w = target.Width / scale;
            var h = target.Height / scale;
            context.DrawImage(bitmap, new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h), target);
        }
    }

    /// <summary>The piece's sound: its loudness (as loud as its volume makes it) and its volume line; hatched when muted.</summary>
    private void DrawSound(DrawingContext context, ClipPiece piece, Rect rect)
    {
        var sound = SoundBrush ?? Brushes.Teal;
        using var clip = context.PushClip(new RoundedRect(rect, Radius));
        using (context.PushOpacity(piece.Muted ? 0.18 : 0.35))
            context.FillRectangle(sound, rect);
        if (piece.Muted)
        {
            using (context.PushOpacity(0.35))
            {
                var hatch = new Pen(Foreground ?? Brushes.Black, 1);
                for (var x = rect.Left - rect.Height; x < rect.Right; x += 8)
                    context.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
            }
            return;
        }
        if (Loudness is { Count: > 0 } levels && DetailEnd > DetailStart)
        {
            var bars = new StreamGeometry();
            using (var g = bars.Open())
            {
                var step = (DetailEnd - DetailStart) / levels.Count;
                var first = Math.Max(0, (int)((piece.Start - DetailStart) / step));
                var last = Math.Min(levels.Count - 1, (int)Math.Ceiling((piece.End - DetailStart) / step));
                for (var i = first; i <= last; i++)
                {
                    var t0 = Math.Max(piece.Start, DetailStart + i * step);
                    var t1 = Math.Min(piece.End, DetailStart + (i + 1) * step);
                    if (t1 <= t0)
                        continue;
                    var x0 = rect.X + (t0 - piece.Start) / (piece.End - piece.Start) * rect.Width;
                    var x1 = rect.X + (t1 - piece.Start) / (piece.End - piece.Start) * rect.Width;
                    var height = Math.Min(rect.Height - 4, Math.Max(0.5, levels[i] * Math.Min(piece.Volume, 2) * (rect.Height - 4) / 2));
                    var middle = rect.Center.Y;
                    g.BeginFigure(new Point(x0, middle - height / 2), true);
                    g.LineTo(new Point(Math.Max(x0 + 0.6, x1 - 0.4), middle - height / 2));
                    g.LineTo(new Point(Math.Max(x0 + 0.6, x1 - 0.4), middle + height / 2));
                    g.LineTo(new Point(x0, middle + height / 2));
                    g.EndFigure(true);
                }
            }
            context.DrawGeometry(sound, null, bars);
        }
        // The volume line: 100 % in the middle, 200 % at the top, silent at the bottom.
        var y = VolumeY(rect, piece.Volume);
        context.DrawLine(new Pen(Brushes.White, 3), new Point(rect.Left, y), new Point(rect.Right, y));
        context.DrawLine(new Pen(Foreground ?? Brushes.Black, 1.2), new Point(rect.Left, y), new Point(rect.Right, y));
    }

    private static double VolumeY(Rect rect, double volume) => rect.Bottom - 3 - Math.Clamp(volume, 0, 2) / 2 * (rect.Height - 6);

    private static double VolumeAt(Rect rect, double y) => Math.Clamp((rect.Bottom - 3 - y) / (rect.Height - 6) * 2, 0, 2);

    /// <summary>A gap a piece cut out left: dashed, hatched, with the button that closes it.</summary>
    private void DrawGap(DrawingContext context, Rect video, Rect sound, IBrush accent)
    {
        var rect = new Rect(video.X, video.Y, video.Width, sound.Bottom - video.Y);
        var dash = new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
        context.DrawRectangle(null, dash, rect.Deflate(1), Radius, Radius);
        using (context.PushClip(rect))
        using (context.PushOpacity(0.25))
        {
            var hatch = new Pen(accent, 1);
            for (var x = rect.Left - rect.Height; x < rect.Right; x += 9)
                context.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
        }
        var chip = GapButton(rect);
        if (chip is null)
            return;
        context.DrawRectangle(accent, null, chip.Value, 999, 999);
        var text = new FormattedText(chip.Value.Width > 40 ? "✕ " + GapText : "✕", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, OnAccentBrush ?? Brushes.White);
        context.DrawText(text, new Point(chip.Value.Center.X - text.Width / 2, chip.Value.Center.Y - text.Height / 2));
    }

    /// <summary>Where a gap's button is: centred, with its words when there is room, just ✕ when the gap is narrow.</summary>
    private Rect? GapButton(Rect gap)
    {
        if (gap.Width < 18)
            return null;
        var text = new FormattedText("✕ " + GapText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var width = text.Width + 16 <= gap.Width - 8 ? text.Width + 16 : 20;
        return new Rect(gap.Center.X - width / 2, gap.Center.Y - 10, width, 20);
    }

    private void DrawBadge(DrawingContext context, ClipPiece piece, Rect rect)
    {
        var parts = new List<string>();
        if (piece.Speed != 1)
            parts.Add("×" + piece.Speed.ToString("0.##", CultureInfo.CurrentCulture));
        if (piece.Muted)
            parts.Add("A1 ✕");
        else if (piece.Volume != 1)
            parts.Add(Math.Round(piece.Volume * 100).ToString(CultureInfo.CurrentCulture) + "%");
        var text = new FormattedText(string.Join(" · ", parts), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, Brushes.White);
        var box = new Rect(rect.Left + 5, rect.Top + 5, text.Width + 10, text.Height + 2);
        if (box.Right > rect.Right - 3)
            return;
        using (context.PushOpacity(0.72))
            context.DrawRectangle(Brushes.Black, null, box, 5, 5);
        context.DrawText(text, new Point(box.Left + 5, box.Top + 1));
    }

    private void DrawFades(DrawingContext context)
    {
        if (!FadeIn && !FadeOut)
            return;
        var width = Math.Max(8, Math.Min(XOf(ClipWindowViewModel.FadeSeconds) - XOf(0), Content.Width / 2));
        var shade = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        var line = new Pen(Brushes.White, 1.5);
        void Ramp(double from, double to)
        {
            foreach (var lane in new[] { VideoLane, SoundLane })
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(from, lane.Top), true);
                    g.LineTo(new Point(to, lane.Top));
                    g.LineTo(new Point(from, lane.Bottom));
                    g.EndFigure(true);
                }
                context.DrawGeometry(shade, null, geometry);
                context.DrawLine(line, new Point(from, lane.Bottom), new Point(to, lane.Top));
            }
        }
        if (FadeIn)
            Ramp(Content.Left, Content.Left + width);
        if (FadeOut)
            Ramp(Content.Right, Content.Right - width);
    }

    /// <summary>Where the music stands on A2, or null without music.</summary>
    internal Rect? MusicRect()
    {
        if (Music is not { } music)
            return null;
        var length = music.PlayLength(Total);
        if (length <= 0)
            return null;
        var left = XOf(music.Offset);
        return new Rect(left, MusicLane.Y, Math.Max(4, XOf(music.Offset + length) - left), MusicLane.Height);
    }

    private void DrawMusic(DrawingContext context, IBrush accent)
    {
        var lane = MusicLane;
        if (MusicRect() is not { } rect || Music is not { } music)
        {
            if (!string.IsNullOrEmpty(NoMusicText))
            {
                var hint = new FormattedText(NoMusicText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11,
                    Foreground ?? Brushes.Gray);
                using (context.PushOpacity(0.5))
                    context.DrawText(hint, new Point(lane.X + 10, lane.Center.Y - hint.Height / 2));
            }
            return;
        }
        var brush = MusicBrush ?? Brushes.Orange;
        using (context.PushClip(new RoundedRect(rect, Radius)))
        {
            using (context.PushOpacity(0.35))
                context.FillRectangle(brush, rect);
            if (MusicLoudness is { Count: > 0 } levels && music.Length > 0)
            {
                var length = music.PlayLength(Total);
                var bars = new StreamGeometry();
                using (var g = bars.Open())
                {
                    for (var i = 0; i < levels.Count; i++)
                    {
                        var t = music.Length * i / levels.Count - music.From; // seconds into its play
                        var t1 = music.Length * (i + 1) / levels.Count - music.From;
                        if (t1 <= 0 || t >= length)
                            continue;
                        var x0 = XOf(music.Offset + Math.Max(0, t));
                        var x1 = XOf(music.Offset + Math.Min(length, t1));
                        var height = Math.Max(0.5, levels[i] * Math.Min(music.Volume, 2) * (rect.Height - 4) / 2);
                        g.BeginFigure(new Point(x0, rect.Center.Y - height / 2), true);
                        g.LineTo(new Point(Math.Max(x0 + 0.6, x1 - 0.3), rect.Center.Y - height / 2));
                        g.LineTo(new Point(Math.Max(x0 + 0.6, x1 - 0.3), rect.Center.Y + height / 2));
                        g.LineTo(new Point(x0, rect.Center.Y + height / 2));
                        g.EndFigure(true);
                    }
                }
                context.DrawGeometry(brush, null, bars);
            }
            var name = new FormattedText(System.IO.Path.GetFileName(music.Path), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5,
                Foreground ?? Brushes.Black);
            context.DrawText(name, new Point(rect.Left + 6, rect.Top + 1));
            var y = VolumeY(rect, music.Volume);
            context.DrawLine(new Pen(Brushes.White, 3), new Point(rect.Left, y), new Point(rect.Right, y));
            context.DrawLine(new Pen(Foreground ?? Brushes.Black, 1.2), new Point(rect.Left, y), new Point(rect.Right, y));
        }
        var selected = SelectedLane == ClipLane.Music;
        context.DrawRectangle(null, new Pen(selected ? accent : brush, selected ? 2.5 : 1), rect.Deflate(selected ? 1.25 : 0.5), Radius, Radius);
    }

    private void DrawPlayhead(DrawingContext context)
    {
        var x = Math.Round(XOf(Math.Clamp(Playhead, 0, Total))) + 0.5;
        var color = Foreground ?? Brushes.Black;
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 4), new Point(x, RulerHeight), new Point(x, MusicLane.Bottom));
        context.DrawLine(new Pen(color, 1.5), new Point(x, 2), new Point(x, MusicLane.Bottom));
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
        context.DrawGeometry(AccentBrush ?? color, null, knob);
    }

    // ───────────────────────── Pointer ─────────────────────────

    /// <summary><paramref name="seconds"/> into the clip, or with the magnet the cut or playhead within reach.</summary>
    private double SnapTime(double seconds, bool playhead)
    {
        if (!Snap)
            return seconds;
        var points = Layout().SelectMany(s => new[] { s.From, s.To }).ToList();
        if (!playhead)
            points.Add(Playhead);
        var snapped = seconds;
        var nearest = SnapDistance;
        foreach (var point in points)
        {
            var distance = Math.Abs(XOf(point) - XOf(seconds));
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
        if (Pieces is not { Count: > 0 } pieces || !IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        _pressedAt = point;
        _scaleAtPress = Content.Width / Total;
        _drag = Drag.None;

        if (point.Y < RulerHeight)
        {
            _drag = Drag.Playhead;
            PlayheadMoved?.Invoke(SnapTime(TimeAt(point.X), playhead: true));
        }
        else if (point.Y >= MusicLane.Top - LaneGap / 2)
        {
            if (MusicRect() is { } music && music.Inflate(new Thickness(0, 2)).Contains(point))
            {
                PieceSelected?.Invoke(-1, ClipLane.Music, ClipSelect.Only);
                if (Math.Abs(point.Y - VolumeY(music, Music!.Volume)) <= 5)
                    _drag = Drag.MusicVolume;
                else
                {
                    _drag = Drag.Music;
                    _musicOffsetAtPress = Music.Offset;
                }
            }
            else
                PlayheadMoved?.Invoke(SnapTime(TimeAt(point.X), playhead: true));
        }
        else
        {
            var lane = point.Y >= SoundLane.Top - LaneGap / 2 ? ClipLane.Audio : ClipLane.Video;
            foreach (var (index, from, to) in Layout())
            {
                var left = XOf(from);
                var right = XOf(to);
                if (point.X < left - EdgeGrab || point.X > right + EdgeGrab)
                    continue;
                var piece = pieces[index];
                if (piece.State == ClipPieceState.Blank)
                {
                    var gap = new Rect(left, VideoLane.Y, right - left, SoundLane.Bottom - VideoLane.Y);
                    if (GapButton(gap) is { } button && button.Inflate(3).Contains(point))
                    {
                        GapCloseRequested?.Invoke(index);
                        e.Handled = true;
                        return;
                    }
                    if (point.X < left || point.X > right)
                        continue;
                }
                else if (Math.Abs(point.X - left) <= EdgeGrab || Math.Abs(point.X - right) <= EdgeGrab)
                {
                    // An edge: trimmed by dragging.
                    _dragEnd = Math.Abs(point.X - right) <= Math.Abs(point.X - left);
                    _edgeAtPress = _dragEnd ? piece.End : piece.Start;
                    _speedAtPress = piece.Speed;
                    _drag = Drag.Edge;
                    PieceSelected?.Invoke(index, lane, ClipSelect.Only);
                    EdgeDragStarted?.Invoke(index, _dragEnd);
                    break;
                }
                if (point.X < left || point.X > right)
                    continue;
                if (lane == ClipLane.Audio && !piece.Muted && Math.Abs(point.Y - VolumeY(new Rect(left, SoundLane.Y, right - left, SoundLane.Height), piece.Volume)) <= 5)
                {
                    _drag = Drag.Volume;
                    _pendingIndex = index;
                    PieceSelected?.Invoke(index, lane, ClipSelect.Only);
                    break;
                }
                // A click selects the piece (on release); a drag selects every piece it goes across.
                _drag = Drag.Pending;
                _pendingIndex = index;
                _pendingLane = lane;
                _pendingMode = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 ? ClipSelect.Toggle
                    : (e.KeyModifiers & KeyModifiers.Shift) != 0 ? ClipSelect.Range
                    : ClipSelect.Only;
                break;
            }
            if (_drag == Drag.None)
                PlayheadMoved?.Invoke(SnapTime(TimeAt(point.X), playhead: true));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = CursorAt(point);
            return;
        }
        var dx = point.X - _pressedAt.X;
        switch (_drag)
        {
            case Drag.Playhead:
                PlayheadMoved?.Invoke(SnapTime(TimeAt(point.X), playhead: true));
                break;
            case Drag.Edge:
                // The edge follows the pointer at the scale the drag started with.
                var seconds = dx / _scaleAtPress * _speedAtPress;
                EdgeDragged?.Invoke(_edgeAtPress + seconds);
                break;
            case Drag.Volume when _pendingIndex >= 0:
                VolumeDragged?.Invoke(_pendingIndex, VolumeAt(SoundLane, point.Y));
                break;
            case Drag.Music:
                var offset = _musicOffsetAtPress + dx / _scaleAtPress;
                MusicMoved?.Invoke(Math.Max(0, SnapTime(offset, playhead: false)));
                break;
            case Drag.MusicVolume:
                MusicVolumeDragged?.Invoke(VolumeAt(MusicLane, point.Y));
                break;
            case Drag.Pending when Math.Abs(dx) >= DragThreshold:
                _drag = Drag.Marquee;
                _marqueeFrom = TimeAt(_pressedAt.X);
                _marqueeAdds = _pendingMode != ClipSelect.Only;
                goto case Drag.Marquee;
            case Drag.Marquee:
                _marqueeTo = TimeAt(point.X);
                MarqueeSelected?.Invoke(_marqueeFrom, _marqueeTo, _pendingLane, _marqueeAdds);
                InvalidateVisual();
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.Pending)
        {
            PieceSelected?.Invoke(_pendingIndex, _pendingLane, _pendingMode);
            // Ctrl and Shift only change the selection; a plain click also puts the playhead there.
            if (_pendingMode == ClipSelect.Only)
                PlayheadMoved?.Invoke(SnapTime(TimeAt(e.GetPosition(this).X), playhead: true));
        }
        var marquee = _drag == Drag.Marquee;
        _drag = Drag.None;
        if (marquee)
            InvalidateVisual();
        _pendingIndex = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
    }

    private Cursor CursorAt(Point point)
    {
        if (point.Y < RulerHeight)
            return IbeamCursor;
        if (point.Y >= MusicLane.Top - LaneGap / 2)
            return MusicRect() is { } music && music.Contains(point)
                ? Math.Abs(point.Y - VolumeY(music, Music!.Volume)) <= 5 ? SizeNorthSouthCursor : SizeAllCursor
                : Cursor.Default;
        if (Pieces is not { } pieces)
            return Cursor.Default;
        foreach (var (index, from, to) in Layout())
        {
            var left = XOf(from);
            var right = XOf(to);
            if (pieces[index].State != ClipPieceState.Blank && (Math.Abs(point.X - left) <= EdgeGrab || Math.Abs(point.X - right) <= EdgeGrab))
                return SizeWestEastCursor;
            if (point.X >= left && point.X <= right && point.Y >= SoundLane.Top && !pieces[index].Muted && pieces[index].IsKept
                && Math.Abs(point.Y - VolumeY(new Rect(left, SoundLane.Y, right - left, SoundLane.Height), pieces[index].Volume)) <= 5)
                return SizeNorthSouthCursor;
        }
        return HandCursor;
    }
}
