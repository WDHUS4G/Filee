// Bottom-right card saying Filee runs as administrator, with the restart that fixes it (ElevationService).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>One-time notice for an elevated Filee. "Restart" starts it again through Explorer and quits this one.</summary>
public partial class ElevationNoticeWindow : Window
{
    private const int ScreenMargin = 12;
    private readonly ElevationService? _elevation;

    /// <summary>For the XAML previewer.</summary>
    public ElevationNoticeWindow() : this(null)
    {
    }

    public ElevationNoticeWindow(ElevationService? elevation)
    {
        _elevation = elevation;
        InitializeComponent();
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (_elevation is null)
            return;
        if (_elevation.RestartWithoutAdministratorRights())
            Close();
        else
            FailedText.IsVisible = true;
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
