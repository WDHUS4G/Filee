// Which Shell.Application window the selection shortcut reads (issue #239). With File Explorer tabs (Windows 11)
// Shell.Application.Windows() has one entry per tab, every one with the window's HWND, in the order the tabs were
// opened; switching tabs doesn't reorder them. Each tab has its own ShellTabWindowClass child window, and only the
// active tab's is visible. The tab an entry belongs to is its shell browser's window (IShellBrowser::GetWindow).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.Windows;

/// <summary>One entry of Shell.Application.Windows(): its top-level window and its tab window (0 when unknown).</summary>
internal readonly record struct ShellWindowEntry(nint TopLevel, nint Tab);

/// <summary>A ShellTabWindowClass child of an Explorer window, in z-order.</summary>
internal readonly record struct TabWindow(nint Handle, bool Visible);

internal static class ExplorerTabs
{
    private const string TabWindowClass = "ShellTabWindowClass";

    /// <summary>
    /// The active tab among the tab windows of one Explorer window: the visible one, else the first in z-order
    /// (the active tab is on top). 0 when the window has no tab windows (the desktop, other windows).
    /// </summary>
    public static nint PickActiveTab(IEnumerable<TabWindow> tabs)
    {
        nint first = 0;
        foreach (var tab in tabs)
        {
            if (tab.Visible)
                return tab.Handle;
            if (first == 0)
                first = tab.Handle;
        }
        return first;
    }

    /// <summary>
    /// The entries to read the selection from, in order. Only entries of the foreground window count. When one of them
    /// is the active tab, it is the only one: another tab's selection, even a stale one, is never what the user means.
    /// Without tabs, or when no entry gives out its tab window, every entry of the window is tried in order.
    /// </summary>
    public static IReadOnlyList<int> Candidates(IReadOnlyList<ShellWindowEntry> entries, nint foreground, nint activeTab)
    {
        var sameWindow = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].TopLevel != foreground)
                continue;
            if (activeTab != 0 && entries[i].Tab == activeTab)
                return [i];
            sameWindow.Add(i);
        }
        return sameWindow;
    }

    /// <summary>The active tab window of an Explorer window, or 0.</summary>
    [SupportedOSPlatform("windows")]
    public static nint ActiveTabOf(nint topLevel) => PickActiveTab(TabWindowsOf(topLevel));

    [SupportedOSPlatform("windows")]
    private static IEnumerable<TabWindow> TabWindowsOf(nint topLevel)
    {
        nint child = 0;
        while ((child = NativeMethods.FindWindowEx(topLevel, child, TabWindowClass, null)) != 0)
            yield return new TabWindow(child, NativeMethods.IsWindowVisible(child));
    }

    /// <summary>The tab window of one Shell.Application window (an IWebBrowser2), or 0 when it doesn't say.</summary>
    [SupportedOSPlatform("windows")]
    public static nint TabOf(object window)
    {
        object? browser = null;
        try
        {
            if (window is not ShellInterop.IServiceProvider provider)
                return 0;
            var service = ShellInterop.SID_STopLevelBrowser;
            var shellBrowserId = typeof(ShellInterop.IShellBrowser).GUID;
            browser = provider.QueryService(ref service, ref shellBrowserId);
            return browser is ShellInterop.IShellBrowser shellBrowser && shellBrowser.GetWindow(out var hwnd) >= 0 ? hwnd : 0;
        }
        catch (COMException)
        {
            return 0;
        }
        catch (InvalidCastException)
        {
            return 0;
        }
        finally
        {
            // Not the final release: the browser may be the same COM object as the window, which is still used.
            if (browser is not null && Marshal.IsComObject(browser))
                Marshal.ReleaseComObject(browser);
        }
    }
}
