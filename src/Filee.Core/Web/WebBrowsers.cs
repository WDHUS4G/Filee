// Web browsers by process name (Windows without ".exe", the Linux executable, the macOS app name). A drag gesture over
// one opens the donut even when gestures are limited to the file manager: a picture or link dragged out of a page
// reaches the donut like a file (see BrowserDrag). Click and key gestures stay off there: they read the file manager's
// selection, which a browser doesn't have.

namespace Filee.Core.Web;

public static class WebBrowsers
{
    private static readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "whale", "arc", "zen", "librewolf", "waterfox",
        "floorp", "chromium", "yandex",
        // Linux
        "firefox-esr", "firefox-bin", "google-chrome", "chromium-browser", "microsoft-edge", "brave-browser",
        "vivaldi-bin", "naver-whale",
        // macOS
        "Safari", "Google Chrome", "Microsoft Edge", "Brave Browser", "Naver Whale", "Chromium", "Zen Browser",
        "Firefox Developer Edition", "Firefox Nightly", "Google Chrome Canary", "Microsoft Edge Dev", "Orion",
    };

    /// <summary>True for a web browser's process, by the name the platform reports.</summary>
    public static bool IsBrowser(string? processName) =>
        processName is not null && Processes.Contains(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName);
}
