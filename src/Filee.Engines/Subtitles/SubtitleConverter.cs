// Text subtitles in managed code: SRT, ASS/SSA, WebVTT, SAMI, LRC, TTML, MicroDVD and SCC are read into one cue model
// and written as SRT, ASS, WebVTT, SAMI, LRC, TTML or MicroDVD, always as UTF-8. Korean CP949 / EUC-KR files (most
// SAMI files) are recognised. Subtitles inside videos are taken out by FFmpeg (video → SRT / ASS / WebVTT), see
// docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Subtitles;

/// <summary>Converts text subtitle formats into each other.</summary>
public sealed class SubtitleConverter : IConverter
{
    public string Id => "subtitle";
    public string DisplayName => "Subtitles (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Every subtitle format to every one Filee writes, also to itself: an SRT or SAMI file in CP949 comes out as UTF-8.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. from source in SubtitleReader.Formats from target in SubtitleWriter.Formats select new ConversionEdge(source, target)];

    public EngineStatus GetStatus() => EngineStatus.Available("SRT, ASS, WebVTT, SAMI, LRC, TTML, MicroDVD, SCC", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var document = SubtitleReader.Read(step.From, File.ReadAllBytes(step.InputPath));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);
            var data = SubtitleWriter.Write(document, step.To);

            var output = step.Output.Allocate(Core.Formats.FormatRegistry.Get(step.To).PrimaryExtension);
            if (output is null)
                return [];
            File.WriteAllBytes(output, data);
            progress?.Report(1);
            return [output];
        }, cancellationToken);
}
