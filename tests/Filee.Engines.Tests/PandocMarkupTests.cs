// reStructuredText and LaTeX through Pandoc (optional engine): to and from Markdown, HTML, DOCX, EPUB, and to HWPX
// through Pandoc's AST and the built-in HWPX writer. Skipped when Pandoc is not installed.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class PandocMarkupTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Rst = """
        Weekly notes
        ============

        Decided: **ship it** and *write docs*.

        - first item
        - second item

        Next steps
        ==========

        Two sections of the same level, so the first one stays a heading (a lone one would become the title).
        """;

    private const string Tex = """
        \documentclass{article}
        \begin{document}
        \section{Results}
        The result is \textbf{good} and \emph{fast}.
        \end{document}
        """;

    private void SkipUnlessInstalled() =>
        Assert.SkipUnless(fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "pandoc")).IsAvailable, "Pandoc not installed");

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Markup_routes()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc"]);
        foreach (var (from, to) in new[] { ("rst", "docx"), ("rst", "md"), ("tex", "html"), ("tex", "epub"), ("md", "rst"), ("docx", "tex") })
            Assert.Equal("pandoc", Assert.Single(planner.Plan(from, to)!.Steps).Converter.Id);
        Assert.DoesNotContain(fx.Converters.Single(c => c.Id == "pandoc").Edges, e => e.To == "pdf");

        // The HWPX writer offers its Pandoc-based readers only when Pandoc is really there (not just assumed).
        Assert.SkipUnless(PandocConverter.Locate() is not null, "Pandoc not found (pwsh build/fetch-engines.ps1 -Only pandoc)");
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("rst", "hwpx")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("tex", "hwpx")!.Steps).Converter.Id);
    }

    [Fact]
    public void More_markup_languages_and_slides()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc", "libreoffice"]);
        foreach (var markup in PandocConverter.Markup)
        {
            Assert.NotNull(FormatRegistry.FindById(markup));
            foreach (var other in new[] { "md", "html", "docx", "odt" })
            {
                Assert.Equal("pandoc", Assert.Single(planner.Plan(other, markup)!.Steps).Converter.Id);
                Assert.NotNull(planner.Plan(markup, other));
            }
            Assert.NotNull(planner.Plan(markup, "epub"));
        }
        Assert.Equal("pandoc", Assert.Single(planner.Plan("org", "adoc")!.Steps).Converter.Id);
        Assert.Equal("pandoc", Assert.Single(planner.Plan("md", "pptx")!.Steps).Converter.Id);
        Assert.Equal("pandoc", Assert.Single(planner.Plan("org", "pptx")!.Steps).Converter.Id);
        // Markdown keeps its built-in routes.
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
        Assert.Equal("docx-writer", Assert.Single(planner.Plan("md", "docx")!.Steps).Converter.Id);
        // Extensions that could mean something else are not claimed by markup formats: a plain .xml is data (the data
        // engine), not DocBook.
        Assert.Equal("xml", FormatRegistry.FindByExtension("xml")!.Id);
        Assert.Null(FormatRegistry.FindByExtension("1"));
        Assert.Equal("mediawiki", FormatRegistry.FindByExtension("wiki")!.Id);
        Assert.Equal("adoc", FormatRegistry.FindByExtension("asciidoc")!.Id);

        Assert.SkipUnless(PandocConverter.Locate() is not null, "Pandoc not found (pwsh build/fetch-engines.ps1 -Only pandoc)");
        // Not through slides and LibreOffice's PPTX → PDF.
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("org", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("md", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("typ", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public async Task Markdown_round_trips_through_every_markup_language()
    {
        SkipUnlessInstalled();
        var markdown = await WriteAsync("notes.md", "# Plan\n\nShip **today** and *test*.\n\n- alpha\n- beta\n\n# Later\n\nMore text.\n");

        foreach (var markup in PandocConverter.Markup.Where(m => m is not ("rst" or "tex")))
        {
            var converted = await ConvertAsync(markdown, markup);
            Assert.Equal(markup, FormatRegistry.Detect(converted)!.Id);
            var back = await File.ReadAllTextAsync(await ConvertAsync(converted, "md"), TestContext.Current.CancellationToken);
            Assert.True(back.Contains("today", StringComparison.Ordinal) && back.Contains("alpha", StringComparison.Ordinal), $"{markup}: {back}");
        }
    }

    [Fact]
    public async Task Org_asciidoc_and_typst_reach_hwpx_and_slides()
    {
        SkipUnlessInstalled();
        var org = await WriteAsync("todo.org", "* Groceries\nBuy *milk* and /bread/.\n* Chores\n- sweep\n- dust\n");
        var adoc = await WriteAsync("guide.adoc", "= Guide\n\n== Install\n\nRun the *installer*.\n\n== Use\n\nOpen the app.\n");
        var typ = await WriteAsync("paper.typ", "= Introduction\nTypst is *fast*.\n\n= Method\nWe measure.\n");

        foreach (var (input, text) in new[] { (org, "milk"), (adoc, "installer"), (typ, "fast") })
        {
            var hwpx = await ConvertAsync(input, "hwpx");
            HwpxAssert.ValidPackage(hwpx);
            Assert.Contains(text, HwpxAssert.Xml(hwpx).Value);

            using var pptx = ZipFile.OpenRead(await ConvertAsync(input, "pptx"));
            Assert.Contains(pptx.Entries, e => e.FullName == "ppt/slides/slide2.xml");
        }
    }

    [Fact]
    public async Task Notebooks_outlines_and_man_pages_are_read()
    {
        SkipUnlessInstalled();
        var notebook = await WriteAsync("analysis.ipynb", """
            {"cells": [
              {"cell_type": "markdown", "metadata": {}, "source": ["# Analysis\n", "Results are *good*."]},
              {"cell_type": "code", "metadata": {}, "execution_count": 1, "outputs": [], "source": ["print(42)"]}
             ],
             "metadata": {"kernelspec": {"name": "python3", "display_name": "Python 3", "language": "python"}},
             "nbformat": 4, "nbformat_minor": 5}
            """);
        var outline = await WriteAsync("ideas.opml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0"><head><title>Ideas</title></head>
            <body><outline text="First idea"><outline text="Detail one"/></outline><outline text="Second idea"/></body></opml>
            """);
        var man = await WriteAsync("tool.man", ".TH TOOL 1\n.SH NAME\ntool \\- does things\n.SH DESCRIPTION\nThe \\fBtool\\fR command runs.\n");

        var fromNotebook = await File.ReadAllTextAsync(await ConvertAsync(notebook, "md"), TestContext.Current.CancellationToken);
        Assert.Contains("# Analysis", fromNotebook);
        Assert.Contains("print(42)", fromNotebook);
        var fromOutline = await File.ReadAllTextAsync(await ConvertAsync(outline, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("First idea", fromOutline);
        Assert.Contains("Detail one", fromOutline);
        var fromMan = await File.ReadAllTextAsync(await ConvertAsync(man, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("command runs", fromMan);
    }

    [Fact]
    public async Task Rst_to_docx_markdown_and_hwpx()
    {
        SkipUnlessInstalled();
        var rst = await WriteAsync("notes.rst", Rst);

        var docx = DocxAssert.ReadBack(await ConvertAsync(rst, "docx"));
        var paragraphs = DocxAssert.Paragraphs(docx.Sections.SelectMany(s => s.Blocks)).ToList();
        Assert.Equal(1, paragraphs.Single(p => DocxAssert.TextOf(p) == "Weekly notes").HeadingLevel);

        var md = await File.ReadAllTextAsync(await ConvertAsync(rst, "md"), TestContext.Current.CancellationToken);
        Assert.Contains("# Weekly notes", md);
        Assert.Contains("**ship it**", md);

        var hwpx = await ConvertAsync(rst, "hwpx");
        HwpxAssert.ValidPackage(hwpx);
        Assert.Contains("first item", HwpxAssert.Xml(hwpx).Value);
    }

    [Fact]
    public async Task Latex_to_markdown_epub_and_back()
    {
        SkipUnlessInstalled();
        var tex = await WriteAsync("paper.tex", Tex);

        var md = await File.ReadAllTextAsync(await ConvertAsync(tex, "md"), TestContext.Current.CancellationToken);
        Assert.Contains("# Results", md);
        Assert.Contains("**good**", md);

        using (var epub = ZipFile.OpenRead(await ConvertAsync(tex, "epub")))
            Assert.Equal("application/epub+zip", new StreamReader(epub.GetEntry("mimetype")!.Open()).ReadToEnd());

        var markdown = await WriteAsync("draft.md", "# Title\n\nSome *text*.\n");
        var latex = await File.ReadAllTextAsync(await ConvertAsync(markdown, "tex"), TestContext.Current.CancellationToken);
        Assert.Contains("\\begin{document}", latex); // standalone, ready to compile
        Assert.Contains("\\section{Title}", latex);

        var hwpx = await ConvertAsync(tex, "hwpx");
        HwpxAssert.ValidPackage(hwpx);
        Assert.Contains("The result is", HwpxAssert.Xml(hwpx).Value);
    }
}
