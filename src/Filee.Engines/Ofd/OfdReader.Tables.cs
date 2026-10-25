// Ruled tables for OfdReader: grids drawn with lines become real tables — the frame of an e-invoice (buyer and
// seller blocks, the item list, totals, remarks) and the forms of official documents.
//
// The same approach as the PDF reader's (PdfDocumentReader.Tables), in millimetres and with y growing downwards:
// path objects give the rules (stroked straight segments, thin filled rectangles), rules on one line are joined,
// rules that touch form a grid, the grid's lines are the column and row edges, and units without a rule between them
// merge into one cell. Text goes into the cell its first character is in; each cell's text is laid out like the
// page (lines, tabbed rows, paragraphs). Conservative: a grid needs 2 rows and 2 columns, cells must be rectangles
// and at least two hold text; anything else stays text in reading order.

using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ofd;

internal sealed partial class OfdReader
{
    /// <summary>Thickest line (mm) still taken for a rule; thicker filled rectangles are shading.</summary>
    private const double MaxRuleThickness = 1.5;

    /// <summary>Shortest rule (mm): shorter strokes are glyph parts, ticks and QR code modules.</summary>
    private const double MinRuleLength = 1.5;

    /// <summary>Rules this close (mm) across their direction lie on one line.</summary>
    private const double SameLine = 0.5;

    /// <summary>Gap (mm) along a line that still joins two rules, and the reach of a rule to one it touches.</summary>
    private const double RuleGap = 0.9;

    /// <summary>Grid lines this close (mm) are one edge (double rules, a frame drawn twice).</summary>
    private const double EdgeTolerance = 1.0;

    /// <summary>Pages with more straight segments than this are drawings, not forms; their tables are not looked for.</summary>
    private const int MaxSegments = 5000;

    /// <summary>A straight rule: horizontal (Position is y, Start..End is x) or vertical (Position is x, Start..End is y).</summary>
    private sealed record Rule(bool Horizontal, double Position, double Start, double End, double Thickness, string Color)
    {
        public double Length => End - Start;
    }

    /// <summary>A filled rectangle that may shade a cell.</summary>
    private sealed record Shade(double Left, double Top, double Right, double Bottom, string Color);

    private sealed class RuledCell(int row, int column, int rowSpan, int columnSpan, double left, double top, double right, double bottom)
    {
        public int Row { get; } = row;
        public int Column { get; } = column;
        public int RowSpan { get; } = rowSpan;
        public int ColumnSpan { get; } = columnSpan;
        public double Left { get; } = left;
        public double Top { get; } = top;
        public double Right { get; } = right;
        public double Bottom { get; } = bottom;
        public List<Fragment> Fragments { get; } = [];
        public HBorders Borders { get; set; } = HBorders.Empty;
        public string? Fill { get; set; }
    }

    private sealed class RuledTable(double[] columns, double[] rows, List<RuledCell> cells)
    {
        /// <summary>Column edges left to right (one more than columns).</summary>
        public double[] Columns { get; } = columns;

        /// <summary>Row edges top to bottom (one more than rows).</summary>
        public double[] Rows { get; } = rows;

        public List<RuledCell> Cells { get; } = cells;
        public double Left => Columns[0];
        public double Right => Columns[^1];
        public double Top => Rows[0];
        public double Bottom => Rows[^1];
    }

    // ───────────────────────── Paths ─────────────────────────

