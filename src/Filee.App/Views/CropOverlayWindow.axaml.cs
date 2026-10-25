using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The crop overlay's window: placement, keys (Enter saves, Esc closes, R resets, A opens the aspect dial, X swaps
/// portrait and landscape, arrows move the frame, Shift × 10), the aspect dial (around the pointer when A is pressed
/// with the pointer over the stage, else over its toolbar button) and moving, sizing and maximizing the frameless
/// window.
/// </summary>
public partial class CropOverlayWindow : Window
{
    private CropOverlayViewModel? _vm;
    private bool _dialByKeyboard;

    /// <summary>Where the pointer is over the stage; null while it is outside the window.</summary>
    private Point? _pointer;

    /// <summary>Where the next opening of the dial centres it (A over the stage); null opens it over its button.</summary>
    private Point? _openDialAt;

    /// <summary>The centre of the open dial when it opened at the pointer.</summary>
    private Point? _dialAt;

    public CropOverlayWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        Canvas.PlayAnimations = Motion.Enabled;
        Motion.Changed += OnMotionChanged;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        ResizeGrip.PointerPressed += OnGripPressed;
        Stage.SizeChanged += (_, _) => PlaceDial();
        // Every move over the stage, also over the canvas and the buttons that handle it.
        Stage.AddHandler(PointerMovedEvent, (_, e) => _pointer = e.GetPosition(Stage), RoutingStrategies.Tunnel, handledEventsToo: true);
        Stage.PointerExited += (_, _) => _pointer = null;
        Closed += (_, _) => Motion.Changed -= OnMotionChanged;
    }

    /// <summary>
    /// Opens near <paramref name="near"/> (the screen of the donut), else on the primary screen, centred in its working
    /// area and no larger than most of it.
    /// </summary>
    public void PositionOnScreen(PixelPoint? near = null)
    {
        var screen = (near.HasValue ? Screens.ScreenFromPoint(near.Value) : null)
            ?? Screens.ScreenFromPoint(Screens.Primary?.Bounds.Center ?? default)
            ?? Screens.Primary;

        if (screen is not null)
        {
            var scaling = screen.Scaling;
            var area = screen.WorkingArea;
            Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scaling * 0.94));
            Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scaling * 0.92));
            var winW = (int)(Width * scaling);
            var winH = (int)(Height * scaling);
            var x = area.X + Math.Max(0, (area.Width - winW) / 2);
            var y = area.Y + Math.Max(0, (area.Height - winH) / 2);
            Position = new PixelPoint(x, y);
        }
    }

    /// <summary>Settles every animation and transition (tests capture the end state).</summary>
    internal void SettleForCapture()
    {
        Classes.Set("reduced-motion", true);
        Canvas.PlayAnimations = false;
        Canvas.FinishAnimations();
    }

    internal CropCanvas CropCanvas => Canvas;
    internal AspectDial AspectDial => Dial;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CropOverlayViewModel vm && !ReferenceEquals(vm, _vm))
        {
            _vm = vm;
            vm.CloseRequested += Close;
            vm.ResetRequested += () => Canvas.ResetCrop();
            vm.AspectRatioChanged += ratio => Canvas.SetAspectRatio(ratio);
            vm.PropertyChanged += OnViewModelChanged;
            Canvas.CropRectChanged += rect => vm.UpdateDimensions(rect);
            Closed += (_, _) => vm.Dispose();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
            return;
        if (e.PropertyName == nameof(CropOverlayViewModel.IsAspectDialOpen))
            ShowDial(_vm.IsAspectDialOpen);
        else if (e.PropertyName == nameof(CropOverlayViewModel.CurrentIndex))
            Dispatcher.UIThread.Post(() => Strip.ContainerFromIndex(_vm.CurrentIndex)?.BringIntoView(), DispatcherPriority.Background);
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Canvas.PlayAnimations = Motion.Enabled;

    // ───────────────────────── The aspect dial ─────────────────────────

    private void ShowDial(bool open)
    {
        if (open)
        {
            // Around the pointer it grows from its middle, as the donut does; over the button, out of the button.
            (_dialAt, _openDialAt) = (_openDialAt, null);
            Dial.RenderTransformOrigin = _dialAt is null ? new RelativePoint(0.5, 1, RelativeUnit.Relative) : RelativePoint.Center;
            PlaceDial();
            Dial.IsVisible = true;
            var byKeyboard = _dialByKeyboard;
            Dispatcher.UIThread.Post(() =>
            {
                var selected = Dial.Segments.FirstOrDefault(s => s.IsSelected) ?? Dial.Segments.FirstOrDefault();
                selected?.Focus(byKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer);
            }, DispatcherPriority.Input);
            return;
        }

        _dialByKeyboard = false;
        if (Dial.IsKeyboardFocusWithin)
            DialButton.Focus();
        // Hidden once it has shrunk away, so Tab doesn't reach its slices.
        if (Motion.Enabled && !Classes.Contains("reduced-motion"))
            DispatcherTimer.RunOnce(() => Dial.IsVisible = _vm?.IsAspectDialOpen == true, TimeSpan.FromMilliseconds(220));
        else
            Dial.IsVisible = false;
    }

    /// <summary>Centres the dial on the pointer it opened at, or over its toolbar button, inside the window.</summary>
    private void PlaceDial()
    {
        if (!DialButton.IsEffectivelyVisible || DialButton.TranslatePoint(new Point(DialButton.Bounds.Width / 2, 0), Stage) is not { } anchor)
            return;
        var origin = CropMath.DialOrigin(_dialAt, anchor, Stage.Bounds.Size, Dial.Side);
        Dial.Margin = new Thickness(origin.X, origin.Y, 0, 0);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null)
            _vm.IsAspectDialOpen = false;
        e.Handled = true;
    }

    /// <summary>Arrow keys on the open dial go round its slices.</summary>
    private void MoveDialFocus(int step)
    {
        var segments = Dial.Segments.ToList();
        if (segments.Count == 0)
            return;
        var index = segments.FindIndex(s => s.IsFocused);
        if (index < 0)
            index = Math.Max(0, segments.FindIndex(s => s.IsSelected));
        else
            index = ((index + step) % segments.Count + segments.Count) % segments.Count;
        segments[index].Focus(NavigationMethod.Tab);
    }

    // ───────────────────────── Keys ─────────────────────────

    /// <summary>Before any focused control: Enter always saves (not "click the focused button").</summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var dialOpen = vm.IsAspectDialOpen;
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape:
                if (dialOpen)
                    vm.IsAspectDialOpen = false;
                else
                    vm.Cancel();
                e.Handled = true;
                break;
            case Key.Enter when !dialOpen:
                if (vm.IsFinished)
                    vm.Cancel();
                else
                    _ = vm.CropAndSaveAsync();
                e.Handled = true;
                break;
            case Key.R when plain && vm.IsEditing:
                vm.IsAspectDialOpen = false;
                vm.Reset();
                e.Handled = true;
                break;
            case Key.A when plain && vm.IsEditing:
                // With the pointer over the stage the dial opens around it, to be picked from with the mouse; without,
                // over its button with a slice focused for the arrow keys.
                _openDialAt = dialOpen ? null : _pointer;
                _dialByKeyboard = _openDialAt is null;
                vm.ToggleAspectDial();
                _openDialAt = null; // used by now, or the dial didn't open (busy)
                e.Handled = true;
                break;
            case Key.X when plain && vm.IsEditing:
                vm.SwapOrientation();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when dialOpen:
                MoveDialFocus(e.Key is Key.Left or Key.Up ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when vm.IsEditing && !vm.IsBusy
                && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
                Canvas.Nudge(e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 },
                    e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 });
                e.Handled = true;
                break;
        }
    }

    // ───────────────────────── Moving and sizing the window ─────────────────────────

    /// <summary>The stage beside the picture moves the window; a double click maximizes it.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleMaximized();
        else if (WindowState == WindowState.Normal)
            BeginMoveDrag(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            Classes.Set("maximized", maximized);
            ResizeGrip.IsVisible = !maximized;
            var key = maximized ? "crop.restore" : "crop.maximize";
            MaximizeButton.Bind(ToolTip.TipProperty, MaximizeButton.GetResourceObservable(key));
            MaximizeButton.Bind(Avalonia.Automation.AutomationProperties.NameProperty, MaximizeButton.GetResourceObservable(key));
        }
    }
}
