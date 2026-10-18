// Markdown → HDocument with Markdig (BSD-2-Clause), so Markdown → HWPX needs no external engine.
//
// Mirrors PandocAstReader: headings (with their ids as bookmarks), paragraphs, bold / italic / strike / sub- and
// superscript / inserted / marked text, code, lists (1. / a. / i. / A. / I. with the start number, nested levels),
// task lists, block quotes, pipe and grid tables with spans, definition lists, links, images, footnotes and math.
// The YAML front matter's "title" becomes the document title. Raw HTML is skipped, except <br>.

using Markdig;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed class MarkdownReader
{
    /// <summary>Indent per block quote level (same as PandocAstReader).</summary>
    private const int QuoteIndent = 2000;

    /// <summary>GitHub-style Markdown plus the common extensions (tables, footnotes, task lists, math, …).</summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    private static readonly HCharFormat Plain = default;
    private static readonly HCharFormat CodeFormat = new(Shade: "#F1F0F5");
    private readonly string _baseFolder;

    private MarkdownReader(string baseFolder) => _baseFolder = baseFolder;

    /// <summary>Parses Markdown text.</summary>
    /// <param name="markdown">The document text.</param>
    /// <param name="baseFolder">Folder of the Markdown file: relative image paths are resolved against it.</param>
    public static HDocument Read(string markdown, string baseFolder)
    {
        var parsed = Markdown.Parse(markdown, Pipeline);
        var reader = new MarkdownReader(baseFolder);
        var section = new HSection();
        reader.Blocks(parsed, section.Blocks, new Context());
        var document = new HDocument { Title = Title(parsed) };
        document.Sections.Add(section);
        return document;
    }

    /// <summary>List nesting and quote indentation of the blocks being read.</summary>
    private readonly record struct Context(int ListLevel = 0, int Indent = 0);

    // ───────────────────────── Blocks ─────────────────────────

    private void Blocks(ContainerBlock container, List<HBlock> output, Context context)
    {
        foreach (var block in container)
            Block(block, output, context);
    }

    private void Block(Block block, List<HBlock> output, Context context)
    {
        switch (block)
        {
            // First: YAML front matter is a code block and HTML blocks are leaf blocks in Markdig's model.
            case HtmlBlock or YamlFrontMatterBlock or LinkReferenceDefinitionGroup or FootnoteGroup:
                break; // raw HTML, metadata and definitions carry no document text (footnotes are read at their links)
            case HeadingBlock heading:
                output.Add(Heading(heading, context));
                break;
            case ParagraphBlock paragraph:
                output.Add(Paragraph(paragraph.Inline, context));
                break;
            case ListBlock list:
                List(list, output, context);
                break;
            case QuoteBlock quote:
                Blocks(quote, output, context with { Indent = context.Indent + QuoteIndent });
                break;
            case MathBlock math:
                CodeLines(math, output, context, Plain with { Italic = true });
                break;
            case CodeBlock code:
                CodeLines(code, output, context, CodeFormat);
                break;
            case ThematicBreakBlock:
                output.Add(NewParagraph(context));
                break;
            case Table table:
                output.Add(TableBlock(table, context));
                break;
            case DefinitionList definitions:
                DefinitionListBlock(definitions, output, context);
                break;
            case ContainerBlock container:
                Blocks(container, output, context); // figures, custom containers, …
                break;
            case LeafBlock { Inline: { } inline }:
                output.Add(Paragraph(inline, context));
                break;
        }
    }

    private static HParagraph NewParagraph(Context context) =>
        new() { Format = context.Indent > 0 ? new HParaFormat(Left: context.Indent) : default };

    private HParagraph Paragraph(ContainerInline? inlines, Context context)
    {
        var paragraph = NewParagraph(context);
        Inlines(inlines, paragraph.Inlines, Plain);
        return paragraph;
    }

    private HParagraph Heading(HeadingBlock heading, Context context)
    {
        var paragraph = Paragraph(heading.Inline, context);
        paragraph.HeadingLevel = Math.Clamp(heading.Level, 1, 9);
        // Heading ids are link targets ("#id" links, e.g. a table of contents).
        if (heading.GetAttributes().Id is { Length: > 0 } id)
            paragraph.Inlines.Insert(0, new HBookmark(id));
        return paragraph;
    }

    private static void CodeLines(LeafBlock code, List<HBlock> output, Context context, HCharFormat format)
    {
        foreach (var line in code.Lines.ToString().Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = NewParagraph(context);
            if (line.Length > 0)
                paragraph.Inlines.Add(new HText(line, format));
            output.Add(paragraph);
        }
    }

    private static HNumbering Numbering(ListBlock list)
    {
        string[] bullets = ["●", "○", "■"];
        string[] defaultFormats = ["DIGIT", "LATIN_CAPITAL", "ROMAN_SMALL"];
        var numbering = new HNumbering();
        var start = int.TryParse(list.OrderedStart, out var value) ? Math.Max(1, value) : 1;
        for (var level = 1; level <= 7; level++)
        {
            if (!list.IsOrdered)
            {
                numbering.Levels.Add(new HNumberingLevel("DIGIT", bullets[(level - 1) % 3], 1, Bullet: true));
                continue;
            }
            // The first level follows the Markdown list; deeper levels use the usual 1. → A. → i. sequence.
            var format = level > 1 ? defaultFormats[(level - 1) % 3] : list.BulletType switch
            {
                'a' => "LATIN_SMALL",
                'A' => "LATIN_CAPITAL",
                'i' => "ROMAN_SMALL",
                'I' => "ROMAN_CAPITAL",
                _ => "DIGIT",
            };
            var text = list.OrderedDelimiter == ')' ? $"^{level})" : $"^{level}.";
            numbering.Levels.Add(new HNumberingLevel(format, text, level == 1 ? start : 1, Bullet: false));
        }
        return numbering;
    }

    private void List(ListBlock list, List<HBlock> output, Context context)
    {
        var numbering = Numbering(list);
        foreach (var item in list.OfType<ListItemBlock>())
        {
            // Only the first paragraph of an item carries the bullet or number.
            var first = true;
            foreach (var block in item)
            {
                switch (block)
                {
                    case ParagraphBlock paragraphBlock:
                        {
                            var paragraph = Paragraph(paragraphBlock.Inline, context);
                            paragraph.List = new HListRef(numbering, context.ListLevel, Numbered: first);
                            output.Add(paragraph);
                            first = false;
                            break;
                        }
                    case ListBlock nested:
                        List(nested, output, context with { ListLevel = context.ListLevel + 1 });
                        break;
                    default:
                        Block(block, output, context);
                        first = false;
                        break;
                }
            }
        }
    }

    private HTable TableBlock(Table table, Context context)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = Math.Max(table.ColumnDefinitions.Count, rows.Count == 0 ? 1 : rows.Max(r => r.OfType<TableCell>().Sum(c => Math.Max(1, c.ColumnSpan))));
        var widths = table.ColumnDefinitions.Select(d => (double)d.Width / 100).ToList();
        while (widths.Count < columns)
            widths.Add(0);
        var hTable = new HTable
        {
            ColumnCount = columns,
            RelativeWidths = widths.Take(columns).All(w => w <= 0) ? null : [.. widths.Take(columns)],
            Indent = context.Indent,
        };
        foreach (var row in rows)
        {
            var hRow = new HRow { Header = row.IsHeader };
            foreach (var cell in row.OfType<TableCell>())
            {
                var hCell = new HCell
                {
                    RowSpan = Math.Max(1, cell.RowSpan),
                    ColSpan = Math.Max(1, cell.ColumnSpan),
                };
                Blocks(cell, hCell.Blocks, new Context());
                if (row.IsHeader)
                    BoldHeader(hCell.Blocks);
                hRow.Cells.Add(hCell);
            }
            hTable.Rows.Add(hRow);
        }
        return hTable;
    }

    /// <summary>Header cells are bold, as Markdown renderers show them.</summary>
    private static void BoldHeader(List<HBlock> blocks)
    {
        foreach (var paragraph in blocks.OfType<HParagraph>())
            for (var i = 0; i < paragraph.Inlines.Count; i++)
                if (paragraph.Inlines[i] is HText text && text.Format.Bold is null)
                    paragraph.Inlines[i] = new HText(text.Text, text.Format with { Bold = true });
    }

    private void DefinitionListBlock(DefinitionList definitions, List<HBlock> output, Context context)
    {
        foreach (var item in definitions.OfType<DefinitionItem>())
        {
            foreach (var block in item)
            {
                if (block is DefinitionTerm term)
                {
                    var paragraph = NewParagraph(context);
                    Inlines(term.Inline, paragraph.Inlines, new HCharFormat(Bold: true));
                    output.Add(paragraph);
                }
                else
                {
                    Block(block, output, context with { Indent = context.Indent + QuoteIndent });
                }
            }
        }
    }

    // ───────────────────────── Inlines ─────────────────────────

    private void Inlines(ContainerInline? container, List<HInline> output, HCharFormat format)
    {
        if (container is null)
            return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Add(new HText(literal.Content.ToString(), format));
                    break;
                case CodeInline code:
                    output.Add(new HText(code.Content, format with { Shade = CodeFormat.Shade }));
                    break;
                case LineBreakInline lineBreak:
                    output.Add(lineBreak.IsHard ? new HLineBreak(format) : new HText(" ", format));
                    break;
                case EmphasisInline emphasis:
                    Inlines(emphasis, output, Emphasis(emphasis, format));
                    break;
                case LinkInline { IsImage: true } image:
                    if (Image(image) is { } picture)
                        output.Add(picture);
                    break;
                case LinkInline link:
                    Link(link.Url, link, output, format);
                    break;
                case AutolinkInline autolink:
                    {
                        var target = autolink.IsEmail && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                            ? "mailto:" + autolink.Url
                            : autolink.Url;
                        var hLink = new HLink(target);
                        hLink.Content.Add(new HText(autolink.Url, format));
                        output.Add(hLink);
                        break;
                    }
                case HtmlEntityInline entity:
                    output.Add(new HText(entity.Transcoded.ToString(), format));
                    break;
                case HtmlInline html:
                    if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                        output.Add(new HLineBreak(format));
                    break; // other raw HTML carries no text of its own
                case TaskList task:
                    output.Add(new HText(task.Checked ? "☑" : "☐", format)); // the following text starts with a space
                    break;
                case MathInline math:
                    output.Add(new HText(math.Content.ToString(), format with { Italic = true }));
                    break;
                case FootnoteLink { IsBackLink: false } footnote:
                    {
                        var note = new HNote(endnote: false);
                        Blocks(footnote.Footnote, note.Blocks, new Context());
                        output.Add(note);
                        break;
                    }
                case FootnoteLink:
                    break; // the "↩" back link inside a footnote
                case ContainerInline other:
                    Inlines(other, output, format);
                    break;
            }
        }
    }

    private static HCharFormat Emphasis(EmphasisInline emphasis, HCharFormat format) => emphasis.DelimiterChar switch
    {
        '*' or '_' when emphasis.DelimiterCount >= 3 => format with { Bold = true, Italic = true },
        '*' or '_' when emphasis.DelimiterCount == 2 => format with { Bold = true },
        '*' or '_' => format with { Italic = true },
        '~' when emphasis.DelimiterCount >= 2 => format with { Strike = true },
        '~' => format with { Subscript = true, Superscript = null },
        '^' => format with { Superscript = true, Subscript = null },
        '+' => format with { Underline = true },
        '=' => format with { Shade = "#FFF3A3" },
        _ => format,
    };

    private void Link(string? url, ContainerInline content, List<HInline> output, HCharFormat format)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Inlines(content, output, format);
            return;
        }
        var link = new HLink(url);
        Inlines(content, link.Content, format);
        output.Add(link);
    }

    private HImage? Image(LinkInline image)
    {
        var path = ResolveImage(image.Url, _baseFolder);
        if (path is null)
            return null; // remote or missing image: skip rather than produce a broken reference

        // Sizes from the generic attributes extension: ![](a.png){width=50% height=3cm}
        var properties = image.GetAttributes().Properties ?? [];
        string? Attribute(string name) => properties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return new HImage(path)
        {
            Width = HwpxUnits.ParseLength(Attribute("width")),
            Height = HwpxUnits.ParseLength(Attribute("height")),
        };
    }

    /// <summary>Local image path of a Markdown image target, or null for remote / missing images.</summary>
    internal static string? ResolveImage(string? target, string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var path = target.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(target).LocalPath
                : Uri.UnescapeDataString(target);
            var candidate = Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>"title:" from the YAML front matter, if any.</summary>
    private static string? Title(MarkdownDocument document)
    {
        if (document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is not { } yaml)
            return null;
        foreach (var line in yaml.Lines.ToString().Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                return trimmed["title:".Length..].Trim().Trim('"', '\'') is { Length: > 0 } title ? title : null;
        }
        return null;
    }
}
