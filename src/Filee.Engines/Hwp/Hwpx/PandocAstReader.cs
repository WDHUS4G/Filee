// Pandoc JSON AST → HDocument. Used for Markdown, HTML, ODT and RTF, which Pandoc parses.
//
// The mapping follows pypandoc-hwpx (MIT, https://github.com/msjang/pypandoc-hwpx), extended so nothing is dropped
// silently: BlockQuote, Div, Figure, LineBlock, DefinitionList, HorizontalRule, Span, Quoted, Cite, Strikeout,
// SmallCaps and Math are converted too, and list numbering honours the Pandoc list style (1. / A. / i) / (a) ...).
// Pandoc's model has no page setup or direct formatting, so those come from the HWPX template.

using System.Text;
using System.Text.Json.Nodes;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed class PandocAstReader
{
    /// <summary>Indent per block quote level.</summary>
    private const int QuoteIndent = 2000;

    private static readonly HCharFormat Plain = default;
    private readonly Func<string, string?> _resolveImage;

    private PandocAstReader(Func<string, string?> resolveImage) => _resolveImage = resolveImage;

    /// <summary>Converts the output of <c>pandoc -t json</c>.</summary>
    /// <param name="resolveImage">Maps an image target from the AST to a local file, or null if unavailable.</param>
    public static HDocument Read(JsonNode ast, Func<string, string?> resolveImage)
    {
        var reader = new PandocAstReader(resolveImage);
        var document = new HDocument { Title = Title(ast["meta"]) };
        var section = new HSection();
        reader.Blocks(ast["blocks"] as JsonArray, section.Blocks, new Context());
        document.Sections.Add(section);
        return document;
    }

    /// <summary>List nesting and quote indentation of the blocks being read.</summary>
    private readonly record struct Context(int ListLevel = 0, int Indent = 0);

    // ───────────────────────── Blocks ─────────────────────────

    private void Blocks(JsonArray? blocks, List<HBlock> output, Context context)
    {
        foreach (var block in blocks ?? [])
        {
            if (block is JsonObject)
                Block(block, output, context);
        }
    }

    private void Block(JsonNode block, List<HBlock> output, Context context)
    {
        var content = block["c"];
        switch (Type(block))
        {
            case "Para":
            case "Plain":
                output.Add(Paragraph(content as JsonArray, context));
                break;
            case "Header":
                output.Add(Heading(content!, context));
                break;
            case "BulletList":
                List(content as JsonArray, Numbering(ordered: false, 1, "", ""), output, context);
                break;
            case "OrderedList":
                {
                    var attrs = content?[0];
                    var numbering = Numbering(ordered: true, (int?)attrs?[0] ?? 1, Type(attrs?[1]), Type(attrs?[2]));
                    List(content?[1] as JsonArray, numbering, output, context);
                    break;
                }
            case "CodeBlock":
                foreach (var line in ((string?)content?[1] ?? "").Split('\n'))
                {
                    var paragraph = NewParagraph(context);
                    paragraph.Inlines.Add(new HText(line, Plain));
                    output.Add(paragraph);
                }
                break;
            case "LineBlock":
                foreach (var line in content as JsonArray ?? [])
                    output.Add(Paragraph(line as JsonArray, context));
                break;
            case "BlockQuote":
                Blocks(content as JsonArray, output, context with { Indent = context.Indent + QuoteIndent });
                break;
            case "Div":
                Blocks(content?[1] as JsonArray, output, context);
                break;
            case "Figure":
                // [attr, caption [short, blocks], blocks]
                Blocks(content?[2] as JsonArray, output, context);
                Blocks(content?[1]?[1] as JsonArray, output, context);
                break;
            case "DefinitionList":
                foreach (var item in content as JsonArray ?? [])
                {
                    var term = NewParagraph(context);
                    Inlines(item?[0] as JsonArray, term.Inlines, new HCharFormat(Bold: true));
                    output.Add(term);
                    foreach (var definition in item?[1] as JsonArray ?? [])
                        Blocks(definition as JsonArray, output, context with { Indent = context.Indent + QuoteIndent });
                }
                break;
            case "HorizontalRule":
                output.Add(NewParagraph(context));
                break;
            case "Table":
                output.Add(Table(content!, context));
                break;
            default:
                break; // RawBlock and unknown blocks carry no document text
        }
    }

    private static HParagraph NewParagraph(Context context) =>
        new() { Format = context.Indent > 0 ? new HParaFormat(Left: context.Indent) : default };

    private HParagraph Paragraph(JsonArray? inlines, Context context)
    {
        var paragraph = NewParagraph(context);
        Inlines(inlines, paragraph.Inlines, Plain);
        return paragraph;
    }

    private HParagraph Heading(JsonNode content, Context context)
    {
        var inlines = content[2] as JsonArray ?? [];
        var paragraph = NewParagraph(context);
        paragraph.HeadingLevel = Math.Clamp((int?)content[0] ?? 1, 1, 9);

        // A line break at the very start of a heading means "column break" (pypandoc-hwpx convention).
        paragraph.ColumnBreakBefore = inlines.Count > 0 && Type(inlines[0]) == "LineBreak";
        Inlines(new JsonArray(inlines.Skip(paragraph.ColumnBreakBefore ? 1 : 0).Select(i => i?.DeepClone()).ToArray()),
            paragraph.Inlines, Plain);

        // Heading ids are link targets ("#id" links, e.g. a generated table of contents).
        if ((string?)content[1]?[0] is { Length: > 0 } id)
            paragraph.Inlines.Insert(0, new HBookmark(id));
        return paragraph;
    }

    private static HNumbering Numbering(bool ordered, int start, string style, string delimiter)
    {
        string[] bullets = ["●", "○", "■"];
        string[] defaultFormats = ["DIGIT", "LATIN_CAPITAL", "ROMAN_SMALL"];
        var numbering = new HNumbering();
        for (var level = 1; level <= 7; level++)
        {
            if (!ordered)
            {
                numbering.Levels.Add(new HNumberingLevel("DIGIT", bullets[(level - 1) % 3], 1, Bullet: true));
                continue;
            }
            var format = style switch
            {
                "Decimal" => "DIGIT",
                "LowerAlpha" => "LATIN_SMALL",
                "UpperAlpha" => "LATIN_CAPITAL",
                "LowerRoman" => "ROMAN_SMALL",
                "UpperRoman" => "ROMAN_CAPITAL",
                _ => defaultFormats[(level - 1) % 3],
            };
            var text = delimiter switch
            {
                "OneParen" => $"^{level})",
                "TwoParens" => $"(^{level})",
                _ => $"^{level}.",
            };
            numbering.Levels.Add(new HNumberingLevel(format, text, level == 1 ? Math.Max(1, start) : 1, Bullet: false));
        }
        return numbering;
    }

    private void List(JsonArray? items, HNumbering numbering, List<HBlock> output, Context context)
    {
        foreach (var item in items ?? [])
        {
            // Only the first paragraph of an item carries the bullet or number.
            var first = true;
            foreach (var block in item as JsonArray ?? [])
            {
                if (block is null)
                    continue;
                switch (Type(block))
                {
                    case "Para":
                    case "Plain":
                        {
                            var paragraph = Paragraph(block["c"] as JsonArray, context);
                            paragraph.List = new HListRef(numbering, context.ListLevel, Numbered: first);
                            output.Add(paragraph);
                            first = false;
                            break;
                        }
                    case "BulletList":
                    case "OrderedList":
                        Block(block, output, context with { ListLevel = context.ListLevel + 1 });
                        break;
                    default:
                        Block(block, output, context);
                        first = false;
                        break;
                }
            }
        }
    }

    private HTable Table(JsonNode content, Context context)
    {
        // Pandoc ≥ 2.10: [attr, caption, colSpecs, head, bodies, foot]; row = [attr, cells];
        // cell = [attr, align, rowspan, colspan, blocks]
        var specs = content[2] as JsonArray ?? [];
        var table = new HTable
        {
            ColumnCount = specs.Count,
            RelativeWidths = specs.Select(s => Type(s?[1]) == "ColWidth" ? (double?)s![1]!["c"] ?? 0 : 0).ToArray(),
            Indent = context.Indent,
        };

        void AddRows(JsonArray? rows, bool header)
        {
            foreach (var row in rows ?? [])
            {
                var hRow = new HRow { Header = header };
                foreach (var cell in row?[1] as JsonArray ?? [])
                {
                    if (cell is null)
                        continue;
                    var hCell = new HCell
                    {
                        RowSpan = Math.Max(1, (int?)cell[2] ?? 1),
                        ColSpan = Math.Max(1, (int?)cell[3] ?? 1),
                    };
                    Blocks(cell[4] as JsonArray, hCell.Blocks, new Context());
                    hRow.Cells.Add(hCell);
                }
                table.Rows.Add(hRow);
            }
        }

        AddRows(content[3]?[1] as JsonArray, header: true);
        foreach (var body in content[4] as JsonArray ?? [])
        {
            AddRows(body?[2] as JsonArray, header: false);
            AddRows(body?[3] as JsonArray, header: false);
        }
        AddRows(content[5]?[1] as JsonArray, header: false);

        Blocks(content[1]?[1] as JsonArray, table.Caption, context);
        return table;
    }

    // ───────────────────────── Inlines ─────────────────────────

    private void Inlines(JsonArray? inlines, List<HInline> output, HCharFormat format)
    {
        foreach (var inline in inlines ?? [])
        {
            if (inline is null)
                continue;
            var c = inline["c"];
            switch (Type(inline))
            {
                case "Str":
                    output.Add(new HText((string?)c ?? "", format));
                    break;
                case "Space":
                case "SoftBreak":
                    output.Add(new HText(" ", format));
                    break;
                case "LineBreak":
                    output.Add(new HLineBreak(format));
                    break;
                case "Strong":
                    Inlines(c as JsonArray, output, format with { Bold = true });
                    break;
                case "Emph":
                    Inlines(c as JsonArray, output, format with { Italic = true });
                    break;
                case "Underline":
                    Inlines(c as JsonArray, output, format with { Underline = true });
                    break;
                case "Strikeout":
                    Inlines(c as JsonArray, output, format with { Strike = true });
                    break;
                case "Superscript":
                    Inlines(c as JsonArray, output, format with { Superscript = true, Subscript = null });
                    break;
                case "Subscript":
                    Inlines(c as JsonArray, output, format with { Subscript = true, Superscript = null });
                    break;
                case "SmallCaps":
                    Inlines(c as JsonArray, output, format);
                    break;
                case "Span":
                    if ((string?)c?[0]?[0] is { Length: > 0 } spanId)
                        output.Add(new HBookmark(spanId));
                    Inlines(c?[1] as JsonArray, output, format);
                    break;
                case "Cite":
                    Inlines(c?[1] as JsonArray, output, format);
                    break;
                case "Quoted":
                    {
                        var (open, close) = Type(c?[0]) == "SingleQuote" ? ("‘", "’") : ("“", "”");
                        output.Add(new HText(open, format));
                        Inlines(c?[1] as JsonArray, output, format);
                        output.Add(new HText(close, format));
                        break;
                    }
                case "Code":
                    output.Add(new HText((string?)c?[1] ?? "", format));
                    break;
                case "Math":
                    output.Add(new HText((string?)c?[1] ?? "", format with { Italic = true }));
                    break;
                case "Link":
                    {
                        var link = new HLink((string?)c?[2]?[0] ?? "");
                        Inlines(c?[1] as JsonArray, link.Content, format);
                        output.Add(link);
                        break;
                    }
                case "Image":
                    if (Image(c!) is { } image)
                        output.Add(image);
                    break;
                case "Note":
                    {
                        var note = new HNote(endnote: false);
                        Blocks(c as JsonArray, note.Blocks, new Context());
                        output.Add(note);
                        break;
                    }
                default:
                    break; // RawInline: format-specific markup, nothing to show.
            }
        }
    }

    private HImage? Image(JsonNode content)
    {
        // [attr [id, classes, [[key, value]]], caption inlines, [target, title]]
        var path = _resolveImage((string?)content[2]?[0] ?? "");
        if (path is null || !File.Exists(path))
            return null; // missing image: skip rather than produce a broken reference

        var attributes = (content[0]?[2] as JsonArray ?? [])
            .Where(kv => kv is JsonArray { Count: 2 })
            .GroupBy(kv => (string?)kv![0] ?? "")
            .ToDictionary(g => g.Key, g => (string?)g.First()![1]);
        return new HImage(path)
        {
            Width = HwpxUnits.ParseLength(attributes.GetValueOrDefault("width")),
            Height = HwpxUnits.ParseLength(attributes.GetValueOrDefault("height")),
        };
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string? Title(JsonNode? meta)
    {
        var title = meta?["title"];
        return Type(title) switch
        {
            "MetaInlines" => PlainText(title!["c"] as JsonArray),
            "MetaString" => (string?)title!["c"],
            _ => null,
        };
    }

    private static string PlainText(JsonArray? inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines ?? [])
        {
            var c = inline?["c"];
            switch (Type(inline))
            {
                case "Str": sb.Append((string?)c); break;
                case "Space" or "SoftBreak" or "LineBreak": sb.Append(' '); break;
                case "Code" or "Math": sb.Append((string?)c?[1]); break;
                case "Link" or "Image" or "Span" or "Cite": sb.Append(PlainText(c?[1] as JsonArray)); break;
                case "Quoted": sb.Append('"').Append(PlainText(c?[1] as JsonArray)).Append('"'); break;
                case "Strong" or "Emph" or "Underline" or "Strikeout" or "Superscript" or "Subscript" or "SmallCaps":
                    sb.Append(PlainText(c as JsonArray)); break;
            }
        }
        return sb.ToString();
    }

    private static string Type(JsonNode? node) => node is JsonObject obj ? (string?)obj["t"] ?? "" : "";
}
