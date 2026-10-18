// Main (settings) window: left navigation + current page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.ViewModels;

/// <summary>Navigation entry. <see cref="Title"/> follows the UI language.</summary>
public sealed partial class NavItem(string key, string titleKey, string iconKey, Type pageType) : ObservableObject
{
    public string Key { get; } = key;
    public string TitleKey { get; } = titleKey;
    public Geometry? Icon { get; } = Application.Current?.FindResource(iconKey) as Geometry;
    public Type PageType { get; } = pageType;

    [ObservableProperty] private string _title = "";

    public void Refresh(ILocalizer loc) => Title = loc[TitleKey];
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ILocalizer _loc;

    /// <summary>The repository, for the GitHub button above the version.</summary>
    public string RepositoryUrl => UpdateService.RepositoryUrl;

    public MainWindowViewModel(IServiceProvider services, ILocalizer loc, UpdateService updates)
    {
        _services = services;
        _loc = loc;
        Updates = updates;
        updates.PropertyChanged += (_, e) =>
        {
            if (UpdateTexts.Affects(e.PropertyName))
                RefreshVersionTexts();
        };
        RefreshVersionTexts();
        Items =
        [
            new("home", "nav.home", "Icon.Home", typeof(HomePageViewModel)),
            new("general", "nav.general", "Icon.Settings", typeof(GeneralPageViewModel)),
            new("triggers", "nav.triggers", "Icon.Keyboard", typeof(TriggersPageViewModel)),
            new("toolbar", "nav.toolbar", "Icon.Donut", typeof(ToolbarPageViewModel)),
            new("presets", "nav.presets", "Icon.Tune", typeof(PresetsPageViewModel)),
            new("watch", "nav.watch", "Icon.Folder", typeof(WatchFoldersPageViewModel)),
            new("theme", "nav.theme", "Icon.Palette", typeof(ThemePageViewModel)),
            new("engines", "nav.engines", "Icon.Engine", typeof(EnginesPageViewModel)),
            new("about", "nav.about", "Icon.Info", typeof(AboutPageViewModel)),
        ];
        foreach (var item in Items)
            item.Refresh(loc);
        loc.LanguageChanged += (_, _) =>
        {
            foreach (var item in Items)
                item.Refresh(loc);
            RefreshVersionTexts();
            CurrentPage = CreatePage(SelectedItem); // rebuild so code-generated labels update too
        };
        _selectedItem = Items[0];
        _currentPage = CreatePage(Items[0]);
    }

    public IReadOnlyList<NavItem> Items { get; }

    /// <summary>Update state for the bottom of the navigation (version, "update available" button).</summary>
    public UpdateService Updates { get; }

    [ObservableProperty] private NavItem _selectedItem;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageScroll))]
    private ObservableObject _currentPage;

    /// <summary>
    /// Pages scroll as a whole, except pages that fit the window height and scroll their own parts
    /// (<see cref="IFitsWindowHeight"/>, e.g. the presets list and editor).
    /// </summary>
    public ScrollBarVisibility PageScroll => CurrentPage is IFitsWindowHeight ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateTip = "";

    /// <summary>Downloads and runs the new installer (or opens the download page in a portable copy).</summary>
    [RelayCommand]
    private Task DownloadUpdate() => Updates.UpdateAsync();

    private void RefreshVersionTexts()
    {
        VersionText = _loc.Format("about.version", UpdateService.CurrentVersion);
        UpdateText = UpdateTexts.Short(Updates, _loc);
        var changes = UpdateTexts.Changes(Updates, _loc);
        UpdateTip = UpdateTexts.Status(Updates, _loc)
                    ?? $"{_loc[$"update.level.{Updates.Level}"]}{Environment.NewLine}{(changes.Length > 0 ? changes + Environment.NewLine : "")}{_loc[UpdateTexts.HowKey]}";
    }

    public void Navigate(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is not null)
            SelectedItem = item;
    }

    // Pages are recreated on every visit so they always reflect the latest settings.
    partial void OnSelectedItemChanged(NavItem value) => CurrentPage = CreatePage(value);

    // Old pages may hold event subscriptions on long-lived services.
    partial void OnCurrentPageChanged(ObservableObject? oldValue, ObservableObject newValue) => (oldValue as IDisposable)?.Dispose();

    private ObservableObject CreatePage(NavItem item) =>
        (ObservableObject)ActivatorUtilities.CreateInstance(_services, item.PageType);
}
