// HDocument → plain text: paragraphs separated by an empty line, list markers and numbers written out, table rows
// as tab-separated cells and footnotes collected at the end. Used for e-books → TXT.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class PlainTextWriter
{
    /// <summary>The text of a document.</summary>
    public static string Write(HDocument document)
    {
        var sb = new StringBuilder();
        var notes = new List<string>();
        var numbers = new ListNumbers();
        foreach (var section in document.Sections)
            Blocks(section.Blocks, sb, notes, numbers);
        if (notes.Count > 0)
        {
            sb.AppendLine().AppendLine("----");
            for (var i = 0; i < notes.Count; i++)
                sb.AppendLine($"[{i + 1}] {notes[i]}");
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void Blocks(List<HBlock> blocks, StringBuilder sb, List<string> notes, ListNumbers numbers)
    {
        HBlock? previous = null;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    {
                        var text = Inlines(paragraph.Inlines, notes);
                        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && text.Length == 0)
                            text = "* * *";
                        if (text.Trim().Length == 0 && paragraph.List is null)
                            break; // spacing paragraphs: the empty line between paragraphs already separates them
                        // List items and code lines stay together; everything else is separated by an empty line.
                        var together = previous is HParagraph before
                                       && ((before.List is not null && paragraph.List is not null) || (IsCode(before) && IsCode(paragraph)));
                        if (sb.Length > 0)
                            sb.AppendLine();
                        if (sb.Length > 0 && !together)
                            sb.AppendLine();
                        var indent = Math.Max(0, paragraph.Format.Left ?? 0) / 1000;
                        if (paragraph.List is { } list)
                        {
                            var marker = list.Numbered ? numbers.Next(list) + " " : "";
                            sb.Append(new string(' ', list.Level * 4 + indent)).Append(marker).Append(text.Replace("\n", "\n" + new string(' ', list.Level * 4 + indent + marker.Length)));
                        }
                        else
                        {
                            sb.Append(new string(' ', indent)).Append(indent > 0 ? text.Replace("\n", "\n" + new string(' ', indent)) : text);
                        }
                        previous = paragraph;
                        break;
                    }
                case HTable { Rows.Count: > 0 } table:
                    if (sb.Length > 0)
                        sb.AppendLine().AppendLine();
                    sb.AppendJoin(Environment.NewLine, table.Rows.Select(row => string.Join('\t', row.Cells.Select(c => Cell(c, notes)))));
                    previous = table;
                    break;
            }
        }
    }

    private static bool IsCode(HParagraph paragraph) =>
        paragraph.Inlines.Count > 0 && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    private static string Cell(HCell cell, List<string> notes) =>
        string.Join(" ", cell.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines, notes).Replace('\n', ' ').Replace('\t', ' ')).Where(t => t.Length > 0));

    private static string Inlines(List<HInline> inlines, List<string> notes)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    sb.Append(text.Text);
                    break;
                case HLineBreak:
                    sb.Append('\n');
                    break;
                case HTab:
                    sb.Append('\t');
                    break;
                case HLink link:
                    sb.Append(Inlines(link.Content, notes));
                    break;
                case HNote note:
                    {
                        var content = new StringBuilder();
                        Blocks(note.Blocks, content, notes, new ListNumbers());
                        notes.Add(content.ToString().Trim().Replace(Environment.NewLine + Environment.NewLine, " "));
                        sb.Append('[').Append(notes.Count).Append(']');
                        break;
                    }
                case HTextBox box:
                    sb.AppendJoin(' ', box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines, notes)));
                    break;
            }
        }
        return sb.ToString().Replace("\n", Environment.NewLine).TrimEnd();
    }
}

/// <summary>Counts list items and formats their numbers like 한글 does ("1.", "가.", "iv)", "●").</summary>
internal sealed class ListNumbers
{
    private readonly Dictionary<HNumbering, int[]> _counters = new(ReferenceEqualityComparer.Instance);

    /// <summary>The marker of the next numbered item of a list.</summary>
    public string Next(HListRef item)
    {
        if (item.Numbering.Levels.Count == 0)
            return "•";
        var level = Math.Min(item.Level, item.Numbering.Levels.Count - 1);
        var definition = item.Numbering.Levels[level];
        if (definition.Bullet)
            return definition.Text.Length > 0 ? definition.Text : "•";

        if (!_counters.TryGetValue(item.Numbering, out var counters))
            _counters[item.Numbering] = counters = new int[Math.Max(10, item.Numbering.Levels.Count)];
        counters[level] = counters[level] == 0 ? definition.Start : counters[level] + 1;
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
            counters[deeper] = 0;

        var text = definition.Text;
        for (var l = Math.Min(9, item.Numbering.Levels.Count); l >= 1; l--)
        {
            var value = counters[l - 1] == 0 ? item.Numbering.Levels[l - 1].Start : counters[l - 1];
            text = text.Replace($"^{l}", Format(value, item.Numbering.Levels[l - 1].Format), StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>A number in an OWPML number format.</summary>
    public static string Format(int value, string format) => format switch
    {
        "LATIN_SMALL" => Letters(value, 'a'),
        "LATIN_CAPITAL" => Letters(value, 'A'),
        "ROMAN_SMALL" => Roman(value).ToLowerInvariant(),
        "ROMAN_CAPITAL" => Roman(value),
        "HANGUL_SYLLABLE" => value is >= 1 and <= 14 ? "가나다라마바사아자차카타파하"[value - 1].ToString() : value.ToString(CultureInfo.InvariantCulture),
        "CIRCLED_DIGIT" => value is >= 1 and <= 20 ? ((char)('①' + value - 1)).ToString() : value.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string Letters(int value, char first)
    {
        if (value < 1)
            return value.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (; value > 0; value = (value - 1) / 26)
            sb.Insert(0, (char)(first + (value - 1) % 26));
        return sb.ToString();
    }

    private static string Roman(int value)
    {
        if (value is < 1 or > 3999)
            return value.ToString(CultureInfo.InvariantCulture);
        (int Value, string Text)[] numerals = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new StringBuilder();
        foreach (var (number, text) in numerals)
            for (; value >= number; value -= number)
                sb.Append(text);
        return sb.ToString();
    }
}
