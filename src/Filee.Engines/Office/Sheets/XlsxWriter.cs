// Workbook → XLSX package: every sheet, numbers and dates as numbers with their number formats, booleans, text as
// inline strings, fonts / fills / borders / alignment, merged cells, column widths, row heights, hidden rows and
// columns, and frozen header rows. Used for CSV, TSV, XLS and ODS → XLSX; opens in Excel, LibreOffice and Google
// Sheets.

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Filee.Engines.Office.Sheets;

internal static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Built-in format ids Excel knows without a numFmt element (ECMA-376 18.8.30, locale independent ones).</summary>
    private static readonly Dictionary<string, int> BuiltInIds = new()
    {
        ["0"] = 1,
        ["0.00"] = 2,
        ["#,##0"] = 3,
        ["#,##0.00"] = 4,
        ["0%"] = 9,
        ["0.00%"] = 10,
        ["0.00E+00"] = 11,
        ["@"] = 49,
    };

    public static void Write(Workbook book, string path)
    {
        var sheets = book.Sheets.Count > 0 ? book.Sheets : [new Worksheet("Sheet1")];
        var names = UniqueNames(sheets.Select(s => s.Name));
        var styles = new StyleTable();
        var sheetXml = sheets.Select((s, i) => SheetXml(s, styles, selected: i == 0)).ToList();

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""" +
            string.Concat(sheets.Select((_, i) => $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")) +
            "</Types>");
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Add(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
            string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""")) +
            """<Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Add(zip, "xl/workbook.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{Main}" xmlns:r="{RelationshipsNs}"><sheets>""" +
            string.Concat(names.Select((name, i) => $"""<sheet name="{Escape(name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""")) +
            "</sheets></workbook>");
        Add(zip, "xl/styles.xml", styles.Xml());
        for (var i = 0; i < sheets.Count; i++)
            Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sheetXml[i]);
    }

    private static string SheetXml(Worksheet sheet, StyleTable styles, bool selected)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append(CultureInfo.InvariantCulture, $"""<worksheet xmlns="{Main}" xmlns:r="{RelationshipsNs}"><sheetViews><sheetView{(selected ? " tabSelected=\"1\"" : "")} workbookViewId="0">""");
        if (sheet.FrozenRows > 0)
            sb.Append(CultureInfo.InvariantCulture, $"""<pane ySplit="{sheet.FrozenRows}" topLeftCell="A{sheet.FrozenRows + 1}" activePane="bottomLeft" state="frozen"/>""");
        sb.Append("</sheetView></sheetViews>");
        sb.Append("""<sheetFormatPr defaultRowHeight="15" """);
        if (Math.Abs(sheet.DefaultColumnWidth - Worksheet.StandardColumnWidth) > 0.005)
            sb.Append(CultureInfo.InvariantCulture, $"""defaultColWidth="{sheet.DefaultColumnWidth:0.###}" """);
        sb.Append("/>");
        Columns(sb, sheet);

        sb.Append("<sheetData>");
        var rows = new SortedSet<int>(sheet.Rows.Keys);
        rows.UnionWith(sheet.RowHeights.Keys);
        rows.UnionWith(sheet.HiddenRows);
        foreach (var row in rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row + 1}\"");
            if (sheet.RowHeights.TryGetValue(row, out var height))
                sb.Append(CultureInfo.InvariantCulture, $" ht=\"{height:0.##}\" customHeight=\"1\"");
            if (sheet.HiddenRows.Contains(row))
                sb.Append(" hidden=\"1\"");
            sb.Append('>');
            foreach (var (column, cell) in sheet.Rows.GetValueOrDefault(row) ?? [])
                Cell(sb, ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture), cell, styles);
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        if (sheet.Merges.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<mergeCells count=\"{sheet.Merges.Count}\">");
            foreach (var merge in sheet.Merges)
                sb.Append(CultureInfo.InvariantCulture, $"<mergeCell ref=\"{ColumnName(merge.Left)}{merge.Top + 1}:{ColumnName(merge.Right)}{merge.Bottom + 1}\"/>");
            sb.Append("</mergeCells>");
        }
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    /// <summary>&lt;cols&gt;: runs of neighbouring columns with the same width and visibility share one element.</summary>
    private static void Columns(StringBuilder sb, Worksheet sheet)
    {
        var columns = sheet.ColumnWidths.Keys.Union(sheet.HiddenColumns).Order().ToList();
        if (columns.Count == 0)
            return;
        sb.Append("<cols>");
        for (var i = 0; i < columns.Count;)
        {
            var first = columns[i];
            var width = sheet.ColumnWidth(first);
            var hidden = sheet.HiddenColumns.Contains(first);
            var last = first;
            while (++i < columns.Count && columns[i] == last + 1 && sheet.ColumnWidth(columns[i]) == width && sheet.HiddenColumns.Contains(columns[i]) == hidden)
                last = columns[i];
            sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{first + 1}\" max=\"{last + 1}\" width=\"{width:0.###}\" customWidth=\"1\"{(hidden ? " hidden=\"1\"" : "")}/>");
        }
        sb.Append("</cols>");
    }

    private static void Cell(StringBuilder sb, string reference, SheetCell cell, StyleTable styles)
    {
        var number = cell.Value ?? (cell.IsNumber && double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
        var style = styles.Index(cell.Style, number is null || cell.IsBoolean ? null : cell.NumberFormat);
        var s = style == 0 ? "" : $" s=\"{style}\"";
        if (cell.IsBoolean && number is { } flag)
            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{s} t=\"b\"><v>{(flag != 0 ? 1 : 0)}</v></c>");
        else if (number is { } value && double.IsFinite(value))
            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{s}><v>{value.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
        else if (cell.Text.Length == 0)
            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{s}/>");
        else
            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{s} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cell.Text)}</t></is></c>");
    }

    /// <summary>0 → "A", 26 → "AA".</summary>
    internal static string ColumnName(int column)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
            name = (char)('A' + (c - 1) % 26) + name;
        return name;
    }

    /// <summary>Excel sheet names: at most 31 characters, none of : \ / ? * [ ], unique ignoring case.</summary>
    internal static List<string> UniqueNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in names)
        {
            var clean = new string([.. name.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))]).Trim('\'').Trim();
            clean = clean.Length == 0 ? "Sheet" + (result.Count + 1) : clean;
            clean = clean.Length > 31 ? clean[..31] : clean;
            var unique = clean;
            for (var n = 2; !used.Add(unique); n++)
            {
                var suffix = $" ({n})";
                unique = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
            }
            result.Add(unique);
        }
        return result;
    }

    internal static string Escape(string text) =>
        SecurityElement.Escape(new string([.. text.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ')])) ?? "";

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>styles.xml: one cell format (xf) per distinct style and number format, sharing fonts, fills and borders.</summary>
    private sealed class StyleTable
    {
        private readonly Dictionary<(CellStyle, string?), int> _xfs = new() { [(CellStyle.Default, null)] = 0 };
        private readonly List<string> _xfXml = ["""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>"""];
        private readonly Dictionary<string, int> _formats = [];
        private readonly List<string> _fonts = ["""<font><sz val="11"/><name val="Calibri"/><family val="2"/></font>"""];
        private readonly List<string> _fills = ["""<fill><patternFill patternType="none"/></fill>""", """<fill><patternFill patternType="gray125"/></fill>"""];
        private readonly List<string> _borders = ["<border><left/><right/><top/><bottom/><diagonal/></border>"];

        /// <summary>The cellXfs index of a style with a number format (null = General).</summary>
        public int Index(CellStyle style, string? format)
        {
            if (_xfs.TryGetValue((style, format), out var index))
                return index;
            var formatId = format is null ? 0 : BuiltInIds.TryGetValue(format, out var builtIn) ? builtIn : FormatId(format);
            var fontId = Add(_fonts, Font(style));
            var fillId = style.Fill is { } fill ? Add(_fills, $"""<fill><patternFill patternType="solid"><fgColor rgb="{Argb(fill)}"/><bgColor indexed="64"/></patternFill></fill>""") : 0;
            var borderId = Add(_borders, $"<border>{Side("left", style.Left)}{Side("right", style.Right)}{Side("top", style.Top)}{Side("bottom", style.Bottom)}<diagonal/></border>");

            var alignment = new StringBuilder();
            if (style.Align != CellAlign.General)
                alignment.Append(CultureInfo.InvariantCulture, $" horizontal=\"{style.Align.ToString().ToLowerInvariant()}\"");
            if (style.VerticalAlign != CellVerticalAlign.Bottom)
                alignment.Append(CultureInfo.InvariantCulture, $" vertical=\"{style.VerticalAlign.ToString().ToLowerInvariant()}\"");
            if (style.Wrap)
                alignment.Append(" wrapText=\"1\"");
            var xf = $"""<xf numFmtId="{formatId}" fontId="{fontId}" fillId="{fillId}" borderId="{borderId}" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1">""" +
                     (alignment.Length > 0 ? $"<alignment{alignment}/>" : "") + "</xf>";
            _xfXml.Add(xf);
            return _xfs[(style, format)] = _xfXml.Count - 1;
        }

        public string Xml()
        {
            var sb = new StringBuilder($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="{Main}">""");
            if (_formats.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<numFmts count=\"{_formats.Count}\">");
                foreach (var (code, id) in _formats)
                    sb.Append(CultureInfo.InvariantCulture, $"<numFmt numFmtId=\"{id}\" formatCode=\"{SecurityElement.Escape(code)}\"/>");
                sb.Append("</numFmts>");
            }
            sb.Append(CultureInfo.InvariantCulture, $"<fonts count=\"{_fonts.Count}\">{string.Concat(_fonts)}</fonts>");
            sb.Append(CultureInfo.InvariantCulture, $"<fills count=\"{_fills.Count}\">{string.Concat(_fills)}</fills>");
            sb.Append(CultureInfo.InvariantCulture, $"<borders count=\"{_borders.Count}\">{string.Concat(_borders)}</borders>");
            sb.Append("""<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""");
            sb.Append(CultureInfo.InvariantCulture, $"<cellXfs count=\"{_xfXml.Count}\">{string.Concat(_xfXml)}</cellXfs>");
            sb.Append("""<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
            return sb.ToString();
        }

        /// <summary>Custom formats are numbered from 164, the first id Excel leaves to workbooks.</summary>
        private int FormatId(string code)
        {
            if (!_formats.TryGetValue(code, out var id))
                _formats[code] = id = 164 + _formats.Count;
            return id;
        }

        private static string Font(CellStyle style) =>
            "<font>" + (style.Bold ? "<b/>" : "") + (style.Italic ? "<i/>" : "") + (style.Strike ? "<strike/>" : "") + (style.Underline ? "<u/>" : "") +
            string.Create(CultureInfo.InvariantCulture, $"<sz val=\"{style.FontSize ?? 11:0.##}\"/>") +
            (style.Color is { } color ? $"<color rgb=\"{Argb(color)}\"/>" : "") + """<name val="Calibri"/><family val="2"/></font>""";

        private static string Side(string name, CellBorder border) =>
            border.IsVisible ? $"<{name} style=\"{border.Style}\"><color rgb=\"{Argb(border.Color ?? "#000000")}\"/></{name}>" : $"<{name}/>";

        /// <summary>"#1F4E79" → "FF1F4E79".</summary>
        private static string Argb(string color) => "FF" + color.TrimStart('#').ToUpperInvariant();

        private static int Add(List<string> list, string xml)
        {
            var index = list.IndexOf(xml);
            if (index >= 0)
                return index;
            list.Add(xml);
            return list.Count - 1;
        }
    }
}
