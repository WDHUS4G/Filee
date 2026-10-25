// The "done" moment: an accent circle pops in, the check mark draws itself and a few soft accent dots burst outwards
// and fade (the finished check mark without animations).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.Setup.Controls;

public sealed class CheckBurst : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<CheckBurst, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> MarkProperty =
        AvaloniaProperty.Register<CheckBurst, IBrush?>(nameof(Mark));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1100);
    private readonly FrameLoop _loop;
    private double _progress;

    static CheckBurst()
    {
        AffectsRender<CheckBurst>(FillProperty, MarkProperty);
    }

    public CheckBurst()
    {
        _progress = SetupMotion.Enabled ? 0 : 1;
        _loop = new FrameLoop(this, elapsed =>
        {
            _progress = Math.Min(1, elapsed / Duration);
            InvalidateVisual();
            return _progress < 1;
        });
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(88, 88);

    public override void Render(DrawingContext context)
    {
        var t = SetupMotion.Enabled ? _progress : 1;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = Math.Min(Bounds.Width, Bounds.Height) * 0.30;

        // Dots fly out between 0.15 and 0.75 and fade.
        var dots = Math.Clamp((t - 0.15) / 0.6, 0, 1);
        if (dots is > 0 and < 1 && Fill is ISolidColorBrush solid)
        {
            var brush = new SolidColorBrush(solid.Color, 1 - dots);
            for (var i = 0; i < 10; i++)
            {
                var angle = i * Math.PI / 5 + 0.3;
                var distance = r * (1.05 + 0.75 * DonutLogo.EaseOut(dots));
                var size = (i % 2 == 0 ? 3.4 : 2.4) * (1 - 0.5 * dots);
                context.DrawEllipse(brush, null, center + new Vector(Math.Cos(angle), Math.Sin(angle)) * distance, size, size);
            }
        }

        var pop = DonutLogo.BackOut(Math.Clamp(t / 0.4, 0, 1));
        if (pop > 0)
            context.DrawEllipse(Fill, null, center, r * pop, r * pop);

        // The check mark: two strokes, drawn in order.
        var draw = Math.Clamp((t - 0.3) / 0.45, 0, 1);
        if (draw <= 0)
            return;
        var a = center + new Vector(-r * 0.42, r * 0.02);
        var b = center + new Vector(-r * 0.12, r * 0.32);
        var c = center + new Vector(r * 0.45, -r * 0.30);
        var first = Distance(a, b);
        var second = Distance(b, c);
        var length = (first + second) * DonutLogo.EaseOut(draw);
        var pen = new Pen(Mark, r * 0.16, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(a, false);
            if (length <= first)
            {
                g.LineTo(a + (b - a) * (length / first));
            }
            else
            {
                g.LineTo(b);
                g.LineTo(b + (c - b) * ((length - first) / second));
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
