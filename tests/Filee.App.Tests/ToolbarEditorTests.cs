// Keyboard behaviour of the chip input (TagEditor) and the profile editing of the donut toolbar page,
// driven through a headless window.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Profiles;
using Filee.Core.Settings;

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
            var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>());

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
    public void A_new_group_heads_the_available_presets_and_every_profile_count_stays_right()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var presets = store.Presets.ToList();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>());
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.PaletteSearch = "png"; // a search that would hide it
            var pdf = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            pdf.Profile.PresetIds.RemoveAt(0); // changed elsewhere, as when a deleted group leaves every donut

            vm.AddGroup(new Filee.Core.Presets.Preset
            {
                Id = "test-new-group",
                Name = "Web",
                Kind = Filee.Core.Presets.PresetKind.Group,
                SubPresetIds = ["to-png"],
            });

            Assert.Equal("", vm.PaletteSearch);
            // Up with the groups at the top, above every conversion: not below ~80 presets.
            var chips = vm.VisiblePalette.Select(c => store.FindPreset(c.PresetId)!).ToList();
            var at = chips.FindIndex(p => p.Id == "test-new-group");
            Assert.True(at >= 0 && chips.Take(at).All(p => p.IsGroup), $"at {at}");
            Assert.Equal($"{pdf.Profile.PresetIds.Count}", pdf.Count);
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
    public void The_last_mixed_profile_stays_mixed()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>());
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
