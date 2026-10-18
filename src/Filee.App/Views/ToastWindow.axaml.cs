using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Views;

/// <summary>Bottom-right progress stack. Visible exactly while ConversionService.ToastJobs is non-empty.</summary>
public partial class ToastWindow : Window
{
    private const int ScreenMargin = 12;

    public ToastWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    private ConversionService? _service;
    private bool _closed;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_service is not null)
            _service.ToastJobs.CollectionChanged -= OnJobsChanged;
        _service = DataContext as ConversionService;
        if (_service is not null)
            _service.ToastJobs.CollectionChanged += OnJobsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_service is not null)
            _service.ToastJobs.CollectionChanged -= OnJobsChanged;
        base.OnClosed(e);
    }

    // After the change: showing the window inside CollectionChanged built the list with the new job already in it, and
    // the list then added it again, so one job had two cards (issue #60).
    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateVisibility);

    private void UpdateVisibility()
    {
        if (_closed || _service is not { } service)
            return;
        if (service.ToastJobs.Count > 0 && !IsVisible)
        {
            Show();
            PlaceBottomRight();
        }
        else if (service.ToastJobs.Count == 0 && IsVisible)
        {
            Hide();
        }
    }

    private void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        Position = new PixelPoint(area.Right - width - ScreenMargin, area.Bottom - height - ScreenMargin);
    }
}
