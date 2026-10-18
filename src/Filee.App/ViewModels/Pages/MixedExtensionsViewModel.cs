// The editor of a mixed-selection donut ("Mixed files"): which extensions a mix of files may contain for this donut
// to open, as a check list grouped by the normal profiles. Nothing checked = any mix no other donut takes.
// Checks are stored in the profile's Extensions and saved immediately; ProfileSelector explains the rules.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Profiles;

namespace Filee.App.ViewModels.Pages;

/// <summary>Backs the check list shown instead of the extension chips for a mixed-selection profile.</summary>
public sealed partial class MixedExtensionsViewModel : ObservableObject
{
    private readonly ToolbarProfile _profile;
    private readonly IReadOnlyList<ToolbarProfile> _profiles;
    private readonly ILocalizer _loc;
    private readonly Action _save;

    /// <param name="profile">The mixed-selection profile being edited.</param>
    /// <param name="profiles">All profiles in selection order; every normal one becomes a group.</param>
    /// <param name="save">Persists the library after every change.</param>
    public MixedExtensionsViewModel(ToolbarProfile profile, IReadOnlyList<ToolbarProfile> profiles, ILocalizer loc, Action save)
    {
        _profile = profile;
        _profiles = profiles;
        _loc = loc;
        _save = save;

        var groups = profiles
            .Where(p => !p.IsFallback)
            .Select(p => new ExtensionGroupViewModel(this, loc.DisplayName(p), p.Extensions))
            .Where(g => g.Extensions.Count > 0)
            .ToList();
        // Checked extensions no normal profile has (removed from it later, or never there) stay visible so they
        // can be unchecked.
        var grouped = profiles.Where(p => !p.IsFallback).SelectMany(p => p.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = profile.Extensions.Where(e => !grouped.Contains(e)).ToList();
        if (others.Count > 0)
            groups.Add(new ExtensionGroupViewModel(this, loc["toolbar.mixed_other_group"], others));
        Groups = groups;
        UpdateHint();
    }

    public IReadOnlyList<ExtensionGroupViewModel> Groups { get; }

    /// <summary>What this donut is used for with the current checks, in words.</summary>
    [ObservableProperty] private string _hint = "";

    /// <summary>True when the checks make this donut unreachable.</summary>
    [ObservableProperty] private bool _hintIsWarning;

    internal ILocalizer Localizer => _loc;

    internal bool IsChecked(string extension) => _profile.Matches(extension);

    /// <summary>Checks or unchecks extensions, saves, and updates every group showing them.</summary>
    internal void Set(IEnumerable<string> extensions, bool check)
    {
        var changed = false;
        foreach (var extension in extensions)
        {
            if (check && !_profile.Matches(extension))
            {
                _profile.Extensions.Add(extension.ToLowerInvariant());
                changed = true;
            }
            else if (!check)
                changed |= _profile.Extensions.RemoveAll(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (!changed)
            return;
        _save();
        // An extension can sit in two groups (two profiles have it): keep both in step.
        foreach (var group in Groups)
            group.Sync();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var catchAll = ProfileSelector.CatchAll(_profiles);
        var catchAllName = catchAll is null ? "" : _loc.DisplayName(catchAll);
        var isCatchAll = ReferenceEquals(catchAll, _profile);
        // A mixed profile listed earlier that allows everything checked here takes all of this one's selections.
        var covering = _profile.Extensions.Count == 0 ? null : _profiles
            .TakeWhile(p => !ReferenceEquals(p, _profile))
            .FirstOrDefault(p => p.IsFallback && p.Extensions.Count > 0 && _profile.Extensions.All(p.Matches));

        (Hint, HintIsWarning) = (_profile.Extensions.Count, isCatchAll, covering) switch
        {
            (0, true, _) => (_loc["toolbar.mixed_catch_all"], false),
            (0, false, _) => (_loc.Format("toolbar.mixed_shadowed", catchAllName), true),
            (_, _, { } earlier) => (_loc.Format("toolbar.mixed_covered", _loc.DisplayName(earlier)), true),
            (_, true, _) => (_loc["toolbar.mixed_others_self"], false),
            _ => (_loc.Format("toolbar.mixed_others", catchAllName), false),
        };
    }
}

/// <summary>The extensions of one normal profile in the mixed-selection check list.</summary>
public sealed partial class ExtensionGroupViewModel : ObservableObject
{
    private readonly MixedExtensionsViewModel _owner;

    internal ExtensionGroupViewModel(MixedExtensionsViewModel owner, string name, IEnumerable<string> extensions)
    {
        _owner = owner;
        Name = name;
        Extensions = extensions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(e => new ExtensionToggleViewModel(owner, e, owner.IsChecked(e)))
            .ToList();
    }

    public string Name { get; }

    public IReadOnlyList<ExtensionToggleViewModel> Extensions { get; }

    /// <summary>Collapsed by default: the group check box and the count tell the state.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>True = all checked, false = none, null = some.</summary>
    public bool? State
    {
        get
        {
            var count = CheckedCount;
            return count == 0 ? false : count == Extensions.Count ? true : null;
        }
    }

    /// <summary>"3 / 41".</summary>
    public string Count => $"{CheckedCount} / {Extensions.Count}";

    private int CheckedCount => Extensions.Count(e => e.IsChecked);

    /// <summary>The group check box: all checked → none; otherwise → all.</summary>
    [RelayCommand]
    private void ToggleAll() => _owner.Set(Extensions.Select(e => e.Extension), State != true);

    internal void Sync()
    {
        foreach (var toggle in Extensions)
            toggle.Sync();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>One extension chip of the mixed-selection check list.</summary>
public sealed partial class ExtensionToggleViewModel : ObservableObject
{
    private readonly MixedExtensionsViewModel _owner;
    private bool _isChecked;

    internal ExtensionToggleViewModel(MixedExtensionsViewModel owner, string extension, bool isChecked)
    {
        _owner = owner;
        _isChecked = isChecked;
        Extension = extension;
        ToolTip = FormatRegistry.FindByExtension(extension) is { } format
            ? FormatLabels.NameAndCategory(owner.Localizer, format)
            : owner.Localizer["toolbar.ext_unknown_tip"];
    }

    public string Extension { get; }

    public string Text => "." + Extension;

    public string ToolTip { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
            _owner.Set([Extension], value);
        }
    }

    /// <summary>Re-reads the state from the profile without writing it back.</summary>
    internal void Sync()
    {
        var value = _owner.IsChecked(Extension);
        if (_isChecked == value)
            return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }
}
