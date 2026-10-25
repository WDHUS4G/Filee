// There is never more than one main window: not when a tray click is handled while ShowMain builds the window (the
// Windows tray raises Clicked straight from its window procedure, so any message handled meanwhile re-enters it), not
// when one arrives while a window that went to the tray is being released, and not after many open/close cycles.

using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.Views;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class SingleMainWindowTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Records the main windows <see cref="WindowService.ShowMain"/> builds in this test (other tests may build theirs
    /// while this one pumps), and can run a "tray click" while one is being built or released.
    /// </summary>
    private sealed class Tracker : IDisposable
    {
        private static readonly FieldInfo Releasing =
            typeof(MainWindow).GetField("_releasing", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private readonly WindowService _windows;
        private readonly IDisposable _subscription;
        private int _inShowMain;

        public Tracker(WindowService windows)
        {
            _windows = windows;
            _subscription = StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            {
                if (_inShowMain == 0 || Created.Contains(window))
                    return;
                Created.Add(window);
                window.Closing += (_, _) =>
                {
                    // The real close after going to the tray (not a close request, which only hides it).
                    if (ClickWhileReleasing && (bool)Releasing.GetValue(window)!)
                    {
                        ClickWhileReleasing = false;
                        ShowMain();
                    }
                };
                if (ClickWhileBuilding)
                {
                    ClickWhileBuilding = false;
                    ShowMain(ClickPage);
                }
            });
        }

        public List<MainWindow> Created { get; } = [];
        public bool ClickWhileBuilding { get; set; }
        public string? ClickPage { get; set; }
        public bool ClickWhileReleasing { get; set; }

        /// <summary>Whether the last request was to show the main window (rather than to close it).</summary>
        public bool ExpectShown { get; set; }

        public void ShowMain(string? page = null)
        {
            ExpectShown = true;
            _inShowMain++;
            try
            {
                _windows.ShowMain(page);
            }
            finally
            {
                _inShowMain--;
            }
        }

        public void AssertOneMainWindow()
        {
            var visible = Created.Where(w => w.IsVisible).ToList();
            Assert.True(visible.Count <= 1, $"{visible.Count} main windows are on screen");
            if (ExpectShown)
            {
                Assert.NotNull(_windows.Main);
                Assert.True(_windows.Main!.IsVisible, "the main window is not on screen");
                Assert.Same(_windows.Main, Assert.Single(visible));
            }
        }

        /// <summary>After the posted releases ran, every window but the open one is closed.</summary>
        public void AssertOthersClosed()
        {
            foreach (var window in Created.Where(w => w != _windows.Main))
                Assert.Null(window.PlatformImpl);
        }

        public void Dispose() => _subscription.Dispose();
    }

    private static WindowService Start()
    {
        TestServices.EnsureInitialized("en");
        var windows = AppHost.Get<WindowService>();
        windows.Main?.Close();
        Pump();
        return windows;
    }

    private static void CloseToTray(WindowService windows, Tracker tracker)
    {
        tracker.ExpectShown = false;
        windows.Main?.Close(); // as the title bar's close button
    }

    [AvaloniaFact]
    public void A_tray_click_handled_while_the_window_is_built_opens_no_second_one()
    {
        var windows = Start();
        using var tracker = new Tracker(windows);
        try
        {
            // A few trips to the tray first: from then on the window opens where, and on the page, the last one was.
            for (var i = 0; i < 3; i++)
            {
                tracker.ShowMain(i == 0 ? "theme" : null);
                Pump();
                tracker.AssertOneMainWindow();
                CloseToTray(windows, tracker);
                Pump();
                Assert.Null(windows.Main);
            }

            tracker.ClickWhileBuilding = true;
            tracker.ShowMain();
            Assert.False(tracker.ClickWhileBuilding);
            tracker.AssertOneMainWindow();
            Pump();
            tracker.AssertOneMainWindow();
            tracker.AssertOthersClosed();
            Assert.Equal("theme", ((ViewModels.MainWindowViewModel)windows.Main!.DataContext!).SelectedItem.Key);

            // A page asked for by the click handled meanwhile still opens.
            CloseToTray(windows, tracker);
            Pump();
            tracker.ClickWhileBuilding = true;
            tracker.ClickPage = "watch";
            tracker.ShowMain();
            Pump();
            tracker.AssertOneMainWindow();
            tracker.AssertOthersClosed();
            Assert.Equal("watch", ((ViewModels.MainWindowViewModel)windows.Main!.DataContext!).SelectedItem.Key);
        }
        finally
        {
            CloseToTray(windows, tracker);
            Pump();
        }
    }

    [AvaloniaFact]
    public void A_tray_click_while_the_window_is_released_opens_a_new_one()
    {
        var windows = Start();
        using var tracker = new Tracker(windows);
        try
        {
            tracker.ShowMain();
            Pump();
            var first = windows.Main!;

            CloseToTray(windows, tracker);
            tracker.ClickWhileReleasing = true;
            Pump();
            Assert.False(tracker.ClickWhileReleasing);
            tracker.AssertOneMainWindow();
            Assert.NotSame(first, windows.Main);
            Assert.Null(first.PlatformImpl);
        }
        finally
        {
            CloseToTray(windows, tracker);
            Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(119)]
    [InlineData(7)]
    [InlineData(2026)]
    public void Rapid_tray_clicks_and_closes_keep_one_main_window(int seed)
    {
        var windows = Start();
        using var tracker = new Tracker(windows);
        var random = new Random(seed);
        try
        {
            for (var step = 0; step < 200; step++)
            {
                switch (random.Next(6))
                {
                    case 0:
                        tracker.ShowMain();
                        break;
                    case 1: // a double click: two clicks in a row
                        tracker.ShowMain();
                        tracker.ShowMain();
                        break;
                    case 2:
                        CloseToTray(windows, tracker);
                        break;
                    case 3:
                        tracker.ExpectShown = false;
                        windows.Main?.HideToTray();
                        break;
                    case 4:
                        tracker.ClickWhileBuilding = true;
                        break;
                    default:
                        tracker.ClickWhileReleasing = true;
                        break;
                }
                if (random.Next(2) == 0)
                    Pump();
                tracker.AssertOneMainWindow();
            }
            Pump();
            tracker.AssertOneMainWindow();
            tracker.AssertOthersClosed();
            Assert.True(tracker.Created.Count > 10, "the windows were released and built again");
        }
        finally
        {
            CloseToTray(windows, tracker);
            Pump();
        }
    }
}

// The app has one WindowService and so one main window: tests that open, close or count it run one at a time, never
// next to each other (a test pumping the dispatcher would otherwise run another one's posted close in between).
[CollectionDefinition("MainWindow", DisableParallelization = true)]
public sealed class MainWindowCollection;
