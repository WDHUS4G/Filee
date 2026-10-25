// Going to the tray releases the main window (issue #119): the next "Open" builds a new one, where and on the page the
// last one was, and nothing keeps the old window, its view model or its page alive.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class MainWindowReleaseTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Closing_to_the_tray_releases_the_window_and_open_builds_a_new_one_where_it_was()
    {
        TestServices.EnsureInitialized("en");
        var windows = AppHost.Get<WindowService>();
        var (window, model, page) = OpenMoveAndClose(windows);
        Assert.Null(windows.Main);

        windows.ShowMain();
        Pump();
        var second = windows.Main!;
        Assert.True(second.IsVisible);
        Assert.Equal("watch", ((MainWindowViewModel)second.DataContext!).SelectedItem.Key);
        Assert.Equal(new Size(900, 640), second.ClientSize);
        Assert.Equal(new PixelPoint(140, 90), second.Position);

        // Nothing holds the closed window, its view model or its page any more.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Pump();
        }
        Assert.False(window.TryGetTarget(out _), "window");
        Assert.False(model.TryGetTarget(out _), "view model");
        Assert.False(page.TryGetTarget(out _), "page");

        // Shown again before the posted release ran (e.g. the tray's Open right after closing): it stays open.
        // In this test, not one of its own: the window service is shared, and tests may run while another pumps.
        second.Close();
        windows.ShowMain();
        Pump();
        Assert.Same(second, windows.Main);
        Assert.True(second.IsVisible);

        second.Close();
        Pump();
        Assert.Null(windows.Main);
    }

    // Not inlined: nothing on the test's stack may hold the first window when it is collected.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<MainWindow>, WeakReference<MainWindowViewModel>, WeakReference<object>) OpenMoveAndClose(WindowService windows)
    {
        windows.ShowMain("watch");
        Pump();
        var main = windows.Main!;
        main.Position = new PixelPoint(140, 90);
        main.Width = 900;
        main.Height = 640;
        Pump();
        Assert.Equal(new PixelPoint(140, 90), main.Position);
        var model = (MainWindowViewModel)main.DataContext!;
        var page = model.CurrentPage;

        main.Close(); // as the title bar's close button
        Pump();
        Assert.False(main.IsVisible);
        main.Close(); // again, e.g. a test cleaning up: harmless
        Pump();
        // The old view model no longer follows the language (it would rebuild its page).
        AppHost.Get<LocalizationService>().SetLanguage("ko");
        Assert.Same(page, model.CurrentPage);
        AppHost.Get<LocalizationService>().SetLanguage("en");
        return (new(main), new(model), new(page));
    }
}
