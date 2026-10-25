// Donut toolbar editor: 1) choose a profile  2) arrange its presets on a live donut by drag & drop, on the front face
// or on the back (Tab flips the open donut to it), picked with the Front / Back switch above the donut.
// The drag gestures themselves live in the view (ToolbarPage.axaml.cs); this class only mutates the model.
// A profile is either normal (extension chips, ExtensionTagsViewModel) or for mixed selections (check list,
// MixedExtensionsViewModel). Normal profiles are kept before the mixed ones, so a new mixed profile never takes over
// the catch-all role of the built-in "Mixed files" donut (see ProfileSelector).

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ProfileItemViewModel(ToolbarProfile profile, ILocalizer loc) : ObservableObject
{
    public ToolbarProfile Profile { get; } = profile;

    private ILocalizer _loc = loc;

    [ObservableProperty] private string _name = loc.DisplayName(profile);

    /// <summary>Slices on the front, and on the back when it has any (#425): "8" or "8 · 3".</summary>
    public string Count => Profile.BackPresetIds is { Count: > 0 } back ? $"{Profile.PresetIds.Count} · {back.Count}" : $"{Profile.PresetIds.Count}";

    /// <summary>What the two numbers are: "Front 8 · Back 3".</summary>
    public string CountTip =>
        $"{_loc["toolbar.face_front"]} {Profile.PresetIds.Count} · {_loc["toolbar.face_back"]} {Profile.BackPresetIds?.Count ?? 0}";

    /// <summary>A donut for mixed selections (marked in the list).</summary>
    public bool IsMixed => Profile.IsFallback;

    public void Refresh(ILocalizer l)
    {
        _loc = l;
        Name = l.DisplayName(Profile);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountTip));
        OnPropertyChanged(nameof(IsMixed));
    }
}

/// <summary>A preset chip in the "available presets" palette.</summary>
public enum PaletteTab { All, Image, Document, Media, Edit, Model3DCad, Map, Archive, Font }

public sealed record PaletteTabItem(PaletteTab Id, string Label);

public sealed record PaletteChip(string PresetId, string Label, string? Caption, string? Reason = null,
    PresetUse Use = PresetUse.Usable)
{
    public bool IsDimmed => Use != PresetUse.Usable;
    public bool CanAdd => Use != PresetUse.Unusable;
}

public sealed partial class ToolbarPageViewModel : ObservableObject, IDisposable
{
    internal static readonly (PaletteTab Id, string LabelKey, FormatCategory[] Categories)[] TabDefinitions =
    [
        (PaletteTab.All, "toolbar.tab.all", []),
        (PaletteTab.Image, "format.category.Image", [FormatCategory.Image, FormatCategory.Vector]),
        (PaletteTab.Document, "format.category.Document", [FormatCategory.Pdf, FormatCategory.Document,
            FormatCategory.Spreadsheet, FormatCategory.Presentation, FormatCategory.Hwp, FormatCategory.Text,
            FormatCategory.Ebook, FormatCategory.Data]),
        (PaletteTab.Media, "toolbar.tab.media", [FormatCategory.Video, FormatCategory.Audio, FormatCategory.Subtitle]),
        (PaletteTab.Edit, "toolbar.tab.edit", []),
        (PaletteTab.Model3DCad, "toolbar.tab.model3d_cad", [FormatCategory.Model3D, FormatCategory.Cad]),
        (PaletteTab.Map, "format.category.Geo", [FormatCategory.Geo]),
        (PaletteTab.Archive, "format.category.Archive", [FormatCategory.Archive]),
        (PaletteTab.Font, "format.category.Font", [FormatCategory.Font]),
    ];

    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly WindowService _windows;
    private readonly PresetAvailability _availability;
    private readonly ConverterCatalog _catalog;
    private readonly ILogger<ToolbarPageViewModel> _log;
    private sealed class PaletteJudging
    {
        public Dictionary<string, PresetJudgement> Judged { get; } = [];
        public PresetAvailability.PaletteSession? Session { get; set; }
    }

    private readonly Dictionary<string, PaletteJudging> _judgingByProfile = [];
    private object? _pass;
    private bool _disposed;
    private bool _savingOwnLibrary;
    private static readonly TimeSpan JudgeSlice = TimeSpan.FromMilliseconds(8);
    private bool _loading;
    private bool _repositioning;
    private string? _newPresetToSelect;
    private PaletteTab? _pendingTabRestore;
    private bool _updatingTabs;

