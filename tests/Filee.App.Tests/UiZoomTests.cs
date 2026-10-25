// Window zoom (UiZoom): Ctrl + wheel, Ctrl + plus / minus and Ctrl + 0 (Cmd on a Mac) zoom every tracked window, small
// wheel deltas add up to whole steps, the size is written once after a moment, windows that can't be resized grow
// with it, and the Theme page shows and resets it.
// In the main-window collection: the zoom is app-wide, and a main window rendered meanwhile would be zoomed too.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class UiZoomTests
{
    /// <summary>The platform's command key as the headless input takes it: Ctrl, or Cmd on a Mac.</summary>
    private static RawInputModifiers Command(TopLevel window) =>
        (window.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control) == KeyModifiers.Meta
            ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static Window Tracked(Control? content = null)
    {
        UiZoom.Configure(1, save: null);
        var window = new Window { Content = content ?? new Border { Width = 100, Height = 100 }, Width = 300, Height = 300 };
        UiZoom.Track(window);
        window.Show();
        return window;
    }

    private static void Close(Window window)
    {
        UiZoom.Configure(1, save: null);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_and_the_wheel_or_plus_minus_zoom_every_window()
    {
        TestServices.EnsureInitialized("en");
        var window = Tracked();
        try
        {
            var command = Command(window);
            var scaled = Assert.IsType<LayoutTransformControl>(window.Content);
            window.KeyPress(Key.OemPlus, command, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            Assert.Equal(1.1, Assert.IsType<ScaleTransform>(scaled.LayoutTransform).ScaleX, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), command);
            Assert.Equal(1.2, UiZoom.Scale, 6);
            window.KeyPress(Key.OemMinus, command, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            window.KeyPress(Key.D0, command, PhysicalKey.None, null);
            Assert.Equal(1, UiZoom.Scale, 6);
            Assert.Null(scaled.LayoutTransform);
            UiZoom.Set(99, remember: false);
            Assert.Equal(UiZoom.Max, UiZoom.Scale);
            UiZoom.Set(0, remember: false);
            Assert.Equal(UiZoom.Min, UiZoom.Scale);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void Small_wheel_deltas_add_up_to_one_step_per_notch()
    {
        TestServices.EnsureInitialized("en");
        var window = Tracked();
        try
        {
            var command = Command(window);
            // A touchpad (or pinching on one): ten small deltas are one notch, not ten.
            for (var i = 0; i < 9; i++)
                window.MouseWheel(new Point(50, 50), new Vector(0, 0.1), command);
            Assert.Equal(1, UiZoom.Scale, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 0.1), command);
            Assert.Equal(1.1, UiZoom.Scale, 6);

            // Turning back starts the count again: what was left over one way doesn't take a step the other way.
            window.MouseWheel(new Point(50, 50), new Vector(0, 0.6), command);
            window.MouseWheel(new Point(50, 50), new Vector(0, -0.6), command);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, -0.5), command);
            Assert.Equal(1, UiZoom.Scale, 6);

            // Three thirds make a notch; a fast wheel takes two at once.
            for (var i = 0; i < 3; i++)
                window.MouseWheel(new Point(50, 50), new Vector(0, -1.0 / 3), command);
            Assert.Equal(0.9, UiZoom.Scale, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 2), command);
            Assert.Equal(1.1, UiZoom.Scale, 6);

            // The wheel alone, or with Shift as well, still scrolls.
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), RawInputModifiers.None);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), command | RawInputModifiers.Shift);
            Assert.Equal(1.1, UiZoom.Scale, 6);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void Shift_is_allowed_with_the_keys_but_not_Alt_and_a_control_using_the_keys_gets_them_first()
    {
        TestServices.EnsureInitialized("en");
        var recorder = new Button { Content = "Record" };
        var window = Tracked(recorder);
        try
        {
            var command = Command(window);
            // Ctrl + Shift + = is "Ctrl +" on a US keyboard, and the only way to type + on some layouts.
            window.KeyPress(Key.OemPlus, command | RawInputModifiers.Shift, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            window.KeyPress(Key.Add, command, PhysicalKey.None, null);
            Assert.Equal(1.2, UiZoom.Scale, 6);
            window.KeyPress(Key.Subtract, command, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);

            // AltGr (Ctrl + Alt on Windows) + 0 types "}" on a German keyboard: not a reset.
            window.KeyPress(Key.D0, command | RawInputModifiers.Alt, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            // Without the command key, or with the Windows / Super key instead or as well, nothing happens.
            var other = command == RawInputModifiers.Meta ? RawInputModifiers.Control : RawInputModifiers.Meta;
            window.KeyPress(Key.D0, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyPress(Key.D0, other, PhysicalKey.None, null);
            window.KeyPress(Key.D0, command | other, PhysicalKey.None, null);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), other);
            Assert.Equal(1.1, UiZoom.Scale, 6);

            // A control that takes the combination itself, as the shortcut recorder on the Triggers page does, wins.
            recorder.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
            recorder.Focus();
            window.KeyPress(Key.D0, command, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void The_size_is_written_once_after_a_moment_without_applying_the_other_settings()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-zoom-").FullName;
        var window = Tracked();
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            var changed = 0;
            store.SettingsChanged += (_, _) => changed++;
            var writes = new List<double>();
            UiZoom.Configure(1, scale =>
            {
                writes.Add(scale);
                store.SaveUiScale(scale);
            });
            var command = Command(window);
            for (var i = 0; i < 3; i++)
                window.MouseWheel(new Point(50, 50), new Vector(0, 1), command);
            Assert.Equal(1.3, UiZoom.Scale, 6);
            Assert.Empty(writes);
            Assert.True(UiZoom.SavePending);

            UiZoom.Flush(); // as when the moment has passed, or Filee exits
            Assert.Equal(1.3, Assert.Single(writes), 6);
            Assert.False(UiZoom.SavePending);
            Assert.Equal(0, changed);
            var saved = new UserDataStore(directory);
            saved.Load();
            Assert.Equal(1.3, saved.Settings.UiScale, 6);

            // Taking the size from the settings (at start) doesn't write it back.
            UiZoom.Configure(1.3, writes.Add);
            Assert.False(UiZoom.SavePending);
            Assert.Single(writes);
        }
        finally
        {
            Close(window);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Settings_from_before_the_zoom_read_as_100_percent()
    {
        var directory = Directory.CreateTempSubdirectory("filee-zoom-old-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"SchemaVersion\": 38}");
            var store = new UserDataStore(directory);
            store.Load();
            Assert.Equal(1, store.Settings.UiScale);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(1.5)]
    [InlineData(1.8)]
    public void Engine_setup_grows_with_the_zoom_and_nothing_is_cut_off(double scale)
    {
        TestServices.EnsureInitialized("en");
        UiZoom.Configure(1, save: null);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        try
        {
            window.Show();
            RenderTests.Pump();
            var designed = window.Width;
            UiZoom.Set(scale, remember: false);
            RenderTests.Pump();
            Assert.Equal(designed * scale, window.Width, 3);
            // The list and the buttons end inside the window, as at 100 %.
            foreach (var name in new[] { "CloseButton", "InstallButton" })
            {
                var button = window.FindControl<Button>(name)!;
                var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                Assert.InRange(corner.X, 0, window.ClientSize.Width + 0.5);
                Assert.InRange(corner.Y, 0, window.ClientSize.Height + 0.5);
            }
            var scroller = window.FindControl<ScrollViewer>("PackageScroller")!;
            var right = scroller.TranslatePoint(new Point(scroller.Bounds.Width, 0), window)!.Value.X;
            Assert.InRange(right, 0, window.ClientSize.Width + 0.5);
            RenderTests.Save(window, $"engine-setup-zoom-{scale * 100:0}.png");
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void A_window_that_cannot_be_resized_grows_and_scrolls_instead_of_cutting_off()
    {
        TestServices.EnsureInitialized("ko");
        UiZoom.Configure(1, save: null);
        using var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(),
            AppHost.Get<ILocalizer>(), AppHost.Get<EngineDownloadService>());
        var window = new LibreOfficeOptionsWindow { DataContext = vm };
        try
        {
            window.Show();
            RenderTests.Pump();
            var scroller = Assert.IsType<ScrollViewer>(window.Content);
            Assert.IsType<LayoutTransformControl>(scroller.Content);
            UiZoom.Set(1.5, remember: false);
            RenderTests.Pump();
            Assert.Equal(520 * 1.5, window.Width, 3);
            RenderTests.Save(window, "window-libreoffice-options-zoom-150-ko.png");
            UiZoom.Set(1, remember: false);
            RenderTests.Pump();
            Assert.Equal(520, window.Width, 3);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void Theme_page_shows_the_window_size_and_resets_it(string language)
    {
        TestServices.EnsureInitialized(language);
        var directory = Directory.CreateTempSubdirectory("filee-zoom-theme-").FullName;
        var store = new UserDataStore(directory);
        store.Load();
        UiZoom.Configure(1, save: null);
        var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());
        var page = new ThemePage { DataContext = vm };
        var window = new Window { Content = page, Width = 940, Height = 1100 };
        try
        {
            window.Show();
            RenderTests.Pump();
            var text = page.FindControl<TextBlock>("WindowSizeText")!;
            var zoomIn = page.FindControl<Button>("ZoomInButton")!;
            var reset = page.FindControl<Button>("ResetZoomButton")!;
            Assert.Equal("100%", text.Text);
            Assert.False(reset.IsEffectivelyEnabled);
            Assert.Contains(OperatingSystem.IsMacOS() ? "Cmd + 0" : "Ctrl + 0", vm.WindowSizeHint);

            zoomIn.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Equal(1.1, UiZoom.Scale, 6);
            Assert.Equal("110%", text.Text);
            Assert.True(reset.IsEffectivelyEnabled);

            // Zoomed elsewhere (Ctrl + wheel in another window), the page follows.
            UiZoom.Set(UiZoom.Max, remember: false);
            RenderTests.Pump();
            Assert.Equal("180%", text.Text);
            Assert.False(zoomIn.IsEffectivelyEnabled);
            RenderTests.Save(window, $"page-theme-window-size-{language}.png");

            reset.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Equal(1, UiZoom.Scale, 6);
            Assert.Equal("100%", text.Text);
            // Nothing is written through the theme settings: the zoom saves itself.
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")));

            // A page left behind stops listening.
            vm.Dispose();
            var notified = 0;
            vm.PropertyChanged += (_, _) => notified++;
            UiZoom.Set(1.2, remember: false);
            Assert.Equal(0, notified);
        }
        finally
        {
            Close(window);
            Directory.Delete(directory, true);
        }
    }
}
