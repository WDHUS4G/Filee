// Page layout analysis for PdfDocumentReader: words → lines (split at column gutters) → paragraphs → reading order.
//
// Reading order handles the usual layouts: one column; two or three columns, with titles, figures or footers that span
// the columns in between. A gutter is a vertical strip that no narrow paragraph crosses while text sits next to it on
// both sides; paragraphs wider than a column (or crossing a gutter) separate the page into bands read one after the
// other, and inside a band the columns are read left to right.

using System.Text.RegularExpressions;

namespace Filee.Engines.Pdf;

internal sealed partial class PdfDocumentReader
{
    /// <summary>Line starts that begin a new paragraph: bullets and "1." / "a)" / "(3)" numbers.</summary>
    private static readonly Regex ListStart = new(
        "^([•◦▪▫●○■□‣⁃∙·\\-–—*]|\\(?\\d{1,3}[.)]|\\(?[a-zA-Z][.)]|[ivxIVX]{1,4}[.)]|[①-⑳]|[가-하][.)])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>
    /// Groups words on a common baseline into lines, top to bottom, and splits a line where the gap between two words
    /// is much wider than a space (a column gutter or a tab stop far away).
    /// </summary>
    private static List<TextLine> Lines(List<TextWord> words)
    {
        var rows = new List<List<TextWord>>();
        foreach (var word in words.OrderByDescending(w => w.Baseline).ThenBy(w => w.Left))
        {
            // Words belong to a recent row whose baseline is close; smaller superscripts sit a little higher.
            List<TextWord>? row = null;
            for (var r = rows.Count - 1; r >= Math.Max(0, rows.Count - 3) && row is null; r--)
            {
                var first = rows[r][0];
                var tolerance = 0.5 * Math.Min(first.Size, word.Size);
                if (Math.Abs(first.Baseline - word.Baseline) <= tolerance || (word.Bottom < first.Top && word.Top > first.Baseline && word.Size < first.Size * 0.8))
                    row = rows[r];
            }
            if (row is null)
                rows.Add(row = []);
            row.Add(word);
        }

        // A space is about a quarter of the font size; the median gap between words of a line tells the page's spacing.
        var gaps = new List<double>();
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.Left.CompareTo(b.Left));
            for (var i = 1; i < row.Count; i++)
                gaps.Add(row[i].Left - row[i - 1].Right);
        }
        var space = gaps.Count > 0 ? Math.Max(0.5, Median(gaps.Where(g => g > 0))) : 3;

