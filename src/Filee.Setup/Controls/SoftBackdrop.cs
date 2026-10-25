// The soft background of Setup's card: three blurred blobs in the accent tints that drift slowly (about 30 fps, only
// while the window is visible and animations are on; otherwise they stay where they are).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.Setup.Controls;

public sealed class SoftBackdrop : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<SoftBackdrop>();

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<SoftBackdrop, Color>(nameof(Accent), Color.Parse("#7F77DD"));

    public static readonly StyledProperty<Color> SecondProperty =
        AvaloniaProperty.Register<SoftBackdrop, Color>(nameof(Second), Color.Parse("#1D9E75"));

    /// <summary>How strong the blobs are (lower in dark mode).</summary>
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<SoftBackdrop, double>(nameof(Intensity), 0.30);

    private readonly FrameLoop _loop;
    private double _time = 1.7;
    private TimeSpan _lastDrawn = TimeSpan.FromSeconds(-1);

    static SoftBackdrop()
    {
        AffectsRender<SoftBackdrop>(BackgroundProperty, AccentProperty, SecondProperty, IntensityProperty);
    }

    public SoftBackdrop()
    {
        IsHitTestVisible = false;
        _loop = new FrameLoop(this, elapsed =>
        {
            // About 30 fps is plenty for blobs that move a few pixels a second.
            if (elapsed - _lastDrawn < TimeSpan.FromMilliseconds(33))
                return true;
            _lastDrawn = elapsed;
            _time = 1.7 + elapsed.TotalSeconds;
            InvalidateVisual();
            return true;
        });
    }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Color Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Second { get => GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public double Intensity { get => GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);
        var w = bounds.Width;
        var h = bounds.Height;
        var t = _time;
        Blob(context, Accent, w * (0.18 + 0.06 * Math.Sin(t * 0.21)), h * (0.22 + 0.08 * Math.Cos(t * 0.17)), w * 0.42, Intensity);
        Blob(context, Second, w * (0.86 + 0.05 * Math.Cos(t * 0.19 + 1.3)), h * (0.80 + 0.07 * Math.Sin(t * 0.23)), w * 0.36, Intensity * 0.75);
        Blob(context, Accent, w * (0.70 + 0.07 * Math.Sin(t * 0.13 + 2.1)), h * (0.12 + 0.06 * Math.Cos(t * 0.29 + 0.7)), w * 0.28, Intensity * 0.6);
    }

    private static void Blob(DrawingContext context, Color color, double x, double y, double radius, double strength)
    {
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * strength), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)(255 * strength * 0.35), color.R, color.G, color.B), 0.55),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }
}
