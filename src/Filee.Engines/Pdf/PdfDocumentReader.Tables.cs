// Ruled tables for PdfDocumentReader: tables drawn with lines become real tables (PDF → DOCX, HWPX, ...).
//
// The page's vector paths give the rules: stroked straight lines and rectangle edges, and thin filled rectangles
// (thick rules are often drawn that way). Rules on one line are joined, rules that touch each other form a grid,
// and the grid's lines are the column and row edges. A unit of the grid without a rule between it and its neighbour
// is merged with it (a spanned cell). Words go into the cell under their centre; each cell's text is read like the
// page (lines, paragraphs, alignment within the cell), and cell borders, shading and row heights come from the PDF.
//
// Conservative on purpose: a grid needs 2 rows and 2 columns, merged cells must be rectangles, text must sit in the
// cells (a rule through a word means a chart or a strike-through, not a table) and at least two cells hold text.
// Anything else stays text in reading order as before. Tables without rules (aligned by white space only) are not
// detected; a table running over several pages becomes one table per page.

using Filee.Engines.Hwp.Hwpx;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace Filee.Engines.Pdf;

internal sealed partial class PdfDocumentReader
{
    /// <summary>Thickest line (points) still taken for a rule; thicker filled rectangles are shading.</summary>
    private const double MaxRuleThickness = 4;

    /// <summary>Shortest rule (points): shorter strokes are glyph parts, ticks and bullets.</summary>
    private const double MinRuleLength = 4;

    /// <summary>Rules this close (points) across their direction lie on one line.</summary>
    private const double SameLine = 1.5;

    /// <summary>Gap (points) along a line that still joins two rules, and the reach of a rule to one it touches.</summary>
    private const double RuleGap = 2.5;

    /// <summary>Grid lines this close (points) are one edge (double rules, a frame drawn twice).</summary>
    private const double EdgeTolerance = 3;

    /// <summary>Pages with more line segments than this are drawings (maps, charts), not forms.</summary>
    private const int MaxSegments = 5000;

    /// <summary>A straight rule: horizontal (Position is y, Start..End is x) or vertical (Position is x, Start..End is y).</summary>
    private sealed record Rule(bool Horizontal, double Position, double Start, double End, double Thickness, string Color, bool Dashed)
    {
        public double Length => End - Start;
    }

    /// <summary>A cell of a ruled table: its place in the grid, its box (points) and the words in it.</summary>
    private sealed class RuledCell(int row, int column, int rowSpan, int columnSpan, PdfRectangle bounds)
    {
        public int Row { get; } = row;
        public int Column { get; } = column;
        public int RowSpan { get; } = rowSpan;
        public int ColumnSpan { get; } = columnSpan;
        public PdfRectangle Bounds { get; } = bounds;
        public List<TextWord> Words { get; } = [];
        public HBorders Borders { get; set; } = HBorders.Empty;
        public string? Fill { get; set; }
    }

    /// <summary>A table found from the rules of a page.</summary>
    private sealed class RuledTable(PdfRectangle bounds, double[] columns, double[] rows, List<RuledCell> cells)
    {
        public PdfRectangle Bounds { get; } = bounds;

        /// <summary>Column edges left to right (one more than columns).</summary>
        public double[] Columns { get; } = columns;

        /// <summary>Row edges top to bottom (one more than rows).</summary>
        public double[] Rows { get; } = rows;

        public List<RuledCell> Cells { get; } = cells;

        /// <summary>Distance from a cell's left edge to its text in left-aligned cells (the PDF's cell padding).</summary>
        public double Padding { get; set; }

        public string Text => string.Join(' ', Cells.SelectMany(c => c.Words).Select(w => w.Text));
    }

    // ───────────────────────── Rules ─────────────────────────

