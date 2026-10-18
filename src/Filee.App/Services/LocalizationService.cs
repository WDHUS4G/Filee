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
    /// <summary>Languages shipped with Filee: code → native name (shown in the language picker).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages =
    [
        ("en", "English"),
        ("ko", "한국어"),
        ("zh-CN", "简体中文"),
    ];

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

    /// <summary>Maps "auto" / a culture name to one of the shipped languages.</summary>
    public static string Resolve(string setting)
    {
        if (!string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase) &&
            Languages.Any(l => l.Code.Equals(setting, StringComparison.OrdinalIgnoreCase)))
            return Languages.First(l => l.Code.Equals(setting, StringComparison.OrdinalIgnoreCase)).Code;

        // macOS gives an app started from Finder no LANG: its language list comes from the system instead.
        var ui = Filee.Platform.MacOS.MacOSLocale.PreferredLanguage() ?? CultureInfo.InstalledUICulture.Name;
        if (ui.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return "ko";
        if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        return "en";
    }

    /// <summary>Reads a translation file. Missing files yield an empty dictionary (English is used instead).</summary>
    internal static Dictionary<string, string> LoadStrings(string code)
    {
        var uri = new Uri($"avares://Filee/Assets/i18n/{code}.json");
        if (!AssetLoader.Exists(uri))
            return [];
        using var stream = AssetLoader.Open(uri);
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.DictionaryStringString) ?? [];
    }
}
