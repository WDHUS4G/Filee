// General document conversions with Pandoc (GPL-2.0-or-later, bundled as a separate program in engines/pandoc).
// Adds Markdown ↔ DOCX / ODT / RTF, HTML ↔ DOCX / ODT, the markup languages (reStructuredText, LaTeX, Org, AsciiDoc,
// Typst, MediaWiki, DocBook, Jupyter notebooks, Textile, man pages, OPML) ↔ Markdown / HTML / DOCX / ODT and each
// other (and → RTF, EPUB, TXT), and Markdown and the lightweight markups → PPTX slides. Markdown → HTML / TXT / HWPX /
// DOCX is built in (MarkdownConverter, the HWPX and DOCX writers). Pictures of a text result are extracted into a
// "<name>_files" folder next to it and linked relative to it (EMF/WMF become PNG); Markdown with "Leave out pictures"
// gets none (a Lua filter drops them). Markdown stays free of HTML: tables come out as plain pipe tables (a Lua
// filter splits merged cells) and pictures as ![](…).

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;

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
        var input = Path.GetFullPath(step.InputPath);
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        List<string> args = [input, "-f", Readers[step.From], "-t", Writers[step.To], "-o", output];
        if (Standalone.Contains(step.To))
            args.Add("--standalone");
        if (step.To == "md")
            args.Add("--lua-filter=" + Filter(step.WorkDirectory, "markdown", PlainMarkdown));
        string? media = null;
        if (step.To == "md" && step.Preset.Document.LeaveOutPictures)
            args.Add("--lua-filter=" + Filter(step.WorkDirectory, "no-pictures", WithoutPictures));
        else if (ExtractsMedia.Contains(step.To))
        {
            // Relative to the result (Pandoc runs in its folder), so the links keep working when both are moved.
            // Pictures the source refers to by a relative path are still looked up next to the source.
            media = Path.GetFileNameWithoutExtension(output) + "_files";
            args.Add("--extract-media=" + media);
            args.Add("--resource-path=" + Path.GetDirectoryName(input));
        }

        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(1.5)))
            result = await ProcessRunner.RunAsync(pandoc, args, Timeout, cancellationToken,
                workingDirectory: media is null ? Path.GetDirectoryName(input) : outputFolder);
        if (!File.Exists(output))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        if (media is not null)
            MetafilesToPng(output, Path.Combine(outputFolder, media), step.Preset);
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// Word documents often hold EMF/WMF pictures, which browsers and Markdown editors don't show: each extracted one
    /// becomes a PNG and the result links to that. Where ImageMagick can't draw metafiles (outside Windows), they stay.
    /// </summary>
    private static void MetafilesToPng(string output, string mediaFolder, Preset preset)
    {
        if (!Directory.Exists(mediaFolder))
            return;
        var text = File.ReadAllText(output);
        var changed = false;
        foreach (var metafile in Directory.EnumerateFiles(mediaFolder, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".emf" or ".wmf").ToList())
        {
            var png = Path.ChangeExtension(metafile, ".png");
            if (File.Exists(png))
                continue;
            try
            {
                using var images = ImageReader.Read(metafile, Path.GetExtension(metafile)[1..].ToLowerInvariant(), preset);
                images[0].Write(png, MagickFormat.Png);
            }
            catch (Exception ex) when (ex is MagickException or IOException)
            {
                continue;
            }
            var from = Path.GetRelativePath(Path.GetDirectoryName(output)!, metafile).Replace('\\', '/');
            text = text.Replace(from, Path.ChangeExtension(from, ".png"), StringComparison.Ordinal);
            File.Delete(metafile);
            changed = true;
        }
        if (changed)
            File.WriteAllText(output, text);
    }

    /// <summary>Writes a Lua filter into the job's work folder and returns its path.</summary>
    private static string Filter(string workDirectory, string name, string code)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, $"{name}-{Guid.NewGuid():N}.lua");
        File.WriteAllText(path, code);
        return path;
    }

    /// <summary>
    /// Drops every picture ("Leave out pictures", DocumentOptions.LeaveOutPictures); a figure keeps its caption as text.
    /// </summary>
    private const string WithoutPictures = """
        function Image(image) return {} end
        function Figure(figure) return figure.caption.long end
        """;

    /// <summary>
    /// Keeps the Markdown free of HTML, which many Markdown editors show as code (#391). GitHub-flavoured Markdown
    /// tables have one header row, one line per cell and no merged cells, and Pandoc writes any other table as HTML:
    /// this lays every table out as a plain grid instead. A merged cell keeps its text in its first position and leaves
    /// the others empty, the paragraphs and list items of a cell are joined with &lt;br&gt;, and a table without a
    /// header row gets its first row as the header. Pictures lose their Word size, which only an &lt;img&gt; can hold.
    /// </summary>
    private const string PlainMarkdown = """
        local br = pandoc.RawInline('html', '<br>')

        local function inlines_of(blocks)
          local out = pandoc.Inlines{}
          local function add(inl)
            if #inl == 0 then return end
            if #out > 0 then out:insert(br) end
            out:extend(inl)
          end
          for _, block in ipairs(blocks) do
            if block.t == 'Para' or block.t == 'Plain' then
              add(block.content)
            elseif block.t == 'BulletList' or block.t == 'OrderedList' then
              for i, item in ipairs(block.content) do
                local mark = block.t == 'BulletList' and '•' or (tostring(i) .. '.')
                local inl = pandoc.Inlines{pandoc.Str(mark), pandoc.Space()}
                inl:extend(inlines_of(item))
                add(inl)
              end
            elseif block.t == 'LineBlock' then
              for _, line in ipairs(block.content) do add(line) end
            elseif block.t == 'Div' or block.t == 'BlockQuote' then
              add(inlines_of(block.content))
            else
              local text = pandoc.utils.stringify(block)
              if text ~= '' then add(pandoc.Inlines{pandoc.Str(text)}) end
            end
          end
          return out:walk{
            LineBreak = function() return br end,
            SoftBreak = function() return pandoc.Space() end,
          }
        end

        local function empty() return pandoc.Cell({}) end

        -- Rows of one section (head, an intermediate head, a body, the foot); row spans stay within it.
        local function grid(rows, columns)
          local below = {}
          for _, row in ipairs(rows) do
            local cells = pandoc.List()
            local source = row.cells
            local next = 1
            local column = 1
            while column <= columns do
              if (below[column] or 0) > 0 then
                below[column] = below[column] - 1
                cells:insert(empty())
                column = column + 1
              elseif next <= #source then
                local cell = source[next]
                next = next + 1
                local text = inlines_of(cell.contents)
                cells:insert(pandoc.Cell(#text > 0 and {pandoc.Plain(text)} or {}, cell.alignment, 1, 1, cell.attr))
                for c = column, column + cell.col_span - 1 do
                  if c > column then cells:insert(empty()) end
                  if cell.row_span > 1 then below[c] = cell.row_span - 1 end
                end
                column = column + cell.col_span
              else
                cells:insert(empty())
                column = column + 1
              end
            end
            while #cells > columns do cells:remove() end
            row.cells = cells
          end
          return rows
        end

        function Image(image)
          image.attributes = {}
          return image
        end

        function Table(tbl)
          local columns = #tbl.colspecs
          if #tbl.head.rows == 0 and #tbl.bodies > 0 and #tbl.bodies[1].body > 0 then
            tbl.head.rows:insert(tbl.bodies[1].body:remove(1))
          end
          tbl.head.rows = grid(tbl.head.rows, columns)
          for _, body in ipairs(tbl.bodies) do
            body.head = grid(body.head, columns)
            body.body = grid(body.body, columns)
          end
          tbl.foot.rows = grid(tbl.foot.rows, columns)
          return tbl
        end
        """;

    /// <summary>Uses the selected copy, a bundled copy, then the system installation.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("pandoc", "pandoc.exe") ?? LocateBundled() ?? EngineEnvironment.SystemProgram("pandoc", "pandoc.exe");

    private static string? LocateBundled() =>
        EngineEnvironment.BundledProgram("pandoc", "pandoc.exe");
}
