// Turns an OutputRule + source file into the concrete output path. Pure logic, unit tested.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Resolves output file paths from <see cref="OutputRule"/>s.</summary>
public static class OutputPathResolver
{
    /// <summary>Values available to file name pattern tokens.</summary>
    public readonly record struct Tokens(string SourcePath, string PresetName, int Index, DateTime Now);

    /// <summary>
    /// Computes the output path for one output file.
    /// </summary>
    /// <param name="rule">Placement rule of the preset.</param>
    /// <param name="tokens">Token values for the file name pattern.</param>
    /// <param name="targetExtension">Extension of the output, without dot; empty for a folder (archive extraction).</param>
    /// <param name="exists">Returns true if a path is already taken (on disk or reserved by this batch).</param>
    /// <param name="suffix">Optional suffix appended to the name, e.g. <c>"_p3"</c> for page 3.</param>
    /// <returns>The path to write, or <c>null</c> when the rule says to skip an existing file.</returns>
    public static string? Resolve(OutputRule rule, Tokens tokens, string targetExtension,
        Func<string, bool> exists, string? suffix = null)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(tokens.SourcePath)) ?? "";
        var dir = rule.Location switch
        {
            OutputLocation.Subfolder => Path.Combine(sourceDir, Sanitize(ExpandPattern(rule.SubfolderName, tokens), "converted")),
            OutputLocation.CustomFolder when !string.IsNullOrWhiteSpace(rule.CustomFolder) =>
                Path.Combine(rule.CustomFolder, SubfolderBelow(rule.KeepFoldersFrom, sourceDir)),
            _ => sourceDir,
        };

        var sourceName = FormatRegistry.NameWithoutExtension(tokens.SourcePath);
        var name = Sanitize(Rename(ExpandPattern(rule.FileNamePattern, tokens), rule.Renames), sourceName) + suffix;
        var ext = targetExtension.TrimStart('.');
        string PathFor(string baseName) => Path.Combine(dir, ext.Length == 0 ? baseName : $"{baseName}.{ext}");
        var candidate = PathFor(name);

        // The source file is never overwritten, whatever the policy says.
        var isSource = PathsEqual(candidate, tokens.SourcePath);
        if (!isSource && !exists(candidate))
            return candidate;

        if (!isSource && rule.Conflict == ConflictPolicy.Overwrite)
            return candidate;
        if (!isSource && rule.Conflict == ConflictPolicy.Skip)
            return null;

        for (var n = 2; ; n++)
        {
            candidate = PathFor($"{name} ({n})");
            if (!exists(candidate) && !PathsEqual(candidate, tokens.SourcePath))
                return candidate;
        }
    }

    /// <summary>
    /// The path of <paramref name="folder"/> below <paramref name="root"/> (<c>2025\scans</c>), or empty when there is no
    /// root, or the folder is the root itself or lies outside it.
    /// </summary>
    private static string SubfolderBelow(string root, string folder)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";
        var relative = Path.GetRelativePath(Path.GetFullPath(root), folder);
        return relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? ""
            : relative;
    }

    /// <summary>Applies <paramref name="steps"/> to <paramref name="name"/> in order.</summary>
    public static string Rename(string name, IEnumerable<RenameStep> steps) =>
        steps.Aggregate(name, (current, step) => step.Apply(current));

    /// <summary>
    /// Replaces the regular expression <paramref name="find"/> in <paramref name="name"/>. A pattern that isn't valid
    /// (see <see cref="RenameError"/>) or runs too long leaves the name as it is.
    /// </summary>
    public static string Rename(string name, string? find, string? replace)
    {
        if (string.IsNullOrEmpty(find))
            return name;
        try
        {
            return Regex.Replace(name, find, replace ?? "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return name;
        }
    }

    /// <summary>Why <paramref name="find"/> is not a valid regular expression, or null.</summary>
    public static string? RenameError(string? find)
    {
        if (string.IsNullOrEmpty(find))
            return null;
        try
        {
            _ = new Regex(find, RegexOptions.CultureInvariant);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Replaces <c>{token}</c> placeholders in a file name pattern.</summary>
    public static string ExpandPattern(string pattern, Tokens tokens)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = "{name}";

        var ext = FormatRegistry.ExtensionOf(tokens.SourcePath);
        return pattern
            .Replace("{name}", FormatRegistry.NameWithoutExtension(tokens.SourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", ext, StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", tokens.PresetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", tokens.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", tokens.Now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", tokens.Index.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes characters that are invalid in file names on any supported OS.</summary>
    public static string Sanitize(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? fallback : result;
    }

    private static bool PathsEqual(string a, string b) =>
        // A Unix mount may still be case-insensitive. Prefer a numbered output to risking the source.
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
