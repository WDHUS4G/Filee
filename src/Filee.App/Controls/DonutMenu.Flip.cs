// Flipping the donut to its other face: every profile has a front and a back list of slices, and Tab turns the open
// donut over. Each slice folds about its spoke, starting under the pointer and spreading both ways; the hub stays
// where it is and says how to flip ("Tab ↻ back").
//
// The flip key (Tab unless the user records another in Settings → Shortcuts) and the mouse wheel flip it:
//   Click mode → the key reaches the focused window (HandleKey), the wheel the donut (OnPointerWheelChanged).
//   Drag mode  → the window has no keyboard focus during an OS drag: RadialController flips when the global hook
//                sees the key or the wheel (TriggerService.FlipKeyPressed / FlipWheelTurned).
//
// The owner swaps Items immediately when FaceSwapping is raised (RadialWindow → RadialViewModel). Old slices are
// retained for drawing only; hit-testing and drops always use the new face.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    /// <summary>There is another face with slices for these files: the flip key or the wheel turns to it.</summary>
    public static readonly StyledProperty<bool> CanFlipProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(CanFlip));

    /// <summary>Small line at the bottom of the hub, e.g. "Tab ↻ back" (null for none).</summary>
    public static readonly StyledProperty<string?> CenterHintProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterHint));

    /// <summary>A small mouse in front of <see cref="CenterHint"/>: the wheel flips (and no key does).</summary>
    public static readonly StyledProperty<bool> CenterHintWheelProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(CenterHintWheel));

    /// <summary>The key that flips the donut in click mode, as the settings name it ("Tab", "F2"); null for none.</summary>
    public static readonly StyledProperty<string?> FlipKeyProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(FlipKey));

    /// <summary>A turn of the mouse wheel over the donut flips it (click mode; drag mode reads the wheel globally).</summary>
    public static readonly StyledProperty<bool> FlipWithWheelProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(FlipWithWheel));

    /// <summary>
    /// A wheel event this soon after the previous one belongs to the same turn of the wheel: one flip per turn, however
    /// many notches (or a touchpad's many small steps) it has, so a fast scroll doesn't flip back and forth.
    /// </summary>
    internal const long WheelQuietMs = 300;

    private long? _lastWheel;

    internal const double SliceTurn = 0.12;
    internal const double Stagger = 0.08;
    internal const double FlipDuration = SliceTurn + Stagger;

    private double _flipClock = -1;   // seconds into the running flip; -1 none
    private bool _flipQueued;         // Tab again during a flip: flip once more after it
    private bool _swapping;           // FaceSwapping is being handled (Items change for the flip, not for new files)
    private Point? _lastExternalAt;   // where a drag from outside was last seen on the donut
    private OldSlice[]? _oldFace;
    private OldGroup? _oldGroup;
    private double[] _oldDelay = [];
    private double[] _newDelay = [];
    private int _oldOriginIndex = -1;
    private double? _pointingAngle;
    private bool _pointingAtSlice;

    private sealed class OldSlice
    {
        public required DonutItem Item { get; init; }
        public double Mid { get; init; }
        public double Span { get; init; }
        public double Open { get; init; }
        public double Hover { get; set; }
        public double Emphasis { get; set; }
        public bool Hot { get; set; }
        public bool Dimmed { get; init; }
    }

    private sealed record OldGroup(int Index, IReadOnlyList<DonutItem> Children, double Expansion,
        int SubHover, bool Expanded, double Opening, double Span);

    public bool CanFlip { get => GetValue(CanFlipProperty); set => SetValue(CanFlipProperty, value); }
    public string? CenterHint { get => GetValue(CenterHintProperty); set => SetValue(CenterHintProperty, value); }
    public bool CenterHintWheel { get => GetValue(CenterHintWheelProperty); set => SetValue(CenterHintWheelProperty, value); }
    public string? FlipKey { get => GetValue(FlipKeyProperty); set => SetValue(FlipKeyProperty, value); }
    public bool FlipWithWheel { get => GetValue(FlipWithWheelProperty); set => SetValue(FlipWithWheelProperty, value); }

    /// <summary>
    /// Drag mode: the global hook flips on the flip key (RadialController), so the window's own key press doesn't flip
    /// too. macOS can give the donut's window the keyboard during a drag, and one press then flipped twice (#504).
    /// </summary>
    public bool FlipKeyFromHook { get; set; }

    /// <summary>
    /// A wheel event at <paramref name="nowMs"/> (milliseconds, any clock): flips on the first one of a turn of the wheel
    /// and ignores the rest of it (<see cref="WheelQuietMs"/>). Returns true when it flipped.
    /// </summary>
    public bool FlipFromWheel(long nowMs)
    {
        var sameTurn = _lastWheel is { } last && nowMs - last < WheelQuietMs;
        _lastWheel = nowMs;
        return !sameTurn && Flip();
    }

    /// <summary>Click mode: the wheel over the donut flips it; nothing under the donut scrolls meanwhile.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!FlipWithWheel || IsEditMode || _progressMode || e.Delta.Y == 0)
            return;
        FlipFromWheel(Environment.TickCount64);
        e.Handled = true;
    }

    /// <summary>
    /// The flip starts: the owner immediately shows the other face by changing
    /// <see cref="Items"/>, <see cref="CanFlip"/> and <see cref="CenterHint"/>.
    /// </summary>
    public event EventHandler? FaceSwapping;

    /// <summary>A flip is running.</summary>
    internal bool IsFlipping => _flipClock >= 0;

    internal double? FlipOrigin { get; private set; }
    internal bool HasOldFace => _oldFace is not null;
    internal bool HasOldGroupRing => _oldGroup is not null;
    internal int CachedWedgeCount => _wedges.Count;

    private double Progress(bool oldFace, int index)
    {
        var delays = oldFace ? _oldDelay : _newDelay;
        return Math.Clamp((_flipClock - delays[index]) / SliceTurn, 0, 1);
    }

    internal double SliceFlipWidth(bool oldFace, int index)
    {
        if (oldFace && (_oldFace is null || index < 0 || index >= _oldFace.Length))
            return 0;
        if (!oldFace && (index < 0 || index >= Count))
            return 0;
        if (!IsFlipping)
            return oldFace ? 0 : 1;
        var u = Progress(oldFace, index);
        if (oldFace ? u >= 0.5 : u < 0.5)
            return 0;
        return Math.Max(Math.Abs(Math.Cos(Math.PI * u)), 0.01);
    }

    internal Matrix SliceFlipTransform(bool oldFace, int index)
    {
        if (!IsFlipping)
            return Matrix.Identity;
        var width = SliceFlipWidth(oldFace, index);
        var mid = oldFace ? _oldFace![index].Mid : IsEditMode ? _mid[index].Value : DonutGeometry.MidAngle(index, Count);
        var center = Center;
        return Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(-mid) *
               Matrix.CreateScale(Math.Max(width, 0.01), 1) * Matrix.CreateRotation(mid) *
               Matrix.CreateTranslation(center.X, center.Y);
    }

    internal double ShadowCoverage
    {
        get
        {
            if (!IsFlipping)
                return 1;
            var coverage = 0.0;
            for (var i = 0; i < Count; i++)
                coverage += SliceFlipWidth(false, i) * DonutGeometry.Span(Count) / (2 * Math.PI);
            if (_oldFace is { } old)
                for (var i = 0; i < old.Length; i++)
                    coverage += SliceFlipWidth(true, i) * old[i].Span / (2 * Math.PI);
            return Math.Min(1, coverage);
        }
    }

    private double Delay(int index, int count)
    {
        if (count == 1 || index == DonutGeometry.IndexAt(FlipOrigin!.Value, count))
            return 0;
        return Stagger * Math.Abs(Math.IEEERemainder(DonutGeometry.MidAngle(index, count) - FlipOrigin.Value,
            2 * Math.PI)) / Math.PI;
    }

    private void BeginWave(bool incomingOnly)
    {
        FlipOrigin = incomingOnly ? 0 : _lastExternalAt is { } drag
            ? drag == Center ? 0 : DonutGeometry.AngleOf(Center, drag)
            : !_keyboardHold && _lastPointer is { } pointer
                ? pointer == Center ? 0 : DonutGeometry.AngleOf(Center, pointer)
                : ActiveIndex >= 0 ? DonutGeometry.MidAngle(ActiveIndex, Count) : 0;
        _oldDelay = _oldFace is { } old
            ? Enumerable.Range(0, old.Length).Select(i => Delay(i, old.Length)).ToArray() : [];
        _oldOriginIndex = _oldFace is { Length: > 0 } ? DonutGeometry.IndexAt(FlipOrigin.Value, _oldFace.Length) : -1;
        _pointingAngle = null;
        _pointingAtSlice = false;
    }

    private void PrepareIncoming()
    {
        _newDelay = Enumerable.Range(0, Count).Select(j =>
        {
            var delay = Delay(j, Count);
            if (_oldFace is { } previous)
                for (var i = 0; i < previous.Length; i++)
                    if (Math.Abs(Math.IEEERemainder(previous[i].Mid - DonutGeometry.MidAngle(j, Count), 2 * Math.PI)) <
                        (previous[i].Span + DonutGeometry.Span(Count)) / 2)
                        delay = Math.Max(delay, _oldDelay[i]);
            return delay;
        }).ToArray();
    }

    private void CaptureOldFace()
    {
        var n = Count;
        _oldFace = Enumerable.Range(0, n).Select(i => new OldSlice
        {
            Item = Items![i],
            Mid = DonutGeometry.MidAngle(i, n),
            Span = DonutGeometry.Span(n),
            Open = _open[i],
            Hover = _hover[i].Value,
            Emphasis = i == _dropTargetIndex ? 1 : _hover[i].Value,
            Hot = i == ActiveIndex || i == _dropTargetIndex,
            Dimmed = i == _dimmedIndex,
        }).ToArray();
        _oldGroup = _shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01 &&
                    Items![_shownExpanded].Children is { Count: > 0 } children
            ? new OldGroup(_shownExpanded, children, _expand.Value, _subHover, _expanded == _shownExpanded,
                _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1), SubSpanOf(children.Count))
            : null;
    }

    private void PointAtAngle(double angle, bool pointed = true)
    {
        if (!IsFlipping || _oldFace is not { Length: > 0 } old)
            return;
        if (_pointingAngle is { } previous && _pointingAtSlice == pointed &&
            Math.Abs(Math.IEEERemainder(previous - angle, 2 * Math.PI)) < 0.000001)
            return;
        _pointingAngle = angle;
        _pointingAtSlice = pointed;
        var oldIndex = DonutGeometry.IndexAt(angle, old.Length);
        if (oldIndex != _oldOriginIndex && _oldOriginIndex >= 0)
        {
            old[_oldOriginIndex].Hover = 0;
            old[_oldOriginIndex].Emphasis = 0;
            old[_oldOriginIndex].Hot = false;
            if (_oldGroup?.Index == _oldOriginIndex)
                _oldGroup = _oldGroup with { SubHover = -1 };
        }
        if (!pointed)
        {
            InvalidateVisual();
            return;
        }
        if (_flipClock < _oldDelay[oldIndex])
            _oldDelay[oldIndex] = _flipClock;
        var newIndex = DonutGeometry.IndexAt(angle, Count);
        if (newIndex >= 0 && Progress(false, newIndex) < 0.5)
            _newDelay[newIndex] = _flipClock - SliceTurn / 2;
        InvalidateVisual();
    }

    private void PointAtSelection()
    {
        if (IsFlipping && ActiveIndex >= 0)
            PointAtAngle(DonutGeometry.MidAngle(ActiveIndex, Count));
    }

    /// <summary>
    /// Immediately switches to the other face (Tab), then folds the old slices and any open group ring away in a wave.
    /// Without animations only the face changes. Returns false when there is nothing to flip to (<see cref="CanFlip"/>), in the editor or
    /// while the donut shows a conversion's progress.
    /// </summary>
    public bool Flip()
    {
        if (IsEditMode || _progressMode || _closing)
            return false;
        if (IsFlipping)
        {
            // Pressed again before the turn ended: once more afterwards (twice cancels that out again).
            _flipQueued = !_flipQueued;
            return true;
        }
        if (!CanFlip)
            return false;
        if (!Motion.Enabled)
        {
            CollapseGroup();
            SwapFaces();
            return true;
        }
        CaptureOldFace();
        BeginWave(incomingOnly: false);
        var pointed = _lastExternalAt is { } drag && HitTest(drag).Kind is DonutHitKind.Segment or DonutHitKind.SubSegment
            ? drag : !_keyboardHold && _lastPointer is { } pointer &&
                     HitTest(pointer).Kind is DonutHitKind.Segment or DonutHitKind.SubSegment ? pointer : (Point?)null;
        CollapseGroup();
        SwapFaces();
        PrepareIncoming();
        _flipClock = 0;
        // SwapFaces resets the current highlight; only a slice actually under the pointer/drag bypasses overlap.
        if (pointed is { } at)
        {
            var angle = DonutGeometry.AngleOf(Center, at);
            _pointingAngle = angle;
            _pointingAtSlice = true;
            var index = DonutGeometry.IndexAt(angle, Count);
            _newDelay[index] = Delay(index, Count);
        }
        StartAnimation();
        return true;
    }

    /// <summary>
    /// The incoming slice wave for items already swapped by the editor's Front / Back switch.
    /// Slices unfold from the top; nothing runs without animations.
    /// </summary>
    public void PlayFlipIn()
    {
        if (!Motion.Enabled)
            return;
        _oldFace = null;
        _oldGroup = null;
        BeginWave(incomingOnly: true);
        PrepareIncoming();
        _flipClock = SliceTurn / 2;
        _flipQueued = false;
        StartAnimation();
    }

    /// <summary>Advances a running flip. Returns true while it needs more frames.</summary>
    private bool StepFlip(double dt)
    {
        if (_flipClock < 0)
            return false;
        _flipClock += dt;
        if (_flipClock < FlipDuration)
            return true;
        EndFlip();
        return IsFlipping; // a queued flip started
    }

    /// <summary>Ends a running wave on the new face: animations off or frames stalled.</summary>
    private void FinishFlip()
    {
        while (IsFlipping)
        {
            EndFlip();
        }
    }

    private void EndFlip()
    {
        _flipClock = -1;
        _oldFace = null;
        _oldGroup = null;
        FlipOrigin = null;
        if (!_flipQueued)
            return;
        _flipQueued = false;
        Flip();
    }

    private void CancelFlip()
    {
        _flipClock = -1;
        _flipQueued = false;
        _oldFace = null;
        _oldGroup = null;
        FlipOrigin = null;
    }

    /// <summary>
    /// Has the owner show the other face, then lights up what is under the drag or the mouse on it, as if it had just
    /// moved there: a keyboard selection starts over (the arrows begin at the first slice again).
    /// </summary>
    private void SwapFaces()
    {
        var dragAt = _lastExternalAt;
        var pointer = _keyboardHold ? null : _lastPointer;
        _swapping = true;
        try
        {
            FaceSwapping?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _swapping = false;
        }

        if (dragAt is { } at)
        {
            SetExternalHit(HitTest(at), at);
        }
        else if (pointer is { } p)
        {
            _lastPointer = p;
            var hit = HitTest(p);
            TrackGroups(hit, p);
            SetHover(hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment ? hit.Index : -1);
        }
        ActiveIndexChanged?.Invoke(this, ActiveIndex); // the hub's text follows the new face
        InvalidateVisual();
    }

}
