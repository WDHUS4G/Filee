// PDF → TXT with PdfPig (Apache-2.0): the text in reading order (also on two-column pages), one paragraph per line and
// an empty line between paragraphs. The invisible text layer of OCR-processed scans is included; scans without one
// have no text to extract (Filee does no OCR).

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>Extracts the text of PDF files.</summary>
public sealed class PdfTextConverter : IConverter
{
    public string Id => "pdf-text";
    public string DisplayName => "PDF text (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("pdf", "txt")];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → TXT", $"{EngineVersions.BuiltIn} · {EngineVersions.Library("PdfPig", typeof(UglyToad.PdfPig.PdfDocument))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            var text = PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(step.InputPath));
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length == 0)
                throw new InvalidOperationException("The PDF contains no text (it may be scanned; Filee does not recognise text in images).");

            var output = step.Output.Allocate("txt");
            if (output is null)
                return [];
            // UTF-8 with BOM, so Notepad and Excel-era tools pick the right encoding for Korean and Chinese text.
            File.WriteAllText(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            progress?.Report(1);
            return [output];
        }, cancellationToken);
}
