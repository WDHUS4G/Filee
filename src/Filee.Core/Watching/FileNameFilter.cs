// Which files of a watch folder are converted: patterns such as "*.heic" and "scan_*", or a regular expression
// between slashes ("/^IMG_\d+/"). A file matching any pattern is converted; no patterns means every file.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Formats;

namespace Filee.Core.Watching;

/// <summary>A file name filter of a watch folder. An empty filter lets every file through.</summary>
public sealed class FileNameFilter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, FileNameFilter> Cache = new();
    private readonly Regex[] _patterns;

    private FileNameFilter(Regex[] patterns) => _patterns = patterns;

    /// <summary>Lets every file through.</summary>
    public static FileNameFilter All { get; } = new([]);

    /// <summary>
    /// Turns what someone typed into a pattern: an extension (<c>heic</c>, <c>.heic</c>) becomes <c>*.heic</c>, text
    /// without <c>*</c> or <c>?</c> matches names containing it (<c>scan</c> → <c>*scan*</c>), and a pattern or a
    /// /regular expression/ stays as it is. Null for blank input.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        var text = typed?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (IsRegex(text) || text.Contains('*') || text.Contains('?'))
            return text;
        var extension = text.TrimStart('.');
        if (extension.Length > 0 && FormatRegistry.FindByExtension(extension) is not null && !extension.Contains('.'))
            return $"*.{extension.ToLowerInvariant()}";
        return $"*{text}*";
    }

    /// <summary>Why a pattern can't be used (a regular expression that isn't valid), or null.</summary>
    public static string? Validate(string pattern)
    {
        try
        {
            _ = ToRegex(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The filter of <paramref name="patterns"/>: <c>*</c> stands for any text, <c>?</c> for one character, and a
    /// pattern between slashes is a regular expression. Case is ignored. Patterns that aren't valid are skipped.
    /// </summary>
    public static FileNameFilter Of(IReadOnlyCollection<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
            return All;
        return Cache.GetOrAdd(string.Join('\n', patterns), _ =>
        {
            var regexes = new List<Regex>();
            foreach (var pattern in patterns)
            {
                try
                {
                    regexes.Add(ToRegex(pattern));
                }
                catch (ArgumentException)
                {
                    // checked when the pattern was added; one edited by hand in settings.json is left out
                }
            }
            return regexes.Count == 0 ? All : new FileNameFilter([.. regexes]);
        });
    }

    /// <summary>True when the file name (without folder) matches one of the patterns.</summary>
    public bool Matches(string fileName)
    {
        if (_patterns.Length == 0)
            return true;
        foreach (var pattern in _patterns)
        {
            try
            {
                if (pattern.IsMatch(fileName))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // a pattern that takes this long on one name is treated as no match
            }
        }
        return false;
    }

    private static bool IsRegex(string pattern) => pattern.Length > 2 && pattern.StartsWith('/') && pattern.EndsWith('/');

    private static Regex ToRegex(string pattern) =>
        new(IsRegex(pattern) ? pattern[1..^1] : FromWildcards(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static string FromWildcards(string pattern)
    {
        var regex = new StringBuilder("^");
        foreach (var c in pattern.Trim())
            regex.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        return regex.Append('$').ToString();
    }
}
