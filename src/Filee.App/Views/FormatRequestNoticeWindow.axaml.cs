using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Bottom-right card for files of a type Filee doesn't know: "Request this format" opens the GitHub form.</summary>
public partial class FormatRequestNoticeWindow : Window
{
    private const int ScreenMargin = 12;

    public FormatRequestNoticeWindow() => InitializeComponent();

    /// <param name="extensions">The unknown extensions, e.g. ".ncm" (see <see cref="FormatRequest.UnknownExtensions"/>).</param>
    public FormatRequestNoticeWindow(IReadOnlyList<string> extensions, string title) : this()
    {
        Extensions = extensions;
        TitleText.Text = title;
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    public IReadOnlyList<string> Extensions { get; } = [];

    private async void OnRequest(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(FormatRequest.Url(Extensions));
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
