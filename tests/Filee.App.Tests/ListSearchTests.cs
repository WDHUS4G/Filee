// The search boxes above the profile, palette and preset lists, and the excluded apps on the Shortcuts page.

using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class ListSearchTests
{
    private sealed record Item(string Name, string Format);

    [Fact]
    public void Filtered_list_follows_the_query_and_the_source()
    {
        var photo = new Item("Photos", "jpg");
        var docs = new Item("Documents", "pdf");
        var source = new ObservableCollection<Item> { photo, docs };
        var list = new FilteredList<Item>(source, i => [i.Name, i.Format]);
        Assert.Equal([photo, docs], list);

        list.Query = "PDF ";
        Assert.Equal([docs], list); // any of the texts, ignoring case and spaces around the query

        var scans = new Item("Scans", "pdf");
        source.Insert(0, scans);
        Assert.Equal([scans, docs], list); // in the source's order

        source.Remove(docs);
        Assert.Equal([scans], list);

        list.Query = "";
        Assert.Equal([scans, photo], list);
    }

    [Fact]
    public void Filtered_list_keeps_the_items_that_still_match()
    {
        var a = new Item("Alpha", "a");
        var b = new Item("Beta", "b");
        var source = new ObservableCollection<Item> { a, b };
        var list = new FilteredList<Item>(source, i => [i.Name]);
        var removed = 0;
        list.CollectionChanged += (_, e) => removed += e.OldItems?.Count ?? 0;

        list.Query = "a"; // both match ("Alpha", "Beta")
        Assert.Equal(0, removed);
        list.Query = "al";
        Assert.Equal(1, removed); // only Beta went: a ListBox keeps Alpha selected
    }

    private sealed class Apps(params string[] names) : IPlatformServices
    {
        public IReadOnlyList<string> RunningAppNames() => IPlatformServices.AppNames(names);
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }

    [Fact]
    public void Excluded_apps_are_tags_picked_or_typed()
    {
        var excluded = new List<string> { "code" };
        var saves = 0;
        var vm = new ExcludedAppsViewModel(excluded, new Apps("Photoshop", "code", "Finder", "photoshop"), () => saves++);
        Assert.Equal(["code"], vm.Tags.Select(t => t.Value));

        // Typing suggests the running apps that aren't excluded yet.
        vm.Text = "sho";
        Assert.Equal(["Photoshop"], vm.Suggestions.Select(s => s.Value));

        // The list to pick from: apps with a window, sorted, without duplicates or excluded ones.
        vm.LoadRunningApps();
        Assert.Equal(["Finder", "Photoshop"], vm.RunningApps);
        vm.RunningSearch = "fin";
        Assert.Equal(["Finder"], vm.VisibleRunningApps);

        vm.AddCommand.Execute("Finder");
        vm.AddCommand.Execute(" Photoshop.exe "); // typed like Windows shows it
        vm.AddCommand.Execute("CODE"); // already there
        Assert.Equal(["code", "Finder", "Photoshop"], excluded);
        Assert.Equal(2, saves);
        Assert.Empty(vm.RunningApps);
        Assert.True(vm.NoRunningApps);

        vm.RemoveCommand.Execute(new TagChip("finder", "finder"));
        Assert.Equal(["code", "Photoshop"], excluded);
        Assert.Equal(["Finder"], vm.RunningApps);
    }

    [Fact]
    public void Running_app_names_leave_out_filee_itself()
    {
        var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        Assert.Equal(["a", "B"], IPlatformServices.AppNames(["B", own, "a", "", null, "b"]));
    }

    [AvaloniaFact]
    public void Updates_are_set_on_the_about_page()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.CheckForUpdates;
        try
        {
            using var about = new AboutPageViewModel(AppHost.Get<Filee.Core.Localization.ILocalizer>(), store, AppHost.Get<UpdateService>());
            Assert.Equal(before, about.CheckForUpdates);
            about.CheckForUpdates = !before;
            Assert.Equal(!before, store.Settings.CheckForUpdates);
            Assert.True(about.CheckNowCommand.CanExecute(null));
        }
        finally
        {
            store.Settings.CheckForUpdates = before;
            store.SaveSettings();
        }
    }
}
