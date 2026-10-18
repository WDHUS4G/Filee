// English texts for the command line, read from the app's translation file (embedded): error messages of failed
// files and the names of built-in presets.

using System.Text.Json;
using Filee.Core.Localization;

namespace Filee.Cli;

internal sealed class EnglishTexts : ILocalizer
{
    private readonly Dictionary<string, string> _texts;

    public EnglishTexts()
    {
        using var stream = typeof(EnglishTexts).Assembly.GetManifestResourceStream("Filee.Cli.en.json")
                           ?? throw new InvalidOperationException("Embedded en.json is missing.");
        using var json = JsonDocument.Parse(stream);
        _texts = json.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    public string Language => "en";

    public string this[string key] => _texts.GetValueOrDefault(key, key);

    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, this[key], args);

    public event EventHandler? LanguageChanged { add { } remove { } }
}
