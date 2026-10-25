// Drag & drop for the donut editor.
//
// Sources: a palette chip (add) or a donut slice (reorder / remove).
// Targets: a donut slice (insert at / move to that position), the empty ring or the hole (append),
//          or the palette box (remove a slice).
// A floating "ghost" chip follows the pointer, and the donut opens a gap where the drop would land, with a
// translucent slice of the dragged preset in it (DonutMenu.SetDropPreview). The Front / Back switch shows the other
// face of the profile, and the donut turns in to it as the open donut does on Tab.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class ToolbarPage : UserControl
{
    private const double DragThreshold = 5;

    private enum Source { Chip, Slice }

    // CanAdd: fixed when the drag starts, so a chip that a search or a tab swaps under the pointer can't change it.
    private sealed record DragState(Source Source, string? PresetId, int SliceIndex, string Label, Point Start, bool CanAdd = true)
    {
        public bool Active { get; set; }
    }

    private DragState? _drag;
    private ToolbarPageViewModel? _subscribed;

    public ToolbarPage()
    {
        InitializeComponent();

        // See every pointer event on the page, even while a chip / the donut has pointer capture.
        Root.AddHandler(PointerMovedEvent, OnRootPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerReleasedEvent, OnRootPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Before the list box, which doesn't move between the rows of its wrap panel.
        PaletteTabs.AddHandler(KeyDownEvent, OnTabsKeyDown, RoutingStrategies.Tunnel);

        Donut.SegmentDragStarted += (_, e) =>
        {
            if (Vm is null || e.Index >= Vm.DonutItems.Count)
                return;
            var p = e.Args.GetPosition(Root);
            _drag = new DragState(Source.Slice, null, e.Index, Vm.DonutItems[e.Index].Label, p) { Active = true };
            Donut.SetDimmedIndex(e.Index);
            ShowGhost(_drag.Label, p);
        };
        Donut.SegmentContextRequested += async (_, index) =>
        {
            if (Vm is not null)
                await Vm.EditAsync(index);
        };
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.PropertyChanged -= OnViewModelChanged;
            _subscribed = Vm;
            if (_subscribed is not null)
                _subscribed.PropertyChanged += OnViewModelChanged;
        };
    }

    /// <summary>The switch showed the other face (its slices are in already): the donut turns in to it.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolbarPageViewModel.EditingBack))
        {
            EndDrag();
            Donut.PlayFlipIn();
        }
    }

    private ToolbarPageViewModel? Vm => DataContext as ToolbarPageViewModel;

    /// <summary>Down / Up in the wrapped tab row: the tab on the next / previous row nearest below / above this one.</summary>
    private void OnTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || e.KeyModifiers != KeyModifiers.None ||
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } from)
            return;
        var down = e.Key == Key.Down;
        var tabs = PaletteTabs.GetRealizedContainers().OfType<ListBoxItem>().ToList();
        var beyond = tabs.Where(t => down ? t.Bounds.Top >= from.Bounds.Bottom - 0.5 : t.Bounds.Bottom <= from.Bounds.Top + 0.5).ToList();
        if (beyond.Count == 0)
            return;
        var row = down ? beyond.Min(t => t.Bounds.Top) : beyond.Max(t => t.Bounds.Top);
        var to = beyond.Where(t => Math.Abs(t.Bounds.Top - row) < 0.5)
            .MinBy(t => Math.Abs(t.Bounds.Center.X - from.Bounds.Center.X))!;
        PaletteTabs.SelectedIndex = PaletteTabs.IndexFromContainer(to);
        to.Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: PaletteChip chip } border)
            return;
        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsRightButtonPressed)
        {
            _ = Vm?.EditChipAsync(chip.PresetId);
            e.Handled = true;
            return;
        }
        // A preset this donut can't run (shown by a search) can be edited, not added.
        if (!point.Properties.IsLeftButtonPressed || !chip.CanAdd)
            return;

        _drag = new DragState(Source.Chip, chip.PresetId, -1, chip.Label, e.GetPosition(Root), chip.CanAdd);
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    /// <summary>Double-clicking a chip appends it to the donut.</summary>
    private void OnChipDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: PaletteChip { CanAdd: true } chip } && Vm is { } vm)
            vm.Insert(chip.PresetId, vm.SliceCount);
    }

    /// <summary>The keyboard's double- and right-click on a chip: Enter appends it, Shift+F10 or the menu key edits it.</summary>
    private void OnChipKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: PaletteChip chip } || Vm is not { } vm)
            return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (chip.CanAdd)
                vm.Insert(chip.PresetId, vm.SliceCount);
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            _ = vm.EditChipAsync(chip.PresetId);
            e.Handled = true;
        }
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is null)
            return;
        var p = e.GetPosition(Root);
        if (!_drag.Active)
        {
            if (Math.Abs(p.X - _drag.Start.X) < DragThreshold && Math.Abs(p.Y - _drag.Start.Y) < DragThreshold)
                return;
            _drag.Active = true;
        }

        ShowGhost(_drag.Label, p);
        var (sliceTarget, overPalette) = FindTarget(e);
        var removing = overPalette && _drag.Source == Source.Slice;
        Donut.SetDropPreview(removing ? -1 : sliceTarget, _drag.Label, _drag.Source == Source.Slice ? _drag.SliceIndex : -1);
        PaletteBox.Classes.Set("over", removing);
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var drag = _drag;
        if (drag is null)
            return;

        if (drag.Active && Vm is { } vm)
        {
            var (sliceTarget, overPalette) = FindTarget(e);
            switch (drag.Source)
            {
                case Source.Chip when drag.CanAdd && sliceTarget >= 0:
                    vm.Insert(drag.PresetId!, sliceTarget);
                    break;
                case Source.Slice when overPalette:
                    vm.RemoveAt(drag.SliceIndex);
                    break;
                case Source.Slice when sliceTarget >= 0 && sliceTarget < vm.SliceCount:
                    vm.Move(drag.SliceIndex, sliceTarget);
                    break;
            }
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    /// <summary>
    /// Where would a drop land? Returns the position in the list after the drop (SliceCount = append; -1 = not on the
    /// donut) and whether the pointer is over the palette. The slots are those of the donut with the gap open (one
    /// more slice for a new preset), so the gap doesn't move away from under the pointer.
    /// </summary>
    private (int SliceTarget, bool OverPalette) FindTarget(PointerEventArgs e)
    {
        var overPalette = new Rect(PaletteBox.Bounds.Size).Contains(e.GetPosition(PaletteBox));
        var point = e.GetPosition(Donut);
        var count = Vm?.SliceCount ?? 0;
        var adding = _drag?.Source == Source.Chip;
        var target = Donut.HitTest(point).Kind == DonutHitKind.Center
            ? adding ? count : -1 // drop in the hole = append
            : Donut.SlotAt(point, adding ? count + 1 : count);
        return (target, overPalette);
    }

    private void ShowGhost(string label, Point at)
    {
        GhostText.Text = label;
        Ghost.IsVisible = true;
        Canvas.SetLeft(Ghost, at.X + 12);
        Canvas.SetTop(Ghost, at.Y + 12);
    }

    private void EndDrag()
    {
        _drag = null;
        Ghost.IsVisible = false;
        Donut.SetDropTarget(-1);
        Donut.SetDropPreview(-1);
        Donut.SetDimmedIndex(-1);
        PaletteBox.Classes.Set("over", false);
    }
}
