// The extension chips of a normal donut profile (Toolbar page): adds and removes extensions, saving immediately,
// and explains the ones that won't work as expected (another profile opens those files, or no engine reads them).

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Profiles;

namespace Filee.App.ViewModels.Pages;

/// <summary>An inline remark under the extension chips.</summary>
public sealed record ExtensionNote(string Text, bool IsWarning);

/// <summary>Backs the <see cref="TagEditor"/> of one normal (not mixed-selection) profile.</summary>
public sealed partial class ExtensionTagsViewModel : ObservableObject
{
    private readonly ToolbarProfile _profile;
    private readonly IReadOnlyList<ToolbarProfile> _profiles;
    private readonly ILocalizer _loc;
    private readonly Action _save;

    /// <param name="profile">The profile being edited.</param>
    /// <param name="profiles">All profiles in selection order (used to find who else has an extension).</param>
    /// <param name="save">Persists the library after every change.</param>
    public ExtensionTagsViewModel(ToolbarProfile profile, IReadOnlyList<ToolbarProfile> profiles, ILocalizer loc, Action save)
    {
        _profile = profile;
        _profiles = profiles;
        _loc = loc;
        _save = save;
        Refresh();
    }

    [ObservableProperty] private IReadOnlyList<TagChip> _tags = [];
    [ObservableProperty] private IReadOnlyList<ExtensionNote> _notes = [];
    [ObservableProperty] private IReadOnlyList<TagSuggestion> _suggestions = [];
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _inputError;

    partial void OnTextChanged(string value)
    {
        UpdateSuggestions();
        // Typing again dismisses the last complaint (clearing the box after a refused token must not).
        if (value.Length > 0)
            InputError = null;
    }

    /// <summary>Adds one typed token. Invalid and duplicate input is refused with an inline message.</summary>
    [RelayCommand]
    private void Add(string? token)
    {
        InputError = null;
        if (string.IsNullOrWhiteSpace(token))
            return;
        var extension = ExtensionInput.Normalize(token);
        if (extension is null)
        {
            InputError = _loc.Format("toolbar.ext_invalid", token.Trim());
            return;
        }
        if (_profile.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            InputError = _loc.Format("toolbar.ext_duplicate", "." + extension);
            return;
        }
        _profile.Extensions.Add(extension);
        _save();
        Refresh();
    }

    [RelayCommand]
    private void Remove(TagChip? chip)
    {
        if (chip is null || _profile.Extensions.RemoveAll(e => e.Equals(chip.Value, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        InputError = null;
        _save();
        Refresh();
    }

    /// <summary>The first other normal profile (selection order) that also has <paramref name="extension"/>.</summary>
    private ToolbarProfile? OwnerOf(string extension) =>
        _profiles.FirstOrDefault(p => !p.IsFallback && !ReferenceEquals(p, _profile) && p.Matches(extension));

    /// <summary>Rebuilds the chips and the remarks from the profile.</summary>
    private void Refresh()
    {
        var myIndex = IndexOf(_profile);
        var chips = new List<TagChip>();
        var claimed = new List<(ToolbarProfile Owner, string Text)>();
        var unknown = new List<string>();

        foreach (var extension in _profile.Extensions)
        {
            var text = "." + extension;
            if (OwnerOf(extension) is { } owner)
            {
                chips.Add(new TagChip(extension, text, TagChipKind.Warning, _loc.Format("toolbar.ext_also_in", _loc.DisplayName(owner))));
                claimed.Add((owner, text));
            }
            else if (FormatRegistry.FindByExtension(extension) is { } format)
                chips.Add(new TagChip(extension, text, TagChipKind.Normal, FormatLabels.NameAndCategory(_loc, format)));
            else
            {
                chips.Add(new TagChip(extension, text, TagChipKind.Muted, _loc["toolbar.ext_unknown_tip"]));
                unknown.Add(text);
            }
        }

        var notes = new List<ExtensionNote>();
        if (_profile.Extensions.Count == 0)
            notes.Add(new ExtensionNote(_loc["toolbar.ext_none"], false));
        // One line per other profile. Which one wins depends on the list order (ProfileSelector takes the first).
        foreach (var group in claimed.GroupBy(c => c.Owner))
        {
            var key = IndexOf(group.Key) < myIndex ? "toolbar.ext_claimed_first" : "toolbar.ext_claimed_later";
            notes.Add(new ExtensionNote(_loc.Format(key, string.Join(", ", group.Select(c => c.Text)), _loc.DisplayName(group.Key)), true));
        }
        if (unknown.Count > 0)
            notes.Add(new ExtensionNote(_loc.Format("toolbar.ext_unknown", string.Join(", ", unknown)), false));

        Tags = chips;
        Notes = notes;
        UpdateSuggestions();
    }

    private void UpdateSuggestions() =>
        Suggestions = ExtensionInput.Suggest(Text, _profile.Extensions, e => OwnerOf(e) is { } owner ? _loc.DisplayName(owner) : null, _loc);

    private int IndexOf(ToolbarProfile profile)
    {
        for (var i = 0; i < _profiles.Count; i++)
        {
            if (ReferenceEquals(_profiles[i], profile))
                return i;
        }
        return -1;
    }
}
