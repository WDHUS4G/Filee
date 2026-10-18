// How much an update matters and what it changes, in the user's language. Each version's CHANGELOG section (and so its
// GitHub release notes) starts with a hidden block GitHub does not show:
//
//   <!-- update
//   level: recommended
//   ko: 엑셀 페이지 나누기가 PDF에 반영돼요
//   en: Excel page breaks carry over to PDF
//   zh: Excel 分页符会保留到 PDF 中
//   -->
//
// level is required (wrong results, lost data, security, Filee not starting), recommended (bug fixes) or optional (new
// features). A language line may repeat for more than one change. A release without the block counts as recommended.

namespace Filee.App.Services;

/// <summary>How much an update matters: what the app does about it.</summary>
public enum UpdateLevel
{
    /// <summary>New features: the sidebar button only, no card.</summary>
    Optional,

    /// <summary>Bug fixes: a card once per version.</summary>
    Recommended,

    /// <summary>Wrong results, lost data, security: a highlighted card every time Filee starts.</summary>
    Required,
}

/// <summary>One version's level and one-line summaries by language ("ko", "en", "zh-CN").</summary>
public sealed record ReleaseNote(string Version, UpdateLevel Level, IReadOnlyDictionary<string, IReadOnlyList<string>> Summary)
{
    /// <summary>The summaries in <paramref name="language"/>, else in English, else none.</summary>
    public IReadOnlyList<string> In(string language) =>
        Summary.TryGetValue(language, out var lines) ? lines
        : Summary.TryGetValue("en", out var english) ? english
        : [];
}

public static class ReleaseNotes
{
    private const string Start = "<!-- update";

    /// <summary>The note of <paramref name="version"/> from its release text; recommended and empty without a block.</summary>
    public static ReleaseNote Parse(string version, string? body)
    {
        var level = UpdateLevel.Recommended;
        var summary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var start = body?.IndexOf(Start, StringComparison.Ordinal) ?? -1;
        var end = start < 0 ? -1 : body!.IndexOf("-->", start, StringComparison.Ordinal);
        if (end > start)
        {
            foreach (var raw in body![(start + Start.Length)..end].Split('\n'))
            {
                var colon = raw.IndexOf(':');
                if (colon <= 0)
                    continue;
                var key = raw[..colon].Trim().ToLowerInvariant();
                var value = raw[(colon + 1)..].Trim();
                if (value.Length == 0)
                    continue;
                if (key == "level")
                    level = value.ToLowerInvariant() switch
                    {
                        "required" => UpdateLevel.Required,
                        "optional" => UpdateLevel.Optional,
                        _ => UpdateLevel.Recommended,
                    };
                else if (key is "ko" or "en" or "zh" or "zh-cn")
                {
                    var language = key.StartsWith("zh", StringComparison.Ordinal) ? "zh-CN" : key;
                    if (!summary.TryGetValue(language, out var lines))
                        summary[language] = lines = [];
                    lines.Add(value);
                }
            }
        }
        return new ReleaseNote(version, level, summary.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value));
    }

    /// <summary>The notes of the releases newer than <paramref name="current"/> up to <paramref name="latest"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseNote> Between(IEnumerable<(string Tag, string? Body)> releases, string current, string latest)
    {
        UpdateService.TryParse(latest, out var newest);
        return releases
            .Where(r => UpdateService.IsNewer(r.Tag, current) && UpdateService.TryParse(r.Tag, out var v) && v <= newest)
            .Select(r => (Version: UpdateService.TryParse(r.Tag, out var v) ? v : new Version(), r.Tag, r.Body))
            .GroupBy(r => r.Version)
            .OrderByDescending(g => g.Key)
            .Select(g => Parse(g.Key.ToString(3), g.First().Body))
            .ToList();
    }

    /// <summary>The level of the whole update: the highest of the versions it brings (recommended when none is known).</summary>
    public static UpdateLevel Highest(IReadOnlyList<ReleaseNote> notes) =>
        notes.Count == 0 ? UpdateLevel.Recommended : notes.Max(n => n.Level);
}
