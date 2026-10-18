// Rough text measurement for the values 한글 files carry as layout caches: table row heights and the width
// of inline tabs. 한글 lays the document out again when it opens it, but simpler readers (and previews)
// use these values as they are, so they should be close to the real layout.

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxWriter
{
    /// <summary>Estimated width of <paramref name="text"/> at a font size (1/100 pt = HWPUNIT per em).</summary>
    internal static double TextWidth(string text, int size)
    {
        double width = 0;
        foreach (var ch in text)
        {
            // Average advances (em) of the fonts Korean documents use (\uB9D1\uC740 \uACE0\uB515, \uAD74\uB9BC, \uD568\uCD08\uB86C, Calibri ...).
            width += ch switch
            {
                ' ' or '\u00A0' => 0.3,
                '.' or ',' or ':' or ';' or '\'' or '!' or '|' or '(' or ')' or '[' or ']' or '-' => 0.3,
                >= '\u2000' and <= '\u206F' => 0.5, // general punctuation (dashes, quotes, bullets)
                >= '\u1100' => 0.92,                // Hangul, CJK, full-width forms: about one em
                >= '0' and <= '9' => 0.55,
                >= 'A' and <= 'Z' => 0.65,
                _ => 0.5,
            } * size;
        }
        return width;
    }

    /// <summary>Base font size (1/100 pt) of the current container's plain paragraphs.</summary>
    private int BaseSize(string charPrId) =>
        (int?)_head.Descendants(Hh + "charPr").FirstOrDefault(c => (string?)c.Attribute("id") == charPrId)?.Attribute("height") ?? 1000;

    /// <summary>Base line spacing (percent) of a paragraph shape.</summary>
    private int BasePercent(string paraPrId)
    {
        var paraPr = _head.Descendants(Hh + "paraPr").FirstOrDefault(p => (string?)p.Attribute("id") == paraPrId);
        var line = paraPr?.Descendants(Hh + "lineSpacing").FirstOrDefault();
        return (string?)line?.Attribute("type") == "PERCENT" ? (int?)line.Attribute("value") ?? 160 : 160;
    }

    /// <summary>Estimated height of blocks laid out in <paramref name="width"/>.</summary>
    private int EstimateHeight(IEnumerable<HBlock> blocks, int width)
    {
        var total = 0;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    total += EstimateHeight(paragraph, width);
                    break;
                case HTable table:
                    {
                        var (placed, columns) = PlaceCells(table);
                        total += EstimateRowHeights(table, placed, ColumnWidths(table, columns, width)).Sum();
                        break;
                    }
            }
        }
        return total;
    }

    private int EstimateHeight(HParagraph paragraph, int width)
    {
        var (_, paraPrBase, charPrBase) = paragraph.HeadingLevel > 0 && _headings.TryGetValue(paragraph.HeadingLevel, out var heading) ? heading : _base;
        var size = LargestSize(paragraph.Inlines) ?? paragraph.MarkFormat.Size ?? BaseSize(charPrBase);
        var line = paragraph.Format.LineSpacing switch
        {
            { Kind: HLineSpacingKind.Fixed } fixedLine => fixedLine.Value,
            { Kind: HLineSpacingKind.AtLeast } atLeast => Math.Max(atLeast.Value, size * 13 / 10),
            { Kind: HLineSpacingKind.Percent } percent => size * percent.Value / 100,
            _ => size * BasePercent(paraPrBase) / 100,
        };
        var available = Math.Max(1000, width - (paragraph.Format.Left ?? 0) - (paragraph.Format.Right ?? 0));

        // Line breaks split the text; each piece wraps on its own. Objects add their own height.
        var lines = 0;
        double current = Math.Max(0, paragraph.Format.FirstLine ?? 0);
        var objects = 0;
        void Measure(IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case HText text:
                        current += TextWidth(text.Text, text.Format.Size ?? size);
                        break;
                    case HTab:
                        current += 4000;
                        break;
                    case HLineBreak:
                        lines += LinesFor(current, available);
                        current = 0;
                        break;
                    case HLink link:
                        Measure(link.Content);
                        break;
                    case HImage { Anchor: null } image:
                        objects += image.Height ?? 0;
                        break;
                    case HTextBox { Anchor: null } box:
                        objects += box.Height;
                        break;
                }
            }
        }
        Measure(paragraph.Inlines);
        lines += LinesFor(current, available);
        return lines * line + objects + (paragraph.Format.Before ?? 0) + (paragraph.Format.After ?? 0);
    }

    /// <summary>Wrapped lines for a text width; 5% slack so text that just fits is not pushed to a second line.</summary>
    private static int LinesFor(double textWidth, int available) =>
        Math.Max(1, (int)Math.Ceiling(textWidth / (available * 1.05)));

    private static int? LargestSize(IEnumerable<HInline> inlines)
    {
        int? max = null;
        foreach (var inline in inlines)
        {
            var size = inline switch
            {
                HText text => text.Format.Size,
                HLink link => LargestSize(link.Content),
                _ => null,
            };
            if (size > (max ?? 0))
                max = size;
        }
        return max;
    }

    /// <summary>
    /// Row heights: at least the row's own height, and tall enough for the estimated content of its cells.
    /// Cells spanning rows spread their extra height over the last row they span.
    /// </summary>
    private int[] EstimateRowHeights(HTable table, List<List<(HCell Cell, int Column)>> placed, int[] widths)
    {
        var margin = table.CellMargin ?? new HInsets(510, 510, 141, 141);
        var heights = table.Rows.Select(r => Math.Max(r.Height ?? 0, 0)).ToArray();
        var outerBase = _base;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var r = 0; r < placed.Count; r++)
            {
                foreach (var (cell, column) in placed[r])
                {
                    if ((pass == 0) != (cell.RowSpan == 1))
                        continue; // single-row cells first, then spans on top of them
                    var width = 0;
                    for (var i = column; i < column + cell.ColSpan && i < widths.Length; i++)
                        width += widths[i];
                    var needed = EstimateHeight(cell.Blocks.Count > 0 ? cell.Blocks : [new HParagraph()], Math.Max(1000, width - margin.Left - margin.Right))
                                 + margin.Top + margin.Bottom;
                    var last = Math.Min(r + cell.RowSpan, heights.Length) - 1;
                    var have = 0;
                    for (var i = r; i <= last; i++)
                        have += heights[i];
                    if (needed > have)
                        heights[last] += needed - have;
                }
            }
        }
        _base = outerBase;
        return heights.Select(h => Math.Max(h, 282)).ToArray();
    }

    // ───────────────────────── Tabs ─────────────────────────

    /// <summary>
    /// Type, leader and width of each inline tab of a paragraph, in order. Like 한글 and Word, a tab moves to the
    /// first tab stop right of the text before it; without a stop it advances to the next default stop.
    /// </summary>
    private static List<string> TabElements(HParagraph paragraph, int size)
    {
        var result = new List<string>();
        var stops = paragraph.Format.Tabs ?? [];
        var left = paragraph.Format.Left ?? 0;
        double x = left + (paragraph.Format.FirstLine ?? 0);
        var pieces = new List<(HInline Inline, double Width)>();
        void Flatten(IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is HLink link)
                    Flatten(link.Content);
                else
                    pieces.Add((inline, inline is HText text ? TextWidth(text.Text, text.Format.Size ?? size) : 0));
            }
        }
        Flatten(paragraph.Inlines);

        for (var i = 0; i < pieces.Count; i++)
        {
            switch (pieces[i].Inline)
            {
                case HText:
                    x += pieces[i].Width;
                    break;
                case HLineBreak:
                    x = left;
                    break;
                case HTab:
                    {
                        // Text up to the next tab or line break, for right / center aligned stops.
                        double following = 0;
                        for (var j = i + 1; j < pieces.Count && pieces[j].Inline is not (HTab or HLineBreak); j++)
                            following += pieces[j].Width;

                        var stop = stops.FirstOrDefault(s => s.Position > x + 1);
                        int type, leader;
                        double target;
                        if (stops.Count > 0 && stop.Position > x + 1)
                        {
                            (type, target) = stop.Kind switch
                            {
                                HTabKind.Right => (2, stop.Position - following),
                                HTabKind.Center => (3, stop.Position - following / 2),
                                HTabKind.Decimal => (4, stop.Position - following),
                                _ => (1, (double)stop.Position),
                            };
                            leader = stop.Leader switch
                            {
                                "SOLID" => 1,
                                "DOT" => 2,
                                "DASH" => 3,
                                _ => 0,
                            };
                        }
                        else
                        {
                            const int DefaultStop = 4000;
                            (type, leader, target) = (1, 0, (Math.Floor(x / DefaultStop) + 1) * DefaultStop);
                        }
                        var width = Math.Max(0, (int)(target - x));
                        result.Add($"<hp:tab width=\"{width}\" leader=\"{leader}\" type=\"{type}\"/>");
                        x += width;
                        break;
                    }
            }
        }
        return result;
    }
}
