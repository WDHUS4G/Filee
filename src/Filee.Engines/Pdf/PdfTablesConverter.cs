// PDF → XLSX (#267): the tables drawn with lines in a PDF, one sheet each. The tables are the ones PDF → DOCX finds
// (PdfDocumentReader's ruled tables, with merged cells, column widths and row heights); text outside them is left out.
//
// - A table that goes on at the top of the next page with the same columns joins the one before it on one sheet; a
//   header row repeated on the new page is left out.
// - Cells that read as numbers ("1,234.50", "(120)", "$9.99"), percentages ("12.5%") or dates with the year first
//   ("2026-10-23", "2026. 10. 23.") become real numbers and dates, in a number format that shows them as the PDF
//   did, so they can be calculated with. Anything else stays text: numbers with a leading zero (codes, phone numbers),
//   more than 15 digits, a decimal comma, or day-first dates, which can't be told from month-first ones.
// - A PDF without such a table (or a scan, which has no text) fails with a message instead of an empty workbook.
//   Tables aligned by white space only aren't found, as in PDF → DOCX.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Pdf;

/// <summary>PDF → XLSX: each table drawn with lines on its own sheet.</summary>
public sealed partial class PdfTablesConverter : IConverter
{
    /// <summary>Points per character of Excel's column width (Calibri 11: 7 px at 96 dpi).</summary>
    private const double PointsPerCharacter = 5.25;

