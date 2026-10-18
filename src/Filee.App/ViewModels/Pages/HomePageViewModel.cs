// Home: drop zone (handled in the view) and recent conversions.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryEntry _entry;
    private readonly IPlatformServices _platform;
    private readonly ConversionService? _conversions;

    public HistoryItemViewModel(HistoryEntry entry, ILocalizer loc, IPlatformServices platform, ConversionService? conversions = null)
    {
        _entry = entry;
        _platform = platform;
        _conversions = conversions;
        Title = entry.PresetName;
        Subtitle = entry.Sources.Count == 1 ? Path.GetFileName(entry.Sources[0]) : loc.Format("home.files", entry.Sources.Count);
        Time = entry.FinishedAt.Date == DateTime.Today ? entry.FinishedAt.ToString("t") : entry.FinishedAt.ToString("d");
        Failures = entry.Failures.Count > 0
            ? entry.Failures.Select(f => new FailureItem(Path.GetFileName(f.Source), FailureText.Reason(loc, f.ErrorKey, f.ErrorDetail))).ToList()
            : entry.Errors.Select(line => FailureText.FromLegacy(loc, line)).ToList();
        HasErrors = Failures.Count > 0 || entry.State == JobState.Failed;
        FailureSummary = loc.Format("home.failed_files", Failures.Count);
        HasOutputs = entry.Outputs.Any(ConversionService.Exists);
        // Only entries that remember their preset (Filee 1.8 and later) and still have a failed file to convert.
        CanRetry = conversions is not null && entry.Preset is not null && entry.Failures.Any(f => ConversionService.Exists(f.Source));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Time { get; }
    public bool HasErrors { get; }
    public bool HasOutputs { get; }

    /// <summary>The files that failed and why.</summary>
    public IReadOnlyList<FailureItem> Failures { get; }

    public bool HasFailures => Failures.Count > 0;

    /// <summary>"2 files failed": the button that shows the list.</summary>
    public string FailureSummary { get; }

    public bool CanRetry { get; }

    [ObservableProperty] private bool _showFailures;

    [RelayCommand]
    private void ToggleFailures() => ShowFailures = !ShowFailures;

    /// <summary>Converts the failed files again the way the job ran them; progress shows in the toast.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_conversions is not null && _entry.Preset is not null)
            _conversions.Retry(_entry.Failures.Select(f => f.Source), _entry.Preset, _entry.WatchRuleId);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        // An output may be a folder ("Extract"): revealing it opens the extracted files.
        var output = _entry.Outputs.FirstOrDefault(ConversionService.Exists);
        if (output is not null)
            _platform.RevealInFileManager(output);
    }
}

public sealed partial class HomePageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly Services.Triggers.TriggerService? _triggers;
    private readonly ConversionService? _conversions;

    public HomePageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform, Services.Triggers.TriggerService? triggers = null,
        ConversionService? conversions = null)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _triggers = triggers;
        _conversions = conversions;
        if (triggers is not null)
            triggers.StatusChanged += OnTriggerStatusChanged;
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        DropHint = loc.Format(OperatingSystem.IsMacOS() ? "home.drop_hint_macos" : OperatingSystem.IsLinux() ? "home.drop_hint_linux" : "home.drop_hint", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
        store.HistoryChanged += OnHistoryChanged;
        Reload();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload);

    public void Dispose()
    {
        _store.HistoryChanged -= OnHistoryChanged;
        if (_triggers is not null)
            _triggers.StatusChanged -= OnTriggerStatusChanged;
    }

    private void OnTriggerStatusChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(NeedsAccessibility));

    /// <summary>macOS: gestures wait for the Accessibility permission (the reminder above the drop zone).</summary>
    public bool NeedsAccessibility => _triggers?.NeedsPermission == true;

    [RelayCommand]
    private void OpenAccessibility()
    {
        if (OperatingSystem.IsMacOS() && _platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            mac.OpenAccessibilitySettings();
    }

    public string DropHint { get; }

    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    private void Reload()
    {
        History.Clear();
        foreach (var entry in _store.History.Take(40))
            History.Add(new HistoryItemViewModel(entry, _loc, _platform, _conversions));
        IsEmpty = History.Count == 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.ClearHistory();
        Reload();
    }
}
