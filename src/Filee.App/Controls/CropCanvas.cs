// The crop overlay's stage: the picture on a dimmed backdrop with soft accent glows, the part outside the frame
// frosted (a tiny copy of the preview drawn large, so nothing is decoded again while dragging), the frame in the
// accent colour with round handles, a rule-of-thirds grid that fades in while dragging and a size chip that follows
// the frame. Programmatic changes (reset, ratio, a new picture) ease in; dragging follows the pointer directly.

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Filee.App.Controls;

public enum CropHandle
{
    None,
    CreateNew,
    Body,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

public sealed class CropCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceImageProperty =
        AvaloniaProperty.Register<CropCanvas, Bitmap?>(nameof(SourceImage));

    public static readonly StyledProperty<Rect> NormalizedCropRectProperty =
        AvaloniaProperty.Register<CropCanvas, Rect>(nameof(NormalizedCropRect), new Rect(0.1, 0.1, 0.8, 0.8));

    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CropCanvas, double>(nameof(AspectRatio), 0);

    /// <summary>Room kept free around the picture for the floating chips and the toolbar.</summary>
    public static readonly StyledProperty<Thickness> ImagePaddingProperty =
        AvaloniaProperty.Register<CropCanvas, Thickness>(nameof(ImagePadding));

    /// <summary>"W × H px" in the chip under the frame; nothing when empty.</summary>
    public static readonly StyledProperty<string?> SizeTextProperty =
        AvaloniaProperty.Register<CropCanvas, string?>(nameof(SizeText));

    /// <summary>The picture's size in pixels: arrow keys move the frame by whole pixels of it.</summary>
    public static readonly StyledProperty<PixelSize> PixelSizeProperty =
        AvaloniaProperty.Register<CropCanvas, PixelSize>(nameof(PixelSize));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<CropCanvas, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    /// <summary>The theme's page colour: the stage under the picture and the frosted veil outside the frame.</summary>
    public static readonly StyledProperty<IBrush?> StageBrushProperty =
        AvaloniaProperty.Register<CropCanvas, IBrush?>(nameof(StageBrush));

    public static readonly StyledProperty<IBrush?> ChipBackgroundProperty =
        AvaloniaProperty.Register<CropCanvas, IBrush?>(nameof(ChipBackground), Brushes.White);

    public static readonly StyledProperty<IBrush?> ChipForegroundProperty =
        AvaloniaProperty.Register<CropCanvas, IBrush?>(nameof(ChipForeground), Brushes.Black);

    /// <summary>False with reduced motion: every change shows at once.</summary>
    public static readonly StyledProperty<bool> PlayAnimationsProperty =
        AvaloniaProperty.Register<CropCanvas, bool>(nameof(PlayAnimations), true);

