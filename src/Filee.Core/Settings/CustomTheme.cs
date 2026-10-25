// A user colour theme: theme.json next to settings.json overrides the surface, text and status colours of the light
// and/or dark variant. It is a separate file (not part of settings.json) so a theme can be shared and replaced on
// its own; the accent colour stays in ThemeSettings, where the Theme page edits it.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Settings;

/// <summary>Colours from <c>theme.json</c>, by variant. Missing keys keep Filee's own colours.</summary>
public sealed partial class CustomTheme
{
    /// <summary>File name inside the data directory (<see cref="UserDataStore.Directory"/>).</summary>
    public const string FileName = "theme.json";

    /// <summary>
    /// Larger files are skipped: Load runs on the UI thread at start-up and on every settings save (the Softness
    /// slider saves on each tick), and a real theme is well under 1 KB.
    /// </summary>
    public const long MaxBytes = 64 * 1024;

    /// <summary>Colour names a theme may set.</summary>
    public static readonly IReadOnlyList<string> Keys =
        ["page", "surface", "surfaceAlt", "border", "text", "textSecondary", "danger", "success", "warning"];

    /// <summary>Colours for the light variant, as name → <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public Dictionary<string, string> Light { get; set; } = [];

    /// <summary>Colours for the dark variant, as name → <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public Dictionary<string, string> Dark { get; set; } = [];

    /// <summary>True when neither variant sets a colour.</summary>
    public bool IsEmpty => Light.Count == 0 && Dark.Count == 0;

    /// <summary>
    /// Reads <c>theme.json</c> from <paramref name="directory"/>. Unknown names and values that are not hex colours
    /// are dropped (and logged); a missing, empty or unreadable file gives null, so Filee's own colours apply.
    /// The file is the user's: unlike settings.json it is never renamed or rewritten.
    /// </summary>
    public static CustomTheme? Load(string directory, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var path = Path.Combine(directory, FileName);
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        if (info.Length > MaxBytes)
        {
            log.LogWarning("Custom theme {Path} is larger than {Max} bytes; using the built-in colours", path, MaxBytes);
            return null;
        }

        CustomTheme? theme;
        try
        {
            using var stream = File.OpenRead(path);
            theme = JsonSerializer.Deserialize(stream, FileeJsonContext.Default.CustomTheme);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Custom theme {Path} could not be read; using the built-in colours", path);
            return null;
        }
        if (theme is not null)
        {
            theme.Light = Clean(theme.Light, "light", path, log);
            theme.Dark = Clean(theme.Dark, "dark", path, log);
        }
        if (theme is null || theme.IsEmpty)
        {
            // Also covers "Dark" or "Light" with a capital: variant names are case-sensitive, colour names are not.
            log.LogWarning("Custom theme {Path} sets no colours Filee can use (expected \"light\"/\"dark\" objects of #RRGGBB values)", path);
            return null;
        }
        return theme;
    }

    private static Dictionary<string, string> Clean(Dictionary<string, string>? colors, string variant, string path, ILogger log)
    {
        var clean = new Dictionary<string, string>();
        foreach (var (name, value) in colors ?? [])
        {
            // Names are matched case-insensitively and stored in their canonical spelling.
            var key = Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                log.LogWarning("Custom theme {Path}: unknown colour {Variant}.{Name} ignored", path, variant, name);
            else if (value is null || !HexColor().IsMatch(value))
                log.LogWarning("Custom theme {Path}: {Variant}.{Name} is not #RRGGBB or #AARRGGBB, ignored", path, variant, name);
            else
                clean[key] = value;
        }
        return clean;
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColor();
}
