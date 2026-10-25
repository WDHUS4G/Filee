// The donut toolbar: a ring completely divided into equal slices, one per preset.
// Used both as the live toolbar (RadialWindow) and as the editor in Settings → Donut toolbar (IsEditMode).
//
// Drawn by hand (no child controls) so it stays fast on low-end PCs and so every slice can be hit-tested by
// its exact wedge shape. Animations are tiny springs advanced on each rendered frame; with
// Motion.Enabled == false every value jumps straight to its target.
//
// After a drop the donut turns into a progress ring: see DonutMenu.Progress.cs. Group slices, which fan out a ring
// of presets, and the keyboard: DonutMenu.Groups.cs. Tab (or the wheel) flips the donut over: DonutMenu.Flip.cs.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>One slice of the donut.</summary>
public sealed class DonutItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Small second line, e.g. the target format. Hidden when equal to <see cref="Label"/>.</summary>
    public string? Caption { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Tooltip shown for disabled slices.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>
    /// A group slice: converts nothing itself; hovering it fans these out in a small ring outside the donut, where they
    /// are clicked or dropped on like ordinary slices.
    /// </summary>
    public IReadOnlyList<DonutItem>? Children { get; init; }

    /// <summary>A group slice (<see cref="Children"/> set), even one left with nothing to fan out.</summary>
    public bool IsGroup => Children is not null;

    /// <summary>A group with something to fan out.</summary>
    public bool CanExpand => Children is { Count: > 0 };
}

