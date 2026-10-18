// Structural checks for the HWPX document model: a canonical text dump (for exact comparisons such as
// read → write → read) and a tolerant comparison of a source model with the model read back from the HWPX it became.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

internal static class HDocumentAssert
{
    // ───────────────────────── Canonical dump ─────────────────────────

    /// <summary>Every member of every block, one line per item; pictures by content hash, lists by first appearance.</summary>
    /// <param name="layout">Include layout caches (table row heights), which the writer estimates.</param>
    public static string Dump(HDocument document, bool layout = true)
    {
        var dump = new Dumper(layout);
        dump.Line(0, $"document title={document.Title}");
        foreach (var section in document.Sections)
        {
            dump.Line(0, $"section page={section.Page} columns={section.Columns} hide={section.HideFirstHeader}/{section.HideFirstFooter} start={section.StartPageNumber}");
            foreach (var header in section.Headers)
            {
                dump.Line(1, $"header {header.Pages}");
                dump.Blocks(2, header.Blocks);
            }
            foreach (var footer in section.Footers)
            {
                dump.Line(1, $"footer {footer.Pages}");
                dump.Blocks(2, footer.Blocks);
            }
            dump.Blocks(1, section.Blocks);
        }
        return dump.ToString();
    }

    private sealed class Dumper(bool layout)
    {
        private readonly StringBuilder _text = new();
        private readonly Dictionary<HNumbering, int> _numberings = [];

        public void Line(int depth, string text) => _text.Append(' ', depth * 2).Append(text).Append('\n');

        public override string ToString() => _text.ToString();

