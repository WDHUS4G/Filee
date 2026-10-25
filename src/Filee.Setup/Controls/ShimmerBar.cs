// A thin rounded progress bar: the fill eases towards the value and a soft highlight sweeps across it while work goes
// on (static without animations).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.Setup.Controls;

public sealed class ShimmerBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ShimmerBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<ShimmerBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ShimmerBar, IBrush?>(nameof(Fill));

    private readonly FrameLoop _loop;
    private double _shown;
    private double _sweep;

    static ShimmerBar()
    {
        AffectsRender<ShimmerBar>(TrackProperty, FillProperty);
    }

    public ShimmerBar()
    {
        Height = 6;
        _loop = new FrameLoop(this, elapsed =>
        {
            _shown += (Value - _shown) * 0.12;
            _sweep = elapsed.TotalSeconds / 1.6 % 1;
            InvalidateVisual();
            return true;
        });
    }

    /// <summary>0 to 100.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            if (!SetupMotion.Enabled)
                _shown = Value;
            InvalidateVisual();
            _loop.Kick();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        context.DrawRectangle(Track, null, bounds, radius, radius);
        var value = Math.Clamp(SetupMotion.Enabled ? _shown : Value, 0, 100);
        if (value <= 0.1)
            return;
        var fill = new Rect(0, 0, Math.Max(bounds.Height, bounds.Width * value / 100), bounds.Height);
        context.DrawRectangle(Fill, null, fill, radius, radius);
        if (!SetupMotion.Enabled || value >= 100)
            return;
        // The highlight: a short white gradient band moving left to right over the fill.
        var band = Math.Max(40, fill.Width * 0.25);
        var x = -band + (fill.Width + band) * _sweep;
        var shine = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(x + band, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(110, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        context.DrawRectangle(shine, null, fill, radius, radius);
    }
}
