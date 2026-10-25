// Closing the main window: keep Filee in the tray, quit, or ask once with "Don't ask again" (Settings → General).

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class CloseActionTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static MainWindow ShowMain()
    {
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("general");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        return window;
    }

    private static void Click(Window window, string resourceKey)
    {
        var text = (string)window.FindResource(resourceKey)!;
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void Keep_in_tray_hides_the_window_without_asking()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Tray;
        var window = ShowMain();
        try
        {
            window.CloseAsTheUserChose();
            Pump();

            Assert.False(window.IsVisible);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Ask_with_dont_ask_again_saves_the_answer_and_the_general_page_follows()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Ask;
        var window = ShowMain();
        try
        {
            var page = (GeneralPageViewModel)((MainWindowViewModel)window.DataContext!).CurrentPage;
            Assert.Equal(CloseAction.Ask, page.CloseChoice.Value);

            window.CloseAsTheUserChose();
            Pump();
            var dialog = Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(dialog, "close.tray");

            Assert.False(dialog.IsVisible);
            Assert.False(window.IsVisible);
            Assert.Equal(CloseAction.Tray, store.Settings.CloseAction);
            Assert.Equal(CloseAction.Tray, page.CloseChoice.Value);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Cancel_keeps_the_window_open_and_asks_again_next_time()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        store.Settings.CloseAction = CloseAction.Ask;
        var window = ShowMain();
        try
        {
            window.CloseAsTheUserChose();
            Pump();
            var dialog = Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click(dialog, "common.cancel");

            Assert.True(window.IsVisible);
            Assert.Equal(CloseAction.Ask, store.Settings.CloseAction);

            // Answered without "Don't ask again": hidden this time, asked again the next.
            window.CloseAsTheUserChose();
            Pump();
            Click(Assert.IsType<CloseAskWindow>(Assert.Single(window.OwnedWindows)), "close.tray");
            Assert.False(window.IsVisible);
            Assert.Equal(CloseAction.Ask, store.Settings.CloseAction);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_general_page_choice_is_saved()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        using var page = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(),
            NullLogger<GeneralPageViewModel>.Instance);
        try
        {
            Assert.Equal(["Ask every time", "Keep Filee running in the tray", "Quit Filee"], page.CloseChoices.Select(c => c.Label));
            page.CloseChoice = page.CloseChoices.Single(c => c.Value == CloseAction.Quit);
            Assert.Equal(CloseAction.Quit, store.Settings.CloseAction);
        }
        finally
        {
            store.Settings.CloseAction = CloseAction.Ask;
        }
    }

    [AvaloniaFact]
    public void The_general_page_saves_each_icon_preference_without_changing_close_action()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-icons-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.CloseAction = CloseAction.Quit;
            store.SaveSettings();
            var platform = new IconPlatform { SupportsDockVisibility = true };
            using var page = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), platform,
                NullLogger<GeneralPageViewModel>.Instance);
            Assert.True(page.ShowInDock);
            Assert.True(page.ShowMenuBarIcon);
            Assert.Empty(platform.DockCalls);

            var saves = 0;
            store.SettingsChanged += (_, _) => saves++;
            page.ShowInDock = false;
            Assert.Equal([false], platform.DockCalls);
            Assert.False(store.Settings.ShowInDock);
            Assert.True(store.Settings.ShowMenuBarIcon);
            Assert.Equal(1, saves);

            page.ShowMenuBarIcon = false;
            Assert.Equal([false], platform.DockCalls);
            Assert.False(store.Settings.ShowMenuBarIcon);
            Assert.Equal(2, saves);

            page.ShowInDock = true;
            Assert.Equal([false, true], platform.DockCalls);
            Assert.True(store.Settings.ShowInDock);
            Assert.False(store.Settings.ShowMenuBarIcon);
            Assert.Equal(3, saves);
            var reloaded = new UserDataStore(directory);
            reloaded.Load();
            Assert.True(reloaded.Settings.ShowInDock);
            Assert.False(reloaded.Settings.ShowMenuBarIcon);
            Assert.Equal(CloseAction.Quit, reloaded.Settings.CloseAction);

            using var unsupported = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(),
                new IconPlatform(), NullLogger<GeneralPageViewModel>.Instance);
            Assert.False(unsupported.SupportsDockVisibility);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Rejected_dock_policy_keeps_preference_and_switch_in_sync()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-icons-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            var platform = new IconPlatform { SupportsDockVisibility = true, AcceptDockChange = false };
            var log = new IconLogger();
            using var page = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), platform, log);
            var localizer = AppHost.Get<ILocalizer>();
            Assert.Empty(platform.DockCalls);
            var saves = 0;
            store.SettingsChanged += (_, _) => saves++;

            page.ShowInDock = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(page.ShowInDock);
            Assert.True(store.Settings.ShowInDock);
            Assert.Equal([false], platform.DockCalls);
            Assert.Equal(0, saves);
            Assert.Equal(localizer["general.dock_failed"], page.DockStatus);
            Assert.Equal(LogLevel.Warning, Assert.Single(log.Levels));
            Assert.Null(Assert.Single(log.Exceptions));

            platform.DockError = new InvalidOperationException("policy denied");
            page.ShowInDock = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(page.ShowInDock);
            Assert.True(store.Settings.ShowInDock);
            Assert.Equal([false, false], platform.DockCalls);
            Assert.Equal(0, saves);
            Assert.Equal(localizer["general.dock_failed"], page.DockStatus);
            Assert.DoesNotContain("policy denied", page.DockStatus);
            Assert.Equal([LogLevel.Warning, LogLevel.Warning], log.Levels);
            Assert.NotNull(log.Exceptions[1]);
            Assert.IsType<InvalidOperationException>(log.Exceptions[1]);
            Assert.Equal("policy denied", log.Exceptions[1]!.Message);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Toggle_switch_reverts_on_rejected_dock_change_with_real_view()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-icons-real-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.ShowInDock = true;
            store.SaveSettings();
            var platform = new IconPlatform { SupportsDockVisibility = true, AcceptDockChange = false };
            using var vm = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), platform,
                NullLogger<GeneralPageViewModel>.Instance);
            var page = new GeneralPage { DataContext = vm };
            var window = new Window { Content = page, Width = 800, Height = 820 };
            try
            {
                window.Show();
                Pump();

                var switchControl = page.FindControl<ToggleSwitch>("ShowInDockSwitch")!;
                Assert.True(switchControl.IsChecked);
                Assert.True(vm.ShowInDock);
                Assert.True(store.Settings.ShowInDock);

                switchControl.IsChecked = false;
                Dispatcher.UIThread.RunJobs();

                Assert.True(switchControl.IsChecked, "Switch should revert to true after rejection");
                Assert.True(vm.ShowInDock, "ViewModel ShowInDock should remain true");
                Assert.True(store.Settings.ShowInDock, "Store settings should remain true");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

internal sealed class IconPlatform : IPlatformServices
{
    public bool SupportsDockVisibility { get; set; }
    public bool AcceptDockChange { get; set; } = true;
    public Exception? DockError { get; set; }
    public List<bool> DockCalls { get; } = [];
    public bool TrySetShowInDock(bool show)
    {
        DockCalls.Add(show);
        if (DockError is not null)
            throw DockError;
        return AcceptDockChange;
    }
    public bool IsFileManagerAt(int x, int y) => false;
    public string? ProcessNameAt(int x, int y) => null;
    public IReadOnlyList<string> GetFileManagerSelection() => [];
    public int SystemDragThreshold => 4;
    public bool PrefersReducedMotion => false;
    public void SetStartWithSystem(bool enabled, string executablePath) { }
    public void SetContextMenu(bool enabled, string executablePath, string label) { }
    public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
    public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
        Task.FromResult(new ModernContextMenuResult(false));
    public void RevealInFileManager(string path) { }
    public void OpenFile(string path) { }
    public void RaiseTopmost(nint windowHandle) { }
}

internal sealed class IconLogger : ILogger<GeneralPageViewModel>
{
    public List<LogLevel> Levels { get; } = [];
    public List<Exception?> Exceptions { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
        Func<TState, Exception?, string> formatter)
    {
        Levels.Add(level);
        Exceptions.Add(error);
    }
}
