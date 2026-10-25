// The log lines that explain a Filee which seems to ignore everything: UI thread stalls, bursts kept short, the
// start-up line, and the restart without administrator rights.

using System.Globalization;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Collects formatted log lines.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
            Lines.Add((logLevel, formatter(state, exception)));
    }
}

public class UiWatchdogTests
{
    private readonly ManualClock _clock = new();
    private readonly ListLogger _log = new();
    private readonly Queue<Action> _uiQueue = new();

    private UiWatchdog Create() => new(_uiQueue.Enqueue, _log, _clock, TimeSpan.FromSeconds(5));

    /// <summary>Ticks once a second for <paramref name="seconds"/> seconds while the UI thread runs nothing.</summary>
    private static void TickFor(UiWatchdog watchdog, ManualClock clock, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            watchdog.Tick();
        }
    }

    private void RunUi()
    {
        while (_uiQueue.TryDequeue(out var action))
            action();
    }

    [Fact]
    public void A_responsive_ui_thread_logs_nothing()
    {
        var watchdog = Create();
        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            watchdog.Tick();
            RunUi();
        }
        Assert.Empty(_log.Lines);
        Assert.Empty(_uiQueue);
    }

    [Fact]
    public void A_stall_is_reported_once_and_its_end_with_the_length()
    {
        // In a language that writes 12,5, on every machine: the log still says 12.5.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var watchdog = Create();
            watchdog.Tick(); // ping sent at 0 s
            TickFor(watchdog, _clock, 4);
            Assert.Empty(_log.Lines); // 4 s: under the threshold

            TickFor(watchdog, _clock, 8); // 12 s
            var warning = Assert.Single(_log.Lines);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal("UI thread blocked for 6 s", warning.Message); // the first tick past 5 s
            Assert.Single(_uiQueue); // no second ping while the first waits

            _clock.Advance(TimeSpan.FromSeconds(0.5));
            RunUi();
            Assert.Equal(2, _log.Lines.Count);
            Assert.Equal(LogLevel.Information, _log.Lines[1].Level);
            // The log formats numbers the same way in every language: 12.5, never 12,5.
            Assert.Equal("UI thread responsive again after 12.5 s", _log.Lines[1].Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Each_stall_is_a_new_episode()
    {
        var watchdog = Create();
        for (var episode = 0; episode < 2; episode++)
        {
            watchdog.Tick();
            TickFor(watchdog, _clock, 7);
            RunUi();
            // Quiet seconds between the stalls.
            for (var i = 0; i < 3; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                watchdog.Tick();
                RunUi();
            }
        }
        Assert.Equal(2, _log.Lines.Count(l => l.Level == LogLevel.Warning));
        Assert.Equal(2, _log.Lines.Count(l => l.Level == LogLevel.Information));
    }

    [Fact]
    public void A_dispatcher_that_has_shut_down_does_not_break_the_timer()
    {
        var watchdog = new UiWatchdog(_ => throw new InvalidOperationException("shut down"), _log, _clock);
        watchdog.Tick();
        TickFor(watchdog, _clock, 10);
        Assert.Empty(_log.Lines);
    }
}

public class LogThrottleTests
{
    [Fact]
    public void Logs_the_first_then_one_per_interval_counting_the_rest()
    {
        var clock = new ManualClock();
        var throttle = new LogThrottle(TimeSpan.FromSeconds(30), clock);

        Assert.True(throttle.TryEnter("forwarded", out var held));
        Assert.Equal(0, held);
        for (var i = 0; i < 99; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False(throttle.TryEnter("forwarded", out held));
        }
        Assert.True(throttle.TryEnter("other kind", out _)); // kinds are counted apart

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(throttle.TryEnter("forwarded", out held));
        Assert.Equal(99, held);
        Assert.Equal(" (99 more since the last one)", LogThrottle.HeldText(held));
        Assert.Equal("", LogThrottle.HeldText(0));
    }
}

public class StartupDiagnosticsTests
{
    [Fact]
    public void The_startup_line_names_version_path_kind_rights_os_and_argument_count()
    {
        var line = StartupDiagnostics.Describe("1.11.0", "/opt/filee/Filee", "portable",
            new ElevationState(IntegrityLevel.High, IntegrityLevel.Medium, true), "Microsoft Windows 10.0.26200 X64", 3);
        Assert.Equal("Filee 1.11.0 starting: /opt/filee/Filee (portable), integrity High (elevated), Explorer Medium, " +
                     "Microsoft Windows 10.0.26200 X64, 3 argument(s)", line);
    }

    [Fact]
    public void An_unknown_path_and_state_still_give_a_line()
    {
        var line = StartupDiagnostics.Describe("1.0.0", null, "development build", ElevationState.Unknown, "Linux", 0);
        Assert.Equal("Filee 1.0.0 starting: (unknown path) (development build), integrity unknown, Linux, 0 argument(s)", line);
    }

    [Theory]
    [InlineData(true, true, "installed")]
    [InlineData(true, false, "installed")]
    [InlineData(false, true, "portable")]
    [InlineData(false, false, "development build")]
    public void Install_kind(bool installed, bool release, string expected) =>
        Assert.Equal(expected, StartupDiagnostics.InstallKind(installed, release));

    [Fact]
    public void The_operating_system_text_is_not_empty() =>
        Assert.False(string.IsNullOrWhiteSpace(StartupDiagnostics.OperatingSystemText()));

    [Fact]
    public void The_early_logger_writes_to_the_daily_file()
    {
        var folder = Directory.CreateTempSubdirectory("filee-early-log-").FullName;
        try
        {
            var log = StartupDiagnostics.CreateEarlyLogger(folder);
            log.LogInformation("{Startup}", "Filee test starting");
            var file = Assert.Single(Directory.GetFiles(folder, "filee-*.log"));
            Assert.Contains("Program: Filee test starting", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Early_store_load_logs_corrupt_settings_and_keeps_broken_copy()
    {
        var folder = Directory.CreateTempSubdirectory("filee-early-store-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{broken");
            _ = StartupDiagnostics.CreateEarlyLogger(Path.Combine(folder, "logs"), out var storeLogger);
            var store = new UserDataStore(folder, storeLogger);

            store.Load();

            var file = Assert.Single(Directory.GetFiles(Path.Combine(folder, "logs"), "filee-*.log"));
            Assert.Contains("WARN UserDataStore: Could not read", File.ReadAllText(file));
            Assert.True(File.Exists(Path.Combine(folder, "settings.json.broken")));
            Assert.True(store.Settings.ShowInDock);
            Assert.True(store.Settings.ShowMenuBarIcon);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Launch_problems_are_shown_in_the_system_language_without_avalonia()
    {
        // Program shows these before Avalonia starts, so they are read without Avalonia's asset service.
        foreach (var key in new[] { "startup.elevated_other", "startup.no_answer" })
        {
            var text = Services.LocalizationService.EarlyText(key);
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }
}

public class ElevationServiceTests
{
    private readonly List<string> _calls = [];

    private ElevationService Create(string? relaunchError) => new(NullLogger<ElevationService>.Instance,
        new ElevationState(IntegrityLevel.High, IntegrityLevel.Medium, true),
        exe =>
        {
            _calls.Add("relaunch " + exe);
            return relaunchError;
        },
        () => _calls.Add("release"), () => _calls.Add("reacquire"), () => _calls.Add("shutdown"));

    [Fact]
    public void The_restart_lets_go_of_the_lock_before_starting_the_new_filee_then_quits()
    {
        var elevation = Create(relaunchError: null);
        Assert.True(elevation.ShellCannotReachFilee);

        Assert.True(elevation.RestartWithoutAdministratorRights("/apps/Filee"));

        Assert.Equal(["release", "relaunch /apps/Filee", "shutdown"], _calls);
        Assert.False(elevation.RestartFailed);
    }

    [Fact]
    public void A_failed_restart_takes_the_lock_back_keeps_running_and_says_so()
    {
        var elevation = Create(relaunchError: "Explorer's desktop window was not found.");
        var changed = new List<string?>();
        elevation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(elevation.RestartWithoutAdministratorRights("/apps/Filee"));

        Assert.Equal(["release", "relaunch /apps/Filee", "reacquire"], _calls);
        Assert.True(elevation.RestartFailed);
        Assert.Contains(nameof(ElevationService.RestartFailed), changed);
    }

    [Fact]
    public void Not_elevated_shows_no_warning()
    {
        var elevation = new ElevationService(NullLogger<ElevationService>.Instance,
            new ElevationState(IntegrityLevel.Medium, IntegrityLevel.Medium, false), _ => null, () => { }, () => { }, () => { });
        Assert.False(elevation.ShellCannotReachFilee);
    }

    [AvaloniaFact]
    public void The_home_page_warns_and_shows_a_failed_restart()
    {
        TestServices.EnsureInitialized();
        var elevation = Create(relaunchError: "Explorer did not answer.");
        using var page = new ViewModels.Pages.HomePageViewModel(AppHost.Get<Filee.Core.Settings.UserDataStore>(),
            AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<IPlatformServices>(), elevation: elevation);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(page.IsElevated);
        Assert.False(page.ElevationRestartFailed);

        page.RestartWithoutAdministratorRightsCommand.Execute(null);

        Assert.True(page.ElevationRestartFailed);
        Assert.Contains(nameof(page.ElevationRestartFailed), changed);
        Assert.DoesNotContain("shutdown", _calls);
    }

    [AvaloniaFact]
    public void The_home_page_has_no_warning_by_default()
    {
        TestServices.EnsureInitialized();
        using var page = new ViewModels.Pages.HomePageViewModel(AppHost.Get<Filee.Core.Settings.UserDataStore>(),
            AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<IPlatformServices>(),
            elevation: AppHost.Get<ElevationService>()); // tests never run Program: the state stays unknown
        Assert.False(page.IsElevated);
    }
}
