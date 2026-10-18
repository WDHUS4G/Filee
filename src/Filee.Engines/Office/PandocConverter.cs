// General document conversions with Pandoc (GPL-2.0-or-later, bundled as a separate program in engines/pandoc).
// Adds Markdown ↔ DOCX / ODT / RTF, HTML ↔ DOCX / ODT, the markup languages (reStructuredText, LaTeX, Org, AsciiDoc,
// Typst, MediaWiki, DocBook, Jupyter notebooks, Textile, man pages, OPML) ↔ Markdown / HTML / DOCX / ODT and each
// other (and → RTF, EPUB, TXT), and Markdown and the lightweight markups → PPTX slides. Markdown → HTML / TXT / HWPX /
// DOCX is built in (MarkdownConverter, the HWPX and DOCX writers).

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Markdown, HTML, DOCX, ODT, RTF and markup language conversions through Pandoc.</summary>
public sealed class PandocConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, string> Readers = new()
    {
        ["md"] = "markdown",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
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

    private static readonly Dictionary<string, string> Writers = new()
    {
        ["md"] = "gfm",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["txt"] = "plain",
        ["rst"] = "rst",
        ["tex"] = "latex",
        ["epub"] = "epub",
        ["org"] = "org",
        ["adoc"] = "asciidoc",
        ["typ"] = "typst",
        ["mediawiki"] = "mediawiki",
        ["dbk"] = "docbook5",
        ["ipynb"] = "ipynb",
        ["textile"] = "textile",
        ["man"] = "man",
        ["opml"] = "opml",
        ["pptx"] = "pptx",
    };

    /// <summary>
    /// Markup languages Pandoc reads and writes well: to and from Markdown, HTML, DOCX and ODT, and into each other.
    /// No PDF, which would need a TeX or Typst engine (PDF comes from the HWPX writer and rhwp instead).
    /// </summary>
    internal static readonly string[] Markup = ["rst", "tex", "org", "adoc", "typ", "mediawiki", "dbk", "ipynb", "textile", "man", "opml"];

    /// <summary>Sources of Pandoc's PowerPoint writer (a slide per heading): Markdown and the lightweight markups.</summary>
    internal static readonly string[] Slides = ["md", "rst", "org", "adoc", "typ", "mediawiki", "textile", "ipynb"];

    /// <summary>Cost of the slide edges, see <see cref="Edges"/>.</summary>
    private const int SlidesCost = 20;

    /// <summary>Targets that are complete documents only with <c>--standalone</c> (root element, header, preamble).</summary>
    private static readonly string[] Standalone = ["html", "tex", "dbk", "man", "opml", "typ"];

    /// <summary>Text targets whose pictures (from DOCX, ODT, EPUB, ...) are extracted into a folder next to them.</summary>
    private static readonly string[] ExtractsMedia = ["md", "html", "rst", "tex", "org", "adoc", "typ", "mediawiki", "textile", "dbk"];

    private string? _pandoc;

    public string Id => "pandoc";
    public string DisplayName => "Pandoc";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        // Markdown in / out: only Pandoc does this.
        .. new[] { "docx", "odt", "html", "rtf" }.Select(from => new ConversionEdge(from, "md")),
        .. new[] { "docx", "odt", "html", "rtf", "txt" }.Select(to => new ConversionEdge("md", to)),
        // Overlaps with LibreOffice (the fallback engine, which costs more).
        .. new[] { "docx", "odt" }.Select(to => new ConversionEdge("html", to, 14)),
        .. new[] { "docx", "odt" }.Select(from => new ConversionEdge(from, "html", 14)),
        // Markup languages in / out: only Pandoc does this. Writing one costs a little more than writing Markdown, so a
        // route with a Pandoc step in the middle (HTML → ? → RTF) goes through Markdown, not through Org or Typst.
        .. from markup in Markup
           from to in new[] { "md", "html", "docx", "odt", "rtf", "epub", "txt" }
           select new ConversionEdge(markup, to),
        .. from markup in Markup
           from source in new[] { "md", "html", "docx", "odt" }.Concat(Markup)
           where source != markup
           select new ConversionEdge(source, markup, 11),
        // Slides from headings: nothing else writes PPTX from text. Expensive, so text → PDF never takes the shortcut
        // through slides and LibreOffice's cheap PPTX → PDF (Markdown → PPTX → PDF would beat Markdown → HWPX → PDF).
        .. Slides.Select(from => new ConversionEdge(from, "pptx", SlidesCost)),
    ];

    public EngineStatus GetStatus()
    {
        _pandoc = Locate();
        return _pandoc is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_pandoc, EngineEnvironment.UsesFileesCopy("pandoc") ? EngineVersions.Component("pandoc") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pandoc = _pandoc ?? Locate() ?? throw new InvalidOperationException("Pandoc was not found.");
        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];

        progress?.Report(0.1);
        List<string> args = [step.InputPath, "-f", Readers[step.From], "-t", Writers[step.To], "-o", output];
        if (Standalone.Contains(step.To))
            args.Add("--standalone");
        if (ExtractsMedia.Contains(step.To))
            args.Add("--extract-media=" + Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files"));

        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(1.5)))
            result = await ProcessRunner.RunAsync(pandoc, args, Timeout, cancellationToken,
                workingDirectory: Path.GetDirectoryName(Path.GetFullPath(step.InputPath)));
        if (!File.Exists(output))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        progress?.Report(1);
        return [output];
    }

    /// <summary>Uses the selected copy, a bundled copy, then the system installation.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("pandoc", "pandoc.exe") ?? LocateBundled() ?? EngineEnvironment.SystemProgram("pandoc", "pandoc.exe");

    private static string? LocateBundled() =>
        EngineEnvironment.BundledProgram("pandoc", "pandoc.exe");
}
