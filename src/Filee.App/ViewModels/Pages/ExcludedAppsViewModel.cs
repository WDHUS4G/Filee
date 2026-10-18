// "Never trigger in these apps" on the Shortcuts page: the apps as tags. A name is typed (the box suggests the apps
// running now) or picked from the list of apps that have a window open; a tag's ✕ removes it. The names are the ones
// TriggerService compares with the window under the cursor (process name, without ".exe").

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.Core.Platform;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ExcludedAppsViewModel : ObservableObject
{
    private readonly List<string> _excluded;
    private readonly IPlatformServices? _platform;
    private readonly Action _save;
    private IReadOnlyList<string> _running = [];
    private bool _loaded;

    /// <param name="excluded">The settings' list, changed in place.</param>
    /// <param name="save">Persists the settings after every change.</param>
    public ExcludedAppsViewModel(List<string> excluded, IPlatformServices? platform, Action save)
    {
        _excluded = excluded;
        _platform = platform;
        _save = save;
        VisibleRunningApps = new FilteredList<string>(RunningApps, name => [name]);
        Refresh();
    }

    [ObservableProperty] private IReadOnlyList<TagChip> _tags = [];
    [ObservableProperty] private IReadOnlyList<TagSuggestion> _suggestions = [];
    [ObservableProperty] private string _text = "";

    /// <summary>Apps with a window open now, not excluded yet (the "pick a running app" list).</summary>
    public ObservableCollection<string> RunningApps { get; } = [];

    public FilteredList<string> VisibleRunningApps { get; }

    /// <summary>The list was read and has nothing to offer.</summary>
    [ObservableProperty] private bool _noRunningApps;

    [ObservableProperty] private string _runningSearch = "";

    partial void OnRunningSearchChanged(string value) => VisibleRunningApps.Query = value;

    partial void OnTextChanged(string value) => UpdateSuggestions();

    /// <summary>Reads the running apps again (when the list opens, and when typing starts).</summary>
    [RelayCommand]
    public void LoadRunningApps()
    {
        _loaded = true;
        try
        {
            _running = _platform?.RunningAppNames() ?? [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _running = [];
        }
        RefreshRunning();
    }

    /// <summary>Adds a typed or picked app name.</summary>
    [RelayCommand]
    private void Add(string? name)
    {
        var app = Normalize(name);
        if (app is null || _excluded.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            Text = "";
            return;
        }
        _excluded.Add(app);
        _save();
        Text = "";
        Refresh();
    }

    [RelayCommand]
    private void Remove(TagChip? chip)
    {
        if (chip is null || _excluded.RemoveAll(n => n.Equals(chip.Value, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        _save();
        Refresh();
    }

    /// <summary>"Photoshop.exe " → "Photoshop": the name TriggerService compares with.</summary>
    internal static string? Normalize(string? name)
    {
        var app = name?.Trim();
        if (string.IsNullOrEmpty(app))
            return null;
        if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            app = app[..^4];
        return app.Length == 0 ? null : app;
    }

    private void Refresh()
    {
        Tags = [.. _excluded.Select(name => new TagChip(name, name))];
        RefreshRunning();
    }

    private void RefreshRunning()
    {
        var available = _running.Where(n => !_excluded.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!available.SequenceEqual(RunningApps))
        {
            RunningApps.Clear();
            foreach (var name in available)
                RunningApps.Add(name);
        }
        NoRunningApps = _loaded && RunningApps.Count == 0;
        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        var typed = Text.Trim();
        if (typed.Length > 0 && !_loaded)
            LoadRunningApps(); // first keystroke: read the running apps once
        Suggestions = typed.Length == 0 ? []
            : [.. RunningApps.Where(n => n.Contains(typed, StringComparison.CurrentCultureIgnoreCase)).Take(12).Select(n => new TagSuggestion(n, n))];
    }
}
