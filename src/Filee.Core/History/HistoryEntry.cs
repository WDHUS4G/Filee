// A finished conversion job as shown on the Home page ("Recent conversions").

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.History;

/// <summary>Persisted summary of a finished job.</summary>
public sealed class HistoryEntry
{
    public string JobId { get; set; } = "";
    public DateTime FinishedAt { get; set; }
    public string PresetId { get; set; } = "";
    public string PresetName { get; set; } = "";
    public JobState State { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<string> Outputs { get; set; } = [];

    /// <summary>The watch folder (<see cref="Watching.WatchRule.Id"/>) the job came from, or null.</summary>
    public string? WatchRuleId { get; set; }

    /// <summary>Files that failed, with the reason.</summary>
    public List<HistoryFailure> Failures { get; set; } = [];

    /// <summary>
    /// The preset as the job ran it (save location already decided, a watch folder's output folder included), kept
    /// when files failed: they can be converted again the same way even after the preset was changed or deleted.
    /// </summary>
    public Preset? Preset { get; set; }

    /// <summary>"file: error" lines written by Filee 1.7 and earlier; read for entries without <see cref="Failures"/>.</summary>
    public List<string> Errors { get; set; } = [];

    public static HistoryEntry From(ConversionJob job)
    {
        var failures = job.Files
            .Where(f => f.State == FileState.Failed)
            .Select(f => new HistoryFailure { Source = f.SourcePath, ErrorKey = f.ErrorKey, ErrorDetail = f.ErrorDetail })
            .ToList();
        return new HistoryEntry
        {
            JobId = job.Id,
            FinishedAt = DateTime.Now,
            PresetId = job.Preset.Id,
            PresetName = job.PresetDisplayName,
            State = job.State,
            Sources = [.. job.Sources],
            Outputs = [.. job.Outputs],
            Failures = failures,
            Preset = failures.Count > 0 ? job.Preset.Clone() : null,
        };
    }
}

/// <summary>A file a job couldn't convert.</summary>
public sealed class HistoryFailure
{
    /// <summary>Full path of the source file.</summary>
    public string Source { get; set; } = "";

    /// <summary>Localization key of the reason (e.g. <c>error.no_route</c>).</summary>
    public string? ErrorKey { get; set; }

    /// <summary>Detail of the reason (an engine's message, the formats of a missing route).</summary>
    public string? ErrorDetail { get; set; }
}