    public string Id => "pdf-tables";
    public string DisplayName => "PDF tables (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Only from a PDF, and never a step on a longer route: an image turned into a PDF has no ruled tables, so
    /// jpg → pdf → xlsx would always fail (and show spreadsheet presets on pictures).
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [new("pdf", "xlsx", DirectOnly: true), new("pdf", "ods", DirectOnly: true), new("pdf", "csv", DirectOnly: true)];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → XLSX, ODS, CSV", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var document = PdfDocumentReader.Read(step.InputPath, Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}"));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7);
            var book = Build(document);
            if (book.Sheets.Count == 0)
                throw new ConversionRefusedException("error.pdf_no_tables");
            // As the spreadsheet engine writes: CSV gets one file per sheet.
            var outputs = SpreadsheetConverter.Write(book, step);
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    /// <summary>A table and the page it is on (0-based).</summary>
    private sealed record Placed(HTable Table, int Page)
    {
        /// <summary>Nothing (no table, no text) comes before it on its page.</summary>
        public bool FirstOnPage { get; set; }

        /// <summary>Nothing (no table, no text) comes after it on its page.</summary>
        public bool LastOnPage { get; set; }
    }

    /// <summary>The document's tables as sheets: one per table, a table going on over the next page joined.</summary>
    internal static Workbook Build(HDocument document)
    {
        // Everything on the pages in order: tables, and null for text (empty paragraphs, such as the one carrying a page
        // break, don't count), so a table only "goes on" when nothing at all sits between it and the next page's table.
        var items = new List<(Placed? Table, int Page)>();
        var page = -1;
        foreach (var section in document.Sections)
        {
            page++;
            foreach (var block in section.Blocks)
            {
                if (block is HParagraph paragraph)
                {
                    if (paragraph.PageBreakBefore)
                        page++;
                    if (paragraph.Inlines.OfType<HText>().Any(t => t.Text.Trim().Length > 0))
                        items.Add((null, page));
                }
                else if (block is HTable table && table.Rows.Count > 0)
                {
                    items.Add((new Placed(table, page), page));
                }
            }
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Table is not { } placed)
                continue;
            placed.FirstOnPage = i == 0 || items[i - 1].Page != placed.Page;
            placed.LastOnPage = i == items.Count - 1 || items[i + 1].Page != placed.Page;
        }
        var tables = items.Where(i => i.Table is not null).Select(i => i.Table!).ToList();

        var book = new Workbook();
        Worksheet? sheet = null;
        Placed? previous = null;
        string[]? header = null;
        var row = 0;
        foreach (var placed in tables)
        {
            var goesOn = previous is not null && sheet is not null && previous.LastOnPage && placed.FirstOnPage
                         && placed.Page == previous.Page + 1 && SameColumns(previous.Table, placed.Table);
            var skip = 0;
            if (goesOn && header is not null && RowTexts(placed.Table.Rows[0]).SequenceEqual(header))
                skip = 1;
            if (!goesOn)
            {
                sheet = new Worksheet($"Table {book.Sheets.Count + 1}");
                book.Sheets.Add(sheet);
                row = 0;
                header = RowTexts(placed.Table.Rows[0]);
                SetColumnWidths(sheet, placed.Table);
            }
            row = Place(sheet!, placed.Table, row, skip);
            previous = placed;
        }
        return book;
    }

    /// <summary>Same number of columns, each within 5 % of the other table's width (or no widths to compare).</summary>
    private static bool SameColumns(HTable a, HTable b)
    {
        if (a.ColumnCount != b.ColumnCount)
            return false;
        if (a.ColumnWidths is not { } left || b.ColumnWidths is not { } right || left.Length != right.Length)
            return true;
        return left.Zip(right).All(w => Math.Abs(w.First - w.Second) <= 0.05 * Math.Max(w.First, w.Second) + 100);
    }

    private static void SetColumnWidths(Worksheet sheet, HTable table)
    {
        if (table.ColumnWidths is not { } widths)
            return;
        for (var column = 0; column < widths.Length; column++)
            sheet.ColumnWidths[column] = Math.Round(Math.Clamp(widths[column] / 100.0 / PointsPerCharacter, 2, 100), 2);
    }

    /// <summary>Writes the table's rows from <paramref name="top"/> on, leaving out its first <paramref name="skip"/>; returns the next free row.</summary>
    private static int Place(Worksheet sheet, HTable table, int top, int skip)
    {
        var taken = new HashSet<(int, int)>();
        var row = top;
        foreach (var source in table.Rows.Skip(skip))
        {
            if (source.Height is { } height && height > 0)
                sheet.RowHeights[row] = Math.Round(height / 100.0, 2);
            var column = 0;
            foreach (var cell in source.Cells)
            {
                if (cell.Column >= 0)
                    column = cell.Column;
                while (taken.Contains((row, column)))
                    column++;
                var style = StyleOf(cell);
                var text = Text(cell);
                sheet.Set(row, column, Shown(text, style));
                var rows = Math.Max(1, cell.RowSpan);
                var columns = Math.Max(1, cell.ColSpan);
                for (var r = row; r < row + rows; r++)
                    for (var c = column; c < column + columns; c++)
                    {
                        taken.Add((r, c));
                        // The rest of a merged area keeps the borders, as Excel draws them from every cell.
                        if (r != row || c != column)
                            sheet.Set(r, c, new SheetCell("", style));
                    }
                if (rows > 1 || columns > 1)
                    sheet.Merges.Add(new CellRange(row, column, row + rows - 1, column + columns - 1));
                column += columns;
            }
            row++;
        }
        return Math.Max(row, taken.Count == 0 ? row : taken.Max(t => t.Item1) + 1);
    }

    private static CellStyle StyleOf(HCell cell) => new()
    {
        Bold = cell.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().ToList() is { Count: > 0 } texts
               && texts.All(t => t.Format.Bold == true || t.Text.Trim().Length == 0),
        Fill = cell.Fill,
        Wrap = true,
        VerticalAlign = cell.VerticalAlign switch
        {
            HVerticalAlign.Center => CellVerticalAlign.Center,
            HVerticalAlign.Bottom => CellVerticalAlign.Bottom,
            _ => CellVerticalAlign.Top,
        },
        Left = Thin,
        Right = Thin,
        Top = Thin,
        Bottom = Thin,
    };

    private static readonly CellBorder Thin = new("thin", "#000000");

    private static string[] RowTexts(HRow row) => [.. row.Cells.Select(Text)];

    /// <summary>A cell's text: its paragraphs as lines.</summary>
    private static string Text(HCell cell)
    {
        var lines = new List<string>();
        foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
        {
            var line = new StringBuilder();
            foreach (var inline in paragraph.Inlines)
                line.Append(inline switch { HText text => text.Text, HTab => "\t", HLineBreak => "\n", _ => "" });
            lines.Add(line.ToString());
        }
        return string.Join('\n', lines).Trim();
    }

    // ───────────────────────── Numbers and dates ─────────────────────────

    /// <summary>A cell for text as the PDF shows it: a number or date with a matching format, or text.</summary>
    internal static SheetCell Shown(string text, CellStyle style)
    {
        if (Number(text) is { } number)
            return new SheetCell(text, style, IsNumber: true) { Value = number.Value, NumberFormat = number.Format };
        if (Date(text) is { } date)
            return new SheetCell(text, style, IsNumber: true) { Value = date.Value, NumberFormat = date.Format };
        return new SheetCell(text, style);
    }

    /// <summary>"1,234.50", "-7", "(120)", "$9.99", "12.5%" as a value and an Excel number format; null otherwise.</summary>
    internal static (double Value, string Format)? Number(string text)
    {
        if (NumberPattern().Match(text.Trim()) is not { Success: true } match)
            return null;
        var whole = match.Groups["whole"].Value;
        var fraction = match.Groups["fraction"].Value;
        var digits = whole.Replace(",", "") + fraction;
        // Codes and phone numbers keep their leading zero; Excel holds 15 significant digits.
        if ((whole.Length > 1 && whole[0] == '0') || digits.TrimStart('0').Length > 15)
            return null;
        var negative = match.Groups["minus"].Success || match.Groups["open"].Success;
        if (match.Groups["open"].Success != match.Groups["close"].Success)
            return null;
        var value = double.Parse(whole.Replace(",", "") + (fraction.Length > 0 ? "." + fraction : ""), CultureInfo.InvariantCulture);
        var format = (whole.Contains(',') ? "#,##0" : "0") + (fraction.Length > 0 ? "." + new string('0', fraction.Length) : "");
        if (match.Groups["percent"].Success)
        {
            value /= 100;
            format += "%";
        }
        if (match.Groups["currency"].Value is { Length: > 0 } currency)
            format = $"\"{currency}\"{format}";
        if (match.Groups["open"].Success)
            format = $"{format};({format})";
        return (negative ? -value : value, format);
    }

    /// <summary>"2026-10-23", "2026/1/5", "2026. 10. 23." as an Excel date serial and format; null otherwise.</summary>
    internal static (double Value, string Format)? Date(string text)
    {
        if (DatePattern().Match(text.Trim()) is not { Success: true } match)
            return null;
        var (year, month, day) = (match.Groups["y"].Value, match.Groups["m"].Value, match.Groups["d"].Value);
        if (!DateOnly.TryParseExact($"{year}-{month.PadLeft(2, '0')}-{day.PadLeft(2, '0')}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var separator = match.Groups["sep"].Value;
        var format = "yyyy" + separator + (month.Length == 2 ? "mm" : "m") + separator + (day.Length == 2 ? "dd" : "d") + match.Groups["end"].Value;
        return (date.DayNumber - new DateOnly(1899, 12, 30).DayNumber, format);
    }

    [GeneratedRegex(@"^(?<minus>-)?(?<open>\()?(?<currency>[$€£¥₩₹])?(?<whole>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<fraction>\d+))?(?<percent>%)?(?<close>\))?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(?<y>\d{4})(?<sep>-|/|\. ?)(?<m>\d{1,2})\k<sep>(?<d>\d{1,2})(?<end>\.?)$")]
    private static partial Regex DatePattern();
}
