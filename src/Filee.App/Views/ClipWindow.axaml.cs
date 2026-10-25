using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>
/// The "Cut a clip" window: placement, keys (Space plays, C cuts at the playhead, Delete cuts a piece out or puts it
/// back, Ctrl+Z / Ctrl+Y undo and redo, M the magnet, Z zooms the timeline in on the clip, S / E pick the start or end handle, P the playhead, the arrow keys
/// move it by 0.1 s, with Shift by 1 s, Enter saves, Esc stops playing or closes), the timeline's drags and moving,
/// sizing and maximizing the frameless window.
/// </summary>
public partial class ClipWindow : Window
{
    /// <summary>How far the arrow keys move a handle, in seconds; with Shift <see cref="LargeStep"/>.</summary>
    public const double SmallStep = 0.1, LargeStep = 1;

    private ClipWindowViewModel? _vm;

    /// <summary>The focus came from the keyboard (Tab, arrows), not from a click.</summary>
    private bool _focusByKeyboard;

    public ClipWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        DataContextChanged += OnDataContextChanged;
        AddHandler(GotFocusEvent, (_, e) => _focusByKeyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
        // Space and Enter are the window's own (play, save), so a button that was clicked can't be pressed by them
        // later; their key-up must not press it either. A button reached with Tab is pressed by them as usual.
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter && !ControlTakes(e))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        ResizeGrip.PointerPressed += OnGripPressed;
    }

    internal ClipTimeline ClipTimeline => Timeline;
    internal ClipSequence ClipSequence => Sequence;

    /// <summary>Music…: picks a sound (or video) file and puts its sound under the clip.</summary>
    private async void OnAddMusic(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = vm.MusicPickerTitle,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType(vm.MusicFilesName)
                    {
                        Patterns = ["*.mp3", "*.m4a", "*.aac", "*.wav", "*.flac", "*.ogg", "*.opus", "*.wma", "*.mp4", "*.mkv", "*.mov", "*.webm"],
                    },
                    Avalonia.Platform.Storage.FilePickerFileTypes.All,
                ],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await vm.AddMusicAsync(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The picker isn't there (a test, an unusual desktop): nothing to add.
            System.Diagnostics.Debug.WriteLine(ex);
        }
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
        if (screen is null)
            return;
        var scaling = screen.Scaling;
        var area = screen.WorkingArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scaling * 0.94));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scaling * 0.92));
        var x = area.X + Math.Max(0, (area.Width - (int)(Width * scaling)) / 2);
        var y = area.Y + Math.Max(0, (area.Height - (int)(Height * scaling)) / 2);
        Position = new PixelPoint(x, y);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClipWindowViewModel vm && !ReferenceEquals(vm, _vm))
        {
            _vm = vm;
            vm.CloseRequested += Close;
            Timeline.HandleMoved += vm.MoveHandle;
            Timeline.ClipMoved += vm.MoveClip;
            Timeline.PlayheadMoved += vm.MovePlayhead;
            Sequence.PlayheadMoved += vm.MoveSequencePlayhead;
            Sequence.PieceSelected += vm.SelectPiece;
            Sequence.MarqueeSelected += vm.SelectAcross;
            Sequence.EdgeDragStarted += vm.BeginTrim;
            Sequence.EdgeDragged += vm.Trim;
            Sequence.GapCloseRequested += vm.CloseGap;
            Sequence.VolumeDragged += vm.SetPieceVolume;
            Sequence.MusicMoved += vm.MoveMusic;
            Sequence.MusicVolumeDragged += vm.SetMusicVolume;
            Closed += (_, _) => vm.Dispose();
        }
    }

    // ───────────────────────── Keys ─────────────────────────

    /// <summary>
    /// The control that has the focus, reached with the keyboard, takes <paramref name="e"/>'s key as it would anywhere
    /// else: Space and Enter press a button, a toggle or a switch; the arrows move a slider or a list.
    /// </summary>
    private bool ControlTakes(KeyEventArgs e)
    {
        if (!_focusByKeyboard || e.KeyModifiers != KeyModifiers.None)
            return false;
        return FocusManager?.GetFocusedElement() switch
        {
            Button => e.Key is Key.Space or Key.Enter,
            Slider => e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown,
            ComboBox => e.Key is Key.Space or Key.Enter or Key.Up or Key.Down,
            _ => false,
        };
    }

    /// <summary>
    /// Before any focused control: Space plays and Enter saves, unless a control was reached with Tab (then its keys are
    /// its own, as everywhere else).
    /// </summary>
    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        if (ControlTakes(e))
            return;
        var plain = e.KeyModifiers == KeyModifiers.None;
        var command = e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta;
        var commandShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) || e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                // Stops playing first, then drops a selection of several pieces, then closes.
                if (vm.IsPlaying)
                    vm.StopPlaying();
                else if (!vm.CollapseSelection())
                    vm.Cancel();
                e.Handled = true;
                break;
            case Key.Space:
                // Always play / pause, whatever was clicked last.
                if (plain && vm.CanEdit)
                    vm.PlayPause();
                e.Handled = true;
                break;
            case Key.A when command && vm.CanEdit:
                vm.SelectAll();
                e.Handled = true;
                break;
            case Key.C when plain && vm.CanEdit:
                vm.Split();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when (plain || e.KeyModifiers == KeyModifiers.Shift) && vm.CanEdit:
                // Shift+Delete: cut out and close up at once (a ripple delete).
                vm.Delete(closeUp: e.KeyModifiers == KeyModifiers.Shift);
                e.Handled = true;
                break;
            case Key.Z when command && vm.CanEdit:
                vm.Undo();
                e.Handled = true;
                break;
            case Key.Y when command && vm.CanEdit:
            case Key.Z when commandShift && vm.CanEdit:
                vm.Redo();
                e.Handled = true;
                break;
            case Key.M when plain && vm.CanEdit:
                vm.IsMagnet = !vm.IsMagnet;
                e.Handled = true;
                break;
            case Key.Z when plain && vm.CanEdit:
                vm.ToggleZoom();
                e.Handled = true;
                break;
            case Key.P when plain && vm.CanEdit:
                vm.Activate(ClipHandle.Playhead);
                e.Handled = true;
                break;
            case Key.Enter:
                if (vm.IsFinished)
                    vm.Cancel();
                else
                    _ = vm.SaveAsync();
                e.Handled = true;
                break;
            case Key.S when plain && vm.CanEdit:
                vm.Activate(ClipHandle.Start);
                e.Handled = true;
                break;
            case Key.E when plain && vm.CanEdit:
                vm.Activate(ClipHandle.End);
                e.Handled = true;
                break;
            // Time runs left to right on the timeline, also in right-to-left languages: Left is always earlier.
            case Key.Left or Key.Right when vm.CanEdit && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? LargeStep : SmallStep;
                vm.Nudge(e.Key == Key.Left ? -step : step);
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
