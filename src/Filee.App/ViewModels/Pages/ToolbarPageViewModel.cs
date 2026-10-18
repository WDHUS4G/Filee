// Donut toolbar editor: 1) choose a profile  2) arrange its presets on a live donut by drag & drop.
// The drag gestures themselves live in the view (ToolbarPage.axaml.cs); this class only mutates the model.
// A profile is either normal (extension chips, ExtensionTagsViewModel) or for mixed selections (check list,
// MixedExtensionsViewModel). Normal profiles are kept before the mixed ones, so a new mixed profile never takes over
// the catch-all role of the built-in "Mixed files" donut (see ProfileSelector).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ProfileItemViewModel(ToolbarProfile profile, ILocalizer loc) : ObservableObject
{
    public ToolbarProfile Profile { get; } = profile;

    [ObservableProperty] private string _name = loc.DisplayName(profile);

    public string Count => $"{Profile.PresetIds.Count}";

    /// <summary>A donut for mixed selections (marked in the list).</summary>
    public bool IsMixed => Profile.IsFallback;

    public void Refresh(ILocalizer l)
    {
        Name = l.DisplayName(Profile);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsMixed));
    }
}

/// <summary>A preset chip in the "available presets" palette.</summary>
public sealed record PaletteChip(string PresetId, string Label, string? Caption);

