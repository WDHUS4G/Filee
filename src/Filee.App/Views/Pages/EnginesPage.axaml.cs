// Engines page: optional downloads (install, use your own copy, LibreOffice settings) and the priority order, changed
// by dragging a card or with its arrows.
//
// Dragging: the list captures the pointer (cards are re-created as they move), the card moves as soon as the pointer
// passes the middle of a neighbour, and the order is saved when the button is released. Near the top or bottom edge
// of the page the page scrolls by itself.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class EnginesPage : UserControl
{
    private const double DragThreshold = 5;
    private const double ScrollEdge = 48;
    private const double ScrollStep = 14;

    private sealed class EngineDrag(EngineItemViewModel item, Point start)
    {
        public EngineItemViewModel Item { get; } = item;
        public Point Start { get; } = start;
        public bool Active { get; set; }

        /// <summary>The pointer in the page's scroll viewer, for scrolling while it rests near an edge.</summary>
        public Point InScroller { get; set; }
    }

    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private EngineDrag? _drag;

    public EnginesPage()
    {
        InitializeComponent();
        EngineList.AddHandler(PointerMovedEvent, OnListPointerMoved, handledEventsToo: true);
        EngineList.AddHandler(PointerReleasedEvent, (_, e) => EndDrag(e.Pointer), handledEventsToo: true);
        EngineList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null), handledEventsToo: true);
        _scrollTimer.Tick += (_, _) => AutoScroll();
    }

    private EnginesPageViewModel? Vm => DataContext as EnginesPageViewModel;

    private ScrollViewer? Scroller => this.FindAncestorOfType<ScrollViewer>();

    /// <summary>Pressing a card (not its arrows, which handle the press) may start dragging it.</summary>
    private void OnEnginePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EngineItemViewModel item } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _drag = new EngineDrag(item, e.GetPosition(EngineList));
        e.Pointer.Capture(EngineList);
        e.Handled = true;
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag)
            return;
        var p = e.GetPosition(EngineList);
        if (!drag.Active)
        {
            if (Math.Abs(p.Y - drag.Start.Y) < DragThreshold && Math.Abs(p.X - drag.Start.X) < DragThreshold)
                return;
            drag.Active = true;
            drag.Item.IsDragging = true;
            _scrollTimer.Start();
        }
        if (Scroller is { } scroller)
            drag.InScroller = e.GetPosition(scroller);
        Follow(p.Y);
    }

    /// <summary>Moves the dragged card to where <paramref name="y"/> (in the list) is: after every other card whose middle is above it.</summary>
    private void Follow(double y)
    {
        if (_drag is not { Active: true } drag || Vm is not { } vm)
            return;
        var index = 0;
        for (var i = 0; i < vm.Engines.Count; i++)
        {
            if (vm.Engines[i] == drag.Item || EngineList.ContainerFromIndex(i) is not { } card)
                continue;
            if (card.TranslatePoint(new Point(0, card.Bounds.Height / 2), EngineList) is { } middle && middle.Y < y)
                index++;
        }
        vm.MoveTo(drag.Item, index);
    }

    private void AutoScroll()
    {
        if (_drag is not { Active: true } drag || Scroller is not { } scroller)
            return;
        var y = drag.InScroller.Y;
        var step = y < ScrollEdge ? -ScrollStep : y > scroller.Viewport.Height - ScrollEdge ? ScrollStep : 0;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var offset = Math.Clamp(scroller.Offset.Y + step, 0, max);
        if (step == 0 || offset == scroller.Offset.Y)
            return;
        scroller.Offset = scroller.Offset.WithY(offset);
        if (scroller.TranslatePoint(drag.InScroller, EngineList) is { } inList)
            Follow(inList.Y);
    }

    private void EndDrag(IPointer? pointer)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        _scrollTimer.Stop();
        pointer?.Capture(null);
        if (!drag.Active)
            return;
        drag.Item.IsDragging = false;
        Vm?.SavePriority();
    }

    /// <summary>The LibreOffice switches, in their own window.</summary>
    private async void OnPackageSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EnginesPageViewModel vm && TopLevel.GetTopLevel(this) is Window window)
            await new LibreOfficeOptionsWindow { DataContext = vm }.ShowDialog(window);
    }

    /// <summary>Picks the program of a copy the user already has (ffmpeg.exe, ebook-convert.exe, …) and uses it.</summary>
    private async void OnUseOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        var programs = Filee.Engines.Infrastructure.EngineEnvironment.OwnCopyPrograms[package.Package.Id];
        string? path;
        if (OperatingSystem.IsMacOS() && package.Package.Id is "libreoffice" or "calibre")
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = loc.Format("engines.use_own_app_folder", package.Name),
                AllowMultiple = false,
            });
            path = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = loc.Format("engines.use_own_title", package.Name),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(programs[0]) { Patterns = [programs[0]] }, FilePickerFileTypes.All],
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is null)
            return;
        if (!vm.UseOwnCopy(package, path))
            await AppHost.Get<WindowService>().MessageAsync(window, loc.Format("engines.use_own_missing", string.Join(", ", programs)));
    }

    private void OnStopOwnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EnginePackageState package } && DataContext is EnginesPageViewModel vm)
            vm.StopUsingOwnCopy(package);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EnginePackageState package } || DataContext is not EnginesPageViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window window)
            return;
        var loc = AppHost.Get<ILocalizer>();
        if (!await AppHost.Get<WindowService>().ConfirmAsync(window, loc.Format("engines.remove_confirm", package.Name), destructive: true))
            return;
        if (!vm.Remove(package))
            await AppHost.Get<WindowService>().MessageAsync(window, loc["engines.remove_failed"]);
    }
}
