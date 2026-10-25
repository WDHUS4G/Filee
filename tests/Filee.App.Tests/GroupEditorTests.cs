// The group editor (Settings → Toolbar → New group): picking the presets a group fans out, their order, the limit of
// six, saving and deleting.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class GroupEditorTests
{
    private static GroupEditorViewModel Edit(Preset group, bool isNew = false)
    {
        TestServices.EnsureInitialized("en");
        return new GroupEditorViewModel(group, AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), isNew);
    }

    private static void Add(GroupEditorViewModel vm, string presetId)
    {
        vm.ToAdd = vm.Available.Single(c => c.Value == presetId);
        vm.AddCommand.Execute(null);
    }

    [AvaloniaFact]
    public void Presets_are_added_in_order_moved_and_removed_then_saved_into_the_group()
    {
        var group = new Preset { Name = "Web", Kind = PresetKind.Group, TargetFormat = BuiltInData.SameAsSource };
        var vm = Edit(group, isNew: true);
        Assert.False(vm.IsValid); // nothing to fan out yet
        Assert.False(vm.CanDelete);
        Assert.DoesNotContain(vm.Available, c => AppHost.Get<UserDataStore>().FindPreset(c.Value)!.IsGroup);

        Add(vm, "to-png");
        Add(vm, "to-jpg");
        Add(vm, "to-webp");
        Assert.DoesNotContain(vm.Available, c => c.Value == "to-jpg"); // each preset once
        vm.MoveUpCommand.Execute(vm.Members[2]);
        vm.RemoveCommand.Execute(vm.Members[0]);
        Assert.Equal("Presets (2 / 6)", vm.MembersTitle);

        Assert.True(vm.Apply());
        Assert.Equal(["to-webp", "to-jpg"], group.SubPresetIds);
        Assert.True(group.IsGroup);
    }

    [AvaloniaFact]
    public void Pdf_edit_group_keeps_its_conversion_and_tool_actions_when_saved()
    {
        var group = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-edit");
        var vm = Edit(group);

        Assert.Equal(group.SubPresetIds, vm.Members.Select(m => m.PresetId));
        Assert.Equal(Preset.MaxSubPresets, vm.Members.Count);
        Assert.True(vm.Apply());
        Assert.Equal([
            "tool-pdf-crop", "pdf-rotate-left", "pdf-rotate-right",
            "tool-pdf-pages", "pdf-split", "tool-pdf-add",
        ], group.SubPresetIds);
    }

    [AvaloniaFact]
    public void A_group_takes_six_presets_at_most_and_needs_a_name()
    {
        var vm = Edit(new Preset { Name = "Many", Kind = PresetKind.Group });
        foreach (var id in new[] { "to-png", "to-jpg", "to-webp", "to-tiff", "to-bmp", "to-ico" })
            Add(vm, id);
        Assert.True(vm.IsFull);
        vm.ToAdd = vm.Available.First();
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.Name = "  ";
        Assert.False(vm.IsValid);
        Assert.NotNull(vm.NameError);
    }

    [AvaloniaFact]
    public void Deleting_a_group_removes_it_from_the_presets_and_every_donut_but_keeps_its_presets()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var group = new Preset { Id = "test-delete-group", Name = "Gone", Kind = PresetKind.Group, SubPresetIds = ["to-png"] };
        var images = store.Profiles.Single(p => p.Id == "images");
        store.Presets.Add(group);
        images.PresetIds.Add(group.Id);
        try
        {
            var vm = Edit(group);
            Assert.True(vm.CanDelete);
            vm.Delete();

            Assert.True(vm.Deleted);
            Assert.Null(store.FindPreset(group.Id));
            Assert.DoesNotContain(group.Id, images.PresetIds);
            Assert.NotNull(store.FindPreset("to-png"));
        }
        finally
        {
            store.Presets.Remove(group);
            images.PresetIds.Remove(group.Id);
        }
    }

    [AvaloniaFact]
    public void The_first_cant_move_up_the_last_cant_move_down_and_a_full_group_says_its_limit()
    {
        var vm = Edit(new Preset { Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-jpg", "to-webp"] });
        Assert.False(vm.MoveUpCommand.CanExecute(vm.Members[0]));
        Assert.True(vm.MoveDownCommand.CanExecute(vm.Members[0]));
        Assert.True(vm.MoveUpCommand.CanExecute(vm.Members[2]));
        Assert.False(vm.MoveDownCommand.CanExecute(vm.Members[2]));
        vm.MoveUpCommand.Execute(vm.Members[2]); // now in the middle: both ways
        Assert.True(vm.MoveDownCommand.CanExecute(vm.Members[1]));
        Assert.Equal("A group holds up to 6 presets.", vm.FullNote);
    }

    [AvaloniaFact]
    public void Delete_asks_over_the_editor_itself_and_names_the_group_as_saved()
    {
        var group = new Preset { Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png"] };
        var vm = Edit(group);
        Window? askedOver = null;
        string? asked = null;
        var window = new GroupEditorWindow
        {
            DataContext = vm,
            Confirm = (owner, message) =>
            {
                askedOver = owner;
                asked = message;
                return Task.FromResult(false); // "Cancel"
            },
        };
        window.Show();
        vm.Name = "Renamed but not saved";
        window.FindControl<Button>("DeleteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(window, askedOver); // modal to the editor, which stays blocked (and visible over the donut)
        Assert.Contains("“Web”", asked);
        Assert.False(vm.Deleted);
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_on_the_chosen_preset_adds_it_instead_of_saving()
    {
        var vm = Edit(new Preset { Name = "Web", Kind = PresetKind.Group }, isNew: true);
        var window = new GroupEditorWindow { DataContext = vm };
        window.Show();
        vm.ToAdd = vm.Available.Single(c => c.Value == "to-png");
        window.FindControl<ComboBox>("AddBox")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("to-png", Assert.Single(vm.Members).PresetId);
        Assert.True(window.IsVisible); // not saved and closed
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("en")]
    public void The_group_editor_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new GroupEditorViewModel(
            new Preset { Name = language == "ko" ? "이미지 도구" : "Image tools", Kind = PresetKind.Group, SubPresetIds = ["half-size", "grayscale", "remove-background"] },
            AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>());
        var window = new GroupEditorWindow { DataContext = vm };
        window.Show();
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"group-editor-{language}.png")))
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }
}
