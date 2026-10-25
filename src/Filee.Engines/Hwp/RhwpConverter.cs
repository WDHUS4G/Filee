// HWP / HWPX → PDF, HWP → HWPX and HWPX → HWP with the rhwp command line tool (MIT, https://github.com/edwardkim/rhwp).
// Bundled under engines/rhwp. Works without Hancom Office, so there is no automation approval dialog.
//
// rhwp draws text one character after the other, left to right, without OpenType shaping: Arabic letters don't join
// and run the wrong way, Indic vowel signs and conjuncts come out as separate glyphs with dotted circles (issues #185,
// #202). Its PDF edges are marked Unshaped, so a document with such text (found by ComplexScripts) goes to LibreOffice,
// or is refused with that reason when LibreOffice is missing. rhwp's width tables cover only ASCII and Latin-1,
// general punctuation, math, the CJK forms and Hangul, so a letter of any other script — Polish, Turkish, Vietnamese,
// Greek, Cyrillic, ... — overlaps at a fixed half-em (rhwp#8143): such a document takes a route without rhwp when
// one exists, and is never refused for it.

using System.IO.Compression;
using System.Xml;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Hwp;

/// <summary>Renders HWP and HWPX documents to PDF and converts between the two 한글 formats.</summary>
public sealed class RhwpConverter : IConverter, ITextShapingCheck
{
    // rhwp renders text slowly: about 1 s per KB of section XML (some 4 s per page of a novel) with rhwp 0.8.7 on an
    // i7-1185G7, so a 640-page book takes some 48 minutes; export-hwpx and convert take seconds for the same book. The
    // time limit grows with the document's text, and still stops a rhwp that hangs.
    //
    // Nearly all of that time goes into the PDF backend, not the layout: the default one draws each page as SVG with
    // one text element per character and converts that to PDF, a few ms per character. Its "direct" backend paints the
    // same layout straight into the PDF 30 to 100 times faster (a 65-page workbook of 180 000 characters: 24 minutes →
    // 20-45 s), but refuses pages with what it can't paint (gradients, patterns, shadows, connectors) and takes neither
    // the generic fallback families nor the equation font of RhwpInput.PdfOptions. Documents Filee writes from a
    // spreadsheet (text in table cells, borders, fills; SheetDocument) use it, with the SVG backend when it fails.
    private static readonly TimeSpan BaseTimeLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimeLimitPerMegabyte = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxTimeLimit = TimeSpan.FromMinutes(60);

    /// <summary>Usual rendering time per MB of section XML, for an HWP (whose characters aren't counted).</summary>
    private static readonly TimeSpan TypicalPerMegabyte = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Usual rendering time per drawn character with the SVG and the direct backend (drives the progress estimate).
    /// Characters, not XML, set the pace: a table cell takes some 600 bytes of XML for a few characters.
    /// </summary>
    private static readonly TimeSpan TypicalPerCharacter = TimeSpan.FromMilliseconds(3.5);
    private static readonly TimeSpan TypicalPerCharacterDirect = TimeSpan.FromMilliseconds(0.2);

    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(1.5);

    private string? _exe;

