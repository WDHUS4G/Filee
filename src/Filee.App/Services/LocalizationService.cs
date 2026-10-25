// Loads Assets/i18n/<lang>.json and exposes the strings to XAML as dynamic resources.
//
// XAML:  Text="{DynamicResource nav.home}"   (updates live when the language changes)
// Code:  _loc["nav.home"]  or  _loc.Format("home.files", 3)

using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Filee.Core.Localization;

namespace Filee.App.Services;

/// <summary>JSON based localization with live language switching.</summary>
public sealed class LocalizationService : ILocalizer
{
    /// <summary>Languages shipped with Filee: code → native name (shown in the language picker; see AppLanguages).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages = AppLanguages.All;

    private readonly ResourceDictionary _resources = new();
    private readonly Dictionary<string, string> _english;
    private Dictionary<string, string> _current;

    public LocalizationService()
    {
        _english = LoadStrings("en");
        _current = _english;
    }

    public string Language { get; private set; } = "en";

    private bool _initialized;

    /// <summary>True if <see cref="SetLanguage"/> would change anything.</summary>
    public bool NeedsUpdate(string setting) => !_initialized || Resolve(setting) != Language;

    public event EventHandler? LanguageChanged;

    public string this[string key] =>
        _current.TryGetValue(key, out var value) ? value
        : _english.TryGetValue(key, out var fallback) ? fallback
        : key;

    public string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, this[key], args);
        }
        catch (FormatException)
        {
            return this[key];
        }
    }

    /// <summary>Registers the string dictionary with the application so XAML can use DynamicResource.</summary>
    public void Attach(Application app) => app.Resources.MergedDictionaries.Add(_resources);

    /// <summary>
    /// Switches the UI language. <paramref name="setting"/> is a language code or <c>"auto"</c>
    /// (the OS UI language, falling back to English).
    /// </summary>
    public void SetLanguage(string setting)
    {
        var code = Resolve(setting);
        _current = code == "en" ? _english : LoadStrings(code);
        Language = code;

        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // Every key is published (English fallback for missing translations) so DynamicResource never breaks.
        foreach (var key in _english.Keys.Concat(_current.Keys).Distinct())
            _resources[key] = this[key];

        _initialized = true;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Left-to-right <paramref name="text"/> inside a sentence of the UI language: in Arabic a list like ".jpg, .png"
    /// keeps its dots in front (left-to-right marks around it). Unchanged in left-to-right languages.
    /// </summary>
    public static string LeftToRight(string text) =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? "\u200E" + text + "\u200E" : text;

    /// <summary>
    /// The language the user reads the OS in, taken before <see cref="SetLanguage"/> makes Filee's own the current one.
    /// Not InstalledUICulture: that is the language Windows was installed in, which stays Polish when the user switches
    /// the display language to English (or Russian, …) later.
    /// </summary>
    private static readonly string UserLanguage = CultureInfo.CurrentUICulture.Name;

    /// <summary>Maps "auto" / a culture name to one of the shipped languages.</summary>
    public static string Resolve(string setting) =>
        // macOS gives an app started from Finder no LANG: its language list comes from the system instead.
        Resolve(setting, () => Filee.Platform.MacOS.MacOSLocale.PreferredLanguage() ?? UserLanguage);

    /// <summary>Maps "auto" (the OS language <paramref name="osLanguage"/> gives) / a culture name to a shipped language.</summary>
    internal static string Resolve(string setting, Func<string> osLanguage)
    {
        if (!string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase) &&
            Languages.Any(l => l.Code.Equals(setting, StringComparison.OrdinalIgnoreCase)))
            return Languages.First(l => l.Code.Equals(setting, StringComparison.OrdinalIgnoreCase)).Code;

        var ui = osLanguage();
        if (AppLanguages.PortugueseVariant(ui) is { } portuguese)
            return portuguese;
        if (ui.Equals("tr", StringComparison.OrdinalIgnoreCase) || ui.StartsWith("tr-", StringComparison.OrdinalIgnoreCase))
            return "tr";
        if (ui.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return "ja";
        if (ui.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return "ko";
        if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return AppLanguages.IsTraditionalChinese(ui) ? "zh-TW" : "zh-CN";
        if (ui.StartsWith("pl", StringComparison.OrdinalIgnoreCase))
            return "pl";
        if (ui.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            return "es";
        if (ui.StartsWith("hi", StringComparison.OrdinalIgnoreCase))
            return "hi";
        if (ui.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            return "ru";
        // "ar" and every region ("ar-SA", "ar-EG", …), but not other codes starting with "ar".
        if (ui.Equals("ar", StringComparison.OrdinalIgnoreCase) || ui.StartsWith("ar-", StringComparison.OrdinalIgnoreCase))
            return "ar";
        // "fr" and every region ("fr-FR", "fr-CA", …), but not North Frisian "frr".
        if (ui.Equals("fr", StringComparison.OrdinalIgnoreCase) || ui.StartsWith("fr-", StringComparison.OrdinalIgnoreCase))
            return "fr";
        // "ne" and "ne-NP", but not Newari "new".
        if (ui.Equals("ne", StringComparison.OrdinalIgnoreCase) || ui.StartsWith("ne-", StringComparison.OrdinalIgnoreCase))
            return "ne";
        // "th" and "th-TH", but not Tharu "thl".
        if (ui.Equals("th", StringComparison.OrdinalIgnoreCase) || ui.StartsWith("th-", StringComparison.OrdinalIgnoreCase))
            return "th";
        return "en";
    }

    /// <summary>
    /// Reads a translation file. Missing files yield an empty dictionary (English is used instead).
    /// <paramref name="loader"/> reads assets before Avalonia is set up (a message shown by Program).
    /// </summary>
    internal static Dictionary<string, string> LoadStrings(string code, IAssetLoader? loader = null)
    {
        var uri = new Uri($"avares://Filee/Assets/i18n/{code}.json");
        if (!(loader?.Exists(uri) ?? AssetLoader.Exists(uri)))
            return [];
        using var stream = loader?.Open(uri) ?? AssetLoader.Open(uri);
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.DictionaryStringString) ?? [];
    }

    /// <summary>
    /// One string in the system's language, before Avalonia (and the settings) are loaded: for the messages a launch
    /// shows when it can't reach the running Filee. Falls back to English, then to the key.
    /// </summary>
    internal static string EarlyText(string key)
    {
        try
        {
            var loader = new StandardAssetLoader(typeof(LocalizationService).Assembly);
            var code = Resolve("auto");
            if (LoadStrings(code, loader).TryGetValue(key, out var text) || LoadStrings("en", loader).TryGetValue(key, out text))
                return text;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
        {
        }
        return key;
    }
}
