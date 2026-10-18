// Rules for the extensions a user types into a donut profile, and the autocomplete list built from FormatRegistry.

using Filee.App.Controls;
using Filee.Core.Formats;
using Filee.Core.Localization;

namespace Filee.App.ViewModels.Pages;

/// <summary>Cleans up and suggests file extensions for toolbar profiles.</summary>
public static class ExtensionInput
{
    private const int MaxLength = 16;

    /// <summary>
    /// Turns user input into a stored extension: trimmed, lower-case, without leading dots or wildcards
    /// (" *.JPG " → "jpg", ".tar.gz" → "tar.gz"). Returns null for text that can't be an extension.
    /// </summary>
    /// <remarks>
    /// Two-part extensions are only accepted when Filee knows them (tar.gz, tar.xz ...): files are matched with
    /// <see cref="FormatRegistry.ExtensionOf"/>, which only sees the last part of any other name ("x.foo.bar" → "bar").
    /// </remarks>
    public static string? Normalize(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('*', '.').TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > MaxLength || value.Contains("..", StringComparison.Ordinal))
            return null;
        if (!value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '.'))
            return null;
        if (value.Contains('.') && FormatRegistry.FindByExtension(value) is null)
            return null;
        return value;
    }

    /// <summary>
    /// Autocomplete entries for <paramref name="text"/>: extensions starting with it first, then formats whose name
    /// matches, then everything in a matching category ("video", "동영상"). Extensions in <paramref name="existing"/>
    /// are left out; <paramref name="ownerOf"/> names the profile that already has an extension (shown as a note).
    /// </summary>
    public static IReadOnlyList<TagSuggestion> Suggest(
        string? text, IEnumerable<string> existing, Func<string, string?> ownerOf, ILocalizer loc, int max = 8)
    {
        var query = (text ?? "").Trim().TrimStart('*', '.').ToLowerInvariant();
        if (query.Length == 0)
            return [];
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = FormatRegistry.Known
            .Where(f => f.Id != FormatRegistry.Folder)
            .SelectMany((format, formatIndex) => format.Extensions
                .Where(e => e.Length > 0 && !taken.Contains(e))
                .Select((extension, i) => (Extension: extension, Format: format, Order: formatIndex * 100 + i)));

        return candidates
            .Select(c => (c.Extension, c.Format, c.Order, Score: Score(query, c.Extension, c.Format, loc)))
            .Where(c => c.Score >= 0)
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Order)
            .Take(max)
            .Select(c => new TagSuggestion(
                c.Extension,
                "." + c.Extension,
                FormatLabels.NameAndCategory(loc, c.Format),
                ownerOf(c.Extension) is { } owner ? loc.Format("toolbar.ext_in_profile", owner) : null))
            .ToList();
    }

    /// <summary>Lower is better; -1 = no match.</summary>
    private static int Score(string query, string extension, FileFormat format, ILocalizer loc)
    {
        if (extension.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (extension.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (format.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase) || format.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        // Substrings of one letter would match almost everything.
        if (query.Length >= 2 && (extension.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || format.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 3;
        if (FormatLabels.Category(loc, format.Category).StartsWith(query, StringComparison.CurrentCultureIgnoreCase)
            || format.Category.ToString().StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        return -1;
    }
}
