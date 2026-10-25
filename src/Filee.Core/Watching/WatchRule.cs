// A watch folder: files that land in the folder are converted with a preset (Settings → Watch folders, or
// "filee watch" on the command line).

using Filee.Core.Presets;

namespace Filee.Core.Watching;

/// <summary>What happens to a source file once it was converted.</summary>
public enum AfterConversion
{
    /// <summary>The source stays where it is.</summary>
    Keep,

    /// <summary>The source moves into <see cref="WatchRule.OriginalsFolder"/>, so the watch folder works like an inbox.</summary>
    MoveToOriginals,
}

/// <summary>One watched folder and how its files are converted.</summary>
public sealed class WatchRule
{
    /// <summary>Name of the default output folder inside the watched folder.</summary>
    public const string DefaultOutputName = "converted";

    /// <summary>Name of the folder inside the watched folder that receives converted sources (<see cref="AfterConversion.MoveToOriginals"/>).</summary>
    public const string OriginalsName = "originals";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public bool Enabled { get; set; } = true;

    public string Folder { get; set; } = "";

    /// <summary>Preset to convert with (<see cref="Presets.Preset.Id"/>).</summary>
    public string PresetId { get; set; } = "";

    public bool IncludeSubfolders { get; set; }

    /// <summary>Where converted files go; empty means <c>converted</c> inside <see cref="Folder"/>.</summary>
    public string OutputFolder { get; set; } = "";

    public AfterConversion Originals { get; set; } = AfterConversion.Keep;

    // Optional: empty means every file, and names as the preset gives them.

    /// <summary>Which files are converted (<see cref="FileNameFilter"/>), e.g. <c>*.heic</c> and <c>scan_*</c>; empty for all.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>File name pattern of converted files (tokens as in <see cref="OutputRule.FileNamePattern"/>); empty for the preset's.</summary>
    public string FileNamePattern { get; set; } = "";

    /// <summary>Replacements applied in order to the names of converted files (<see cref="OutputRule.Renames"/>).</summary>
    public List<RenameStep> Renames { get; set; } = [];

    /// <summary>The output folder actually used.</summary>
    public string ResolvedOutputFolder =>
        string.IsNullOrWhiteSpace(OutputFolder) ? Path.Combine(Folder, DefaultOutputName) : OutputFolder;

    /// <summary>Where converted sources go with <see cref="AfterConversion.MoveToOriginals"/>.</summary>
    public string OriginalsFolder => Path.Combine(Folder, OriginalsName);

    /// <summary>
    /// The preset as this folder converts with it: output into <see cref="ResolvedOutputFolder"/>, files from
    /// subfolders into the same subfolders there (as <see cref="AfterConversion.MoveToOriginals"/> does with the
    /// sources), and the folder's own name rules where set.
    /// </summary>
    public Preset Apply(Preset preset)
    {
        var applied = preset.Clone();
        applied.Output.Location = OutputLocation.CustomFolder;
        applied.Output.CustomFolder = ResolvedOutputFolder;
        applied.Output.KeepFoldersFrom = Folder;
        // A watch folder never replaces what it watches: the result would be converted again.
        applied.Output.ReplaceOriginal = false;
        if (!string.IsNullOrWhiteSpace(FileNamePattern))
            applied.Output.FileNamePattern = FileNamePattern.Trim();
        // Steps without a pattern are being typed; leaving them out keeps the name as it is.
        applied.Output.Renames.AddRange(Renames.Where(r => !r.IsBlank).Select(r => r.Clone()));
        return applied;
    }

    public WatchRule Clone()
    {
        var clone = (WatchRule)MemberwiseClone();
        clone.Include = [.. Include];
        clone.Renames = Renames.Select(r => r.Clone()).ToList();
        return clone;
    }
}
