// The crop overlay's aspect dial: a small donut whose slices are the ratios (free, the picture's own, 1:1, 5:4, 4:3,
// 3:2, 16:9), each with a tiny outline of its shape, and a hub that swaps portrait and landscape. The slices are real
// buttons (focus, keyboard, screen readers) that only answer inside their wedge. AspectGlyph is the dial in miniature
// on the toolbar button that opens it.

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Filee.App.Controls;

/// <summary>Lays out <see cref="RingSegment"/> children as a ring, clockwise from the top, and any other child in the hub.</summary>
public sealed class AspectDial : Panel
{
    public static readonly StyledProperty<double> OuterRadiusProperty =
        AvaloniaProperty.Register<AspectDial, double>(nameof(OuterRadius), 118);

    public static readonly StyledProperty<double> InnerRadiusProperty =
        AvaloniaProperty.Register<AspectDial, double>(nameof(InnerRadius), 52);

    /// <summary>The theme's softness: slices are rounded by half of it, as on the donut.</summary>
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<AspectDial, CornerRadius>(nameof(CornerRadius), new CornerRadius(16));

    public static readonly StyledProperty<string?> SelectedModeProperty =
        AvaloniaProperty.Register<AspectDial, string?>(nameof(SelectedMode));

    public static readonly StyledProperty<bool> IsPortraitProperty =
        AvaloniaProperty.Register<AspectDial, bool>(nameof(IsPortrait));

    /// <summary>Width / height of the picture: the outline on the "original" slice.</summary>
    public static readonly StyledProperty<double> OriginalRatioProperty =
        AvaloniaProperty.Register<AspectDial, double>(nameof(OriginalRatio));