        var lines = new List<TextLine>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var current = new List<TextWord> { row[0] };
            for (var i = 1; i < row.Count; i++)
            {
                var gap = row[i].Left - row[i - 1].Right;
                var size = Math.Min(row[i].Size, row[i - 1].Size);
                if (gap > Math.Max(5 * space, 1.5 * size))
                {
                    lines.Add(new TextLine(current, r));
                    current = [];
                }
                current.Add(row[i]);
            }
            lines.Add(new TextLine(current, r));
        }
        return lines;
    }

    /// <summary>
    /// Joins the parts of a split row again, with a tab between them, where no column gutter lies between the parts
    /// and the gap is a tab: after a list number ("1."), wider than a column gutter with a ragged line end (6 em), or
    /// at the same place as a tab in a row right above or below (the value column of a form, whatever the label's
    /// length). "1.  RFP Published   08/14/2026" stays one row instead of three paragraphs. Null when no row changes.
    /// </summary>
    private static List<TextLine>? JoinTabbedRows(List<TextLine> lines, List<Item> items)
    {
        var rows = lines.GroupBy(l => l.Row).Select(g => g.ToList()).ToList();
        if (items.Count == 0 || rows.All(r => r.Count == 1))
            return null;
        var gutters = Gutters(items, items.Min(i => i.Left), items.Max(i => i.Right));

        // Gaps that may be tabs: (row, index of the part after the gap, where that part starts).
        var candidates = new List<(int Row, int Part, double X)>();
        var tabs = new HashSet<(int Row, int Part)>();
        foreach (var row in rows)
        {
            for (var p = 1; p < row.Count; p++)
            {
                var (before, after) = (row[p - 1], row[p]);
                if (gutters.Any(g => g.Start >= before.Right - 2 && g.End <= after.Left + 2))
                    continue;
                candidates.Add((before.Row, p, after.Left));
                var number = before.Words.Count == 1 && ListStart.IsMatch(before.Words[0].Text);
                if (number || after.Left - before.Right > 6 * Math.Max(before.Size, after.Size))
                    tabs.Add((before.Row, p));
            }
        }
        if (tabs.Count == 0)
            return null;
        for (var grown = true; grown;)
        {
            grown = false;
            foreach (var (row, part, x) in candidates)
            {
                if (!tabs.Contains((row, part)) && candidates.Any(c => tabs.Contains((c.Row, c.Part)) && Math.Abs(c.Row - row) <= 2 && Math.Abs(c.X - x) <= 1.5))
                    grown |= tabs.Add((row, part));
            }
        }

        var result = new List<TextLine>();
        foreach (var row in rows)
        {
            var current = row[0];
            for (var p = 1; p < row.Count; p++)
            {
                if (tabs.Contains((current.Row, p)))
                {
                    var joined = new TextLine([.. current.Words, .. row[p].Words], current.Row);
                    joined.Tabs.AddRange(current.Tabs);
                    joined.Tabs.Add(current.Words.Count);
                    current = joined;
                }
                else
                {
                    result.Add(current);
                    current = row[p];
                }
            }
            result.Add(current);
        }
        return result;
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>
    /// Stacks lines into paragraphs. A line continues the paragraph right above it when it is close below (at most
    /// about one line pitch), overlaps it horizontally and has a similar size, unless it starts a new paragraph: a
    /// bullet or number, a first-line indent, or a previous line that ended early (its next word would have fit).
    /// </summary>
    private static List<Item> Paragraphs(List<TextLine> lines)
    {
        var items = new List<Item>();
        foreach (var line in lines)
        {
            Item? best = null;
            var bestDistance = double.MaxValue;
            foreach (var item in items)
            {
                if (line.Tabs.Count > 0)
                    break; // a tabbed row starts its own paragraph
                var last = item.Lines[^1];
                // A tabbed row continues only with text under its last part (the wrapped text of "1.  Text …").
                var head = item.Lines[0];
                if (head.Tabs.Count > 0 && Math.Abs(line.Left - head.Words[head.Tabs[^1]].Left) > 0.5 * line.Size)
                    continue;
                var distance = last.Baseline - line.Baseline;
                var size = Math.Max(last.Size, line.Size);
                if (distance <= 0 || distance > 1.9 * size || Math.Abs(last.Size - line.Size) > 0.2 * size)
                    continue;
                if (item.Lines.Count >= 2 && distance > 1.3 * Pitch(item))
                    continue; // more space than between the lines so far: a new paragraph
                var overlap = Math.Min(item.Right, line.Right) - Math.Max(item.Left, line.Left);
                if (overlap < 0.3 * Math.Min(item.Width, line.Right - line.Left))
                    continue;
                if (distance < bestDistance)
                    (best, bestDistance) = (item, distance);
            }

            if (best is not null && !StartsParagraph(best, line))
            {
                best.Lines.Add(line);
            }
            else
            {
                var item = new Item();
                item.Lines.Add(line);
                items.Add(item);
            }
        }
        return items;
    }

    private static bool StartsParagraph(Item item, TextLine line)
    {
        var last = item.Lines[^1];
        var em = line.Size;
        if (ListStart.IsMatch(line.Words[0].Text))
            return true;

        // First-line indent: this line starts further right than the paragraph's other lines.
        var bodyLeft = item.Lines.Count > 1 ? item.Lines.Skip(1).Min(l => l.Left) : last.Left;
        var listItem = ListStart.IsMatch(item.Lines[0].Words[0].Text) || item.Lines[0].Tabs.Count > 0;
        if (line.Left > bodyLeft + 0.8 * em && !listItem && (item.Lines.Count > 1 || line.Left > last.Left + 0.8 * em))
            return true;

        // The previous line ended although the next word would have fitted: the paragraph ended there.
        var right = Math.Max(item.Right, line.Right);
        var firstWord = line.Words[0].Right - line.Words[0].Left;
        return last.Right + 0.3 * em + firstWord < right - 0.5 * em && right - item.Left > 10 * em;
    }

    // ───────────────────────── Reading order ─────────────────────────

    /// <summary>Orders a page's paragraphs and pictures for reading and sets the column each one sits in.</summary>
    private static List<Item> ReadingOrder(List<Item> items)
    {
        if (items.Count == 0)
            return items;
        var areaLeft = items.Min(i => i.Left);
        var areaRight = items.Max(i => i.Right);
        foreach (var item in items)
            (item.ContainerLeft, item.ContainerRight) = (areaLeft, areaRight);

        var gutters = Gutters(items, areaLeft, areaRight);
        if (gutters.Count == 0)
            return [.. items.OrderByDescending(i => i.Top).ThenBy(i => i.Left)];

        // Column bounds between the gutters.
        var edges = new List<double> { areaLeft };
        foreach (var (start, end) in gutters)
            edges.AddRange([start, end]);
        edges.Add(areaRight);
        int ColumnOf(Item item)
        {
            var center = (item.Left + item.Right) / 2;
            for (var c = 0; c < edges.Count / 2; c++)
            {
                if (center <= edges[2 * c + 1] + 1)
                    return c;
            }
            return edges.Count / 2 - 1;
        }
        bool Spans(Item item) => gutters.Any(g => item.Left < g.Start - 1 && item.Right > g.End + 1);

        var columnItems = items.Where(i => !Spans(i)).ToList();
        foreach (var item in columnItems)
        {
            var column = ColumnOf(item);
            var members = columnItems.Where(i => ColumnOf(i) == column).ToList();
            (item.ContainerLeft, item.ContainerRight) = (members.Min(i => i.Left), members.Max(i => i.Right));
        }

        // Spanning items split the page into bands; each band is read column by column.
        var result = new List<Item>();
        var remaining = columnItems.ToList();
        foreach (var separator in items.Where(Spans).OrderByDescending(i => i.Top))
        {
            var above = remaining.Where(i => i.Top > separator.Top).ToList();
            result.AddRange(above.OrderBy(ColumnOf).ThenByDescending(i => i.Top));
            remaining.RemoveAll(above.Contains);
            result.Add(separator);
        }
        result.AddRange(remaining.OrderBy(ColumnOf).ThenByDescending(i => i.Top));
        return result;
    }

    /// <summary>
    /// Vertical strips between columns: no narrow item crosses them, text lies on both sides next to each other over
    /// several lines, and the strip is at least a few points wide.
    /// </summary>
    private static List<(double Start, double End)> Gutters(List<Item> items, double left, double right)
    {
        var width = right - left;
        var narrow = items.Where(i => i.Width < width * 0.6).ToList();
        if (narrow.Count < 2 || width < 100)
            return [];

        const double Bin = 2;
        var bins = (int)Math.Ceiling(width / Bin);
        var covered = new bool[bins];
        foreach (var item in narrow)
        {
            for (var b = Math.Max(0, (int)((item.Left - left) / Bin)); b < Math.Min(bins, (int)Math.Ceiling((item.Right - left) / Bin)); b++)
                covered[b] = true;
        }

        var gutters = new List<(double, double)>();
        for (var b = 0; b < bins;)
        {
            if (covered[b])
            {
                b++;
                continue;
            }
            var start = b;
            while (b < bins && !covered[b])
                b++;
            var (gapLeft, gapRight) = (left + start * Bin, left + b * Bin);
            if (start == 0 || b >= bins || gapRight - gapLeft < 6)
                continue;

            // Real columns: both sides hold a good share of the page's text height (a heading and a date on one line
            // do not), with text next to each other over a few lines.
            var leftSide = narrow.Where(i => i.Right <= gapLeft + 1).ToList();
            var rightSide = narrow.Where(i => i.Left >= gapRight - 1).ToList();
            var textHeight = Math.Max(1, (items.Max(i => i.Top) - items.Min(i => i.Bottom)) * 0.2);
            static double Height(List<Item> side) => side.Sum(i => i.Top - i.Bottom);
            static int Lines(List<Item> side) => side.Sum(i => Math.Max(1, i.Lines.Count));
            var overlapping = leftSide.Any(l => rightSide.Any(r => Math.Min(l.Top, r.Top) - Math.Max(l.Bottom, r.Bottom) > 2 * Math.Max(l.Size, r.Size)));
            if (Height(leftSide) >= textHeight && Height(rightSide) >= textHeight && Lines(leftSide) >= 3 && Lines(rightSide) >= 3 && overlapping)
                gutters.Add((gapLeft, gapRight));
        }
        return gutters;
    }
}
