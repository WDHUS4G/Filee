// The languages Filee ships (Assets/i18n/<code>.json). The installer (src/Filee.Setup) links this file, so a language
// added here is offered by Setup too.

namespace Filee.App.Services;

internal static class AppLanguages
{
    /// <summary>Code → native name (shown in the language pickers of the app and of Setup).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> All =
    [
        ("en", "English"),
        ("tr", "Türkçe"),
        ("es", "Español"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("pl", "Polski"),
        ("hi", "हिन्दी"),
        ("ru", "Русский"),
        ("ar", "العربية"),
        ("pt-PT", "Português (Portugal)"),
        ("pt-BR", "Português (Brasil)"),
        ("fr", "Français"),
        ("ne", "नेपाली"),
        ("th", "ไทย"),
    ];

    /// <summary>Shared Portuguese region policy for the app and Setup, including legacy Inno language codes.</summary>
    public static string? PortugueseVariant(string name)
    {
        var normalized = name.Trim().Replace('_', '-');
        if (normalized.Equals("ptpt", StringComparison.OrdinalIgnoreCase))
            return "pt-PT";
        if (normalized.Equals("ptbr", StringComparison.OrdinalIgnoreCase))
            return "pt-BR";
        var parts = normalized.Split('-');
        if (!parts[0].Equals("pt", StringComparison.OrdinalIgnoreCase))
            return null;
        // The region follows the language and optional script (pt-Latn-BR). Do not mistake an extension or
        // private-use subtag for a region: pt-PT-x-br still means Portuguese as used in Portugal.
        var regionIndex = parts.Length > 1 && parts[1].Length == 4 && parts[1].All(char.IsAsciiLetter) ? 2 : 1;
        return parts.ElementAtOrDefault(regionIndex)?.Equals("BR", StringComparison.OrdinalIgnoreCase) == true
            ? "pt-BR" : "pt-PT";
    }

    /// <summary>
    /// True for a Chinese culture name written in Traditional characters: "zh-TW", "zh-HK", "zh-Hant-TW", "zh-CHT" …
    /// An explicit script wins over the region (macOS has "zh-Hans-TW", Simplified in Taiwan); a bare "zh" is Simplified.
    /// </summary>
    public static bool IsTraditionalChinese(string culture)
    {
        var parts = culture.Trim().Replace('_', '-').Split('-');
        if (!parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = parts.Skip(1).ToArray();
        if (rest.Contains("Hans", StringComparer.OrdinalIgnoreCase))
            return false;
        return rest.Intersect(["Hant", "TW", "HK", "MO", "CHT"], StringComparer.OrdinalIgnoreCase).Any();
    }
}
