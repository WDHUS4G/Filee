// In-memory spreadsheet between the readers (XLSX, XLS, ODS, CSV/TSV) and the writers (XLSX, ODS, CSV/TSV, HWPX
// tables). Cells hold the text as the spreadsheet shows it (numbers and dates already formatted) plus, for numbers,
// the value and its number format, so spreadsheet → spreadsheet conversions keep real numbers.

namespace Filee.Engines.Office.Sheets;

internal sealed class Workbook
{
    public List<Worksheet> Sheets { get; } = [];
}

internal sealed class Worksheet(string name)
{
    /// <summary>Excel's default column width, in characters of the default font.</summary>
    public const double StandardColumnWidth = 8.43;

    public string Name { get; } = name;

    /// <summary>Cells by 0-based row, then 0-based column. Only cells with a value or visible formatting.</summary>
    public SortedDictionary<int, SortedDictionary<int, SheetCell>> Rows { get; } = [];

    /// <summary>Column widths in characters, for columns that set one.</summary>
    public Dictionary<int, double> ColumnWidths { get; } = [];

    public double DefaultColumnWidth { get; set; } = StandardColumnWidth;

    /// <summary>Row heights in points, for rows with a custom height.</summary>
    public Dictionary<int, double> RowHeights { get; } = [];

    public HashSet<int> HiddenRows { get; } = [];
    public HashSet<int> HiddenColumns { get; } = [];
    public List<CellRange> Merges { get; } = [];

    /// <summary>Rows frozen at the top (usually headers): repeated on every printed page.</summary>
    public int FrozenRows { get; set; }

    /// <summary>
    /// 0-based rows to repeat at the top of every printed page (Page Layout → Print Titles), or null. They win over
    /// <see cref="FrozenRows"/> and need not start at the top.
    /// </summary>
    public (int First, int Last)? TitleRows { get; set; }

    /// <summary>0-based rows that start a new printed page (page breaks set in Excel: Page Layout → Breaks).</summary>
    public SortedSet<int> RowBreaks { get; } = [];

    public void Set(int row, int column, SheetCell cell)
    {
        if (!Rows.TryGetValue(row, out var cells))
            Rows[row] = cells = [];
        cells[column] = cell;
    }

    public SheetCell? Get(int row, int column) =>
        Rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var cell) ? cell : null;

    public double ColumnWidth(int column) => ColumnWidths.TryGetValue(column, out var width) ? width : DefaultColumnWidth;

    /// <summary>
    /// The smallest range holding every cell with text, fill or borders (merged areas included); null for an empty
    /// sheet.
    /// </summary>
    public CellRange? UsedRange()
    {
        int top = int.MaxValue, left = int.MaxValue, bottom = -1, right = -1;
        foreach (var (row, cells) in Rows)
        {
            foreach (var (column, cell) in cells)
            {
                if (!cell.HasContent)
                    continue;
                top = Math.Min(top, row);
                bottom = Math.Max(bottom, row);
                left = Math.Min(left, column);
                right = Math.Max(right, column);
            }
        }
        if (bottom < 0)
            return null;
        foreach (var merge in Merges.Where(m => m.Top <= bottom && m.Left <= right && m.Top >= top && m.Left >= left))
        {
            bottom = Math.Max(bottom, merge.Bottom);
            right = Math.Max(right, merge.Right);
        }
        return new CellRange(top, left, bottom, right);
    }
}

/// <summary>An inclusive, 0-based range of cells.</summary>
internal readonly record struct CellRange(int Top, int Left, int Bottom, int Right)
{
    public bool Contains(int row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;

    /// <summary>Parses "B2" or "A1:C3".</summary>
    public static CellRange? Parse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var parts = reference.Replace("$", "", StringComparison.Ordinal).Split(':');
        if (!TryParseCell(parts[0], out var r1, out var c1))
            return null;
        if (parts.Length == 1)
            return new CellRange(r1, c1, r1, c1);
        return TryParseCell(parts[1], out var r2, out var c2)
            ? new CellRange(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2))
            : null;
    }

    /// <summary>"AB12" → row 11, column 27.</summary>
    public static bool TryParseCell(string reference, out int row, out int column)
    {
        row = column = -1;
        var i = 0;
        var col = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
            col = col * 26 + (char.ToUpperInvariant(reference[i++]) - 'A' + 1);
        if (i == 0 || i == reference.Length || !int.TryParse(reference.AsSpan(i), out var r) || r < 1)
            return false;
        row = r - 1;
        column = col - 1;
        return true;
    }
}

internal enum CellAlign
{
    General,
    Left,
    Center,
    Right,
}

internal enum CellVerticalAlign
{
    Bottom,
    Center,
    Top,
}

/// <summary>One border side: Excel style name ("thin", "medium", "dashed", ...) and colour.</summary>
internal readonly record struct CellBorder(string Style, string? Color)
{
    public static readonly CellBorder None = new("none", null);
    public bool IsVisible => Style is not ("none" or "");
}

internal sealed record CellStyle
{
    public static readonly CellStyle Default = new();

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }

    /// <summary>Font size in points; null = the workbook default.</summary>
    public double? FontSize { get; init; }

    public string? Color { get; init; }
    public string? Fill { get; init; }
    public CellAlign Align { get; init; }
    public CellVerticalAlign VerticalAlign { get; init; }
    public bool Wrap { get; init; }
    public CellBorder Left { get; init; } = CellBorder.None;
    public CellBorder Right { get; init; } = CellBorder.None;
    public CellBorder Top { get; init; } = CellBorder.None;
    public CellBorder Bottom { get; init; } = CellBorder.None;

    public bool HasBorders => Left.IsVisible || Right.IsVisible || Top.IsVisible || Bottom.IsVisible;
}

/// <summary>A cell as displayed: formatted text plus style.</summary>
/// <param name="Text">What the cell shows ("1,234.50", "2026-10-01", "TRUE").</param>
/// <param name="IsNumber">Numbers (and dates) align right under the General alignment.</param>
internal sealed record SheetCell(string Text, CellStyle Style, bool IsNumber = false)
{
    /// <summary>
    /// The value behind <see cref="Text"/>: numbers, dates and times as Excel serial days (1900 date system) and
    /// booleans as 1 / 0; null for text and errors. Writers store it instead of the text.
    /// </summary>
    public double? Value { get; init; }

    /// <summary>Excel number format code of <see cref="Value"/> ("#,##0", "yyyy-mm-dd"); null = General.</summary>
    public string? NumberFormat { get; init; }

    /// <summary>A TRUE / FALSE cell (<see cref="Value"/> is 1 or 0).</summary>
    public bool IsBoolean { get; init; }

    public bool HasContent => Text.Length > 0 || Style.Fill is not null || Style.HasBorders;
}
