// Application start-up: builds services, applies settings, wires global gestures to the donut toolbar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Filee.App.Controls;
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

    /// <summary>Forwarded launches and empty selections can come in bursts: a few lines a minute are enough.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromSeconds(30));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SingleSelectionGuard.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppHost.Build(preloadedStore: Program.PreloadedStore);
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Ends Filee as the tray's Quit does.</summary>
    public static void Quit() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = AppHost.Get<ILogger<App>>();
        // macOS: "Convert with Filee" in Finder's right-click menu. Registered first, so a service picked while
        // Filee wasn't running is delivered as soon as it starts. A request without files Filee can find (another
        // file manager put its selection on the pasteboard in a form Filee doesn't read) is logged with what it held.
        if (OperatingSystem.IsMacOS())
            Filee.Platform.MacOS.MacOSServicesProvider.Register(
                files => Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(["--convert", .. files]))),
                types => log.LogWarning("Convert with Filee was picked, but the request named no existing file; it held: {Types}",
                    types.Count == 0 ? "nothing" : string.Join(", ", types)));
        // Exceptions in UI event handlers end Filee; write them down first (they are not marked handled).
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            log.LogCritical(e.Exception, "Unhandled exception on the UI thread");
        var watchdog = new UiWatchdog(action => Dispatcher.UIThread.Post(action), AppHost.Get<ILogger<UiWatchdog>>());
        watchdog.Start();
        var store = AppHost.Get<UserDataStore>();
        if (Program.PreloadedStore is null)
            store.Load();

        var loc = AppHost.Get<LocalizationService>();
        loc.Attach(this);
        ApplySettings(store.Settings);
        // Window zoom: set once here, not with the other settings, since the zoom saves itself (UiZoom) without
        // raising SettingsChanged.
        UiZoom.Configure(store.Settings.UiScale, scale =>
        {
            try
            {
                store.SaveUiScale(scale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not remember the window zoom");
            }
        });
        // Every engine's availability, checked once off the UI thread: the first donut or drop otherwise starts
        // every engine's check (FFmpeg -version, folder searches) on the UI thread and freezes it meanwhile.
        _ = AppHost.Get<ConverterCatalog>().CheckInBackgroundAsync();

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
                else if (files.Count == 0 && Throttle.TryEnter("empty-selection", out var held))
                    log.LogInformation("The selection shortcut found no selected files in {Window}{Elevation}{Held}",
                        platform.DescribeForegroundWindow() is { Length: > 0 } window ? window : "the foreground window",
                        Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator and can't read Explorer)" : "",
                        LogThrottle.HeldText(held));
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
        // Which way the transparent windows (donut, toast) are drawn, for reports of one that isn't see-through (#269).
        if (OperatingSystem.IsWindows())
        {
            log.LogInformation("{Rendering}", WindowsRendering.Describe(Environment.GetEnvironmentVariable(WindowsRendering.Variable)));
            TransparentWindows.Watch(log);
        }
        // macOS: ask for Accessibility with the system's own alert when the user opened Filee (not at sign-in) and
        // a drag gesture is on, once ever (see ShouldAskForPermission). The home page keeps a reminder until it is
        // given. Asked once the main window is up: asked earlier, the alert ends up behind it.
        if (OperatingSystem.IsMacOS() && !Program.Options.Background
            && platform is Filee.Platform.MacOS.MacOSPlatformServices mac
            && TriggerService.ShouldAskForPermission(store.Settings, triggers.NeedsPermission))
            DispatcherTimer.RunOnce(() =>
            {
                if (!OperatingSystem.IsMacOS() || !TriggerService.ShouldAskForPermission(store.Settings, triggers.NeedsPermission))
                    return;
                store.Settings.AccessibilityPromptShown = true;
                store.SaveSettings();
                mac.RequestAccessibilityPermission();
            }, TimeSpan.FromSeconds(1.5));

        store.SettingsChanged += (_, _) => ApplySettings(store.Settings);

        AppHost.Get<TrayService>().Create(this);
        AppHost.Get<WindowService>().EnsureToast();
        AppHost.Get<ConversionService>().FeedbackDue += (_, _) => AppHost.Get<WindowService>().ShowFeedbackNotice();
        AppHost.Get<WatchFolderService>().Start();
        // Originals of files from a browser that a crash kept from being deleted ("Save only the converted file").
        _ = Task.Run(() => Filee.Core.Web.WebDrop.DeleteStale(Filee.Core.Web.WebDrop.DefaultWorkRoot, TimeSpan.FromDays(1)));
        // Likewise the work folders of conversions that never got to clean up after themselves.
        _ = Task.Run(() => JobQueue.DeleteStaleWorkFolders(JobQueue.DefaultWorkRoot, TimeSpan.FromDays(1)));
        // Leftover update workspaces from cancelled or failed in-app updates (Linux only).
        if (OperatingSystem.IsLinux())
            _ = Task.Run(() => UpdateService.DeleteStaleWorkspaces());
        // Once LibreOffice is installed: prepare its profiles while the user isn't converting yet.
        AppHost.Get<EngineDownloadService>().WarmUpInBackground(TimeSpan.FromSeconds(10));

        // Later launches (context menu, Send To, double-clicking the exe) forward their arguments here.
        if (Program.Instance is { } instance)
            ListenForLaterLaunches(instance, log);
        Program.InstanceAcquired += again => ListenForLaterLaunches(again, log);

        desktop.Exit += (_, _) =>
        {
            UiZoom.Flush();
            watchdog.Dispose();
            AppHost.Get<MemoryTrimmer>().Dispose();
            AppHost.Get<TriggerService>().Dispose();
            AppHost.Get<TrayService>().Dispose();
            AppHost.Get<WatchFolderService>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        };

        // Before HandleCommandLine marks the first run complete: a fresh install gets no slides (an update by the
        // installer does, even when its settings are gone). Without the main window (sign-in, the Explorer menu) they
        // wait until the user opens Filee, unless the installer asked for them.
        AppHost.Get<WhatsNewService>().CheckAtStartup(
            background: Program.Options.Background && !Program.Options.ShowSettings && !Program.Options.WhatsNew
                        || Program.Options.ConvertFiles.Count > 0,
            updated: Program.Options.WhatsNew, updatedFrom: Program.Options.UpdatedFrom);
        HandleCommandLine(Program.Options, firstLaunch: true);
        if (OperatingSystem.IsMacOS())
            this.TryGetFeature<IActivatableLifetime>()?.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Clicking the Dock icon or opening Filee again in Finder brings the window back, as Mac apps do
                        // (#232), also while it only runs in the background.
                        AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");
                        if (!store.Settings.ShowInDock && !platform.TrySetShowInDock(false))
                            log.LogWarning("Could not restore the macOS Dock visibility policy after reopening Filee");
                    });
            };
        WatchForUpdates(store);
        // Filee runs as administrator while Explorer doesn't (an installer started "as administrator" started it):
        // say so once, next to the tray, and on the Home page for as long as it lasts.
        if (AppHost.Get<ElevationService>().ShellCannotReachFilee)
            DispatcherTimer.RunOnce(() => AppHost.Get<WindowService>().ShowElevationNotice(), TimeSpan.FromSeconds(2));
    }

    private void ListenForLaterLaunches(SingleInstance instance, ILogger log)
    {
        instance.ArgumentsReceived += args =>
        {
            if (Throttle.TryEnter("forwarded", out var held))
                log.LogInformation("A later launch handed over {Count} argument(s){Held}", args.Length, LogThrottle.HeldText(held));
            Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(args), forwarded: true));
        };
        instance.StartListening();
    }

    /// <summary>
    /// Looks for new releases in the background. A new version shows up at the bottom of the navigation and in the
    /// tray menu; the bottom-right notice appears once per version.
    /// </summary>
    private static void WatchForUpdates(UserDataStore store)
    {
        var updates = AppHost.Get<UpdateService>();
        updates.RequestApplicationShutdown = Quit;
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
        // The last run's update failed after Filee closed (Linux): the installer rolled back and started this copy.
        if (updates.CheckPendingFailure(store.Directory))
            Dispatcher.UIThread.Post(async () => await AppHost.Get<WindowService>().ShowUpdateRolledBackAsync(updates.LastFailureLogPath));
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

        var custom = CustomTheme.Load(AppHost.Get<UserDataStore>().Directory, AppHost.Get<ILogger<App>>());
        AppHost.Get<ThemeService>().Apply(settings.Theme, loc.Language, custom);
        AppHost.Get<TriggerService>().Apply(settings);
        AppHost.Get<ConverterCatalog>().Priority = settings.EnginePriority;
        if (ownCopiesChanged || routesChanged)
        {
            AppHost.Get<ConverterCatalog>().Refresh();
            AppHost.Get<EngineDownloadService>().RefreshStatus();
        }
        AppHost.Get<SystemIntegrationService>().Apply(settings);
    }

    private void HandleCommandLine(CommandLine options, bool firstLaunch = false, bool forwarded = false)
    {
        // Pairs with "A later launch handed over …": the request reached the UI thread too.
        if (forwarded && Throttle.TryEnter("forwarded-handled", out var held))
            AppHost.Get<ILogger<App>>().LogInformation("Handling a later launch: {Request}{Held}",
                options.Quit ? "quit" : options.ConvertFiles.Count > 0 ? $"convert {options.ConvertFiles.Count} file(s)" : "show Filee",
                LogThrottle.HeldText(held));
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
    /// Options ticked in Setup, and the language picked there. Saving applies them (SystemIntegrationService, the
    /// language) as if they had been switched on the General page.
    /// </summary>
    private static void ApplyInstallerChoices(CommandLine options, UserDataStore store)
    {
        // Only Filee's languages; anything else keeps the setting (normally "auto", Windows' language).
        var language = LocalizationService.Languages.FirstOrDefault(l => l.Code.Equals(options.Language, StringComparison.OrdinalIgnoreCase)).Code;
        if (options.StartWithWindows is null && options.ContextMenu is null && language is null)
            return;
        if (options.StartWithWindows is { } start)
            store.Settings.StartWithSystem = start;
        if (options.ContextMenu is { } menu)
            store.Settings.ContextMenuEnabled = menu;
        if (language is not null)
            store.Settings.Language = language;
        store.SaveSettings();
    }

    private void OpenPendingFiles()
    {
        var files = _pendingConvertFiles.Distinct(FileSystemPaths.Comparer).ToList();
        _pendingConvertFiles.Clear();
        var (x, y) = AppHost.Get<TriggerService>().LastCursor;
        if (x == 0 && y == 0 && AppHost.Get<WindowService>().Main is { } main)
            (x, y) = (main.Position.X + 200, main.Position.Y + 200);
        AppHost.Get<RadialController>().ShowForFiles(x, y, files, forAnotherProcess: true);
    }

}
