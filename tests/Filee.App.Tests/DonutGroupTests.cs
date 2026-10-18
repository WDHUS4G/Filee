// Group slices: a group opens only when the pointer means it, works from the keyboard and opens without animation
// when animations are off.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutGroupTests
{
    private const int Group = 2;

    private static DonutItem Item(string label, bool enabled = true) => new() { Id = label, Label = label, IsEnabled = enabled };

    private static DonutItem GroupOf(params DonutItem[] children) => new() { Id = "group", Label = "More", Children = children };

    /// <summary>PNG, JPG, a group of three (WEBP / PDF / TIFF) and BMP.</summary>
    private static (DonutMenu Donut, Window Window) Open() =>
        Open(Item("PNG"), Item("JPG"), GroupOf(Item("WEBP"), Item("PDF"), Item("TIFF")), Item("BMP"));

    private static (DonutMenu Donut, Window Window) Open(params DonutItem[] items)
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu { Items = items };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (donut, window);
    }

    private static Point Center(DonutMenu donut) => new(donut.Bounds.Width / 2, donut.Bounds.Height / 2);

    /// <summary>A point on the group's slice at <paramref name="radius"/> from the centre.</summary>
    private static Point OnGroup(DonutMenu donut, double radius) =>
        DonutGeometry.PointAt(Center(donut), radius, DonutGeometry.MidAngle(Group, donut.Items!.Count));

    /// <summary>The middle of the group's ring slice <paramref name="sub"/>.</summary>
    private static Point OnRing(DonutMenu donut, int sub) => donut.SubSliceCenter(Group, sub);

    private static bool IsOpen(DonutMenu donut) => donut.HitTest(OnRing(donut, 1)).Kind == DonutHitKind.SubSegment;

    [AvaloniaFact]
    public void Passing_over_a_group_on_the_way_elsewhere_does_not_open_it()
    {
        var (donut, window) = Open();
        var at = OnGroup(donut, donut.OuterRadius * 0.7);
        donut.SetExternalHit(donut.HitTest(at), at);
        Assert.False(IsOpen(donut)); // not at once

        var bmp = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.7, DonutGeometry.MidAngle(3, 4));
        donut.SetExternalHit(donut.HitTest(bmp), bmp);
        donut.ExpandPendingGroup(); // the delay runs out after the pointer moved on
        Assert.False(IsOpen(donut));
        window.Close();
    }

    [AvaloniaFact]
    public void Resting_on_a_group_or_heading_outward_opens_it()
    {
        var (donut, window) = Open();
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        donut.SetExternalHit(donut.HitTest(inner), inner);
        donut.ExpandPendingGroup(); // rested for the delay
        Assert.True(IsOpen(donut));

        donut.CollapseGroup();
        donut.SetExternalHit(donut.HitTest(inner), inner);
        var further = OnGroup(donut, donut.OuterRadius * 0.6 + 12);
        donut.SetExternalHit(donut.HitTest(further), further); // moving outward: no waiting
        Assert.True(IsOpen(donut));
        Assert.Equal(new DonutHit(DonutHitKind.SubSegment, Group, 2), donut.HitTest(OnRing(donut, 2)));

        // Came in near the rim, drifted inward, then headed out: outward counts from the innermost point.
        donut.CollapseGroup();
        foreach (var r in new[] { 0.9, 0.55, 0.55 + 12 / donut.OuterRadius })
        {
            var at = OnGroup(donut, donut.OuterRadius * r);
            donut.SetExternalHit(donut.HitTest(at), at);
        }
        Assert.True(IsOpen(donut));
        window.Close();
    }

    [AvaloniaFact]
    public void A_quick_flick_out_to_where_the_ring_opens_opens_it()
    {
        var (donut, window) = Open();
        var mid = DonutGeometry.MidAngle(Group, 4) + 0.1;
        var rim = DonutGeometry.PointAt(Center(donut), donut.OuterRadius - 4, mid); // came in near the rim
        donut.SetExternalHit(donut.HitTest(rim), rim);
        var out_ = DonutGeometry.PointAt(Center(donut), donut.OuterRadius + 30, mid); // next event: already out there
        Assert.Equal(DonutHitKind.Outside, donut.HitTest(out_).Kind);
        donut.SetExternalHit(donut.HitTest(out_), out_);
        Assert.True(IsOpen(donut)); // no waiting for the delay, and not cancelled either

        // Out in another direction instead: that is leaving, it stays closed.
        donut.CollapseGroup();
        donut.SetExternalHit(donut.HitTest(rim), rim);
        var away = DonutGeometry.PointAt(Center(donut), donut.OuterRadius + 30, mid + Math.PI);
        donut.SetExternalHit(donut.HitTest(away), away);
        donut.ExpandPendingGroup();
        Assert.False(IsOpen(donut));
        window.Close();
    }

    [AvaloniaFact]
    public void A_click_or_tap_opens_a_group_at_once()
    {
        var (donut, window) = Open();
        var invoked = 0;
        donut.SegmentInvoked += (_, _) => invoked++;
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        window.MouseDown(inner, MouseButton.Left); // a tap: no hover before it
        window.MouseUp(inner, MouseButton.Left);
        Assert.True(IsOpen(donut));
        Assert.Equal(0, invoked); // the group itself converts nothing
        window.Close();
    }

    [AvaloniaFact]
    public void Right_click_on_a_ring_slice_asks_to_edit_that_preset()
    {
        var (donut, window) = Open();
        donut.AllowContextEdit = true; // click mode
        var edits = new List<(int, int)>();
        var groupEdits = new List<int>();
        donut.SubSegmentContextRequested += (_, hit) => edits.Add(hit);
        donut.SegmentContextRequested += (_, index) => groupEdits.Add(index);
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        donut.SetExternalHit(donut.HitTest(inner), inner);
        donut.ExpandPendingGroup();

        // The mouse comes onto the ring first: right after another test's click, a headless press with no move before it
        // can miss the donut.
        window.MouseMove(OnRing(donut, 2));
        window.MouseDown(OnRing(donut, 2), MouseButton.Right);
        window.MouseUp(OnRing(donut, 2), MouseButton.Right);
        Assert.Equal([(Group, 2)], edits);
        Assert.Empty(groupEdits); // that preset, not the group
        window.Close();
    }

    [AvaloniaFact]
    public void The_preset_behind_each_ring_slice_is_the_one_edited()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Preset { Id = "test-edit-group", Name = "Formats", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-jpg"] };
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            var index = vm.Items.Count - 1;
            Assert.Same(group, vm.PresetShownAt(index, -1));
            Assert.Equal("to-jpg", vm.PresetShownAt(index, 1)!.Id);
            Assert.Null(vm.PresetShownAt(index, 2));
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    [AvaloniaFact]
    public void A_drag_reported_leaving_over_the_donut_still_opens_the_group_it_rests_on()
    {
        var (donut, window) = Open();
        var onGroup = OnGroup(donut, donut.OuterRadius * 0.6);
        donut.SetExternalHit(donut.HitTest(onGroup), onGroup);

        // As macOS does while the backdrop is drawn under the drag: "left", though still on the group's slice.
        donut.ExternalDragLeft(onGroup);
        Assert.Equal(Group, donut.ActiveIndex); // still lit
        donut.SetExternalHit(donut.HitTest(onGroup), onGroup); // and back with the next drag event
        donut.ExpandPendingGroup(); // the delay runs out
        Assert.True(IsOpen(donut));

        donut.ExternalDragLeft(OnRing(donut, 1)); // "left" from the ring: still there too
        Assert.True(IsOpen(donut));

        // Really gone: last seen away from the donut and the ring.
        donut.ExternalDragLeft(DonutGeometry.PointAt(Center(donut), donut.OuterRadius + 30, DonutGeometry.MidAngle(0, 4)));
        Assert.False(IsOpen(donut));
        Assert.Equal(-1, donut.ActiveIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void An_open_ring_holds_across_the_gap_and_folds_when_the_pointer_goes_round_elsewhere()
    {
        var (donut, window) = Open();
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        donut.SetExternalHit(donut.HitTest(inner), inner);
        donut.ExpandPendingGroup();

        // The gap between the popped-out slice and the ring still belongs to the group: no flicker on the way.
        var gap = OnGroup(donut, donut.OuterRadius + 7 + 3);
        Assert.Equal(new DonutHit(DonutHitKind.Segment, Group), donut.HitTest(gap));
        donut.SetExternalHit(donut.HitTest(gap), gap);
        Assert.True(IsOpen(donut));
        Assert.Equal(Group, donut.ActiveIndex);

        // Round the outside to the far side of the donut: away from the ring, which folds.
        var far = DonutGeometry.PointAt(Center(donut), donut.OuterRadius + 20, DonutGeometry.MidAngle(0, 4));
        donut.SetExternalHit(donut.HitTest(far), far);
        Assert.False(IsOpen(donut));
        window.Close();
    }

    /// <summary>
    /// <paramref name="count"/> slices with a group of six at <see cref="Group"/>, and a point on the outer half of the
    /// next slice, in the direction of the group's ring (crossed on the way to its outer slices).
    /// </summary>
    private static (DonutMenu Donut, Window Window, Point Neighbour) OpenCrowded(int count)
    {
        var items = Enumerable.Range(0, count)
            .Select(i => i == Group ? GroupOf([.. Enumerable.Range(0, Preset.MaxSubPresets).Select(k => Item($"R{k}"))]) : Item($"S{i}"))
            .ToArray();
        var (donut, window) = Open(items);
        var neighbour = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.9,
            DonutGeometry.MidAngle(Group, count) + DonutGeometry.Span(count) * 0.75);
        return (donut, window, neighbour);
    }

    [AvaloniaTheory]
    [InlineData(12)]
    [InlineData(8)]
    public void A_click_on_a_neighbours_outer_half_under_an_open_ring_converts_with_that_neighbour(int count)
    {
        var (donut, window, neighbour) = OpenCrowded(count);
        var invoked = new List<int>();
        donut.SegmentInvoked += (_, index) => invoked.Add(index);
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        window.MouseMove(inner);
        window.MouseDown(inner, MouseButton.Left);
        window.MouseUp(inner, MouseButton.Left);
        Assert.True(IsOpen(donut));

        window.MouseMove(neighbour);
        Assert.Equal(new DonutHit(DonutHitKind.Segment, Group + 1), donut.HitTest(neighbour));
        Assert.Equal(Group + 1, donut.ActiveIndex);
        Assert.True(IsOpen(donut)); // still open: the pointer may be on its way to the ring's outer slices

        window.MouseDown(neighbour, MouseButton.Left);
        window.MouseUp(neighbour, MouseButton.Left);
        Assert.Equal([Group + 1], invoked);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(12)]
    [InlineData(8)]
    public void A_drop_on_a_neighbours_outer_half_under_an_open_ring_converts_with_that_neighbour(int count)
    {
        var (donut, window, neighbour) = OpenCrowded(count);
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        donut.SetExternalHit(donut.HitTest(inner), inner);
        donut.ExpandPendingGroup();
        Assert.True(IsOpen(donut));

        donut.SetExternalHit(donut.HitTest(neighbour), neighbour);
        Assert.True(IsOpen(donut));
        Assert.Equal(Group + 1, donut.ActiveIndex);
        var drop = donut.HitTest(neighbour); // what RadialWindow.OnDrop hands the controller
        Assert.Equal(new DonutHit(DonutHitKind.Segment, Group + 1), drop);
        Assert.True(donut.Converts(drop));

        // Carrying on across it reaches the ring's last slice.
        var last = OnRing(donut, Preset.MaxSubPresets - 1);
        donut.SetExternalHit(donut.HitTest(last), last);
        Assert.Equal(new DonutHit(DonutHitKind.SubSegment, Group, Preset.MaxSubPresets - 1), donut.HitTest(last));
        Assert.Equal(Preset.MaxSubPresets - 1, donut.ActiveSubIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Leaving_the_window_past_the_ring_or_over_its_edge_folds_it()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu { Items = [Item("PNG"), Item("JPG"), GroupOf(Item("WEBP"), Item("PDF"), Item("TIFF")), Item("BMP")] };
        var window = new Window { Content = new Border { Padding = new Thickness(40), Child = donut }, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var origin = donut.TranslatePoint(new Point(0, 0), window)!.Value;
        Point InWindow(Point p) => new(p.X + origin.X, p.Y + origin.Y);

        // The mouse opens the ring, goes onto it and on out of the donut.
        var inner = OnGroup(donut, donut.OuterRadius * 0.6);
        window.MouseMove(InWindow(inner));
        window.MouseDown(InWindow(inner), MouseButton.Left);
        window.MouseUp(InWindow(inner), MouseButton.Left);
        window.MouseMove(InWindow(OnRing(donut, 1)));
        Assert.True(IsOpen(donut));
        window.MouseMove(new Point(2, 2));
        Assert.False(IsOpen(donut));

        // A drag last seen on the ring's outer rim went out over it: not a see-through seam.
        donut.SetExternalHit(donut.HitTest(inner), inner);
        donut.ExpandPendingGroup();
        Assert.True(IsOpen(donut));
        var middle = OnRing(donut, 1);
        var c = Center(donut);
        var along = Math.Sqrt(Math.Pow(middle.X - c.X, 2) + Math.Pow(middle.Y - c.Y, 2));
        var rim = new Point(c.X + (middle.X - c.X) * (along + 21) / along, c.Y + (middle.Y - c.Y) * (along + 21) / along);
        donut.ExternalDragLeft(rim);
        Assert.False(IsOpen(donut));
        Assert.Equal(-1, donut.ActiveIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void A_group_about_to_open_stays_closed_once_the_donut_closed()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Preset { Id = "test-close-group", Name = "Formats", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-jpg"] };
        var radial = AppHost.Get<RadialController>();
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Insert(0, group.Id);
            radial.ShowForFiles(400, 400, ["/photos/a.jpg"]);
            Dispatcher.UIThread.RunJobs();
            var donut = radial.Window!.DonutControl;
            var onGroup = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.6, DonutGeometry.MidAngle(0, donut.Items!.Count));
            donut.SetExternalHit(donut.HitTest(onGroup), onGroup); // resting on it: the dwell timer runs

            radial.Close(animate: false); // Esc, or a click elsewhere
            donut.ExpandPendingGroup(); // what the timer would do, had it outlived the donut
            Assert.Equal(-1, donut.ActiveSubIndex);
            Assert.NotEqual(DonutHitKind.SubSegment, donut.HitTest(donut.SubSliceCenter(0, 1)).Kind);
        }
        finally
        {
            radial.Close(animate: false);
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    [AvaloniaFact]
    public void Only_enabled_slices_take_a_drop_and_an_empty_group_opens_nothing()
    {
        var empty = new DonutItem { Id = "empty", Label = "Empty", Children = [], IsEnabled = false };
        var (donut, window) = Open(Item("PNG"), Item("JPG"), GroupOf(Item("WEBP", enabled: false), Item("PDF"), Item("TIFF")), empty);
        Assert.True(donut.Converts(new DonutHit(DonutHitKind.Segment, 0)));
        Assert.False(donut.Converts(new DonutHit(DonutHitKind.Segment, Group))); // a group converts nothing itself
        Assert.False(donut.Converts(new DonutHit(DonutHitKind.SubSegment, Group, 0))); // can't take these files
        Assert.True(donut.Converts(new DonutHit(DonutHitKind.SubSegment, Group, 1)));

        // A group whose presets were all deleted: still a group (no drop), but nothing to open.
        Assert.False(donut.Converts(new DonutHit(DonutHitKind.Segment, 3)));
        var onEmpty = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.6, DonutGeometry.MidAngle(3, 4));
        donut.SetExternalHit(donut.HitTest(onEmpty), onEmpty);
        donut.ExpandPendingGroup();
        donut.HandleKey(Key.Enter);
        Assert.Equal(-1, donut.ActiveSubIndex);
        Assert.Equal(new DonutHit(DonutHitKind.Outside), donut.HitTest(DonutGeometry.PointAt(Center(donut), donut.OuterRadius + 40, DonutGeometry.MidAngle(3, 4))));
        window.Close();
    }

    [AvaloniaFact]
    public void Only_a_donut_with_a_group_keeps_room_for_its_ring_when_clicked_open()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Preset { Id = "test-room-group", Name = "Formats", Kind = PresetKind.Group, SubPresetIds = ["to-png"] };
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            var vm = AppHost.Get<RadialViewModel>();

            vm.ResetForDrag(); // dragging: the files (and so the donut) aren't known yet
            Assert.True(vm.ReserveSubRing);

            vm.Load([@"C:\docs\a.pdf"], clickMode: true); // the PDF donut has no group
            vm.FitToThisDonut();
            Assert.False(vm.ReserveSubRing);
            vm.Load([@"C:\photos\a.jpg"], clickMode: true); // the image donut has
            vm.FitToThisDonut();
            Assert.True(vm.ReserveSubRing);
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    [AvaloniaFact]
    public void Rotate_is_offered_for_images_and_says_why_not_for_a_PDF()
    {
        TestServices.EnsureInitialized("en");
        var availability = AppHost.Get<PresetAvailability>();
        var rotate = BuiltInData.CreatePresets().Single(p => p.Id == "rotate-left");

        Assert.Null(availability.Check(rotate, [Filee.Core.Formats.FormatRegistry.Get("png")]));
        // PDF → PDF exists (split, extract), but it copies pages unturned: not "no engine", and no download offered.
        Assert.Equal("Rotate and flip work on images only", availability.Check(rotate, [Filee.Core.Formats.FormatRegistry.Get("pdf")]));
        // Merging PDFs takes them as they are: turning them is not on offer either.
        var mergeTurned = rotate.Clone();
        mergeTurned.TargetFormat = "pdf";
        mergeTurned.Pdf.MergeIntoSingle = true;
        Assert.Equal("Rotate and flip work on images only", availability.Check(mergeTurned, [Filee.Core.Formats.FormatRegistry.Get("pdf")]));
    }

    [AvaloniaFact]
    public void A_group_left_without_presets_says_why_on_the_donut()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var before = images.PresetIds.ToList();
        var group = new Preset { Id = "test-empty-group", Name = "Gone", Kind = PresetKind.Group, SubPresetIds = ["no-such-preset"] };
        try
        {
            store.Presets.Add(group);
            images.PresetIds.Add(group.Id);
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true);
            var index = vm.Items.Count - 1;
            Assert.True(vm.Items[index].IsGroup);
            Assert.False(vm.Items[index].IsEnabled);
            Assert.Contains("No presets left", vm.Items[index].DisabledReason);
            vm.ShowHint(index, -1, centerHot: false);
            Assert.Equal(vm.Items[index].DisabledReason, vm.CenterSubtitle); // not "Move outward to choose"
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(before);
            store.Presets.Remove(group);
        }
    }

    [AvaloniaFact]
    public void A_donut_of_just_a_group_fans_out_slices_that_dont_overlap()
    {
        var (donut, window) = Open(GroupOf(Item("WEBP"), Item("PDF"), Item("TIFF")));
        var at = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.6, 0);
        donut.SetExternalHit(donut.HitTest(at), at);
        donut.ExpandPendingGroup();
        // Each slice is hit where it is drawn: around the donut once, not one and a half times over itself.
        for (var sub = 0; sub < 3; sub++)
            Assert.Equal(new DonutHit(DonutHitKind.SubSegment, 0, sub), donut.HitTest(donut.SubSliceCenter(0, sub)));
        window.Close();
    }

    [AvaloniaFact]
    public void The_keyboard_moves_opens_converts_and_goes_back()
    {
        var (donut, window) = Open();
        var invoked = new List<int>();
        var subInvoked = new List<(int, int)>();
        donut.SegmentInvoked += (_, i) => invoked.Add(i);
        donut.SubSegmentInvoked += (_, hit) => subInvoked.Add(hit);

        Assert.True(donut.HandleKey(Key.Right)); // nothing active: the first slice
        Assert.Equal(0, donut.ActiveIndex);
        donut.HandleKey(Key.Left); // wraps around
        Assert.Equal(3, donut.ActiveIndex);
        donut.HandleKey(Key.Enter);
        Assert.Equal([3], invoked);

        donut.HandleKey(Key.Up); // to the group
        Assert.Equal(Group, donut.ActiveIndex);
        donut.HandleKey(Key.Enter); // opens it on its first slice
        Assert.True(IsOpen(donut));
        Assert.Equal(0, donut.ActiveSubIndex);
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Enter);
        Assert.Equal([(Group, 1)], subInvoked);
        Assert.Equal([3], invoked); // the group itself converts nothing

        Assert.True(donut.HandleKey(Key.Escape)); // back to the donut, not closing it
        Assert.False(IsOpen(donut));
        Assert.Equal(Group, donut.ActiveIndex);
        Assert.False(donut.HandleKey(Key.Escape)); // now Esc is the window's: it closes the donut
        window.Close();
    }

    [AvaloniaFact]
    public void Esc_folds_a_ring_the_mouse_opened_before_closing_the_donut()
    {
        var (donut, window) = Open();
        window.MouseMove(OnGroup(donut, donut.OuterRadius * 0.6));
        donut.ExpandPendingGroup();
        Assert.True(IsOpen(donut));
        Assert.Equal(-1, donut.ActiveSubIndex); // opened by resting the mouse: no ring slice chosen yet

        Assert.True(donut.HandleKey(Key.Escape));
        Assert.False(IsOpen(donut));
        Assert.False(donut.HandleKey(Key.Escape)); // now it's the window's
        window.Close();
    }

    [AvaloniaFact]
    public void A_mouse_nudge_keeps_what_the_keyboard_chose_a_real_move_takes_over()
    {
        var (donut, window) = Open();
        var c = Center(donut);
        window.MouseMove(c); // opened at the cursor: it rests in the hole
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right); // to the group
        donut.HandleKey(Key.Enter);
        Assert.Equal(0, donut.ActiveSubIndex);

        window.MouseMove(new Point(c.X + 2, c.Y + 1)); // a nudge, or Windows re-sending the position
        Assert.Equal(0, donut.ActiveSubIndex);
        Assert.True(IsOpen(donut));

        var bmp = DonutGeometry.PointAt(c, donut.OuterRadius * 0.6, DonutGeometry.MidAngle(3, 4));
        window.MouseMove(bmp); // moved on purpose: the mouse leads again
        Assert.Equal(3, donut.ActiveIndex);
        Assert.False(IsOpen(donut));
        window.Close();
    }

    [AvaloniaFact]
    public void Opened_again_the_donut_follows_the_mouse_at_once_after_the_keyboard_was_used_last_time()
    {
        var (donut, window) = Open();
        var png = DonutGeometry.PointAt(Center(donut), donut.OuterRadius * 0.6, DonutGeometry.MidAngle(0, 4));
        window.MouseMove(png);
        donut.HandleKey(Key.Right); // the keyboard took over last time, with the mouse resting on PNG

        donut.Items = [.. donut.Items!]; // opened again for new files: the window is reused
        window.MouseMove(new Point(png.X + 2, png.Y)); // a small move near where the hold was
        Assert.Equal(0, donut.ActiveIndex); // followed, not swallowed by last time's hold
        window.Close();
    }

    [AvaloniaFact]
    public void A_ring_the_keyboard_opened_stays_while_the_mouse_comes_from_the_hole_to_click_it()
    {
        var (donut, window) = Open();
        var c = Center(donut);
        window.MouseMove(c); // opened at the cursor: it rests in the hole
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right); // to the group
        donut.HandleKey(Key.Enter); // its ring opens
        Assert.True(IsOpen(donut));

        // The mouse heads for the ring: out of the hole, across the group's slice, to a ring slice.
        foreach (var radius in new[] { 15.0, donut.OuterRadius * 0.3, donut.OuterRadius * 0.7 })
        {
            window.MouseMove(OnGroup(donut, radius));
            Assert.True(IsOpen(donut), $"folded at {radius:0}");
        }
        window.MouseMove(OnRing(donut, 2));
        Assert.Equal(2, donut.ActiveSubIndex);

        // From here the mouse leads as usual: back in the hole the ring folds.
        window.MouseMove(new Point(c.X + 10, c.Y));
        Assert.False(IsOpen(donut));
        window.Close();
    }

    /// <summary>Alpha of the donut's pixel at <paramref name="p"/>, drawn on its own (as on its see-through window).</summary>
    private static byte AlphaAt(DonutMenu donut, Point p)
    {
        Dispatcher.UIThread.RunJobs();
        var size = new PixelSize((int)donut.Bounds.Width, (int)donut.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render(donut);
        var pixel = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)p.X, (int)p.Y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }
        return pixel[3];
    }

    [AvaloniaFact]
    public void An_open_ring_keeps_the_pointer_on_the_window_across_the_donuts_see_through_seams()
    {
        var (donut, window) = Open();
        var c = Center(donut);
        // The see-through ring between the hole and the slices, on the way from the hole to the group.
        var seam = OnGroup(donut, donut.OuterRadius * donut.HoleRatio - 3);
        Assert.Equal(0, AlphaAt(donut, seam)); // closed: clicks there go through, as without groups

        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Right);
        donut.HandleKey(Key.Enter);
        Assert.True(AlphaAt(donut, seam) > 0); // open: the pointer stays on the window there
        Assert.Equal(0, AlphaAt(donut, DonutGeometry.PointAt(c, donut.OuterRadius + 40, DonutGeometry.MidAngle(0, 4)))); // not around the donut

        // Where Windows or macOS still report the pointer leaving inside the donut or the ring, it didn't leave.
        Assert.True(donut.LeftThroughSeam(seam));
        Assert.True(donut.LeftThroughSeam(OnRing(donut, 0)));
        Assert.False(donut.LeftThroughSeam(new Point(0, 0)));
        window.Close();
    }

    [AvaloniaFact]
    public void With_animations_off_a_group_opens_at_once()
    {
        var (donut, window) = Open();
        try
        {
            Motion.Configure(true, () => false); // "Reduce motion" on
            var inner = OnGroup(donut, donut.OuterRadius * 0.6);
            donut.SetExternalHit(donut.HitTest(inner), inner);
            donut.ExpandPendingGroup();
            Assert.False(donut.Advance(0)); // nothing left to animate: drawn fully open on the first frame
            Assert.True(IsOpen(donut));
        }
        finally
        {
            Motion.Configure(false, () => false);
            window.Close();
        }
    }

    [Theory]
    [InlineData(5, 5)]           // top-left corner
    [InlineData(1915, 1075)]     // bottom-right corner
    [InlineData(960, 3)]         // top edge
    [InlineData(2500, 540)]      // second monitor's cursor reported on this one: still pulled in
    public void Near_a_screen_edge_the_rings_of_groups_stay_on_screen(int x, int y)
    {
        var area = new PixelRect(0, 0, 1920, 1080);
        const double radius = 150;
        var side = (int)Math.Ceiling(DonutMenu.SideFor(radius, subRing: true));

        var position = RadialController.WindowPosition(new PixelPoint(x, y), area, side);

        // The ring a group fans out (labels included) is inside the window, and the window inside the screen.
        var center = new PixelPoint(position.X + side / 2, position.Y + side / 2);
        var ringReach = radius + 7 + DonutMenu.SubRingSpace;
        Assert.True(ringReach <= side / 2.0);
        Assert.True(center.X - ringReach >= area.X && center.X + ringReach <= area.Right, $"x {center.X}");
        Assert.True(center.Y - ringReach >= area.Y && center.Y + ringReach <= area.Bottom, $"y {center.Y}");
    }

    [Fact]
    public void Away_from_the_edges_the_donut_is_centred_on_the_cursor()
    {
        var side = (int)Math.Ceiling(DonutMenu.SideFor(150, subRing: true));
        var position = RadialController.WindowPosition(new PixelPoint(960, 540), new PixelRect(0, 0, 1920, 1080), side);
        Assert.Equal(new PixelPoint(960 - side / 2, 540 - side / 2), position);
    }
}
