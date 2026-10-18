// Output location / file naming / conflict handling for a preset.

namespace Filee.Core.Presets;

/// <summary>Where converted files go.</summary>
public enum OutputLocation
{
    /// <summary>
    /// The default save location from Settings → General (<see cref="Settings.AppSettings.DefaultOutput"/>). Where
    /// nothing resolves it (the command line), it means <see cref="SameFolder"/>.
    /// </summary>
    Default,
    /// <summary>Next to the source file.</summary>
    SameFolder,
    /// <summary>In a sub folder (see <see cref="OutputRule.SubfolderName"/>) next to the source file.</summary>
    Subfolder,
    /// <summary>In a fixed folder (see <see cref="OutputRule.CustomFolder"/>).</summary>
    CustomFolder,
}

/// <summary>What to do when the output file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Append " (2)", " (3)" ... to the file name.</summary>
    Rename,
    /// <summary>Replace the existing file (never the source file itself).</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and skip this conversion.</summary>
    Skip,
}

/// <summary>Output placement and naming rules.</summary>
public sealed class OutputRule
{
    public OutputLocation Location { get; set; } = OutputLocation.Default;

    public string SubfolderName { get; set; } = "converted";

    public string CustomFolder { get; set; } = "";

    /// <summary>
    /// With <see cref="OutputLocation.CustomFolder"/>: a source inside this folder keeps its subfolder below
    /// <see cref="CustomFolder"/> (<c>Inbox\2025\a.jpg</c> → <c>converted\2025\a.pdf</c>), so files of the same name in
    /// different subfolders don't end up side by side as <c>a.pdf</c> and <c>a (2).pdf</c>. Empty: everything goes
    /// straight into <see cref="CustomFolder"/>. Watch folders set it to the watched folder.
    /// </summary>
    public string KeepFoldersFrom { get; set; } = "";

    /// <summary>
    /// File name pattern without extension. Tokens:
    /// <c>{name}</c> source name, <c>{ext}</c> source extension, <c>{preset}</c> preset name,
    /// <c>{date}</c> yyyyMMdd, <c>{time}</c> HHmmss, <c>{index}</c> 1-based position in the batch.
    /// </summary>
    public string FileNamePattern { get; set; } = "{name}";

    /// <summary>Replacements applied in order to the name made from <see cref="FileNamePattern"/>.</summary>
    public List<RenameStep> Renames { get; set; } = [];

    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;

    /// <summary>
    /// Give converted files the creation and modification dates of their source, e.g. so converted photos still sort
    /// by when they were taken. Not for merged PDFs or combined archives, which come from many files.
    /// </summary>
    public bool KeepDates { get; set; }

    public OutputRule Clone()
    {
        var clone = (OutputRule)MemberwiseClone();
        clone.Renames = Renames.Select(r => r.Clone()).ToList();
        return clone;
    }
}

/// <summary>What a <see cref="RenameStep"/> does to a name.</summary>
public enum RenameKind
{
    /// <summary>Replaces the text <see cref="RenameStep.Find"/> with <see cref="RenameStep.Replace"/> (case ignored).</summary>
    Replace,
    /// <summary>Removes the text <see cref="RenameStep.Find"/> (case ignored).</summary>
    Remove,
    /// <summary>Puts <see cref="RenameStep.Replace"/> before the name.</summary>
    Prefix,
    /// <summary>Puts <see cref="RenameStep.Replace"/> after the name.</summary>
    Suffix,
    /// <summary>"my trip photo" → "my_trip_photo".</summary>
    SpacesToUnderscores,
    /// <summary>"report (2)" → "report".</summary>
    RemoveCopyNumber,
    /// <summary>"song [remix] (live)" → "song".</summary>
    RemoveBrackets,
    /// <summary>"01 - intro" → "intro".</summary>
    RemoveLeadingNumber,
    /// <summary>"invoice#12@acme!" → "invoice12acme".</summary>
    RemoveSymbols,
    Lowercase,
    Uppercase,
    /// <summary>Replaces the regular expression <see cref="RenameStep.Find"/> with <see cref="RenameStep.Replace"/> (<c>$1</c> inserts a group).</summary>
    Regex,
}

/// <summary>One change to the name of a converted file, e.g. "replace IMG_ with Photo_" or "spaces to underscores".</summary>
public sealed class RenameStep
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public RenameKind Kind { get; set; } = RenameKind.Replace;

    /// <summary>The text (or, for <see cref="RenameKind.Regex"/>, the expression) looked for.</summary>
    public string Find { get; set; } = "";

    /// <summary>What goes in its place, or the text added by <see cref="RenameKind.Prefix"/> and <see cref="RenameKind.Suffix"/>.</summary>
    public string Replace { get; set; } = "";

    /// <summary>True for a step that changes nothing because its text is still missing.</summary>
    public bool IsBlank => Kind switch
    {
        RenameKind.Replace or RenameKind.Remove or RenameKind.Regex => Find.Length == 0,
        RenameKind.Prefix or RenameKind.Suffix => Replace.Length == 0,
        _ => false,
    };

    /// <summary>The name after this step. A regular expression that isn't valid leaves it as it is.</summary>
    public string Apply(string name) => Kind switch
    {
        _ when IsBlank => name,
        RenameKind.Replace => name.Replace(Find, Replace, StringComparison.OrdinalIgnoreCase),
        RenameKind.Remove => name.Replace(Find, "", StringComparison.OrdinalIgnoreCase),
        RenameKind.Prefix => Replace + name,
        RenameKind.Suffix => name + Replace,
        RenameKind.SpacesToUnderscores => Fixed(name, @"\s+", "_"),
        RenameKind.RemoveCopyNumber => Fixed(name, @"\s*\(\d+\)$", ""),
        RenameKind.RemoveBrackets => Fixed(name, @"\s*(\([^)]*\)|\[[^\]]*\]|\{[^}]*\})", "").Trim(),
        RenameKind.RemoveLeadingNumber => Fixed(name, @"^\d+[\s._-]*", ""),
        RenameKind.RemoveSymbols => Fixed(name, @"[^\p{L}\p{N}\s._-]", ""),
        RenameKind.Lowercase => name.ToLowerInvariant(),
        RenameKind.Uppercase => name.ToUpperInvariant(),
        RenameKind.Regex => OutputPathResolver.Rename(name, Find, Replace),
        _ => name,
    };

    public RenameStep Clone() => (RenameStep)MemberwiseClone();

    private static string Fixed(string name, string pattern, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(name, pattern, replacement, System.Text.RegularExpressions.RegexOptions.CultureInvariant, Timeout);
}