/// <summary>Donut-shaped menu control.</summary>
public sealed partial class DonutMenu : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutItem>?> ItemsProperty =
        AvaloniaProperty.Register<DonutMenu, IReadOnlyList<DonutItem>?>(nameof(Items));
    public static readonly StyledProperty<double> OuterRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(OuterRadius), 150);
    public static readonly StyledProperty<double> HoleRatioProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(HoleRatio), 0.42);
    public static readonly StyledProperty<double> SliceOpacityProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(SliceOpacity), 0.96);
    public static readonly StyledProperty<string?> CenterTitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterTitle));
    public static readonly StyledProperty<string?> CenterSubtitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterSubtitle));
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(IsEditMode));
    public static readonly StyledProperty<bool> AllowContextEditProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(AllowContextEdit));
    public static readonly StyledProperty<bool> ReserveSubRingProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(ReserveSubRing));

    /// <summary>The theme's softness (Styles/Controls.axaml binds SoftRadius): rounds the corners of the slices.</summary>
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<IBrush?> SliceBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> SliceBorderBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBorderBrush));
    public static readonly StyledProperty<IBrush?> CenterBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(CenterBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(OnAccentBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(Foreground), Brushes.Black);
    public static readonly StyledProperty<IBrush?> SecondaryForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SecondaryForeground), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> DangerBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(DangerBrush), Brushes.IndianRed);

    /// <summary>Gap between slices in pixels (keeps the donut "full" while separating slices).</summary>
    private const double GapPixels = 3;
    private const double PopOut = 7;
    private const double OpenDuration = 0.42;
    private const double OpenStagger = 0.028;
    private const double DragStartDistance = 5;

    // Pencil (Material "edit") path in a 24×24 box.
    private static readonly Geometry PencilIcon = Geometry.Parse(
        "M3,17.25V21h3.75L17.81,9.94l-3.75-3.75L3,17.25z M20.71,7.04c0.39-0.39,0.39-1.02,0-1.41l-2.34-2.34c-0.39-0.39-1.02-0.39-1.41,0l-1.83,1.83l3.75,3.75L20.71,7.04z");

    private Spring[] _hover = [];
    private double[] _open = [];
    private Spring _centerPulse;
    private double _openClock = double.MaxValue;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private Action? _closeCallback;
    private bool _closing;

    private int _hoverIndex = -1;
    private int _externalHighlight = -1;
    private bool _externalCenterHot;
    private int _dimmedIndex = -1;
    private int _dropTargetIndex = -1;

    // Edit mode drop preview: the slices make room where a dragged preset would land and a translucent slice shows
    // it there. Every slice's centre angle and width is a spring, so the others slide aside instead of jumping.
    private Spring[] _mid = [];
    private Spring[] _span = [];
    private Spring _previewMid;
    private Spring _previewSpan;
    private int _previewSlot = -1;
    private int _previewRemoved = -1;
    private string? _previewLabel;
    private (int Index, Point Start, PointerPressedEventArgs Args)? _press;

    static DonutMenu()
    {
        AffectsMeasure<DonutMenu>(OuterRadiusProperty, ReserveSubRingProperty);
        AffectsRender<DonutMenu>(HoleRatioProperty, SliceOpacityProperty, CenterTitleProperty, CenterSubtitleProperty,
            IsEditModeProperty, SliceBrushProperty, SliceBorderBrushProperty, CenterBrushProperty, AccentBrushProperty,
            OnAccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty, SuccessBrushProperty, CornerRadiusProperty,
            CenterHintProperty, CenterHintWheelProperty);
        ItemsProperty.Changed.AddClassHandler<DonutMenu>((d, _) => d.OnItemsChanged());
    }

    public DonutMenu()
    {
        // A circle reads the same either way: slices keep their clockwise order in Arabic too, their labels are
        // shaped right to left (TextDirection).
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IReadOnlyList<DonutItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double OuterRadius { get => GetValue(OuterRadiusProperty); set => SetValue(OuterRadiusProperty, value); }
    public double HoleRatio { get => GetValue(HoleRatioProperty); set => SetValue(HoleRatioProperty, value); }
    public double SliceOpacity { get => GetValue(SliceOpacityProperty); set => SetValue(SliceOpacityProperty, value); }
    public string? CenterTitle { get => GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }
    public string? CenterSubtitle { get => GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }

    /// <summary>Settings editor mode: shows ✎ on hover, raises drag and edit events instead of invoking slices.</summary>
    public bool IsEditMode { get => GetValue(IsEditModeProperty); set => SetValue(IsEditModeProperty, value); }

    /// <summary>Live toolbar in click mode: right-click on a slice raises <see cref="SegmentContextRequested"/>.</summary>
    public bool AllowContextEdit { get => GetValue(AllowContextEditProperty); set => SetValue(AllowContextEditProperty, value); }

    /// <summary>
    /// Leave room for a group's ring even before the slices are known (the drag window is placed before files arrive),
    /// so the window doesn't grow, and the donut move, when they do.
    /// </summary>
    public bool ReserveSubRing { get => GetValue(ReserveSubRingProperty); set => SetValue(ReserveSubRingProperty, value); }

    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public IBrush? SliceBrush { get => GetValue(SliceBrushProperty); set => SetValue(SliceBrushProperty, value); }
    public IBrush? SliceBorderBrush { get => GetValue(SliceBorderBrushProperty); set => SetValue(SliceBorderBrushProperty, value); }
    public IBrush? CenterBrush { get => GetValue(CenterBrushProperty); set => SetValue(CenterBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SecondaryForeground { get => GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }

    /// <summary>True while an external drag hovers the hole (drop = cancel).</summary>
    public bool IsCenterHot => _externalCenterHot;

    /// <summary>Currently emphasised slice (pointer hover or external highlight), or -1.</summary>
    public int ActiveIndex => _externalHighlight >= 0 ? _externalHighlight : _hoverIndex;

    /// <summary>A slice was clicked (live toolbar, click mode).</summary>
    public event EventHandler<int>? SegmentInvoked;

    /// <summary>A slice of a group's ring was clicked: (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentInvoked;

    /// <summary>The hovered slice of a group's ring, or -1. <see cref="ActiveIndexChanged"/> is raised when it changes.</summary>
    public int ActiveSubIndex => _subHover;

    /// <summary>Right-click or ✎ on a slice.</summary>
    public event EventHandler<int>? SegmentContextRequested;

    /// <summary>Right-click on a slice of a group's ring (live toolbar, click mode): (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentContextRequested;

    /// <summary>The hole was clicked.</summary>
    public event EventHandler? CenterInvoked;

    /// <summary>Edit mode: the user started dragging slice <c>Index</c>.</summary>
    public event EventHandler<(int Index, PointerEventArgs Args)>? SegmentDragStarted;

    /// <summary>The emphasised slice changed (-1 = none). Useful to update the centre text.</summary>
    public event EventHandler<int>? ActiveIndexChanged;

    private int Count => Items?.Count ?? 0;
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double InnerRadius => OuterRadius * Math.Clamp(HoleRatio, 0.2, 0.75);

    // ───────────────────────── Public API ─────────────────────────

    /// <summary>Plays the "pop out from the centre" animation.</summary>
    public void PlayOpenAnimation()
    {
        ResetProgress();
        _closing = false;
        _closeCallback = null;
        if (!Motion.Enabled)
        {
            Array.Fill(_open, 1);
            InvalidateVisual();
            return;
        }
        Array.Fill(_open, 0);
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Shrinks the donut back into the centre, then calls <paramref name="done"/>.</summary>
    public void PlayCloseAnimation(Action done)
    {
        if (IsFlipping)
            FinishFlip();
        if (!Motion.Enabled || Count == 0)
        {
            done();
            return;
        }
        _closing = true;
        _closeCallback = done;
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Short "boing" of the centre, e.g. after a drop.</summary>
    public void PulseCenter()
    {
        if (!Motion.Enabled)
            return;
        _centerPulse.Velocity = 6;
        StartAnimation();
    }

    /// <summary>Highlights a slice from outside (drag-over in the live toolbar). -1 clears.</summary>
    public void SetExternalHighlight(int index, bool centerHot = false)
    {
        if (_externalHighlight == index && _externalCenterHot == centerHot)
            return;
        _externalHighlight = index;
        _externalCenterHot = centerHot;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    /// <summary>How far slice <paramref name="index"/> is drawn lit up right now (0 to 1), for the drag log.</summary>
    public double LitAmount(int index) => index >= 0 && index < _hover.Length ? _hover[index].Value : 0;

    /// <summary>Edit mode: the slice being dragged is drawn faded.</summary>
    public void SetDimmedIndex(int index)
    {
        _dimmedIndex = index;
        InvalidateVisual();
    }

    /// <summary>Edit mode: highlights where a dragged item would land (-1 none, Count = empty ring).</summary>
    public void SetDropTarget(int index)
    {
        if (_dropTargetIndex == index)
            return;
        _dropTargetIndex = index;
        InvalidateVisual();
    }

    /// <summary>
    /// Edit mode: opens a gap at <paramref name="slot"/> (the position in the list after the drop) and shows a
    /// translucent slice labelled <paramref name="label"/> there. <paramref name="removed"/> is the slice being moved,
    /// which leaves its place. -1 closes the gap.
    /// </summary>
    public void SetDropPreview(int slot, string? label = null, int removed = -1)
    {
        if (_previewSlot == slot && _previewRemoved == removed && _previewLabel == label)
            return;
        if (_previewSlot < 0 && slot >= 0)
        {
            // The new slice grows out of nothing where it appears.
            _previewMid = new Spring { Value = DonutGeometry.MidAngle(slot, SlotCount(removed)) };
            _previewSpan = default;
        }
        _previewSlot = slot;
        _previewRemoved = removed;
        _previewLabel = label;
        StartAnimation();
    }

    /// <summary>The slot under <paramref name="p"/> when the donut has <paramref name="slots"/> slices; -1 off the ring.</summary>
    public int SlotAt(Point p, int slots)
    {
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Math.Max(slots, 1), PopOut);
        return hit.Kind == DonutHitKind.Segment ? hit.Index : -1;
    }

    /// <summary>Slices while previewing: the items, minus the one being moved, plus the preview.</summary>
    private int SlotCount(int removed) => Count + 1 - (removed >= 0 ? 1 : 0);

    /// <summary>Where slice <paramref name="i"/> belongs: its usual place, or shifted to make room for the preview.</summary>
    private (double Mid, double Span) LayoutTarget(int i)
    {
        var n = Count;
        if (_previewSlot < 0)
            return (DonutGeometry.MidAngle(i, n), DonutGeometry.Span(n));
        if (i == _previewRemoved)
            return (i < _mid.Length ? _mid[i].Value : DonutGeometry.MidAngle(i, n), 0); // it becomes the preview
        var slot = _previewRemoved >= 0 && i > _previewRemoved ? i - 1 : i;
        if (slot >= _previewSlot)
            slot++;
        var m = SlotCount(_previewRemoved);
        return (DonutGeometry.MidAngle(slot, m), DonutGeometry.Span(m));
    }

    /// <summary>Moves the slices and the preview towards their places. Returns true while they are still moving.</summary>
    private bool StepLayout(double dt, bool snap = false)
    {
        snap |= !Motion.Enabled;
        var active = false;
        for (var i = 0; i < _mid.Length; i++)
        {
            var (mid, span) = LayoutTarget(i);
            if (snap)
            {
                _mid[i] = new Spring { Value = mid };
                _span[i] = new Spring { Value = span };
                continue;
            }
            active |= _mid[i].Step(mid, dt, 420, 36);
            active |= _span[i].Step(span, dt, 420, 36);
        }
        var m = SlotCount(_previewRemoved);
        var previewMid = _previewSlot >= 0 ? DonutGeometry.MidAngle(_previewSlot, m) : _previewMid.Value;
        var previewSpan = _previewSlot >= 0 ? DonutGeometry.Span(m) : 0;
        if (snap)
        {
            _previewMid = new Spring { Value = previewMid };
            _previewSpan = new Spring { Value = previewSpan };
            return false;
        }
        active |= _previewMid.Step(previewMid, dt, 420, 36);
        active |= _previewSpan.Step(previewSpan, dt, 420, 36);
        return active;
    }

    /// <summary>Hit-tests a point in this control's coordinates.</summary>
    public DonutHit HitTest(Point p)
    {
        if (_progressMode)
            return HitTestProgress(p);
        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < Count &&
            Distance(p, EditButtonCenter(_hoverIndex)) <= 13)
            return new DonutHit(DonutHitKind.EditButton, _hoverIndex);
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Count, PopOut);
        if (ExpandedChildren is { } children)
        {
            var (inner, outer) = SubRadii;
            var parentMid = DonutGeometry.MidAngle(_expanded, Count);
            var subSpan = SubSpanOf(children.Count);
            var sub = DonutGeometry.SubIndexAt(Center, p, inner, outer, parentMid, children.Count, subSpan);
            if (sub >= 0)
                return new DonutHit(DonutHitKind.SubSegment, _expanded, sub);
            // On the way out to the ring the pointer crosses the gap before it: there it still belongs to the group, or
            // the ring would fold away (or the hint and the drag cursor flicker) just before it is reached. A neighbour's
            // outer half stays that neighbour, so a click or a drop there converts with it; TrackGroups keeps the ring
            // open over it (OnWayToRing).
            if (hit.Kind == DonutHitKind.Outside && Distance(p, Center) < inner && InFan(p, _expanded))
                return new DonutHit(DonutHitKind.Segment, _expanded);
        }
        return hit;
    }

    // ───────────────────────── Layout ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = SideFor(OuterRadius, ReserveSubRing || (!IsEditMode && Items?.Any(i => i.IsGroup) == true));
        return new Size(side, side);
    }

    private void OnItemsChanged()
    {
        var n = Count;
        _hover = new Spring[n];
        _open = Enumerable.Repeat(1.0, n).ToArray();
        _hoverIndex = -1;
        _externalHighlight = -1;
        _dimmedIndex = -1;
        _dropTargetIndex = -1;
        // A drop changes the items into exactly what the preview showed: start there, without a jump.
        _previewSlot = -1;
        _previewRemoved = -1;
        _previewSpan = default;
        _mid = Enumerable.Range(0, n).Select(i => new Spring { Value = DonutGeometry.MidAngle(i, n) }).ToArray();
        _span = Enumerable.Range(0, n).Select(_ => new Spring { Value = DonutGeometry.Span(n) }).ToArray();
        _expanded = _shownExpanded = _subHover = -1;
        ClearPending();
        _expand = default;
        // New files (or none): a flip of the old ones is over. A flip's own swap keeps going (SwapFaces).
        if (!_swapping)
            CancelFlip();
        _lastExternalAt = null;
        // The window is reused: a keyboard hold from the last time it was open must not swallow the first moves now.
        _keyboardHold = _ringFromKeys = false;
        _keyAnchor = _lastPointer = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ───────────────────────── Input ─────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_progressMode)
        {
            OnProgressPointerMoved(p);
            return;
        }

        if (_press is { } press && IsEditMode && Distance(p, press.Start) > DragStartDistance && press.Index >= 0)
        {
            _press = null;
            SegmentDragStarted?.Invoke(this, (press.Index, e));
            return;
        }

        if (_keyboardHold)
        {
            if (_keyAnchor is not { } anchor)
                _keyAnchor = anchor = p;
            if (Distance(p, anchor) < KeyboardJitter)
                return;
            _keyboardHold = false;
        }
        _lastPointer = p;
        var hit = HitTest(p);
        if (IsFlipping)
            PointAtAngle(DonutGeometry.AngleOf(Center, p), hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment);
        TrackGroups(hit, p);
        SetHover(hit.Kind switch
        {
            DonutHitKind.Segment or DonutHitKind.EditButton or DonutHitKind.SubSegment => hit.Index,
            _ => -1,
        });
        // On a group's ring, why that slice (not the group) is disabled.
        var hovered = hit.Kind == DonutHitKind.SubSegment ? Items![hit.Index].Children![hit.Sub]
            : _hoverIndex >= 0 ? Items![_hoverIndex] : null;
        ToolTip.SetTip(this, hovered is { IsEnabled: false } ? hovered.DisabledReason : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_progressMode)
        {
            OnProgressPointerMoved(new Point(double.MaxValue / 4, 0));
            return;
        }
        if (_press is null && !LeftThroughSeam(e.GetPosition(this)))
        {
            CollapseGroup();
            SetHover(-1);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);

        if (_progressMode)
        {
            if (point.Properties.IsLeftButtonPressed)
            {
                _press = (-1, point.Position, e);
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.EditButton && (IsEditMode || AllowContextEdit))
            {
                SegmentContextRequested?.Invoke(this, hit.Index);
                e.Handled = true;
            }
            else if (hit.Kind == DonutHitKind.SubSegment && AllowContextEdit)
            {
                SubSegmentContextRequested?.Invoke(this, (hit.Index, hit.Sub));
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _press = (hit.Kind == DonutHitKind.Segment ? hit.Index : -1, point.Position, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null)
            return;
        _press = null;

        if (_progressMode)
        {
            OnProgressPointerReleased(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        var hit = HitTest(e.GetPosition(this));
        switch (hit.Kind)
        {
            case DonutHitKind.EditButton:
                SegmentContextRequested?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index] is { IsEnabled: true, IsGroup: false }:
                SegmentInvoked?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index].Children is { Count: > 0 }:
                Expand(hit.Index); // no waiting when clicked; a tap has no hover to rest on at all
                break;
            case DonutHitKind.Outside when !IsEditMode && _expanded >= 0:
                CollapseGroup(); // clicked past the ring
                break;
            case DonutHitKind.SubSegment when !IsEditMode && Items![hit.Index].Children![hit.Sub].IsEnabled:
                SubSegmentInvoked?.Invoke(this, (hit.Index, hit.Sub));
                break;
            case DonutHitKind.Center when !IsEditMode:
                CenterInvoked?.Invoke(this, EventArgs.Empty);
                break;
        }
        e.Handled = true;
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
            return;
        _hoverIndex = index;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    // ───────────────────────── Animation ─────────────────────────

    /// <summary>Damping at which a spring of stiffness 320 settles without overshooting (2·√320).</summary>
    private const double CriticalDamping = 36;

    /// <summary>Underdamped spring: gives the soft "boing" when a slice pops out.</summary>
    private struct Spring
    {
        public double Value;
        public double Velocity;

        /// <summary>
        /// Longest time one integration step covers. With these stiffnesses a single step of a late frame (50 ms)
        /// overshoots more each frame instead of settling: slices stayed lit and flew apart on a busy machine.
        /// </summary>
        private const double MaxStep = 1 / 120.0;

        public bool Step(double target, double dt, double stiffness = 320, double damping = 20)
        {
            var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxStep));
            var h = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var force = stiffness * (target - Value) - damping * Velocity;
                Velocity += force * h;
                Value += Velocity * h;
            }
            var settled = Math.Abs(target - Value) < 0.001 && Math.Abs(Velocity) < 0.01;
            if (settled)
            {
                Value = target;
                Velocity = 0;
            }
            return !settled;
        }
    }

    /// <summary>
    /// A frame loop that got no frame for this long is treated as stopped: a frame request can be lost (the window
    /// hidden in between, for one), and every later animation would wait for it, so the slice under a dragged file
    /// would never light up.
    /// </summary>
    internal const long StalledLoopMs = 250;

    private int _loop;           // the current frame loop; frames of an older one are ignored
    private long _lastTick;      // Environment.TickCount64 of the last frame or frame request

    /// <summary>Frame requests asked again before the animation is finished at once (see <see cref="CheckLoop"/>).</summary>
    internal const int FrameRetries = 2;

    private int _stalls; // times the current animation's frames stopped coming

    private void StartAnimation()
    {
        InvalidateVisual();
        if (_animating && Environment.TickCount64 - _lastTick < StalledLoopMs)
            return;
        _stalls = 0;
        StartLoop();
    }

    private void StartLoop()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            _animating = false;
            return;
        }
        _animating = true;
        _lastFrame = null;
        _lastTick = Environment.TickCount64;
        var loop = ++_loop;
        top.RequestAnimationFrame(time => OnFrame(time, loop));
        Watch(loop);
    }

    private void Watch(int loop) =>
        DispatcherTimer.RunOnce(() =>
        {
            if (loop == _loop)
                CheckLoop();
        }, TimeSpan.FromMilliseconds(StalledLoopMs + 50));

    /// <summary>
    /// Watches the frame loop without waiting for another animation call. When its frames stop (a request lost
    /// while the window was hidden, or the renderer waking up after a long idle), the frame is asked for again; after
    /// <see cref="FrameRetries"/> stalls the animations jump to their end, so an opening donut always appears. Stalls
    /// are counted per animation: frames that trickle in once per retry would otherwise open the donut over seconds.
    /// </summary>
    internal void CheckLoop()
    {
        if (!_animating)
            return;
        if (Environment.TickCount64 - _lastTick < StalledLoopMs)
        {
            Watch(_loop); // frames are coming: keep watching until the loop ends
            return;
        }
        if (++_stalls <= FrameRetries)
        {
            StartLoop();
            return;
        }
        _animating = false;
        _loop++;
        FinishAnimations();
    }

    /// <summary>
    /// Sets the opening, the highlights and the slice layout to where they end (a closing donut closes). The progress
    /// ring keeps its own timers.
    /// </summary>
    internal void FinishAnimations()
    {
        for (var i = 0; i < _hover.Length; i++)
            _hover[i] = new Spring { Value = i == ActiveIndex ? 1 : 0 };
        _centerPulse = default;
        _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
        if (_expanded < 0)
            _shownExpanded = -1;
        StepLayout(0, snap: true);
        FinishFlip();
        if (_openClock < double.MaxValue)
        {
            _openClock = double.MaxValue;
            Array.Fill(_open, _closing ? 0 : 1);
            if (_closing)
                FinishClose();
        }
        InvalidateVisual();
    }

    private void OnFrame(TimeSpan time, int loop)
    {
        if (loop != _loop)
            return; // a loop that was given up on as stalled, replaced by a newer one
        _lastTick = Environment.TickCount64;
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = time;
        var active = Advance(dt);

        InvalidateVisual();
        if (active && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(next => OnFrame(next, loop));
        else
            _animating = false;
    }

    /// <summary>
    /// Advances every animation by <paramref name="dt"/> seconds. Returns true while more frames are needed.
    /// Internal so tests can step time deterministically.
    /// </summary>
    internal bool Advance(double dt)
    {
        var active = false;

        if (!Motion.Enabled)
        {
            for (var i = 0; i < _hover.Length; i++)
            {
                _hover[i].Value = i == ActiveIndex ? 1 : 0;
                _hover[i].Velocity = 0;
            }
            Array.Fill(_open, _closing ? 0 : 1);
            _centerPulse = default;
            _expand = new Spring { Value = _expanded >= 0 ? 1 : 0 };
            if (_expanded < 0)
                _shownExpanded = -1;
            StepLayout(dt);
            FinishFlip(); // animations switched off halfway through a flip
            if (_closing)
                FinishClose();
            active |= StepProgress(dt); // hold timers still run without animation
        }
        else
        {
            // Popping out bounces; letting go settles without a bounce: one below zero would make the faded accent
            // flash once more on its way back.
            for (var i = 0; i < _hover.Length; i++)
                active |= i == ActiveIndex ? _hover[i].Step(1, dt) : _hover[i].Step(0, dt, 320, CriticalDamping);
            active |= _centerPulse.Step(0, dt, 260, 12);
            // A soft bounce opening, like the slices popping out; folding away settles without one. The bounce grows the
            // ring by about a pixel, well inside the room kept for it (SubRingSpace).
            active |= _expanded >= 0 ? _expand.Step(1, dt, 320, 24) : _expand.Step(0, dt, 320, CriticalDamping);
            if (_expanded < 0 && _expand.Value <= 0.001)
                _shownExpanded = -1;
            active |= StepProgress(dt);
            active |= StepLayout(dt);
            active |= StepFlip(dt);

            if (_openClock < double.MaxValue)
            {
                _openClock += dt;
                var done = true;
                for (var i = 0; i < _open.Length; i++)
                {
                    var t = Math.Clamp((_openClock - i * (_closing ? 0.01 : OpenStagger)) / (_closing ? 0.16 : OpenDuration), 0, 1);
                    _open[i] = _closing ? 1 - EaseInCubic(t) : EaseOutBack(t);
                    done &= t >= 1;
                }
                if (done)
                {
                    _openClock = double.MaxValue;
                    if (_closing)
                        FinishClose();
                }
                else
                {
                    active = true;
                }
            }
        }

        return active;
    }

    private void FinishClose()
    {
        _closing = false;
        var callback = _closeCallback;
        _closeCallback = null;
        callback?.Invoke();
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.55;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseInCubic(double t) => t * t * t;

    // ───────────────────────── Rendering ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var r2 = OuterRadius;
        var r1 = InnerRadius;
        var n = Count;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var typefaceRegular = new Typeface(GetValue(TextElement.FontFamilyProperty));

        if (_progressMode)
        {
            RenderProgress(context, center, r1, r2, typeface, typefaceRegular);
            return;
        }

        RenderRing(context, center, r1, r2, n, typeface, typefaceRegular);

        var reaching = _expanded >= 0 ? _expanded : _pendingGroup;
        if (!IsEditMode && reaching >= 0 && reaching < n && Items![reaching].Children is { Count: > 0 } reachable)
            DrawHitBackdrop(context, center, reaching, reachable.Count);
        if (!IsEditMode && _shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01)
            DrawSubRing(context, center, typeface, typefaceRegular);
        if (HasOldGroupRing)
            DrawOldSubRing(context, center, typeface, typefaceRegular);

        DrawCenter(context, center, r1, typeface, typefaceRegular);

        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < n && _dimmedIndex < 0 && !IsFlipping)
            DrawEditButton(context, EditButtonCenter(_hoverIndex));
    }

    /// <summary>The shadow, the slices and the edit mode's drop preview.</summary>
    private void RenderRing(DrawingContext context, Point center, double r1, double r2, int n, Typeface typeface, Typeface typefaceRegular)
    {
        DrawSoftShadow(context, center, r2);

        if (n == 0)
        {
            var ring = CreateRing(center, r1, r2);
            context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity), BorderPen(), ring);
            if (_dropTargetIndex >= 0)
                context.DrawGeometry(WithOpacity(AccentBrush, 0.35), null, ring);
        }

        for (var i = 0; i < n; i++)
        {
            if (IsFlipping && SliceFlipWidth(false, i) == 0)
                continue;
            var item = Items![i];
            var open = i < _open.Length ? _open[i] : 1;
            if (open <= 0.001)
                continue;

            var previewing = IsEditMode && i < _mid.Length;
            var hover = i < _hover.Length && _previewSlot < 0 ? _hover[i].Value : 0;
            var mid = previewing ? _mid[i].Value : DonutGeometry.MidAngle(i, n);
            var span = previewing ? _span[i].Value : DonutGeometry.Span(n);
            if (span < 0.01)
                continue;
            var offset = DonutGeometry.PointAt(new Point(0, 0), hover * PopOut, mid);

            // Open animation: scale up from the centre with a small twist.
            var scale = 0.55 + 0.45 * open;
            var rotate = (1 - Math.Min(open, 1)) * -0.35;
            var transform =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateRotation(rotate) *
                Matrix.CreateScale(scale * (1 + hover * 0.025), scale * (1 + hover * 0.025)) *
                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                SliceFlipTransform(false, i);

            using (context.PushTransform(transform))
            using (context.PushOpacity(Math.Clamp(open, 0, 1) * (i == _dimmedIndex ? 0.3 : 1)))
            {
                var wedge = CreateWedge(center, r1, r2, mid, span);
                context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);

                // The accent fades in and out with the highlight (no floor: a floor made it vanish with a jump).
                var emphasis = i == _dropTargetIndex ? 1.0 : hover;
                if (emphasis > 0.01 && item.IsEnabled)
                    context.DrawGeometry(WithOpacity(AccentBrush, 0.82 * Math.Clamp(emphasis, 0, 1)), null, wedge);

                var hot = (i == ActiveIndex || i == _dropTargetIndex) && item.IsEnabled;
                DrawLabel(context, item, center, (r1 + r2) / 2, mid, span * (r1 + r2) / 2 - 10,
                    r2 - r1 - 16, typeface, typefaceRegular, hot);
            }
        }

        if (_oldFace is { } old)
            for (var i = 0; i < old.Length; i++)
            {
                var slice = old[i];
                if (SliceFlipWidth(true, i) == 0 || slice.Open <= 0.001)
                    continue;
                var offset = DonutGeometry.PointAt(new Point(0, 0), slice.Hover * PopOut, slice.Mid);
                var scale = 0.55 + 0.45 * slice.Open;
                var rotate = (1 - Math.Min(slice.Open, 1)) * -0.35;
                var transform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(rotate) *
                                Matrix.CreateScale(scale * (1 + slice.Hover * 0.025), scale * (1 + slice.Hover * 0.025)) *
                                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                                SliceFlipTransform(true, i);
                using (context.PushTransform(transform))
                using (context.PushOpacity(Math.Clamp(slice.Open, 0, 1) * (slice.Dimmed ? 0.3 : 1)))
                {
                    var wedge = CreateWedge(center, r1, r2, slice.Mid, slice.Span);
                    context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (slice.Item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);
                    if (slice.Emphasis > 0.01 && slice.Item.IsEnabled)
                        context.DrawGeometry(WithOpacity(AccentBrush, 0.82 * Math.Clamp(slice.Emphasis, 0, 1)), null, wedge);
                    DrawLabel(context, slice.Item, center, (r1 + r2) / 2, slice.Mid,
                        slice.Span * (r1 + r2) / 2 - 10, r2 - r1 - 16, typeface, typefaceRegular,
                        slice.Hot && slice.Item.IsEnabled);
                }
            }

        if (IsEditMode && _previewSpan.Value > 0.01)
            DrawPreview(context, center, r1, r2, typeface, typefaceRegular);
    }

    private void DrawSoftShadow(DrawingContext context, Point center, double r2)
    {
        // A radial gradient ring fakes a soft drop shadow without blur effects (cheap on any GPU).
        var shadowRadius = r2 + 14;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), (r2 - 18) / shadowRadius),
                new GradientStop(Color.FromArgb(46, 0, 0, 0), (r2 - 2) / shadowRadius),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);
        using (context.PushOpacity(opening * ShadowCoverage))
            context.DrawEllipse(brush, null, new Point(center.X, center.Y + 4), shadowRadius, shadowRadius);
    }

    private void DrawCenter(DrawingContext context, Point center, double r1, Typeface bold, Typeface regular)
    {
        var pulse = 1 + _centerPulse.Value * 0.06;
        var radius = (r1 - 6) * pulse;
        var cancelHot = _externalCenterHot;
        context.DrawEllipse(CenterBrush, BorderPen(), center, radius, radius);
        if (cancelHot)
            context.DrawEllipse(WithOpacity(DangerBrush, 0.18), null, center, radius, radius);

        var maxWidth = radius * 1.6;
        var title = MakeText(CenterTitle ?? "", bold, 14, Foreground, maxWidth, maxLines: 2);
        var subtitle = MakeText(CenterSubtitle ?? "", regular, 11.5, cancelHot ? DangerBrush : SecondaryForeground, maxWidth, maxLines: 2);
        // "Tab ↻ back": small, under the rest, a little narrower (the hub is round).
        var hint = string.IsNullOrEmpty(CenterHint) ? null : MakeText(CenterHint, regular, 10.5, SecondaryForeground, maxWidth * 0.85);
        var total = title.Height + (string.IsNullOrEmpty(CenterSubtitle) ? 0 : subtitle.Height + 2) + (hint is null ? 0 : hint.Height + 5);
        var y = center.Y - total / 2;
        DrawCentered(context, title, center.X, y);
        y += title.Height;
        if (!string.IsNullOrEmpty(CenterSubtitle))
        {
            DrawCentered(context, subtitle, center.X, y + 2);
            y += subtitle.Height + 2;
        }
        if (hint is not null)
        {
            using (context.PushOpacity(0.85))
            {
                if (!CenterHintWheel)
                {
                    DrawCentered(context, hint, center.X, y + 5);
                }
                else
                {
                    // A small mouse in front: "🖱 ↻ back" when the wheel flips and no key does.
                    const double iconWidth = 7, gap = 4;
                    var left = center.X - (hint.Width + iconWidth + gap) / 2;
                    DrawMouse(context, new Rect(left, y + 5 + (hint.Height - 10) / 2, iconWidth, 10), SecondaryForeground);
                    DrawCentered(context, hint, center.X + (iconWidth + gap) / 2, y + 5);
                }
            }
        }
    }

    /// <summary>An outline of a mouse with its wheel, filling <paramref name="box"/>.</summary>
    private static void DrawMouse(DrawingContext context, Rect box, IBrush? brush)
    {
        var pen = new Pen(brush, 1.1);
        context.DrawRectangle(null, pen, box, box.Width / 2, box.Width / 2);
        context.DrawLine(pen, new Point(box.Center.X, box.Top + 2), new Point(box.Center.X, box.Top + 4.5));
    }

    private void DrawLabel(DrawingContext context, DonutItem item, Point center, double radius, double angle,
        double maxWidth, double maxHeight, Typeface bold, Typeface regular, bool hot)
    {
        var foreground = hot ? OnAccentBrush : item.IsEnabled ? Foreground : SecondaryForeground;
        var width = Math.Clamp(maxWidth, 30, 120);
        var text = KeepKoreanWords(item.Label);
        var label = MakeText(text, bold, LabelSize(text, bold, width), foreground, width, maxLines: 2);
        var showCaption = !string.IsNullOrEmpty(item.Caption) &&
                          !string.Equals(item.Caption, item.Label, StringComparison.OrdinalIgnoreCase) &&
                          label.Height + 14 < maxHeight;
        var caption = showCaption
            ? MakeCaption(item.Caption!, regular, hot ? OnAccentBrush : SecondaryForeground, width, label.Height, maxHeight)
            : null;

        var total = label.Height + (caption?.Height ?? 0);
        var anchor = DonutGeometry.PointAt(center, radius, angle);
        var y = anchor.Y - total / 2;
        DrawCentered(context, label, anchor.X, y);
        if (caption is not null)
            DrawCentered(context, caption, anchor.X, y + label.Height);
    }

    /// <summary>
    /// A slice's caption under its name: one line, or two where the slice is deep enough, so a caption wider than the
    /// slice ("Same format" on a small donut) wraps instead of ending in "Same for…".
    /// </summary>
    internal static FormattedText MakeCaption(string caption, Typeface typeface, IBrush? brush, double width,
        double labelHeight, double maxHeight) =>
        MakeText(caption, typeface, 10.5, brush, width, maxLines: labelHeight + 28 < maxHeight ? 2 : 1);

    private const double LabelFontSize = 13;
    private const double MinLabelFontSize = 10;
    private readonly Dictionary<(string Text, Typeface Typeface, double Width), double> _labelSizes = new();

    /// <summary>
    /// The size a slice's name is drawn at: 13, or smaller (down to 10) when one of its words is wider than the slice
    /// (<paramref name="width"/>), which would otherwise break in the middle ("Grayscal|e" on a thin slice). Words in
    /// Chinese or Japanese are left out: those break between any two characters.
    /// </summary>
    internal double LabelSize(string text, Typeface typeface, double width)
    {
        var line = Math.Round(width);
        var key = (text, typeface, line);
        if (_labelSizes.TryGetValue(key, out var size))
            return size;
        if (_labelSizes.Count >= MaxCachedWedges)
            _labelSizes.Clear();
        var widest = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Any(IsHanOrKana))
            .Select(word => new FormattedText(word, CultureInfo.CurrentUICulture, TextDirection, typeface, LabelFontSize, null).Width)
            .DefaultIfEmpty(0)
            .Max();
        // A pixel to spare: a word exactly as wide as the line still wraps.
        size = widest < line ? LabelFontSize : Math.Max(MinLabelFontSize, LabelFontSize * (line - 1) / widest);
        return _labelSizes[key] = size;
    }

    /// <summary>Chinese characters, kana and the CJK symbols and full-width forms: a line may break between any two.</summary>
    private static bool IsHanOrKana(char c) =>
        c is >= '\u2E80' and <= '\u312F' or >= '\u3190' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';

    /// <summary>
    /// Korean is written with spaces between words and wraps there, but the text layout also breaks between any two
    /// Hangul syllables ("ZIP 하나|로"). A word joiner between them keeps each word on one line.
    /// </summary>
    internal static string KeepKoreanWords(string text)
    {
        if (!text.Any(IsHangul))
            return text;
        var kept = new System.Text.StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            kept.Append(text[i]);
            if (i + 1 < text.Length && IsHangul(text[i]) && IsHangul(text[i + 1]))
                kept.Append('\u2060');
        }
        return kept.ToString();
    }

    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3';

    private Point EditButtonCenter(int index)
    {
        var mid = DonutGeometry.MidAngle(index, Count);
        return DonutGeometry.PointAt(Center, OuterRadius - 14 + PopOut, mid);
    }

    private void DrawEditButton(DrawingContext context, Point at)
    {
        context.DrawEllipse(AccentBrush, new Pen(CenterBrush, 2), at, 12, 12);
        var bounds = PencilIcon.Bounds;
        var s = 13 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(at.X, at.Y);
        using (context.PushTransform(matrix))
            context.DrawGeometry(OnAccentBrush, null, PencilIcon);
    }

    /// <summary>
    /// Draws text horizontally centred on <paramref name="centerX"/>.
    /// The text is laid out with <see cref="TextAlignment.Center"/> inside a box of <see cref="FormattedText.MaxTextWidth"/>,
    /// so the box (not the measured <see cref="FormattedText.Width"/>) must be centred — otherwise short labels
    /// drift right by (box − text) / 2.
    /// </summary>
    internal static void DrawCentered(DrawingContext context, FormattedText text, double centerX, double top) =>
        context.DrawText(text, new Point(centerX - text.MaxTextWidth / 2, top));

    private Pen? BorderPen() => SliceBorderBrush is null ? null : new Pen(SliceBorderBrush, 1);

    private static FormattedText MakeText(string text, Typeface typeface, double size, IBrush? brush, double maxWidth, int maxLines = 1) =>
        new(text, CultureInfo.CurrentUICulture, TextDirection, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private static FlowDirection TextDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static IBrush? WithOpacity(IBrush? brush, double opacity) => brush switch
    {
        ISolidColorBrush solid => new SolidColorBrush(solid.Color, solid.Opacity * opacity),
        _ => brush,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>The translucent slice where a dragged preset would land, with its name.</summary>
    private void DrawPreview(DrawingContext context, Point center, double r1, double r2, Typeface bold, Typeface regular)
    {
        var mid = _previewMid.Value;
        var span = _previewSpan.Value;
        var wedge = CreateWedge(center, r1, r2, mid, span);
        var outline = new Pen(WithOpacity(AccentBrush, 0.9), 1.5, new DashStyle([4, 3], 0));
        context.DrawGeometry(WithOpacity(AccentBrush, 0.22), outline, wedge);
        if (_previewLabel is null || span < 0.25)
            return;
        using (context.PushOpacity(Math.Clamp((span - 0.25) / 0.3, 0, 1) * 0.9))
            DrawLabel(context, new DonutItem { Id = "", Label = _previewLabel }, center, (r1 + r2) / 2, mid,
                span * (r1 + r2) / 2 - 10, r2 - r1 - 16, bold, regular, hot: false);
    }

    /// <summary>
    /// How round the slices' corners are: half the theme's softness (0 to 28), so the largest setting still leaves the
    /// slices clearly apart. <see cref="DonutGeometry.Wedge"/> shrinks it for slices too thin for it.
    /// </summary>
    internal double SliceCornerRadius => Math.Clamp(CornerRadius.TopLeft, 0, 28) * 0.5;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, int index, int count) =>
        count == 1 ? CreateRing(c, r1, r2) : CreateWedge(c, r1, r2, DonutGeometry.MidAngle(index, count), DonutGeometry.Span(count));

    /// <summary>A rounded slice of <paramref name="span"/> radians centred on <paramref name="mid"/> (a ring when it is all of it).</summary>
    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span) =>
        CreateWedge(c, r1, r2, mid, span, SliceCornerRadius);

    // Slices are drawn every frame while the donut animates, but their shapes only change with the size, the layout
    // or the softness: the shapes are kept and reused. Animations move them with transforms; only the edit mode's
    // sliding slices make new ones, and the cache starts over when it fills up.
    private readonly Dictionary<(Point C, double R1, double R2, double Mid, double Span, double Corner), StreamGeometry> _wedges = new();
    private const int MaxCachedWedges = 256;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        var key = (c, r1, r2, mid, span, cornerRadius);
        if (_wedges.TryGetValue(key, out var cached))
            return cached;
        if (_wedges.Count >= MaxCachedWedges)
            _wedges.Clear();
        return _wedges[key] = BuildWedge(c, r1, r2, mid, span, cornerRadius);
    }

    /// <summary>A slice with a constant-width gap to its neighbours and corners rounded by <paramref name="cornerRadius"/>.</summary>
    private static StreamGeometry BuildWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        if (span >= 2 * Math.PI - 0.001)
            return CreateRing(c, r1, r2);

        var outline = DonutGeometry.Wedge(c, r1, r2, mid, span, GapPixels, cornerRadius);
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

    /// <summary>Full ring (used for a single slice and for the empty donut).</summary>
    private static StreamGeometry CreateRing(Point c, double r1, double r2)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        foreach (var r in new[] { r2, r1 })
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true);
            ctx.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return geometry;
    }
}