    public ToolbarPageViewModel(UserDataStore store, ILocalizer loc, WindowService windows,
        PresetAvailability availability, ConverterCatalog catalog, ILogger<ToolbarPageViewModel> log)
    {
        _store = store;
        _loc = loc;
        _windows = windows;
        _availability = availability;
        _catalog = catalog;
        _log = log;
        // A profile is found by its name or by an extension it handles ("png" finds the photo donut).
        VisibleProfiles = new FilteredList<ProfileItemViewModel>(Profiles, p => [p.Name, .. p.Profile.Extensions]);
        foreach (var profile in store.Profiles)
            Profiles.Add(new ProfileItemViewModel(profile, loc));

        _loading = true;
        OuterRadius = store.Settings.Donut.OuterRadius;
        HoleRatio = store.Settings.Donut.HoleRatio * 100;
        SliceOpacity = Math.Clamp(store.Settings.Donut.Opacity, DonutSettings.MinOpacity, 1) * 100;
        _loading = false;

        SelectedProfile = Profiles.FirstOrDefault();

        // How the open donut flips, as set in Settings → Shortcuts: "Flip the open donut: Tab / mouse wheel".
        var ways = new List<string>();
        if (store.Settings.Donut.EffectiveFlipKey(store.Settings.Triggers) is { } key)
            ways.Add(KeyNames.Display(key));
        if (store.Settings.Donut.FlipWithWheel)
            ways.Add(loc["toolbar.face_hint_wheel"]);
        FaceHint = ways.Count == 0 ? loc["toolbar.face_hint_off"] : loc.Format("toolbar.face_hint", string.Join(" / ", ways));
        _catalog.Changed += OnCatalogChanged;
        _store.LibraryChanged += OnLibraryChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _catalog.Changed -= OnCatalogChanged;
        _store.LibraryChanged -= OnLibraryChanged;
        StopJudging();
        _judgingByProfile.Clear();
    }

    private void StopJudging()
    {
        _pass = null;
    }

    private void ForgetJudgements(string? profileId = null)
    {
        StopJudging();
        if (profileId is null)
            _judgingByProfile.Clear();
        else
            _judgingByProfile.Remove(profileId);
    }

