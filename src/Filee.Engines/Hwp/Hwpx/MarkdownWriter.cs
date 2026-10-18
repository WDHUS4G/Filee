// HDocument → Markdown (the dialect MarkdownReader reads back: GitHub tables, ~sub~, ^sup^, footnotes): headings,
// emphasis, code, lists, block quotes, pipe tables, links, pictures and footnotes. Used for e-books → MD and TXTZ.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed class MarkdownWriter
{
    private readonly Func<string, string?> _imageTarget;
    private readonly List<string> _notes = [];
    private readonly ListNumbers _numbers = new();

    private MarkdownWriter(Func<string, string?> imageTarget) => _imageTarget = imageTarget;

    /// <summary>The Markdown text of a document.</summary>
    /// <param name="imageTarget">Maps a picture file to the path written into the Markdown, or null to leave it out.</param>
    public static string Write(HDocument document, Func<string, string?> imageTarget)
    {
        var writer = new MarkdownWriter(imageTarget);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(document.Title))
            sb.Append("---\ntitle: \"").Append(document.Title.Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\"\n---\n\n");
        foreach (var section in document.Sections)
            writer.Blocks(section.Blocks, sb, "");
        for (var i = 0; i < writer._notes.Count; i++)
            sb.Append($"\n[^{i + 1}]: ").Append(writer._notes[i]).Append('\n');
        return sb.ToString().TrimEnd() + "\n";
    }

    private void Blocks(List<HBlock> blocks, StringBuilder sb, string prefix)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case HParagraph paragraph when IsCode(paragraph):
                    {
                        var lines = new List<string>();
                        for (; i < blocks.Count && blocks[i] is HParagraph code && IsCode(code); i++)
                            lines.Add(string.Concat(code.Inlines.OfType<HText>().Select(t => t.Text)));
                        i--;
                        var fence = lines.Any(l => l.Contains("```", StringComparison.Ordinal)) ? "~~~~" : "```";
                        sb.Append(prefix).Append(fence).Append('\n');
                        foreach (var line in lines)
                            sb.Append(prefix).Append(line).Append('\n');
                        sb.Append(prefix).Append(fence).Append("\n\n");
                        break;
                    }
                case HParagraph paragraph:
                    Paragraph(paragraph, sb, prefix, i + 1 < blocks.Count ? blocks[i + 1] : null);
                    break;
                case HTable table:
                    Table(table, sb, prefix);
                    break;
            }
        }
    }

    private static bool IsCode(HParagraph paragraph) =>
        paragraph is { List: null, HeadingLevel: 0 } && paragraph.Inlines.Count > 0
        && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    private void Paragraph(HParagraph paragraph, StringBuilder sb, string prefix, HBlock? next)
    {
        var text = Inlines(paragraph.Inlines);
        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && text.Trim().Length == 0)
        {
            sb.Append(prefix).Append("---\n\n");
            return;
        }
        if (text.Trim().Length == 0)
            return;

        // Block quotes: the readers indent quotes by 2000 HWPUNIT per level.
        var quote = paragraph.List is null ? string.Concat(Enumerable.Repeat("> ", Math.Max(0, paragraph.Format.Left ?? 0) / 2000)) : "";
        var linePrefix = prefix + quote;
        if (paragraph.HeadingLevel > 0)
        {
            sb.Append(linePrefix).Append('#', Math.Clamp(paragraph.HeadingLevel, 1, 6)).Append(' ').Append(text.Replace("\\\n", " ", StringComparison.Ordinal)).Append("\n\n");
            return;
        }
        if (paragraph.List is { } list)
        {
            var indent = new string(' ', list.Level * 4);
            var marker = !list.Numbered ? "" : IsBullet(list) ? "- " : OrderedMarker(list);
            var continuation = linePrefix + indent + new string(' ', Math.Max(marker.Length, list.Numbered ? 0 : 4));
            sb.Append(list.Numbered ? linePrefix + indent + marker : continuation)
              .Append(text.Replace("\n", "\n" + continuation, StringComparison.Ordinal)).Append('\n');
            // Items of one list stay together; a blank line ends the list.
            if (next is not HParagraph { List: not null })
                sb.Append('\n');
            return;
        }
        sb.Append(linePrefix).Append(EscapeLineStart(text).Replace("\n", "\n" + linePrefix, StringComparison.Ordinal)).Append("\n\n");
    }

    private static bool IsBullet(HListRef list) =>
        list.Numbering.Levels.Count == 0 || list.Numbering.Levels[Math.Min(list.Level, list.Numbering.Levels.Count - 1)].Bullet;

    /// <summary>"3. " — Markdown only knows numbers, so letters and roman numerals become their position.</summary>
    private string OrderedMarker(HListRef list)
    {
        var level = list.Numbering.Levels[Math.Min(list.Level, list.Numbering.Levels.Count - 1)];
        var number = _numbers.Next(list);
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        return (level.Format == "DIGIT" && digits.Length > 0 ? digits : "1") + ". ";
    }

    private void Table(HTable table, StringBuilder sb, string prefix)
    {
        if (table.Rows.Count == 0)
            return;
        var columns = Math.Max(table.ColumnCount, table.Rows.Max(r => r.Cells.Sum(c => c.ColSpan)));
        var grid = new List<string[]>();
        var covered = new Dictionary<(int Row, int Column), bool>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = new string[columns];
            var column = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (column < columns && covered.ContainsKey((r, column)))
                    column++;
                if (column >= columns)
                    break;
                // Header cells are bold by nature: no ** inside them.
                var header = table.Rows[r].Header;
                cells[column] = string.Join(" ", cell.Blocks.OfType<HParagraph>()
                        .Select(p => header ? p.Inlines.Select(i => i is HText t ? new HText(t.Text, t.Format with { Bold = null }) : i).ToList() : p.Inlines)
                        .Select(inlines => Inlines(inlines).Replace("\\\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)))
                    .Replace("|", "\\|", StringComparison.Ordinal);
                for (var rr = r; rr < r + cell.RowSpan; rr++)
                    for (var cc = column; cc < Math.Min(columns, column + cell.ColSpan); cc++)
                        covered[(rr, cc)] = true;
                column += cell.ColSpan;
            }
            grid.Add(cells);
        }

        sb.Append(prefix).Append("| ").AppendJoin(" | ", grid[0].Select(c => c ?? "")).Append(" |\n");
        sb.Append(prefix).Append('|').Append(string.Concat(Enumerable.Repeat("---|", columns))).Append('\n');
        foreach (var row in grid.Skip(1))
            sb.Append(prefix).Append("| ").AppendJoin(" | ", row.Select(c => c ?? "")).Append(" |\n");
        sb.Append('\n');
    }

    private string Inlines(List<HInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    sb.Append(Run(text.Text, text.Format));
                    break;
                case HLineBreak:
                    sb.Append("\\\n");
                    break;
                case HTab:
                    sb.Append("    ");
                    break;
                case HLink link:
                    {
                        var content = Inlines(link.Content);
                        // Bookmarks have no Markdown form: links inside the document keep their text only.
                        sb.Append(link.Target.StartsWith('#') || content.Trim().Length == 0 ? content : $"[{content}](<{link.Target}>)");
                        break;
                    }
                case HImage image:
                    if (_imageTarget(image.Path) is { } target)
                        sb.Append($"![](<{target}>)");
                    break;
                case HNote note:
                    {
                        var content = new StringBuilder();
                        Blocks(note.Blocks, content, "");
                        _notes.Add(content.ToString().Trim().Replace("\n\n", "\n    ", StringComparison.Ordinal));
                        sb.Append($"[^{_notes.Count}]");
                        break;
                    }
                case HTextBox box:
                    sb.AppendJoin(' ', box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines)));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Run(string text, HCharFormat format)
    {
        if (text.Trim().Length == 0)
            return text;
        if (format.Shade == HtmlReader.CodeShade)
            return text.Contains('`') ? $"`` {text} ``" : $"`{text}`";

        // Emphasis markers must touch the text: spaces stay outside.
        var core = Escape(text.Trim());
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        if (format.Superscript is true)
            core = $"^{core.Replace(" ", "\\ ", StringComparison.Ordinal)}^";
        else if (format.Subscript is true)
            core = $"~{core.Replace(" ", "\\ ", StringComparison.Ordinal)}~";
        if (format.Strike is true)
            core = $"~~{core}~~";
        if (format.Italic is true)
            core = $"*{core}*";
        if (format.Bold is true)
            core = $"**{core}**";
        if (format.Shade == HtmlReader.MarkShade)
            core = $"=={core}==";
        return lead + core + trail;
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '^' or '|' or '=')
                sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Text that would read as a heading, list item or quote at the start of a line is escaped.</summary>
    private static string EscapeLineStart(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
            return "\\" + trimmed;
        var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
        if (digits > 0 && digits < trimmed.Length && trimmed[digits] is '.' or ')')
            return trimmed[..digits] + "\\" + trimmed[digits..];
        return text;
    }
}
