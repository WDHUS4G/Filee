// Dialog showing download progress and status while an in-app update is downloading or being installed.

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.Views;

/// <summary>
/// Dialog box showing the progress bar and status while downloading an update.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private readonly UpdateService? _updates;
    private readonly ILocalizer? _loc;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
    }

    public UpdateProgressWindow(UpdateService updates, ILocalizer loc) : this()
    {
        _updates = updates;
        _loc = loc;
        Title = loc["app.name"];
        TitleText.Text = loc["about.updates"];
        VersionText.Text = updates.LatestVersion is { } ver ? loc.Format("general.update_available", ver) : "";

        updates.PropertyChanged += OnUpdatesChanged;
        Closed += (_, _) => updates.PropertyChanged -= OnUpdatesChanged;
        UpdateState();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (UpdateTexts.Affects(e.PropertyName))
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateState();
            else
                Dispatcher.UIThread.Post(UpdateState);
        }
    }

    private void UpdateState()
    {
        if (_updates is null || _loc is null)
            return;

        if (_updates.LatestVersion is { } ver)
            VersionText.Text = _loc.Format("general.update_available", ver);

        switch (_updates.Step)
        {
            case UpdateStep.Downloading:
                ProgressBar.IsVisible = true;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = _updates.DownloadPercent;
                PercentText.Text = $"{_updates.DownloadPercent}%";
                var text = _loc["update.downloading"];
                var ellipsis = text.IndexOf('…');
                StatusText.Text = ellipsis >= 0 ? text[..(ellipsis + 1)] : text;
                break;

            case UpdateStep.Installing:
                ProgressBar.IsVisible = true;
                ProgressBar.IsIndeterminate = true;
                PercentText.Text = "";
                StatusText.Text = _loc["update.installing"];
                break;

            case UpdateStep.Cancelled:
                ProgressBar.IsVisible = false;
                PercentText.Text = "";
                StatusText.Text = _loc["update.cancelled"];
                break;

            case UpdateStep.Failed:
                ProgressBar.IsVisible = false;
                PercentText.Text = "";
                StatusText.Text = _loc["update.failed"];
                break;

            case UpdateStep.Idle:
                ProgressBar.IsVisible = false;
                PercentText.Text = "";
                StatusText.Text = "";
                Close();
                break;
        }
    }
}
