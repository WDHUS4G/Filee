// Workbook → HDocument: every non-empty sheet becomes a section with one table, so XLSX / CSV → HWPX works directly
// and XLSX / CSV → PDF goes through the HWPX writer and rhwp, without Excel or LibreOffice.
//
// Layout choices, since a sheet has no page of its own: A4 with narrow margins, landscape when the columns are much
// wider than a portrait page, columns (and text) scaled down to fit the width like "Fit all columns on one page",
// light grey grid lines where cells have no border, the rows to repeat at the top (or frozen top rows) on every page.

using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Hwp.Hwpx;

internal static class SheetDocument
{
    /// <summary>The formats DocumentReaders reads into a document through <see cref="Build"/>.</summary>
    public static readonly IReadOnlySet<string> SourceFormats =
        new HashSet<string>(["xlsx", "xlsm", "xltx", "csv", "tsv", "xls", "xlt", "ods"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest table written; bigger sheets are cut with a note (a PDF of 100 000 rows helps nobody).</summary>
    private const int MaxRows = 5000;
    private const int MaxColumns = 60;

    private const int A4Short = 59528;
    private const int A4Long = 84188;
    private const int Margin = 2268;          // 8 mm
    private const double DefaultFontSize = 11; // Excel's default (Calibri / 맑은 고딕 11 pt)

    private static readonly HBorder GridLine = new(HBorderStyle.Solid, 0.1, "#D9D9D9");

    public static HDocument Build(Workbook book)
    {
        var document = new HDocument();
        var sheets = book.Sheets.Where(s => s.UsedRange() is not null).ToList();
        foreach (var sheet in sheets)
            document.Sections.Add(Section(sheet, withTitle: sheets.Count > 1));
        if (document.Sections.Count == 0)
        {
            var empty = new HSection();
            empty.Blocks.Add(new HParagraph());
            document.Sections.Add(empty);
        }
        return document;
    }

    private static HSection Section(Worksheet sheet, bool withTitle)
    {
        var range = sheet.UsedRange()!.Value;
        var rows = Enumerable.Range(range.Top, range.Bottom - range.Top + 1).Where(r => !sheet.HiddenRows.Contains(r)).ToList();
        var columns = Enumerable.Range(range.Left, range.Right - range.Left + 1).Where(c => !sheet.HiddenColumns.Contains(c)).ToList();
        var cutRows = rows.Count > MaxRows;
        var cutColumns = columns.Count > MaxColumns;
        rows = rows.Take(MaxRows).ToList();
        columns = columns.Take(MaxColumns).ToList();

        // Excel widths count digits of Calibri 11 (7 px each + 5 px padding). The 한글 fonts that render the table have
        // wider digits, so a character gets 8 px plus room for the cell margins; 75 HWPUNIT per pixel.
        var widths = columns.Select(c => (int)Math.Round((sheet.ColumnWidth(c) * 8 + 10) * 75)).ToArray();
        var total = Math.Max(1, widths.Sum());
        var landscape = total > A4Short - 2 * Margin;
        var page = landscape
            ? new HPage(A4Long, A4Short, Margin, Margin, Margin / 2, Margin / 2, Margin / 2, Margin / 2, 0)
            : new HPage(A4Short, A4Long, Margin, Margin, Margin / 2, Margin / 2, Margin / 2, Margin / 2, 0);
        var scale = Math.Min(1.0, (double)page.TextWidth / total);
        var fontScale = Math.Max(0.5, scale);

        var section = new HSection { Page = page };
        if (withTitle)
        {
            var title = new HParagraph { Format = new HParaFormat(After: 400), MarkFormat = new HCharFormat(Size: 1300) };
            title.Inlines.Add(new HText(sheet.Name, new HCharFormat(Bold: true, Size: 1300)));
            section.Blocks.Add(title);
        }

        // Page breaks set in Excel start a new table on a new page; the rows to repeat at the top (Print Titles, else
        // frozen header rows) repeat at its top. Rows above print titles (a document title) come once, before them.
        var titles = sheet.TitleRows ?? (sheet.FrozenRows is > 0 and <= 5 ? (0, sheet.FrozenRows - 1) : null);
        var headerRows = titles is { } t ? rows.Where(r => r >= t.First && r <= t.Last).ToHashSet() : [];
        var lead = titles is { } above ? rows.Where(r => r < above.First).ToList() : [];
        var header = rows.Where(headerRows.Contains).ToList();
        var body = rows.Where(r => !headerRows.Contains(r) && (titles is not { } u || r > u.Last)).ToList();
        if (lead.Count > 0)
            section.Blocks.Add(Table(sheet, lead, columns, [], fontScale, widths.Select(w => Math.Max(300, (int)(w * scale))).ToArray()));
        var pages = new List<List<int>> { new(header) };
        var previous = -1;
        foreach (var r in body)
        {
            // A break above a hidden row still counts: the next visible row starts the page.
            if (pages[^1].Count > header.Count && sheet.RowBreaks.GetViewBetween(previous + 1, r).Count > 0)
                pages.Add(new List<int>(header));
            pages[^1].Add(r);
            previous = r;
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var table = Table(sheet, pages[i], columns, headerRows, fontScale, widths.Select(w => Math.Max(300, (int)(w * scale))).ToArray());
            table.PageBreakBefore = i > 0;
            section.Blocks.Add(table);
        }

        if (cutRows || cutColumns)
        {
            var note = new HParagraph();
            note.Inlines.Add(new HText(
                $"… {sheet.Name}: {(cutRows ? $"first {MaxRows} rows" : "")}{(cutRows && cutColumns ? ", " : "")}{(cutColumns ? $"first {MaxColumns} columns" : "")} shown",
                new HCharFormat(Italic: true, Size: 900, Color: "#808080")));
            section.Blocks.Add(note);
        }
        return section;
    }

    /// <summary>One table of the given rows (one printed page or more), merged areas cut at its first and last row.</summary>
    private static HTable Table(Worksheet sheet, List<int> rows, List<int> columns, HashSet<int> headerRows, double fontScale, int[] widths)
    {
        var table = new HTable
        {
            ColumnCount = columns.Count,
            ColumnWidths = widths,
            CellMargin = new HInsets(141, 141, 57, 57),
            Borders = new HBorders(GridLine, GridLine, GridLine, GridLine),
        };
        var columnIndex = columns.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);

        // Merged areas: the top-left visible cell spans the visible rows and columns of the area; the rest are skipped.
        var anchors = new Dictionary<(int, int), (int RowSpan, int ColSpan)>();
        var covered = new HashSet<(int, int)>();
        foreach (var merge in sheet.Merges)
        {
            var mergeRows = rows.Where(r => r >= merge.Top && r <= merge.Bottom).ToList();
            var mergeColumns = columns.Where(c => c >= merge.Left && c <= merge.Right).ToList();
            if (mergeRows.Count == 0 || mergeColumns.Count == 0)
                continue;
            anchors[(mergeRows[0], mergeColumns[0])] = (mergeRows.Count, mergeColumns.Count);
            foreach (var r in mergeRows)
                foreach (var c in mergeColumns)
                    if (r != mergeRows[0] || c != mergeColumns[0])
                        covered.Add((r, c));
        }

        foreach (var r in rows)
        {
            var row = new HRow
            {
                Header = headerRows.Contains(r),
                Height = sheet.RowHeights.TryGetValue(r, out var points) ? (int)(points * 100 * fontScale) : null,
            };
            foreach (var c in columns)
            {
                if (covered.Contains((r, c)))
                    continue;
                var (rowSpan, colSpan) = anchors.GetValueOrDefault((r, c), (1, 1));
                row.Cells.Add(Cell(sheet, r, c, rowSpan, colSpan, columnIndex[c], fontScale));
            }
            table.Rows.Add(row);
        }
        return table;
    }

    private static HCell Cell(Worksheet sheet, int r, int c, int rowSpan, int colSpan, int column, double fontScale)
    {
        var value = sheet.Get(r, c);
        var style = value?.Style ?? CellStyle.Default;
        var size = (int)Math.Round((style.FontSize ?? DefaultFontSize) * 100 * fontScale);
        var format = new HCharFormat(
            Bold: style.Bold ? true : null,
            Italic: style.Italic ? true : null,
            Underline: style.Underline ? true : null,
            Strike: style.Strike ? true : null,
            Size: size,
            Color: style.Color is null or "#000000" ? null : style.Color);

        var align = style.Align switch
        {
            CellAlign.Center => HAlign.Center,
            CellAlign.Right => HAlign.Right,
            CellAlign.Left => HAlign.Left,
            _ => value?.IsNumber == true ? HAlign.Right : HAlign.Left,
        };
        var paragraph = new HParagraph { Format = new HParaFormat(Align: align), MarkFormat = new HCharFormat(Size: size) };
        var lines = (value?.Text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new HLineBreak(format));
            if (lines[i].Length > 0)
                paragraph.Inlines.Add(new HText(lines[i], format));
        }

        var cell = new HCell
        {
            Column = column,
            RowSpan = rowSpan,
            ColSpan = colSpan,
            Fill = style.Fill,
            VerticalAlign = style.VerticalAlign switch
            {
                CellVerticalAlign.Top => HVerticalAlign.Top,
                CellVerticalAlign.Center => HVerticalAlign.Center,
                _ => HVerticalAlign.Bottom,
            },
            // A border drawn by the neighbour counts too: in Excel both cells share the line.
            Borders = new HBorders(
                Border(style.Left, sheet.Get(r, c - 1)?.Style.Right),
                Border(style.Right, sheet.Get(r, c + colSpan)?.Style.Left),
                Border(style.Top, sheet.Get(r - 1, c)?.Style.Bottom),
                Border(style.Bottom, sheet.Get(r + rowSpan, c)?.Style.Top)),
        };
        cell.Blocks.Add(paragraph);
        return cell;
    }

    private static HBorder Border(CellBorder own, CellBorder? neighbour)
    {
        var side = own.IsVisible ? own : neighbour is { IsVisible: true } other ? other : CellBorder.None;
        if (!side.IsVisible)
            return GridLine;
        var color = side.Color ?? "#000000";
        return side.Style switch
        {
            "hair" => new HBorder(HBorderStyle.Solid, 0.1, color),
            "medium" => new HBorder(HBorderStyle.Solid, 0.4, color),
            "thick" => new HBorder(HBorderStyle.Solid, 0.7, color),
            "double" => new HBorder(HBorderStyle.Double, 0.5, color),
            "dotted" => new HBorder(HBorderStyle.Dot, 0.12, color),
            "dashed" or "mediumDashed" => new HBorder(HBorderStyle.Dash, side.Style == "dashed" ? 0.12 : 0.4, color),
            "dashDot" or "dashDotDot" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => new HBorder(HBorderStyle.DashDot, 0.12, color),
            _ => new HBorder(HBorderStyle.Solid, 0.12, color),
        };
    }
}
