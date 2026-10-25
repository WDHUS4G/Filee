// Preset list + the shared PresetEditorView on the right.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class PresetListItem(Preset preset, ILocalizer loc) : ObservableObject
{
    public Preset Preset { get; } = preset;

    [ObservableProperty] private string _name = loc.DisplayName(preset);
    [ObservableProperty] private string _target = TargetLabel(preset, loc);

    public void Refresh(ILocalizer l)
    {
        Name = l.DisplayName(Preset);
        Target = TargetLabel(Preset, l);
    }

    private static string TargetLabel(Preset p, ILocalizer l) =>
        p.TargetFormat == BuiltInData.SameAsSource ? l["presets.same_as_source"]
        : "→ " + (FormatLabels.NameOf(l, p.TargetFormat) ?? p.TargetFormat);
}

/// <summary>Pages that fill the window height and scroll their own parts instead of scrolling as a whole.</summary>
public interface IFitsWindowHeight;

public sealed partial class PresetsPageViewModel : ObservableObject, IFitsWindowHeight
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly WindowService _windows;

    public PresetsPageViewModel(UserDataStore store, ILocalizer loc, WindowService windows)
    {
        _store = store;
        _loc = loc;
        _windows = windows;
        VisiblePresets = new FilteredList<PresetListItem>(Presets, p => [p.Name, p.Target, p.Preset.TargetFormat]);
        // Groups and tools convert nothing: they live on the donut only (Settings → Toolbar).
        foreach (var preset in store.Presets.Where(p => !p.IsGroup && !p.IsTool))
            Presets.Add(new PresetListItem(preset, loc));
        SelectedPreset = Presets.FirstOrDefault();
    }

    public ObservableCollection<PresetListItem> Presets { get; } = [];

    /// <summary>The presets the search box above the list lets through (name or target format).</summary>
    public FilteredList<PresetListItem> VisiblePresets { get; }

    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value) => VisiblePresets.Query = value;

    [ObservableProperty] private PresetListItem? _selectedPreset;
    [ObservableProperty] private PresetEditorViewModel? _editor;
    [ObservableProperty] private string? _savedMessage;

    partial void OnSelectedPresetChanged(PresetListItem? value)
    {
        Editor = value is null ? null : new PresetEditorViewModel(value.Preset, _loc);
        SavedMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (Editor is null || SelectedPreset is null || !Editor.Apply())
            return;
        _store.SaveLibrary();
        SelectedPreset.Refresh(_loc);
        SavedMessage = "✓";
    }

    [RelayCommand]
    private void Revert() => OnSelectedPresetChanged(SelectedPreset);

    [RelayCommand]
    private void Add() => AddPreset(new Preset { Name = _loc["toolbar.new_preset"], TargetFormat = "png" });

    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedPreset is null)
            return;
        var copy = SelectedPreset.Preset.Clone();
        copy.Id = Preset.NewId();
        copy.Name = _loc.DisplayName(SelectedPreset.Preset) + " 2";
        copy.NameKey = null;
        AddPreset(copy);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedPreset is null || _windows.Main is null)
            return;
        var message = _loc.Format("presets.delete_confirm", SelectedPreset.Name);
        if (!await _windows.ConfirmAsync(_windows.Main, message, destructive: true))
            return;

        var index = Presets.IndexOf(SelectedPreset);
        _store.Presets.Remove(SelectedPreset.Preset);
        _store.SaveLibrary(); // also removes it from every profile
        Presets.Remove(SelectedPreset);
        SelectedPreset = Presets.Count == 0 ? null : Presets[Math.Clamp(index, 0, Presets.Count - 1)];
    }

    private void AddPreset(Preset preset)
    {
        _store.Presets.Add(preset);
        _store.SaveLibrary();
        var item = new PresetListItem(preset, _loc);
        // Clear the search filter first so the new preset is visible regardless of what was typed.
        Search = "";
        Presets.Add(item);
        SelectedPreset = item;
    }
}