public sealed partial class ToolbarPageViewModel : ObservableObject
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly WindowService _windows;
    private bool _loading;
    private bool _repositioning;

    public ToolbarPageViewModel(UserDataStore store, ILocalizer loc, WindowService windows)
    {
        _store = store;
        _loc = loc;
        _windows = windows;
        // A profile is found by its name or by an extension it handles ("png" finds the photo donut).
        VisibleProfiles = new FilteredList<ProfileItemViewModel>(Profiles, p => [p.Name, .. p.Profile.Extensions]);
        VisiblePalette = new FilteredList<PaletteChip>(Palette, c => [c.Label, c.Caption]);
        foreach (var profile in store.Profiles)
            Profiles.Add(new ProfileItemViewModel(profile, loc));

        _loading = true;
        OuterRadius = store.Settings.Donut.OuterRadius;
        HoleRatio = store.Settings.Donut.HoleRatio * 100;
        SliceOpacity = store.Settings.Donut.Opacity * 100;
        _loading = false;

        SelectedProfile = Profiles.FirstOrDefault();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<PaletteChip> Palette { get; } = [];

    /// <summary>The profiles and the presets the search boxes above the two lists let through.</summary>
    public FilteredList<ProfileItemViewModel> VisibleProfiles { get; }
    public FilteredList<PaletteChip> VisiblePalette { get; }

    [ObservableProperty] private string _profileSearch = "";
    [ObservableProperty] private string _paletteSearch = "";

    partial void OnProfileSearchChanged(string value) => VisibleProfiles.Query = value;
    partial void OnPaletteSearchChanged(string value) => VisiblePalette.Query = value;

    [ObservableProperty] private ProfileItemViewModel? _selectedProfile;
    [ObservableProperty] private IReadOnlyList<DonutItem> _donutItems = [];
    [ObservableProperty] private string _centerTitle = "";
    [ObservableProperty] private string _centerSubtitle = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string? _message;

    /// <summary>The selected profile is for mixed selections (toggle in the profile card).</summary>
    [ObservableProperty] private bool _isMixed;

    /// <summary>False for the last mixed-selection profile: Filee always needs one.</summary>
    [ObservableProperty] private bool _canChangeMixed;

    [ObservableProperty] private bool _canDelete;

    /// <summary>Extension chips of a normal profile (null for a mixed one).</summary>
    [ObservableProperty] private ExtensionTagsViewModel? _extensionTags;

    /// <summary>Check list of a mixed-selection profile (null for a normal one).</summary>
    [ObservableProperty] private MixedExtensionsViewModel? _mixedExtensions;
    [ObservableProperty] private double _outerRadius;
    [ObservableProperty] private double _holeRatio;
    [ObservableProperty] private double _sliceOpacity;

    private ToolbarProfile? Profile => SelectedProfile?.Profile;

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        // The list box briefly drops its selection while the selected profile is moved (see Reposition).
        if (_repositioning)
            return;
        _loading = true;
        ProfileName = value is null ? "" : _loc.DisplayName(value.Profile);
        _loading = false;
        Message = null;
        RebuildProfileEditor();
        Rebuild();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_loading || Profile is null || string.IsNullOrWhiteSpace(value) || value == _loc.DisplayName(Profile))
            return;
        Profile.Name = value.Trim();
        SelectedProfile!.Refresh(_loc);
        CenterTitle = value.Trim();
        _store.SaveLibrary();
    }

    partial void OnIsMixedChanged(bool value)
    {
        if (_loading || SelectedProfile is not { } item || item.Profile.IsFallback == value)
            return;
        if (!value && !CanChangeMixed)
        {
            RebuildProfileEditor(); // the last mixed profile stays mixed
            return;
        }
        // The extensions are kept either way: chips become checks and back, so switching is harmless.
        item.Profile.IsFallback = value;
        Reposition(item);
        _store.SaveLibrary();
        item.Refresh(_loc);
        RebuildProfileEditor();
    }

    /// <summary>Normal profiles first, then the mixed ones; a profile that switches kind moves to the border.</summary>
    private void Reposition(ProfileItemViewModel item)
    {
        var profile = item.Profile;
        _store.Profiles.Remove(profile);
        var index = profile.IsFallback ? -1 : _store.Profiles.FindIndex(p => p.IsFallback);
        index = index < 0 ? _store.Profiles.Count : index;
        _store.Profiles.Insert(index, profile);

        _repositioning = true;
        try
        {
            Profiles.Move(Profiles.IndexOf(item), index);
            SelectedProfile = item;
        }
        finally
        {
            _repositioning = false;
        }
    }

    /// <summary>Shows the chips or the check list for the selected profile.</summary>
    private void RebuildProfileEditor()
    {
        var profile = Profile;
        var mixedCount = _store.Profiles.Count(p => p.IsFallback);
        _loading = true;
        IsMixed = profile?.IsFallback == true;
        _loading = false;
        CanChangeMixed = profile is not null && !(profile.IsFallback && mixedCount <= 1);
        CanDelete = CanChangeMixed;
        ExtensionTags = profile is { IsFallback: false } ? new ExtensionTagsViewModel(profile, _store.Profiles, _loc, _store.SaveLibrary) : null;
        MixedExtensions = profile is { IsFallback: true } ? new MixedExtensionsViewModel(profile, _store.Profiles, _loc, _store.SaveLibrary) : null;
    }

    partial void OnOuterRadiusChanged(double value) => SaveAppearance();
    partial void OnHoleRatioChanged(double value) => SaveAppearance();
    partial void OnSliceOpacityChanged(double value) => SaveAppearance();

    private void SaveAppearance()
    {
        if (_loading)
            return;
        _store.Settings.Donut.OuterRadius = Math.Round(OuterRadius);
        _store.Settings.Donut.HoleRatio = Math.Round(HoleRatio) / 100;
        _store.Settings.Donut.Opacity = Math.Round(SliceOpacity) / 100;
        _store.SaveSettings();
    }

    // ───────── Editing operations called by the view's drag & drop ─────────

    public int SliceCount => Profile?.PresetIds.Count ?? 0;

    /// <summary>Adds a palette preset at <paramref name="index"/> (clamped). Returns false when the donut is full.</summary>
    public bool Insert(string presetId, int index)
    {
        if (Profile is null || Profile.PresetIds.Contains(presetId))
            return false;
        if (Profile.PresetIds.Count >= ToolbarProfile.MaxSlices)
        {
            Message = _loc.Format("toolbar.max_reached", ToolbarProfile.MaxSlices);
            return false;
        }
        Profile.PresetIds.Insert(Math.Clamp(index, 0, Profile.PresetIds.Count), presetId);
        Commit();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (Profile is null || index < 0 || index >= Profile.PresetIds.Count)
            return;
        Profile.PresetIds.RemoveAt(index);
        Commit();
    }

    /// <summary>Moves a slice onto another slice's position.</summary>
    public void Move(int from, int to)
    {
        if (Profile is null || from == to || from < 0 || from >= Profile.PresetIds.Count)
            return;
        var id = Profile.PresetIds[from];
        Profile.PresetIds.RemoveAt(from);
        Profile.PresetIds.Insert(Math.Clamp(to, 0, Profile.PresetIds.Count), id);
        Commit();
    }

    /// <summary>Opens the preset popup for the slice at <paramref name="index"/>.</summary>
    public async Task EditAsync(int index)
    {
        if (Profile is null || index < 0 || index >= Profile.PresetIds.Count)
            return;
        var preset = _store.FindPreset(Profile.PresetIds[index]);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            Commit();
    }

    /// <summary>Opens the preset popup for a palette chip.</summary>
    public async Task EditChipAsync(string presetId)
    {
        var preset = _store.FindPreset(presetId);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            Commit();
    }

    /// <summary>
    /// A new group (a slice that fans out presets). It joins the available presets only: no donut gets it until the
    /// user drags it there. Groups head that list, and the search is cleared, so it is right there to drag.
    /// </summary>
    [RelayCommand]
    private async Task NewGroup()
    {
        var group = new Preset { Name = _loc["toolbar.new_group"], Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget };
        if (!await _windows.EditGroupAsync(group, _windows.Main, isNew: true))
            return;
        AddGroup(group);
    }

    /// <summary>Adds a group made in the editor to the available presets.</summary>
    internal void AddGroup(Preset group)
    {
        _store.Presets.Add(group);
        PaletteSearch = "";
        Commit();
    }

    [RelayCommand]
    private async Task NewPreset()
    {
        var preset = new Preset { Name = _loc["toolbar.new_preset"], TargetFormat = "png" };
        if (!await _windows.EditPresetAsync(preset, _windows.Main))
            return;
        _store.Presets.Add(preset);
        if (Profile is not null && Profile.PresetIds.Count < ToolbarProfile.MaxSlices)
            Profile.PresetIds.Add(preset.Id);
        Commit();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ToolbarProfile { Name = _loc["toolbar.profile_new"], PresetIds = ["to-pdf", "to-png"] };
        // Before the mixed-selection profiles (normal ones come first; see Reposition).
        var index = _store.Profiles.FindIndex(p => p.IsFallback);
        index = index < 0 ? _store.Profiles.Count : index;
        _store.Profiles.Insert(index, profile);
        _store.SaveLibrary();
        var item = new ProfileItemViewModel(profile, _loc);
        Profiles.Insert(index, item);
        SelectedProfile = item;
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (Profile is null || !CanDelete || _windows.Main is null)
            return;
        if (!await _windows.ConfirmAsync(_windows.Main, _loc["toolbar.delete_profile"] + "?", destructive: true))
            return;
        _store.Profiles.Remove(Profile);
        _store.SaveLibrary();
        Profiles.Remove(SelectedProfile!);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void Commit()
    {
        _store.SaveLibrary();
        // Every profile: deleting a group (or a preset) takes it off every donut, not just this one.
        foreach (var item in Profiles)
            item.Refresh(_loc);
        Rebuild();
    }

    /// <summary>Rebuilds the donut slices and the palette from the model.</summary>
    private void Rebuild()
    {
        var profile = Profile;
        var used = profile?.PresetIds ?? [];
        DonutItems = used
            .Select(_store.FindPreset)
            .OfType<Preset>()
            .Select(p => new DonutItem { Id = p.Id, Label = _loc.DisplayName(p), Caption = Caption(p) })
            .ToList();

        Palette.Clear();
        // Groups first: few, and otherwise at the very end, below every conversion.
        foreach (var preset in _store.Presets.Where(p => !used.Contains(p.Id)).OrderBy(p => !p.IsGroup))
        {
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            // "GIF  GIF" says nothing twice: only show the format when the name doesn't already.
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            Palette.Add(new PaletteChip(preset.Id, label, caption));
        }

        CenterTitle = profile is null ? "" : _loc.DisplayName(profile);
        CenterSubtitle = $"{used.Count} / {ToolbarProfile.MaxSlices}";
    }

    private string? Caption(Preset p) =>
        p.IsGroup ? _loc.Format("donut.group_caption", p.SubPresetIds.Count)
        : p.TargetFormat == BuiltInData.SameAsSource ? _loc["donut.same_format"] : FormatLabels.NameOf(_loc, p.TargetFormat);
}