        public void Blocks(int depth, IEnumerable<HBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case HParagraph p:
                        Line(depth, $"P {Para(p.Format)} mark={Char(p.MarkFormat)} heading={p.HeadingLevel} list={List(p.List)} break={p.PageBreakBefore}/{p.ColumnBreakBefore} columns={p.ColumnsChange}");
                        Inlines(depth + 1, p.Inlines);
                        break;
                    case HTable t:
                        Line(depth, $"TABLE count={t.ColumnCount} widths={Join(t.ColumnWidths)} relative={Join(t.RelativeWidths)} align={t.Align} indent={t.Indent} margin={t.CellMargin} borders={Borders(t.Borders)}");
                        foreach (var row in t.Rows)
                        {
                            Line(depth + 1, $"ROW height={(layout ? row.Height : null)} header={row.Header}");
                            foreach (var cell in row.Cells)
                            {
                                Line(depth + 2, $"CELL col={cell.Column} span={cell.RowSpan}x{cell.ColSpan} valign={cell.VerticalAlign} fill={cell.Fill} borders={Borders(cell.Borders)}");
                                Blocks(depth + 3, cell.Blocks);
                            }
                        }
                        if (t.Caption.Count > 0)
                        {
                            Line(depth + 1, "CAPTION");
                            Blocks(depth + 2, t.Caption);
                        }
                        break;
                }
            }
        }

        private void Inlines(int depth, IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case HText text:
                        Line(depth, $"T \"{text.Text}\" {Char(text.Format)}");
                        break;
                    case HTab tab:
                        Line(depth, $"TAB {Char(tab.Format)}");
                        break;
                    case HLineBreak lineBreak:
                        Line(depth, $"BR {Char(lineBreak.Format)}");
                        break;
                    case HLink link:
                        Line(depth, $"LINK {link.Target}");
                        Inlines(depth + 1, link.Content);
                        break;
                    case HBookmark bookmark:
                        Line(depth, $"BOOKMARK {bookmark.Name}");
                        break;
                    case HNote note:
                        Line(depth, $"NOTE endnote={note.Endnote}");
                        Blocks(depth + 1, note.Blocks);
                        break;
                    case HField field:
                        Line(depth, $"FIELD {field.Kind} {Char(field.Format)}");
                        break;
                    case HImage image:
                        Line(depth, $"IMAGE {Hash(image.Path)} {image.Width}x{image.Height} anchor={image.Anchor}");
                        break;
                    case HTextBox box:
                        Line(depth, $"TEXTBOX {box.Width}x{box.Height} anchor={box.Anchor} fill={box.Fill} gradient={Gradient(box.Gradient)} line={Border(box.Line)} shape={box.Shape} ratio={box.CornerRatio} padding={box.Padding} valign={box.VerticalAlign}");
                        Blocks(depth + 1, box.Blocks);
                        break;
                    case HShape shape:
                        Line(depth, $"SHAPE {shape.Kind} {shape.Width}x{shape.Height} anchor={shape.Anchor} fill={shape.Fill} gradient={Gradient(shape.Gradient)} line={Border(shape.Line)} ratio={shape.CornerRatio} flip={shape.FlipHorizontal}/{shape.FlipVertical}");
                        break;
                }
            }
        }

        private string List(HListRef? list)
        {
            if (list is null)
                return "none";
            if (!_numberings.TryGetValue(list.Numbering, out var id))
            {
                id = _numberings[list.Numbering] = _numberings.Count + 1;
                return $"#{id}[{string.Join("|", list.Numbering.Levels.Select(l => $"{l.Format},{l.Text},{l.Start},{l.Bullet}"))}] L{list.Level} {list.Numbered}";
            }
            return $"#{id} L{list.Level} {list.Numbered}";
        }
    }

    private static string Para(HParaFormat f) =>
        $"[{f.Align} {f.Left}/{f.Right}/{f.FirstLine} {f.Before}/{f.After} {f.LineSpacing} keep={f.KeepWithNext}/{f.KeepLines}/{f.WidowOrphan} tabs={(f.Tabs is null ? "-" : string.Join(";", f.Tabs))}]";

    private static string Char(HCharFormat f) => f.ToString().Replace("HCharFormat ", "", StringComparison.Ordinal);

    private static string Borders(HBorders? b) => b is { } x ? $"{Border(x.Left)} {Border(x.Right)} {Border(x.Top)} {Border(x.Bottom)}" : "-";

    /// <summary>Widths as 한글 stores them (snapped), so equal borders compare equal after a round trip.</summary>
    private static string Border(HBorder b) =>
        b.Style == HBorderStyle.None ? "none" : $"{b.Style}:{b.WidthMm.ToString("0.###", CultureInfo.InvariantCulture)}:{b.Color}";

    private static string Gradient(HGradient? g) => g is null ? "-" : $"{string.Join(",", g.Colors)}@{g.Angle}";

    private static string Join<T>(IEnumerable<T>? values) => values is null ? "-" : string.Join(",", values);

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12] : "missing";

    // ───────────────────────── Tolerant comparison ─────────────────────────

    /// <summary>
    /// Compares a source model with the model read back from the HWPX written from it. Format members the source
    /// leaves open (null = "the 한글 style decides") are not compared; values the writer derives (layout caches such as
    /// row heights, relative column widths, snapped line widths, the empty paragraph that carries a section's setup
    /// before a table) are compared the way the writer derives them.
    /// </summary>
    public static void Similar(HDocument expected, HDocument actual)
    {
        var differences = new List<string>();
        new Comparer(differences).Document(expected, actual);
        Assert.True(differences.Count == 0, "Documents differ:\n" + string.Join("\n", differences.Take(40)));
    }

    private sealed class Comparer(List<string> differences)
    {
        private readonly Stack<string> _path = new();

        private void Differ(string message) => differences.Add($"{string.Join("/", _path.Reverse())}: {message}");

        private void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                Differ($"{what}: expected {expected}, got {actual}");
        }

        /// <summary>Compared only when the source sets it.</summary>
        private void Set<T>(T? expected, T? actual, string what) where T : struct
        {
            if (expected is { } value && !Nullable.Equals(value, actual))
                Differ($"{what}: expected {value}, got {actual}");
        }

        private void Set(string? expected, string? actual, string what)
        {
            if (expected is not null && expected != actual)
                Differ($"{what}: expected {expected}, got {actual}");
        }

        private void In(string name, Action action)
        {
            _path.Push(name);
            try
            {
                action();
            }
            finally
            {
                _path.Pop();
            }
        }

        public void Document(HDocument expected, HDocument actual)
        {
            Set(expected.Title, actual.Title, "title");
            var sections = expected.Sections.Count == 0 ? [new HSection()] : expected.Sections;
            Equal(sections.Count, actual.Sections.Count, "sections");
            for (var i = 0; i < Math.Min(sections.Count, actual.Sections.Count); i++)
                In($"section{i}", () => Section(sections[i], actual.Sections[i]));
        }

        private void Section(HSection expected, HSection actual)
        {
            if (expected.Page is { } page)
                Equal(page, actual.Page, "page");
            Equal(expected.Columns, actual.Columns, "columns");
            Equal(expected.HideFirstHeader, actual.HideFirstHeader, "hideFirstHeader");
            Equal(expected.HideFirstFooter, actual.HideFirstFooter, "hideFirstFooter");
            Set(expected.StartPageNumber, actual.StartPageNumber, "start page");
            HeadersFooters("header", expected.Headers, actual.Headers);
            HeadersFooters("footer", expected.Footers, actual.Footers);

            // The writer puts the section setup into an empty paragraph when the section starts with a table.
            var blocks = expected.Blocks.Count == 0 || expected.Blocks[0] is not HParagraph ? expected.Blocks.Prepend(new HParagraph()).ToList() : expected.Blocks;
            Blocks(blocks, actual.Blocks, firstIsSetup: true);
        }

        private void HeadersFooters(string kind, List<HHeaderFooter> expected, List<HHeaderFooter> actual)
        {
            Equal(expected.Count, actual.Count, kind + "s");
            for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            {
                In($"{kind}{i}", () =>
                {
                    Equal(expected[i].Pages, actual[i].Pages, "pages");
                    Blocks(expected[i].Blocks, actual[i].Blocks);
                });
            }
        }

        private void Blocks(IReadOnlyList<HBlock> expected, IReadOnlyList<HBlock> actual, bool firstIsSetup = false)
        {
            // Table captions are written as paragraphs after the table.
            var flat = expected.SelectMany(b => b is HTable t ? t.Caption.Prepend(b) : [b]).ToList();
            Equal(flat.Count, actual.Count, "blocks");
            for (var i = 0; i < Math.Min(flat.Count, actual.Count); i++)
            {
                var (e, a) = (flat[i], actual[i]);
                var setup = firstIsSetup && i == 0;
                In($"{(e is HTable ? "table" : "p")}{i}", () =>
                {
                    switch (e)
                    {
                        case HParagraph p when a is HParagraph q:
                            Paragraph(p, q, setup);
                            break;
                        case HTable t when a is HTable u:
                            Table(t, u);
                            break;
                        default:
                            Differ($"expected {e.GetType().Name}, got {a.GetType().Name}");
                            break;
                    }
                });
            }
        }

        private void Paragraph(HParagraph expected, HParagraph actual, bool setup)
        {
            Equal(expected.HeadingLevel, actual.HeadingLevel, "heading");
            Equal(expected.PageBreakBefore, actual.PageBreakBefore, "page break");
            Equal(expected.ColumnBreakBefore, actual.ColumnBreakBefore, "column break");
            Equal(expected.ColumnsChange, actual.ColumnsChange, "columns change");
            List(expected.List, actual.List);
            ParaFormat(expected.Format, actual.Format, expected.List is not null);
            // The mark only shows in empty paragraphs, and not in the one that carries the section setup.
            if (expected.Inlines.Count == 0 && !setup)
                CharFormat(expected.MarkFormat, actual.MarkFormat, "mark");
            Inlines(expected.Inlines, actual.Inlines);
        }

        private void List(HListRef? expected, HListRef? actual)
        {
            // Continuation paragraphs of a list item are written without numbering.
            if (expected is null || !expected.Numbered)
            {
                if (actual is not null)
                    Differ("unexpected list");
                return;
            }
            if (actual is null)
            {
                Differ("list missing");
                return;
            }
            Equal(expected.Level, actual.Level, "list level");
            Equal(expected.Numbered, actual.Numbered, "numbered");
            var level = expected.Numbering.Levels[expected.Level];
            if (actual.Level < actual.Numbering.Levels.Count)
                Equal(level, actual.Numbering.Levels[actual.Level], "list level definition");
        }

        private void ParaFormat(HParaFormat e, HParaFormat a, bool inList)
        {
            Set(e.Align, a.Align, "align");
            if (!inList || e.Left is not null)
            {
                Set(e.Left, a.Left, "left");
                Set(e.FirstLine, a.FirstLine, "first line");
            }
            Set(e.Right, a.Right, "right");
            Set(e.Before, a.Before, "before");
            Set(e.After, a.After, "after");
            Set(e.LineSpacing, a.LineSpacing, "line spacing");
            Set(e.KeepWithNext, a.KeepWithNext, "keep with next");
            Set(e.KeepLines, a.KeepLines, "keep lines");
            Set(e.WidowOrphan, a.WidowOrphan, "widow/orphan");
            if (e.Tabs is { Count: > 0 } tabs)
                Equal(string.Join(";", tabs), string.Join(";", a.Tabs ?? []), "tabs");
        }

        private void CharFormat(HCharFormat e, HCharFormat a, string what)
        {
            Set(e.Bold, a.Bold, what + " bold");
            Set(e.Italic, a.Italic, what + " italic");
            Set(e.Underline, a.Underline, what + " underline");
            Set(e.Strike, a.Strike, what + " strike");
            Set(e.Superscript, a.Superscript, what + " superscript");
            Set(e.Subscript, a.Subscript, what + " subscript");
            Set(e.Size, a.Size, what + " size");
            Set(e.Font ?? e.EastAsianFont, a.Font, what + " font");
            Set(e.EastAsianFont ?? e.Font, a.EastAsianFont, what + " East Asian font");
            Set(e.Color, a.Color, what + " color");
            Set(e.Shade, a.Shade, what + " shade");
            Set(e.Spacing, a.Spacing, what + " spacing");
        }

        private void Inlines(List<HInline> expected, List<HInline> actual)
        {
            var e = Normalize(expected);
            var a = Normalize(actual);
            Equal(string.Join(" ", e.Select(Kind)), string.Join(" ", a.Select(Kind)), "inlines");
            if (e.Count != a.Count)
                return;
            for (var i = 0; i < e.Count; i++)
            {
                var (x, y) = (e[i], a[i]);
                In(Kind(x), () => Inline(x, y));
            }
        }

        private void Inline(HInline expected, HInline actual)
        {
            switch ((expected, actual))
            {
                case (HText x, HText y):
                    Equal(x.Text, y.Text, "text");
                    CharFormat(x.Format, y.Format, "text");
                    break;
                case (HTab x, HTab y):
                    CharFormat(x.Format, y.Format, "tab");
                    break;
                case (HLink x, HLink y):
                    Equal(x.Target, y.Target, "target");
                    Inlines(x.Content, y.Content);
                    break;
                case (HBookmark x, HBookmark y):
                    Equal(x.Name, y.Name, "bookmark");
                    break;
                case (HNote x, HNote y):
                    Equal(x.Endnote, y.Endnote, "endnote");
                    Blocks(x.Blocks, y.Blocks);
                    break;
                case (HField x, HField y):
                    Equal(x.Kind, y.Kind, "field");
                    break;
                case (HImage x, HImage y):
                    Equal(Hash(x.Path), Hash(y.Path), "picture content");
                    Set(x.Width, y.Width, "width");
                    Set(x.Height, y.Height, "height");
                    Anchor(x.Anchor, y.Anchor);
                    break;
                case (HTextBox x, HTextBox y):
                    Equal((x.Width, x.Height), (y.Width, y.Height), "size");
                    Anchor(x.Anchor, y.Anchor);
                    Equal(x.Fill, y.Fill, "fill");
                    Equal(Gradient(x.Gradient), Gradient(y.Gradient), "gradient");
                    Line(x.Line, y.Line);
                    Equal(x.Shape, y.Shape, "shape");
                    Equal(x.CornerRatio, y.CornerRatio, "corner");
                    Equal(x.Padding, y.Padding, "padding");
                    Equal(x.VerticalAlign, y.VerticalAlign, "vertical align");
                    Blocks(x.Blocks, y.Blocks);
                    break;
                case (HShape x, HShape y):
                    Equal(x.Kind, y.Kind, "kind");
                    // The writer gives lines at least one unit of height or width; the reader takes it back.
                    Equal((Math.Max(1, x.Width), Math.Max(1, x.Height)), (Math.Max(1, y.Width), Math.Max(1, y.Height)), "size");
                    Anchor(x.Anchor, y.Anchor);
                    Equal(x.Fill, y.Fill, "fill");
                    Equal(Gradient(x.Gradient), Gradient(y.Gradient), "gradient");
                    Line(x.Line, y.Line);
                    Equal(x.CornerRatio, y.CornerRatio, "corner");
                    Equal((x.FlipHorizontal, x.FlipVertical), (y.FlipHorizontal, y.FlipVertical), "flip");
                    break;
            }
        }

        private void Anchor(HAnchor? expected, HAnchor? actual)
        {
            if (expected is null || actual is null)
            {
                Equal(expected is null, actual is null, "floating");
                return;
            }
            // Offsets into the margin are re-anchored by the writer (see HwpxWriter.NonNegative).
            if (expected.HorizontalOffset >= 0 && expected.VerticalOffset >= 0)
                Equal(expected, actual, "anchor");
            else
                Equal(expected.Wrap, actual.Wrap, "wrap");
        }

        private void Line(HBorder expected, HBorder actual)
        {
            Equal(expected.Style, actual.Style, "line style");
            if (expected.Style == HBorderStyle.None)
                return;
            Equal(expected.Color, actual.Color, "line color");
            if (Math.Abs(expected.WidthMm - actual.WidthMm) > 0.01)
                Differ($"line width: expected {expected.WidthMm}, got {actual.WidthMm}");
        }

        private void Table(HTable expected, HTable actual)
        {
            Equal(expected.Rows.Count, actual.Rows.Count, "rows");
            Equal(expected.Align, actual.Align, "align");
            if (expected.CellMargin is { } margin)
                Equal(margin, actual.CellMargin, "cell margin");
            // Tables wider than their place are scaled down by the writer, keeping the proportions.
            if (expected.ColumnWidths is { } widths && expected.ColumnCount == widths.Length &&
                (actual.ColumnWidths is not { } got || got.Length != widths.Length || got.Sum() > widths.Sum() ||
                 widths.Zip(got).Any(w => Math.Abs(w.First * (double)got.Sum() / widths.Sum() - w.Second) > 2)))
                Differ($"column widths: expected {string.Join(",", widths)}, got {string.Join(",", actual.ColumnWidths ?? [])}");
            for (var r = 0; r < Math.Min(expected.Rows.Count, actual.Rows.Count); r++)
            {
                var (er, ar) = (expected.Rows[r], actual.Rows[r]);
                In($"row{r}", () =>
                {
                    Equal(er.Header, ar.Header, "header");
                    // Row heights are layout caches the writer estimates; they only grow.
                    if (er.Height is { } height && !(ar.Height >= height))
                        Differ($"row height: expected at least {height}, got {ar.Height}");
                    Equal(er.Cells.Count, ar.Cells.Count, "cells");
                    for (var c = 0; c < Math.Min(er.Cells.Count, ar.Cells.Count); c++)
                    {
                        var (ec, ac) = (er.Cells[c], ar.Cells[c]);
                        In($"cell{c}", () =>
                        {
                            if (ec.Column >= 0)
                                Equal(ec.Column, ac.Column, "column");
                            Equal((ec.RowSpan, ec.ColSpan), (ac.RowSpan, ac.ColSpan), "span");
                            Equal(ec.VerticalAlign, ac.VerticalAlign, "vertical align");
                            Equal(ec.Fill, ac.Fill, "fill");
                            Equal(SnappedBorders(ec.Borders ?? expected.Borders ?? HBorders.Grid), SnappedBorders(ac.Borders), "borders");
                            Blocks(ec.Blocks.Count == 0 ? [new HParagraph()] : ec.Blocks, ac.Blocks);
                        });
                    }
                });
            }
        }

        private static string SnappedBorders(HBorders? borders) => borders is { } b
            ? string.Join(" ", new[] { b.Left, b.Right, b.Top, b.Bottom }.Select(x => x.Style == HBorderStyle.None ? "none" : $"{x.Style}:{HwpxWriter.LineWidth(x.WidthMm)}:{x.Color}"))
            : "-";

        private static string Kind(HInline inline) => inline switch
        {
            HText => "T",
            HTab => "TAB",
            HLineBreak => "BR",
            HLink => "LINK",
            HBookmark => "BM",
            HNote => "NOTE",
            HField f => f.Kind.ToString(),
            HImage => "IMG",
            HTextBox => "BOX",
            HShape s => s.Kind.ToString(),
            _ => inline.GetType().Name,
        };

        /// <summary>Adjacent texts with the same format are one run in 한글; empty texts disappear.</summary>
        private static List<HInline> Normalize(List<HInline> inlines)
        {
            var result = new List<HInline>();
            foreach (var inline in inlines)
            {
                if (inline is HText { Text.Length: 0 })
                    continue;
                if (inline is HText text && result.Count > 0 && result[^1] is HText last && last.Format == text.Format)
                    result[^1] = new HText(last.Text + text.Text, text.Format);
                else
                    result.Add(inline);
            }
            return result;
        }
    }
}
