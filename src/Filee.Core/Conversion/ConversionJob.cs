// State of one "drop" — a batch of files converted with one preset.

using Filee.Core.Presets;

namespace Filee.Core.Conversion;

public enum JobState
{
    Queued,
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
}

public enum FileState
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>Result for one source file of a job.</summary>
public sealed class FileResult
{
    public required string SourcePath { get; init; }
    public FileState State { get; set; } = FileState.Pending;
    public double Progress { get; set; }
    public List<string> Outputs { get; } = [];

    /// <summary>Localization key of the error (e.g. <c>error.no_route</c>), if any.</summary>
    public string? ErrorKey { get; set; }

    /// <summary>Raw error detail for logs / tooltips.</summary>
    public string? ErrorDetail { get; set; }
}

/// <summary>A batch of files converted with one preset.</summary>
public sealed class ConversionJob
{
    private readonly CancellationTokenSource _cts = new();

    public ConversionJob(IReadOnlyList<string> sources, Preset preset, string presetDisplayName)
    {
        Sources = sources;
        Preset = preset.Clone(); // later edits of the preset must not affect a running job
        PresetDisplayName = presetDisplayName;
        Files = sources.Select(s => new FileResult { SourcePath = s }).ToList();
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime CreatedAt { get; } = DateTime.Now;
    public IReadOnlyList<string> Sources { get; }
    public Preset Preset { get; }
    public string PresetDisplayName { get; }
    public IReadOnlyList<FileResult> Files { get; }
    public JobState State { get; internal set; } = JobState.Queued;

    /// <summary>Overall progress 0..1.</summary>
    public double Progress => Files.Count == 0 ? 1 : Files.Average(f => f.Progress);

    /// <summary>Every file written by the job.</summary>
    public IEnumerable<string> Outputs => Files.SelectMany(f => f.Outputs);

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>Requests cancellation. Running engines stop as soon as they can.</summary>
    public void Cancel() => _cts.Cancel();
}