    private void OnCatalogChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (_disposed) return; ForgetJudgements(); Rebuild(); });

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(InvalidateExternalLibrary);
            return;
        }
        if (!_savingOwnLibrary)
            InvalidateExternalLibrary();
    }

    private void InvalidateExternalLibrary()
    {
        if (_disposed)
            return;
        _pendingTabRestore ??= SelectedTab is { Id: not PaletteTab.All } tab ? tab.Id : null;
        ForgetJudgements();
        Rebuild();
    }

    private T SaveOwnLibrary<T>(Func<T> action)
    {
        var previous = _savingOwnLibrary;
        _savingOwnLibrary = true;
        try
        {
            DuringOwnSave?.Invoke();
            return action();
        }
        finally { _savingOwnLibrary = previous; }
    }

    private void SaveOwnLibrary() => SaveOwnLibrary(() => { _store.SaveLibrary(); return true; });

    internal Action? DuringOwnSave { get; set; }

    internal Action<string, string>? JudgementObserved { get; set; }
    internal bool HasJudgement(string profileId, string presetId) =>
        _judgingByProfile.TryGetValue(profileId, out var judging) && judging.Judged.ContainsKey(presetId);

    internal static PaletteTab TabOf(Preset preset)
    {
        if (preset.IsTool || preset.TargetFormat == BuiltInData.SameAsSource)
            return PaletteTab.Edit;
        var category = FormatRegistry.FindById(preset.TargetFormat)?.Category;
        return category is { } value
            ? TabDefinitions.First(t => t.Categories.Contains(value)).Id
            : PaletteTab.All;
    }

    /// <summary>Under the Front / Back switch: what flips the open donut (or that nothing does).</summary>
    public string FaceHint { get; }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<PaletteChip> Palette { get; } = [];
    public ObservableCollection<PaletteTabItem> Tabs { get; } = [];
    public ObservableCollection<PaletteChip> VisiblePalette { get; } = [];

    /// <summary>The profiles the search box above the list lets through.</summary>
    public FilteredList<ProfileItemViewModel> VisibleProfiles { get; }

    [ObservableProperty] private string _profileSearch = "";
    [ObservableProperty] private string _paletteSearch = "";
    [ObservableProperty] private PaletteTabItem? _selectedTab;
    [ObservableProperty] private bool _showTabRow;

    partial void OnProfileSearchChanged(string value) => VisibleProfiles.Query = value;
    partial void OnPaletteSearchChanged(string value)
    {
        _pendingTabRestore = null;
        Rebuild();
    }
    partial void OnSelectedTabChanged(PaletteTabItem? value)
    {
        if (!_updatingTabs)
            _pendingTabRestore = null;
        RefreshVisiblePalette();
    }

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

    /// <summary>The selected profile is a built-in donut, which "Reset to default" can put back (a new one can't).</summary>
    [ObservableProperty] private bool _canReset;

    /// <summary>Extension chips of a normal profile (null for a mixed one).</summary>
    [ObservableProperty] private ExtensionTagsViewModel? _extensionTags;

    /// <summary>The donut shows and edits the back face of the profile (the Front / Back switch).</summary>
    [ObservableProperty] private bool _editingBack;

    /// <summary>The front face is edited: the other half of the switch.</summary>
    public bool EditingFront
    {
        get => !EditingBack;
        set => EditingBack = !value;
    }

    /// <summary>Slices on the front face, for the switch.</summary>
    public int FrontCount => Profile?.PresetIds.Count ?? 0;

    /// <summary>Slices on the back face, for the switch.</summary>
    public int BackCount => Profile?.BackPresetIds?.Count ?? 0;

    /// <summary>Check list of a mixed-selection profile (null for a normal one).</summary>
    [ObservableProperty] private MixedExtensionsViewModel? _mixedExtensions;
    [ObservableProperty] private double _outerRadius;
    [ObservableProperty] private double _holeRatio;
    [ObservableProperty] private double _sliceOpacity;

    private ToolbarProfile? Profile => SelectedProfile?.Profile;

    /// <summary>The preset ids of the face being edited (null without a profile).</summary>
    private List<string>? Slices => Profile is not { } profile ? null
        : EditingBack ? (profile.BackPresetIds ??= []) : profile.PresetIds;

    partial void OnEditingBackChanged(bool value)
    {
        OnPropertyChanged(nameof(EditingFront));
        Message = null;
        Rebuild();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        // The list box briefly drops its selection while the selected profile is moved (see Reposition).
        if (_repositioning)
            return;
        _loading = true;
        ProfileName = value is null ? "" : _loc.DisplayName(value.Profile);
        _loading = false;
        Message = null;
        // A search typed for the last donut's presets would hide this one's (#427).
        PaletteSearch = "";
        RebuildProfileEditor();
        StopJudging();
        _newPresetToSelect = null;
        _pendingTabRestore = null;
        SelectedTab = null;
        Rebuild();
    }

    partial void OnProfileNameChanged(string value)
    {
        if (_loading || Profile is null || string.IsNullOrWhiteSpace(value) || value == _loc.DisplayName(Profile))
            return;
        Profile.Name = value.Trim();
        SelectedProfile!.Refresh(_loc);
        CenterTitle = value.Trim();
        SaveOwnLibrary();
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
        SaveOwnLibrary();
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
        CanReset = profile is not null && _store.CanResetProfile(profile);
        ExtensionTags = profile is { IsFallback: false } ? new ExtensionTagsViewModel(profile, _store.Profiles, _loc, OnExtensionsSaved) : null;
        MixedExtensions = profile is { IsFallback: true } ? new MixedExtensionsViewModel(profile, _store.Profiles, _loc, OnExtensionsSaved) : null;
    }

    private void OnExtensionsSaved()
    {
        SaveOwnLibrary();
        ForgetJudgements(Profile?.Id);
        Rebuild();
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

    /// <summary>Slices on the face being edited.</summary>
    public int SliceCount => Slices?.Count ?? 0;

    /// <summary>
    /// Adds a palette preset to the face being edited at <paramref name="index"/> (clamped). Returns false when that
    /// face is full.
    /// </summary>
    public bool Insert(string presetId, int index)
    {
        if (Slices is not { } slices || slices.Contains(presetId))
            return false;
        if (slices.Count >= ToolbarProfile.MaxSlices)
        {
            Message = _loc.Format("toolbar.max_reached", ToolbarProfile.MaxSlices);
            return false;
        }
        slices.Insert(Math.Clamp(index, 0, slices.Count), presetId);
        Commit();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (Slices is not { } slices || index < 0 || index >= slices.Count)
            return;
        slices.RemoveAt(index);
        Commit();
    }

    /// <summary>Moves a slice onto another slice's position.</summary>
    public void Move(int from, int to)
    {
        if (Slices is not { } slices || from == to || from < 0 || from >= slices.Count)
            return;
        var id = slices[from];
        slices.RemoveAt(from);
        slices.Insert(Math.Clamp(to, 0, slices.Count), id);
        Commit();
    }

    /// <summary>Opens the preset popup for the slice at <paramref name="index"/>.</summary>
    public async Task EditAsync(int index)
    {
        if (Slices is not { } slices || index < 0 || index >= slices.Count)
            return;
        var preset = _store.FindPreset(slices[index]);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            PresetEdited(preset);
    }

    /// <summary>Opens the preset popup for a palette chip.</summary>
    public async Task EditChipAsync(string presetId)
    {
        var preset = _store.FindPreset(presetId);
        if (preset is not null && await _windows.EditPresetAsync(preset, _windows.Main))
            PresetEdited(preset);
    }

    internal void PresetEdited(Preset preset)
    {
        StopJudging();
        foreach (var judging in _judgingByProfile.Values)
            judging.Judged.Remove(preset.Id);
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
        if (Slices is { Count: < ToolbarProfile.MaxSlices } slices)
            slices.Add(preset.Id);
        else
            _newPresetToSelect = preset.Id;
        Commit();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ToolbarProfile { Name = _loc["toolbar.profile_new"], PresetIds = ["to-pdf", "to-png"], BackPresetIds = [] };
        // Before the mixed-selection profiles (normal ones come first; see Reposition).
        var index = _store.Profiles.FindIndex(p => p.IsFallback);
        index = index < 0 ? _store.Profiles.Count : index;
        _store.Profiles.Insert(index, profile);
        SaveOwnLibrary();
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
        var id = Profile.Id;
        _judgingByProfile.Remove(id);
        StopJudging();
        SaveOwnLibrary();
        Profiles.Remove(SelectedProfile!);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ResetProfile()
    {
        if (SelectedProfile is not { } item || !CanReset || _windows.Main is null)
            return;
        if (!await _windows.ConfirmAsync(_windows.Main, _loc.Format("toolbar.reset_profile_confirm", item.Name), destructive: true))
            return;
        Reset(item);
    }

    /// <summary>Puts a built-in profile back to its defaults (see <see cref="UserDataStore.ResetProfile"/>).</summary>
    internal void Reset(ProfileItemViewModel item)
    {
        if (!SaveOwnLibrary(() => _store.ResetProfile(item.Profile)))
            return;
        // A profile that changed kind moved to the border between the normal and the mixed ones.
        var from = Profiles.IndexOf(item);
        var to = _store.Profiles.IndexOf(item.Profile);
        if (from != to)
        {
            _repositioning = true;
            try
            {
                Profiles.Move(from, to);
                SelectedProfile = item;
            }
            finally
            {
                _repositioning = false;
            }
        }
        _loading = true;
        ProfileName = _loc.DisplayName(item.Profile);
        _loading = false;
        Message = null;
        // Every profile: the extension hints of the others change with this one's extensions.
        foreach (var other in Profiles)
            other.Refresh(_loc);
        RebuildProfileEditor();
        ForgetJudgements(item.Profile.Id);
        Rebuild();
    }

    private void Commit()
    {
        SaveOwnLibrary();
        // Every profile: deleting a group (or a preset) takes it off every donut, not just this one.
        foreach (var item in Profiles)
            item.Refresh(_loc);
        Rebuild();
    }

    /// <summary>Rebuilds the donut slices (of the face being edited) and the palette from the model.</summary>
    private void Rebuild()
    {
        var profile = Profile;
        var used = Slices ?? [];
        DonutItems = used
            .Select(_store.FindPreset)
            .OfType<Preset>()
            .Select(p => new DonutItem { Id = p.Id, Label = _loc.DonutName(p), Caption = Caption(p) })
            .ToList();

        Palette.Clear();
        var placed = profile is null ? [] : profile.PresetIds.Concat(profile.BackPresetIds ?? []).ToHashSet();
        var judge = profile is { Extensions.Count: > 0 };
        _judgingByProfile.TryGetValue(profile?.Id ?? "", out var judging);
        var pending = false;
        var query = PaletteSearch.Trim();
        if (_newPresetToSelect is { } newId && (!judge || judging?.Judged.ContainsKey(newId) == true)
            && _store.FindPreset(newId) is { } newPreset)
        {
            var newName = _loc.DisplayName(newPreset);
            if (judge && judging!.Judged[newId].Use == PresetUse.Unusable && query != newName)
            {
                PaletteSearch = newName;
                return;
            }
            if (query.Length > 0 && !newName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && (Caption(newPreset)?.Contains(query, StringComparison.CurrentCultureIgnoreCase) != true))
            {
                PaletteSearch = "";
                return;
            }
        }
        var candidates = new List<(PaletteChip Chip, PaletteTab Tab, bool Group)>();
        // Groups first: few, and otherwise at the very end, below every conversion.
        foreach (var preset in _store.Presets.Where(p => !placed.Contains(p.Id)).OrderBy(p => !p.IsGroup))
        {
            string? reason = null;
            var use = PresetUse.Usable;
            if (judge && !preset.IsGroup)
            {
                if (judging is null || !judging.Judged.TryGetValue(preset.Id, out var judgement))
                {
                    pending = true;
                    continue;
                }
                use = judgement.Use;
                reason = judgement.Reason;
            }
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            // "GIF  GIF" says nothing twice: only show the format when the name doesn't already.
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            var chip = new PaletteChip(preset.Id, label, caption, reason, use);
            if (use != PresetUse.Unusable)
                Palette.Add(chip);
            if ((query.Length == 0 && use != PresetUse.Unusable)
                || (query.Length > 0 && (label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || caption?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)))
                candidates.Add((chip, preset.IsGroup ? PaletteTab.All : TabOf(preset), preset.IsGroup));
        }
        if (pending && _pass is null && !_disposed)
        {
            var pass = _pass = new object();
            var profileId = profile!.Id;
            Dispatcher.UIThread.Post(() => JudgeSome(pass, profileId), DispatcherPriority.Background);
        }
        var selected = SelectedTab?.Id ?? PaletteTab.All;
        var active = candidates.Where(c => !c.Group).Select(c => c.Tab).ToHashSet();
        var wanted = TabDefinitions.Where(t => t.Id == PaletteTab.All || active.Contains(t.Id)).ToList();
        _updatingTabs = true;
        try
        {
            for (var i = Tabs.Count - 1; i >= 0; i--)
                if (!wanted.Any(t => t.Id == Tabs[i].Id))
                    Tabs.RemoveAt(i);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < Tabs.Count && Tabs[i].Id == wanted[i].Id)
                    continue;
                var existing = Tabs.FirstOrDefault(t => t.Id == wanted[i].Id);
                if (existing is not null)
                    Tabs.Move(Tabs.IndexOf(existing), i);
                else
                    Tabs.Insert(i, new PaletteTabItem(wanted[i].Id, _loc[wanted[i].LabelKey]));
            }
            var allIds = candidates.Where(c => !c.Group).Select(c => c.Chip.PresetId).ToHashSet();
            ShowTabRow = Tabs.Count > 2 || (Tabs.Count == 2
                && !allIds.SetEquals(candidates.Where(c => !c.Group && c.Tab == Tabs[1].Id)
                    .Select(c => c.Chip.PresetId)));
            if (_newPresetToSelect is { } addedId && candidates.FirstOrDefault(c => c.Chip.PresetId == addedId) is { Chip: not null } added)
            {
                if (added.Chip.Use == PresetUse.Unusable && query != added.Chip.Label)
                {
                    PaletteSearch = added.Chip.Label;
                    return;
                }
                _newPresetToSelect = null;
                selected = added.Tab;
            }
            if (!pending && _pendingTabRestore is { } restore)
            {
                selected = restore;
                _pendingTabRestore = null;
            }
            if (!ShowTabRow || !wanted.Any(t => t.Id == selected))
                selected = PaletteTab.All;
            SelectedTab = Tabs.First(t => t.Id == selected);
        }
        finally
        {
            _updatingTabs = false;
        }
        RefreshVisiblePalette(candidates);

        CenterTitle = profile is null ? "" : _loc.DisplayName(profile);
        CenterSubtitle = EditingBack
            ? $"{_loc["toolbar.face_back"]} · {used.Count} / {ToolbarProfile.MaxSlices}"
            : $"{used.Count} / {ToolbarProfile.MaxSlices}";
        OnPropertyChanged(nameof(FrontCount));
        OnPropertyChanged(nameof(BackCount));
    }

    private void RefreshVisiblePalette()
    {
        var query = PaletteSearch.Trim();
        var placed = Profile is null ? [] : Profile.PresetIds.Concat(Profile.BackPresetIds ?? []).ToHashSet();
        _judgingByProfile.TryGetValue(Profile?.Id ?? "", out var judging);
        var candidates = new List<(PaletteChip Chip, PaletteTab Tab, bool Group)>();
        foreach (var preset in _store.Presets.Where(p => !placed.Contains(p.Id)).OrderBy(p => !p.IsGroup))
        {
            var judgement = judging?.Judged.GetValueOrDefault(preset.Id) ?? new(PresetUse.Usable, null);
            if (Profile is { Extensions.Count: > 0 } && !preset.IsGroup && !HasJudgement(Profile.Id, preset.Id))
                continue;
            if (judgement.Use == PresetUse.Unusable && query.Length == 0)
                continue;
            var label = _loc.DisplayName(preset);
            var caption = Caption(preset);
            if (caption is not null && label.Contains(caption, StringComparison.OrdinalIgnoreCase))
                caption = null;
            if (query.Length > 0 && !label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && caption?.Contains(query, StringComparison.CurrentCultureIgnoreCase) != true)
                continue;
            candidates.Add((new PaletteChip(preset.Id, label, caption, judgement.Reason, judgement.Use),
                preset.IsGroup ? PaletteTab.All : TabOf(preset), preset.IsGroup));
        }
        RefreshVisiblePalette(candidates);
    }

    private void RefreshVisiblePalette(List<(PaletteChip Chip, PaletteTab Tab, bool Group)> candidates)
    {
        var tab = SelectedTab?.Id ?? PaletteTab.All;
        var wanted = candidates.Where(c => c.Group || tab == PaletteTab.All || c.Tab == tab).Select(c => c.Chip).ToList();
        VisiblePalette.Clear();
        foreach (var chip in wanted)
            VisiblePalette.Add(chip);
    }

    private void JudgeSome(object pass, string profileId)
    {
        if (pass != _pass || _disposed || Profile?.Id != profileId)
            return;
        var profile = Profile;
        if (!_judgingByProfile.TryGetValue(profileId, out var judging))
            _judgingByProfile[profileId] = judging = new PaletteJudging();
        var placed = profile.PresetIds.Concat(profile.BackPresetIds ?? []).ToHashSet();
        var pending = _store.Presets.Where(p => !p.IsGroup && !placed.Contains(p.Id) && !judging.Judged.ContainsKey(p.Id)).ToList();
        if (pending.Count > 0)
        {
            judging.Session ??= _availability.ForPalette(profile.Extensions.Select(FormatRegistry.FindByExtension)
                .OfType<FileFormat>().DistinctBy(f => f.Id).ToList());
            var clock = Stopwatch.StartNew();
            foreach (var preset in pending)
            {
                judging.Judged[preset.Id] = JudgeOne(judging.Session, preset);
                JudgementObserved?.Invoke(profileId, preset.Id);
                if (clock.Elapsed >= JudgeSlice)
                    break;
            }
        }
        if (_store.Presets.Any(p => !p.IsGroup && !placed.Contains(p.Id) && !judging.Judged.ContainsKey(p.Id)))
            Dispatcher.UIThread.Post(() => JudgeSome(pass, profileId), DispatcherPriority.Background);
        else
        {
            _pass = null;
            Rebuild();
        }
    }

    // A preset whose judging throws stays usable (not dimmed) and the rest go on: this runs as a dispatcher job, where an
    // exception would end Filee.
    private PresetJudgement JudgeOne(PresetAvailability.PaletteSession session, Preset preset)
    {
        try
        {
            return session.Judge(preset);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Judging preset {Preset} for the palette failed", preset.Id);
            return new PresetJudgement(PresetUse.Usable, null);
        }
    }

    private string? Caption(Preset p) =>
        p.IsGroup ? _loc.Format("donut.group_caption", p.SubPresetIds.Count)
        : p.TargetFormat == BuiltInData.SameAsSource ? _loc["donut.same_format"] : FormatLabels.NameOf(_loc, p.TargetFormat);
}
