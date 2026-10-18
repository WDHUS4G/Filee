// XLS (Excel 97–2003, BIFF8, also .xlt and the older BIFF2–5) → Workbook without Excel or LibreOffice: the binary
// records are read by ExcelDataReader (MIT), values are formatted with the same number formats as XLSX
// (NumberFormatter), plus merged cells, column widths, row heights, hidden rows / columns / sheets and alignment.
// ExcelDataReader exposes no fonts, fills or borders, so XLS cells keep the default look; widths and heights come
// from the BIFF records themselves (XlsLayout).

using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;

namespace Filee.Engines.Office.Sheets;

internal static class XlsReader
{
    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        // Web apps often save XLSX with an .xls name; the XLSX reader keeps more of such files (fonts, fills).
        if (StartsWith(path, "PK"u8))
            return XlsxReader.Read(path);

        // BIFF5 and older store text in the workbook's ANSI code page (cp1252, cp949, ...).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var formatter = new NumberFormatter(); // ExcelDataReader already turns 1904-based dates into DateTime
            var layouts = XlsLayout.Read(path);
            var book = new Workbook();
            var index = 0;
            do
            {
                if (reader.VisibleState is null or "visible")
                    book.Sheets.Add(ReadSheet(reader, formatter, layouts?.ElementAtOrDefault(index)));
                index++;
            }
            while (reader.NextResult());
            return book;
        }
        catch (InvalidPasswordException)
        {
            throw new InvalidDataException("The workbook is protected with a password (encrypted). Remove the password in Excel and convert it again.");
        }
        catch (HeaderException)
        {
            throw new InvalidDataException(StartsWith(path, "<"u8) || StartsWith(path, [0xEF, 0xBB, 0xBF, (byte)'<'])
                ? "The file is not an Excel 97–2003 workbook but a web page or XML file saved with an .xls name. Open it in Excel and save it as XLSX."
                : "The file is not an Excel 97–2003 workbook.");
        }
        catch (Exception ex) when (ex is ExcelReaderException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidDataException("The Excel 97–2003 workbook could not be read: " + ex.Message, ex);
        }
    }

    /// <param name="layout">Widths and heights from the BIFF records; null to use what ExcelDataReader reports.</param>
    private static Worksheet ReadSheet(IExcelDataReader reader, NumberFormatter formatter, XlsLayout.Sheet? layout)
    {
        var sheet = new Worksheet(string.IsNullOrEmpty(reader.Name) ? "Sheet" : reader.Name);
        var heights = new Dictionary<int, double>();
        var row = -1;
        while (reader.Read())
        {
            row++;
            heights[row] = reader.RowHeight;
            for (var c = 0; c < reader.FieldCount; c++)
            {
                if (Cell(reader, c, formatter) is { HasContent: true } cell)
                    sheet.Set(row, c, cell);
            }
        }

        if (layout is not null)
        {
            foreach (var (c, (width, hidden)) in layout.Columns)
            {
                if (hidden)
                    sheet.HiddenColumns.Add(c);
                else
                    sheet.ColumnWidths[c] = width;
            }
            foreach (var (r, (points, custom, hidden)) in layout.Rows)
            {
                if (hidden)
                    sheet.HiddenRows.Add(r);
                else if (custom)
                    sheet.RowHeights[r] = points;
            }
        }
        else
        {
            // ExcelDataReader reports hidden rows with height 0 and every other row's height, custom or not: keep
            // the heights unlike the sheet's usual row height.
            var usual = heights.Values.Where(h => h > 0).GroupBy(h => h).MaxBy(g => g.Count())?.Key;
            foreach (var (r, height) in heights)
            {
                if (height <= 0)
                    sheet.HiddenRows.Add(r);
                else if (height != usual)
                    sheet.RowHeights[r] = height;
            }
            // Hidden columns report width 0; columns without a width report Excel's standard width.
            for (var c = 0; c < reader.FieldCount; c++)
            {
                var width = reader.GetColumnWidth(c);
                if (width <= 0)
                    sheet.HiddenColumns.Add(c);
                else if (Math.Abs(width - Worksheet.StandardColumnWidth) > 0.005)
                    sheet.ColumnWidths[c] = width;
            }
        }

        foreach (var merge in reader.MergeCells ?? [])
        {
            if (merge.ToRow > merge.FromRow || merge.ToColumn > merge.FromColumn)
                sheet.Merges.Add(new CellRange(merge.FromRow, merge.FromColumn, merge.ToRow, merge.ToColumn));
        }
        return sheet;
    }

    private static SheetCell? Cell(IExcelDataReader reader, int column, NumberFormatter formatter)
    {
        var value = reader.GetValue(column);
        var style = Style(reader.GetCellStyle(column));
        if (reader.GetCellError(column) is { } error)
            return new SheetCell(ErrorText(error), style);

        double number;
        switch (value)
        {
            case null:
                return null; // blank cells: their fills and borders are not available
            case string text:
                return new SheetCell(text, style);
            case bool on:
                return new SheetCell(on ? "TRUE" : "FALSE", style) { Value = on ? 1 : 0, IsBoolean = true };
            case DateTime date:
                number = NumberFormatter.ToSerial(date);
                break;
            case TimeSpan duration:
                number = duration.TotalDays;
                break;
            default:
                number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        // Built-in formats the XLSX reader knows (dates in ISO order) look the same from XLS; ExcelDataReader's
        // string is used for the workbook's own formats and localized built-ins.
        var index = reader.GetNumberFormatIndex(column);
        var format = NumberFormatter.BuiltInFormats.TryGetValue(index, out var builtIn) ? builtIn : reader.GetNumberFormatString(column);
        return new SheetCell(formatter.Format(number, format), style, IsNumber: true)
        {
            Value = number,
            NumberFormat = NumberFormatter.IsGeneral(format) ? null : format,
        };
    }

    private static CellStyle Style(ExcelDataReader.CellStyle? style)
    {
        if (style is null)
            return CellStyle.Default;
        var align = style.HorizontalAlignment switch
        {
            HorizontalAlignment.Left or HorizontalAlignment.Filled or HorizontalAlignment.Justified or HorizontalAlignment.Distributed => CellAlign.Left,
            HorizontalAlignment.Center or HorizontalAlignment.Centered or HorizontalAlignment.CenteredAcrossSelection => CellAlign.Center,
            HorizontalAlignment.Right => CellAlign.Right,
            _ => CellAlign.General,
        };
        var vertical = style.VerticalAlignment switch
        {
            VerticalAlignment.Top => CellVerticalAlign.Top,
            VerticalAlignment.Center or VerticalAlignment.Justify or VerticalAlignment.Distributed => CellVerticalAlign.Center,
            _ => CellVerticalAlign.Bottom,
        };
        return align == CellAlign.General && vertical == CellVerticalAlign.Bottom
            ? CellStyle.Default
            : CellStyle.Default with { Align = align, VerticalAlign = vertical };
    }

    private static string ErrorText(CellError error) => error switch
    {
        CellError.NULL => "#NULL!",
        CellError.DIV0 => "#DIV/0!",
        CellError.VALUE => "#VALUE!",
        CellError.REF => "#REF!",
        CellError.NAME => "#NAME?",
        CellError.NUM => "#NUM!",
        CellError.NA => "#N/A",
        _ => "#GETTING_DATA",
    };

    private static bool StartsWith(string path, ReadOnlySpan<byte> signature)
    {
        Span<byte> head = stackalloc byte[signature.Length];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.SequenceEqual(signature);
    }
}
