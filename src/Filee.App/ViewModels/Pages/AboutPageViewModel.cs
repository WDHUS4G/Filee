// About: version, license, third-party notices.

using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class AboutPageViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly UpdateService _updates;

    public AboutPageViewModel(ILocalizer loc, UserDataStore store, UpdateService updates)
    {
        _loc = loc;
        _store = store;
        _updates = updates;
        Version = loc.Format("about.version", UpdateService.CurrentVersion);
        _checkForUpdates = store.Settings.CheckForUpdates;
        ShowUpdateState(updates.LatestVersion);
        updates.PropertyChanged += OnUpdatesChanged;
    }

    public string Version { get; }
    public string RepositoryUrl { get; } = UpdateService.RepositoryUrl;

    /// <summary>The page is rebuilt on every visit; the update service lives on.</summary>
    public void Dispose() => _updates.PropertyChanged -= OnUpdatesChanged;

    // ── Updates: the automatic check and "Check now" ──

    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>"Update" in an installed copy, "Download" in a portable one.</summary>
    public string UpdateButtonText => _loc[UpdateTexts.ButtonKey];

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _store.Settings.CheckForUpdates = value;
        _store.SaveSettings();
    }

    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (UpdateTexts.Affects(e.PropertyName))
            ShowUpdateState(_updates.LatestVersion);
    }

    [RelayCommand]
    private async Task CheckNow() => ShowUpdateState(await _updates.CheckAsync(), afterCheck: true);

    /// <summary>Downloads and runs the new installer, which updates Filee in place (a portable copy opens the download page).</summary>
    [RelayCommand]
    private Task DownloadUpdate() => _updates.UpdateAsync();

    private void ShowUpdateState(string? newer, bool afterCheck = false)
    {
        UpdateAvailable = newer is not null && !_updates.IsBusy;
        UpdateStatus = UpdateTexts.Status(_updates, _loc) is { } progress ? progress
            : newer is not null ? _loc.Format("general.update_available", newer)
            : afterCheck ? _loc.Format("general.update_none", UpdateService.CurrentVersion)
            : "";
    }

    public string Notices { get; } = ReadNotices();

    private static string ReadNotices()
    {
        var uri = new Uri("avares://Filee/Assets/THIRD-PARTY-NOTICES.md");
        if (!AssetLoader.Exists(uri))
            return "";
        using var reader = new StreamReader(AssetLoader.Open(uri));
        return reader.ReadToEnd();
    }
}
