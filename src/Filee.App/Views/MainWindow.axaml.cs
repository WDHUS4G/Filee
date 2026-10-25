using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Views;

public partial class MainWindow : Window
{
    private bool _asking;
    private bool _releasePosted;
    private bool _releasing;

    public MainWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }

    private async void OnSponsor(object? sender, RoutedEventArgs e) =>
        await SponsorWindow.Create(AppHost.Get<ILocalizer>()).ShowDialog(this);

    /// <summary>
    /// Closing the window keeps Filee running in the tray, quits, or asks which (<see cref="AppSettings.CloseAction"/>).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is not WindowCloseReason.WindowClosing || _releasing)
            return;
        e.Cancel = true;
        // Close() from code goes to the tray, as before; the setting is for the title bar's close button and Alt+F4.
        if (e.IsProgrammatic)
            HideToTray();
        else
            CloseAsTheUserChose();
    }

    /// <summary>What the user's close does: hide, quit or ask (<see cref="AppSettings.CloseAction"/>).</summary>
    internal void CloseAsTheUserChose()
    {
        var store = AppHost.Get<UserDataStore>();
        switch (store.Settings.CloseAction)
        {
            case CloseAction.Tray:
                HideToTray();
                break;
            case CloseAction.Quit:
                Quit();
                break;
            default:
                _ = AskAsync(store);
                break;
        }
    }

    private async Task AskAsync(UserDataStore store)
    {
        if (_asking)
            return;
        _asking = true;
        try
        {
            var dialog = new CloseAskWindow();
            if (await dialog.ShowDialog<CloseAction?>(this) is not { } action)
                return;
            if (dialog.DontAskAgain)
            {
                store.Settings.CloseAction = action;
                store.SaveSettings();
            }
            if (action == CloseAction.Quit)
                Quit();
            else
                HideToTray();
        }
        finally
        {
            _asking = false;
        }
    }

    // Posted: shutting down closes this window again, which must not happen inside its own Closing handler.
    private static void Quit() => Dispatcher.UIThread.Post(() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    /// <summary>
    /// Leaves Filee in the tray: hides the window at once, then closes it for real, so its pages, visuals and images
    /// don't stay in memory until the next time (issue #119). <see cref="WindowService.ShowMain"/> opens a new one.
    /// </summary>
    internal void HideToTray()
    {
        Hide();
        if (_releasePosted)
            return;
        _releasePosted = true;
        // Posted: a window can't close inside its own Closing handler.
        Dispatcher.UIThread.Post(() =>
        {
            _releasePosted = false;
            if (IsVisible)
                return; // shown again meanwhile
            _releasing = true;
            Close();
            // Still open (the close was cancelled): the next close goes to the tray again, not for good.
            if (PlatformImpl is not null)
                _releasing = false;
        });
    }

    /// <summary>
    /// Being closed for good after going to the tray, or closed: it can't be shown again, so
    /// <see cref="WindowService.ShowMain"/> opens a new one instead.
    /// </summary>
    internal bool IsReleased => _releasing || PlatformImpl is null;
}
