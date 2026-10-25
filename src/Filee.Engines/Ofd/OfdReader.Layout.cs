// Layout for OfdReader: the fragments of a page become lines (same baseline, left to right) and lines paragraphs
// (a line that ran to the right edge continues in the next one), in reading order from the top. A line with wide
// gaps (the columns of an invoice's item rows) keeps its parts at their places with tab stops; text under such a row
// in its columns is the wrapped end of its cells and follows in the same paragraph after a line break. Grids drawn with lines become tables first (OfdReader.Tables). Pictures
// alone in their band of the page become paragraphs; pictures beside or over text (QR codes, seals) float where
// they are on the paper. Vertical space between blocks is kept as space before paragraphs, so a page looks like the
// original.

using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ofd;

internal sealed partial class OfdReader
{
    private const double MmToUnit = 7200.0 / 25.4;

    /// <summary>Margins (mm) are kept within these bounds.</summary>
    private const double MinMargin = 3, MaxMargin = 40;

    private static int Hwp(double mm) => (int)Math.Round(Math.Clamp(mm * MmToUnit, -1e9, 1e9));

    /// <summary>
    /// Fragments on one baseline, left to right; <see cref="Tabs"/> are the ones a wide gap comes before.
    /// <see cref="Wrapped"/> holds the wrapped ends of a tabbed row's cells, each with the column it belongs to.
    /// </summary>
    private sealed class Line(Fragment first)
    {
        public List<Fragment> Fragments { get; } = [first];
        public HashSet<int> Tabs { get; } = [];
        public List<(Fragment Fragment, int Column)> Wrapped { get; } = [];
        public double Baseline { get; } = first.Baseline;
        public double Size => Fragments.Max(f => f.Size);
        public double Left => Fragments.Min(f => f.Left);
        public double Right => Fragments.Max(f => f.Right);
        public double LastBaseline => Fragments.Concat(Wrapped.Select(w => w.Fragment)).Max(f => f.Baseline + f.Run);
        public bool Vertical => Fragments[0].Vertical;

        /// <summary>Line distance of a paragraph of this one line when the line above leaves less than 1.2 sizes.</summary>
        public double? Cap { get; set; }
    }

    /// <summary>A paragraph, a table or a picture at its place on the page (mm from the top).</summary>
    private sealed record Placed(double Top, double Bottom, List<Line>? Lines = null, RuledTable? Table = null, HImage? Image = null, double Left = 0);

    // ───────────────────────── Pages ─────────────────────────

    private HDocument Build(List<PageContent> pages)
    {
        var document = new HDocument();
        var margins = pages.GroupBy(p => (p.Width, p.Height)).ToDictionary(g => g.Key, g => Margins(g));
        HSection? section = null;
        foreach (var page in pages)
        {
            var (left, top, right, bottom) = margins[(page.Width, page.Height)];
            var hpage = new HPage(Hwp(page.Width), Hwp(page.Height), Hwp(left), Hwp(right), Hwp(top), Hwp(bottom), 0, 0, 0);
            var newSection = section is null || section.Page != hpage;
            if (newSection)
            {
                section = new HSection { Page = hpage };
                document.Sections.Add(section);
            }
            var blocks = PageBlocks(page, left, page.Width - right, top);
            // A table cannot start a page in every format: a tiny empty paragraph before it carries the page break.
            if (blocks.Count == 0 || blocks[0] is HTable)
                blocks.Insert(0, Spacer());
            if (!newSection && blocks[0] is HParagraph first)
                first.PageBreakBefore = true;
            section!.Blocks.AddRange(blocks);
        }
        if (document.Sections.Count == 0)
        {
            var empty = new HSection();
            empty.Blocks.Add(new HParagraph());
            document.Sections.Add(empty);
        }
        return document;
    }

