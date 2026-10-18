// CSV ↔ the data tree. CSV is read like the spreadsheet engine reads it (delimiter and encoding detected, see
// CsvFormat): the first row names the columns and every other row becomes an object. Cells that are plain numbers
// (not "007") or true / false get that type; everything else, empty cells included, stays text.
// Only tables become CSV: an array of objects (columns = every key, in the order first seen), an object of arrays
// (one column per key), an array of arrays (rows as they are) or a single flat object (one row). Objects inside a row
// one level deep become "parent.child" columns; deeper nesting, or a list inside a row, is an error. Wrappers around
// the array (an object with only that key, XML's <rows><row/>…</rows>) are looked through. Written as the spreadsheet
// engine writes CSV: UTF-8 with BOM, CRLF, so Excel opens Korean text correctly.

using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Data;

internal static partial class CsvData
{
    /// <summary>The rows of a CSV file as text, delimiter (comma, semicolon or tab) and encoding detected.</summary>
    public static List<List<string>> ReadRows(string path)
    {
        var text = HwpxConverter.DecodeText(File.ReadAllBytes(path));
        var rows = CsvFormat.Parse(text, CsvFormat.DetectDelimiter(text));
        rows.RemoveAll(row => row.All(cell => cell.Length == 0));
        return rows;
    }

    /// <summary>A CSV file as an array of objects keyed by the header row.</summary>
    public static List<object?> Read(string path)
    {
        var rows = ReadRows(path);
        if (rows.Count == 0)
            return [];
        var headers = UniqueHeaders(rows[0]);
        var records = new List<object?>();
        foreach (var row in rows.Skip(1))
        {
            while (headers.Count < row.Count)
                headers.Add($"column{headers.Count + 1}");
            var record = new DataMap();
            for (var c = 0; c < headers.Count; c++)
                record[headers[c]] = Typed(c < row.Count ? row[c] : "");
            records.Add(record);
        }
        return records;
    }

    /// <summary>Header names: blank ones become "columnN", repeated ones get "_2", "_3".</summary>
    private static List<string> UniqueHeaders(List<string> row)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var headers = new List<string>();
        for (var c = 0; c < row.Count; c++)
        {
            var name = row[c].Trim();
            if (name.Length == 0)
                name = $"column{c + 1}";
            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
                unique = $"{name}_{i}";
            headers.Add(unique);
        }
        return headers;
    }

    private static object? Typed(string cell)
    {
        if (cell.Length is > 0 and < 20 && JsonNumber().IsMatch(cell))
            return DataTree.Number(cell);
        if (cell.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (cell.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;
        return cell;
    }

    /// <summary>Writes a table-like tree as CSV, or explains why it is not a table.</summary>
    public static void Write(object? root, string path) => CsvFormat.WriteRows(Table(root), path);

    /// <summary>The header row and the rows of a table-like tree.</summary>
    internal static List<List<string>> Table(object? root)
    {
        var node = DataTree.UnwrapRecords(root);
        switch (node)
        {
            case List<object?> list when list.Count == 0:
                return [];
            case List<object?> list when list.All(item => item is DataMap or null):
                return Records(list.Select(item => item as DataMap ?? new DataMap()).ToList());
            case List<object?> list when list.All(item => item is List<object?> row && row.All(DataTree.IsScalar)):
                return list.Select(item => ((List<object?>)item!).Select(DataTree.Text).ToList()).ToList();
            case List<object?> list when list.All(DataTree.IsScalar):
                return [["value"], .. list.Select(item => new List<string> { DataTree.Text(item) })];
            case DataMap map when map.Count > 0 && map.Values.All(v => v is List<object?> column && column.All(DataTree.IsScalar)):
                return Columns(map);
            case DataMap map:
                return Records([map]);
            case List<object?>:
                throw NotATable("The array mixes objects, lists and single values");
            default:
                throw NotATable("The data is a single value");
        }
    }

    private static List<List<string>> Records(List<DataMap> records)
    {
        var columns = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var flat = new List<Dictionary<string, string>>();
        for (var r = 0; r < records.Count; r++)
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in records[r])
            {
                if (value is DataMap nested)
                {
                    foreach (var (childKey, child) in nested)
                    {
                        if (!DataTree.IsScalar(child))
                            throw NotATable($"Row {r + 1}, “{key}.{childKey}” is nested more than one level");
                        Add($"{key}.{childKey}", child);
                    }
                }
                else if (value is List<object?>)
                {
                    throw NotATable($"Row {r + 1}, “{key}” holds a list");
                }
                else
                {
                    Add(key, value);
                }
            }
            flat.Add(cells);

            void Add(string column, object? value)
            {
                if (known.Add(column))
                    columns.Add(column);
                cells[column] = DataTree.Text(value);
            }
        }
        return [columns, .. flat.Select(cells => columns.Select(c => cells.GetValueOrDefault(c, "")).ToList())];
    }

    private static List<List<string>> Columns(DataMap map)
    {
        var columns = map.Values.Cast<List<object?>>().ToList();
        var height = columns.Max(c => c.Count);
        var rows = new List<List<string>> { map.Keys.ToList() };
        for (var r = 0; r < height; r++)
            rows.Add(columns.Select(c => r < c.Count ? DataTree.Text(c[r]) : "").ToList());
        return rows;
    }

    private static InvalidDataException NotATable(string what) =>
        new($"{what}: CSV holds a table. Convert an array of objects (one level of nesting at most), an object of arrays or an array of arrays.");

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();
}
