// Editor for a donut group (Settings → Toolbar → New group, or right-click on a group): its name and the presets
// its slice fans out. Works on copies; Apply() writes them back into the group (Cancel = just drop the VM).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.ViewModels;

/// <summary>One preset in a group, in fan-out order.</summary>
public sealed record GroupMember(string PresetId, string Label, string? Caption);

public sealed partial class GroupEditorViewModel : ObservableObject
{
    private readonly Preset _group;
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;

    /// <param name="isNew">A group not yet in the store: it can't be deleted, only not saved.</param>
    public GroupEditorViewModel(Preset group, UserDataStore store, ILocalizer loc, bool isNew = false)
    {
        _group = group;
        _store = store;
        _loc = loc;
        IsNew = isNew;
        _name = loc.DisplayName(group);
        foreach (var id in group.SubPresetIds)
        {
            if (store.FindPreset(id) is { IsGroup: false } preset)
                Members.Add(MemberOf(preset));
        }
        Members.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    public bool IsNew { get; }

    public bool CanDelete => !IsNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(NameError))]
    private string _name;

    /// <summary>The presets the group fans out, in order.</summary>
    public ObservableCollection<GroupMember> Members { get; } = [];

    /// <summary>Actions that can still be added (not groups, not already in it).</summary>
    public ObservableCollection<Choice<string>> Available { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private Choice<string>? _toAdd;

    public bool IsFull => Members.Count >= Preset.MaxSubPresets;

    public bool IsEmpty => Members.Count == 0;

    /// <summary>"Presets (2 / 6)".</summary>
    public string MembersTitle => _loc.Format("group.members", Members.Count, Preset.MaxSubPresets);

    /// <summary>"A group holds up to 6 presets."</summary>
    public string FullNote => _loc.Format("group.full", Preset.MaxSubPresets);

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? _loc["presets.name_required"] : null;

    /// <summary>A name and at least one preset: a group without presets would fan out nothing.</summary>
    public bool IsValid => NameError is null && Members.Count > 0;

    /// <summary>Set by <see cref="Delete"/>: the group was removed from the store and its donuts.</summary>
    public bool Deleted { get; private set; }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (ToAdd is null || _store.FindPreset(ToAdd.Value) is not { } preset)
            return;
        Members.Add(MemberOf(preset));
        ToAdd = null;
    }

    private bool CanAdd() => ToAdd is not null && !IsFull;

    [RelayCommand]
    private void Remove(GroupMember member) => Members.Remove(member);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp(GroupMember member) => Move(member, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown(GroupMember member) => Move(member, 1);

    // The first can't go up, the last can't go down: their buttons are disabled rather than doing nothing.
    private bool CanMoveUp(GroupMember? member) => member is not null && Members.IndexOf(member) > 0;

    private bool CanMoveDown(GroupMember? member) =>
        member is not null && Members.IndexOf(member) is var index && index >= 0 && index < Members.Count - 1;

    private void Move(GroupMember member, int by)
    {
        var from = Members.IndexOf(member);
        var to = from + by;
        if (from >= 0 && to >= 0 && to < Members.Count)
            Members.Move(from, to);
    }

    /// <summary>Copies the edits into the group. Returns false (and changes nothing) when they aren't valid.</summary>
    public bool Apply()
    {
        if (!IsValid)
            return false;
        // Keep a built-in group's translatable name unless the user actually renamed it.
        if (_group.NameKey is null || Name.Trim() != _loc.DisplayName(_group))
            _group.Name = Name.Trim();
        _group.Kind = PresetKind.Group;
        _group.TargetFormat = Preset.GroupTarget;
        _group.SubPresetIds = Members.Select(m => m.PresetId).ToList();
        return true;
    }

    /// <summary>Removes the group from the store and from every donut, front and back (its presets stay).</summary>
    public void Delete()
    {
        _store.Presets.Remove(_group);
        foreach (var profile in _store.Profiles)
        {
            profile.PresetIds.Remove(_group.Id);
            profile.BackPresetIds?.Remove(_group.Id);
        }
        Deleted = true;
    }

    /// <summary>Names the group as saved, not as half-renamed in the box.</summary>
    public string DeleteConfirmation => _loc.Format("group.delete_confirm", _loc.DisplayName(_group));

    private void Refresh()
    {
        var taken = Members.Select(m => m.PresetId).ToHashSet();
        Available.Clear();
        foreach (var preset in _store.Presets.Where(p => !p.IsGroup && !taken.Contains(p.Id)))
            Available.Add(new Choice<string>(preset.Id, _loc.DisplayName(preset), CaptionOf(preset)));
        OnPropertyChanged(nameof(IsFull));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(MembersTitle));
        OnPropertyChanged(nameof(IsValid));
        AddCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private GroupMember MemberOf(Preset preset) => new(preset.Id, _loc.DisplayName(preset), CaptionOf(preset));

    private string? CaptionOf(Preset preset) =>
        preset.TargetFormat == BuiltInData.SameAsSource ? _loc["donut.same_format"] : FormatLabels.NameOf(_loc, preset.TargetFormat);
}