    /// <summary>
    /// The rules of a page (stroked lines and edges, thin filled rectangles) and the larger filled rectangles that
    /// may be cell shading, shifted like the words. Invisible (white, clipping) paths are left out.
    /// </summary>
    private static (List<Rule> Rules, List<(PdfRectangle Bounds, string Color)> Shades) PageRules(IEnumerable<PdfPath> paths, PdfRectangle crop)
    {
        var rules = new List<Rule>();
        var shades = new List<(PdfRectangle, string)>();
        var segments = 0;
        foreach (var path in paths)
        {
            if (path.IsClipping || (!path.IsFilled && !path.IsStroked))
                continue;
            var stroke = path.IsStroked ? Color(path.StrokeColor) ?? "#000000" : null;
            var fill = path.IsFilled ? Color(path.FillColor) ?? "#000000" : null;
            var dashed = path.LineDashPattern is { Array.Count: > 0 };
            foreach (var subpath in path)
            {
                var lines = new List<(PdfPoint From, PdfPoint To)>();
                var curved = false;
                PdfPoint? start = null, current = null;
                foreach (var command in subpath.Commands)
                {
                    switch (command)
                    {
                        case PdfSubpath.Move move:
                            start = current = move.Location;
                            break;
                        case PdfSubpath.Line line:
                            lines.Add((line.From, line.To));
                            start ??= line.From;
                            current = line.To;
                            break;
                        case PdfSubpath.BezierCurve curve:
                            curved = true;
                            current = curve.EndPoint;
                            break;
                        case PdfSubpath.Close when start is { } first && current is { } last:
                            lines.Add((last, first));
                            current = first;
                            break;
                    }
                }
                segments += lines.Count;
                if (segments > MaxSegments)
                    return ([], []);
                var shifted = lines.Select(l => (From: new PdfPoint(l.From.X - crop.Left, l.From.Y - crop.Bottom), To: new PdfPoint(l.To.X - crop.Left, l.To.Y - crop.Bottom))).ToList();

                if (stroke is not null && !IsWhite(stroke))
                {
                    var thickness = Math.Max(0.25, path.LineWidth);
                    foreach (var (from, to) in shifted)
                    {
                        if (Math.Abs(from.Y - to.Y) <= 0.5 && Math.Abs(from.X - to.X) >= MinRuleLength)
                            rules.Add(new Rule(true, (from.Y + to.Y) / 2, Math.Min(from.X, to.X), Math.Max(from.X, to.X), thickness, stroke, dashed));
                        else if (Math.Abs(from.X - to.X) <= 0.5 && Math.Abs(from.Y - to.Y) >= MinRuleLength)
                            rules.Add(new Rule(false, (from.X + to.X) / 2, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y), thickness, stroke, dashed));
                    }
                }

                // A filled shape of straight, upright edges: a thin one is a rule, a larger one may shade a cell.
                if (fill is null || curved || shifted.Count < 3 || IsWhite(fill)
                    || !shifted.All(l => Math.Abs(l.From.X - l.To.X) <= 0.5 || Math.Abs(l.From.Y - l.To.Y) <= 0.5))
                    continue;
                var left = shifted.Min(l => Math.Min(l.From.X, l.To.X));
                var right = shifted.Max(l => Math.Max(l.From.X, l.To.X));
                var bottom = shifted.Min(l => Math.Min(l.From.Y, l.To.Y));
                var top = shifted.Max(l => Math.Max(l.From.Y, l.To.Y));
                var (width, height) = (right - left, top - bottom);
                if (height <= MaxRuleThickness && width >= MinRuleLength && width > 2 * height)
                    rules.Add(new Rule(true, (top + bottom) / 2, left, right, Math.Max(0.25, height), fill, false));
                else if (width <= MaxRuleThickness && height >= MinRuleLength && height > 2 * width)
                    rules.Add(new Rule(false, (left + right) / 2, bottom, top, Math.Max(0.25, width), fill, false));
                else if (width > MaxRuleThickness && height > MaxRuleThickness)
                    shades.Add((new PdfRectangle(left, bottom, right, top), fill));
            }
        }
        return (JoinRules(rules), shades);
    }

    private static bool IsWhite(string color) =>
        Convert.ToInt32(color[1..3], 16) >= 242 && Convert.ToInt32(color[3..5], 16) >= 242 && Convert.ToInt32(color[5..7], 16) >= 242;

    /// <summary>Rules on one line that overlap or nearly touch become one rule (cells drawn one by one share edges).</summary>
    private static List<Rule> JoinRules(List<Rule> rules)
    {
        var result = new List<Rule>();
        foreach (var direction in rules.GroupBy(r => r.Horizontal))
        {
            var sorted = direction.OrderBy(r => r.Position).ToList();
            for (var i = 0; i < sorted.Count;)
            {
                var j = i + 1;
                while (j < sorted.Count && sorted[j].Position - sorted[j - 1].Position <= SameLine)
                    j++;
                Rule? current = null;
                foreach (var rule in sorted[i..j].OrderBy(r => r.Start))
                {
                    if (current is not null && rule.Start <= current.End + RuleGap)
                    {
                        current = current with
                        {
                            End = Math.Max(current.End, rule.End),
                            Thickness = Math.Max(current.Thickness, rule.Thickness),
                            // The longer piece decides where the line is and how it looks.
                            Position = rule.Length > current.Length ? rule.Position : current.Position,
                            Color = rule.Length > current.Length ? rule.Color : current.Color,
                            Dashed = current.Dashed && rule.Dashed,
                        };
                    }
                    else
                    {
                        if (current is not null)
                            result.Add(current);
                        current = rule;
                    }
                }
                result.Add(current!);
                i = j;
            }
        }
        return result;
    }

    // ───────────────────────── Grids ─────────────────────────

    /// <summary>
    /// Ruled tables of a page; the words in them are taken out of <paramref name="words"/>. Grids are tried largest
    /// first, and a grid inside a table already found (a table in a cell) stays part of that cell.
    /// </summary>
    private static List<RuledTable> RuledTables(IEnumerable<PdfPath> paths, PdfRectangle crop, List<TextWord> words)
    {
        var (rules, shades) = PageRules(paths, crop);
        var tables = new List<RuledTable>();
        foreach (var group in Grids(rules).OrderByDescending(g => Area(Extent(g))))
        {
            var extent = Extent(group);
            if (tables.Any(t => Overlaps(t.Bounds, extent)))
                continue;
            if (Table(group, words, shades) is not { } table)
                continue;
            tables.Add(table);
            var taken = table.Cells.SelectMany(c => c.Words).ToHashSet();
            words.RemoveAll(taken.Contains);
        }
        return tables;
    }

    /// <summary>Groups of rules that touch each other (a horizontal and a vertical rule crossing or meeting).</summary>
    private static IEnumerable<List<Rule>> Grids(List<Rule> rules)
    {
        var parent = Enumerable.Range(0, rules.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var a = 0; a < rules.Count; a++)
        {
            if (!rules[a].Horizontal)
                continue;
            for (var b = 0; b < rules.Count; b++)
            {
                if (!rules[b].Horizontal && Touch(rules[a], rules[b]))
                    parent[Find(a)] = Find(b);
            }
        }
        return Enumerable.Range(0, rules.Count).GroupBy(Find).Select(g => g.Select(i => rules[i]).ToList())
            .Where(g => g.Any(r => r.Horizontal) && g.Any(r => !r.Horizontal) && g.Count >= 4);
    }

    private static bool Touch(Rule horizontal, Rule vertical)
    {
        var reach = RuleGap + Math.Max(horizontal.Thickness, vertical.Thickness) / 2;
        return vertical.Position >= horizontal.Start - reach && vertical.Position <= horizontal.End + reach
               && horizontal.Position >= vertical.Start - reach && horizontal.Position <= vertical.End + reach;
    }

    /// <summary>
    /// The box of a grid: its outer vertical and horizontal rules, or further out where most rules of the other
    /// direction reach (a table without side or bottom rules).
    /// </summary>
    private static PdfRectangle Extent(List<Rule> group)
    {
        var horizontal = group.Where(r => r.Horizontal).ToList();
        var vertical = group.Where(r => !r.Horizontal).ToList();
        var left = vertical.Min(r => r.Position);
        var right = vertical.Max(r => r.Position);
        var bottom = horizontal.Min(r => r.Position);
        var top = horizontal.Max(r => r.Position);
        left = Math.Min(left, Median(horizontal.Select(r => r.Start)));
        right = Math.Max(right, Median(horizontal.Select(r => r.End)));
        bottom = Math.Min(bottom, Median(vertical.Select(r => r.Start)));
        top = Math.Max(top, Median(vertical.Select(r => r.End)));
        return new PdfRectangle(left, bottom, right, top);
    }

    private static double Area(PdfRectangle box) => box.Width * box.Height;

    private static bool Overlaps(PdfRectangle a, PdfRectangle b) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > EdgeTolerance && Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom) > EdgeTolerance;

    /// <summary>Positions closer than <see cref="EdgeTolerance"/> merged into one (their mean), sorted.</summary>
    private static List<double> Edges(IEnumerable<double> positions)
    {
        var result = new List<List<double>>();
        foreach (var position in positions.Order())
        {
            if (result.Count > 0 && position - result[^1][^1] <= EdgeTolerance)
                result[^1].Add(position);
            else
                result.Add([position]);
        }
        return result.Select(g => g.Average()).ToList();
    }

    /// <summary>
    /// The rule covering most of the segment from <paramref name="from"/> to <paramref name="to"/> on the line at
    /// <paramref name="position"/>, or null when rules cover less than half of it.
    /// </summary>
    private static Rule? Covering(List<Rule> rules, double position, double from, double to)
    {
        var length = to - from;
        if (length <= 0)
            return null;
        var covered = 0.0;
        Rule? best = null;
        var bestOverlap = 0.0;
        foreach (var rule in rules)
        {
            if (Math.Abs(rule.Position - position) > EdgeTolerance + rule.Thickness / 2)
                continue;
            var overlap = Math.Min(to, rule.End) - Math.Max(from, rule.Start);
            if (overlap <= 0)
                continue;
            covered += overlap;
            if (overlap > bestOverlap)
                (best, bestOverlap) = (rule, overlap);
        }
        return covered >= 0.5 * length ? best : null;
    }

    /// <summary>The table a grid of rules draws, with the words in its cells; null when it does not look like one.</summary>
    private static RuledTable? Table(List<Rule> group, List<TextWord> words, List<(PdfRectangle Bounds, string Color)> shades)
    {
        var extent = Extent(group);
        var horizontal = group.Where(r => r.Horizontal).ToList();
        var vertical = group.Where(r => !r.Horizontal).ToList();
        var xs = Edges(vertical.Select(r => r.Position).Where(x => x > extent.Left && x < extent.Right).Append(extent.Left).Append(extent.Right));
        var ys = Edges(horizontal.Select(r => r.Position).Where(y => y > extent.Bottom && y < extent.Top).Append(extent.Bottom).Append(extent.Top));
        ys.Reverse(); // top to bottom

        // Grid lines that separate no two units anywhere (a rule too short to bound a cell) are dropped.
        bool VerticalEdge(int c, int r) => Covering(vertical, xs[c], ys[r + 1], ys[r]) is not null;
        bool HorizontalEdge(int r, int c) => Covering(horizontal, ys[r], xs[c], xs[c + 1]) is not null;
        while (true)
        {
            var column = Enumerable.Range(1, Math.Max(0, xs.Count - 2)).FirstOrDefault(c => !Enumerable.Range(0, ys.Count - 1).Any(r => VerticalEdge(c, r)));
            if (column > 0)
            {
                xs.RemoveAt(column);
                continue;
            }
            var row = Enumerable.Range(1, Math.Max(0, ys.Count - 2)).FirstOrDefault(r => !Enumerable.Range(0, xs.Count - 1).Any(c => HorizontalEdge(r, c)));
            if (row == 0)
                break;
            ys.RemoveAt(row);
        }
        var (rows, columns) = (ys.Count - 1, xs.Count - 1);
        if (rows < 2 || columns < 2 || rows * columns > 2500)
            return null;

        // Units of the grid joined where no rule separates them; each group must be a rectangle (a cell).
        var owner = new int[rows, columns];
        var parent = Enumerable.Range(0, rows * columns).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                if (c + 1 < columns && !VerticalEdge(c + 1, r))
                    parent[Find(r * columns + c)] = Find(r * columns + c + 1);
                if (r + 1 < rows && !HorizontalEdge(r + 1, c))
                    parent[Find(r * columns + c)] = Find((r + 1) * columns + c);
            }
        }
        var cells = new List<RuledCell>();
        foreach (var unit in Enumerable.Range(0, rows * columns).GroupBy(Find))
        {
            var (top, bottom) = (unit.Min(u => u / columns), unit.Max(u => u / columns));
            var (left, right) = (unit.Min(u => u % columns), unit.Max(u => u % columns));
            if ((bottom - top + 1) * (right - left + 1) != unit.Count())
                return null; // an L-shaped region: not a table cell
            var cell = new RuledCell(top, left, bottom - top + 1, right - left + 1, new PdfRectangle(xs[left], ys[bottom + 1], xs[right + 1], ys[top]));
            foreach (var u in unit)
                owner[u / columns, u % columns] = cells.Count;
            cells.Add(cell);
        }
        if (cells.Count < 4 && (cells.Select(c => c.Row).Distinct().Count() < 2 || cells.Select(c => c.Column).Distinct().Count() < 2))
            return null;

        // Words by their centre; a rule through the middle of a word means this is no table around text.
        var bounds = new PdfRectangle(xs[0], ys[^1], xs[^1], ys[0]);
        foreach (var word in words)
        {
            var (x, y) = ((word.Left + word.Right) / 2, (word.Top + word.Bottom) / 2);
            if (x <= bounds.Left || x >= bounds.Right || y <= bounds.Bottom || y >= bounds.Top)
                continue;
            var band = (word.Top - word.Bottom) * 0.3;
            if (vertical.Any(v => v.Position > word.Left + 1 && v.Position < word.Right - 1 && v.Start < y && v.End > y)
                || horizontal.Any(h => h.Position > word.Bottom + band && h.Position < word.Top - band && h.Start < word.Right - 1 && h.End > word.Left + 1))
                return null;
            var column = Math.Clamp(xs.FindLastIndex(e => e <= x), 0, columns - 1);
            var row = Math.Clamp(ys.FindLastIndex(e => e >= y), 0, rows - 1);
            cells[owner[row, column]].Words.Add(word);
        }
        if (cells.Count(c => c.Words.Count > 0) < 2)
            return null;

        foreach (var cell in cells)
        {
            var box = cell.Bounds;
            cell.Borders = new HBorders(
                Border(Covering(vertical, box.Left, box.Bottom, box.Top)),
                Border(Covering(vertical, box.Right, box.Bottom, box.Top)),
                Border(Covering(horizontal, box.Top, box.Left, box.Right)),
                Border(Covering(horizontal, box.Bottom, box.Left, box.Right)));
            // Shading: a filled rectangle inside the table that covers most of the cell.
            cell.Fill = shades.LastOrDefault(s => s.Bounds.Left >= bounds.Left - EdgeTolerance && s.Bounds.Right <= bounds.Right + EdgeTolerance
                                                  && s.Bounds.Bottom >= bounds.Bottom - EdgeTolerance && s.Bounds.Top <= bounds.Top + EdgeTolerance
                                                  && Intersection(s.Bounds, box) >= 0.8 * Area(box)).Color;
        }

        var table = new RuledTable(bounds, [.. xs], [.. ys], cells);
        var paddings = cells.Where(c => c.Words.Count > 0).Select(c => c.Words.Min(w => w.Left) - c.Bounds.Left).ToList();
        table.Padding = Math.Clamp(paddings.Min(), 1, 8);
        return table;
    }

    private static HBorder Border(Rule? rule) => rule is null
        ? HBorder.None
        : new HBorder(rule.Dashed ? HBorderStyle.Dash : HBorderStyle.Solid, Math.Max(0.1, rule.Thickness * 25.4 / 72), rule.Color);

    private static double Intersection(PdfRectangle a, PdfRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));

    // ───────────────────────── Building ─────────────────────────

    /// <summary>
    /// The table of a ruled table item: column widths and row heights from the PDF, each cell's text read like a page
    /// (paragraphs, alignment within the cell, fonts), borders and shading per cell.
    /// </summary>
    private static HTable TableBlock(Item item, double lineRatio)
    {
        var grid = item.Table!;
        var padding = grid.Padding;
        var table = new HTable
        {
            ColumnCount = grid.Columns.Length - 1,
            ColumnWidths = [.. grid.Columns.Zip(grid.Columns.Skip(1), (a, b) => Hwp(b - a))],
            Borders = HBorders.Empty,
            CellMargin = new HInsets(Hwp(padding), Hwp(padding), Hwp(1), Hwp(1)),
        };
        var containerWidth = item.ContainerRight - item.ContainerLeft;
        var center = (grid.Bounds.Left + grid.Bounds.Right) / 2;
        if (Math.Abs(center - (item.ContainerLeft + item.ContainerRight) / 2) < Math.Max(6, containerWidth * 0.05) && grid.Bounds.Width < containerWidth * 0.9)
            table.Align = HAlign.Center;
        else if (grid.Bounds.Left - item.ContainerLeft > 2)
            table.Indent = Hwp(grid.Bounds.Left - item.ContainerLeft);

        for (var r = 0; r < grid.Rows.Length - 1; r++)
            table.Rows.Add(new HRow { Height = Hwp(grid.Rows[r] - grid.Rows[r + 1]) });
        var noHeadings = new Dictionary<double, int>();
        foreach (var cell in grid.Cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
        {
            var hcell = new HCell
            {
                Column = cell.Column,
                RowSpan = cell.RowSpan,
                ColSpan = cell.ColumnSpan,
                Borders = cell.Borders,
                Fill = cell.Fill,
            };
            if (cell.Words.Count > 0)
            {
                var items = TextItems(cell.Words).OrderByDescending(i => i.Top).ThenBy(i => i.Left).ToList();
                Item? previous = null;
                foreach (var paragraph in items)
                {
                    (paragraph.ContainerLeft, paragraph.ContainerRight) = (cell.Bounds.Left + padding, cell.Bounds.Right - padding);
                    hcell.Blocks.Add(TextParagraph(paragraph, previous, noHeadings, lineRatio));
                    previous = paragraph;
                }
                hcell.VerticalAlign = VerticalAlign(cell, items);
            }
            table.Rows[cell.Row].Cells.Add(hcell);
        }
        return table;
    }

    /// <summary>Text about as far from the top as from the bottom of a taller cell is centred; text at the bottom stays there.</summary>
    private static HVerticalAlign VerticalAlign(RuledCell cell, List<Item> items)
    {
        var above = cell.Bounds.Top - items.Max(i => i.Top);
        var below = items.Min(i => i.Bottom) - cell.Bounds.Bottom;
        if (above < 0 || below < 0)
            return HVerticalAlign.Top;
        if (Math.Abs(above - below) <= Math.Max(2, 0.25 * (above + below)) && above + below > 4)
            return HVerticalAlign.Center;
        return below < 0.4 * above && above > items[0].Size * 1.5 ? HVerticalAlign.Bottom : HVerticalAlign.Top;
    }
}
