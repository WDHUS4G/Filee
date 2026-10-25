// One compact card in a few states: Welcome (the logo, one Install button and an Options sheet with the folder, the
// options, the engines and the language) → Progress (the new version's "What's new" slides while the files go in) → Done ("Launch Filee") or
// Failed ("Copy details"). An update shows only what it may change (desktop icon, PATH); Filee's own settings stay as
// they are. The uninstaller uses the same card: Uninstall (keep or delete the settings) → Progress → Uninstalled.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.Setup.Installation;
using Filee.Setup.Payload;

namespace Filee.Setup.ViewModels;

public enum SetupPage
{
    Welcome,
    Progress,
    Done,
    Failed,
    Uninstall,
    Uninstalled,
}

/// <summary>What the card does when the user goes ahead (the real installer, or a fake in tests).</summary>
public interface ISetupRunner
{
    InstallResult Install(InstallChoices choices, IProgress<SetupProgress> progress);

    /// <summary>Uninstalls; null when it worked, else the error.</summary>
    Exception? Uninstall(bool deleteSettings, IProgress<SetupProgress> progress);
}

/// <summary>A language in the picker.</summary>
public sealed record LanguageItem(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An engine package in the Options sheet.</summary>
public sealed partial class EngineItem(EngineOffer offer, bool installed) : ObservableObject
{
    public EngineOffer Offer { get; } = offer;
    public string Id => Offer.Id;
    public bool Installed { get; } = installed;
    public bool CanSelect => !Installed;

    [ObservableProperty] private bool _selected = !installed && offer.Suggested;
    [ObservableProperty] private string _name = offer.Id;
    [ObservableProperty] private string _detail = "";
}

public sealed partial class SetupViewModel : ObservableObject
{
    private readonly SetupTexts _texts;
    private readonly ISetupRunner _runner;
    private readonly PreviousInstall? _previous;
    private readonly InstallChoices _initial;
    private readonly long _neededSpace;
    private bool _languagePicked;
    private InstallResult? _result;
    private SlideDeck? _deck;
    private bool _deckRead;

    /// <param name="version">The Filee version Setup installs (or the uninstaller removes).</param>
    /// <param name="initial">The choices from the command line and the defaults.</param>
    /// <param name="engines">Packages offered on a fresh install, with whether each is installed already.</param>
    /// <param name="modernMenuSupported">Windows 11 x64 and administrator rights: the top-level menu can be offered.</param>
    /// <param name="uninstall">The uninstaller's card.</param>
    public SetupViewModel(SetupTexts texts, ISetupRunner runner, string version, InstallChoices initial, PreviousInstall? previous,
        IReadOnlyList<(EngineOffer Offer, bool Installed)> engines, bool modernMenuSupported, long neededSpace, bool uninstall = false,
        bool languagePicked = false)
    {
        _texts = texts;
        _runner = runner;
        _previous = previous;
        _initial = initial;
        _neededSpace = neededSpace;
        _languagePicked = languagePicked;
        Version = version;
        IsUninstall = uninstall;
        IsUpdate = previous is not null && !uninstall;
        ModernMenuSupported = modernMenuSupported;
        Languages = SetupTexts.Languages.Select(l => new LanguageItem(l.Code, l.NativeName)).ToList();
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == texts.Language) ?? Languages[0];
        _folder = initial.Folder;
        _contextMenu = initial.Tasks.ContextMenu;
        _modernMenu = initial.Tasks.ModernMenu && modernMenuSupported;
        _startup = initial.Tasks.Startup;
        _cli = initial.Tasks.Cli;
        _desktopIcon = initial.Tasks.DesktopIcon;
        Engines = new ObservableCollection<EngineItem>(engines.Select(e => new EngineItem(e.Offer, e.Installed)));
        foreach (var engine in Engines)
        {
            engine.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EngineItem.Selected))
                    UpdateEngineSelection();
            };
        }
        _page = uninstall ? SetupPage.Uninstall : SetupPage.Welcome;
        texts.LanguageChanged += (_, _) => RefreshTexts();
        RefreshTexts();
    }

    public string Version { get; }
    public bool IsUninstall { get; }
    public bool IsUpdate { get; }
    public bool IsFresh => !IsUpdate && !IsUninstall;
    public bool ModernMenuSupported { get; }
    public IReadOnlyList<LanguageItem> Languages { get; }
    public ObservableCollection<EngineItem> Engines { get; }

    /// <summary>Fresh installs offer the engines that aren't downloaded yet.</summary>
    public bool HasEngines => IsFresh && Engines.Any(e => !e.Installed);

    /// <summary>/SILENT: the progress card only, no slides.</summary>
    public bool Silent { get; set; }

    /// <summary>
    /// The newest "What's new" slides up to the version being installed (Assets/WhatsNew, shared with the app), shown
    /// while the files go in; null when there are none. Read when first asked for: the model is made before Avalonia
    /// starts, and its resources can't be read until then.
    /// </summary>
    public SlideDeck? Deck
    {
        get
        {
            if (!_deckRead && !IsUninstall)
                _deck = SlideDeck.Newest(SlideDeck.LoadAll("Filee.Setup"), seen: null, SlideDeck.TryParseVersion(Version) ?? new Version(0, 0, 0));
            _deckRead = true;
            return _deck;
        }
        init
        {
            _deck = value;
            _deckRead = true;
        }
    }

    /// <summary>Raised when the window should close (finished, cancelled).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Asks the view for a folder (the folder picker); null when cancelled.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Puts text on the clipboard (the view's).</summary>
    public Func<string, Task>? CopyText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsProgress), nameof(IsDone), nameof(IsFailed), nameof(IsUninstallPage),
        nameof(IsUninstalled), nameof(ShowShowcase), nameof(PrimaryText))]
    private SetupPage _page;

    public bool IsWelcome => Page == SetupPage.Welcome;
    public bool IsProgress => Page == SetupPage.Progress;
    public bool IsDone => Page == SetupPage.Done;
    public bool IsFailed => Page == SetupPage.Failed;
    public bool IsUninstallPage => Page == SetupPage.Uninstall;
    public bool IsUninstalled => Page == SetupPage.Uninstalled;

    /// <summary>The progress card shows the "What's new" slides (not for /SILENT or the uninstaller).</summary>
    public bool ShowShowcase => IsProgress && !Silent && !IsUninstall && Deck is { Slides.Count: > 0 };

    [ObservableProperty] private bool _optionsOpen;
    [ObservableProperty] private LanguageItem _selectedLanguage;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private bool _contextMenu;
    [ObservableProperty] private bool _modernMenu;
    [ObservableProperty] private bool _startup;
    [ObservableProperty] private bool _cli;
    [ObservableProperty] private bool _desktopIcon;
    [ObservableProperty] private bool _deleteSettings;

    [ObservableProperty] private string _welcomeText = "";
    [ObservableProperty] private string _spaceText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressTitle = "";
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _uninstallText = "";
    [ObservableProperty] private string _uninstalledText = "";
    [ObservableProperty] private string _engineSelectionText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _settingsText = "";
    [ObservableProperty] private bool _copied;

    /// <summary>The big button of the Welcome card.</summary>
    public string PrimaryText => _texts[IsUpdate ? "installer.update" : "installer.install"];

    /// <summary>The fresh install's top-level menu option needs the Explorer menu option.</summary>
    public bool ModernMenuEnabled => ContextMenu;

    /// <summary>Exit code for Setup (Inno Setup's: 0 done, 2 cancelled before installing, 4 failed during installing).</summary>
    public int ExitCode { get; private set; } = 2;

    /// <summary>What to start when the window closes ("Launch Filee").</summary>
    public LaunchRequest? Launch { get; private set; }

    public InstallResult? Result => _result;

    /// <summary>Setup's log file (named on the error card).</summary>
    public string? LogFile
    {
        get;
        set
        {
            field = value;
            RefreshTexts();
        }
    }

    /// <summary>Filee's settings folder (named on the uninstall card).</summary>
    public string SettingsFolder
    {
        get;
        set
        {
            field = value;
            RefreshTexts();
        }
    } = "";

    partial void OnSelectedLanguageChanged(LanguageItem value)
    {
        if (value is null || value.Code == _texts.Language)
            return;
        _languagePicked = true;
        _texts.SetLanguage(value.Code);
    }

    partial void OnContextMenuChanged(bool value) => OnPropertyChanged(nameof(ModernMenuEnabled));

    /// <summary>A translated text for the current language (the slides' titles and texts).</summary>
    public string Text(string key) => _texts[key];

    /// <summary>Recomputes the texts with arguments (after a language change).</summary>
    private void RefreshTexts()
    {
        WelcomeText = IsUpdate
            ? _texts.Format("installer.welcome.update", _previous?.Version ?? "?", Version)
            : _texts.Format("installer.welcome.fresh", Version);
        SpaceText = _texts.Format("installer.options.space", Installer.FormatSize(_neededSpace));
        UninstallText = _texts.Format("installer.uninstall.text", Version);
        SettingsText = _texts.Format("installer.uninstall.settings_where", SettingsFolder);
        LogText = LogFile is null ? "" : _texts.Format("installer.error.log", LogFile);
        foreach (var engine in Engines)
        {
            engine.Name = _texts[$"engines.package.{engine.Id}.name"];
            engine.Detail = engine.Installed
                ? _texts["engines.state.installed"]
                : _texts.Format("engines.package.size", Installer.FormatSize(engine.Offer.DownloadSize), Installer.FormatSize(engine.Offer.InstalledSize));
        }
        UpdateEngineSelection();
        ErrorTitle = _texts[IsUninstall ? "installer.error.uninstall_title" : "installer.error.title"];
        ProgressTitle = _texts[IsUninstall ? "installer.uninstall.progress" : IsUpdate ? "installer.progress.update" : "installer.progress.install"];
        DoneTitle = _texts["installer.done.title"];
        OnPropertyChanged(nameof(PrimaryText));
    }

    private void UpdateEngineSelection()
    {
        var selected = Engines.Where(e => e.Selected).Select(e => e.Name).ToList();
        EngineSelectionText = selected.Count == 0 ? _texts["setup.nothing_selected"] : _texts.Format("setup.selection", string.Join(", ", selected));
    }

    [RelayCommand]
    private void ToggleOptions() => OptionsOpen = !OptionsOpen;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolder is null)
            return;
        if (await PickFolder(Folder) is { } picked)
        {
            // A folder of its own, like Inno Setup: picking "D:\Apps" installs into "D:\Apps\Filee".
            Folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(picked)).Equals("Filee", StringComparison.OrdinalIgnoreCase)
                ? picked
                : Path.Combine(picked, "Filee");
        }
    }

    /// <summary>The window's close button and Escape: closes the Options sheet first, then cancels (before installing).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (OptionsOpen)
        {
            OptionsOpen = false;
            return;
        }
        if (IsProgress)
            return;
        WindowClosing();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window is closing: before installing it is a cancel; after, nothing is started.</summary>
    public void WindowClosing()
    {
        if (Page is SetupPage.Welcome or SetupPage.Uninstall)
            ExitCode = 2;
    }

    [RelayCommand]
    private Task Install() => InstallAsync();

    [RelayCommand]
    private Task Uninstall() => UninstallAsync();

    /// <summary>"Launch Filee" on the Done card: after an update the main window, not the tray, so the slides show over it.</summary>
    [RelayCommand]
    private void LaunchAndClose()
    {
        Launch = IsUpdate && _result?.Launch is { } launch
            ? launch with { Arguments = Installer.LaunchArguments(_initial, update: true, shown: true, _previous?.Version) }
            : _result?.Launch;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task CopyDetailsAsync()
    {
        if (CopyText is null)
            return;
        await CopyText(ErrorText + Environment.NewLine + Environment.NewLine + LogText);
        Copied = true;
    }

    /// <summary>The choices as they stand in the Options sheet.</summary>
    public InstallChoices Choices()
    {
        var tasks = new SetupTasks(ContextMenu, ContextMenu && ModernMenu && ModernMenuSupported, Startup, Cli, DesktopIcon);
        return _initial with
        {
            Folder = Folder,
            Tasks = tasks,
            Engines = IsFresh ? Engines.Where(e => e.Selected && !e.Installed).Select(e => e.Id).ToList() : [],
            Language = _texts.Language,
            LanguagePicked = _languagePicked,
        };
    }

    /// <summary>Installs with the current choices (also what /SILENT does right away).</summary>
    public async Task InstallAsync()
    {
        if (Page != SetupPage.Welcome)
            return;
        RefreshTexts();
        var choices = Choices();
        OptionsOpen = false;
        Page = SetupPage.Progress;
        Percent = 0;
        var progress = new SyncProgress<SetupProgress>(p => Dispatcher.UIThread.Post(() => ShowProgress(p)));
        _result = await Task.Run(() => _runner.Install(choices, progress));
        if (_result.Succeeded)
        {
            ExitCode = 0;
            Percent = 100;
            WarningText = string.Join(Environment.NewLine + Environment.NewLine, _result.Warnings.Select(WarningMessage));
            // With a warning to read, the card leaves out how to use Filee (Filee shows that itself).
            DoneText = string.Join(Environment.NewLine + Environment.NewLine, new[]
            {
                IsUpdate ? _texts["installer.done.updated"] : WarningText.Length > 0 ? "" : _texts["installer.done.text"],
                choices.Engines.Count > 0 ? _texts["installer.done.engines"] : "",
            }.Where(t => t.Length > 0));
            Page = SetupPage.Done;
        }
        else
        {
            ExitCode = 4;
            ErrorText = ErrorMessage(_result.Error!) + Environment.NewLine + Environment.NewLine
                        + _texts[_result.RolledBack ? IsUpdate ? "installer.error.rolled_back" : "installer.error.undone" : "installer.error.nothing_changed"];
            Page = SetupPage.Failed;
        }
    }

    /// <summary>Uninstalls (also what a silent uninstall does right away).</summary>
    public async Task UninstallAsync()
    {
        if (Page != SetupPage.Uninstall)
            return;
        RefreshTexts();
        var deleteSettings = DeleteSettings;
        Page = SetupPage.Progress;
        Percent = 0;
        var progress = new SyncProgress<SetupProgress>(p => Dispatcher.UIThread.Post(() => ShowProgress(p)));
        var error = await Task.Run(() => _runner.Uninstall(deleteSettings, progress));
        if (error is null)
        {
            ExitCode = 0;
            Percent = 100;
            UninstalledText = _texts["installer.uninstall.done"]
                              + (deleteSettings ? "" : Environment.NewLine + Environment.NewLine + _texts["installer.uninstall.settings_kept"]);
            Page = SetupPage.Uninstalled;
        }
        else
        {
            ExitCode = 1;
            ErrorText = ErrorMessage(error);
            Page = SetupPage.Failed;
        }
    }

    private void ShowProgress(SetupProgress progress)
    {
        Percent = Math.Round(progress.Fraction * 100, 1);
        StatusText = _texts[progress.Step switch
        {
            SetupStep.Verifying => "installer.status.verifying",
            SetupStep.Closing => "installer.status.closing",
            SetupStep.RemovingOld => "installer.status.removing_old",
            SetupStep.Copying => "installer.status.copying",
            SetupStep.ExplorerMenu => "installer.status.explorer_menu",
            SetupStep.RollingBack => "installer.status.rolling_back",
            SetupStep.Removing => "installer.status.removing",
            _ => "installer.status.finishing",
        }];
    }

    /// <summary>A warning in the UI language.</summary>
    public string WarningMessage(SetupWarning warning) => SetupMessages.Warning(_texts, warning);

    /// <summary>An error in the UI language.</summary>
    public string ErrorMessage(Exception error) => SetupMessages.Error(_texts, error);
}

/// <summary>Warnings and errors in the UI language.</summary>
public static class SetupMessages
{
    public static string Warning(SetupTexts texts, SetupWarning warning) => warning switch
    {
        ExplorerMenuWarning menu => texts.Format("installer.warning.explorer_menu", menu.Error),
        RuntimeWarning runtime => texts.Format("installer.warning.runtime",
            string.Join(", ", runtime.Missing.Select(m => Path.GetFileName(m.Program)).Distinct(StringComparer.OrdinalIgnoreCase)),
            string.Join(", ", runtime.Missing.Select(m => m.Dll).Distinct(StringComparer.OrdinalIgnoreCase)),
            SetupNames.VcRedistUrl),
        _ => warning.ToString(),
    };

    /// <summary>The technical message only for errors nobody expected.</summary>
    public static string Error(SetupTexts texts, Exception error) => error switch
    {
        SetupException known => texts.Format(known.TextKey, [.. known.Arguments]),
        InvalidDataException => texts["installer.error.damaged"],
        UnauthorizedAccessException or IOException => texts.Format("installer.error.files", error.Message),
        _ => texts.Format("installer.error.unexpected", error.Message),
    };
}