    public double OuterRadius { get => GetValue(OuterRadiusProperty); set => SetValue(OuterRadiusProperty, value); }
    public double InnerRadius { get => GetValue(InnerRadiusProperty); set => SetValue(InnerRadiusProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public string? SelectedMode { get => GetValue(SelectedModeProperty); set => SetValue(SelectedModeProperty, value); }
    public bool IsPortrait { get => GetValue(IsPortraitProperty); set => SetValue(IsPortraitProperty, value); }
    public double OriginalRatio { get => GetValue(OriginalRatioProperty); set => SetValue(OriginalRatioProperty, value); }

    /// <summary>Gap between slices, as on the donut.</summary>
    public const double Gap = 4;

    static AspectDial()
    {
        AffectsArrange<AspectDial>(OuterRadiusProperty, InnerRadiusProperty, CornerRadiusProperty, SelectedModeProperty,
            IsPortraitProperty, OriginalRatioProperty);
        AffectsMeasure<AspectDial>(OuterRadiusProperty);
    }

    public IEnumerable<RingSegment> Segments => Children.OfType<RingSegment>();

    /// <summary>A donut slice (<see cref="DonutGeometry.Wedge"/>) as a geometry to draw.</summary>
    internal static StreamGeometry WedgeGeometry(Point center, double inner, double outer, double mid, double span, double gap, double corner)
    {
        var outline = DonutGeometry.Wedge(center, inner, outer, mid, span, gap, corner);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(outline.Start, true);
        foreach (var step in outline.Steps)
        {
            if (step.IsArc)
                ctx.ArcTo(step.To, new Size(step.ArcRadius, step.ArcRadius), 0, step.IsLargeArc,
                    step.Clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            else
                ctx.LineTo(step.To);
        }
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>Width and height of the dial, with room for a hovered slice moving out.</summary>
    public double Side => OuterRadius * 2 + RingSegment.PopOut * 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Side;
        foreach (var child in Children)
            child.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var segments = Segments.ToList();
        var center = new Point(finalSize.Width / 2, finalSize.Height / 2);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            segment.Place(center, i, segments.Count, InnerRadius, OuterRadius, Math.Clamp(CornerRadius.TopLeft, 0, 28) * 0.5);
            segment.Show(SelectedMode == segment.Mode, IsPortrait, OriginalRatio);
            segment.Arrange(new Rect(finalSize));
        }

        var hub = (InnerRadius - Gap) * 2;
        foreach (var child in Children.Where(c => c is not RingSegment))
            child.Arrange(new Rect(center.X - hub / 2, center.Y - hub / 2, hub, hub));
        return finalSize;
    }
}

/// <summary>One ratio of the <see cref="AspectDial"/>: a wedge with the ratio's outline and name.</summary>
public sealed class RingSegment : Button, ICustomHitTest
{
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<RingSegment, string>(nameof(Mode), "free");

    /// <summary>0 at rest, 1 hovered: the slice moves out a little and lights up (eased by a transition).</summary>
    public static readonly StyledProperty<double> PopProperty =
        AvaloniaProperty.Register<RingSegment, double>(nameof(Pop));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<RingSegment, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> AccentSoftBrushProperty =
        AvaloniaProperty.Register<RingSegment, IBrush?>(nameof(AccentSoftBrush));

    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<RingSegment, IBrush?>(nameof(OnAccentBrush), Brushes.White);

    public string Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double Pop { get => GetValue(PopProperty); set => SetValue(PopProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? AccentSoftBrush { get => GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }

    /// <summary>How far a hovered slice moves out.</summary>
    public const double PopOut = 5;

    private Point _center;
    private int _index;
    private int _count = 1;
    private double _inner;
    private double _outer;
    private double _corner;
    private Geometry? _wedge;
    private bool _portrait;
    private double _originalRatio;

    /// <summary>Whether this slice's ratio is the one in use.</summary>
    public bool IsSelected { get; private set; }

    /// <summary>The name drawn on the slice: the content, or the ratio's numbers ("3:4" in portrait).</summary>
    public string Label => Content as string ?? CropMath.Numbers(Mode, _portrait);

    static RingSegment()
    {
        AffectsRender<RingSegment>(PopProperty, AccentBrushProperty, AccentSoftBrushProperty, OnAccentBrushProperty,
            ModeProperty, BackgroundProperty, ForegroundProperty, BorderBrushProperty);
        FocusAdornerProperty.OverrideDefaultValue<RingSegment>(null);
    }

    public RingSegment()
    {
        ClipToBounds = false;
        Template = null; // drawn by Render: a button's template would cover the whole dial
    }

    internal void Place(Point center, int index, int count, double inner, double outer, double corner)
    {
        if (_center == center && _index == index && _count == count && _inner == inner && _outer == outer && _corner == corner && _wedge is not null)
            return;
        (_center, _index, _count, _inner, _outer, _corner) = (center, index, count, inner, outer, corner);
        _wedge = BuildWedge();
        InvalidateVisual();
    }

    internal void Show(bool selected, bool portrait, double originalRatio)
    {
        var label = Label;
        IsSelected = selected;
        _portrait = portrait;
        _originalRatio = originalRatio;
        PseudoClasses.Set(":selected", selected);
        if (label != Label || AutomationProperties.GetName(this) is null or "")
            AutomationProperties.SetName(this, Label);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty || change.Property == ModeProperty)
            AutomationProperties.SetName(this, Label);
    }

    private double Mid => DonutGeometry.MidAngle(_index, _count);

    private Geometry BuildWedge() =>
        AspectDial.WedgeGeometry(_center, _inner, _outer, Mid, DonutGeometry.Span(_count), AspectDial.Gap, _corner);

    /// <summary>Only the wedge answers the pointer, so neighbouring slices (sharing one square) each get their own.</summary>
    public bool HitTest(Point point) => _wedge?.FillContains(point) == true;

    public override void Render(DrawingContext context)
    {
        if (_wedge is null)
            return;
        var pop = Math.Clamp(Pop, -0.2, 1.3);
        var offset = DonutGeometry.PointAt(default, pop * PopOut, Mid);
        using var moved = context.PushTransform(Matrix.CreateTranslation(offset.X, offset.Y));

        var pen = BorderBrush is { } border ? new Pen(border, 1) : null;
        context.DrawGeometry(IsSelected ? AccentBrush : Background, pen, _wedge);
        if (!IsSelected && pop > 0.01 && AccentSoftBrush is { } soft)
        {
            using var lit = context.PushOpacity(Math.Clamp(pop, 0, 1));
            context.DrawGeometry(soft, null, _wedge);
        }
        if (IsFocused && PseudoClasses.Contains(":focus-visible") && AccentBrush is { } accent)
            context.DrawGeometry(null, new Pen(accent, 2.5), _wedge);

        var ink = IsSelected ? OnAccentBrush : Foreground;
        var labelCenter = DonutGeometry.PointAt(_center, (_inner + _outer) / 2, Mid);
        DrawShape(context, ink, new Point(labelCenter.X, labelCenter.Y - 9));

        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var width = LabelWidth(_inner, _outer, _count);
        var direction = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        FormattedText Text(double size) => new(Label, CultureInfo.CurrentUICulture, direction, typeface, size, ink)
        {
            MaxTextWidth = width,
            MaxLineCount = 2,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var text = Text(12);
        var longest = Label.Split(' ').Max(word => new FormattedText(word, CultureInfo.CurrentUICulture, direction, typeface, 12, ink).Width);
        if (longest > width)
            text = Text(Math.Max(MinLabelSize, 12 * width / longest));
        context.DrawText(text, new Point(labelCenter.X - width / 2, labelCenter.Y + 2));
    }

    /// <summary>
    /// The smallest size a long one-word label shrinks to before it breaks. Ru's "Произвольные" needs 9.4 with seven
    /// slices on a desktop and 8.7 on CI's runners, whose fonts differ, so there's room below that.
    /// </summary>
    internal const double MinLabelSize = 8;

    /// <summary>As wide as the slice is a little outside its middle; a long word gets a smaller size before it would break.</summary>
    internal static double LabelWidth(double inner, double outer, int count) =>
        2 * ((inner + outer) / 2 + 6) * Math.Sin(Math.Min(Math.PI / 2, DonutGeometry.Span(count) / 2)) - 12;

    /// <summary>A small outline of the ratio over its name; free is a dashed square.</summary>
    private void DrawShape(DrawingContext context, IBrush? ink, Point center)
    {
        if (ink is null)
            return;
        var ratio = Mode == "original" ? _originalRatio : CropMath.Ratio(Mode, _portrait, 0, 0);
        const double box = 15;
        double w = box, h = box;
        if (ratio > 0)
        {
            if (ratio >= 1)
                h = box / ratio;
            else
                w = box * ratio;
        }
        var rect = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
        var pen = new Pen(ink, 1.5) { DashStyle = ratio > 0 ? null : new DashStyle([2, 1.6], 0) };
        context.DrawRectangle(null, pen, new RoundedRect(rect, 2.5));
    }
}

/// <summary>The dial in miniature (the toolbar button): a slice per ratio, the one in use lit.</summary>
public sealed class AspectGlyph : Control
{
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<AspectGlyph, int>(nameof(SelectedIndex));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<AspectGlyph, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<AspectGlyph, IBrush?>(nameof(RingBrush), Brushes.Gray);

    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? RingBrush { get => GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    static AspectGlyph()
    {
        AffectsRender<AspectGlyph>(SelectedIndexProperty, AccentBrushProperty, RingBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        var count = CropMath.AspectModes.Count;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var inner = outer * 0.48;
        for (var i = 0; i < count; i++)
        {
            var geometry = AspectDial.WedgeGeometry(center, inner, outer, DonutGeometry.MidAngle(i, count), DonutGeometry.Span(count), 1.6, 1);
            if (i == SelectedIndex)
            {
                context.DrawGeometry(AccentBrush, null, geometry);
            }
            else
            {
                using var faint = context.PushOpacity(0.45);
                context.DrawGeometry(RingBrush, null, geometry);
            }
        }
    }
}
