using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>
/// Bottom-right notice that engines the user downloaded have a newer version. "Update" queues their downloads and
/// opens Settings → Engines, where the progress shows.
/// </summary>
public partial class EngineUpdateNoticeWindow : Window
{
    private const int ScreenMargin = 12;
    private readonly Action? _update;

    public EngineUpdateNoticeWindow() => InitializeComponent();

    /// <param name="caption">The engines and their new versions, e.g. "FFmpeg 8.0 · LibreOffice 26.8".</param>
    /// <param name="update">Starts the updates.</param>
    public EngineUpdateNoticeWindow(string caption, Action update) : this()
    {
        _update = update;
        CaptionText.Text = caption;
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    /// <summary>Extra distance from the bottom of the screen, e.g. to stay above another card.</summary>
    public int BottomOffset { get; set; }

    private void OnUpdate(object? sender, RoutedEventArgs e)
    {
        _update?.Invoke();
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
        Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin - BottomOffset);
    }
}
