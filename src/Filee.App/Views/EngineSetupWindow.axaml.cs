using Avalonia;
using Avalonia.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class EngineSetupWindow : Window
{
    private const double ScreenMargin = 12;
    private PixelRect? _area;
    private double _scaling = 1;

    public EngineSetupWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        // Keeps itself inside the screen (FitTo), its engine list scrolls.
        UiZoom.Track(this, fitsItself: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is EngineSetupViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
        Opened += (_, _) => FitToScreen();
        // Progress bars make the cards taller while engines install.
        SizeChanged += (_, e) => KeepInside(e.NewSize.Height);
    }

    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
            FitTo(screen.WorkingArea, screen.Scaling);
    }

    /// <summary>
    /// Keeps the window inside <paramref name="area"/> (pixels): the window grows with the engine list, so on a small or
    /// scaled screen it would run past the bottom with its buttons (#370). Capped, the list scrolls instead; a window
    /// that fits keeps its size and place.
    /// </summary>
    internal void FitTo(PixelRect area, double scaling)
    {
        _area = area;
        _scaling = scaling;
        MaxHeight = Math.Max(MinHeight, area.Height / scaling - FrameHeight() - 2 * ScreenMargin);
        KeepInside(Math.Min(ClientSize.Height, MaxHeight));
    }

    /// <summary>Moves the window back on screen when its bottom or top would be off it, else leaves it where it is.</summary>
    private void KeepInside(double clientHeight)
    {
        if (_area is not { } area)
            return;
        var height = (int)Math.Ceiling((clientHeight + FrameHeight()) * _scaling);
        if (Position.Y >= area.Y && Position.Y + height <= area.Bottom)
            return;
        var margin = (int)Math.Round(ScreenMargin * _scaling);
        var y = Math.Max(area.Y + margin, Math.Min(Position.Y, area.Bottom - margin - height));
        Position = new PixelPoint(Position.X, y);
    }

    /// <summary>The title bar and borders, which MaxHeight (the client area) leaves out.</summary>
    private double FrameHeight() => FrameSize is { } size ? Math.Max(0, size.Height - ClientSize.Height) : 0;
}