    /// <summary>The rules and shading rectangles a path object draws.</summary>
    private static void PathObject(XElement element, Resources res, PageContent page, string? layerDraw)
    {
        if (page.Segments > MaxSegments || Child(element, "AbbreviatedData")?.Value is not { Length: > 0 } data)
            return;
        var box = Box(Attr(element, "Boundary")) ?? default;
        var ctm = Matrix.Parse(Attr(element, "CTM"));
        var own = res.Draw(Attr(element, "DrawParam"));
        var layer = res.Draw(layerDraw);
        var stroke = Attr(element, "Stroke") != "false"
            ? ColorOf(Child(element, "StrokeColor")) ?? own.Stroke ?? layer.Stroke ?? "#000000"
            : null;
        // Without a colour a fill is transparent (the standard's default).
        var fill = Attr(element, "Fill") == "true" ? ColorOf(Child(element, "FillColor")) ?? own.Fill ?? layer.Fill : null;
        var lineWidth = (Number(Attr(element, "LineWidth")) ?? own.LineWidth ?? layer.LineWidth ?? 0.353) * (ctm.ScaleX + ctm.ScaleY) / 2;

        (double X, double Y) Point(double x, double y)
        {
            var (px, py) = ctm.Apply(x, y);
            return (box.X + px, box.Y + py);
        }

        var tokens = data.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<((double X, double Y) From, (double X, double Y) To)>();
        var curved = false;
        (double X, double Y)? start = null, current = null;
        void Flush()
        {
            AddRules(page, lines, curved, stroke, fill, lineWidth);
            lines.Clear();
            curved = false;
        }
        double At(int i) => i < tokens.Length ? Number(tokens[i]) ?? 0 : 0;

        for (var i = 0; i < tokens.Length && page.Segments <= MaxSegments;)
        {
            switch (tokens[i])
            {
                case "S" or "M":
                    Flush();
                    start = current = Point(At(i + 1), At(i + 2));
                    i += 3;
                    break;
                case "L":
                    var to = Point(At(i + 1), At(i + 2));
                    if (current is { } from)
                        lines.Add((from, to));
                    start ??= to;
                    current = to;
                    i += 3;
                    break;
                case "Q":
                    curved = true;
                    current = Point(At(i + 3), At(i + 4));
                    i += 5;
                    break;
                case "B":
                    curved = true;
                    current = Point(At(i + 5), At(i + 6));
                    i += 7;
                    break;
                case "A":
                    curved = true;
                    current = Point(At(i + 6), At(i + 7));
                    i += 8;
                    break;
                case "C":
                    if (current is { } last && start is { } first)
                        lines.Add((last, first));
                    current = start;
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }
        Flush();
    }

    private static void AddRules(PageContent page, List<((double X, double Y) From, (double X, double Y) To)> lines, bool curved,
        string? stroke, string? fill, double lineWidth)
    {
        page.Segments += lines.Count;
        if (lines.Count == 0 || page.Segments > MaxSegments)
            return;

        if (stroke is not null && !IsWhite(stroke))
        {
            var thickness = Math.Max(0.05, lineWidth);
            foreach (var (from, to) in lines)
            {
                if (Math.Abs(from.Y - to.Y) <= 0.2 && Math.Abs(from.X - to.X) >= MinRuleLength)
                    page.Rules.Add(new Rule(true, (from.Y + to.Y) / 2, Math.Min(from.X, to.X), Math.Max(from.X, to.X), thickness, stroke));
                else if (Math.Abs(from.X - to.X) <= 0.2 && Math.Abs(from.Y - to.Y) >= MinRuleLength)
                    page.Rules.Add(new Rule(false, (from.X + to.X) / 2, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y), thickness, stroke));
            }
        }

        // A filled shape of straight, upright edges: a thin one is a rule, a larger one may shade a cell.
        if (fill is null || curved || lines.Count < 3 || IsWhite(fill)
            || !lines.All(l => Math.Abs(l.From.X - l.To.X) <= 0.2 || Math.Abs(l.From.Y - l.To.Y) <= 0.2))
            return;
        var left = lines.Min(l => Math.Min(l.From.X, l.To.X));
        var right = lines.Max(l => Math.Max(l.From.X, l.To.X));
        var top = lines.Min(l => Math.Min(l.From.Y, l.To.Y));
        var bottom = lines.Max(l => Math.Max(l.From.Y, l.To.Y));
        var (width, height) = (right - left, bottom - top);
        if (height <= MaxRuleThickness && width >= MinRuleLength && width > 2 * height)
            page.Rules.Add(new Rule(true, (top + bottom) / 2, left, right, Math.Max(0.05, height), fill));
        else if (width <= MaxRuleThickness && height >= MinRuleLength && height > 2 * width)
            page.Rules.Add(new Rule(false, (left + right) / 2, top, bottom, Math.Max(0.05, width), fill));
        else if (width > MaxRuleThickness && height > MaxRuleThickness)
            page.Shades.Add(new Shade(left, top, right, bottom, fill));
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
                            Position = rule.Length > current.Length ? rule.Position : current.Position,
                            Color = rule.Length > current.Length ? rule.Color : current.Color,
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

    /// <summary>The ruled tables of a page, largest first; the fragments in their cells are taken out of <paramref name="fragments"/>.</summary>
    private static List<RuledTable> RuledTables(PageContent page, List<Fragment> fragments)
    {
        var tables = new List<RuledTable>();
        if (page.Segments > MaxSegments || page.Rules.Count < 4)
            return tables;
        var rules = JoinRules(page.Rules);
        foreach (var group in Grids(rules).Select(g => (Rules: g, Extent: Extent(g))).OrderByDescending(g => Area(g.Extent)))
        {
            if (tables.Any(t => Overlap(t.Left, t.Right, group.Extent.Left, group.Extent.Right) > EdgeTolerance
                                && Overlap(t.Top, t.Bottom, group.Extent.Top, group.Extent.Bottom) > EdgeTolerance))
                continue;
            if (Table(group.Rules, group.Extent, fragments, page.Shades) is not { } table)
                continue;
            tables.Add(table);
            var taken = table.Cells.SelectMany(c => c.Fragments).ToHashSet();
            fragments.RemoveAll(taken.Contains);
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

    /// <summary>The box of a grid: its outer rules, or further out where most rules of the other direction reach.</summary>
    private static (double Left, double Top, double Right, double Bottom) Extent(List<Rule> group)
    {
        var horizontal = group.Where(r => r.Horizontal).ToList();
        var vertical = group.Where(r => !r.Horizontal).ToList();
        var left = Math.Min(vertical.Min(r => r.Position), Median(horizontal.Select(r => r.Start)));
        var right = Math.Max(vertical.Max(r => r.Position), Median(horizontal.Select(r => r.End)));
        var top = Math.Min(horizontal.Min(r => r.Position), Median(vertical.Select(r => r.Start)));
        var bottom = Math.Max(horizontal.Max(r => r.Position), Median(vertical.Select(r => r.End)));
        return (left, top, right, bottom);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static double Area((double Left, double Top, double Right, double Bottom) box) => (box.Right - box.Left) * (box.Bottom - box.Top);

    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

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

    /// <summary>The rule covering most of a segment on the line at <paramref name="position"/>; null when rules cover less than half.</summary>
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

    /// <summary>The table a grid of rules draws, with the fragments in its cells; null when it does not look like one.</summary>
    private static RuledTable? Table(List<Rule> group, (double Left, double Top, double Right, double Bottom) extent,
        List<Fragment> fragments, List<Shade> shades)
    {
        var horizontal = group.Where(r => r.Horizontal).ToList();
        var vertical = group.Where(r => !r.Horizontal).ToList();
        var xs = Edges(vertical.Select(r => r.Position).Where(x => x > extent.Left && x < extent.Right).Append(extent.Left).Append(extent.Right));
        var ys = Edges(horizontal.Select(r => r.Position).Where(y => y > extent.Top && y < extent.Bottom).Append(extent.Top).Append(extent.Bottom));

        if ((xs.Count - 1) * (ys.Count - 1) > 2500)
            return null; // a drawing (hatching, a chart's grid), not a form

        // Grid lines that separate no two units anywhere (a rule too short to bound a cell) are dropped.
        bool VerticalEdge(int c, int r) => Covering(vertical, xs[c], ys[r], ys[r + 1]) is not null;
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
            foreach (var u in unit)
                owner[u / columns, u % columns] = cells.Count;
            cells.Add(new RuledCell(top, left, bottom - top + 1, right - left + 1, xs[left], ys[top], xs[right + 1], ys[bottom + 1]));
        }
        if (cells.Count < 4 && (cells.Select(c => c.Row).Distinct().Count() < 2 || cells.Select(c => c.Column).Distinct().Count() < 2))
            return null;

        // Fragments by their first character (a width estimated without DeltaX may reach past the cell). Many rules
        // through the middle of text mean a chart or crossed-out text, not a table.
        var crossed = 0;
        var inside = 0;
        foreach (var fragment in fragments)
        {
            var x = fragment.Left + Math.Min(fragment.Size, fragment.Width) / 2;
            var y = fragment.Vertical ? (fragment.Top + fragment.Bottom) / 2 : fragment.Baseline - 0.35 * fragment.Size;
            if (x <= xs[0] || x >= xs[^1] || y <= ys[0] || y >= ys[^1])
                continue;
            inside++;
            if (horizontal.Any(h => h.Position > fragment.Top + 0.3 * fragment.Size && h.Position < fragment.Baseline - 0.2 * fragment.Size
                                    && h.Start < fragment.Right - fragment.Size && h.End > fragment.Left + fragment.Size))
                crossed++;
            var column = Math.Clamp(xs.FindLastIndex(e => e <= x), 0, columns - 1);
            var row = Math.Clamp(ys.FindLastIndex(e => e <= y), 0, rows - 1);
            cells[owner[row, column]].Fragments.Add(fragment);
        }
        if (crossed > Math.Max(1, inside / 10) || cells.Count(c => c.Fragments.Count > 0) < 2)
            return null;

        foreach (var cell in cells)
        {
            cell.Borders = new HBorders(
                Border(Covering(vertical, cell.Left, cell.Top, cell.Bottom)),
                Border(Covering(vertical, cell.Right, cell.Top, cell.Bottom)),
                Border(Covering(horizontal, cell.Top, cell.Left, cell.Right)),
                Border(Covering(horizontal, cell.Bottom, cell.Left, cell.Right)));
            cell.Fill = shades.LastOrDefault(s => Math.Max(0, Overlap(s.Left, s.Right, cell.Left, cell.Right)) * Math.Max(0, Overlap(s.Top, s.Bottom, cell.Top, cell.Bottom))
                                                  >= 0.8 * (cell.Right - cell.Left) * (cell.Bottom - cell.Top)
                                                  && s.Right - s.Left < 1.5 * (cell.Right - cell.Left) + 2 && s.Bottom - s.Top < 1.5 * (cell.Bottom - cell.Top) + 2)?.Color;
        }
        return new RuledTable([.. xs], [.. ys], cells);
    }

    private static HBorder Border(Rule? rule) =>
        rule is null ? HBorder.None : new HBorder(HBorderStyle.Solid, Math.Max(0.1, rule.Thickness), rule.Color);

    // ───────────────────────── Building ─────────────────────────

    /// <summary>
    /// The table of a grid: column widths and row heights from the page, each cell's text laid out like the page
    /// (tabbed rows keep their columns), borders and shading per cell.
    /// </summary>
    private static HTable TableBlock(RuledTable grid, double containerLeft)
    {
        const double Padding = 0.5;
        var table = new HTable
        {
            ColumnCount = grid.Columns.Length - 1,
            ColumnWidths = [.. grid.Columns.Zip(grid.Columns.Skip(1), (a, b) => Hwp(b - a))],
            Borders = HBorders.Empty,
            CellMargin = new HInsets(Hwp(Padding), Hwp(Padding), 0, 0),
            Tight = true, // the next block's space before is the distance on the page
        };
        if (grid.Left - containerLeft > 0.5)
            table.Indent = Hwp(grid.Left - containerLeft);

        for (var r = 0; r < grid.Rows.Length - 1; r++)
            table.Rows.Add(new HRow { Height = Hwp(grid.Rows[r + 1] - grid.Rows[r]) });
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
            if (cell.Fragments.Count > 0)
            {
                var (left, right) = (cell.Left + Padding, cell.Right - Padding);
                // Only lines at about single spacing are one paragraph: widely spaced lines in a cell are rows (小计,
                // 折扣, 总计 under each other) that must stay level with the rows of the cells beside them, and renderers
                // place text differently inside a tall exact line (Word at its foot, 한글 and rhwp at its head).
                var paragraphs = FitLineHeights(Paragraphs(Lines(cell.Fragments), left, right, maxPitch: 1.6));

                // One paragraph about as far from the top as from the bottom is centred. Anything else keeps its distance
                // from the top (as the cell's top padding: space before a cell's first paragraph is not drawn
                // everywhere): the cells of a column-ruled list (序号, 项目名称, 金额 side by side, each with all rows)
                // hold their rows at the same heights, so they line up across the columns as on the page.
                var space = (Top: TextTop(paragraphs[0]) - cell.Top, Bottom: cell.Bottom - TextBottom(paragraphs[^1]));
                var centred = paragraphs.Count == 1 && space.Top > 1 && Math.Abs(space.Top - space.Bottom) <= Math.Max(1, 0.25 * (space.Top + space.Bottom));
                hcell.VerticalAlign = centred ? HVerticalAlign.Center : HVerticalAlign.Top;
                if (!centred && space.Top > 0.2)
                    hcell.CellMargin = new HInsets(Hwp(Padding), Hwp(Padding), Hwp(Math.Min(space.Top, 100)), 0);
                var flow = TextTop(paragraphs[0]);
                foreach (var paragraph in paragraphs)
                {
                    var before = Math.Clamp(TextTop(paragraph) - flow, 0, 100);
                    hcell.Blocks.Add(TextParagraph(paragraph, left, right, Hwp(before), exact: true));
                    flow += before + TextBottom(paragraph) - TextTop(paragraph);
                }
            }
            table.Rows[cell.Row].Cells.Add(hcell);
        }
        return table;
    }
}
