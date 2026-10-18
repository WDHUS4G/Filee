// Application start-up: builds services, applies settings, wires global gestures to the donut toolbar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.App;

public partial class App : Application
{
    private readonly List<string> _pendingConvertFiles = [];
    private IDisposable? _convertDebounce;
    private bool _selectionPending;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppHost.Build();
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // macOS: "Convert with Filee" in Finder's right-click menu. Registered first, so a service picked while
        // Filee wasn't running is delivered as soon as it starts.
        if (OperatingSystem.IsMacOS())
            Filee.Platform.MacOS.MacOSServicesProvider.Register(files =>
                Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(["--convert", .. files]))));
        var log = AppHost.Get<ILogger<App>>();
        var store = AppHost.Get<UserDataStore>();
        store.Load();

        var loc = AppHost.Get<LocalizationService>();
        loc.Attach(this);
        ApplySettings(store.Settings);

        // Global gestures → donut toolbar
        var triggers = AppHost.Get<TriggerService>();
        var radial = AppHost.Get<RadialController>();
        var platform = AppHost.Get<IPlatformServices>();
        triggers.DragStarted += (_, e) => radial.ShowForDrag(e.X, e.Y);
        triggers.DragEnded += (_, _) => radial.OnDragGestureEnded();
        triggers.SelectionGesture += async (_, e) =>
        {
            if (_selectionPending || store.Settings.Paused)
                return;
            _selectionPending = true;
            try
            {
                var files = OperatingSystem.IsMacOS()
                    ? await Task.Run(platform.GetFileManagerSelection) : platform.GetFileManagerSelection();
                if (files.Count > 0 && !store.Settings.Paused)
                    radial.ShowForFiles(e.X, e.Y, files);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Could not read the file manager selection");
                AppHost.Get<WindowService>().ShowMain("triggers");
                if (AppHost.Get<WindowService>().Main is { } window)
                    _ = AppHost.Get<WindowService>().MessageAsync(window, ex.Message);
            }
            finally
            {
                _selectionPending = false;
            }
        };
        triggers.Start();
        log.LogInformation("Filee {Version} started", UpdateService.CurrentVersion);
        // macOS: ask for Accessibility with the system's own alert when the user opened Filee (not at sign-in) and
        // a drag gesture is on. The home page keeps a reminder until it is given. Asked once the main window is up:
        // asked earlier, the alert ends up behind it.
        if (OperatingSystem.IsMacOS() && triggers.NeedsPermission && !Program.Options.Background
            && platform is Filee.Platform.MacOS.MacOSPlatformServices mac
            && store.Settings.Triggers.Any(t => t.Enabled && t.Kind == TriggerKind.Drag))
            DispatcherTimer.RunOnce(() =>
            {
                if (OperatingSystem.IsMacOS() && triggers.NeedsPermission)
                    mac.RequestAccessibilityPermission();
            }, TimeSpan.FromSeconds(1.5));

        store.SettingsChanged += (_, _) => ApplySettings(store.Settings);

        AppHost.Get<TrayService>().Create(this);
        AppHost.Get<WindowService>().EnsureToast();
        AppHost.Get<ConversionService>().FeedbackDue += (_, _) => AppHost.Get<WindowService>().ShowFeedbackNotice();
        AppHost.Get<WatchFolderService>().Start();
        // Once LibreOffice is installed: prepare its profiles while the user isn't converting yet.
        AppHost.Get<EngineDownloadService>().WarmUpInBackground(TimeSpan.FromSeconds(10));

        // Later launches (context menu, Send To, double-clicking the exe) forward their arguments here.
        if (Program.Instance is { } instance)
        {
            instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(args)));
            instance.StartListening();
        }

        desktop.Exit += (_, _) =>
        {
            AppHost.Get<TriggerService>().Dispose();
            AppHost.Get<TrayService>().Dispose();
            AppHost.Get<WatchFolderService>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        };

        HandleCommandLine(Program.Options, firstLaunch: true);
        WatchForUpdates(store);
    }

    /// <summary>
    /// Looks for new releases in the background. A new version shows up at the bottom of the navigation and in the
    /// tray menu; the bottom-right notice appears once per version.
    /// </summary>
    private static void WatchForUpdates(UserDataStore store)
    {
        var updates = AppHost.Get<UpdateService>();
        // Required: the card every time Filee starts (UpdateFound fires once per run). Recommended: once per version.
        // Optional: no card, the sidebar button only. Frequent releases no longer ask every day.
        updates.UpdateFound += (_, version) =>
        {
            if (updates.Level == UpdateLevel.Optional
                || updates.Level == UpdateLevel.Recommended && store.Settings.NotifiedUpdateVersion == version)
                return;
            store.Settings.NotifiedUpdateVersion = version;
            store.SaveSettings();
            AppHost.Get<WindowService>().ShowUpdateNotice(version);
        };
        updates.UpdateFailed += async (_, _) => await AppHost.Get<WindowService>().ShowUpdateFailedAsync();
        updates.StartPeriodicChecks(() => store.Settings.CheckForUpdates, TimeSpan.FromSeconds(20));
        DispatcherTimer.RunOnce(() => NotifyEngineUpdates(store), TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Engines the user downloaded that are older than the versions this Filee pins (it was updated since): one
    /// notice per new version, only to people who installed that engine, and only while update checks are on.
    /// </summary>
    private static void NotifyEngineUpdates(UserDataStore store)
    {
        if (!store.Settings.CheckForUpdates)
            return;
        var downloads = AppHost.Get<EngineDownloadService>();
        downloads.RefreshStatus();
        var outdated = downloads.Outdated;
        if (!outdated.Any(p => store.Settings.NotifiedEngineVersions.GetValueOrDefault(p.Package.Id) != p.Version))
            return;
        foreach (var package in outdated)
            store.Settings.NotifiedEngineVersions[package.Package.Id] = package.Version ?? "";
        store.SaveSettings();
        AppHost.Get<WindowService>().ShowEngineUpdateNotice(outdated);
    }

    /// <summary>Applies everything derived from settings. Called at start-up and whenever settings are saved.</summary>
    private static void ApplySettings(AppSettings settings)
    {
        // Before the catalog is created (the first time) or re-checked: engines look for the user's own copies first.
        var ownCopiesChanged = !EngineEnvironment.OwnCopies.OrderBy(p => p.Key).SequenceEqual(settings.EngineOwnCopies.OrderBy(p => p.Key));
        if (ownCopiesChanged)
            EngineEnvironment.OwnCopies = new Dictionary<string, string>(settings.EngineOwnCopies);
        var routesChanged = EngineEnvironment.LibreOfficeForSlides != settings.LibreOfficeForSlides
                            || EngineEnvironment.LibreOfficeForHwp != settings.LibreOfficeForHwp
                            || EngineEnvironment.LibreOfficeForDocuments != settings.LibreOfficeForDocuments
                            || EngineEnvironment.LibreOfficeForSheets != settings.LibreOfficeForSheets;
        EngineEnvironment.LibreOfficeForSlides = settings.LibreOfficeForSlides;
        EngineEnvironment.LibreOfficeForHwp = settings.LibreOfficeForHwp;
        EngineEnvironment.LibreOfficeForDocuments = settings.LibreOfficeForDocuments;
        EngineEnvironment.LibreOfficeForSheets = settings.LibreOfficeForSheets;

        var loc = AppHost.Get<LocalizationService>();
        if (loc.NeedsUpdate(settings.Language))
            loc.SetLanguage(settings.Language);

        AppHost.Get<ThemeService>().Apply(settings.Theme, loc.Language);
        AppHost.Get<TriggerService>().Apply(settings);
        AppHost.Get<ConverterCatalog>().Priority = settings.EnginePriority;
        if (ownCopiesChanged || routesChanged)
        {
            AppHost.Get<ConverterCatalog>().Refresh();
            AppHost.Get<EngineDownloadService>().RefreshStatus();
        }
        AppHost.Get<SystemIntegrationService>().Apply(settings);
    }

    private void HandleCommandLine(CommandLine options, bool firstLaunch = false)
    {
        if (options.Quit)
        {
            // The installer is about to replace or remove Filee's files (Program.RunInstallerCommand waits for this).
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        // The Windows 11 Explorer menu hands over huge selections in a temp file; its paths are in ConvertFiles now.
        options.DeleteListFile();
        if (options.ConvertFiles.Count > 0)
        {
            // Explorer may start one process per selected file: collect them for a moment, then open once.
            _pendingConvertFiles.AddRange(options.ConvertFiles);
            _convertDebounce?.Dispose();
            _convertDebounce = DispatcherTimer.RunOnce(OpenPendingFiles, TimeSpan.FromMilliseconds(350));
            return;
        }

        var store = AppHost.Get<UserDataStore>();
        ApplyInstallerChoices(options, store);
        if (!options.Background || options.ShowSettings || !firstLaunch)
            AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");

        if (options.InstallEngines is { } engines)
        {
            // Picked on the installer's engine page: download them now. Picking none answers the first-run question.
            if (engines.Count > 0)
                AppHost.Get<WindowService>().ShowEngineSetup(engines);
        }
        else if (!store.Settings.FirstRunCompleted)
        {
            // The installer only contains the small engines: offer the large ones on first run (portable zip, or an
            // installation that skipped the engine page).
            if (AppHost.Get<EngineDownloadService>().Packages.Any(p => !p.IsInstalled))
                AppHost.Get<WindowService>().ShowEngineSetup();
        }
        if (!store.Settings.FirstRunCompleted)
        {
            store.Settings.FirstRunCompleted = true;
            store.SaveSettings();
        }
    }

    /// <summary>
    /// Options ticked on the installer's task page. Saving applies them through SystemIntegrationService
    /// as if they had been switched on the General page.
    /// </summary>
    private static void ApplyInstallerChoices(CommandLine options, UserDataStore store)
    {
        if (options.StartWithWindows is null && options.ContextMenu is null)
            return;
        if (options.StartWithWindows is { } start)
            store.Settings.StartWithSystem = start;
        if (options.ContextMenu is { } menu)
            store.Settings.ContextMenuEnabled = menu;
        store.SaveSettings();
    }

    private void OpenPendingFiles()
    {
        var files = _pendingConvertFiles.Distinct(FileSystemPaths.Comparer).ToList();
        _pendingConvertFiles.Clear();
        var (x, y) = AppHost.Get<TriggerService>().LastCursor;
        if (x == 0 && y == 0 && AppHost.Get<WindowService>().Main is { } main)
            (x, y) = (main.Position.X + 200, main.Position.Y + 200);
        AppHost.Get<RadialController>().ShowForFiles(x, y, files);
    }

}
