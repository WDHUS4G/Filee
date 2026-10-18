// Anything → HWPX without Hancom Office: a reader from DocumentReaders turns the source into the HWPX document model
// and HwpxWriter writes the OWPML package.
//  * DOCX (and DOCM / DOTX / DOTM) is read directly (Docx/DocxReader), keeping page setup, headers/footers, columns,
//    text boxes and formatting.
//  * TXT needs no reader: one paragraph per line. Markdown is parsed with Markdig (MarkdownReader).
//  * XLSX / XLSM / XLTX and CSV are read directly (Office/Sheets); every sheet becomes a table (SheetDocument).
//  * PPTX / PPTM / POTX / PPSX are read directly (Pptx/PptxReader): one page per slide.
//  * PDF is read with PdfPig (Pdf/PdfDocumentReader): text in reading order, headings, pictures, scans as images.
//  * HTML (HtmlReader, AngleSharp), EPUB, MOBI / AZW3, FB2, HTMLZ and TXTZ (Ebooks) and HWPX (HwpxReader) are read
//    directly.
//  * ODT, RTF, reStructuredText and LaTeX are parsed by Pandoc (optional engine) into its JSON AST.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Writes HWPX files with the built-in OWPML writer.</summary>
public sealed class HwpxConverter : IConverter
{
    private bool? _pandoc;

    public string Id => "hwpx-writer";
    public string DisplayName => "HWPX writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges =>
        DocumentReaders.Formats(_pandoc ?? PandocConverter.Locate() is not null)
            .Where(from => from != "hwpx") // HWPX is read only to write other formats (DOCX, HTML, EPUB, ...)
            .Select(from => new ConversionEdge(from, "hwpx", from == "pdf" ? 10 + DocumentReaders.PdfPenalty : 10)).ToList();

    public EngineStatus GetStatus()
    {
        var pandoc = PandocConverter.Locate();
        _pandoc = pandoc is not null;
        var builtIn = string.Join(", ", DocumentReaders.BuiltInFormats.Select(f => f.ToUpperInvariant()));
        var withPandoc = string.Join(", ", DocumentReaders.PandocFormats.Select(f => f.ToUpperInvariant()));
        return EngineStatus.Available(pandoc is null
            ? $"{builtIn} → HWPX. {withPandoc} need Pandoc."
            : $"{builtIn}; {withPandoc} with Pandoc ({pandoc})", EngineVersions.BuiltIn);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        progress?.Report(0.6);

        var output = step.Output.Allocate("hwpx");
        if (output is null)
            return [];
        HwpxWriter.Write(document, output);
        progress?.Report(1);
        return [output];
    }

    /// <summary>Plain text: one paragraph per line (see <see cref="DocumentReaders.TextDocument"/>).</summary>
    internal static HDocument TextDocument(byte[] bytes) => DocumentReaders.TextDocument(bytes);

    /// <summary>UTF-8, UTF-16 with BOM or CP949 (see <see cref="DocumentReaders.DecodeText"/>).</summary>
    internal static string DecodeText(byte[] bytes) => DocumentReaders.DecodeText(bytes);
}
