// CSV / TSV ↔ Worksheet. Reading follows RFC 4180 (quotes, doubled quotes, line breaks inside quotes), detects the
// CSV delimiter (comma, semicolon or tab; TSV is always tab) and the encoding (UTF-8 / UTF-16 with BOM, else CP949
// like Korean Excel). Writing produces what Excel opens correctly everywhere: UTF-8 with BOM, CRLF line ends,
// quotes only when needed.

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Office.Sheets;

internal static class CsvFormat
{
    /// <summary>The TSV delimiter.</summary>
    public const char Tab = '\t';

    /// <summary>Reads a CSV file into one sheet named after the file.</summary>
    public static Workbook Read(string path) => Read(path, delimiter: null);

    /// <summary>Reads a TSV file (tab-separated, whatever other characters it holds).</summary>
    public static Workbook ReadTsv(string path) => Read(path, Tab);

    private static Workbook Read(string path, char? delimiter)
    {
        var text = HwpxConverter.DecodeText(File.ReadAllBytes(path));
        var sheet = new Worksheet(Path.GetFileNameWithoutExtension(path));
        var rows = Parse(text, delimiter ?? DetectDelimiter(text));
        var widths = new Dictionary<int, double>();
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                var value = rows[r][c];
                if (value.Length == 0)
                    continue;
                sheet.Set(r, c, IsNumber(value)
                    ? new SheetCell(value, CellStyle.Default, IsNumber: true) { Value = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) }
                    : new SheetCell(value, CellStyle.Default));
                widths[c] = Math.Max(widths.GetValueOrDefault(c), DisplayWidth(value));
            }
        }
        // CSV has no widths: size columns to their content like Excel's "AutoFit" would, within reason.
        foreach (var (column, width) in widths)
            sheet.ColumnWidths[column] = Math.Clamp(width + 1, 4, 50);
        var book = new Workbook();
        book.Sheets.Add(sheet);
        return book;
    }

    /// <summary>Writes the used range of a sheet as CSV, or as TSV with <see cref="Tab"/> (UTF-8 with BOM, CRLF).</summary>
    public static void Write(Worksheet sheet, string path, char delimiter = ',')
    {
        var sb = new StringBuilder();
        if (sheet.UsedRange() is { } range)
        {
            for (var r = 0; r <= range.Bottom; r++)
            {
                if (sheet.HiddenRows.Contains(r))
                    continue;
                var fields = new List<string>();
                for (var c = 0; c <= range.Right; c++)
                {
                    if (!sheet.HiddenColumns.Contains(c))
                        fields.Add(Quote(sheet.Get(r, c)?.Text ?? "", delimiter));
                }
                // Excel omits trailing empty fields only for fully empty rows; keep the column count stable.
                sb.Append(string.Join(delimiter, fields)).Append("\r\n");
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Writes rows of text as CSV the same way as <see cref="Write"/> (UTF-8 with BOM, CRLF).</summary>
    public static void WriteRows(IEnumerable<IEnumerable<string>> rows, string path, char delimiter = ',')
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(delimiter, row.Select(value => Quote(value, delimiter)))).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    internal static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The most frequent of comma, semicolon and tab outside quotes in the first lines.</summary>
    internal static char DetectDelimiter(string text)
    {
        var sample = text.Length > 4096 ? text[..4096] : text;
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var ch in sample)
        {
            if (ch == '"')
                quoted = !quoted;
            else if (!quoted && counts.ContainsKey(ch))
                counts[ch]++;
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == ',' ? 0 : 1).First().Key;
    }

    private static string Quote(string value, char delimiter) =>
        value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    /// <summary>A plain number Excel would store as a number: "12", "-3.5", "1e3" (not "007" or "1,234").</summary>
    internal static bool IsNumber(string value) =>
        value.Length is > 0 and < 20
        && !(value.Length > 1 && value[0] == '0' && value[1] != '.')
        && !(value.Length > 2 && value[0] == '-' && value[1] == '0' && value[2] != '.')
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Width in "characters" as Excel counts them: wide (CJK) glyphs count twice.</summary>
    internal static double DisplayWidth(string text)
    {
        var longest = 0.0;
        foreach (var line in text.Split('\n'))
        {
            var width = 0.0;
            foreach (var ch in line)
                width += ch >= 0x1100 && (ch <= 0x115F || ch >= 0x2E80 && ch <= 0xA4CF || ch >= 0xAC00 && ch <= 0xD7A3 || ch >= 0xF900 && ch <= 0xFAFF || ch >= 0xFF00 && ch <= 0xFF60) ? 2 : 1;
            longest = Math.Max(longest, width);
        }
        return longest;
    }
}
