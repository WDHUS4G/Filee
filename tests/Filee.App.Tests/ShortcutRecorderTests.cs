// #450: the shortcut recorder couldn't take Space or Enter. The focused button handled them first, and Space's release
// clicked it, which switched recording off and kept the old shortcut, so even the default Ctrl+Alt+Space couldn't be
// recorded again.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class ShortcutRecorderTests
{
    [AvaloniaFact]
    public void Ctrl_Alt_Space_is_recorded_on_the_button()
    {
        TestServices.EnsureInitialized();
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.Triggers.Select(t => t.Clone()).ToList();
        Window? window = null;
        try
        {
            store.Settings.Triggers.Clear();
            store.Settings.Triggers.Add(new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl, Key = "K" });
            var vm = new TriggersPageViewModel(store, AppHost.Get<ILocalizer>());
            var shortcut = vm.Gestures.Single();
            window = new Window { Content = new TriggersPage { DataContext = vm }, Width = 820, Height = 1500 };
            window.Show();
            RenderTests.Pump();

            var recorder = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Classes.Contains("recorder"));
            Assert.True(recorder.Focus());
            shortcut.IsRecording = true;
            Assert.True(recorder.IsChecked);

            window.KeyPress(Key.Space, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();

            Assert.False(shortcut.IsRecording); // and the release didn't click it back on
            Assert.False(recorder.IsChecked);
            Assert.Equal("Space", shortcut.Key);
            Assert.Equal(ModifierKeys.Ctrl | ModifierKeys.Alt, store.Settings.Triggers.Single().Modifiers);
            Assert.Equal("Space", store.Settings.Triggers.Single().Key);

            // Enter is taken as well, without clicking the button.
            shortcut.IsRecording = true;
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, "\r");
            window.KeyRelease(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.False(shortcut.IsRecording);
            Assert.Equal(ModifierKeys.Shift, store.Settings.Triggers.Single().Modifiers);
            Assert.Equal("Enter", store.Settings.Triggers.Single().Key);

            // Space on the flip recorder is refused with the reason shown, instead of clicking the button.
            var flip = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "FlipRecorder");
            Assert.True(flip.Focus());
            vm.Flip.IsRecording = true;
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.Flip.IsRecording);
            Assert.NotNull(vm.Flip.Error);
        }
        finally
        {
            window?.Close();
            store.Settings.Triggers.Clear();
            store.Settings.Triggers.AddRange(before);
            store.Settings.Donut.FlipKey = DonutSettings.DefaultFlipKey;
            store.SaveSettings();
        }
    }
}
