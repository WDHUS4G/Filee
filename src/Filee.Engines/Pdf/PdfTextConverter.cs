// PDF → TXT and Markdown with PdfPig (Apache-2.0), read by PdfDocumentReader: the text in reading order (also on
// two-column pages), without running headers, footers and page numbers.
//
// TXT has one paragraph per line and an empty line between paragraphs. The invisible text layer of OCR-processed
// scans is included; scans without one have no text to extract unless the preset recognizes it first (PdfOptions.Ocr).
//
// Markdown keeps headings, bold / italic and links (MarkdownWriter, as for e-books). The pictures go into a
// "<name>_files" folder next to the Markdown file and are linked relatively (MarkdownPictures); a page without text
// (a scan) becomes the picture of the page. With "Leave out pictures" (DocumentOptions.LeaveOutPictures) the
// Markdown is read like TXT: text only, OCR layers included.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;

namespace Filee.Engines.Pdf;

/// <summary>Extracts the text of PDF files, as plain text or Markdown.</summary>
public sealed class PdfTextConverter : IConverter
{
    public string Id => "pdf-text";
    public string DisplayName => "PDF text (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("pdf", "txt"),
        // Read once, written once: cheaper than through DOCX and Pandoc or HWPX and Unhwp. Direct only, so no other
        // route (PDF → HTML, EPUB, ...) starts going through a Markdown file and its picture folder.
        new("pdf", "md", 10 + DocumentReaders.PdfPenalty, DirectOnly: true),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → TXT, Markdown", $"{EngineVersions.BuiltIn} · {EngineVersions.Library("PdfPig", typeof(UglyToad.PdfPig.PdfDocument))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => step.To == "md" ? Markdown(step, progress, cancellationToken) : Text(step, progress, cancellationToken), cancellationToken);

    private static IReadOnlyList<string> Text(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var text = PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(step.InputPath));
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0)
            throw NoText();

        var output = step.Output.Allocate("txt");
        if (output is null)
            return [];
        // UTF-8 with BOM, so Notepad and Excel-era tools pick the right encoding for Korean and Chinese text.
        File.WriteAllText(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        progress?.Report(1);
        return [output];
    }

    private static IReadOnlyList<string> Markdown(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var textOnly = step.Preset.Document.LeaveOutPictures;
        var document = textOnly
            ? PdfDocumentReader.ReadText(step.InputPath)
            : step.Preset.Pdf.Ocr
                ? PdfDocumentReader.ReadWithOcrLayer(step.InputPath, Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}"))
                : PdfDocumentReader.Read(step.InputPath, Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}"));
        cancellationToken.ThrowIfCancellationRequested();
        if (textOnly && PdfDocumentReader.PlainText(document).Length == 0)
            throw NoText();
        progress?.Report(0.6);

        // The PDF's information title is often a file name ("Microsoft Word - report.docx"): no front matter. Indents
        // are the PDF's layout, not quotes (MarkdownWriter reads large left indents as block quote levels), and
        // headings are bold by nature (no "# **Title**").
        document.Title = null;
        foreach (var paragraph in document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>())
        {
            paragraph.Format = paragraph.Format with { Left = null };
            if (paragraph.HeadingLevel > 0)
                NotBold(paragraph.Inlines);
        }

        var output = step.Output.Allocate("md");
        if (output is null)
            return [];
        Func<string, string?> pictures = textOnly ? _ => null : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
        File.WriteAllText(output, MarkdownWriter.Write(document, pictures), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        progress?.Report(1);
        return [output];
    }

    private static void NotBold(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
        {
            if (inlines[i] is HText text)
                inlines[i] = new HText(text.Text, text.Format with { Bold = null });
            else if (inlines[i] is HLink link)
                NotBold(link.Content);
        }
    }

    private static InvalidOperationException NoText() =>
        new("The PDF contains no text (it may be scanned; Filee does not recognise text in images).");
}
