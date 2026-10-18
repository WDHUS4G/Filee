// Group slices of the donut: a slice that converts nothing itself but fans out a small ring of presets outside it,
// where they are clicked or dropped on like ordinary slices.
//
//   Pointer / drag  → resting on a group's slice (ExpandDelay), or moving outward on it, opens its ring; the ring
//                     stays open on its way out to it (the gap, the neighbours' outer half) and folds on another slice,
//                     the hole, or going round the outside away from it.
//   Keyboard        → arrows / Tab move, Enter opens a group or converts, Esc / Backspace fold a ring first.
//   See-through     → Windows and macOS let the pointer and drags through fully transparent pixels: a nearly
//                     transparent backdrop and LeftThroughSeam keep a group from folding when it only seems to leave.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    /// <summary>Gap between a popped-out slice and the ring a group fans out.</summary>
    private const double SubGap = 6;

    /// <summary>Thickness of the ring a group fans out.</summary>
    private const double SubBand = 46;

    /// <summary>Extra room around the donut for the ring a group fans out (and its labels, which reach past it).</summary>
    public const double SubRingSpace = SubGap + SubBand + 10;

    /// <summary>Width and height of a donut of <paramref name="outerRadius"/>, with room for group rings when asked.</summary>
    public static double SideFor(double outerRadius, bool subRing) => 2 * (outerRadius + PopOut + 14 + (subRing ? SubRingSpace : 0));

    // Group slices: the one fanned out (-1 none), the one still drawn while its ring folds away, and the hovered
    // slice of the ring.
    private int _expanded = -1;
    private int _shownExpanded = -1;
    private int _subHover = -1;
    private Spring _expand;

    // A group slice the pointer is on but hasn't opened yet (-1 none), and the closest to the centre it has been on it.
    private int _pendingGroup = -1;
    private IDisposable? _expandTimer;
    private double _pendingDistance;

    /// <summary>How long the pointer rests on a group slice before it opens: passing over on the way elsewhere doesn't.</summary>
    internal static readonly TimeSpan ExpandDelay = TimeSpan.FromMilliseconds(160);

    /// <summary>Moving this far outward on a group slice opens it at once (the user is heading for its ring).</summary>
    private const double OutwardPixels = 10;

    // Keyboard: set by a handled key until the mouse moves KeyboardJitter away from where it was then.
    private bool _keyboardHold;

    // A ring opened with Enter: the mouse, resting in the hole as the donut opened at the cursor, crosses the hole on
    // its way to it. Until it reaches the group (its slice or ring) the hole doesn't fold it.
    private bool _ringFromKeys;
    private Point? _keyAnchor;
    private Point? _lastPointer;
    private const double KeyboardJitter = 6;

    /// <summary>The slices of the group fanned out right now, or null.</summary>
    private IReadOnlyList<DonutItem>? ExpandedChildren =>
        !IsEditMode && _expanded >= 0 && _expanded < Count ? Items![_expanded].Children : null;

    private (double Inner, double Outer) SubRadii => (OuterRadius + PopOut + SubGap, OuterRadius + PopOut + SubGap + SubBand);

    /// <summary>How far past an open ring (and the gap before it) the pointer still counts as on its way to it.</summary>
    private const double BackdropMargin = 8;

    /// <summary>True when <paramref name="p"/> is in the direction of <paramref name="group"/>'s ring, at any distance.</summary>
    private bool InFan(Point p, int group)
    {
        var count = group >= 0 && group < Count ? Items![group].Children?.Count ?? 0 : 0;
        return count > 0 && DonutGeometry.SubIndexAt(Center, p, 0, double.MaxValue, DonutGeometry.MidAngle(group, Count),
            count, SubSpanOf(count)) >= 0;
    }

    /// <summary>
    /// True when <paramref name="p"/> is on the outer half of another slice, in the direction of the open ring: the
    /// pointer crosses there on its way to the ring's outer slices.
    /// </summary>
    private bool OnWayToRing(Point p) =>
        _expanded >= 0 && Distance(p, Center) > (InnerRadius + OuterRadius) / 2 && InFan(p, _expanded);

    /// <summary>Width of each slice of a ring of <paramref name="count"/>.</summary>
    private double SubSpanOf(int count) =>
        DonutGeometry.SubSpan(DonutGeometry.Span(Count), count, (SubRadii.Inner + SubRadii.Outer) / 2);

    /// <summary>The middle of slice <paramref name="sub"/> of group <paramref name="group"/>'s ring (tests).</summary>
    internal Point SubSliceCenter(int group, int sub)
    {
        var count = Items![group].Children!.Count;
        var mid = DonutGeometry.SubMidAngle(DonutGeometry.MidAngle(group, Count), sub, count, SubSpanOf(count));
        return DonutGeometry.PointAt(Center, (SubRadii.Inner + SubRadii.Outer) / 2, mid);
    }

    /// <summary>
    /// Fans a group out once the pointer means it: after resting on its slice for <see cref="ExpandDelay"/>, or at once
    /// when it moves outward on it (<paramref name="at"/>, when known), even straight past its edge. Keeps it open while
    /// the pointer is on its ring or crossing the gap to it; another slice, the hole or going round the outside away
    /// from the ring folds it away.
    /// </summary>
    private void TrackGroups(DonutHit hit, Point? at)
    {
        if (IsEditMode)
            return;
        var before = (_expanded, _pendingGroup);
        var sub = -1;
        switch (hit.Kind)
        {
            case DonutHitKind.Segment when hit.Index == _expanded:
                _ringFromKeys = false; // the mouse reached it: from here on it leads
                break;
            case DonutHitKind.Segment when at is { } across && OnWayToRing(across):
                // A neighbour's outer half under the open ring, crossed on the way to its outer slices: that neighbour is
                // what a click or a drop here converts with, but the ring stays open.
                ClearPending();
                break;
            case DonutHitKind.Segment when hit.Index >= 0 && hit.Index < Count && Items![hit.Index].CanExpand:
                _expanded = -1;
                var distance = at is { } p ? Distance(p, Center) : 0;
                if (_pendingGroup != hit.Index)
                {
                    ClearPending(); // its timer too: back on this slice within the delay, it waits the full delay again
                    _pendingGroup = hit.Index;
                    _pendingDistance = distance;
                    _expandTimer = DispatcherTimer.RunOnce(ExpandPendingGroup, ExpandDelay);
                }
                else if (at is not null && distance - _pendingDistance >= OutwardPixels)
                {
                    ExpandPendingGroup();
                }
                else if (at is not null)
                {
                    _pendingDistance = Math.Min(_pendingDistance, distance); // outward from its innermost point
                }
                break;
            case DonutHitKind.SubSegment:
                _ringFromKeys = false;
                sub = hit.Sub;
                break;
            case DonutHitKind.Outside when _pendingGroup >= 0 && at is { } flick && InFan(flick, _pendingGroup) &&
                                           Distance(flick, Center) <= SubRadii.Outer + BackdropMargin:
                ExpandPendingGroup(); // flicked straight out to where its ring opens: no need to wait
                break;
            case DonutHitKind.Outside:
                ClearPending(); // left the slice before it opened: it stays closed
                if (_expanded >= 0 && at is { } q && !InFan(q, _expanded))
                    _expanded = -1; // went round the outside, away from the ring
                break;
            case DonutHitKind.Center when _ringFromKeys:
                sub = _subHover; // on its way out of the hole to the ring
                break;
            default:
                _expanded = -1;
                _ringFromKeys = false;
                ClearPending();
                break;
        }
        SetSub(sub);
        // Redraw only when something changed: this runs on every pointer move and drag-over event.
        if ((_expanded, _pendingGroup) != before)
            StartAnimation();
    }

    private void ClearPending()
    {
        _pendingGroup = -1;
        _expandTimer?.Dispose();
        _expandTimer = null;
    }

    /// <summary>Opens the group the pointer rests on (the timer of <see cref="TrackGroups"/>; tests call it directly).</summary>
    internal void ExpandPendingGroup()
    {
        if (_pendingGroup < 0 || _pendingGroup >= Count)
            return;
        Expand(_pendingGroup);
    }

    private void Expand(int group)
    {
        _ringFromKeys = false;
        // Another group's ring still folding away: this one grows out of its own slice, not from half open.
        if (_shownExpanded != group)
            _expand = default;
        _expanded = _shownExpanded = group;
        ClearPending();
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    private void SetSub(int sub)
    {
        if (_subHover == sub)
            return;
        _subHover = sub;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    /// <summary>
    /// Keyboard (click mode): arrows and Tab move around the donut, Enter converts or opens a group; in a group's ring
    /// the arrows move between its slices, Enter converts and Esc / Backspace go back to the donut.
    /// Returns false for keys left to the window (Esc on the donut closes it).
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (!HandleKeyCore(key, modifiers))
            return false;
        // The keyboard has the donut now: the mouse resting where it was (or nudged a pixel, or Windows re-sending its
        // position) doesn't take it back; moving it on purpose does.
        _keyboardHold = true;
        _keyAnchor = _lastPointer;
        return true;
    }

    private bool HandleKeyCore(Key key, KeyModifiers modifiers)
    {
        if (IsEditMode || _progressMode || Count == 0)
            return false;
        var step = key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            Key.Tab => modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1,
            _ => 0,
        };

        // Esc / Backspace fold an open ring first, however it was opened; only then does Esc close the donut.
        if (ExpandedChildren is not null && key is Key.Escape or Key.Back)
        {
            var group = _expanded;
            CollapseGroup();
            SetHover(group);
            return true;
        }

        if (ExpandedChildren is { } children && _subHover >= 0)
        {
            if (key is Key.Enter or Key.Space)
            {
                if (children[_subHover].IsEnabled)
                    SubSegmentInvoked?.Invoke(this, (_expanded, _subHover));
                return true;
            }
            if (step == 0)
                return false;
            SetSub((_subHover + step + children.Count) % children.Count);
            return true;
        }

        var current = ActiveIndex;
        switch (key)
        {
            case Key.Enter or Key.Space when current >= 0:
                if (Items![current].CanExpand)
                {
                    Expand(current);
                    SetSub(0);
                    _ringFromKeys = true;
                }
                else if (Items[current].IsEnabled)
                {
                    SegmentInvoked?.Invoke(this, current);
                }
                return true;
        }
        if (step == 0)
            return false;
        CollapseGroup();
        SetHover(current < 0 ? (step > 0 ? 0 : Count - 1) : (current + step + Count) % Count);
        return true;
    }

    /// <summary>
    /// True when dropping or clicking at <paramref name="hit"/> converts: an enabled slice that isn't a group, or an
    /// enabled slice of a group's ring. Elsewhere the drag shows "no drop".
    /// </summary>
    public bool Converts(DonutHit hit) => hit.Kind switch
    {
        DonutHitKind.Segment => hit.Index >= 0 && hit.Index < Count && Items![hit.Index] is { IsGroup: false, IsEnabled: true },
        DonutHitKind.SubSegment => hit.Index >= 0 && hit.Index < Count && Items![hit.Index].Children is { } children &&
                                   hit.Sub >= 0 && hit.Sub < children.Count && children[hit.Sub].IsEnabled,
        _ => false,
    };

    /// <summary>
    /// A drag from outside left the window, last seen at <paramref name="lastAt"/>: clears what it lit up and folds a
    /// group. Unless it was still on the donut or on the fan of a group that is open or about to open: Windows and macOS
    /// report a drag leaving when it crosses a see-through pixel, or when the window's shape changes under it (as the
    /// backdrop is drawn for a group about to open); taken as leaving, the group would never get to open.
    /// </summary>
    public void ExternalDragLeft(Point? lastAt)
    {
        if (lastAt is { } at && LeftThroughSeam(at))
            return;
        CollapseGroup();
        SetExternalHighlight(-1);
    }

    /// <summary>Folds an open group away (the drag left the window, or files were dropped).</summary>
    public void CollapseGroup()
    {
        _expanded = -1;
        _ringFromKeys = false;
        ClearPending();
        SetSub(-1);
        StartAnimation();
    }

    /// <summary>
    /// Highlights what a drag is over from outside: a slice, a slice of a group's ring, or the hole.
    /// <paramref name="at"/> (in this control) lets a group open at once when the drag heads outward on it.
    /// </summary>
    public void SetExternalHit(DonutHit hit, Point? at = null)
    {
        TrackGroups(hit, at);
        SetExternalHighlight(hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment ? hit.Index : -1,
            hit.Kind == DonutHitKind.Center);
    }

    /// <summary>
    /// Nearly transparent fill (alpha 1/255) under a group's ring and the gap to it, while it is open or about to
    /// open. Windows passes the mouse and drags through fully transparent pixels of a layered window, so without it the
    /// pointer "leaves" the donut in the gap and the ring folds away (or never opens) before it is reached.
    /// </summary>
    private static readonly IBrush HitBackdrop = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    /// <summary>
    /// <see cref="HitBackdrop"/> over the donut and the fan of <paramref name="group"/>. The donut has see-through seams
    /// (between the hole and the slices, between slices): the pointer crossing one on its way to the ring would leave
    /// the window there too. Around the donut, outside the fan, clicks and drags still go through to the windows
    /// below, as without groups.
    /// </summary>
    private void DrawHitBackdrop(DrawingContext context, Point center, int group, int count)
    {
        var reach = SubRadii.Outer + BackdropMargin;
        var span = Math.Min(2 * Math.PI, SubSpanOf(count) * count + 2 * BackdropMargin / reach);
        context.DrawEllipse(HitBackdrop, null, center, OuterRadius + PopOut, OuterRadius + PopOut);
        context.DrawGeometry(HitBackdrop, null, CreateWedge(center, OuterRadius, reach, DonutGeometry.MidAngle(group, Count), span));
    }

    /// <summary>
    /// How far inside the edge of the donut and the ring's backdrop the pointer must last have been for a "leave" to be
    /// a see-through seam: one seen closer to the edge than this went out over it.
    /// </summary>
    private const double LeaveEdge = 12;

    /// <summary>
    /// True when the pointer "left" at <paramref name="p"/>, well inside the donut or the backdrop of the group that is
    /// open or about to open (<see cref="DrawHitBackdrop"/>): a see-through pixel let it through there (Windows and
    /// macOS do that), it didn't really leave. Last seen near the edge of that area, or outside it, it did.
    /// </summary>
    internal bool LeftThroughSeam(Point p)
    {
        var group = _expanded >= 0 ? _expanded : _pendingGroup;
        if (group < 0 || group >= Count || IsEditMode)
            return false;
        var distance = Distance(p, Center);
        if (distance <= OuterRadius + PopOut - LeaveEdge)
            return true;
        var count = Items![group].Children?.Count ?? 0;
        var reach = SubRadii.Outer + BackdropMargin;
        if (count == 0 || distance > reach - LeaveEdge)
            return false;
        // Inside the backdrop's wedge, and LeaveEdge away from its sides.
        var halfSpan = Math.Min(2 * Math.PI, SubSpanOf(count) * count + 2 * BackdropMargin / reach) / 2;
        var inside = halfSpan - Math.Abs(Math.IEEERemainder(DonutGeometry.AngleOf(Center, p) - DonutGeometry.MidAngle(group, Count), 2 * Math.PI));
        return inside > 0 && (inside >= Math.PI / 2 || distance * Math.Sin(inside) >= LeaveEdge);
    }

    /// <summary>The ring an open group fans out, growing out of its slice.</summary>
    private void DrawSubRing(DrawingContext context, Point center, Typeface bold, Typeface regular)
    {
        var group = _shownExpanded;
        var children = Items![group].Children!;
        var parentMid = DonutGeometry.MidAngle(group, Count);
        var subSpan = SubSpanOf(children.Count);
        var (inner, outer) = SubRadii;
        var t = Math.Max(0, _expand.Value);
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);
        // Grows outward from the group's slice: scaled about the point where it meets the donut.
        var anchor = DonutGeometry.PointAt(center, inner, parentMid);
        var scale = 0.8 + 0.2 * t;
        var transform = Matrix.CreateTranslation(-anchor.X, -anchor.Y) * Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation(anchor.X, anchor.Y);

        using (context.PushTransform(transform))
        using (context.PushOpacity(Math.Clamp(t, 0, 1) * opening))
        {
            for (var k = 0; k < children.Count; k++)
            {
                var item = children[k];
                var mid = DonutGeometry.SubMidAngle(parentMid, k, children.Count, subSpan);
                var wedge = CreateWedge(center, inner, outer, mid, subSpan);
                context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);
                var hot = k == _subHover && group == _expanded && item.IsEnabled;
                if (hot)
                    context.DrawGeometry(WithOpacity(AccentBrush, 0.82), null, wedge);
                DrawLabel(context, item, center, (inner + outer) / 2, mid, subSpan * (inner + outer) / 2 - 8,
                    SubBand - 8, bold, regular, hot);
            }
        }
    }
}
