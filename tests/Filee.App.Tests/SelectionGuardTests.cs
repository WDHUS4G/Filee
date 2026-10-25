// Combo boxes and the navigation list keep their selection when the selected item is picked again with the toggle
// modifier (Ctrl+Space / Ctrl+click, Cmd on macOS), and the settings behind them never become null (#107).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SelectionGuardTests
{
    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Ctrl on Windows and Linux, Cmd on macOS: what Avalonia reads as "toggle the selection".</summary>
    private static RawInputModifiers Toggle(TopLevel top) =>
        (RawInputModifiers)(top.GetPlatformSettings() ?? Application.Current!.PlatformSettings)!.HotkeyConfiguration.CommandModifiers;

    private static void CtrlSpace(Window window)
    {
        window.KeyPress(Key.Space, Toggle(window), PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, Toggle(window), PhysicalKey.Space, " ");
        Pump();
    }

    private static Point Middle(Control control, Visual to) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), to)!.Value;

    private static (Window Window, TriggerGestureViewModel Gesture, ComboBox Kind) ShowTriggers()
    {
        TestServices.EnsureInitialized("en");
        var vm = new TriggersPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>());
        var window = new Window { Content = new TriggersPage { DataContext = vm }, Width = 900, Height = 900 };
        window.Show();
        Pump();
        var gesture = vm.Gestures[0];
        var kind = window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, gesture.Kinds));
        return (window, gesture, kind);
    }

    [AvaloniaFact]
    public void Ctrl_space_twice_on_a_combo_box_keeps_its_choice()
    {
        var (window, gesture, kind) = ShowTriggers();
        try
        {
            var before = gesture.Kind;
            var changes = 0;
            kind.SelectionChanged += (_, _) => changes++;
            Assert.True(kind.Focus(NavigationMethod.Tab));

            CtrlSpace(window); // opens the drop-down on the selected item
            CtrlSpace(window); // picks that item again: used to clear the selection
            CtrlSpace(window);
            CtrlSpace(window);

            Assert.Same(before, gesture.Kind);
            Assert.Same(before, kind.SelectedItem);
            Assert.Equal(0, changes); // never cleared, not just put back
            Assert.False(kind.IsDropDownOpen);
            Assert.Equal(before.Value, gesture.Gesture.Kind);
            Assert.Empty(DataValidationErrors.GetErrors(kind) ?? []);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Ctrl_clicking_the_selected_item_of_an_open_combo_box_keeps_it_and_closes_the_drop_down()
    {
        var (window, gesture, kind) = ShowTriggers();
        try
        {
            var before = gesture.Kind;
            kind.IsDropDownOpen = true;
            Pump();
            var changes = 0;
            kind.SelectionChanged += (_, _) => changes++;
            var item = (ComboBoxItem)kind.ContainerFromItem(before)!;
            var root = TopLevel.GetTopLevel(item)!;
            var at = Middle(item, root);
            root.MouseDown(at, MouseButton.Left, Toggle(root));
            root.MouseUp(at, MouseButton.Left, Toggle(root));
            Pump();

            Assert.Same(before, gesture.Kind);
            Assert.Same(before, kind.SelectedItem);
            Assert.Equal(0, changes);
            Assert.False(kind.IsDropDownOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_combo_box_cleared_anyway_shows_its_choice_again()
    {
        var (window, gesture, kind) = ShowTriggers();
        try
        {
            var before = gesture.Kind;
            kind.SelectedItem = null; // e.g. an Avalonia path the guard does not know
            Pump();

            Assert.Same(before, gesture.Kind);
            Assert.Same(before, kind.SelectedItem);
            Assert.Empty(DataValidationErrors.GetErrors(kind) ?? []);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Ctrl_clicking_the_open_page_in_the_navigation_keeps_it_open()
    {
        TestServices.EnsureInitialized("en");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var nav = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Classes.Contains("nav"));
            var home = vm.SelectedItem;
            var page = vm.CurrentPage;
            var changes = 0;
            nav.SelectionChanged += (_, _) => changes++;
            var at = Middle(nav.ContainerFromItem(home)!, window);
            window.MouseDown(at, MouseButton.Left, Toggle(window));
            window.MouseUp(at, MouseButton.Left, Toggle(window));
            Pump();
            Assert.Same(home, vm.SelectedItem);
            Assert.Same(page, vm.CurrentPage);
            Assert.Equal(0, changes);

            // Ctrl+clicking another page still opens it.
            at = Middle(nav.ContainerFromItem(vm.Items[1])!, window);
            window.MouseDown(at, MouseButton.Left, Toggle(window));
            window.MouseUp(at, MouseButton.Left, Toggle(window));
            Pump();
            Assert.Same(vm.Items[1], vm.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Multi_selection_lists_still_toggle()
    {
        TestServices.EnsureInitialized("en");
        var list = new ListBox { ItemsSource = new[] { "a", "b", "c" }, SelectionMode = SelectionMode.Multiple };
        var window = new Window { Content = list, Width = 300, Height = 300 };
        window.Show();
        Pump();
        try
        {
            list.SelectedIndex = 0;
            Pump();
            Assert.True(list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab));
            CtrlSpace(window);
            Assert.Empty(list.SelectedItems!);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Choice_properties_ignore_null_and_announce_their_value_again()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var store = AppHost.Get<UserDataStore>();

        var gesture = new TriggerGestureViewModel(new TriggerGesture { Button = TriggerMouseButton.Left }, loc, () => { });
        AssertKeeps(gesture, nameof(gesture.Kind), () => gesture.Kind, v => gesture.Kind = v);
        AssertKeeps(gesture, nameof(gesture.Button), () => gesture.Button, v => gesture.Button = v);
        AssertKeeps(gesture, nameof(gesture.Scope), () => gesture.Scope, v => gesture.Scope = v);

        var theme = new ThemePageViewModel(store, loc, AppHost.Get<IPlatformServices>());
        AssertKeeps(theme, nameof(theme.Mode), () => theme.Mode, v => theme.Mode = v);

        var general = ActivatorUtilitiesCreate<GeneralPageViewModel>();
        AssertKeeps(general, nameof(general.Language), () => general.Language, v => general.Language = v);
        AssertKeeps(general, nameof(general.DefaultLocation), () => general.DefaultLocation, v => general.DefaultLocation = v);

        var editor = new PresetEditorViewModel(store.FindPreset("to-png")!, loc);
        AssertKeeps(editor, nameof(editor.Target), () => editor.Target, v => editor.Target = v);
        AssertKeeps(editor, nameof(editor.Resize), () => editor.Resize, v => editor.Resize = v);
        AssertKeeps(editor, nameof(editor.Rotation), () => editor.Rotation, v => editor.Rotation = v);
        AssertKeeps(editor, nameof(editor.Flip), () => editor.Flip, v => editor.Flip = v);
        AssertKeeps(editor, nameof(editor.PageSize), () => editor.PageSize, v => editor.PageSize = v);
        AssertKeeps(editor, nameof(editor.Location), () => editor.Location, v => editor.Location = v);
        AssertKeeps(editor, nameof(editor.Conflict), () => editor.Conflict, v => editor.Conflict = v);
        AssertKeeps(editor, nameof(editor.TiffCompression), () => editor.TiffCompression, v => editor.TiffCompression = v);
        AssertKeeps(editor, nameof(editor.MediaQuality), () => editor.MediaQuality, v => editor.MediaQuality = v);
        AssertKeeps(editor, nameof(editor.MaxHeight), () => editor.MaxHeight, v => editor.MaxHeight = v);
        AssertKeeps(editor, nameof(editor.AudioBitrate), () => editor.AudioBitrate, v => editor.AudioBitrate = v);
        AssertKeeps(editor, nameof(editor.AudioChannels), () => editor.AudioChannels, v => editor.AudioChannels = v);
        AssertKeeps(editor, nameof(editor.SampleRate), () => editor.SampleRate, v => editor.SampleRate = v);
        AssertKeeps(editor, nameof(editor.WavFormat), () => editor.WavFormat, v => editor.WavFormat = v);
        AssertKeeps(editor, nameof(editor.ArchiveLevel), () => editor.ArchiveLevel, v => editor.ArchiveLevel = v);
        AssertKeeps(editor, nameof(editor.TraceMode), () => editor.TraceMode, v => editor.TraceMode = v);
        AssertKeeps(editor, nameof(editor.VideoCodecChoice), () => editor.VideoCodecChoice, v => editor.VideoCodecChoice = v);
        AssertKeeps(editor, nameof(editor.AudioCodecChoice), () => editor.AudioCodecChoice, v => editor.AudioCodecChoice = v);
        Assert.True(editor.Apply());

        var nav = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
        AssertKeeps(nav, nameof(nav.SelectedItem), () => nav.SelectedItem, v => nav.SelectedItem = v);

        var rename = new RenameToolViewModel("", "{name}", [new RenameStep()], [], (_, _, name) => name, loc);
        var step = rename.Steps[0];
        AssertKeeps(step, nameof(step.Kind), () => step.Kind, v => step.Kind = v);

        var watch = ActivatorUtilitiesCreate<WatchFoldersPageViewModel>();
        watch.AddCommand.Execute(null);
        var rule = watch.Rules[^1];
        try
        {
            AssertKeeps(rule, nameof(rule.Preset), () => rule.Preset!, v => rule.Preset = v);
            AssertKeeps(rule, nameof(rule.Originals), () => rule.Originals, v => rule.Originals = v);
            Assert.Equal(rule.Preset!.Value, rule.Rule.PresetId);
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule.Rule);
            store.SaveSettings();
            watch.Dispose();
        }
    }

    private static T ActivatorUtilitiesCreate<T>() =>
        Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<T>(AppHost.Services);

    private static void AssertKeeps<T>(System.ComponentModel.INotifyPropertyChanged vm, string property, Func<T> get, Action<T> set)
        where T : class
    {
        var before = get();
        Assert.NotNull(before);
        var announced = false;
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => announced |= e.PropertyName == property;
        vm.PropertyChanged += OnChanged;
        try
        {
            set(null!);
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }
        Assert.Equal(before, get()); // the codec choices are rebuilt on every read: equal, not the same
        Assert.True(announced, $"{property} did not announce its value again");
    }
}
