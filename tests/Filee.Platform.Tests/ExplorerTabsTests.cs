using Filee.Platform.Windows;

namespace Filee.Platform.Tests;

/// <summary>The selection shortcut reads the active Explorer tab, not the oldest tab of the window (#239).</summary>
public sealed class ExplorerTabsTests
{
    private const nint Window = 0x1000, OtherWindow = 0x2000;
    private const nint TabA = 0x11, TabB = 0x12, TabC = 0x13;

    [Fact]
    public void The_active_tab_is_the_only_entry_read_among_tabs_of_one_window()
    {
        // Shell.Application lists the tabs in the order they were opened, all with the window's HWND.
        ShellWindowEntry[] entries = [new(OtherWindow, 0x21), new(Window, TabA), new(Window, TabB), new(Window, TabC)];

        Assert.Equal([2], ExplorerTabs.Candidates(entries, Window, TabB));
        Assert.Equal([3], ExplorerTabs.Candidates(entries, Window, TabC));
        Assert.Equal([1], ExplorerTabs.Candidates(entries, Window, TabA));
    }

    [Fact]
    public void A_single_window_is_read_as_before()
    {
        ShellWindowEntry[] entries = [new(OtherWindow, 0x21), new(Window, TabA)];

        Assert.Equal([1], ExplorerTabs.Candidates(entries, Window, TabA));
        // No tab window found (the desktop, Windows 10 builds that don't give it out): every entry of the window.
        Assert.Equal([1], ExplorerTabs.Candidates(entries, Window, 0));
    }

    [Fact]
    public void Without_a_matching_tab_every_entry_of_the_window_is_tried_in_order()
    {
        // The tabs didn't give out their windows (0), or none of them is the visible one: the old lookup.
        ShellWindowEntry[] unknown = [new(Window, 0), new(OtherWindow, 0), new(Window, 0)];
        ShellWindowEntry[] noMatch = [new(Window, TabA), new(Window, TabB)];

        Assert.Equal([0, 2], ExplorerTabs.Candidates(unknown, Window, TabC));
        Assert.Equal([0, 1], ExplorerTabs.Candidates(noMatch, Window, TabC));
        Assert.Empty(ExplorerTabs.Candidates(noMatch, OtherWindow, TabA));
        Assert.Empty(ExplorerTabs.Candidates([], Window, TabA));
    }

    [Fact]
    public void The_active_tab_window_is_the_visible_one_else_the_topmost()
    {
        Assert.Equal(TabB, ExplorerTabs.PickActiveTab([new(TabA, false), new(TabB, true), new(TabC, false)]));
        Assert.Equal(TabA, ExplorerTabs.PickActiveTab([new(TabA, true), new(TabB, false)]));
        Assert.Equal(TabA, ExplorerTabs.PickActiveTab([new(TabA, false), new(TabB, false)]));
        Assert.Equal(0, ExplorerTabs.PickActiveTab([]));
    }
}
