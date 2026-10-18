// The one place that knows how to read a file of some format into the document model (HDocument). The HWPX writer
// (HwpxConverter) and the DOCX writer (DocxWriterConverter) take their input formats from here, so a new reader is one
// line in BuiltIn and both writers (and every route through them) pick it up.

using System.Text;
using System.Text.Json.Nodes;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Registry of document readers: format id → reader.</summary>
internal static class DocumentReaders
{
    private static readonly TimeSpan PandocTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Readers built into Filee: (input file, folder for extracted pictures) → document. Office Open XML variants
    /// (macro-enabled files, templates, slide shows) share the reader of their family.
    /// </summary>
    private static readonly Dictionary<string, Func<string, string, HDocument>> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = DocxReader.Read,
        ["docm"] = DocxReader.Read,
        ["dotx"] = DocxReader.Read,
        ["dotm"] = DocxReader.Read,
        ["txt"] = (path, _) => TextDocument(File.ReadAllBytes(path)),
        ["md"] = (path, _) => MarkdownReader.Read(DecodeText(File.ReadAllBytes(path)), Path.GetDirectoryName(Path.GetFullPath(path))!),
        ["xlsx"] = (path, _) => SheetDocument.Build(XlsxReader.Read(path)),
        ["xlsm"] = (path, _) => SheetDocument.Build(XlsxReader.Read(path)),
        ["xltx"] = (path, _) => SheetDocument.Build(XlsxReader.Read(path)),
        ["csv"] = (path, _) => SheetDocument.Build(CsvFormat.Read(path)),
        ["tsv"] = (path, _) => SheetDocument.Build(SpreadsheetConverter.Read(path, "tsv")),
        ["xls"] = (path, _) => SheetDocument.Build(SpreadsheetConverter.Read(path, "xls")),
        ["xlt"] = (path, _) => SheetDocument.Build(SpreadsheetConverter.Read(path, "xlt")),
        ["ods"] = (path, _) => SheetDocument.Build(SpreadsheetConverter.Read(path, "ods")),
        ["pptx"] = PptxReader.Read,
        ["pptm"] = PptxReader.Read,
        ["potx"] = PptxReader.Read,
        ["ppsx"] = PptxReader.Read,
        ["pdf"] = PdfDocumentReader.Read,
        // HWP comes here as HWPX, converted by rhwp first (the route planner adds that step).
        ["hwpx"] = HwpxReader.Read,
        ["html"] = HtmlReader.Read,
        ["epub"] = EpubReader.Read,
        ["kepub"] = EpubReader.Read, // a Kobo EPUB: its koboSpan markup is plain spans to the HTML reader
        ["mobi"] = MobiReader.Read,
        ["azw3"] = MobiReader.Read,
        ["azw"] = MobiReader.Read,
        ["prc"] = MobiReader.Read,
        ["fb2"] = Fb2Reader.Read,
        ["htmlz"] = ZippedText.ReadHtmlz,
        ["txtz"] = ZippedText.ReadTxtz,
    };

    /// <summary>
    /// Formats read through Pandoc's JSON AST (PandocAstReader) when Pandoc is installed: format id → Pandoc reader.
    /// A built-in reader for the same format wins.
    /// </summary>
    private static readonly Dictionary<string, string> ThroughPandoc = new(StringComparer.OrdinalIgnoreCase)
    {
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["html"] = "html",
        ["rst"] = "rst",
        ["tex"] = "latex",
        ["org"] = "org",
        ["adoc"] = "asciidoc",
        ["typ"] = "typst",
        ["mediawiki"] = "mediawiki",
        ["dbk"] = "docbook",
        ["ipynb"] = "ipynb",
        ["textile"] = "textile",
        ["man"] = "man",
        ["opml"] = "opml",
    };

    /// <summary>Formats read without any optional engine.</summary>
    public static IEnumerable<string> BuiltInFormats => BuiltIn.Keys;

    /// <summary>
    /// Extra cost of reading a PDF: its text is rebuilt from positioned words, so a route that keeps the source's own
    /// structure wins (HWP → HWPX → DOCX rather than HWP → PDF → DOCX), while PDF files themselves still convert.
    /// </summary>
    public const int PdfPenalty = 6;

    /// <summary>Formats that need Pandoc (those without a built-in reader).</summary>
    public static IEnumerable<string> PandocFormats => ThroughPandoc.Keys.Where(f => !BuiltIn.ContainsKey(f));

    /// <summary>Every readable format; the Pandoc ones only when Pandoc is available.</summary>
    public static IEnumerable<string> Formats(bool pandoc) => pandoc ? BuiltInFormats.Concat(PandocFormats) : BuiltInFormats;

    /// <summary>True when <paramref name="format"/> can only be read with Pandoc.</summary>
    public static bool NeedsPandoc(string format) => !BuiltIn.ContainsKey(format) && ThroughPandoc.ContainsKey(format);

    /// <summary>Reads <paramref name="path"/> (of format <paramref name="format"/>) into the document model.</summary>
    /// <param name="workDirectory">Scratch folder of the job: extracted pictures go into a new folder inside it.</param>
    public static async Task<HDocument> ReadAsync(string path, string format, string workDirectory, CancellationToken cancellationToken)
    {
        var media = Path.Combine(workDirectory, $"media-{Guid.NewGuid():N}");
        if (BuiltIn.TryGetValue(format, out var reader))
            return await Task.Run(() => reader(path, media), cancellationToken);
        if (ThroughPandoc.TryGetValue(format, out var pandocReader))
            return await ReadWithPandocAsync(path, pandocReader, workDirectory, media, cancellationToken);
        throw new NotSupportedException($"There is no document reader for {format.ToUpperInvariant()} files.");
    }

    // ───────────────────────── Pandoc ─────────────────────────

    private static async Task<HDocument> ReadWithPandocAsync(string path, string pandocReader, string workDirectory, string media, CancellationToken cancellationToken)
    {
        var pandoc = PandocConverter.Locate() ?? throw new InvalidOperationException("Pandoc was not found.");
        var json = Path.Combine(workDirectory, $"ast-{Guid.NewGuid():N}.json");
        var inputFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // Working directory = the input's folder, so relative image paths in HTML and LaTeX resolve.
        var result = await ProcessRunner.RunAsync(pandoc,
            [path, "-f", pandocReader, "-t", "json", "--extract-media=" + media, "-o", json],
            PandocTimeout, cancellationToken, workingDirectory: inputFolder);
        if (!File.Exists(json))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        var ast = JsonNode.Parse(await File.ReadAllTextAsync(json, cancellationToken))
                  ?? throw new InvalidDataException("Pandoc produced an empty document.");
        return PandocAstReader.Read(ast, target => ResolveImage(target, inputFolder));
    }

    /// <summary>Images extracted by Pandoc have absolute paths; HTML may use paths relative to the input.</summary>
    private static string? ResolveImage(string target, string inputFolder)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains("://", StringComparison.Ordinal))
            return null;
        var path = Uri.UnescapeDataString(target);
        foreach (var candidate in new[] { path, Path.Combine(inputFolder, path) })
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // ───────────────────────── Plain text ─────────────────────────

    /// <summary>Plain text: one paragraph per line, text kept verbatim.</summary>
    internal static HDocument TextDocument(byte[] bytes)
    {
        var section = new HSection();
        foreach (var line in DecodeText(bytes).Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new HParagraph();
            if (line.Length > 0)
                paragraph.Inlines.Add(new HText(line, default));
            section.Blocks.Add(paragraph);
        }
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 with BOM; anything else is read as CP949, common for Korean text.</summary>
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }
}
