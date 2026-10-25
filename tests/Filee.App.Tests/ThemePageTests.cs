using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class ThemePageTests
{
    [AvaloniaTheory]
    [InlineData("en", ThemeMode.Light)]
    [InlineData("en", ThemeMode.Dark)]
    [InlineData("ar", ThemeMode.Light)]
    [InlineData("ar", ThemeMode.Dark)]
    public void Visual_picker_and_swatches_stay_in_sync_and_persist(string language, ThemeMode mode)
    {
        TestServices.EnsureInitialized(language);
        var directory = Directory.CreateTempSubdirectory("filee-theme-").FullName;
        var store = new UserDataStore(directory);
        store.Load();
        store.Settings.Theme.Mode = mode;
        AppHost.Get<ThemeService>().Apply(store.Settings.Theme, language);
        var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
        var page = new ThemePage { DataContext = vm };
        var window = new Window { Content = page, Width = 940, Height = 700 };
        try
        {
            window.Show();
            RenderTests.Pump();
            var picker = page.FindControl<ColorPicker>("AccentPicker")!;
            var swatches = page.FindControl<ListBox>("AccentSwatches")!;
            Assert.Equal(vm.Accent, picker.Color);
            Assert.False(picker.IsAlphaEnabled);
            Assert.False(picker.IsColorComponentsVisible);

            // Custom choices outside the palette must survive the list losing its selection.
            picker.Color = Color.Parse("#287FA3");
            RenderTests.Pump();
            Assert.Equal("#287FA3", vm.AccentHex);
            Assert.Equal("#287FA3", store.Settings.Theme.Accent);
            Assert.Null(swatches.SelectedItem);

            var swatch = (ListBoxItem)swatches.ContainerFromIndex(1)!;
            var point = swatch.TranslatePoint(new Point(swatch.Bounds.Width / 2, swatch.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            RenderTests.Pump();
            Assert.Equal(vm.Swatches[1], picker.Color);
            Assert.Equal(vm.Swatches[1], swatches.SelectedItem);
            Assert.True(swatch.IsSelected);

            Assert.True(swatch.Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            RenderTests.Pump();
            Assert.Equal(vm.Accent, picker.Color);
            Assert.Equal(vm.Accent, swatches.SelectedItem);

            // A colour typed as #RRGGBB moves the picker too.
            vm.AccentHex = "#2E8B57";
            RenderTests.Pump();
            Assert.Equal(Color.Parse("#2E8B57"), picker.Color);
            Assert.Equal("#2E8B57", store.Settings.Theme.Accent);

            // Open the actual flyout with the keyboard and capture the spectrum for visual review.
            var button = picker.GetVisualDescendants().OfType<Button>().First();
            Assert.True(button.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            RenderTests.Pump();
            Assert.True(button.Flyout!.IsOpen);
            var content = Assert.IsAssignableFrom<Control>(Assert.IsType<Flyout>(button.Flyout).Content);
            var spectrum = content.GetVisualDescendants().OfType<ColorSpectrum>().Single();
            Assert.True(spectrum.Focus(), $"Focusable={spectrum.Focusable}; Visible={spectrum.IsEffectivelyVisible}; Enabled={spectrum.IsEffectivelyEnabled}; Bounds={spectrum.Bounds}");
            var before = vm.Accent;
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            RenderTests.Pump();
            Assert.NotEqual(before, vm.Accent);
            Assert.Equal(vm.Accent, picker.Color);
            Assert.Equal(vm.AccentHex, store.Settings.Theme.Accent);
            RenderTests.Save(window, $"theme-picker-{language}-{mode}.png");
            button.Flyout.Hide();

            var restored = new UserDataStore(directory);
            restored.Load();
            Assert.Equal(store.Settings.Theme.Accent, restored.Settings.Theme.Accent);
            var restoredVm = new ThemePageViewModel(restored, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
            Assert.Equal(vm.Accent, restoredVm.Accent);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Preview_toggle_is_interactive_but_does_not_change_settings()
    {
        TestServices.EnsureInitialized();
        var directory = Directory.CreateTempSubdirectory("filee-theme-preview-").FullName;
        var store = new UserDataStore(directory);
        store.Load();
        AppHost.Get<ThemeService>().Apply(store.Settings.Theme, "en");
        var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
        var page = new ThemePage { DataContext = vm };
        var window = new Window { Content = page, Width = 940, Height = 700 };

        try
        {
            window.Show();
            RenderTests.Pump();

            var toggle = page.FindControl<ToggleSwitch>("PreviewToggle")!;
            Assert.NotNull(toggle);
            Assert.False(toggle.IsChecked);
            Assert.Contains("sample", AutomationProperties.GetName(toggle) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var settingsBefore = System.Text.Json.JsonSerializer.Serialize(store.Settings);
            var point = toggle.TranslatePoint(
                new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            RenderTests.Pump();

            Assert.True(toggle.IsChecked);
            Assert.Equal(settingsBefore, System.Text.Json.JsonSerializer.Serialize(store.Settings));

            Assert.True(toggle.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            RenderTests.Pump();

            Assert.False(toggle.IsChecked);
            Assert.Equal(settingsBefore, System.Text.Json.JsonSerializer.Serialize(store.Settings));

            foreach (var name in new[] { "PreviewPrimaryButton", "PreviewSecondaryButton" })
            {
                var button = page.FindControl<Button>(name)!;
                Assert.NotNull(button);
                Assert.False(button.Focusable);
                Assert.False(button.IsTabStop);
            }
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
            Directory.Delete(directory, true);
        }
    }

}
