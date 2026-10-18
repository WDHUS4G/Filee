using Filee.Core.Conversion;

namespace Filee.Core.Tests;

/// <summary>Test double: "converts" by writing the input name and target format into the output file.</summary>
internal sealed class FakeConverter(string id, params ConversionEdge[] edges) : IConverter
{
    public string Id => id;
    public string DisplayName => id;
    public IReadOnlyList<ConversionEdge> Edges => edges;
    public int MaxParallelism => 0;
    public bool Available { get; set; } = true;

    /// <summary>Number of output files per input (simulates multi-page outputs).</summary>
    public int OutputsPerInput { get; set; } = 1;

    public bool Throw { get; set; }

    public EngineStatus GetStatus() => Available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_installed");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Throw)
            throw new InvalidOperationException("boom");

        var written = new List<string>();
        for (var i = 0; i < OutputsPerInput; i++)
        {
            var path = step.Output.Allocate(step.To, OutputsPerInput > 1 ? $"_p{i + 1}" : null);
            if (path is null)
                continue;
            File.WriteAllText(path, $"{Path.GetFileName(step.InputPath)}→{step.To} by {id}");
            written.Add(path);
        }
        progress?.Report(1);
        return Task.FromResult<IReadOnlyList<string>>(written);
    }
}

/// <summary>Test double for an engine that turns images: records the rotation each step was asked for.</summary>
internal sealed class FakeTurner(string id, params ConversionEdge[] edges) : IConverter, IImageTransformer
{
    private readonly FakeConverter _writer = new(id, edges);

    public string Id => id;
    public string DisplayName => id;
    public IReadOnlyList<ConversionEdge> Edges => edges;
    public int MaxParallelism => 0;

    public List<(string To, Filee.Core.Presets.ImageRotation Rotation)> Asked { get; } = [];

    public bool Transforms(string from, string to) => true;

    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        lock (Asked)
            Asked.Add((step.To, step.Preset.Image.Rotation));
        return _writer.ConvertAsync(step, progress, cancellationToken);
    }
}

/// <summary>An engine that doesn't turn images (no <see cref="IImageTransformer"/>) but records the preset it gets.</summary>
internal sealed class FakeRecorder(string id, params ConversionEdge[] edges) : IConverter
{
    private readonly FakeConverter _writer = new(id, edges);

    public string Id => id;
    public string DisplayName => id;
    public IReadOnlyList<ConversionEdge> Edges => edges;
    public int MaxParallelism => 0;

    public List<(string To, Filee.Core.Presets.ImageRotation Rotation)> Asked { get; } = [];

    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        lock (Asked)
            Asked.Add((step.To, step.Preset.Image.Rotation));
        return _writer.ConvertAsync(step, progress, cancellationToken);
    }
}

internal sealed class FakeMerger : IPdfMerger
{
    public List<string> Inputs { get; } = [];

    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken)
    {
        Inputs.AddRange(inputPaths);
        File.WriteAllText(outputPath, string.Join("|", inputPaths.Select(Path.GetFileName)));
        return Task.CompletedTask;
    }
}

/// <summary>Test double for multi-page TIFF: writes the input names into the output file.</summary>
internal sealed class FakeTiffMerger : ITiffMerger
{
    public List<string> Inputs { get; } = [];

    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, Filee.Core.Presets.ImageOptions options,
        CancellationToken cancellationToken)
    {
        Inputs.AddRange(inputPaths);
        File.WriteAllText(outputPath, string.Join("|", inputPaths.Select(Path.GetFileName)));
        return Task.CompletedTask;
    }
}

/// <summary>Test double for "Compress into one archive": writes the input names into the output file.</summary>
internal sealed class FakeCombiner : IFileCombiner
{
    public List<string> Inputs { get; } = [];

    public bool Throw { get; set; }

    public bool CanCombineInto(string format) => true;

    public Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Filee.Core.Presets.Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Throw)
            throw new InvalidOperationException("boom");
        Inputs.AddRange(inputPaths);
        File.WriteAllText(outputPath, string.Join("|", inputPaths.Select(Path.GetFileName)));
        return Task.CompletedTask;
    }
}

/// <summary>Creates and removes a unique temp directory.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "filee-tests", Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content = "x")
    {
        var full = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
