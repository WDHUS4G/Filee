// The crop overlay's end summary as a small donut: a slice per file (cropped in the accent colour, skipped muted,
// failed in the danger colour), grouped into three slices when there are too many files for a slice each, and the
// number cropped in the hub.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Filee.App.Controls;

public sealed class CropSummaryRing : Control
{
    public static readonly StyledProperty<int> DoneProperty = AvaloniaProperty.Register<CropSummaryRing, int>(nameof(Done));
    public static readonly StyledProperty<int> SkippedProperty = AvaloniaProperty.Register<CropSummaryRing, int>(nameof(Skipped));
    public static readonly StyledProperty<int> FailedProperty = AvaloniaProperty.Register<CropSummaryRing, int>(nameof(Failed));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<CropSummaryRing, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<CropSummaryRing, IBrush?>(nameof(MutedBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> DangerBrushProperty =
        AvaloniaProperty.Register<CropSummaryRing, IBrush?>(nameof(DangerBrush), Brushes.IndianRed);

    public static readonly StyledProperty<IBrush?> HubBrushProperty =
        AvaloniaProperty.Register<CropSummaryRing, IBrush?>(nameof(HubBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CropSummaryRing>();

    public int Done { get => GetValue(DoneProperty); set => SetValue(DoneProperty, value); }
    public int Skipped { get => GetValue(SkippedProperty); set => SetValue(SkippedProperty, value); }
    public int Failed { get => GetValue(FailedProperty); set => SetValue(FailedProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public IBrush? HubBrush { get => GetValue(HubBrushProperty); set => SetValue(HubBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Up to this many files, each gets its own slice.</summary>
    private const int MaxSlices = 24;

    static CropSummaryRing()
    {
        AffectsRender<CropSummaryRing>(DoneProperty, SkippedProperty, FailedProperty, AccentBrushProperty, MutedBrushProperty,
            DangerBrushProperty, HubBrushProperty, ForegroundProperty);
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
        var inner = outer * 0.62;
        var total = Done + Skipped + Failed;
        if (total <= 0 || outer <= 4)
            return;

        var groups = new[] { (Done, AccentBrush), (Skipped, MutedBrush), (Failed, DangerBrush) }.Where(g => g.Item1 > 0).ToList();
        var slices = total <= MaxSlices
            ? groups.SelectMany(g => Enumerable.Repeat((Share: 1.0, Brush: g.Item2), g.Item1)).ToList()
            : groups.Select(g => (Share: (double)g.Item1, Brush: g.Item2)).ToList();
        var sum = slices.Sum(s => s.Share);

        if (slices.Count == 1)
        {
            context.DrawEllipse(null, new Pen(slices[0].Brush, outer - inner), center, (outer + inner) / 2, (outer + inner) / 2);
        }
        else
        {
            var start = 0.0;
            foreach (var (share, brush) in slices)
            {
                var span = 2 * Math.PI * share / sum;
                var geometry = AspectDial.WedgeGeometry(center, inner, outer, start + span / 2, span, 3, 3);
                context.DrawGeometry(brush, null, geometry);
                start += span;
            }
        }

        context.DrawEllipse(HubBrush, null, center, inner - 5, inner - 5);
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var text = new FormattedText($"{Done}/{total}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            Math.Max(12, inner * 0.42), Foreground);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