    /// <summary>
    /// Margins of the pages of one size: the smallest distances from the edges to anything drawn. The bottom one is
    /// halved, so text that comes out a little taller than in the original still fits on its page.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom) Margins(IEnumerable<PageContent> pages)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MaxValue, bottom = double.MaxValue;
        foreach (var page in pages)
        {
            var boxes = page.Fragments.Select(f => (f.Left, f.Top, f.Right, f.Bottom))
                .Concat(page.Rules.Select(r => r.Horizontal ? (r.Start, r.Position, r.End, r.Position) : (r.Position, r.Start, r.Position, r.End)))
                .Concat(page.Pictures.Select(p => (p.Left, p.Top, p.Right, p.Bottom)));
            foreach (var (l, t, r, b) in boxes)
            {
                left = Math.Min(left, l);
                top = Math.Min(top, t);
                right = Math.Min(right, page.Width - r);
                bottom = Math.Min(bottom, page.Height - b);
            }
        }
        static double Clamp(double value) => value == double.MaxValue ? 20 : Math.Clamp(value, MinMargin, MaxMargin);
        return (Clamp(left), Clamp(top), Clamp(right), Clamp(bottom / 2));
    }

    /// <summary>The blocks of one page in reading order; floating pictures go into the first paragraph.</summary>
    private List<HBlock> PageBlocks(PageContent page, double containerLeft, double containerRight, double marginTop)
    {
        var fragments = Deduplicated(page.Fragments);
        var items = new List<Placed>();
        foreach (var table in RuledTables(page, fragments))
            items.Add(new Placed(table.Top, table.Bottom, Table: table));
        foreach (var paragraph in FitLineHeights(Paragraphs(Lines(fragments), containerLeft, containerRight)))
            items.Add(new Placed(TextTop(paragraph), TextBottom(paragraph), Lines: paragraph));

        var floating = new List<HImage>();
        foreach (var picture in page.Pictures.OrderBy(p => p.Top))
        {
            if ((picture.File ?? (picture.Part is { } part ? Extract(part) : null)) is not { } file)
                continue;
            var image = new HImage(file) { Width = Hwp(picture.Width), Height = Hwp(picture.Height) };
            if (items.Any(i => i.Top < picture.Bottom - 0.5 && i.Bottom > picture.Top + 0.5))
            {
                image.Anchor = new HAnchor("PAPER", "LEFT", Hwp(picture.Left), "PAPER", "TOP", Hwp(picture.Top), "IN_FRONT_OF_TEXT", AllowOverlap: true);
                floating.Add(image);
            }
            else
            {
                items.Add(new Placed(picture.Top, picture.Bottom, Image: image, Left: picture.Left));
            }
        }

        // The blocks follow each other down the page; where one starts above the end of the one before (a title with
        // the invoice number beside it) it is pushed down, and the next gaps take that back, so the rest of the page
        // stays at its place.
        var blocks = new List<HBlock>();
        var flow = marginTop;
        foreach (var item in items.OrderBy(i => i.Top))
        {
            var before = Math.Clamp(item.Top - flow, 0, 100);
            if (item.Table is { } table)
            {
                // A table has no space before: a 1 pt paragraph carries the distance.
                if (before > 0.5)
                {
                    var spacer = Spacer();
                    spacer.Format = spacer.Format with { Before = Hwp(before - 0.35) };
                    blocks.Add(spacer);
                }
                blocks.Add(TableBlock(table, containerLeft));
            }
            else if (item.Image is { } image)
            {
                var paragraph = new HParagraph();
                var left = Hwp(item.Left - containerLeft);
                var centered = Math.Abs(left + image.Width!.Value / 2 - Hwp(containerRight - containerLeft) / 2) < Hwp(3);
                paragraph.Format = new HParaFormat(Align: centered ? HAlign.Center : HAlign.Left, Left: centered || left < Hwp(1) ? null : left,
                    Before: Hwp(before), After: 0, LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 100));
                paragraph.Inlines.Add(image);
                blocks.Add(paragraph);
            }
            else
            {
                // Exact line distance where the largest text fits in it: "at least" grows each line to the font's own
                // height in Word and LibreOffice (KaiTi: ~1.4 em), and a full invoice page ran over.
                var lines = item.Lines!;
                var exact = lines.Max(l => l.Size) <= LineHeight(lines) + 0.01;
                blocks.Add(TextParagraph(lines, containerLeft, containerRight, Hwp(before), exact));
            }
            flow += before + (item.Bottom - item.Top);
        }

        // The first block's distance from the top margin becomes the height of an empty line: space before the first
        // paragraph of a page is not drawn by every renderer (rhwp leaves it out, and the page came out 3 mm high).
        if (blocks.Count > 0 && blocks[0] is HParagraph first && first.Format.Before is > 0 and var top && top > Hwp(0.5))
        {
            var spacer = Spacer();
            spacer.Format = spacer.Format with { LineSpacing = new HLineSpacing(HLineSpacingKind.Fixed, top) };
            first.Format = first.Format with { Before = 0 };
            blocks.Insert(0, spacer);
        }

        if (floating.Count > 0)
        {
            if (blocks.Count == 0 || blocks[0] is not HParagraph)
                blocks.Insert(0, Spacer());
            ((HParagraph)blocks[0]).Inlines.InsertRange(0, floating);
        }
        return blocks;
    }

    /// <summary>An empty 1 pt paragraph with no spacing: carries a page break before a table, or the floating pictures.</summary>
    private static HParagraph Spacer() => new()
    {
        Format = new HParaFormat(Before: 0, After: 0, LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 100)),
        MarkFormat = new HCharFormat(Size: 100),
    };

    /// <summary>The same text drawn twice at one place (fake bold, a label in both the template and the page) once.</summary>
    private static List<Fragment> Deduplicated(List<Fragment> fragments)
    {
        var seen = new HashSet<(string, long, long)>();
        return fragments.Where(f => seen.Add((f.Text, (long)Math.Round(f.X * 3), (long)Math.Round(f.Baseline * 3)))).ToList();
    }

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>Fragments into lines: one baseline (within a fraction of the font size), ordered left to right.</summary>
    private static List<Line> Lines(IEnumerable<Fragment> fragments)
    {
        var lines = new List<Line>();
        foreach (var fragment in fragments.OrderBy(f => f.Baseline).ThenBy(f => f.X))
        {
            var line = fragment.Vertical ? null
                : lines.TakeLast(4).LastOrDefault(l => !l.Vertical && Math.Abs(l.Baseline - fragment.Baseline) <= 0.4 * Math.Min(l.Size, fragment.Size));
            if (line is null)
                lines.Add(new Line(fragment));
            else
                line.Fragments.Add(fragment);
        }
        lines = [.. lines.OrderBy(l => l.Fragments.Min(f => f.Top))];
        foreach (var line in lines)
        {
            line.Fragments.Sort((a, b) => a.X.CompareTo(b.X));
            for (var i = 1; i < line.Fragments.Count; i++)
            {
                var (previous, fragment) = (line.Fragments[i - 1], line.Fragments[i]);
                if (fragment.Left - previous.Right >= Math.Max(1.5 * fragment.Size, 2))
                    line.Tabs.Add(i);
            }
        }
        JoinWrappedCells(lines);
        return lines;
    }

    private static readonly Regex Amount = new(@"^[-+¥￥$€]?[\d,.]+%?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The wrapped ends of cells in a row (转印组 / 件套装 in an invoice's item list): a line right under a tabbed row,
    /// whose parts all start in that row's columns and are no amounts, belongs to the row — a new row always brings its
    /// own amounts. It stays a line of its own in the row's paragraph (joined, the longer text would push the row's
    /// columns apart).
    /// </summary>
    private static void JoinWrappedCells(List<Line> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            var (row, wrapped) = (lines[i - 1], lines[i]);
            var above = row.Wrapped.Count > 0 ? row.Wrapped[^1].Fragment.Baseline : row.Baseline;
            if (row.Tabs.Count < 2 || wrapped.Fragments.Count >= row.Fragments.Count || row.Vertical || wrapped.Vertical
                || !row.Fragments.Any(f => Amount.IsMatch(f.Text)) || wrapped.Fragments.Any(f => Amount.IsMatch(f.Text))
                || wrapped.Baseline - above > 2.2 * wrapped.Size)
            {
                continue;
            }
            var columns = wrapped.Fragments.Select(f => row.Fragments.FindIndex(c => Math.Abs(c.Left - f.Left) < 1.5)).ToList();
            if (columns.Any(c => c < 0 || c > 0 && !row.Tabs.Contains(c)) || columns.Distinct().Count() != columns.Count)
                continue;
            row.Wrapped.AddRange(wrapped.Fragments.Zip(columns));
            lines.RemoveAt(i--);
        }
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>
    /// Lines into paragraphs: a line continues the paragraph above when that one filled the measure (ran to the
    /// right edge from at least half-way), the size and line distance match and it starts no further right than the
    /// paragraph's body (the first line may be indented). Lines further apart than <paramref name="maxPitch"/> font
    /// sizes stay paragraphs of their own.
    /// </summary>
    private static List<List<Line>> Paragraphs(List<Line> lines, double containerLeft, double containerRight, double maxPitch = 2.5)
    {
        var paragraphs = new List<List<Line>>();
        foreach (var line in lines)
        {
            var current = paragraphs.LastOrDefault();
            if (current is not null && Continues(current, line, containerLeft, containerRight, maxPitch))
                current.Add(line);
            else
                paragraphs.Add([line]);
        }
        return paragraphs;
    }

    private static bool Continues(List<Line> paragraph, Line line, double containerLeft, double containerRight, double maxPitch)
    {
        var last = paragraph[^1];
        var size = last.Size;
        if (last.Tabs.Count > 0 || line.Tabs.Count > 0 || last.Vertical || line.Vertical || Math.Abs(line.Size - size) > 0.15 * size)
            return false;
        var pitch = line.Baseline - last.Baseline;
        if (pitch < 0.9 * size || pitch > maxPitch * size || paragraph.Count > 1 && Math.Abs(pitch - (last.Baseline - paragraph[^2].Baseline)) > 0.25 * size)
            return false;
        // A line its writer broke inside one TextCode (a block of cipher text) follows after a line break.
        if (line.Fragments[0].Continued)
            return line.Fragments.Count == 1 && Math.Abs(line.Left - last.Left) < 0.6 * size;
        if (paragraph.Skip(1).Any(l => l.Fragments[0].Continued))
            return false;
        var body = paragraph.Count > 1 ? paragraph.Skip(1).Min(l => l.Left) : paragraph[0].Left;
        if (last.Right < containerRight - 2.5 * size || last.Right - Math.Min(body, last.Left) < 0.5 * (containerRight - containerLeft))
            return false;
        return line.Left <= body + 0.6 * size && line.Left >= body - 4 * size;
    }

    /// <summary>Line distance of a paragraph: its baseline pitch (also to wrapped cell ends), or 1.2 times the font size.</summary>
    private static double LineHeight(List<Line> lines)
    {
        var size = lines[0].Size;
        var wrapped = lines[0].Wrapped.Select(w => w.Fragment.Baseline).Distinct().Count();
        var pitch = lines.Count > 1 ? (lines[^1].Baseline - lines[0].Baseline) / (lines.Count - 1)
            : wrapped > 0 ? (lines[0].LastBaseline - lines[0].Baseline) / wrapped
            : lines[0].Cap ?? size * 1.2;
        return Math.Clamp(pitch, size, size * 3);
    }

    /// <summary>
    /// A one-line paragraph closer to the line above than 1.2 times its size (an invoice's label rows, 4.7 mm apart
    /// with 4.2 mm numbers) gets a line distance that fits in between, else every such line pushes the rest down.
    /// </summary>
    private static List<List<Line>> FitLineHeights(List<List<Line>> paragraphs)
    {
        for (var i = 1; i < paragraphs.Count; i++)
        {
            var (above, line) = (paragraphs[i - 1][^1], paragraphs[i][0]);
            if (paragraphs[i].Count > 1 || line.Wrapped.Count > 0 || line.Vertical || above.Vertical)
                continue;
            var room = line.Baseline - above.LastBaseline + 0.25 * (line.Size - above.Size);
            if (room > 0 && room < 1.2 * line.Size)
                line.Cap = room;
        }
        return paragraphs;
    }

    /// <summary>Top of a paragraph's first line box (Word puts the extra line distance above the text).</summary>
    private static double TextTop(List<Line> lines) =>
        lines[0].Vertical ? lines[0].Fragments[0].Top : lines[0].Baseline - (LineHeight(lines) - 0.25 * lines[0].Size);

    private static double TextBottom(List<Line> lines) => lines[^1].LastBaseline + 0.25 * lines[^1].Size;

    /// <param name="exact">Exact line distance (table cells, whose rows must keep their height); else "at least".</param>
    private static HParagraph TextParagraph(List<Line> lines, double containerLeft, double containerRight, int before, bool exact = false)
    {
        var size = lines[0].Size;
        var width = Math.Max(1, containerRight - containerLeft);
        var leftGap = lines[0].Left - containerLeft;
        var rightGap = containerRight - lines.Max(l => l.Right);
        var tabbed = lines.Any(l => l.Tabs.Count > 0);

        HAlign align;
        if (lines.Count == 1 && !tabbed && Math.Abs(leftGap - rightGap) < Math.Max(1.5, width * 0.03) && leftGap > Math.Max(3, width * 0.05))
            align = HAlign.Center;
        else if (lines.Count == 1 && !tabbed && rightGap < 1.5 && leftGap > width * 0.2)
            align = HAlign.Right;
        else if (lines.Count > 1 && !lines.Skip(1).Any(l => l.Fragments[0].Continued))
            align = HAlign.Justify; // not lines that end in a line break: Word would spread them
        else
            align = HAlign.Left;

        int? left = null, firstLine = null;
        if (align is HAlign.Left or HAlign.Justify)
        {
            var body = (lines.Count > 1 ? lines.Skip(1).Min(l => l.Left) : lines[0].Left) - containerLeft;
            if (body > 0.5)
                left = Hwp(body);
            var first = lines[0].Left - containerLeft - Math.Max(0, body);
            if (Math.Abs(first) > 0.5)
                firstLine = Hwp(first);
        }

        // A tabbed row: a left tab stop where each part starts on the page.
        var tabs = lines.SelectMany(l => l.Tabs.Select(t => Hwp(l.Fragments[t].Left - containerLeft))).Distinct().Order()
            .Select(position => new HTabStop(position, HTabKind.Left, "NONE")).ToList();

        var paragraph = new HParagraph
        {
            Format = new HParaFormat(
                Align: align,
                Left: left,
                FirstLine: firstLine,
                Tabs: tabs.Count > 0 ? tabs : null,
                Before: before,
                After: 0,
                LineSpacing: new HLineSpacing(exact ? HLineSpacingKind.Fixed : HLineSpacingKind.AtLeast, Hwp(LineHeight(lines)))),
            MarkFormat = new HCharFormat(Size: lines[0].Fragments[0].Format.Size),
        };
        Runs(lines, paragraph.Inlines);
        return paragraph;
    }

    /// <summary>
    /// The text of a paragraph: fragments joined into runs of one format, a space where the page leaves one (and at
    /// line ends, except next to Chinese text), a tab before the parts of a tabbed row; wrapped cell ends after a line
    /// break, tabbed to their columns.
    /// </summary>
    private static void Runs(List<Line> lines, List<HInline> output)
    {
        var pieces = new List<(string Text, HCharFormat Format)>();
        void Space()
        {
            if (pieces.Count > 0 && pieces[^1].Text is not ("\t" or "\n") && !pieces[^1].Text.EndsWith(' '))
                pieces[^1] = (pieces[^1].Text + " ", pieces[^1].Format);
        }
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            for (var i = 0; i < line.Fragments.Count; i++)
            {
                var fragment = line.Fragments[i];
                if (i > 0 && line.Tabs.Contains(i))
                {
                    pieces.Add(("\t", fragment.Format));
                }
                else if (i > 0)
                {
                    var previous = line.Fragments[i - 1];
                    var gap = fragment.Left - previous.Right;
                    if (gap > 0.5 * fragment.Size || gap > 0.2 * fragment.Size && !IsWide(previous.Text[^1]) && !IsWide(fragment.Text[0]))
                        Space();
                }
                else if (l > 0 && fragment.Continued)
                {
                    pieces.Add(("\n", fragment.Format));
                }
                else if (l > 0 && pieces.Count > 0 && !IsWide(pieces[^1].Text[^1]) && !IsWide(fragment.Text[0]))
                {
                    Space();
                }
                pieces.Add((fragment.Text, fragment.Format));
            }

            // Wrapped cell ends, a line per baseline: column k's tab stop is the row's n-th tab.
            foreach (var wrappedLine in line.Wrapped.GroupBy(w => w.Fragment.Baseline))
            {
                pieces.Add(("\n", wrappedLine.First().Fragment.Format));
                var stop = 0;
                foreach (var (fragment, column) in wrappedLine.OrderBy(w => w.Column))
                {
                    for (var target = line.Tabs.Count(t => t <= column); stop < target; stop++)
                        pieces.Add(("\t", fragment.Format));
                    pieces.Add((fragment.Text, fragment.Format));
                }
            }
        }

        foreach (var (text, format) in pieces)
        {
            if (text == "\t")
                output.Add(new HTab(format));
            else if (text == "\n")
                output.Add(new HLineBreak(format));
            else if (output.Count > 0 && output[^1] is HText run && run.GetType() == typeof(HText) && run.Format == format)
                output[^1] = new HText(run.Text + text, format);
            else
                output.Add(new HText(text, format));
        }
    }
}