    public Bitmap? SourceImage
    {
        get => GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    public Rect NormalizedCropRect
    {
        get => GetValue(NormalizedCropRectProperty);
        set => SetValue(NormalizedCropRectProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public Thickness ImagePadding { get => GetValue(ImagePaddingProperty); set => SetValue(ImagePaddingProperty, value); }
    public string? SizeText { get => GetValue(SizeTextProperty); set => SetValue(SizeTextProperty, value); }
    public PixelSize PixelSize { get => GetValue(PixelSizeProperty); set => SetValue(PixelSizeProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? StageBrush { get => GetValue(StageBrushProperty); set => SetValue(StageBrushProperty, value); }
    public IBrush? ChipBackground { get => GetValue(ChipBackgroundProperty); set => SetValue(ChipBackgroundProperty, value); }
    public IBrush? ChipForeground { get => GetValue(ChipForegroundProperty); set => SetValue(ChipForegroundProperty, value); }
    public bool PlayAnimations { get => GetValue(PlayAnimationsProperty); set => SetValue(PlayAnimationsProperty, value); }

    public event Action<Rect>? CropRectChanged;

    private CropHandle _activeHandle = CropHandle.None;
    private CropHandle _hoverHandle = CropHandle.None;
    private Point _dragStartPoint;
    private Rect _dragStartCrop;

    // The frame being moved sits on the picture's vertical / horizontal centre line: its guide is drawn.
    private bool _snappedX, _snappedY;
    private const double MinSizePx = 24.0;

    // ───── Motion: tweens evaluated against the clock at each render, so a lost frame never leaves one half done ─────

    private const double FrameSeconds = 0.34;   // reset, ratio
    private const double AppearSeconds = 0.42;  // a new picture
    private const double GridInSeconds = 0.14;
    private const double GridOutSeconds = 0.38;
    private const double HandleSeconds = 0.26;

    private Tween _frame = Tween.Done(1);    // 0 → 1 from _frameFrom to NormalizedCropRect
    private Rect _frameFrom;
    private Tween _appear = Tween.Done(1);
    private Tween _grid = Tween.Done(0);
    private Tween _handlePop = Tween.Done(0); // the hovered or dragged handle grows a little
    private CropHandle _poppedHandle = CropHandle.None;
    private bool _animateNextRect;
    private bool _looping;

    // ───── Cached drawing resources ─────

    private Bitmap? _soft;                 // the tiny copy for the frosted outside
    private FormattedText? _chipText;
    private string? _chipTextFor;
    private IBrush? _chipTextBrush;

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SizeNorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);

    private static readonly IPen FrameShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 4);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1);
    private static readonly IPen GridShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1);
    private static readonly IPen HandleRingPen = new Pen(Brushes.White, 2.5);
    private static readonly IBrush HandleShadowBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
    private static readonly BoxShadows PictureShadow = BoxShadows.Parse("0 10 30 0 #38000000");
    private static readonly BoxShadows ChipShadow = BoxShadows.Parse("0 2 8 0 #26000000");

    private static double SafeClamp(double val, double min, double max) =>
        min > max ? Math.Clamp(val, max, min) : Math.Clamp(val, min, max);

    static CropCanvas()
    {
        AffectsRender<CropCanvas>(SourceImageProperty, NormalizedCropRectProperty, AspectRatioProperty, ImagePaddingProperty,
            SizeTextProperty, AccentBrushProperty, StageBrushProperty, ChipBackgroundProperty, ChipForegroundProperty);
        FocusableProperty.OverrideDefaultValue<CropCanvas>(false);
    }

    // The picture is never mirrored, also in a right-to-left UI.
    public CropCanvas() => FlowDirection = FlowDirection.LeftToRight;

    public void ResetCrop()
    {
        SetRect(new Rect(0, 0, 1, 1), animate: true);
    }

    public void SetAspectRatio(double ratio)
    {
        AspectRatio = ratio;
        if (ratio <= 0)
        {
            InvalidateVisual();
            return;
        }

        var imgBounds = CalculateImageBounds();
        if (imgBounds.Width <= 0 || imgBounds.Height <= 0)
            return;

        var current = GetScreenCropRect(imgBounds);

        double w, h;
        if (current.Width / Math.Max(1.0, current.Height) > ratio)
        {
            // Current crop is wider than desired ratio: shrink width to match
            h = current.Height;
            w = h * ratio;
        }
        else
        {
            // Current crop is taller than desired ratio: shrink height to match
            w = current.Width;
            h = w / ratio;
        }

        if (w > imgBounds.Width)
        {
            w = imgBounds.Width;
            h = w / ratio;
        }
        if (h > imgBounds.Height)
        {
            h = imgBounds.Height;
            w = h * ratio;
        }

        w = Math.Max(MinSizePx, w);
        h = Math.Max(MinSizePx, h);

        var x = SafeClamp(current.Center.X - w / 2.0, imgBounds.Left, Math.Max(imgBounds.Left, imgBounds.Right - w));
        var y = SafeClamp(current.Center.Y - h / 2.0, imgBounds.Top, Math.Max(imgBounds.Top, imgBounds.Bottom - h));

        SetCropFromScreen(new Rect(x, y, w, h), imgBounds, animate: true);
    }

    /// <summary>Moves the frame by whole pixels of the picture (arrow keys; the window passes 10 with Shift).</summary>
    public void Nudge(int dx, int dy)
    {
        var size = PixelSize;
        if (SourceImage is null || size.Width <= 0 || size.Height <= 0)
            return;
        SetRect(CropMath.Nudge(NormalizedCropRect, dx, dy, size.Width, size.Height), animate: false);
    }

    /// <summary>The picture's place: as large as fits inside <see cref="ImagePadding"/>, centred.</summary>
    public Rect CalculateImageBounds()
    {
        if (SourceImage is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return default;

        var area = new Rect(Bounds.Size).Deflate(ImagePadding);
        if (area.Width <= 0 || area.Height <= 0)
            area = new Rect(Bounds.Size);
        var imgSize = SourceImage.Size;
        var scale = Math.Min(area.Width / imgSize.Width, area.Height / imgSize.Height);
        var w = imgSize.Width * scale;
        var h = imgSize.Height * scale;
        var x = area.X + (area.Width - w) / 2.0;
        var y = area.Y + (area.Height - h) / 2.0;

        return new Rect(x, y, w, h);
    }

    public Rect GetScreenCropRect(Rect imgBounds) => ToScreen(NormalizedCropRect, imgBounds);

    private static Rect ToScreen(Rect norm, Rect imgBounds) => new(
        imgBounds.X + norm.X * imgBounds.Width,
        imgBounds.Y + norm.Y * imgBounds.Height,
        Math.Max(MinSizePx, norm.Width * imgBounds.Width),
        Math.Max(MinSizePx, norm.Height * imgBounds.Height));

    private void SetCropFromScreen(Rect screenCrop, Rect imgBounds, bool animate = false)
    {
        if (imgBounds.Width <= 0 || imgBounds.Height <= 0)
            return;

        var x = SafeClamp(screenCrop.X, imgBounds.Left, Math.Max(imgBounds.Left, imgBounds.Right - MinSizePx));
        var y = SafeClamp(screenCrop.Y, imgBounds.Top, Math.Max(imgBounds.Top, imgBounds.Bottom - MinSizePx));
        var maxW = imgBounds.Right - x;
        var maxH = imgBounds.Bottom - y;
        var w = SafeClamp(screenCrop.Width, MinSizePx, Math.Max(MinSizePx, maxW));
        var h = SafeClamp(screenCrop.Height, MinSizePx, Math.Max(MinSizePx, maxH));

        SetRect(new Rect(
            (x - imgBounds.Left) / imgBounds.Width,
            (y - imgBounds.Top) / imgBounds.Height,
            w / imgBounds.Width,
            h / imgBounds.Height), animate);
    }

    private void SetRect(Rect normalized, bool animate)
    {
        _animateNextRect = animate;
        var same = NormalizedCropRect == normalized;
        NormalizedCropRect = normalized;
        _animateNextRect = false;
        if (same)
            CropRectChanged?.Invoke(normalized); // told again: the view model may have moved on (a new picture)
        InvalidateVisual();
    }

    /// <summary>Ends every running animation (tests capture the settled picture).</summary>
    internal void FinishAnimations()
    {
        _frame = Tween.Done(1);
        _appear = Tween.Done(1);
        _grid = Tween.Done(_grid.To);
        _handlePop = Tween.Done(_handlePop.To);
        InvalidateVisual();
    }

    /// <summary>The frame where it is drawn now (between two places while it eases).</summary>
    internal Rect ShownCropRect => Lerp(_frameFrom, NormalizedCropRect, EaseOutCubic(_frame.Value(Now())));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == NormalizedCropRectProperty)
        {
            var now = Now();
            var appearing = _appear.Value(now) < 1;
            if (_animateNextRect && PlayAnimations && !appearing && IsEffectivelyVisible)
            {
                // From where it is drawn now, even halfway through the last change.
                _frameFrom = Lerp(_frameFrom, change.GetOldValue<Rect>(), EaseOutCubic(_frame.Value(now)));
                _frame = Tween.Begin(0, 1, FrameSeconds);
                StartLoop();
            }
            else
            {
                _frameFrom = change.GetNewValue<Rect>();
                _frame = Tween.Done(1);
            }
            CropRectChanged?.Invoke(change.GetNewValue<Rect>());
        }
        else if (change.Property == SourceImageProperty)
        {
            _soft?.Dispose();
            _soft = Soften(change.GetNewValue<Bitmap?>());
            _frame = Tween.Done(1);
            _frameFrom = NormalizedCropRect;
            if (change.GetNewValue<Bitmap?>() is not null && PlayAnimations)
            {
                _appear = Tween.Begin(0, 1, AppearSeconds);
                StartLoop();
            }
            else
            {
                _appear = Tween.Done(1);
            }
        }
        else if (change.Property == PlayAnimationsProperty && !change.GetNewValue<bool>())
        {
            FinishAnimations();
        }
        base.OnPropertyChanged(change);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _soft?.Dispose();
        _soft = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _soft ??= Soften(SourceImage);
    }

