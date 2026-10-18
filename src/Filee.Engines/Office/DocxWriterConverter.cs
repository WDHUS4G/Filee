// Anything → DOCX without Word, LibreOffice or Pandoc: a built-in reader (DocumentReaders) turns the source into the
// document model and DocxWriter writes WordprocessingML. Its edges cost less than Pandoc's and LibreOffice's, so the
// built-in writer is used wherever it can read the source.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Writes DOCX files with the built-in WordprocessingML writer.</summary>
public sealed class DocxWriterConverter : IConverter
{
    /// <summary>Below Pandoc's and LibreOffice's direct edges (10 and more), so the built-in writer wins.</summary>
    private const int Cost = 8;

    /// <summary>Word files themselves: DOCX → DOCX is no conversion, the variants are the OOXML engine's job.</summary>
    private static readonly HashSet<string> WordFormats = new(StringComparer.OrdinalIgnoreCase) { "docx", "docm", "dotx", "dotm" };

    public string Id => "docx-writer";
    public string DisplayName => "DOCX writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => !WordFormats.Contains(f))
            .Select(f => new ConversionEdge(f, "docx", f == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → DOCX", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        progress?.Report(0.6);

        var output = step.Output.Allocate("docx");
        if (output is null)
            return [];
        await Task.Run(() => DocxWriter.Write(document, output), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
