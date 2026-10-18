// Font conversion between TTF, OTF, WOFF, WOFF2 and EOT, entirely in managed code (zlib and Brotli come with .NET).
// CFF (PostScript) outlines are converted to TrueType when the target needs them (TTF, EOT). See docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Fonts;

/// <summary>Converts web and desktop font formats into each other.</summary>
public sealed class FontConverter : IConverter
{
    public string Id => "font";
    public string DisplayName => "Fonts (built-in)";
    public int MaxParallelism => 0;

    /// <summary>Every format to every other one, directly.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. from source in FontFile.Formats from target in FontFile.Formats where source != target select new ConversionEdge(source, target)];

    public EngineStatus GetStatus() => EngineStatus.Available("TTF, OTF, WOFF, WOFF2, EOT", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var font = FontFile.Load(File.ReadAllBytes(step.InputPath), cancellationToken);
            progress?.Report(0.2);
            var outlines = progress is null ? null : new SubProgress(progress, 0.2, 0.6);
            var data = FontFile.Save(font, step.To, cancellationToken, outlines);
            progress?.Report(0.9);

            var output = step.Output.Allocate(step.To);
            if (output is null)
                return [];
            File.WriteAllBytes(output, data);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>Maps 0–1 progress of one phase onto a slice of the step's progress, synchronously.</summary>
    private sealed class SubProgress(IProgress<double> parent, double start, double length) : IProgress<double>
    {
        public void Report(double value) => parent.Report(start + length * value);
    }
}