    /// <summary>A copy at most 40 px across: drawn as large as the picture it is a soft blur, at no cost per frame.</summary>
    private static Bitmap? Soften(Bitmap? source)
    {
        if (source is null)
            return null;
        try
        {
            var size = source.PixelSize;
            var scale = 40.0 / Math.Max(size.Width, size.Height);
            if (scale >= 1)
                return null;
            var target = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
            return source.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // the sharp picture under the veil is fine too
        }
    }

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var now = Now();
        var bounds = new Rect(Bounds.Size);
        DrawStage(context, bounds);

        var imgBounds = CalculateImageBounds();
        if (SourceImage is null || imgBounds.Width <= 0 || imgBounds.Height <= 0)
            return;

        // A new picture settles in: a little smaller and transparent at first.
        var appear = EaseOutCubic(_appear.Value(now));
        var scale = 0.965 + 0.035 * appear;
        var center = imgBounds.Center;
        using var appearing = context.PushTransform(
            Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X, center.Y));
        using var fading = context.PushOpacity(appear);

        var source = new Rect(SourceImage.Size);
        context.DrawRectangle(StageBrush ?? Brushes.Gray, null, new RoundedRect(imgBounds), PictureShadow);

        // Outside the frame: the frosted copy under a veil of the page colour (milky in light, dim in dark).
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            if (_soft is { } soft)
                context.DrawImage(soft, new Rect(soft.Size), imgBounds);
            else
                context.DrawImage(SourceImage, source, imgBounds);
        }
        using (context.PushOpacity(IsDark ? 0.62 : 0.58))
            context.FillRectangle(StageBrush ?? Brushes.Gray, imgBounds);

        // Inside: the picture itself.
        var crop = ToScreen(ShownCropRect, imgBounds);
        using (context.PushClip(crop))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(SourceImage, source, imgBounds);

        DrawGrid(context, crop, _grid.Value(now));
        DrawCentreGuides(context, imgBounds);
        DrawFrame(context, crop, now);
        DrawSizeChip(context, crop, imgBounds, bounds);
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>The backdrop: the page colour dimmed, with two soft glows of the accent colour.</summary>
    private void DrawStage(DrawingContext context, Rect bounds)
    {
        if (StageBrush is { } stage)
            context.FillRectangle(stage, bounds);
        context.FillRectangle(new SolidColorBrush(Colors.Black, IsDark ? 0.28 : 0.06), bounds);

        if (AccentBrush is ISolidColorBrush { Color: var accent })
        {
            var strength = IsDark ? 0.16 : 0.22;
            Glow(context, accent, new Point(bounds.Width * 0.12, bounds.Height * 0.10), bounds.Width * 0.45, strength);
            Glow(context, accent, new Point(bounds.Width * 0.92, bounds.Height * 0.95), bounds.Width * 0.38, strength * 0.7);
        }
    }

    private static void Glow(DrawingContext context, Color color, Point center, double radius, double strength)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * strength), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)(255 * strength * 0.35), color.R, color.G, color.B), 0.55),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    private static void DrawGrid(DrawingContext context, Rect crop, double opacity)
    {
        if (opacity <= 0.01 || crop.Width < 50 || crop.Height < 50)
            return;
        using var fade = context.PushOpacity(opacity);
        for (var i = 1; i <= 2; i++)
        {
            var x = Math.Round(crop.X + crop.Width * i / 3.0) + 0.5;
            var y = Math.Round(crop.Y + crop.Height * i / 3.0) + 0.5;
            context.DrawLine(GridShadowPen, new Point(x + 1, crop.Top), new Point(x + 1, crop.Bottom));
            context.DrawLine(GridShadowPen, new Point(crop.Left, y + 1), new Point(crop.Right, y + 1));
            context.DrawLine(GridPen, new Point(x, crop.Top), new Point(x, crop.Bottom));
            context.DrawLine(GridPen, new Point(crop.Left, y), new Point(crop.Right, y));
        }
    }

    /// <summary>The picture's centre line the moved frame snapped to, across the whole picture.</summary>
    private void DrawCentreGuides(DrawingContext context, Rect picture)
    {
        if (_activeHandle != CropHandle.Body || !(_snappedX || _snappedY))
            return;
        var pen = new Pen(AccentBrush ?? Brushes.MediumPurple, 1, DashStyle.Dash);
        var x = Math.Round(picture.Center.X) + 0.5;
        var y = Math.Round(picture.Center.Y) + 0.5;
        if (_snappedX)
            context.DrawLine(pen, new Point(x, picture.Top), new Point(x, picture.Bottom));
        if (_snappedY)
            context.DrawLine(pen, new Point(picture.Left, y), new Point(picture.Right, y));
    }

    private void DrawFrame(DrawingContext context, Rect crop, long now)
    {
        var accent = AccentBrush ?? Brushes.MediumPurple;
        context.DrawRectangle(null, FrameShadowPen, crop);
        context.DrawRectangle(null, new Pen(accent, 2), crop);

        var pop = EaseOutBack(_handlePop.Value(now));
        foreach (var handle in new[] { CropHandle.TopLeft, CropHandle.TopRight, CropHandle.BottomRight, CropHandle.BottomLeft })
        {
            var grow = handle == _poppedHandle ? pop : 0;
            DrawDot(context, accent, HandlePoint(crop, handle), 6.5 + 2.5 * grow);
        }

        // Pills on the edges, where the frame is long enough for them to stay apart from the corners.
        foreach (var handle in new[] { CropHandle.Top, CropHandle.Right, CropHandle.Bottom, CropHandle.Left })
        {
            var horizontal = handle is CropHandle.Top or CropHandle.Bottom;
            if ((horizontal ? crop.Width : crop.Height) < 90)
                continue;
            var grow = handle == _poppedHandle ? pop : 0;
            var length = 22 + 8 * grow;
            const double thickness = 7;
            var c = HandlePoint(crop, handle);
            var rect = horizontal
                ? new Rect(c.X - length / 2, c.Y - thickness / 2, length, thickness)
                : new Rect(c.X - thickness / 2, c.Y - length / 2, thickness, length);
            context.DrawRectangle(HandleShadowBrush, null, new RoundedRect(rect.Translate(new Vector(0, 1)).Inflate(1.5), thickness));
            context.DrawRectangle(accent, HandleRingPen, new RoundedRect(rect, thickness / 2));
        }
    }

    private static void DrawDot(DrawingContext context, IBrush accent, Point center, double radius)
    {
        context.DrawEllipse(HandleShadowBrush, null, center + new Vector(0, 1), radius + 2, radius + 2);
        context.DrawEllipse(accent, HandleRingPen, center, radius, radius);
    }

    private static Point HandlePoint(Rect crop, CropHandle handle) => handle switch
    {
        CropHandle.TopLeft => crop.TopLeft,
        CropHandle.TopRight => crop.TopRight,
        CropHandle.BottomRight => crop.BottomRight,
        CropHandle.BottomLeft => crop.BottomLeft,
        CropHandle.Top => new Point(crop.Center.X, crop.Top),
        CropHandle.Bottom => new Point(crop.Center.X, crop.Bottom),
        CropHandle.Left => new Point(crop.Left, crop.Center.Y),
        CropHandle.Right => new Point(crop.Right, crop.Center.Y),
        _ => crop.Center,
    };

    /// <summary>How far below the picture the size chip may go (the toolbar starts further down).</summary>
    private const double ChipReach = 40;

    private void DrawSizeChip(DrawingContext context, Rect crop, Rect imgBounds, Rect bounds)
    {
        if (string.IsNullOrEmpty(SizeText))
            return;
        var foreground = ChipForeground ?? Brushes.Black;
        if (_chipText is null || _chipTextFor != SizeText || !ReferenceEquals(_chipTextBrush, foreground))
        {
            var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
            _chipText = new FormattedText(SizeText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11.5, foreground);
            _chipTextFor = SizeText;
            _chipTextBrush = foreground;
        }

        var chip = new Size(Math.Ceiling(_chipText.Width) + 20, Math.Ceiling(_chipText.Height) + 8);
        var area = new Rect(imgBounds.Left, imgBounds.Top, imgBounds.Width, imgBounds.Height + ChipReach)
            .Intersect(bounds.Deflate(6));
        var origin = CropMath.SizeChipOrigin(crop, chip, area);
        var pill = new RoundedRect(new Rect(origin, chip), chip.Height / 2);
        context.DrawRectangle(ChipBackground, null, pill, ChipShadow);
        context.DrawText(_chipText, new Point(origin.X + 10, origin.Y + 4));
    }

    // ───────────────────────── Motion ─────────────────────────

    /// <summary>
    /// The clock the animations read (<see cref="Stopwatch"/> ticks). The What's new clip steps it by hand, a frame at
    /// a time, so a frame that takes long to capture doesn't jump the animations ahead.
    /// </summary>
    internal static Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    private static long Now() => Clock();

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>Overshoots a little before settling: the "squishy" grow of a handle.</summary>
    private static double EaseOutBack(double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double s = 1.9;
        return 1 + (s + 1) * Math.Pow(t - 1, 3) + s * Math.Pow(t - 1, 2);
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    private void SetGrid(bool visible)
    {
        var target = visible ? 1 : 0;
        if (_grid.To == target)
            return;
        _grid = PlayAnimations ? _grid.Retarget(target, visible ? GridInSeconds : GridOutSeconds, Now()) : Tween.Done(target);
        StartLoop();
    }

    private void PopHandle(CropHandle handle)
    {
        if (handle is CropHandle.None or CropHandle.Body or CropHandle.CreateNew)
            handle = CropHandle.None;
        if (handle == _poppedHandle)
            return;
        _poppedHandle = handle;
        _handlePop = handle == CropHandle.None || !PlayAnimations
            ? Tween.Done(handle == CropHandle.None ? 0 : 1)
            : Tween.Begin(0, 1, HandleSeconds);
        StartLoop();
    }

    private void StartLoop()
    {
        InvalidateVisual();
        if (_looping || TopLevel.GetTopLevel(this) is not { } top)
            return;
        _looping = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        InvalidateVisual();
        var now = Now();
        if (_frame.IsRunning(now) || _appear.IsRunning(now) || _grid.IsRunning(now) || _handlePop.IsRunning(now))
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.RequestAnimationFrame(OnFrame);
                return;
            }
        }
        _looping = false;
    }

    /// <summary>A value easing from <see cref="From"/> to <see cref="To"/> over a time, read against the clock.</summary>
    private readonly record struct Tween(double From, double To, long Start, double Seconds)
    {
        public static Tween Done(double value) => new(value, value, 0, 0);

        public static Tween Begin(double from, double to, double seconds) => new(from, to, Now(), seconds);

        public double Progress(long now) => Seconds <= 0 ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(Start, now).TotalSeconds / Seconds, 0, 1);

        public double Value(long now) => From + (To - From) * Progress(now);

        public bool IsRunning(long now) => Progress(now) < 1;

        /// <summary>Heads for a new value from where it is now.</summary>
        public Tween Retarget(double to, double seconds, long now) => new(Value(now), to, now, seconds);
    }

    // ───────────────────────── Pointer ─────────────────────────

    private CropHandle HitTestHandle(Point pt, Rect crop, Rect imgBounds)
    {
        const double hitTol = 16.0;

        if (Distance(pt, crop.TopLeft) <= hitTol) return CropHandle.TopLeft;
        if (Distance(pt, crop.TopRight) <= hitTol) return CropHandle.TopRight;
        if (Distance(pt, crop.BottomLeft) <= hitTol) return CropHandle.BottomLeft;
        if (Distance(pt, crop.BottomRight) <= hitTol) return CropHandle.BottomRight;

        if (Math.Abs(pt.Y - crop.Top) <= hitTol && pt.X >= crop.Left && pt.X <= crop.Right) return CropHandle.Top;
        if (Math.Abs(pt.Y - crop.Bottom) <= hitTol && pt.X >= crop.Left && pt.X <= crop.Right) return CropHandle.Bottom;
        if (Math.Abs(pt.X - crop.Left) <= hitTol && pt.Y >= crop.Top && pt.Y <= crop.Bottom) return CropHandle.Left;
        if (Math.Abs(pt.X - crop.Right) <= hitTol && pt.Y >= crop.Top && pt.Y <= crop.Bottom) return CropHandle.Right;

        if (crop.Contains(pt)) return CropHandle.Body;
        if (imgBounds.Contains(pt)) return CropHandle.CreateNew;
        return CropHandle.None;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverHandle = CropHandle.None;
        if (_activeHandle == CropHandle.None)
            PopHandle(CropHandle.None);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var imgBounds = CalculateImageBounds();
        if (imgBounds.Width <= 0 || imgBounds.Height <= 0) return;

        var pt = e.GetPosition(this);
        var crop = GetScreenCropRect(imgBounds);

        if (_activeHandle == CropHandle.None)
        {
            var h = HitTestHandle(pt, crop, imgBounds);
            Cursor = h switch
            {
                CropHandle.TopLeft or CropHandle.BottomRight => TopLeftCursor,
                CropHandle.TopRight or CropHandle.BottomLeft => TopRightCursor,
                CropHandle.Top or CropHandle.Bottom => SizeNorthSouthCursor,
                CropHandle.Left or CropHandle.Right => SizeWestEastCursor,
                CropHandle.Body => SizeAllCursor,
                CropHandle.CreateNew => CrossCursor,
                _ => ArrowCursor,
            };
            _hoverHandle = h;
            PopHandle(h);
            return;
        }

        // Active manual selection drag
        if (_activeHandle == CropHandle.CreateNew)
        {
            var p1 = new Point(
                SafeClamp(_dragStartPoint.X, imgBounds.Left, imgBounds.Right),
                SafeClamp(_dragStartPoint.Y, imgBounds.Top, imgBounds.Bottom));
            var p2 = new Point(
                SafeClamp(pt.X, imgBounds.Left, imgBounds.Right),
                SafeClamp(pt.Y, imgBounds.Top, imgBounds.Bottom));

            if (AspectRatio <= 0)
            {
                // Free-form box selection
                var l = Math.Min(p1.X, p2.X);
                var t = Math.Min(p1.Y, p2.Y);
                var w = Math.Max(MinSizePx, Math.Abs(p2.X - p1.X));
                var h = Math.Max(MinSizePx, Math.Abs(p2.Y - p1.Y));
                SetCropFromScreen(new Rect(l, t, w, h), imgBounds);
            }
            else
            {
                // Constrained aspect ratio selection
                var R = AspectRatio;
                var dx = p2.X - p1.X;
                var dy = p2.Y - p1.Y;
                var signX = dx >= 0 ? 1 : -1;
                var signY = dy >= 0 ? 1 : -1;

                var rawW = Math.Abs(dx);
                var rawH = Math.Abs(dy);

                var w = Math.Max(rawW, rawH * R);
                var h = w / R;

                var maxW = signX > 0 ? (imgBounds.Right - p1.X) : (p1.X - imgBounds.Left);
                var maxH = signY > 0 ? (imgBounds.Bottom - p1.Y) : (p1.Y - imgBounds.Top);

                if (w > maxW) { w = maxW; h = w / R; }
                if (h > maxH) { h = maxH; w = h * R; }

                w = Math.Max(MinSizePx, w);
                h = Math.Max(MinSizePx, h);

                var left = signX > 0 ? p1.X : p1.X - w;
                var top = signY > 0 ? p1.Y : p1.Y - h;

                SetCropFromScreen(new Rect(left, top, w, h), imgBounds);
            }
            return;
        }

        // Active panning/moving the selection box over the image
        if (_activeHandle == CropHandle.Body)
        {
            var deltaX = pt.X - _dragStartPoint.X;
            var deltaY = pt.Y - _dragStartPoint.Y;

            var newX = SafeClamp(_dragStartCrop.X + deltaX, imgBounds.Left, Math.Max(imgBounds.Left, imgBounds.Right - _dragStartCrop.Width));
            var newY = SafeClamp(_dragStartCrop.Y + deltaY, imgBounds.Top, Math.Max(imgBounds.Top, imgBounds.Bottom - _dragStartCrop.Height));
            (newX, _snappedX) = CropMath.SnapToCentre(newX, _dragStartCrop.Width, imgBounds.Left, imgBounds.Right);
            (newY, _snappedY) = CropMath.SnapToCentre(newY, _dragStartCrop.Height, imgBounds.Top, imgBounds.Bottom);

            SetCropFromScreen(new Rect(newX, newY, _dragStartCrop.Width, _dragStartCrop.Height), imgBounds);
            return;
        }

        // Active handle resizing
        var dxRes = pt.X - _dragStartPoint.X;
        var dyRes = pt.Y - _dragStartPoint.Y;

        if (AspectRatio <= 0)
        {
            // Free-form handle resizing
            var left = _dragStartCrop.Left;
            var top = _dragStartCrop.Top;
            var right = _dragStartCrop.Right;
            var bottom = _dragStartCrop.Bottom;

            switch (_activeHandle)
            {
                case CropHandle.TopLeft:
                    left = SafeClamp(_dragStartCrop.Left + dxRes, imgBounds.Left, right - MinSizePx);
                    top = SafeClamp(_dragStartCrop.Top + dyRes, imgBounds.Top, bottom - MinSizePx);
                    break;
                case CropHandle.TopRight:
                    right = SafeClamp(_dragStartCrop.Right + dxRes, left + MinSizePx, imgBounds.Right);
                    top = SafeClamp(_dragStartCrop.Top + dyRes, imgBounds.Top, bottom - MinSizePx);
                    break;
                case CropHandle.BottomLeft:
                    left = SafeClamp(_dragStartCrop.Left + dxRes, imgBounds.Left, right - MinSizePx);
                    bottom = SafeClamp(_dragStartCrop.Bottom + dyRes, top + MinSizePx, imgBounds.Bottom);
                    break;
                case CropHandle.BottomRight:
                    right = SafeClamp(_dragStartCrop.Right + dxRes, left + MinSizePx, imgBounds.Right);
                    bottom = SafeClamp(_dragStartCrop.Bottom + dyRes, top + MinSizePx, imgBounds.Bottom);
                    break;
                case CropHandle.Top:
                    top = SafeClamp(_dragStartCrop.Top + dyRes, imgBounds.Top, bottom - MinSizePx);
                    break;
                case CropHandle.Bottom:
                    bottom = SafeClamp(_dragStartCrop.Bottom + dyRes, top + MinSizePx, imgBounds.Bottom);
                    break;
                case CropHandle.Left:
                    left = SafeClamp(_dragStartCrop.Left + dxRes, imgBounds.Left, right - MinSizePx);
                    break;
                case CropHandle.Right:
                    right = SafeClamp(_dragStartCrop.Right + dxRes, left + MinSizePx, imgBounds.Right);
                    break;
            }

            SetCropFromScreen(new Rect(left, top, right - left, bottom - top), imgBounds);
        }
        else
        {
            // Proportional aspect ratio handle resizing
            var R = AspectRatio;

            switch (_activeHandle)
            {
                case CropHandle.BottomRight:
                    {
                        var anchor = _dragStartCrop.TopLeft;
                        var delta = (dxRes + dyRes * R) / 2.0;
                        var maxW = Math.Min(imgBounds.Right - anchor.X, (imgBounds.Bottom - anchor.Y) * R);
                        var w = SafeClamp(_dragStartCrop.Width + delta, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        SetCropFromScreen(new Rect(anchor.X, anchor.Y, w, h), imgBounds);
                        break;
                    }
                case CropHandle.BottomLeft:
                    {
                        var anchorX = _dragStartCrop.Right;
                        var anchorY = _dragStartCrop.Top;
                        var delta = (-dxRes + dyRes * R) / 2.0;
                        var maxW = Math.Min(anchorX - imgBounds.Left, (imgBounds.Bottom - anchorY) * R);
                        var w = SafeClamp(_dragStartCrop.Width + delta, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        SetCropFromScreen(new Rect(anchorX - w, anchorY, w, h), imgBounds);
                        break;
                    }
                case CropHandle.TopRight:
                    {
                        var anchorX = _dragStartCrop.Left;
                        var anchorY = _dragStartCrop.Bottom;
                        var delta = (dxRes - dyRes * R) / 2.0;
                        var maxW = Math.Min(imgBounds.Right - anchorX, (anchorY - imgBounds.Top) * R);
                        var w = SafeClamp(_dragStartCrop.Width + delta, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        SetCropFromScreen(new Rect(anchorX, anchorY - h, w, h), imgBounds);
                        break;
                    }
                case CropHandle.TopLeft:
                    {
                        var anchorX = _dragStartCrop.Right;
                        var anchorY = _dragStartCrop.Bottom;
                        var delta = (-dxRes - dyRes * R) / 2.0;
                        var maxW = Math.Min(anchorX - imgBounds.Left, (anchorY - imgBounds.Top) * R);
                        var w = SafeClamp(_dragStartCrop.Width + delta, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        SetCropFromScreen(new Rect(anchorX - w, anchorY - h, w, h), imgBounds);
                        break;
                    }
                case CropHandle.Right:
                    {
                        var anchorLeft = _dragStartCrop.Left;
                        var maxW = Math.Min(imgBounds.Right - anchorLeft, imgBounds.Height * R);
                        var w = SafeClamp(_dragStartCrop.Width + dxRes, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        var y = SafeClamp(_dragStartCrop.Center.Y - h / 2.0, imgBounds.Top, imgBounds.Bottom - h);
                        SetCropFromScreen(new Rect(anchorLeft, y, w, h), imgBounds);
                        break;
                    }
                case CropHandle.Left:
                    {
                        var anchorRight = _dragStartCrop.Right;
                        var maxW = Math.Min(anchorRight - imgBounds.Left, imgBounds.Height * R);
                        var w = SafeClamp(_dragStartCrop.Width - dxRes, MinSizePx, Math.Max(MinSizePx, maxW));
                        var h = w / R;
                        var y = SafeClamp(_dragStartCrop.Center.Y - h / 2.0, imgBounds.Top, imgBounds.Bottom - h);
                        SetCropFromScreen(new Rect(anchorRight - w, y, w, h), imgBounds);
                        break;
                    }
                case CropHandle.Bottom:
                    {
                        var anchorTop = _dragStartCrop.Top;
                        var maxH = Math.Min(imgBounds.Bottom - anchorTop, imgBounds.Width / R);
                        var h = SafeClamp(_dragStartCrop.Height + dyRes, MinSizePx, Math.Max(MinSizePx, maxH));
                        var w = h * R;
                        var x = SafeClamp(_dragStartCrop.Center.X - w / 2.0, imgBounds.Left, imgBounds.Right - w);
                        SetCropFromScreen(new Rect(x, anchorTop, w, h), imgBounds);
                        break;
                    }
                case CropHandle.Top:
                    {
                        var anchorBottom = _dragStartCrop.Bottom;
                        var maxH = Math.Min(anchorBottom - imgBounds.Top, imgBounds.Width / R);
                        var h = SafeClamp(_dragStartCrop.Height - dyRes, MinSizePx, Math.Max(MinSizePx, maxH));
                        var w = h * R;
                        var x = SafeClamp(_dragStartCrop.Center.X - w / 2.0, imgBounds.Left, imgBounds.Right - w);
                        SetCropFromScreen(new Rect(x, anchorBottom - h, w, h), imgBounds);
                        break;
                    }
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var imgBounds = CalculateImageBounds();
        if (imgBounds.Width <= 0 || imgBounds.Height <= 0) return;

        var pt = e.GetPosition(this);
        var crop = GetScreenCropRect(imgBounds);
        _activeHandle = HitTestHandle(pt, crop, imgBounds);

        if (_activeHandle != CropHandle.None)
        {
            _dragStartPoint = pt;
            _dragStartCrop = crop;
            _snappedX = _snappedY = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            PopHandle(_activeHandle);
            SetGrid(true);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeHandle != CropHandle.None)
        {
            _activeHandle = CropHandle.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            SetGrid(false);
            PopHandle(_hoverHandle);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_activeHandle != CropHandle.None)
        {
            _activeHandle = CropHandle.None;
            SetGrid(false);
            PopHandle(CropHandle.None);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var imgBounds = CalculateImageBounds();
        if (imgBounds.Width <= 0 || imgBounds.Height <= 0) return;

        var crop = GetScreenCropRect(imgBounds);
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001) return;

        var zoomFactor = delta > 0 ? 1.05 : 0.95;
        double newW, newH;

        if (AspectRatio > 0)
        {
            newW = crop.Width * zoomFactor;
            newH = newW / AspectRatio;
            if (newW > imgBounds.Width || newH > imgBounds.Height)
            {
                newW = Math.Min(imgBounds.Width, imgBounds.Height * AspectRatio);
                newH = newW / AspectRatio;
            }
        }
        else
        {
            newW = SafeClamp(crop.Width * zoomFactor, MinSizePx, imgBounds.Width);
            newH = SafeClamp(crop.Height * zoomFactor, MinSizePx, imgBounds.Height);
        }

        newW = Math.Max(1, newW);
        newH = Math.Max(1, newH);

        var newX = SafeClamp(crop.Center.X - newW / 2.0, imgBounds.Left, imgBounds.Right - newW);
        var newY = SafeClamp(crop.Center.Y - newH / 2.0, imgBounds.Top, imgBounds.Bottom - newH);

        SetCropFromScreen(new Rect(newX, newY, newW, newH), imgBounds);
        e.Handled = true;
    }
}
