// Keyboard behaviour of the chip input (TagEditor) and the profile editing of the donut toolbar page,
// driven through a headless window.

using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
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
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class TagEditorTests
{
    private readonly ToolbarProfile _profile = new() { Id = "mine", Name = "Mine", Extensions = ["png"] };

    private (Window Window, TagEditor Editor, ExtensionTagsViewModel Vm) Show()
    {
        TestServices.EnsureInitialized("en");
        var vm = new ExtensionTagsViewModel(_profile, [_profile], new KeyLocalizer(), () => { });
        var editor = new TagEditor { AddCommand = vm.AddCommand, RemoveCommand = vm.RemoveCommand, DataContext = vm };
        editor.Bind(TagEditor.TagsProperty, new ReflectionBinding(nameof(vm.Tags)));
        editor.Bind(TagEditor.SuggestionsProperty, new ReflectionBinding(nameof(vm.Suggestions)));
        editor.Bind(TagEditor.TextProperty, new ReflectionBinding(nameof(vm.Text)) { Mode = BindingMode.TwoWay });
        var window = new Window { Width = 500, Height = 400, Content = new StackPanel { Children = { editor, new TextBox() } } };
        window.Show();
        Pump();
        editor.FocusInput();
        Pump();
        return (window, editor, vm);
    }

    private static TextBox Input(TagEditor editor) => editor.GetVisualDescendants().OfType<TextBox>().First();

    [AvaloniaFact]
    public void Enter_turns_the_text_into_a_chip()
    {
        var (window, editor, vm) = Show();
        window.KeyTextInput(".JPG");
        Press(window, Key.Enter);

        Assert.Equal(["png", "jpg"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        Assert.Equal("", vm.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Separators_and_pasted_lists_add_several_chips()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("gif,");
        Assert.Equal(["png", "gif"], _profile.Extensions);

        window.KeyTextInput("webp tif");
        Assert.Equal(["png", "gif", "webp"], _profile.Extensions);
        Assert.Equal("tif", Input(editor).Text); // unfinished: stays in the box

        // A pasted list with line breaks and wildcards.
        Input(editor).Text = "";
        window.KeyTextInput("*.bmp;\r\n*.TGA\n");
        Assert.Equal(["png", "gif", "webp", "bmp", "tga"], _profile.Extensions);
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_adds_the_text_but_still_moves_on_from_an_empty_box()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("avif");
        Press(window, Key.Tab);
        Assert.Equal(["png", "avif"], _profile.Extensions);
        Assert.True(Input(editor).IsFocused);

        Press(window, Key.Tab);
        Assert.False(Input(editor).IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_in_the_empty_box_removes_the_last_chip()
    {
        var (window, _, vm) = Show();
        window.KeyTextInput("gif");
        Press(window, Key.Enter);
        Press(window, Key.Back);

        Assert.Equal(["png"], _profile.Extensions);
        Assert.Equal([".png"], vm.Tags.Select(t => t.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Arrow_keys_pick_a_suggestion()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("jp");
        Pump();
        Assert.True(editor.IsSuggestionListOpen);

        Press(window, Key.Down);
        Press(window, Key.Down);
        Press(window, Key.Enter);

        Assert.Equal(["png", "jpeg"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        Assert.False(editor.IsSuggestionListOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_suggestions_then_clears_the_text()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("jp");
        Pump();
        Press(window, Key.Escape);
        Assert.False(editor.IsSuggestionListOpen);
        Assert.Equal("jp", Input(editor).Text);

        Press(window, Key.Escape);
        Assert.Equal("", Input(editor).Text);
        Assert.Equal(["png"], _profile.Extensions);
        window.Close();
    }

    [AvaloniaFact]
    public void Chips_can_be_reached_and_removed_with_the_keyboard()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("gif,webp,");
        Press(window, Key.Left);  // into the last chip (.webp)
        Press(window, Key.Left);  // .gif
        Press(window, Key.Delete);
        Pump();

        Assert.Equal(["png", "webp"], _profile.Extensions);
        Assert.False(Input(editor).IsFocused); // focus stays among the chips
        Press(window, Key.Escape);
        Assert.True(Input(editor).IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Leaving_the_box_keeps_what_was_typed()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("heic");
        ((TextBox)((StackPanel)window.Content!).Children[1]).Focus();
        Pump();

        Assert.Equal(["png", "heic"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        window.Close();
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}

public class ToolbarPageTests
{
    [AvaloniaFact]
    public void Profiles_switch_between_extension_chips_and_the_mixed_check_list()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);

            // A new profile goes before the mixed ones and starts as a normal profile.
            vm.AddProfileCommand.Execute(null);
            var item = vm.SelectedProfile!;
            var mixedIndex = store.Profiles.FindIndex(p => p.IsFallback);
            Assert.Equal(mixedIndex - 1, store.Profiles.IndexOf(item.Profile));
            Assert.NotNull(vm.ExtensionTags);
            Assert.Null(vm.MixedExtensions);
            vm.ExtensionTags!.AddCommand.Execute("jpg");

            // Made mixed: it moves behind the built-in "Mixed files" (which stays the catch-all) and keeps its
            // extensions as checks.
            vm.IsMixed = true;
            Assert.Same(item, vm.SelectedProfile);
            Assert.True(item.Profile.IsFallback);
            Assert.True(item.IsMixed);
            Assert.Same(item.Profile, store.Profiles[^1]);
            Assert.Equal(store.Profiles.Select(p => p.Id), vm.Profiles.Select(p => p.Profile.Id));
            Assert.Null(vm.ExtensionTags);
            Assert.NotNull(vm.MixedExtensions);
            Assert.True(vm.MixedExtensions!.Groups.Single(g => g.Name == "Images").State is null);
            Assert.Equal("mixed", ProfileSelector.CatchAll(store.Profiles)!.Id);
            Assert.True(vm.CanDelete);

            // And back.
            vm.IsMixed = false;
            Assert.False(item.Profile.IsFallback);
            Assert.Equal(store.Profiles.FindIndex(p => p.IsFallback) - 1, store.Profiles.IndexOf(item.Profile));
            Assert.Equal(["jpg"], item.Profile.Extensions);
        }
        finally
        {
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void A_profile_counts_both_faces_and_switching_profiles_clears_the_preset_search()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);

            // #425: the badge counted only the front face.
            var images = vm.Profiles.Single(p => p.Profile.Id == "images");
            images.Profile.PresetIds = ["to-png", "to-jpg", "to-webp"];
            images.Profile.BackPresetIds = ["to-pdf"];
            images.Refresh(AppHost.Get<Filee.Core.Localization.ILocalizer>());
            Assert.Equal("3 · 1", images.Count);
            Assert.Equal("Front 3 · Back 1", images.CountTip);
            images.Profile.BackPresetIds = [];
            images.Refresh(AppHost.Get<Filee.Core.Localization.ILocalizer>());
            Assert.Equal("3", images.Count); // no back face: the one number, as before

            // #427: a search typed for one donut's presets doesn't hide the next donut's.
            vm.SelectedProfile = images;
            vm.PaletteSearch = "png";
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            Assert.Equal("", vm.PaletteSearch);
            Assert.Equal(vm.Palette.Count, vm.VisiblePalette.Count);
        }
        finally
        {
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Search_boxes_have_a_clear_button()
    {
        TestServices.EnsureInitialized("en");
        using var vm = new ToolbarPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
        var page = new ToolbarPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1100, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var boxes = page.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("search")).ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.Contains("clearButton", box.Classes));
        vm.PaletteSearch = "pdf";
        boxes[1].Focus(); // Fluent shows the button in a focused box with text
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(boxes[1].GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible);
        RenderTests.Save(window, "toolbar-search-clear-en.png");
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_group_heads_the_available_presets_and_every_profile_count_stays_right()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var presets = store.Presets.ToList();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.PaletteSearch = "png"; // a search that would hide it
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Image);
            var pdf = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            pdf.Profile.PresetIds.RemoveAt(0); // changed elsewhere, as when a deleted group leaves every donut

            vm.AddGroup(new Filee.Core.Presets.Preset
            {
                Id = "test-new-group",
                Name = "Web",
                Kind = Filee.Core.Presets.PresetKind.Group,
                SubPresetIds = ["to-png"],
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("", vm.PaletteSearch);
            Assert.Equal(PaletteTab.Image, vm.SelectedTab!.Id);
            // After "Edit" of the PDF donut, the one built-in group not on the images donut.
            Assert.Equal(["pdf-edit", "test-new-group"], vm.VisiblePalette.Take(2).Select(c => c.PresetId));
            // Up with the groups at the top, above every conversion: not below ~80 presets.
            var chips = vm.VisiblePalette.Select(c => store.FindPreset(c.PresetId)!).ToList();
            var at = chips.FindIndex(p => p.Id == "test-new-group");
            Assert.True(at >= 0 && chips.Take(at).All(p => p.IsGroup), $"at {at}");
            Assert.Equal($"{pdf.Profile.PresetIds.Count} · {pdf.Profile.BackPresetIds!.Count}", pdf.Count);
            Assert.DoesNotContain("test-new-group", vm.SelectedProfile!.Profile.PresetIds); // on no donut until dragged there
        }
        finally
        {
            store.Presets.Clear();
            store.Presets.AddRange(presets);
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Reset_puts_a_built_in_profile_back_and_the_list_follows()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var presets = store.Presets.ToList();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            var item = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.SelectedProfile = item;
            Assert.True(vm.CanReset);
            vm.ProfileName = "Pictures";
            vm.RemoveAt(0);
            vm.IsMixed = true; // moves behind "Mixed files"
            Assert.Same(item.Profile, store.Profiles[^1]);

            vm.Reset(item);

            var builtIn = Filee.Core.Presets.BuiltInData.CreateProfiles().Single(p => p.Id == "images");
            Assert.Same(item, vm.SelectedProfile);
            Assert.Equal("Images", item.Name);
            Assert.Equal("Images", vm.ProfileName);
            Assert.Equal("Images", vm.CenterTitle);
            Assert.False(vm.IsMixed);
            Assert.NotNull(vm.ExtensionTags);
            Assert.Equal(builtIn.PresetIds, vm.DonutItems.Select(d => d.Id));
            Assert.Equal($"{builtIn.PresetIds.Count} · {builtIn.BackPresetIds!.Count}", item.Count);
            Assert.Equal(store.Profiles.Select(p => p.Id), vm.Profiles.Select(p => p.Profile.Id));
            Assert.Equal(store.Profiles.FindIndex(p => p.IsFallback) - 1, store.Profiles.IndexOf(item.Profile));

            // A new profile has nothing to go back to.
            vm.AddProfileCommand.Execute(null);
            Assert.False(vm.CanReset);
            Assert.True(vm.CanDelete);
            var mine = vm.SelectedProfile!;
            vm.Reset(mine);
            Assert.Equal("New profile", mine.Name);
        }
        finally
        {
            store.Presets.Clear();
            store.Presets.AddRange(presets);
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void The_last_mixed_profile_stays_mixed()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "mixed");

        Assert.True(vm.IsMixed);
        Assert.False(vm.CanChangeMixed);
        Assert.False(vm.CanDelete);
        vm.IsMixed = false;

        Assert.True(store.Profiles.Single(p => p.Id == "mixed").IsFallback);
        Assert.True(vm.IsMixed);
        Assert.NotNull(vm.MixedExtensions);
    }
}

[Collection("MainWindow")]
public class PresetAvailabilityTests
{
    internal class FakeConverter(string id, IReadOnlyList<ConversionEdge> edges) : IConverter
    {
        public FakeConverter(string id, params (string From, string To)[] edges)
            : this(id, edges.Select(e => new ConversionEdge(e.From, e.To)).ToList()) { }

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 1;
        public bool Available = true;
        public int StatusCalls;
        public EngineStatus GetStatus()
        {
            StatusCalls++;
            return Available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_found");
        }
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeTransformer(string id, IReadOnlyList<ConversionEdge> edges)
        : FakeConverter(id, edges), IImageTransformer
    {
        public bool Transforms(string from, string to) => true;
    }

    private sealed class ThrowingCombiner() : FakeConverter("test-throwing-combiner", Array.Empty<ConversionEdge>()), IFileCombiner
    {
        public bool CanCombineInto(string format) => throw new InvalidOperationException("broken combiner");

        public Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
            string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PaletteLogger : ILogger<ToolbarPageViewModel>
    {
        public readonly List<(LogLevel Level, Exception? Exception, string Message)> Warnings = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly UserDataStore _store;
        private readonly List<Preset> _presets;
        private readonly List<ToolbarProfile> _profiles;
        public readonly FakeConverter Built = new("test-built", new ConversionEdge[] { new("png", "jpg"), new("png", "png", Cost: 10) });
        public readonly FakeConverter Gated = new("ffmpeg", ("png", "mp4")) { Available = false };
        public readonly FakeConverter Motion = new("test-motion", new ConversionEdge[] { new("jpg", "mp4", MotionPhoto: true) });
        public readonly FakeTransformer Turner = new("pandoc", [new("png", "png", Cost: 1)]) { Available = false };
        public readonly ConverterCatalog Catalog;
        public readonly PresetAvailability Availability;
        public readonly ILocalizer Loc;
        public readonly List<EnginePackageState> Packages;

        public Fixture(IConverter? extra = null)
        {
            TestServices.EnsureInitialized("en");
            _store = AppHost.Get<UserDataStore>();
            _presets = _store.Presets.ToList();
            _profiles = _store.Profiles.Select(p => p.Clone()).ToList();
            Loc = AppHost.Get<ILocalizer>();
            var converters = new List<IConverter> { Built, Gated, Motion, Turner };
            if (extra is not null)
                converters.Add(extra);
            Catalog = new ConverterCatalog(converters);
            Packages = EngineDownloads.Packages.Where(p => p.Id is "pandoc" or "ffmpeg")
                .Select(p => new EnginePackageState(p, Loc) { Status = EnginePackageStatus.NotInstalled }).ToList();
            Availability = new PresetAvailability(Catalog, Loc, Packages);
            _store.Presets.AddRange(
            [
                new() { Id = "test-jpg", TargetFormat = "jpg" },
                new() { Id = "test-mp4", TargetFormat = "mp4" },
                new() { Id = "test-wav", TargetFormat = "wav" },
                new() { Id = "test-rotate-png", TargetFormat = "png", Image = new() { Rotation = ImageRotation.Right90 } },
                new() { Id = "test-motion", TargetFormat = "mp4", Media = new() { ExtractMotionPhoto = true } },
                new() { Id = "test-group", Kind = PresetKind.Group, SubPresetIds = ["test-wav"] },
                new() { Id = "test-pack", TargetFormat = "zip", Archive = new() { CombineIntoOne = true } },
                new() { Id = "test-merge", TargetFormat = "pdf", Pdf = new() { MergeIntoSingle = true } },
            ]);
        }

        public Preset Preset(string id) => _store.FindPreset(id)!;
        public UserDataStore Store => _store;
        public ToolbarPageViewModel Page(ToolbarProfile profile, ILogger<ToolbarPageViewModel>? log = null)
        {
            _store.Profiles.Add(profile);
            var vm = new ToolbarPageViewModel(_store, Loc, AppHost.Get<WindowService>(), Availability, Catalog,
                log ?? NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => ReferenceEquals(p.Profile, profile));
            return vm;
        }
        public ToolbarPageViewModel PageWithPalette(params string[] extensions) => Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = [.. extensions],
            PresetIds = [],
            BackPresetIds = [],
        });
        public static FileFormat Format(string id) => FormatRegistry.Get(id);

        public void Dispose()
        {
            _store.Presets.Clear();
            _store.Presets.AddRange(_presets);
            _store.Profiles.Clear();
            _store.Profiles.AddRange(_profiles);
            _store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Check_keeps_its_answers()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        var mp4 = Fixture.Format("mp4");
        var msrc = Fixture.Format(MediaOptions.MotionPhotoSources[0]);
        Preset P(string id) => f.Preset(id);
        string? Check(string id, params FileFormat[] formats) => f.Availability.Check(P(id), formats);

        Assert.Null(Check("tool-crop", png));
        Assert.Equal(f.Loc["error.unsupported_source"], Check("tool-crop", png, mp4));
        Assert.Equal(f.Loc["error.unsupported_source"], Check("test-jpg"));
        Assert.Equal(f.Loc["error.no_route"], Check("test-pack"));
        Assert.Null(Check("test-jpg", png));
        Assert.Equal(f.Loc.Format("engine.reason.needs_package", f.Packages.Single(p => p.Package.Id == "ffmpeg").Name),
            Check("test-mp4", png));
        Assert.Equal(f.Loc["error.no_route"], Check("test-wav", png));
        Assert.Equal(f.Loc["error.cannot_turn"], Check("test-rotate-png", png));
        Assert.Null(Check("test-jpg", mp4, png));
        Assert.Null(Check("test-motion", msrc));
        Assert.Equal(f.Loc["error.no_route"], Check("test-mp4", msrc));
        Assert.Null(Check("test-merge", Fixture.Format("pdf")));
    }

    [AvaloniaFact]
    public void Spreadsheet_print_studio_requires_xlsx_and_LibreOffice()
    {
        var libreOffice = new FakeConverter("libreoffice", ("xlsx", "pdf"));
        using var f = new Fixture(libreOffice);
        var studio = f.Preset("tool-sheet-studio");

        Assert.Null(f.Availability.Check(studio, [Fixture.Format("xlsx")]));
        Assert.Equal(f.Loc["error.unsupported_source"],
            f.Availability.Check(studio, [Fixture.Format("ods")]));
        Assert.Null(f.Availability.Check(studio, [Fixture.Format("xlsx"), Fixture.Format("xlsx")]));

        libreOffice.Available = false;
        Assert.Equal(f.Loc["error.no_route"],
            f.Availability.Check(studio, [Fixture.Format("xlsx")]));
    }

    [AvaloniaFact]
    public void The_palette_session_matches_check_and_sorts_the_reasons()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        var session = f.Availability.ForPalette([png]);
        Assert.Equal(new PresetJudgement(PresetUse.Usable, null), session.Judge(f.Preset("test-jpg")));
        foreach (var (id, use) in new[]
        {
            ("test-mp4", PresetUse.NeedsPackage),
            ("test-wav", PresetUse.Unusable),
            ("test-rotate-png", PresetUse.Unusable),
            ("test-pack", PresetUse.Unusable),
        })
            Assert.Equal(new PresetJudgement(use, f.Availability.Check(f.Preset(id), [png])), session.Judge(f.Preset(id)));

        Assert.Equal(new PresetJudgement(PresetUse.Usable, null),
            f.Availability.ForPalette([png, Fixture.Format("mp4")]).Judge(f.Preset("tool-crop")));
        Assert.Equal(new PresetJudgement(PresetUse.Unusable, f.Loc["error.unsupported_source"]),
            f.Availability.ForPalette([Fixture.Format("mp4")]).Judge(f.Preset("tool-crop")));
    }

    [AvaloniaFact]
    public void A_preset_whose_judging_throws_shows_undimmed_and_the_rest_are_judged()
    {
        using var f = new Fixture(new ThrowingCombiner());
        f.Store.Presets.Add(new() { Id = "test-jpg-after", TargetFormat = "jpg" });
        f.Store.Presets.Add(new() { Id = "test-mp4-after", TargetFormat = "mp4" });
        var log = new PaletteLogger();
        using var vm = f.Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = ["png", "mp4"],
            PresetIds = ["zip-all"],
            BackPresetIds = [],
        }, log);

        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-pack").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg-after").Reason);
        // Judged after the failure, not just shown: it still needs FFmpeg.
        var after = f.Availability.Check(f.Preset("test-mp4-after"), [Fixture.Format("png")]);
        Assert.NotNull(after);
        Assert.Equal(after, vm.Palette.Single(c => c.PresetId == "test-mp4-after").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.Equal(f.Availability.Check(f.Preset("test-mp4"), [Fixture.Format("png")]),
            vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-wav");
        var warning = Assert.Single(log.Warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Contains("test-pack", warning.Message);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "zip-all");
    }

    [AvaloniaFact]
    public void Check_builds_no_planner_when_it_answers_early()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        void CheckEarly(string id, params FileFormat[] formats)
        {
            f.Catalog.Refresh();
            f.Built.StatusCalls = 0;
            f.Availability.Check(f.Preset(id), formats);
            Assert.Equal(0, f.Built.StatusCalls);
        }

        CheckEarly("tool-crop", png);
        CheckEarly("test-jpg");
        CheckEarly("test-pack");
        f.Catalog.Refresh();
        f.Built.StatusCalls = 0;
        f.Availability.Check(f.Preset("test-jpg"), [png]);
        Assert.True(f.Built.StatusCalls > 0);
    }

    [AvaloniaFact]
    public void The_palette_lists_what_the_donut_can_run_and_dims_what_needs_an_engine()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.Equal(f.Availability.Check(f.Preset("test-mp4"), [Fixture.Format("png")]),
            vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "tool-crop").Reason);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-group");
        foreach (var id in new[] { "test-wav", "test-rotate-png", "test-pack" })
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == id);
        var firstNormal = vm.Palette.ToList().FindIndex(c => !f.Preset(c.PresetId).IsGroup);
        Assert.True(vm.Palette.Take(firstNormal).All(c => f.Preset(c.PresetId).IsGroup));
    }

    [AvaloniaFact]
    public void A_preset_on_either_face_stays_out_of_the_palette()
    {
        using var f = new Fixture();
        using var vm = f.Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = ["png"],
            PresetIds = ["test-jpg"],
            BackPresetIds = ["test-mp4"],
        });
        Dispatcher.UIThread.RunJobs();
        void Check()
        {
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg" || c.PresetId == "test-mp4");
        }
        Check();
        vm.EditingBack = true;
        Dispatcher.UIThread.RunJobs();
        Check();
    }

    [AvaloniaFact]
    public void A_donut_without_extensions_shows_everything()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        vm.AddProfileCommand.Execute(null);
        var placed = vm.SelectedProfile!.Profile.PresetIds.Concat(vm.SelectedProfile.Profile.BackPresetIds ?? []).ToHashSet();
        Assert.Empty(vm.SelectedProfile.Profile.Extensions);
        Assert.Equal(f.Store.Presets.Select(p => p.Id).Except(placed).Order(),
            vm.Palette.Select(c => c.PresetId).Order());
        Assert.All(vm.Palette, c => Assert.Null(c.Reason));
        Assert.Contains(vm.Palette, c => c.PresetId == "test-wav");
        Assert.Contains(vm.Palette, c => c.PresetId == "test-pack");
    }

    [AvaloniaFact]
    public void Editing_the_extensions_judges_the_palette_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        vm.ExtensionTags!.AddCommand.Execute("png");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg");
        vm.ExtensionTags.RemoveCommand.Execute(vm.ExtensionTags.Tags.Single(t => t.Value == "png"));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");

        vm.IsMixed = true;
        var png = vm.MixedExtensions!.Groups.SelectMany(g => g.Extensions).First(e => e.Extension == "png");
        png.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg");
        png.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
    }

    [AvaloniaFact]
    public void An_engine_change_judges_the_palette_again_until_the_page_is_closed()
    {
        using var f = new Fixture();
        var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
        var changed = typeof(ConverterCatalog).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(changed);
        EventHandler? Subscribers() => changed.GetValue(f.Catalog) as EventHandler;
        Assert.Contains(Subscribers()!.GetInvocationList(), d => ReferenceEquals(d.Target, vm));
        f.Gated.Available = true;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        vm.Dispose();
        Assert.DoesNotContain(Subscribers()?.GetInvocationList() ?? [], d => ReferenceEquals(d.Target, vm));
        f.Gated.Available = false;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
    }

    [AvaloniaFact]
    public void Switching_a_donut_to_mixed_keeps_its_palette()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        var before = vm.Palette.Select(c => (c.PresetId, c.Reason)).ToList();
        vm.IsMixed = true;
        Assert.Equal(before, vm.Palette.Select(c => (c.PresetId, c.Reason)));
    }

    [AvaloniaFact]
    public void A_dimmed_chip_stays_dimmed_with_its_reason_and_can_still_be_added()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        var window = new Window { Width = 1080, Height = 820, Content = new ToolbarPage { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var chips = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is PaletteChip).ToList();
        var dimmed = chips.Single(b => (b.DataContext as PaletteChip)!.PresetId == "test-mp4");
        var regular = chips.Single(b => (b.DataContext as PaletteChip)!.PresetId == "test-jpg");
        Assert.Contains("dimmed", dimmed.Classes);
        Assert.Equal(0.55, dimmed.Opacity);
        Assert.Equal(((PaletteChip)dimmed.DataContext!).Reason, ToolTip.GetTip(dimmed));
        Assert.True(dimmed.IsEnabled);
        Assert.DoesNotContain("dimmed", regular.Classes);
        Assert.Equal(1, regular.Opacity);
        dimmed.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Assert.Contains("test-mp4", vm.SelectedProfile!.Profile.PresetIds);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-mp4");
        window.Close();
    }

    [AvaloniaFact]
    public void The_keyboard_adds_a_focused_chip_with_enter_and_edits_it_with_shift_f10()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        var window = new Window { Width = 1080, Height = 820, Content = new ToolbarPage { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Border Chip(string id) => window.GetVisualDescendants().OfType<Border>().Single(b => b.DataContext is PaletteChip c && c.PresetId == id);

        var jpg = Chip("test-jpg");
        Assert.True(jpg.Focus(NavigationMethod.Tab));
        Assert.Equal(((PaletteChip)jpg.DataContext!).Label, AutomationProperties.GetName(jpg));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("test-jpg", vm.SelectedProfile!.Profile.PresetIds[^1]);

        var windows = OpenMain();
        try
        {
            // Opening the main window made it the active one; the keys go to the page's window again.
            window.Activate();
            Assert.True(Chip("test-mp4").Focus(NavigationMethod.Tab));
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            SaveInEditor(windows, "jpg");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        }
        finally
        {
            CloseMain(windows);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Editing_a_preset_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        var preset = f.Preset("test-wav");
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == preset.Id);
        preset.TargetFormat = "jpg";
        vm.PresetEdited(preset);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == preset.Id).Reason);
        preset.TargetFormat = "mp4";
        vm.PresetEdited(preset);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == preset.Id).IsDimmed);
    }

    [AvaloniaFact]
    public async Task Saving_a_chip_in_the_editor_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
        var windows = OpenMain();
        try
        {
            var editing = vm.EditChipAsync("test-mp4");
            SaveInEditor(windows, "jpg");
            await editing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        }
        finally { CloseMain(windows); }
    }

    [AvaloniaFact]
    public async Task Saving_a_slice_in_the_editor_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Insert("test-mp4", 0));
        var windows = OpenMain();
        try
        {
            var editing = vm.EditAsync(0);
            SaveInEditor(windows, "wav");
            await editing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            vm.RemoveAt(0);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-mp4");
        }
        finally { CloseMain(windows); }
    }

    // The editor needs a visible owner. Closing from code only hides the main window and posts the real close.
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
    public void Resetting_a_built_in_donut_judges_the_palette_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("wav");
        var images = vm.Profiles.Single(p => p.Profile.Id == "images");
        images.Profile.Extensions = ["wav"];
        vm.SelectedProfile = images;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        vm.Reset(images);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
    }

    [AvaloniaFact]
    public void Closing_the_page_stops_its_judging()
    {
        using var f = new Fixture();
        var first = f.PageWithPalette("png");
        Assert.Equal(0, f.Built.StatusCalls);
        first.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, f.Built.StatusCalls);

        using var second = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        f.Built.StatusCalls = 0;
        f.Catalog.Refresh();
        second.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, f.Built.StatusCalls);
    }

    [AvaloniaFact]
    public void Choosing_a_donut_returns_before_judging_and_the_dispatcher_finishes_it()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        Assert.Equal(0, f.Built.StatusCalls);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-group");
        Assert.DoesNotContain(vm.Palette, c => c.PresetId is "test-jpg" or "test-mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Built.StatusCalls >= 1);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
    }
}

