// A toolbar profile decides which presets the donut shows for a given set of source files.

using Filee.Core.Formats;

namespace Filee.Core.Profiles;

/// <summary>
/// A donut toolbar layout for one family of source files (e.g. "Images" for jpg/png/...).
/// </summary>
public sealed class ToolbarProfile
{
    /// <summary>Maximum number of slices on one donut ring.</summary>
    public const int MaxSlices = 12;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>User-defined name. When empty, <see cref="NameKey"/> is localized instead.</summary>
    public string Name { get; set; } = "";

    public string? NameKey { get; set; }

    /// <summary>
    /// Source extensions (without dot). For a normal profile: the files it is used for. For a mixed-selection profile
    /// (<see cref="IsFallback"/>): the extensions a mixed selection must stay within ("every selected file is one
    /// of ..."); empty means any mixed selection.
    /// </summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// A "mixed files" profile: used when the dropped files span several profiles (or match none). Several may exist,
    /// each limited to its <see cref="Extensions"/>; one with no extensions takes every other mixed selection
    /// (see <see cref="ProfileSelector.Select"/>). The name is kept for compatibility with saved profiles.
    /// </summary>
    public bool IsFallback { get; set; }

    /// <summary>Preset ids in donut order, clockwise starting at 12 o'clock.</summary>
    public List<string> PresetIds { get; set; } = [];

    /// <summary>Returns true if the extension (with or without dot) belongs to this profile.</summary>
    public bool Matches(string extension) =>
        Extensions.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public ToolbarProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        Extensions = [.. Extensions],
        IsFallback = IsFallback,
        PresetIds = [.. PresetIds],
    };
}

/// <summary>Chooses the profile for a set of dropped files.</summary>
public static class ProfileSelector
{
    /// <summary>
    /// Picks the profile for the given files, in this order:
    /// <list type="number">
    /// <item>the first normal profile that has every file's extension;</item>
    /// <item>otherwise the first mixed-selection profile (list order) whose extensions include every file's;</item>
    /// <item>otherwise the catch-all (see <see cref="CatchAll"/>).</item>
    /// </list>
    /// </summary>
    public static ToolbarProfile? Select(IReadOnlyList<ToolbarProfile> profiles, IEnumerable<string> filePaths)
    {
        var extensions = filePaths
            .Select(FormatRegistry.ExtensionOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensions.Count > 0)
        {
            var match = profiles.FirstOrDefault(p => !p.IsFallback && extensions.All(p.Matches))
                        ?? profiles.FirstOrDefault(p => p.IsFallback && p.Extensions.Count > 0 && extensions.All(p.Matches));
            if (match is not null)
                return match;
        }

        return CatchAll(profiles);
    }

    /// <summary>
    /// The profile used for selections no other profile takes: the first mixed-selection profile with no extensions.
    /// If the user limited every mixed profile to some extensions, the first mixed profile is still better than an
    /// unrelated single-type donut; the first profile is the last resort (no mixed profile at all).
    /// </summary>
    public static ToolbarProfile? CatchAll(IReadOnlyList<ToolbarProfile> profiles) =>
        profiles.FirstOrDefault(p => p.IsFallback && p.Extensions.Count == 0)
        ?? profiles.FirstOrDefault(p => p.IsFallback)
        ?? profiles.FirstOrDefault();

    /// <summary>Formats of the given files that Filee recognises.</summary>
    public static IReadOnlyList<FileFormat> DetectFormats(IEnumerable<string> filePaths) =>
        filePaths.Select(FormatRegistry.Detect).OfType<FileFormat>().Distinct().ToList();
}
