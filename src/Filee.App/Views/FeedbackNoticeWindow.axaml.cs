using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Bottom-right card asking once for a GitHub star or feedback. Both buttons open the browser.</summary>
public partial class FeedbackNoticeWindow : Window
{
    private const int ScreenMargin = 12;

    public FeedbackNoticeWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    private async void OnStar(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri(UpdateService.RepositoryUrl));
        Close();
    }

    private async void OnIssue(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri(UpdateService.NewIssueUrl));
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        var height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin);
    }
}
