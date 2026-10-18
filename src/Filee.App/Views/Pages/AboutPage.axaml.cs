using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class AboutPage : UserControl
{
    public AboutPage() => InitializeComponent();

    private async void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutPageViewModel vm && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(vm.RepositoryUrl));
    }

    // A star is given on the repository page itself.
    private void OnStar(object? sender, RoutedEventArgs e) => OnOpenRepository(sender, e);

    private async void OnSponsor(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await SponsorWindow.Create(AppHost.Get<ILocalizer>()).ShowDialog(owner);
    }

    private async void OnIssue(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(Services.UpdateService.NewIssueUrl));
    }
}