    public string Id => "rhwp";
    public string DisplayName => "rhwp";
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "pdf", Unshaped: true),
        new("hwpx", "pdf", Unshaped: true),
        // Lossless within the 한글 document model; anything → HWP goes through the HWPX writer first.
        new("hwp", "hwpx"),
        new("hwpx", "hwp"),
    ];

    /// <summary>
    /// The scripts in the source's text that rhwp can't draw: one that needs shaping, and one it draws but has no
    /// widths for — letters outside ASCII and Latin-1 other than the full-width CJK it estimates at an em
    /// (rhwp#8143). Both answers come from one pass. Every format that reaches rhwp through the HWPX writer is
    /// scanned (DOCX, TXT, Markdown, HTML, PPTX, XLSX, EPUB, HWPX, ...); a binary HWP is not.
    /// </summary>
    public TextScripts ScanScripts(string sourcePath, string sourceFormat)
    {
        var (shaping, unmeasured) = ComplexScripts.FindBothInFile(sourcePath, sourceFormat);
        // An HWPX stays with rhwp, the most faithful renderer for it; only shaping sends it elsewhere.
        return new TextScripts(shaping, sourceFormat == "hwpx" ? null : unmeasured);
    }

    public EngineStatus GetStatus()
    {
        _exe = Locate();
        return _exe is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_exe, EngineVersions.Component("rhwp"));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _exe ?? Locate() ?? throw new InvalidOperationException("rhwp was not found.");
        var temp = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.{step.To}");
        try
        {
            // Intermediate HWPX files include template fonts the user's original document never requested.
            if (step.To == "pdf" && DocumentFonts.Missing(step.OriginalSourcePath ?? step.InputPath) is [_, ..] missing)
                step.MissingFonts?.Invoke(missing);
            var input = step.To == "pdf" ? await PrepareAsync(exe, step, cancellationToken) : step.InputPath;
            var text = TextSize(input);
            var characters = step.To == "pdf" ? Characters(input) : null;
            progress?.Report(0.1);

            if (step.To == "pdf" && UsesDirectBackend(step.OriginalSourcePath ?? step.InputPath))
            {
                ProcessResult direct;
                using (ProgressEstimate.Start(progress, Typical(characters, text, direct: true)))
                    direct = await ProcessRunner.RunAsync(exe, ["export-pdf", input, "-o", temp, "--backend", "direct"], TimeLimit(text), cancellationToken);
                if (direct.ExitCode == 0 && Written(temp))
                    return Finish(step, temp, progress);
                File.Delete(temp); // the SVG backend draws what the direct one refused
            }

            string[] arguments = step.To switch
            {
                "pdf" => ["export-pdf", input, "-o", temp, .. RhwpInput.PdfOptions()],
                "hwpx" => ["export-hwpx", step.InputPath, temp],
                "hwp" => ["convert", step.InputPath, temp],
                _ => throw new NotSupportedException(step.To),
            };
            ProcessResult result;
            using (ProgressEstimate.Start(progress, step.To == "pdf" ? Typical(characters, text, direct: false) : TypicalDuration))
                result = await ProcessRunner.RunAsync(exe, arguments, TimeLimit(text), cancellationToken);
            if (!Written(temp))
                throw new InvalidOperationException($"rhwp failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        }
        catch (TimeoutException ex)
        {
            // E-books reach rhwp through the HWPX writer; calibre makes their PDFs much faster when it is ranked above it.
            throw new ConversionRefusedException(
                CalibreConverter.Locate() is null ? "error.rhwp_timeout" : "error.rhwp_timeout_calibre", ex.Message);
        }

        return Finish(step, temp, progress);
    }

    private static IReadOnlyList<string> Finish(ConversionStep step, string temp, IProgress<double>? progress)
    {
        var target = step.Output.Allocate(step.To);
        if (target is null)
            return [];
        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        return [target];
    }

    private static bool Written(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>
    /// Whether the PDF of a document converted from <paramref name="sourcePath"/> is drawn with rhwp's direct backend:
    /// one Filee wrote from a spreadsheet.
    /// </summary>
    internal static bool UsesDirectBackend(string sourcePath) =>
        FormatRegistry.Detect(sourcePath) is { } format && SheetDocument.SourceFormats.Contains(format.Id);

    /// <summary>
    /// The document rhwp renders: a copy with the fixes of <see cref="RhwpInput"/> (fonts, shape sizes, equation
    /// scripts, tables floating in cells) when it needs any, else the original. An HWP is read through an HWPX copy for
    /// that, and used as it is when nothing needs fixing or the HWPX copy fails.
    /// </summary>
    private static async Task<string> PrepareAsync(string exe, ConversionStep step, CancellationToken cancellationToken)
    {
        var hwpx = step.InputPath;
        if (step.From == "hwp")
        {
            hwpx = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.hwpx");
            await ProcessRunner.RunAsync(exe, ["export-hwpx", step.InputPath, hwpx], TimeLimit(TextSize(step.InputPath)), cancellationToken);
            if (!File.Exists(hwpx) || new FileInfo(hwpx).Length == 0)
                return step.InputPath;
        }
        return RhwpInput.Prepare(hwpx, step.WorkDirectory) ?? step.InputPath;
    }

    /// <summary>How long one rhwp run on a document with <paramref name="textBytes"/> of text may take.</summary>
    internal static TimeSpan TimeLimit(long textBytes)
    {
        var limit = BaseTimeLimit + TimeLimitPerMegabyte * (textBytes / 1048576.0);
        return limit < MaxTimeLimit ? limit : MaxTimeLimit;
    }

    /// <summary>The usual time of an <c>export-pdf</c> run: by its characters, else (an HWP) by its text size.</summary>
    internal static TimeSpan Typical(long? characters, long textBytes, bool direct)
    {
        var typical = characters is { } count
            ? (direct ? TypicalPerCharacterDirect : TypicalPerCharacter) * count
            : TypicalPerMegabyte * (textBytes / 1048576.0);
        return typical > TypicalDuration ? typical : TypicalDuration;
    }

    /// <summary>
    /// The characters rhwp draws in an HWPX (the text of its sections without spaces: one SVG element each), or null for
    /// a file that isn't an HWPX package.
    /// </summary>
    internal static long? Characters(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var sections = zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                                                  && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sections.Count == 0)
                return null;
            long count = 0;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            foreach (var section in sections)
            {
                using var stream = section.Open();
                using var reader = XmlReader.Create(stream, settings);
                var depth = -1; // depth of the hp:t element being read, -1 outside one
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && depth < 0 && reader.LocalName == "t" && !reader.IsEmptyElement)
                        depth = reader.Depth;
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                        depth = -1;
                    else if (depth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                        foreach (var c in reader.Value)
                            if (!char.IsWhiteSpace(c))
                                count++;
                }
            }
            return count;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException)
        {
            return null; // not a ZIP (an HWP) or not readable XML: the text size stands in
        }
    }

    /// <summary>
    /// The size of a document's text, which rendering time follows: the unpacked section XML of an HWPX (pictures left
    /// out), else three times the file (an HWP deflates its body text to about a third).
    /// </summary>
    internal static long TextSize(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var sections = zip.Entries
                .Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Sum(e => e.Length);
            if (sections > 0)
                return sections;
        }
        catch (InvalidDataException) { } // not a ZIP: an HWP
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new FileInfo(path).Length * 3;
    }

    /// <summary>Filee's own copy (engines/rhwp), never one found elsewhere on the system.</summary>
    private static string? Locate() =>
        EngineEnvironment.FindBundled("rhwp") is { } bundled
            ? EngineEnvironment.FirstExisting(Path.Combine(bundled, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp"))
            : null;
}
