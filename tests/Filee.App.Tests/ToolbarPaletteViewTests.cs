// The palette tabs, the search and the chip input on the real ToolbarPage: what the user sees and can do in the
// "available presets" column (tests/Filee.App.Tests/ToolbarEditorTests.cs checks the view model behind it).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class ToolbarPaletteViewTests
{
    /// <summary>A ToolbarPage for a fixture view model in a window, and the controls of its palette column.</summary>
    private sealed class View : IDisposable
    {
        public View(ToolbarPageViewModel vm)
        {
            Page = new ToolbarPage { DataContext = vm };
            // In a scroll viewer, as the main window shows this page (MainWindowViewModel.PageScroll).
            Window = new Window { Width = 1080, Height = 820, Content = new ScrollViewer { Content = Page } };
            Window.Show();
            RenderTests.Pump();
        }

        public ToolbarPage Page { get; }
        public Window Window { get; }
        public ListBox Tabs => Page.FindControl<ListBox>("PaletteTabs")!;
        public TextBox Search => Page.FindControl<TextBox>("PaletteSearchBox")!;
        public Border Box => Page.FindControl<Border>("PaletteBox")!;
        public Border Ghost => Page.FindControl<Border>("Ghost")!;

        /// <summary>The chips shown in the palette box, in their order on screen.</summary>
        public List<Border> ChipBorders => Box.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("chip") && b.DataContext is PaletteChip).ToList();

        public List<string> Chips => ChipBorders.Select(b => ((PaletteChip)b.DataContext!).PresetId).ToList();
        public Border Chip(string id) => ChipBorders.Single(b => ((PaletteChip)b.DataContext!).PresetId == id);
        public List<PaletteTab> TabIds => Tabs.Items.Cast<PaletteTabItem>().Select(t => t.Id).ToList();
        public PaletteTab? Selected => (Tabs.SelectedItem as PaletteTabItem)?.Id;

        public Point Center(Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;

        public void Click(Control control, MouseButton button = MouseButton.Left)
        {
            var point = Center(control);
            Window.MouseDown(point, button);
            Window.MouseUp(point, button);
            RenderTests.Pump();
        }

        /// <summary>Clicks the tab of that kind in the tab row.</summary>
        public void ClickTab(PaletteTab tab)
        {
            Assert.True(Tabs.IsEffectivelyVisible);
            var item = Tabs.Items.Cast<PaletteTabItem>().Single(t => t.Id == tab);
            Click((ListBoxItem)Tabs.ContainerFromItem(item)!);
            Assert.Equal(tab, Selected);
        }

        public void Type(string text)
        {
            Assert.True(Search.Focus());
            Search.SelectAll();
            Window.KeyTextInput(text);
            RenderTests.Pump();
            Assert.Equal(text, Search.Text);
        }

        public void ClearSearch()
        {
            Search.Clear();
            RenderTests.Pump();
        }

        /// <summary>Presses on the chip, moves into the donut's hole (a drop there appends) and lets go.</summary>
        public bool DragToDonut(Border chip)
        {
            var donut = Page.FindControl<Filee.App.Controls.DonutMenu>("Donut")!;
            // Below the window's bottom edge (how far down depends on the font): scrolled to, as a user would.
            chip.BringIntoView();
            RenderTests.Pump();
            var from = Center(chip);
            var to = Center(donut);
            var hit = Window.InputHitTest(from) as Visual;
            Assert.True(hit is not null && (hit == chip || chip.IsVisualAncestorOf(hit)),
                $"press at {from} hits {hit?.GetType().Name}");
            Window.MouseDown(from, MouseButton.Left);
            Window.MouseMove(to);
            RenderTests.Pump();
            var dragging = Ghost.IsVisible;
            Window.MouseUp(to, MouseButton.Left);
            RenderTests.Pump();
            return dragging;
        }

        public void Dispose() => Window.Close();
    }

    private static ToolbarProfile Profile(string id, params string[] extensions) => new()
    {
        Id = id,
        Name = id,
        Extensions = [.. extensions],
        PresetIds = [],
        BackPresetIds = [],
    };

    private static void DoubleTap(Border chip) => chip.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));

    // The editors need a visible owner (see PresetAvailabilityTests).
    private static WindowService OpenMain()
    {
        var windows = AppHost.Get<WindowService>();
        Assert.Null(windows.Main);
        windows.ShowMain();
        return windows;
    }

    private static void CloseMain(WindowService windows)
    {
        windows.Main?.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(windows.Main);
    }

    private static void SaveInEditor(WindowService windows, string target)
    {
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.Single(windows.Main!.OwnedWindows.OfType<PresetEditorWindow>());
        var model = Assert.IsType<PresetEditorViewModel>(editor.DataContext);
        model.Target = model.Targets.Single(t => t.Value == target);
        Dispatcher.UIThread.RunJobs();
        editor.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.Saved);
    }

    [AvaloniaFact]
    public void All_is_the_first_selected_tab()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);

        Assert.True(view.Tabs.IsEffectivelyVisible);
        Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media, PaletteTab.Edit], view.TabIds);
        Assert.Same(view.Tabs.Items[0], view.Tabs.SelectedItem);
        Assert.True(((ListBoxItem)view.Tabs.ContainerFromIndex(0)!).IsSelected);
        Assert.Equal(f.Loc["toolbar.tab.all"], ((PaletteTabItem)view.Tabs.Items[0]!).Label);
        Assert.Contains("test-jpg", view.Chips);
        Assert.Contains("test-mp4", view.Chips);

        // A donut with no extensions (nothing to judge, so every kind at once): All is chosen there too.
        var everything = Profile("tabs-all");
        f.Store.Profiles.Add(everything);
        vm.Profiles.Add(new ProfileItemViewModel(everything, f.Loc));
        var profiles = view.Page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("soft"));
        profiles.SelectedItem = vm.Profiles[^1];
        RenderTests.Pump();
        Assert.Equal(Enum.GetValues<PaletteTab>(), view.TabIds);
        Assert.True(view.Tabs.IsEffectivelyVisible);
        Assert.Same(view.Tabs.Items[0], view.Tabs.SelectedItem);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
    }

    [AvaloniaFact]
    public void Empty_tabs_and_redundant_row_disappear()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        // Nothing converts a WAV here: only the groups are left, and no tab row.
        using var vm = f.Page(Profile("tabs-wav", "wav"));
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        Assert.False(view.Tabs.IsVisible);
        Assert.Equal(["pdf-edit", "rotate", "flip", "test-group"], view.Chips);

        // PDF: one kind (Documents) that would only repeat All. Two tabs, but the row stays hidden.
        // (Without the PDF page tools, which would add the Edit kind.)
        f.Store.Presets.RemoveAll(p => p.Id.StartsWith("tool-pdf-", StringComparison.Ordinal));
        f.Store.Profiles.Add(Profile("tabs-pdf", "pdf"));
        vm.Profiles.Add(new ProfileItemViewModel(f.Store.Profiles[^1], f.Loc));
        vm.SelectedProfile = vm.Profiles[^1];
        Dispatcher.UIThread.RunJobs();
        RenderTests.Pump();
        Assert.Equal([PaletteTab.All, PaletteTab.Document], view.TabIds);
        Assert.False(view.Tabs.IsVisible);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(["pdf-edit", "rotate", "flip", "test-group", "merge-pdf", "test-merge"], view.Chips);

        // PNG: several kinds, so the row shows.
        f.Store.Profiles.Add(Profile("tabs-png", "png"));
        vm.Profiles.Add(new ProfileItemViewModel(f.Store.Profiles[^1], f.Loc));
        vm.SelectedProfile = vm.Profiles[^1];
        Dispatcher.UIThread.RunJobs();
        RenderTests.Pump();
        Assert.True(view.Tabs.IsEffectivelyVisible);
        Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media, PaletteTab.Edit], view.TabIds);

        // Two tabs that differ: a match of no kind and a PNG one. All has both, Image only the PNG one, so the row shows.
        f.Store.Presets.AddRange(
        [
            new() { Id = "test-odd", Name = "Odd pair unregistered", TargetFormat = "not-registered" },
            new() { Id = "test-odd-png", Name = "Odd pair PNG", TargetFormat = "png" },
        ]);
        f.Store.Profiles.Add(Profile("tabs-odd", "png"));
        vm.Profiles.Add(new ProfileItemViewModel(f.Store.Profiles[^1], f.Loc));
        vm.SelectedProfile = vm.Profiles[^1];
        Dispatcher.UIThread.RunJobs();
        RenderTests.Pump();
        view.Type("Odd pair");
        Assert.Equal([PaletteTab.All, PaletteTab.Image], view.TabIds);
        Assert.True(view.Tabs.IsEffectivelyVisible);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(["test-odd", "test-odd-png"], view.Chips.Order());
        view.ClickTab(PaletteTab.Image);
        Assert.Equal(PaletteTab.Image, vm.SelectedTab!.Id);
        Assert.True(((ListBoxItem)view.Tabs.ContainerFromIndex(1)!).IsFocused);
        Assert.Equal(["test-odd-png"], view.Chips);

        // Back to the WAV donut: the row hides again, and Tab from the search box never lands on one of its tabs.
        vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "tabs-wav");
        view.ClearSearch();
        Assert.False(view.Tabs.IsVisible);
        Assert.True(view.Search.Focus());
        for (var i = 0; i < 4; i++)
        {
            view.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            view.Window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            RenderTests.Pump();
            var focused = view.Window.FocusManager?.GetFocusedElement() as Visual;
            Assert.False(focused is not null && (focused == view.Tabs || view.Tabs.IsVisualAncestorOf(focused)),
                $"focus on {focused?.GetType().Name} in the hidden tab row");
        }
    }

    [AvaloniaFact]
    public void Audio_donut_tabs_follow_its_real_presets()
    {
        // The built-in audio donut with the real converters. Its presets change as built-ins are added, so this checks
        // the rules rather than a fixed list: each kind tab holds exactly All's chips of that kind, and the row shows
        // only when more than one kind is left.
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        using var vm = new ToolbarPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "audio");
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        var groups = store.Presets.Where(p => p.IsGroup).Select(p => p.Id).ToList();

        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        var all = view.Chips.ToList();
        Assert.Equal(groups, all.Take(groups.Count));
        var presets = all.Skip(groups.Count).ToList();
        Assert.NotEmpty(presets);
        PaletteTab KindOf(string id) => ToolbarPageViewModel.TabOf(store.FindPreset(id)!);
        var kinds = ToolbarPageViewModel.TabDefinitions.Select(d => d.Id).Where(t => presets.Any(id => KindOf(id) == t)).ToList();
        Assert.Equal([PaletteTab.All, .. kinds], view.TabIds);
        Assert.Equal(kinds.Count > 1, view.Tabs.IsVisible);

        foreach (var kind in kinds)
        {
            vm.SelectedTab = vm.Tabs.Single(t => t.Id == kind);
            RenderTests.Pump();
            Assert.Equal([.. groups, .. presets.Where(id => KindOf(id) == kind)], view.Chips);
        }
    }

    [AvaloniaFact]
    public void Groups_lead_every_tab_and_tools_use_edit()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Store.Presets.AddRange(
        [
            new() { Id = "tool-join", Kind = PresetKind.Tool, TargetFormat = "mp4" },
            new() { Id = "test-same", TargetFormat = BuiltInData.SameAsSource },
            // A target no format is registered for: in All only, under no kind.
            new() { Id = "test-unregistered", TargetFormat = "not-registered" },
        ]);
        // No extensions: nothing is judged, every kind has presets.
        using var vm = f.Page(Profile("tabs-all"));
        using var view = new View(vm);
        var groups = f.Store.Presets.Where(p => p.IsGroup).Select(p => p.Id).ToList();
        Assert.Equal(Enum.GetValues<PaletteTab>(), view.TabIds);

        var shown = new Dictionary<PaletteTab, List<string>>();
        foreach (var tab in Enum.GetValues<PaletteTab>())
        {
            view.ClickTab(tab);
            var chips = view.Chips;
            Assert.Equal(groups, chips.Take(groups.Count));
            Assert.DoesNotContain(chips.Skip(groups.Count), id => groups.Contains(id));
            shown[tab] = chips.Skip(groups.Count).ToList();
        }
        foreach (var tool in new[] { "tool-crop", "tool-clip", "tool-join", "test-same" })
        {
            Assert.Contains(tool, shown[PaletteTab.Edit]);
            Assert.DoesNotContain(tool, shown[PaletteTab.Media]);
            Assert.DoesNotContain(tool, shown[PaletteTab.Image]);
        }
        Assert.Contains("to-png", shown[PaletteTab.Image]);
        Assert.Contains("remove-background", shown[PaletteTab.Image]);
        Assert.DoesNotContain("to-png", shown[PaletteTab.Edit]);
        Assert.Contains("to-pdf", shown[PaletteTab.Document]);
        Assert.Contains("to-zip", shown[PaletteTab.Archive]);

        // Every chip with a kind is under exactly one tab; together they are All without the chips of no kind.
        var kinds = shown.Where(s => s.Key != PaletteTab.All).SelectMany(s => s.Value).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        var noKind = shown[PaletteTab.All].Except(kinds).ToList();
        Assert.Contains("test-unregistered", noKind);
        Assert.All(noKind, id =>
        {
            var preset = f.Preset(id);
            Assert.False(preset.IsTool);
            Assert.NotEqual(BuiltInData.SameAsSource, preset.TargetFormat);
            Assert.Null(FormatRegistry.FindById(preset.TargetFormat));
        });
        Assert.Equal(shown[PaletteTab.All].Except(noKind).Order(), kinds.Order());
    }

    [AvaloniaFact]
    public void Search_finds_hidden_presets_across_tabs()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        // A second match of another kind keeps the tab row up while searching (one kind alone would hide it).
        f.Preset("test-jpg").Name = "Hidden JPG sample";
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        Assert.True(vm.HasJudgement("test-palette", "test-wav"));
        Assert.DoesNotContain("test-wav", view.Chips);
        Assert.Contains(PaletteTab.Media, view.TabIds);

        view.ClickTab(PaletteTab.Image);
        Assert.Contains("test-jpg", view.Chips);
        Assert.Contains("Hidden WAV", f.Loc.DisplayName(f.Preset("test-wav")));
        view.Type("Hidden");
        Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media], view.TabIds);
        Assert.Equal(PaletteTab.Image, view.Selected);
        Assert.Equal(["test-jpg"], view.Chips);

        view.ClickTab(PaletteTab.Media);
        Assert.Equal(["test-wav"], view.Chips);
        var chip = view.Chip("test-wav");
        Assert.Contains("dimmed", chip.Classes);
        Assert.Equal(0.55, chip.Opacity);
        Assert.Equal(f.Loc["error.no_route"], ToolTip.GetTip(chip));
        Assert.False(((PaletteChip)chip.DataContext!).CanAdd);

        // Cleared: hidden again; Media stays (the engine-gated MP4 presets) and so does the choice.
        view.ClearSearch();
        Assert.DoesNotContain("test-wav", view.Chips);
        Assert.Contains("test-mp4", view.Chips);
        Assert.Equal(PaletteTab.Media, view.Selected);
    }

    [AvaloniaFact]
    public void External_library_save_restores_selected_tab_after_rejudging()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        f.Preset("test-jpg").Name = "Hidden JPG sample";
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        var groups = f.Store.Presets.Where(p => p.IsGroup).Select(p => p.Id).ToList();

        // Before the save: the search shows both, the WAV one dimmed under Media, the JPG one under Image.
        view.Type("Hidden");
        Assert.Equal("Hidden", view.Search.Text);
        Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media], view.TabIds);
        view.ClickTab(PaletteTab.Media);
        var wav = view.Chip("test-wav");
        Assert.Equal(PresetUse.Unusable, ((PaletteChip)wav.DataContext!).Use);
        Assert.Contains("dimmed", wav.Classes);
        Assert.Equal(0.55, wav.Opacity);
        Assert.Equal(f.Loc["error.no_route"], ToolTip.GetTip(wav));
        view.ClickTab(PaletteTab.Image);
        Assert.Equal(["test-jpg"], view.Chips.Except(groups));
        view.ClearSearch();
        view.ClickTab(PaletteTab.Image);

        // Saved elsewhere (the open donut's editor): every judgement goes. Until they are made again, no unjudged chip
        // and no empty Image tab: All is chosen. No jobs run here (they would judge), only the layout.
        f.Preset("test-wav").TargetFormat = "jpg";
        f.Store.SaveLibrary();
        view.Window.UpdateLayout();
        Assert.Equal("", view.Search.Text);
        Assert.DoesNotContain(PaletteTab.Image, view.TabIds);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Assert.Equal(groups, view.Chips);

        // Judged again: Image is back with its chips, and chosen again.
        Dispatcher.UIThread.RunJobs();
        view.Window.UpdateLayout();
        Assert.Contains(PaletteTab.Image, view.TabIds);
        Assert.Equal(PaletteTab.Image, view.Selected);
        Assert.Equal(PaletteTab.Image, vm.SelectedTab!.Id);
        Assert.Contains("test-jpg", view.Chips);
        Assert.Contains("test-wav", view.Chips);

        // Searched again: both are Image presets now, so the row would only repeat All. It hides and All is chosen.
        view.Type("Hidden");
        Dispatcher.UIThread.RunJobs();
        RenderTests.Pump();
        Assert.Equal("Hidden", view.Search.Text);
        Assert.Equal([PaletteTab.All, PaletteTab.Image], view.TabIds);
        Assert.False(view.Tabs.IsVisible);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(["test-jpg", "test-wav"], view.Chips.Except(groups).Order());
        var session = f.Availability.ForPalette([PresetAvailabilityTests.Fixture.Format("png")]);
        foreach (var id in new[] { "test-jpg", "test-wav" })
        {
            var expected = session.Judge(f.Preset(id));
            var border = view.Chip(id);
            var chip = (PaletteChip)border.DataContext!;
            Assert.Equal(expected.Use, chip.Use);
            Assert.Equal(expected.Use != PresetUse.Usable, border.Classes.Contains("dimmed"));
            Assert.Equal(expected.Use != PresetUse.Usable ? 0.55 : 1, border.Opacity);
            Assert.Equal(expected.Reason, ToolTip.GetTip(border));
        }
        Assert.Equal(PresetUse.Usable, ((PaletteChip)view.Chip("test-wav").DataContext!).Use);
    }

    [AvaloniaFact]
    public void Profile_change_resets_tab_but_face_flip_keeps_it()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var jpg = Profile("tabs-jpg", "jpg");
        using var vm = f.Page(jpg);
        var png = Profile("tabs-png", "png");
        f.Store.Profiles.Add(png);
        vm.Profiles.Add(new ProfileItemViewModel(png, f.Loc));
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        var faces = view.Page.FindControl<Border>("FaceSwitch")!.GetVisualDescendants().OfType<RadioButton>().ToList();
        var profiles = view.Page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("soft"));
        Assert.Equal([PaletteTab.All, PaletteTab.Media, PaletteTab.Edit], view.TabIds);
        // PNG judged once: going there later shows its tabs (Video & audio too) at once, so only the reset makes it All.
        profiles.SelectedItem = vm.Profiles.Single(p => p.Profile == png);
        Dispatcher.UIThread.RunJobs();
        profiles.SelectedItem = vm.Profiles.Single(p => p.Profile == jpg);
        RenderTests.Pump();
        Assert.Equal([PaletteTab.All, PaletteTab.Media, PaletteTab.Edit], view.TabIds);

        view.ClickTab(PaletteTab.Media);
        view.Click(faces[1]);
        Assert.True(vm.EditingBack);
        Assert.Equal(PaletteTab.Media, view.Selected);
        Assert.Equal(["pdf-edit", "rotate", "flip", "test-group", "extract-motion", "test-motion"], view.Chips);
        view.Click(faces[0]);
        Assert.False(vm.EditingBack);
        Assert.Equal(PaletteTab.Media, view.Selected);

        profiles.SelectedItem = vm.Profiles.Single(p => p.Profile == png);
        RenderTests.Pump();
        Assert.Contains(PaletteTab.Media, view.TabIds);
        Assert.True(view.Tabs.IsEffectivelyVisible);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Contains("test-jpg", view.Chips);

        // Back on JPG: All again. Its only edit preset goes on the donut, the Edit tab empties and All takes over.
        profiles.SelectedItem = vm.Profiles.Single(p => p.Profile == jpg);
        RenderTests.Pump();
        Assert.Equal(PaletteTab.All, view.Selected);
        view.ClickTab(PaletteTab.Edit);
        Assert.Equal(["pdf-edit", "rotate", "flip", "test-group", "tool-crop"], view.Chips);
        DoubleTap(view.Chip("tool-crop"));
        RenderTests.Pump();
        Assert.Equal(["tool-crop"], jpg.PresetIds);
        Assert.DoesNotContain(PaletteTab.Edit, view.TabIds);
        Assert.Equal(PaletteTab.All, view.Selected);
        Assert.Equal(["pdf-edit", "rotate", "flip", "test-group", "extract-motion", "test-motion"], view.Chips);
    }

    [AvaloniaFact]
    public void Unusable_blocks_add_but_allows_edit()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        List<string> Slices() => vm.SelectedProfile!.Profile.PresetIds;
        view.Type("Hidden WAV");
        var unusable = view.Chip("test-wav");
        var chip = (PaletteChip)unusable.DataContext!;
        Assert.Equal(PresetUse.Unusable, chip.Use);
        Assert.False(chip.CanAdd);
        Assert.Contains("dimmed", unusable.Classes);
        Assert.Equal(0.55, unusable.Opacity);
        Assert.Equal(f.Loc["error.no_route"], ToolTip.GetTip(unusable));

        // Neither a double-click nor a drag onto the donut adds it; the drag doesn't even start.
        DoubleTap(unusable);
        RenderTests.Pump();
        Assert.Empty(Slices());
        Assert.False(view.DragToDonut(unusable));
        Assert.Empty(Slices());

        // A right-click still opens its editor.
        var windows = OpenMain();
        try
        {
            view.Click(unusable, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(windows.Main!.OwnedWindows.OfType<PresetEditorWindow>());
            Assert.IsType<PresetEditorViewModel>(editor.DataContext);
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(windows.Main!.OwnedWindows.OfType<PresetEditorWindow>());
        }
        finally { CloseMain(windows); }

        // A preset that only needs an engine download goes on by drag and by double-click.
        view.ClearSearch();
        var needsPackage = view.Chip("test-mp4");
        Assert.Equal(PresetUse.NeedsPackage, ((PaletteChip)needsPackage.DataContext!).Use);
        Assert.True(view.DragToDonut(needsPackage));
        Assert.Equal(["test-mp4"], Slices());
        var other = view.Chip("to-mp4");
        Assert.Equal(PresetUse.NeedsPackage, ((PaletteChip)other.DataContext!).Use);
        DoubleTap(other);
        RenderTests.Pump();
        Assert.Equal(["test-mp4", "to-mp4"], Slices());
    }

    [AvaloniaFact]
    public async Task New_group_and_unplaced_preset_select_visible_tab()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-jpg").Name = "Sample JPG";
        f.Preset("test-mp4").Name = "Sample MP4";
        var profile = Profile("tabs-new", "png");
        profile.PresetIds.AddRange(["to-webp", "to-tiff", "to-bmp", "to-ico", "to-gif", "to-avif", "to-heic", "to-pdf",
            "to-docx", "to-txt", "to-md"]);
        Assert.Equal(ToolbarProfile.MaxSlices - 1, profile.PresetIds.Count);
        using var vm = f.Page(profile);
        Dispatcher.UIThread.RunJobs();
        using var view = new View(vm);
        var groups = f.Store.Presets.Where(p => p.IsGroup).Select(p => p.Id).ToList();

        // A group made while searching: the search clears, the tab stays, and the group heads its chips.
        view.ClickTab(PaletteTab.Image);
        view.Type("Sample");
        Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media], view.TabIds);
        Assert.Equal(PaletteTab.Image, view.Selected);
        Assert.Equal(["test-jpg"], view.Chips);
        var group = new Preset { Id = "test-new-group", Kind = PresetKind.Group, SubPresetIds = ["test-jpg"] };
        vm.AddGroup(group);
        RenderTests.Pump();
        Assert.Equal("", view.Search.Text);
        Assert.Equal(PaletteTab.Image, view.Selected);
        Assert.Equal([.. groups, group.Id], view.Chips.Take(groups.Count + 1));

        var windows = OpenMain();
        try
        {
            // A free slot: the new preset goes on the donut, and the tab stays.
            var saving = vm.NewPresetCommand.ExecuteAsync(null);
            SaveInEditor(windows, "mp4");
            await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();
            Assert.Equal(f.Store.Presets[^1].Id, profile.PresetIds[^1]);
            Assert.Equal(PaletteTab.Image, view.Selected);

            // A full donut: the new preset stays in the palette and its tab is chosen for it.
            Assert.Equal(ToolbarProfile.MaxSlices, profile.PresetIds.Count);
            saving = vm.NewPresetCommand.ExecuteAsync(null);
            SaveInEditor(windows, "mp4");
            await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();
            var kept = f.Store.Presets[^1];
            Assert.DoesNotContain(kept.Id, profile.PresetIds);
            Assert.Equal(PaletteTab.Media, view.Selected);
            Assert.Contains(kept.Id, view.Chips);
            Assert.Equal("", view.Search.Text);

            // One this donut can't run: searched for by its name, so it shows (dimmed).
            saving = vm.NewPresetCommand.ExecuteAsync(null);
            SaveInEditor(windows, "wav");
            await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();
            var unusable = f.Store.Presets[^1];
            Assert.Equal(f.Loc.DisplayName(unusable), view.Search.Text);
            Assert.Contains("dimmed", view.Chip(unusable.Id).Classes);
            Assert.False(((PaletteChip)view.Chip(unusable.Id).DataContext!).CanAdd);
            // Its name finds Media presets only: no tab row, so All (every tab of a hidden row is All's list).
            Assert.Equal([PaletteTab.All, PaletteTab.Media], view.TabIds);
            Assert.False(view.Tabs.IsVisible);
            Assert.Equal(PaletteTab.All, view.Selected);
            Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);

            // The same name on an image preset too: the search keeps two kinds, the row shows and the new preset's tab is chosen.
            f.Preset("test-jpg").Name = f.Loc.DisplayName(unusable) + " JPG";
            saving = vm.NewPresetCommand.ExecuteAsync(null);
            SaveInEditor(windows, "wav");
            await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();
            var second = f.Store.Presets[^1];
            Assert.Equal(f.Loc.DisplayName(second), view.Search.Text);
            Assert.Equal([PaletteTab.All, PaletteTab.Image, PaletteTab.Media], view.TabIds);
            Assert.True(view.Tabs.IsEffectivelyVisible);
            Assert.Equal(PaletteTab.Media, view.Selected);
            Assert.Equal(PaletteTab.Media, vm.SelectedTab!.Id);
            Assert.Contains("dimmed", view.Chip(second.Id).Classes);
            Assert.DoesNotContain("test-jpg", view.Chips);
        }
        finally { CloseMain(windows); }
    }
}