public class ToolbarPaletteModelTests
{
    private static ToolbarProfile Profile(string id, string extension) => new()
    {
        Id = id,
        Name = id,
        Extensions = [extension],
        PresetIds = [],
        BackPresetIds = [],
    };

    private static void Select(ToolbarPageViewModel vm, string id) =>
        vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == id);

    /// <summary>
    /// Searches for the preset's name on the selected profile and checks its chip against a fresh palette session for that
    /// profile's extensions: the same use (<paramref name="use"/>), dimmed unless usable, and the same reason.
    /// </summary>
    private static void AssertJudgedAfresh(PresetAvailabilityTests.Fixture f, ToolbarPageViewModel vm, string id, PresetUse use)
    {
        var formats = vm.SelectedProfile!.Profile.Extensions.Select(FormatRegistry.FindByExtension).OfType<FileFormat>()
            .DistinctBy(x => x.Id).ToList();
        var expected = f.Availability.ForPalette(formats).Judge(f.Preset(id));
        Assert.Equal(use, expected.Use);
        var search = vm.PaletteSearch;
        vm.PaletteSearch = f.Loc.DisplayName(f.Preset(id));
        var chip = Assert.Single(vm.VisiblePalette, c => c.PresetId == id);
        Assert.Equal(expected.Use, chip.Use);
        Assert.Equal(expected.Use != PresetUse.Usable, chip.IsDimmed);
        Assert.Equal(expected.Reason, chip.Reason);
        vm.PaletteSearch = search;
    }

    [AvaloniaFact]
    public void Groups_categories_into_eight_tabs()
    {
        var targets = new (string Format, PaletteTab Tab)[]
        {
            ("png", PaletteTab.Image), ("svg", PaletteTab.Image),
            ("pdf", PaletteTab.Document), ("csv", PaletteTab.Document),
            ("mp4", PaletteTab.Media), ("srt", PaletteTab.Media),
            ("zip", PaletteTab.Archive), ("geojson", PaletteTab.Map),
            (FormatRegistry.Known.First(f => f.Category == FormatCategory.Model3D).Id, PaletteTab.Model3DCad),
            (FormatRegistry.Known.First(f => f.Category == FormatCategory.Cad).Id, PaletteTab.Model3DCad),
            (FormatRegistry.Known.First(f => f.Category == FormatCategory.Font).Id, PaletteTab.Font),
        };
        foreach (var (format, tab) in targets)
            Assert.Equal(tab, ToolbarPageViewModel.TabOf(new Preset { TargetFormat = format }));
        Assert.Equal(PaletteTab.Edit, ToolbarPageViewModel.TabOf(new Preset { Kind = PresetKind.Tool, TargetFormat = "mp4" }));
        Assert.Equal(PaletteTab.Edit, ToolbarPageViewModel.TabOf(new Preset { TargetFormat = BuiltInData.SameAsSource }));
        Assert.Equal(PaletteTab.Image, ToolbarPageViewModel.TabOf(new Preset { TargetFormat = "png" }));
        Assert.Equal(PaletteTab.All, ToolbarPageViewModel.TabOf(new Preset { TargetFormat = "not-registered" }));
        Assert.Equal(PaletteTab.Archive, ToolbarPageViewModel.TabOf(new Preset { TargetFormat = FormatRegistry.Folder }));
    }

    [AvaloniaFact]
    public void Category_mapping_covers_every_enum_value()
    {
        var expected = new Dictionary<FormatCategory, PaletteTab>
        {
            [FormatCategory.Image] = PaletteTab.Image,
            [FormatCategory.Vector] = PaletteTab.Image,
            [FormatCategory.Pdf] = PaletteTab.Document,
            [FormatCategory.Document] = PaletteTab.Document,
            [FormatCategory.Spreadsheet] = PaletteTab.Document,
            [FormatCategory.Presentation] = PaletteTab.Document,
            [FormatCategory.Hwp] = PaletteTab.Document,
            [FormatCategory.Text] = PaletteTab.Document,
            [FormatCategory.Ebook] = PaletteTab.Document,
            [FormatCategory.Data] = PaletteTab.Document,
            [FormatCategory.Video] = PaletteTab.Media,
            [FormatCategory.Audio] = PaletteTab.Media,
            [FormatCategory.Subtitle] = PaletteTab.Media,
            [FormatCategory.Archive] = PaletteTab.Archive,
            [FormatCategory.Font] = PaletteTab.Font,
            [FormatCategory.Model3D] = PaletteTab.Model3DCad,
            [FormatCategory.Cad] = PaletteTab.Model3DCad,
            [FormatCategory.Geo] = PaletteTab.Map,
        };
        Assert.Equal(Enum.GetValues<FormatCategory>().Length, expected.Count);
        var definitions = ToolbarPageViewModel.TabDefinitions;
        Assert.Equal(Enum.GetValues<PaletteTab>(), definitions.Select(t => t.Id));
        Assert.Equal(expected.OrderBy(x => x.Key), definitions.SelectMany(t => t.Categories.Select(c => new KeyValuePair<FormatCategory, PaletteTab>(c, t.Id)))
            .OrderBy(x => x.Key));
        foreach (var category in Enum.GetValues<FormatCategory>())
            Assert.Equal(expected[category], ToolbarPageViewModel.TabOf(new Preset
            {
                TargetFormat = FormatRegistry.Known.First(f => f.Category == category).Id,
            }));
    }

    [AvaloniaFact]
    public void Two_tabs_hide_the_row_only_when_their_preset_ids_match()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Store.Presets.Add(new Preset { Id = "test-unregistered", Name = "OnlyPair unknown", TargetFormat = "not-registered" });
        f.Store.Presets.Add(new Preset { Id = "test-image-pair", Name = "OnlyPair image", TargetFormat = "png" });
        using var vm = f.PageWithPalette();
        vm.PaletteSearch = "OnlyPair";
        Assert.Equal([PaletteTab.All, PaletteTab.Image], vm.Tabs.Select(t => t.Id));
        Assert.True(vm.ShowTabRow);
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-unregistered");
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-image-pair");
        vm.SelectedTab = vm.Tabs[1];
        Assert.DoesNotContain(vm.VisiblePalette, c => c.PresetId == "test-unregistered");
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-image-pair");

        vm.PaletteSearch = "OnlyPair image";
        Assert.Equal([PaletteTab.All, PaletteTab.Image], vm.Tabs.Select(t => t.Id));
        Assert.False(vm.ShowTabRow);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-image-pair");
    }

    [AvaloniaFact]
    public void Tab_labels_reuse_format_categories_in_declaration_order()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.PageWithPalette();
        var expectedKeys = new[]
        {
            "toolbar.tab.all", "format.category.Image", "format.category.Document",
            "toolbar.tab.media", "toolbar.tab.edit", "toolbar.tab.model3d_cad",
            "format.category.Geo", "format.category.Archive", "format.category.Font",
        };
        Assert.Equal(Enum.GetValues<PaletteTab>(), vm.Tabs.Select(t => t.Id));
        Assert.Equal(expectedKeys.Select(key => f.Loc[key]), vm.Tabs.Select(t => t.Label));
    }

    [AvaloniaFact]
    public void Search_finds_hidden_presets_across_tabs()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        using var vm = f.PageWithPalette("png");
        Assert.DoesNotContain(vm.VisiblePalette, c => c.PresetId == "test-wav");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement("test-palette", "test-wav"));
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-wav");
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Image);
        vm.PaletteSearch = " hidden WAV ";
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Assert.Contains(vm.Tabs, t => t.Id == PaletteTab.Media);
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Media);
        var chip = Assert.Single(vm.VisiblePalette, c => c.PresetId == "test-wav");
        Assert.Equal(PresetUse.Unusable, chip.Use);
        Assert.False(chip.CanAdd);
        Assert.True(chip.IsDimmed);
        Assert.Equal(f.Loc["error.no_route"], chip.Reason);
        vm.PaletteSearch = "";
        Assert.DoesNotContain(vm.VisiblePalette, c => c.PresetId == "test-wav");
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-group");
    }

    [AvaloniaFact]
    public void Selecting_a_cached_profile_reuses_its_results()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("cache-a", "png");
        using var vm = f.Page(a);
        var b = Profile("cache-b", "mp4");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        var judged = new List<(string Profile, string Preset)>();
        vm.JudgementObserved = (profile, preset) => judged.Add((profile, preset));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement(a.Id, "test-jpg"));
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement(b.Id, "test-wav"));
        var count = judged.Count;
        Select(vm, a.Id);
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-jpg");
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(count, judged.Count);
    }

    [AvaloniaFact]
    public void Adding_presets_judges_only_new_ids_on_visit()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("add-a", "png");
        using var vm = f.Page(a);
        var b = Profile("add-b", "mp4");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, a.Id);
        var observed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
        f.Store.Presets.Add(new Preset { Id = "test-jpg-after", TargetFormat = "jpg" });
        vm.AddGroup(new Preset { Id = "test-added-group", Kind = PresetKind.Group, SubPresetIds = ["test-jpg"] });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([("add-a", "test-jpg-after")], observed);
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-added-group");
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([("add-a", "test-jpg-after"), ("add-b", "test-jpg-after")], observed);
        Assert.DoesNotContain(observed, x => x.Item2 is "test-jpg" or "test-added-group");
    }

    [AvaloniaFact]
    public async Task External_library_save_rejudges_cached_profiles()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        var a = Profile("external-a", "png");
        using var vm = f.Page(a);
        var b = Profile("external-b", "mp4");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        AssertJudgedAfresh(f, vm, "test-wav", PresetUse.Unusable);
        Select(vm, a.Id);
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Image);
        var observed = new List<(string, string)>();
        var lastCandidate = f.Store.Presets.Last(p => !p.IsGroup).Id;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.JudgementObserved = (id, preset) =>
        {
            observed.Add((id, preset));
            if (id == a.Id && preset == lastCandidate)
                completed.TrySetResult();
        };
        f.Preset("test-wav").TargetFormat = "jpg";
        f.Store.SaveLibrary();
        Assert.False(vm.HasJudgement(a.Id, "test-wav"));
        Assert.False(vm.HasJudgement(b.Id, "test-wav"));
        Assert.DoesNotContain(vm.Tabs, t => t.Id == PaletteTab.Image);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Dispatcher.UIThread.RunJobs();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PaletteTab.Image, vm.SelectedTab!.Id);
        Assert.Contains(vm.VisiblePalette, c => c.PresetId == "test-wav" && c.Use == PresetUse.Usable);
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        AssertJudgedAfresh(f, vm, "test-wav", PresetUse.Unusable);
        Assert.Equal(1, observed.Count(x => x == (a.Id, "test-wav")));
        Assert.Equal(1, observed.Count(x => x == (b.Id, "test-wav")));
    }

    [AvaloniaFact]
    public void External_save_on_another_thread_during_own_save_invalidates_all_profiles()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("thread-a", "png");
        using var vm = f.Page(a);
        var b = Profile("thread-b", "mp4");
        var c = Profile("thread-c", "png");
        foreach (var profile in new[] { b, c })
        {
            f.Store.Profiles.Add(profile);
            vm.Profiles.Add(new ProfileItemViewModel(profile, f.Loc));
        }
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, c.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement(a.Id, "test-wav"));
        Assert.True(vm.HasJudgement(b.Id, "test-wav"));
        var postedOffThread = false;
        var ownThread = Environment.CurrentManagedThreadId;
        f.Preset("test-wav").TargetFormat = "jpg";
        vm.DuringOwnSave = () =>
        {
            var worker = new Thread(() =>
            {
                postedOffThread = Environment.CurrentManagedThreadId != ownThread;
                f.Store.SaveLibrary();
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "external save thread did not finish");
        };
        vm.ProfileName = "Updated C";
        vm.DuringOwnSave = null;
        Assert.True(postedOffThread);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasJudgement(a.Id, "test-wav"));
        Assert.False(vm.HasJudgement(b.Id, "test-wav"));
        var observed = new List<(string Profile, string Preset)>();
        vm.JudgementObserved = (profile, preset) => observed.Add((profile, preset));
        Select(vm, a.Id);
        Dispatcher.UIThread.RunJobs();
        AssertJudgedAfresh(f, vm, "test-wav", PresetUse.Usable);
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        AssertJudgedAfresh(f, vm, "test-wav", PresetUse.Unusable);
        Assert.Equal(1, observed.Count(x => x == (a.Id, "test-wav")));
        Assert.Equal(1, observed.Count(x => x == (b.Id, "test-wav")));
    }

    [AvaloniaFact]
    public void Search_change_cancels_pending_external_tab_restore()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Image);
        f.Store.SaveLibrary();
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        Assert.DoesNotContain(vm.Tabs, t => t.Id == PaletteTab.Image);
        vm.PaletteSearch = "test-jpg";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
        vm.PaletteSearch = "";
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ShowTabRow);
        Assert.Contains(vm.Tabs, t => t.Id == PaletteTab.Image);
        Assert.Equal(PaletteTab.All, vm.SelectedTab!.Id);
    }

    [AvaloniaFact]
    public void Deleted_preset_never_resurrects_from_cache()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        f.Preset("test-wav").Name = "Hidden WAV sample";
        f.Preset("test-jpg").Name = "Hidden JPG sample";
        var a = Profile("delete-a", "png");
        using var vm = f.Page(a);
        var b = Profile("delete-b", "mp4");
        // C has the preset on its front face.
        var c = Profile("delete-c", "png");
        c.PresetIds.Add("test-wav");
        foreach (var other in new[] { b, c })
        {
            f.Store.Profiles.Add(other);
            vm.Profiles.Add(new ProfileItemViewModel(other, f.Loc));
        }
        var page = new ToolbarPage { DataContext = vm };
        var window = new Window { Width = 1080, Height = 820, Content = page };
        List<string> Shown() => page.FindControl<Border>("PaletteBox")!.GetVisualDescendants().OfType<Border>()
            .Where(x => x.Classes.Contains("chip") && x.DataContext is PaletteChip).Select(x => ((PaletteChip)x.DataContext!).PresetId).ToList();
        try
        {
            window.Show();
            RenderTests.Pump();
            vm.PaletteSearch = "Hidden";
            RenderTests.Pump();
            Assert.Contains("test-wav", Shown());
            Select(vm, b.Id);
            RenderTests.Pump();
            // Another donut starts with an empty search (#427), so type it again.
            Assert.Equal("", vm.PaletteSearch);
            vm.PaletteSearch = "Hidden";
            RenderTests.Pump();
            Assert.Contains("test-wav", Shown());
            Assert.True(vm.HasJudgement(a.Id, "test-wav"));
            Assert.True(vm.HasJudgement(b.Id, "test-wav"));
            Assert.Equal(["test-wav"], c.PresetIds);

            // As the presets page deletes: removed, then saved. Checked before any posted judging runs.
            f.Store.Presets.Remove(f.Preset("test-wav"));
            f.Store.SaveLibrary();
            Assert.False(vm.HasJudgement(a.Id, "test-wav"));
            Assert.False(vm.HasJudgement(b.Id, "test-wav"));
            Assert.Null(f.Store.FindPreset("test-wav"));
            Assert.DoesNotContain("test-wav", c.PresetIds);

            Dispatcher.UIThread.RunJobs();
            foreach (var id in new[] { a.Id, b.Id })
            {
                Select(vm, id);
                RenderTests.Pump();
                vm.PaletteSearch = "Hidden";
                RenderTests.Pump();
                var shown = Shown();
                Assert.DoesNotContain("test-wav", shown);
                Assert.Contains("test-jpg", shown);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Moving_slices_and_flipping_keep_judgement_cache()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.Page(Profile("moving-a", "png"));
        Dispatcher.UIThread.RunJobs();
        var profile = vm.SelectedProfile!.Profile;
        string[] moved = ["test-jpg", "test-mp4", "test-rotate-png", "test-motion"];
        Assert.All(moved, id => Assert.True(vm.HasJudgement("moving-a", id)));
        var observed = new List<string>();
        vm.JudgementObserved = (_, preset) => observed.Add(preset);

        // Front: two slices, then the first moved onto the second.
        Assert.True(vm.Insert("test-jpg", 0));
        Assert.True(vm.Insert("test-mp4", 1));
        vm.Move(0, 1);
        Assert.Equal(["test-mp4", "test-jpg"], profile.PresetIds);

        // Back: the same, the other way round.
        vm.EditingBack = true;
        Assert.True(vm.Insert("test-rotate-png", 0));
        Assert.True(vm.Insert("test-motion", 1));
        vm.Move(1, 0);
        Assert.Equal(["test-motion", "test-rotate-png"], profile.BackPresetIds);
        Assert.Equal(["test-mp4", "test-jpg"], profile.PresetIds);
        Assert.DoesNotContain(vm.Palette, c => moved.Contains(c.PresetId));

        // Flipped back to the front: both faces stay off the palette until a slice comes off.
        vm.EditingBack = false;
        Assert.DoesNotContain(vm.Palette, c => moved.Contains(c.PresetId));
        vm.RemoveAt(0);
        Assert.Equal(["test-jpg"], profile.PresetIds);
        Assert.Equal(["test-motion", "test-rotate-png"], profile.BackPresetIds);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-mp4");
        Assert.All(moved, id => Assert.True(vm.HasJudgement("moving-a", id)));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(observed);
    }

    [AvaloniaFact]
    public void Editing_a_preset_invalidates_its_id_in_every_profile()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("edit-a", "png");
        using var vm = f.Page(a);
        var b = Profile("edit-b", "png");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, a.Id);
        var observed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
        var preset = f.Preset("test-wav");
        preset.TargetFormat = "jpg";
        vm.PresetEdited(preset);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Palette, c => c.PresetId == preset.Id && c.Use == PresetUse.Usable);
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([("edit-a", preset.Id), ("edit-b", preset.Id)], observed);
        Assert.DoesNotContain(observed, x => x.Item2 == "test-jpg");
    }

    [AvaloniaFact]
    public void Extension_save_invalidates_only_its_profile()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("extensions-a", "mp4");
        using var vm = f.Page(a);
        var b = Profile("extensions-b", "png");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, a.Id);
        var observed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
        int OnA() => observed.Count(x => x == (a.Id, "test-jpg"));
        // B (PNG) keeps its cached judgement: the chip is there at once, and nothing is judged for B.
        void BStaysCached()
        {
            Select(vm, b.Id);
            Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg" && c.Use == PresetUse.Usable);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(observed, x => x.Item1 == b.Id);
            Select(vm, a.Id);
            Dispatcher.UIThread.RunJobs();
        }

        vm.ExtensionTags!.AddCommand.Execute("png");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, OnA());
        AssertJudgedAfresh(f, vm, "test-jpg", PresetUse.Usable);
        BStaysCached();
        vm.ExtensionTags!.RemoveCommand.Execute(vm.ExtensionTags.Tags.Single(t => t.Value == "png"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, OnA());
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        AssertJudgedAfresh(f, vm, "test-jpg", PresetUse.Unusable);
        BStaysCached();

        // The same through the mixed-selection checkboxes: checked, then unchecked.
        vm.IsMixed = true;
        Dispatcher.UIThread.RunJobs();
        var judged = OnA();
        var png = vm.MixedExtensions!.Groups.SelectMany(g => g.Extensions).First(e => e.Extension == "png");
        png.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(judged + 1, OnA());
        AssertJudgedAfresh(f, vm, "test-jpg", PresetUse.Usable);
        BStaysCached();
        png = vm.MixedExtensions!.Groups.SelectMany(g => g.Extensions).First(e => e.Extension == "png");
        png.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(judged + 2, OnA());
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        AssertJudgedAfresh(f, vm, "test-jpg", PresetUse.Unusable);
        BStaysCached();
    }

    [AvaloniaFact]
    public void Catalog_change_invalidates_every_profile()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("catalog-a", "png");
        using var vm = f.Page(a);
        var b = Profile("catalog-b", "png");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, a.Id);
        Assert.Equal(PresetUse.NeedsPackage, vm.Palette.Single(c => c.PresetId == "test-mp4").Use);
        var observed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
        f.Gated.Available = true;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PresetUse.Usable, vm.Palette.Single(c => c.PresetId == "test-mp4").Use);
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, observed.Count(x => x == (a.Id, "test-mp4")));
        Assert.Equal(1, observed.Count(x => x == (b.Id, "test-mp4")));
    }

    [AvaloniaFact]
    public void Resetting_a_built_in_donut_invalidates_only_that_profile()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var images = f.Store.Profiles.Single(p => p.Id == "images");
        images.Extensions = ["wav"];
        var b = Profile("reset-b", "png");
        b.IsFallback = true;
        using var vm = f.Page(b);
        Dispatcher.UIThread.RunJobs();
        Select(vm, images.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(vm, images.Id);
        var observed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
        vm.Reset(vm.SelectedProfile!);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, observed.Count(x => x == (images.Id, "test-jpg")));
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg" && c.Use == PresetUse.Usable);
        Select(vm, b.Id);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(observed, x => x.Item1 == b.Id);
    }

    [AvaloniaFact]
    public void Closing_a_cached_page_clears_both_profiles_and_subscriptions()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var a = Profile("closing-a", "png");
        var vm = f.Page(a);
        var b = Profile("closing-b", "mp4");
        f.Store.Profiles.Add(b);
        vm.Profiles.Add(new ProfileItemViewModel(b, f.Loc));
        Dispatcher.UIThread.RunJobs();
        Select(vm, b.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement(a.Id, "test-wav"));
        Assert.True(vm.HasJudgement(b.Id, "test-wav"));
        var library = typeof(UserDataStore).GetField("LibraryChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var catalog = typeof(ConverterCatalog).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(library);
        Assert.NotNull(catalog);
        bool Subscribed(object source, FieldInfo field) =>
            (field.GetValue(source) as EventHandler)?.GetInvocationList().Any(d => ReferenceEquals(d.Target, vm)) == true;
        Assert.True(Subscribed(f.Store, library));
        Assert.True(Subscribed(f.Catalog, catalog));
        var closed = new List<(string, string)>();
        vm.JudgementObserved = (id, preset) => closed.Add((id, preset));
        f.Store.Presets.Add(new Preset { Id = "test-late-control", TargetFormat = "jpg" });
        vm.PaletteSearch = "test-late-control";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains((b.Id, "test-late-control"), closed);
        closed.Clear();
        f.Store.Presets.Add(new Preset { Id = "test-late", TargetFormat = "jpg" });
        vm.PaletteSearch = "test-late";
        Assert.False(vm.HasJudgement(b.Id, "test-late"));
        vm.Dispose();
        Assert.False(vm.HasJudgement(a.Id, "test-wav"));
        Assert.False(vm.HasJudgement(b.Id, "test-wav"));
        Assert.False(Subscribed(f.Store, library));
        Assert.False(Subscribed(f.Catalog, catalog));
        f.Store.SaveLibrary();
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasJudgement(a.Id, "test-wav"));
        Assert.Empty(closed);
        Assert.False(vm.HasJudgement(b.Id, "test-late"));

        // The page opened again: a new view model on the same library judges each profile afresh, once.
        using var reopened = new ToolbarPageViewModel(f.Store, f.Loc, AppHost.Get<WindowService>(), f.Availability, f.Catalog,
            NullLogger<ToolbarPageViewModel>.Instance);
        var observed = new List<(string, string)>();
        reopened.JudgementObserved = (id, preset) => observed.Add((id, preset));
        Select(reopened, a.Id);
        Dispatcher.UIThread.RunJobs();
        Select(reopened, b.Id);
        Dispatcher.UIThread.RunJobs();
        Select(reopened, a.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.True(reopened.HasJudgement(a.Id, "test-wav"));
        Assert.True(reopened.HasJudgement(b.Id, "test-wav"));
        Assert.True(reopened.HasJudgement(b.Id, "test-late"));
        Assert.Equal(1, observed.Count(x => x == (a.Id, "test-wav")));
        Assert.Equal(1, observed.Count(x => x == (b.Id, "test-wav")));
        Assert.Empty(closed);
    }

    [AvaloniaFact]
    public async Task Deleted_profile_loses_its_cache_entry()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        var original = Profile("reused-id", "png");
        using var vm = f.Page(original);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJudgement(original.Id, "test-jpg"));
        var windows = AppHost.Get<WindowService>();
        Assert.Null(windows.Main);
        windows.ShowMain();
        try
        {
            var delete = vm.DeleteProfileCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(windows.Main!.OwnedWindows);
            // Found wherever it sits: the window zoom wraps a dialog's content.
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await delete.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(vm.HasJudgement(original.Id, "test-jpg"));
            var replacement = Profile(original.Id, "mp4");
            f.Store.Profiles.Add(replacement);
            vm.Profiles.Add(new ProfileItemViewModel(replacement, f.Loc));
            var observed = new List<(string, string)>();
            vm.JudgementObserved = (id, preset) => observed.Add((id, preset));
            Select(vm, replacement.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, observed.Count(x => x == (replacement.Id, "test-jpg")));
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        }
        finally
        {
            windows.Main?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
