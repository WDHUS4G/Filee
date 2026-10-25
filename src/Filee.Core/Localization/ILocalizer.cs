// Localization contract. The Avalonia implementation lives in Filee.App (LocalizationService).
// Translations are JSON files in src/Filee.App/Assets/i18n. See docs/ADDING-A-LANGUAGE.md.

using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Localization;

/// <summary>Looks up translated UI strings by key.</summary>
public interface ILocalizer
{
    /// <summary>Current language code, e.g. <c>"ko"</c>.</summary>
    string Language { get; }

    /// <summary>Returns the translation for <paramref name="key"/>, falling back to English, then to the key itself.</summary>
    string this[string key] { get; }

    /// <summary>Translation with <see cref="string.Format(string, object[])"/> style arguments.</summary>
    string Format(string key, params object[] args);

    /// <summary>Raised after the language changed.</summary>
    event EventHandler? LanguageChanged;
}

/// <summary>Helpers that resolve user-facing names of presets and profiles.</summary>
public static class LocalizerExtensions
{
    /// <summary>
    /// A file's notice with why in brackets, the reason translated and the system's own words after it:
    /// "The original was kept… (the file is open in another program)".
    /// </summary>
    public static string Notice(this ILocalizer loc, Conversion.FileResult file)
    {
        var why = file.NoticeReasonKey is { } key ? loc[key] : null;
        if (!string.IsNullOrWhiteSpace(file.NoticeDetail))
            why = why is null ? file.NoticeDetail : $"{why}: {file.NoticeDetail}";
        return why is null ? loc[file.NoticeKey!] : $"{loc[file.NoticeKey!]} ({why})";
    }

    public static string DisplayName(this ILocalizer loc, Preset preset) =>
        !string.IsNullOrWhiteSpace(preset.Name) ? preset.Name
        : preset.NameKey is not null ? loc[preset.NameKey]
        : preset.TargetFormat.ToUpperInvariant();

    /// <summary>A compact name on the donut, where the target format is already shown underneath.</summary>
    public static string DonutName(this ILocalizer loc, Preset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.Name) || preset.NameKey is null)
            return loc.DisplayName(preset);
        var key = preset.NameKey + ".donut";
        var text = loc[key];
        return text == key ? loc.DisplayName(preset) : text;
    }

    /// <summary>
    /// The name on a slice of a group's ring, where the group's name already says what it is about: a built-in
    /// preset's short name (its <see cref="Preset.NameKey"/> + <c>.short</c>, "Left" for "Rotate left") when it has one,
    /// otherwise <see cref="DisplayName(ILocalizer, Preset)"/>.
    /// </summary>
    public static string RingName(this ILocalizer loc, Preset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.Name) || preset.NameKey is null)
            return loc.DisplayName(preset);
        var key = preset.NameKey + ".short";
        var text = loc[key];
        return text == key ? loc.DisplayName(preset) : text;
    }

    public static string DisplayName(this ILocalizer loc, ToolbarProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name
        : profile.NameKey is not null ? loc[profile.NameKey]
        : profile.Id;
}
