// Filee's logo (src/Filee.App/Assets/Icons/filee.svg) drawn from its paths, so it can assemble itself: the six slices
// spring in one after the other while the ring turns into place, then the file in the middle drops in. Without
// animations (or after 1.4 s) it is simply the logo.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Filee.Setup.Controls;

public sealed class DonutLogo : Control
{
    /// <summary>Plays the entrance when shown; off draws the finished logo.</summary>
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<DonutLogo, bool>(nameof(Animate), true);

    private const double ViewBox = 512;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1400);

    // filee.svg: the slices (path, colour, offset of the "pulled out" slice) and the file in the middle.
    private static readonly (Geometry Path, IBrush Fill, Vector Offset)[] Slices =
    [
        (Geometry.Parse("M154.61 65.27 A216 216 0 0 1 357.39 65.27 L304.82 164.17 A104 104 0 0 0 207.18 164.17 Z"), Brush("#7F77DD"), default),
        (Geometry.Parse("M373.66 67.74 A222 222 0 0 1 477.86 248.23 L359.94 252.36 A104 104 0 0 0 311.12 167.81 Z"), Brush("#1D9E75"), new Vector(13.86, -8)),
        (Geometry.Parse("M471.87 263.56 A216 216 0 0 1 370.48 439.17 L311.12 344.19 A104 104 0 0 0 359.94 259.64 Z"), Brush("#7F77DD"), default),
        (Geometry.Parse("M357.39 446.73 A216 216 0 0 1 154.61 446.73 L207.18 347.83 A104 104 0 0 0 304.82 347.83 Z"), Brush("#6A62C9"), default),
        (Geometry.Parse("M141.52 439.17 A216 216 0 0 1 40.13 263.56 L152.06 259.64 A104 104 0 0 0 200.88 344.19 Z"), Brush("#7F77DD"), default),
        (Geometry.Parse("M40.13 248.44 A216 216 0 0 1 141.52 72.83 L200.88 167.81 A104 104 0 0 0 152.06 252.36 Z"), Brush("#6A62C9"), default),
    ];

    private static readonly Geometry FileBody = Geometry.Parse("M222 212 h44 l24 24 v64 h-68 z");
    private static readonly Geometry FileCorner = Geometry.Parse("M266 212 v24 h24 z");
    private static readonly IBrush FileBodyBrush = Brush("#3C3489");
    private static readonly IBrush FileCornerBrush = Brush("#AFA9EC");

    private readonly FrameLoop _loop;
    private double _progress;

    static DonutLogo()
    {
        AffectsRender<DonutLogo>(AnimateProperty);
    }

    public DonutLogo()
    {
        // The logo is never mirrored, also in Arabic.
        FlowDirection = FlowDirection.LeftToRight;
        _progress = SetupMotion.Enabled ? 0 : 1;
        _loop = new FrameLoop(this, elapsed =>
        {
            _progress = Math.Min(1, elapsed / Duration);
            InvalidateVisual();
            return _progress < 1;
        });
    }

    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    /// <summary>Plays the entrance again.</summary>
    public void Replay()
    {
        if (!SetupMotion.Enabled || !Animate)
            return;
        _progress = 0;
        _loop.Restart();
    }

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 96 : availableSize.Height);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var progress = Animate && SetupMotion.Enabled ? _progress : 1;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
            return;
        var scale = side / ViewBox;
        var origin = new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        var center = new Point(ViewBox / 2, ViewBox / 2);
        // The ring turns a third of a turn into place while the slices arrive.
        var ring = EaseOut(Math.Min(1, progress / 0.75));
        var rotation = Matrix.CreateTranslation(-center.X, -center.Y)
                       * Matrix.CreateRotation((1 - ring) * -2.1)
                       * Matrix.CreateTranslation(center.X, center.Y);
        var view = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y);

        for (var i = 0; i < Slices.Length; i++)
        {
            var (path, fill, offset) = Slices[i];
            var local = Math.Clamp((progress - i * 0.07) / 0.45, 0, 1);
            if (local <= 0)
                continue;
            var spring = BackOut(local);
            // Each slice grows from the ring's centre line outwards, with a little overshoot.
            var grow = Matrix.CreateTranslation(-center.X, -center.Y)
                       * Matrix.CreateScale(0.55 + 0.45 * spring, 0.55 + 0.45 * spring)
                       * Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y);
            using (context.PushTransform(grow * rotation * view))
            using (context.PushOpacity(Math.Min(1, local * 1.6)))
                context.DrawGeometry(fill, null, path);
        }

        var file = Math.Clamp((progress - 0.55) / 0.4, 0, 1);
        if (file > 0)
        {
            var drop = (1 - BackOut(file)) * -40;
            using (context.PushTransform(Matrix.CreateTranslation(0, drop) * view))
            using (context.PushOpacity(file))
            {
                context.DrawGeometry(FileBodyBrush, null, FileBody);
                context.DrawGeometry(FileCornerBrush, null, FileCorner);
            }
        }
    }

    internal static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    internal static double BackOut(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
