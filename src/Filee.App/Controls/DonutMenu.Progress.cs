// Progress mode of the donut: after a drop the slices are sucked into the centre, the hole turns into a small
// progress ring with the percentage in the middle, and on completion a check mark pops up in the centre.
//
//   BeginProgress()  → slices collapse (≈0.3 s) → ring springs in
//   SetProgress(x)   → arc follows smoothly
//   CompleteProgress → arc fills (success colour) → ✓ / ! pops in the centre → hold → fade → callback
//   HandOffProgress  → ring shrinks away quickly (the toast window takes over) → callback
//
// The ring stays visible for at least MinRingSeconds so very fast conversions still show their ✓ instead of
// flashing. With Motion.Enabled == false everything snaps; only the hold time remains.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>How a conversion shown by the progress ring ended.</summary>
public enum DonutProgressOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed partial class DonutMenu
{
    public static readonly StyledProperty<IBrush?> SuccessBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SuccessBrush), Brushes.SeaGreen);

    /// <summary>Radius of the progress ring (centre of the stroke).</summary>
    public const double RingRadius = 36;

    private const double RingStroke = 6;
    private const double CollapseSeconds = 0.28;
    private const double CollapseStagger = 0.02;
    private const double MinRingSeconds = 0.6;
    private const double HoldSuccessSeconds = 0.9;
    private const double HoldFailureSeconds = 1.4;
    private const double FadeSeconds = 0.25;
    private const double HandOffSeconds = 0.22;

    // Material "close" icon (24×24) for the cancel button.
    private static readonly Geometry CloseIcon = Geometry.Parse(
        "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z");

    private bool _progressMode;
    private double _collapseClock;          // seconds since BeginProgress
    private double _ringClock = -1;         // seconds since the ring became visible (-1 = not yet)
    private Spring _ring;                   // ring "pop in" scale 0..1
    private double _shownProgress;          // what the arc shows (smoothed)
    private double _targetProgress;         // latest real progress 0..1
    private DonutProgressOutcome? _pendingOutcome;
    private DonutProgressOutcome? _outcome; // shown outcome (badge visible)
    private Spring _badge;                  // ✓ / ! pop scale
    private double _holdClock = -1;
    private double _fadeClock = -1;
    private double _handOffClock = -1;
    private Action? _progressDone;
    private bool _ringHover;

    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }

    /// <summary>True while the donut shows a conversion's progress instead of presets.</summary>
    public bool IsInProgressMode => _progressMode;

    /// <summary>The finished ring is still holding its badge or fading out, and hides the window when it is done.</summary>
    public bool IsFinishingProgress => _progressMode && _progressDone is not null;

    /// <summary>Drops the progress ring (and its pending "done" callback) for a new gesture.</summary>
    public void LeaveProgressMode() => ResetProgress();

    /// <summary>Outcome badge currently visible (null while running). For tests.</summary>
    internal DonutProgressOutcome? ShownOutcome => _outcome;

    /// <summary>The × on the ring was clicked while a conversion was running.</summary>
    public event EventHandler? ProgressCancelRequested;

    /// <summary>The ring was clicked (e.g. to open the output folder).</summary>
    public event EventHandler? ProgressClicked;

    /// <summary>Switches to progress mode: collapses the slices into the centre and shows the progress ring.</summary>
    public void BeginProgress()
    {
        ResetProgress();
        _progressMode = true;
        _closing = false;
        _closeCallback = null;
        _openClock = double.MaxValue;
        if (!Motion.Enabled)
        {
            _collapseClock = CollapseEnd;
            _ring.Value = 1;
            _ringClock = 0;
        }
        StartAnimation();
    }

    /// <summary>Updates the progress (0..1). The arc animates towards it.</summary>
    public void SetProgress(double value)
    {
        _targetProgress = Math.Clamp(value, 0, 1);
        StartAnimation();
    }

    /// <summary>
    /// Ends the conversion: shows the outcome badge in the centre, holds, fades and then calls <paramref name="done"/>.
    /// </summary>
    public void CompleteProgress(DonutProgressOutcome outcome, Action done)
    {
        if (!_progressMode)
        {
            done();
            return;
        }
        if (outcome == DonutProgressOutcome.Succeeded)
            _targetProgress = 1;
        _pendingOutcome = outcome;
        _progressDone = done;
        StartAnimation();
    }

    /// <summary>Quickly shrinks the ring away (the toast window takes over), then calls <paramref name="done"/>.</summary>
    public void HandOffProgress(Action done)
    {
        if (!_progressMode)
        {
            done();
            return;
        }
        _progressDone = done;
        _pendingOutcome = null;
        if (!Motion.Enabled)
        {
            FinishProgress();
            return;
        }
        _handOffClock = 0;
        StartAnimation();
    }

    private double CollapseEnd => CollapseSeconds + CollapseStagger * Math.Max(0, Count - 1);

    private void ResetProgress()
    {
        _progressMode = false;
        _collapseClock = 0;
        _ringClock = -1;
        _ring = default;
        _shownProgress = 0;
        _targetProgress = 0;
        _pendingOutcome = null;
        _outcome = null;
        _badge = default;
        _holdClock = -1;
        _fadeClock = -1;
        _handOffClock = -1;
        _progressDone = null;
        _ringHover = false;
    }

    private void FinishProgress()
    {
        var done = _progressDone;
        _progressDone = null;
        done?.Invoke();
    }

    /// <summary>Advances all progress-mode animations. Returns true while another frame is needed.</summary>
    private bool StepProgress(double dt)
    {
        if (!_progressMode)
            return false;

        var animate = Motion.Enabled;
        var active = false;

        // 1. Slices collapse into the centre, then the ring appears.
        if (_collapseClock < CollapseEnd)
        {
            _collapseClock = animate ? _collapseClock + dt : CollapseEnd;
            active = true;
        }
        else if (_ringClock < 0)
        {
            _ringClock = 0;
            _ring.Velocity = animate ? 2 : 0;
            if (!animate)
                _ring.Value = 1;
        }
        if (_ringClock >= 0)
        {
            _ringClock += dt;
            active |= animate ? _ring.Step(1, dt, 300, 16) : false;
        }

        // 2. The arc follows the real progress smoothly (exponential approach).
        var gap = _targetProgress - _shownProgress;
        _shownProgress = animate ? _shownProgress + gap * Math.Min(1, dt * 9) : _targetProgress;
        if (Math.Abs(_targetProgress - _shownProgress) < 0.002)
            _shownProgress = _targetProgress;
        else
            active = true;

        // 3. Show the outcome once the ring has been visible long enough and the arc caught up.
        if (_pendingOutcome is { } pending && _outcome is null && _ringClock >= MinRingSeconds &&
            (pending != DonutProgressOutcome.Succeeded || _shownProgress >= 0.999))
        {
            _outcome = pending;
            _holdClock = 0;
            if (animate)
                _badge.Velocity = 9; // kick → overshoot "pop"
            else
                _badge.Value = 1;
        }
        if (_pendingOutcome is not null)
            active = true;

        if (_outcome is { } outcome)
        {
            active |= animate && _badge.Step(1, dt, 420, 14);
            if (_fadeClock < 0)
            {
                _holdClock += dt;
                var hold = outcome switch
                {
                    DonutProgressOutcome.Failed => HoldFailureSeconds,
                    DonutProgressOutcome.Cancelled => 0.2,
                    _ => HoldSuccessSeconds,
                };
                if (_holdClock >= hold)
                    _fadeClock = 0;
                active = true;
            }
            else
            {
                _fadeClock = animate ? _fadeClock + dt : FadeSeconds;
                if (_fadeClock >= FadeSeconds)
                {
                    FinishProgress();
                    return false;
                }
                active = true;
            }
        }

        // 4. Hand-off to the toast window.
        if (_handOffClock >= 0)
        {
            _handOffClock += dt;
            if (_handOffClock >= HandOffSeconds)
            {
                FinishProgress();
                return false;
            }
            active = true;
        }

        return active;
    }

    // ───────────────────────── Rendering ─────────────────────────

    private void RenderProgress(DrawingContext context, Point center, double r1, double r2, Typeface bold, Typeface regular)
    {
        var n = Count;
        var collapseAll = CollapseEnd <= 0 ? 1 : Math.Clamp(_collapseClock / CollapseEnd, 0, 1);

        // Slices shrink towards the centre with a slight stagger ("sucked in").
        if (collapseAll < 1)
        {
            using (context.PushOpacity(1 - collapseAll))
                DrawSoftShadow(context, center, r2);

            for (var i = 0; i < n; i++)
            {
                var t = Math.Clamp((_collapseClock - i * CollapseStagger) / CollapseSeconds, 0, 1);
                var c = EaseInCubic(t);
                if (c >= 0.999)
                    continue;
                var scale = 1 - 0.8 * c;
                var transform =
                    Matrix.CreateTranslation(-center.X, -center.Y) *
                    Matrix.CreateRotation(c * 0.25) *
                    Matrix.CreateScale(scale, scale) *
                    Matrix.CreateTranslation(center.X, center.Y);
                using (context.PushTransform(transform))
                using (context.PushOpacity(1 - c))
                {
                    var wedge = CreateWedge(center, r1, r2, i, n);
                    context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity), BorderPen(), wedge);
                    var hot = i == ActiveIndex;
                    if (hot)
                        context.DrawGeometry(WithOpacity(AccentBrush, 0.8), null, wedge);
                    // Labels shrink with their slice instead of vanishing when the collapse starts.
                    DrawLabel(context, Items![i], center, (r1 + r2) / 2, DonutGeometry.MidAngle(i, n),
                        DonutGeometry.Span(n) * (r1 + r2) / 2 - 10, r2 - r1 - 16, bold, regular, hot);
                }
            }

            // The hole shrinks to the size of the ring's plate.
            var plate = RingRadius + RingStroke + 2;
            var holeRadius = Lerp(r1 - 6, plate, EaseInCubic(collapseAll));
            context.DrawEllipse(CenterBrush, BorderPen(), center, holeRadius, holeRadius);
            return;
        }

        // Ring: plate, track, arc, percentage or outcome badge.
        var fade = _fadeClock >= 0 ? Math.Clamp(_fadeClock / FadeSeconds, 0, 1) : 0;
        var handOff = _handOffClock >= 0 ? Math.Clamp(_handOffClock / HandOffSeconds, 0, 1) : 0;
        var ringScale = Math.Max(0, _ring.Value) * (1 - 0.15 * fade) * (1 - 0.7 * EaseInCubic(handOff));
        var opacity = (1 - fade) * (1 - handOff);
        if (ringScale <= 0.01 || opacity <= 0.01)
            return;

        var ringTransform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(ringScale, ringScale) *
                            Matrix.CreateTranslation(center.X, center.Y);
        using var _ = context.PushTransform(ringTransform);
        using var __ = context.PushOpacity(opacity);

        var plateRadius = RingRadius + RingStroke + 2;
        DrawPlateShadow(context, center, plateRadius);
        context.DrawEllipse(CenterBrush, BorderPen(), center, plateRadius, plateRadius);
        context.DrawEllipse(null, new Pen(WithOpacity(SecondaryForeground, 0.22), RingStroke), center, RingRadius, RingRadius);

        var arcBrush = _outcome switch
        {
            DonutProgressOutcome.Succeeded => SuccessBrush,
            DonutProgressOutcome.Failed => DangerBrush,
            _ => AccentBrush,
        };
        var sweep = _outcome == DonutProgressOutcome.Failed ? Math.Max(_shownProgress, 0.02) : _shownProgress;
        DrawArc(context, center, RingRadius, sweep, new Pen(arcBrush, RingStroke, lineCap: PenLineCap.Round));

        var badge = _outcome is null ? 0 : Math.Max(0, _badge.Value);
        if (badge < 0.98)
        {
            using (context.PushOpacity(1 - Math.Clamp(badge, 0, 1)))
            {
                var percent = MakeText($"{Math.Round(_shownProgress * 100):0}%", bold, 15, Foreground, RingRadius * 1.6);
                DrawCentered(context, percent, center.X, center.Y - percent.Height / 2);
            }
        }

        if (_outcome is { } outcome && outcome != DonutProgressOutcome.Cancelled && badge > 0.01)
            DrawOutcomeBadge(context, center, outcome, badge, bold);

        if (_ringHover && _outcome is null && _handOffClock < 0)
            DrawCancelButton(context, CancelButtonCenter(center));
    }

    /// <summary>✓ (success) or ! (failure) popping in the centre of the ring.</summary>
    private void DrawOutcomeBadge(DrawingContext context, Point center, DonutProgressOutcome outcome, double scale, Typeface bold)
    {
        var radius = 22 * scale;
        var fill = outcome == DonutProgressOutcome.Succeeded ? SuccessBrush : DangerBrush;
        context.DrawEllipse(fill, null, center, radius, radius);

        var transform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation(center.X, center.Y);
        using var _ = context.PushTransform(transform);
        if (outcome == DonutProgressOutcome.Succeeded)
        {
            var check = new StreamGeometry();
            using (var g = check.Open())
            {
                g.BeginFigure(new Point(center.X - 9, center.Y + 0.5), false);
                g.LineTo(new Point(center.X - 2.5, center.Y + 7));
                g.LineTo(new Point(center.X + 10, center.Y - 6.5));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(OnAccentBrush, 3.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), check);
        }
        else
        {
            var mark = MakeText("!", bold, 22, OnAccentBrush, 30);
            DrawCentered(context, mark, center.X, center.Y - mark.Height / 2);
        }
    }

    private Point CancelButtonCenter(Point center) =>
        DonutGeometry.PointAt(center, RingRadius + RingStroke, Math.PI / 4);

    private void DrawCancelButton(DrawingContext context, Point at)
    {
        context.DrawEllipse(Foreground, new Pen(CenterBrush, 2), at, 10, 10);
        var bounds = CloseIcon.Bounds;
        var s = 10 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateScale(s, s) *
                     Matrix.CreateTranslation(at.X, at.Y);
        using (context.PushTransform(matrix))
            context.DrawGeometry(CenterBrush, null, CloseIcon);
    }

    private static void DrawPlateShadow(DrawingContext context, Point center, double radius)
    {
        var outer = radius + 10;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), (radius - 8) / outer),
                new GradientStop(Color.FromArgb(52, 0, 0, 0), radius / outer),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        context.DrawEllipse(brush, null, new Point(center.X, center.Y + 3), outer, outer);
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="fraction"/> of the circle.</summary>
    private static void DrawArc(DrawingContext context, Point center, double radius, double fraction, Pen pen)
    {
        if (fraction <= 0.001)
            return;
        if (fraction >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var sweep = fraction * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(DonutGeometry.PointAt(center, radius, 0), false);
            g.ArcTo(DonutGeometry.PointAt(center, radius, sweep), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    // ───────────────────────── Input ─────────────────────────

    /// <summary>Hit test in progress mode.</summary>
    private DonutHit HitTestProgress(Point p)
    {
        if (_ringHover && _outcome is null && Distance(p, CancelButtonCenter(Center)) <= 11)
            return new DonutHit(DonutHitKind.ProgressCancel);
        return Distance(p, Center) <= RingRadius + RingStroke + 2
            ? new DonutHit(DonutHitKind.ProgressRing)
            : new DonutHit(DonutHitKind.Outside);
    }

    private void OnProgressPointerMoved(Point p)
    {
        var over = Distance(p, Center) <= RingRadius + RingStroke + 14;
        if (over == _ringHover)
            return;
        _ringHover = over;
        StartAnimation();
    }

    private void OnProgressPointerReleased(Point p)
    {
        switch (HitTestProgress(p).Kind)
        {
            case DonutHitKind.ProgressCancel:
                ProgressCancelRequested?.Invoke(this, EventArgs.Empty);
                break;
            case DonutHitKind.ProgressRing:
                ProgressClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
