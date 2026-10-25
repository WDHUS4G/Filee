// General settings: language, start-up, Explorer integration, preset library import/export. Updates are on the About page.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels.Pages;

public sealed partial class GeneralPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly ILogger<GeneralPageViewModel> _log;
    private readonly SystemIntegrationService? _integration;

    public GeneralPageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform,
        ILogger<GeneralPageViewModel> log, SystemIntegrationService? integration = null)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _log = log;
        _integration = integration;

        Languages =
        [
            new Choice<string>("auto", loc["general.language_auto"]),
            .. LocalizationService.Languages.Select(l => new Choice<string>(l.Code, l.NativeName)),
        ];
        _language = Languages.FirstOrDefault(l => l.Value == store.Settings.Language) ?? Languages[0];
        _startWithSystem = store.Settings.StartWithSystem;
        _showInDock = store.Settings.ShowInDock;
        _showMenuBarIcon = store.Settings.ShowMenuBarIcon;
        _contextMenu = store.Settings.ContextMenuEnabled;
        _noHistory = !store.Settings.KeepHistory;
        _historyLimit = store.Settings.EffectiveHistoryLimit;
        OutputLocations = new[] { OutputLocation.SameFolder, OutputLocation.Subfolder, OutputLocation.CustomFolder }
            .Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        var output = store.Settings.DefaultOutput;
        _defaultLocation = OutputLocations.FirstOrDefault(l => l.Value == output.Location) ?? OutputLocations[0];
        _defaultSubfolder = output.SubfolderName;
        _defaultFolder = output.CustomFolder;
        _keepFileDates = store.Settings.KeepFileDates;
        _browserFolder = store.Settings.BrowserDrops.Folder;
        _saveOnlyConverted = store.Settings.BrowserDrops.SaveOnlyConverted;
        _zipGeoResults = store.Settings.ZipGeoResults;
        CloseChoices = Enum.GetValues<CloseAction>().Select(v => new Choice<CloseAction>(v, loc[$"general.close.{v}"])).ToList();
        _closeChoice = CloseChoices.First(c => c.Value == store.Settings.CloseAction);
        store.SettingsChanged += OnSettingsChanged;
        _historyCount = store.History.Count;
        RefreshModernMenu();
    }

    public IReadOnlyList<Choice<string>> Languages { get; }
    public string DataFolder => _store.Directory;
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool HasSystemIntegration => _platform is not NullPlatformServices;
    /// <summary>Whether this platform supports changing Filee's Dock visibility.</summary>
    public bool SupportsDockVisibility => _platform.SupportsDockVisibility;
    public string ContextMenuText => _loc[OperatingSystem.IsMacOS() ? "general.context_menu_macos"
        : OperatingSystem.IsLinux() ? "general.context_menu_linux" : "general.context_menu"];
    public string? IntegrationMessage => _integration?.Error ?? (OperatingSystem.IsMacOS()
        ? _loc["general.integration_macos"] : OperatingSystem.IsLinux() ? _loc["general.integration_linux"] : null);

    /// <summary>Choices for the default save location (everything but "Default" itself).</summary>
    public IReadOnlyList<Choice<OutputLocation>> OutputLocations { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDefaultSubfolder), nameof(ShowDefaultFolder))]
    private Choice<OutputLocation> _defaultLocation;

    [ObservableProperty] private string _defaultSubfolder;
    [ObservableProperty] private string _defaultFolder;

    public bool ShowDefaultSubfolder => DefaultLocation.Value == OutputLocation.Subfolder;
    public bool ShowDefaultFolder => DefaultLocation.Value == OutputLocation.CustomFolder;

    partial void OnDefaultLocationChanged(Choice<OutputLocation>? oldValue, Choice<OutputLocation> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => DefaultLocation = v))
            Save(s => s.DefaultOutput.Location = newValue.Value);
    }

    partial void OnDefaultSubfolderChanged(string value) =>
        Save(s => s.DefaultOutput.SubfolderName = string.IsNullOrWhiteSpace(value) ? "converted" : value.Trim());

    partial void OnDefaultFolderChanged(string value) => Save(s => s.DefaultOutput.CustomFolder = value.Trim());

    [ObservableProperty] private bool _keepFileDates;
    [ObservableProperty] private bool _zipGeoResults;

    /// <summary>Where files dragged from a browser are saved; empty for the Downloads folder.</summary>
    [ObservableProperty] private string _browserFolder;

    [ObservableProperty] private bool _saveOnlyConverted;

    /// <summary>The user's Downloads folder, shown while no other folder is set.</summary>
    public string DownloadsFolder => _platform.DownloadsFolder;

    /// <summary>The folder files from a browser go to now.</summary>
    public string BrowserFolderInUse => string.IsNullOrWhiteSpace(BrowserFolder) ? DownloadsFolder : BrowserFolder.Trim();

    partial void OnBrowserFolderChanged(string value) => Save(s => s.BrowserDrops.Folder = value.Trim());

    partial void OnSaveOnlyConvertedChanged(bool value) => Save(s => s.BrowserDrops.SaveOnlyConverted = value);

    /// <summary>What closing the main window does; its "Don't ask again" also sets it.</summary>
    public IReadOnlyList<Choice<CloseAction>> CloseChoices { get; }

    [ObservableProperty] private Choice<CloseAction> _closeChoice;

    partial void OnCloseChoiceChanged(Choice<CloseAction>? oldValue, Choice<CloseAction> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => CloseChoice = v) && _store.Settings.CloseAction != newValue.Value)
            Save(s => s.CloseAction = newValue.Value);
    }

    // The close dialog may answer "Don't ask again" while this page stays open behind the hidden window.
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (CloseChoice.Value != _store.Settings.CloseAction)
            CloseChoice = CloseChoices.First(c => c.Value == _store.Settings.CloseAction);
    }

    public void Dispose() => _store.SettingsChanged -= OnSettingsChanged;

    partial void OnKeepFileDatesChanged(bool value) => Save(s => s.KeepFileDates = value);

    partial void OnZipGeoResultsChanged(bool value) => Save(s => s.ZipGeoResults = value);

    [ObservableProperty] private Choice<string> _language;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _showInDock;
    [ObservableProperty] private bool _showMenuBarIcon;
    [ObservableProperty] private string? _dockStatus;
    [ObservableProperty] private bool _contextMenu;
    [ObservableProperty] private string? _libraryMessage;

    // Windows 11 top-level Explorer menu entry (see ExplorerMenuRegistration). Only shown when this build ships it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModernMenu), nameof(CanAddModernMenu), nameof(CanRemoveModernMenu))]
    private ModernContextMenuState _modernMenuState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddModernMenu), nameof(CanRemoveModernMenu))]
    private bool _modernMenuBusy;

    [ObservableProperty] private string? _modernMenuStatus;

    public bool ShowModernMenu => ContextMenu && ModernMenuState != ModernContextMenuState.Unsupported;
    public bool CanAddModernMenu => !ModernMenuBusy && ModernMenuState is ModernContextMenuState.Off or ModernContextMenuState.Outdated;
    public bool CanRemoveModernMenu => !ModernMenuBusy && ModernMenuState is ModernContextMenuState.On or ModernContextMenuState.Outdated;

    partial void OnLanguageChanged(Choice<string>? oldValue, Choice<string> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => Language = v))
            Save(s => s.Language = newValue.Value);
    }
    partial void OnStartWithSystemChanged(bool value) => Save(s => s.StartWithSystem = value);

    partial void OnShowInDockChanged(bool value)
    {
        if (value == _store.Settings.ShowInDock)
            return;

        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        Exception? error = null;
        try
        {
            if (_platform.TrySetShowInDock(value))
            {
                DockStatus = null;
                Save(s => s.ShowInDock = value);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex;
        }

        _log.LogWarning(error, "Could not change Dock visibility to {ShowInDock}", value);
        DockStatus = _loc["general.dock_failed"];
        // Put back after the switch has finished writing its value: set from inside that write, the switch keeps
        // showing the refused value.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowInDock = _store.Settings.ShowInDock);
    }

    partial void OnShowMenuBarIconChanged(bool value) => Save(s => s.ShowMenuBarIcon = value);

    partial void OnContextMenuChanged(bool value)
    {
        Save(s => s.ContextMenuEnabled = value);
        OnPropertyChanged(nameof(ShowModernMenu));
    }

    /// <summary>Registers the top-level entry; Windows shows one administrator prompt.</summary>
    [RelayCommand]
    private Task AddModernMenu() => ChangeModernMenu(true);

    [RelayCommand]
    private Task RemoveModernMenu() => ChangeModernMenu(false);

    private async Task ChangeModernMenu(bool enabled)
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        ModernMenuBusy = true;
        ModernMenuStatus = _loc["general.modern_menu_busy"];
        ModernContextMenuResult result;
        try
        {
            result = await _platform.SetModernContextMenuAsync(enabled, exe);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A command that throws would end the app (async void path): report it on the page instead.
            result = new ModernContextMenuResult(false, Error: ex.Message);
        }
        ModernMenuBusy = false;
        RefreshModernMenu();
        if (result.Cancelled)
            ModernMenuStatus = _loc["general.modern_menu_cancelled"];
        else if (!result.Succeeded)
            ModernMenuStatus = _loc.Format(enabled ? "general.modern_menu_failed" : "general.modern_menu_remove_failed", result.Error ?? "");
    }

    private void RefreshModernMenu()
    {
        ModernMenuState = Environment.ProcessPath is { } exe
            ? _platform.GetModernContextMenuState(exe)
            : ModernContextMenuState.Unsupported;
        ModernMenuStatus = _loc[ModernMenuState switch
        {
            ModernContextMenuState.On => "general.modern_menu_on",
            ModernContextMenuState.Outdated => "general.modern_menu_outdated",
            _ => "general.modern_menu_hint",
        }];
    }

    // Conversion history (home page): don't record it, how much of it to keep, or clear what is there.
    [ObservableProperty] private bool _noHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryText))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    private int _historyCount;

    [ObservableProperty] private bool _historyCleared;

    public string HistoryText => HistoryCleared ? _loc["general.history_cleared"] : _loc.Format("general.history_count", HistoryCount);

    partial void OnNoHistoryChanged(bool value) => Save(s => s.KeepHistory = !value);

    /// <summary>How many conversions the history keeps (<see cref="AppSettings.HistoryLimit"/>).</summary>
    [ObservableProperty] private int _historyLimit;

    partial void OnHistoryLimitChanged(int value) =>
        Save(s => s.HistoryLimit = Math.Clamp(value, AppSettings.MinHistoryLimit, AppSettings.MaxHistoryLimit));

    partial void OnHistoryClearedChanged(bool value) => OnPropertyChanged(nameof(HistoryText));

    private bool CanClearHistory() => HistoryCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearHistory))]
    private void ClearHistory()
    {
        _store.ClearHistory();
        HistoryCount = 0;
        HistoryCleared = true;
    }

    private void Save(Action<AppSettings> change)
    {
        change(_store.Settings);
        _store.SaveSettings();
        OnPropertyChanged(nameof(IntegrationMessage));
    }

    [RelayCommand]
    private void OpenDataFolder() => _platform.RevealInFileManager(_store.Directory);

    public void Export(string path)
    {
        _store.Export(path);
        LibraryMessage = Path.GetFileName(path);
    }

    public void Import(string path)
    {
        try
        {
            var count = _store.Import(path);
            LibraryMessage = _loc.Format("general.imported", count);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            LibraryMessage = ex.Message;
        }
    }

    public void ResetLibrary() => _store.ResetLibrary();
}
