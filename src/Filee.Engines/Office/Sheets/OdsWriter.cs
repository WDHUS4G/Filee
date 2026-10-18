// Workbook → ODS (OpenDocument spreadsheet) without LibreOffice: one table per sheet with typed cells (numbers,
// percentages, currencies, dates and times keep their value and number format as a data style, the shown text is
// the cached text:p), booleans, text with line breaks and repeated spaces, cell styles (font, colour, fill,
// borders, alignment), merged cells, column widths, row heights, hidden rows and columns, and frozen rows. Opens in
// LibreOffice, Excel and Google Sheets.

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Filee.Engines.Office.Sheets;

internal static class OdsWriter
{
    private const string MimeType = "application/vnd.oasis.opendocument.spreadsheet";
    private const string Version = "1.3";
    private const string Declaration = """<?xml version="1.0" encoding="UTF-8"?>""";

    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:number=\"urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0\"";

    /// <summary>Columns written per sheet: LibreOffice before 7.4 stops at 1024 and warns about anything beyond.</summary>
    private const int SheetColumns = 1024;

    /// <summary>
    /// Excel's default font size, so a workbook from Excel keeps its proportions (LibreOffice's own default is 10 pt).
    /// </summary>
    private const string StylesXml = Declaration +
        $"<office:document-styles {Namespaces} office:version=\"{Version}\"><office:styles>" +
        "<style:default-style style:family=\"table-cell\"><style:text-properties fo:font-size=\"11pt\" style:font-size-asian=\"11pt\" style:font-size-complex=\"11pt\"/></style:default-style>" +
        "<style:style style:name=\"Default\" style:family=\"table-cell\"/>" +
        "</office:styles></office:document-styles>";

    public static void Write(Workbook book, string path)
    {
        var sheets = book.Sheets.Count > 0 ? book.Sheets : [new Worksheet("Sheet1")];
        var names = XlsxWriter.UniqueNames(sheets.Select(s => s.Name));
        var styles = new AutomaticStyles();
        var body = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
            Table(body, sheets[i], names[i], styles);

        var content = Declaration +
                      $"<office:document-content {Namespaces} office:version=\"{Version}\"><office:automatic-styles>{styles.Xml()}</office:automatic-styles>" +
                      $"<office:body><office:spreadsheet>{body}</office:spreadsheet></office:body></office:document-content>";
        var frozen = sheets.Select((s, i) => (Name: names[i], s.FrozenRows)).Where(f => f.FrozenRows > 0).ToList();

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // OpenDocument rule: "mimetype" first and stored uncompressed, so the type can be read at a fixed offset.
        Add(zip, "mimetype", MimeType, CompressionLevel.NoCompression);
        Add(zip, "content.xml", content);
        Add(zip, "styles.xml", StylesXml);
        if (frozen.Count > 0)
            Add(zip, "settings.xml", Settings(frozen));
        Add(zip, "META-INF/manifest.xml", Declaration +
            $"<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"{Version}\">" +
            $"<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"{Version}\" manifest:media-type=\"{MimeType}\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" +
            (frozen.Count > 0 ? "<manifest:file-entry manifest:full-path=\"settings.xml\" manifest:media-type=\"text/xml\"/>" : "") +
            "</manifest:manifest>");
    }

    private static void Table(StringBuilder sb, Worksheet sheet, string name, AutomaticStyles styles)
    {
        var range = sheet.UsedRange();
        var right = range?.Right ?? 0;
        var bottom = range?.Bottom ?? 0;
        sb.Append(CultureInfo.InvariantCulture, $"<table:table table:name=\"{Escape(name)}\" table:style-name=\"{styles.Table()}\">");

        for (var c = 0; c <= right;)
        {
            var width = sheet.ColumnWidth(c);
            var hidden = sheet.HiddenColumns.Contains(c);
            var count = 1;
            while (c + count <= right && sheet.ColumnWidth(c + count) == width && sheet.HiddenColumns.Contains(c + count) == hidden)
                count++;
            sb.Append(CultureInfo.InvariantCulture, $"<table:table-column table:style-name=\"{styles.Column(width)}\"{Repeated("columns", count)}{(hidden ? " table:visibility=\"collapse\"" : "")}/>");
            c += count;
        }
        // The rest of the sheet gets the workbook's default width (LibreOffice's own default is wider than Excel's).
        if (right + 1 < SheetColumns)
            sb.Append(CultureInfo.InvariantCulture, $"<table:table-column table:style-name=\"{styles.Column(sheet.DefaultColumnWidth)}\"{Repeated("columns", SheetColumns - right - 1)}/>");

        // Merged areas: the top-left cell spans, the others are covered cells.
        var anchors = new Dictionary<(int, int), CellRange>();
        var covered = new HashSet<(int, int)>();
        foreach (var merge in sheet.Merges)
        {
            if (anchors.ContainsKey((merge.Top, merge.Left)) || covered.Contains((merge.Top, merge.Left)))
                continue; // overlapping merges are invalid; keep the first
            anchors[(merge.Top, merge.Left)] = merge;
            for (var r = merge.Top; r <= merge.Bottom; r++)
                for (var c = merge.Left; c <= merge.Right; c++)
                    if (r != merge.Top || c != merge.Left)
                        covered.Add((r, c));
        }

        for (var r = 0; r <= bottom;)
        {
            var height = sheet.RowHeights.TryGetValue(r, out var points) ? points : (double?)null;
            var hidden = sheet.HiddenRows.Contains(r);
            var visibility = hidden ? " table:visibility=\"collapse\"" : "";
            if (IsEmptyRow(sheet, r, right, covered))
            {
                // Runs of empty rows alike become one repeated row.
                var count = 1;
                while (r + count <= bottom && IsEmptyRow(sheet, r + count, right, covered) && sheet.HiddenRows.Contains(r + count) == hidden &&
                       (sheet.RowHeights.TryGetValue(r + count, out var next) ? next : (double?)null) == height)
                    count++;
                sb.Append(CultureInfo.InvariantCulture, $"<table:table-row table:style-name=\"{styles.Row(height)}\"{Repeated("rows", count)}{visibility}><table:table-cell{Repeated("columns", right + 1)}/></table:table-row>");
                r += count;
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture, $"<table:table-row table:style-name=\"{styles.Row(height)}\"{visibility}>");
            for (var c = 0; c <= right;)
            {
                var cell = sheet.Get(r, c);
                var isCovered = covered.Contains((r, c));
                if (isCovered || cell is null && !anchors.ContainsKey((r, c)))
                {
                    var count = 1;
                    while (c + count <= right && covered.Contains((r, c + count)) == isCovered && (isCovered || sheet.Get(r, c + count) is null && !anchors.ContainsKey((r, c + count))))
                        count++;
                    sb.Append(isCovered ? "<table:covered-table-cell" : "<table:table-cell").Append(Repeated("columns", count)).Append("/>");
                    c += count;
                    continue;
                }
                Cell(sb, cell, anchors.TryGetValue((r, c), out var merge) ? merge : null, styles);
                c++;
            }
            sb.Append("</table:table-row>");
            r++;
        }
        sb.Append("</table:table>");
    }

    private static bool IsEmptyRow(Worksheet sheet, int row, int right, HashSet<(int, int)> covered) =>
        !sheet.Rows.ContainsKey(row) && !Enumerable.Range(0, right + 1).Any(c => covered.Contains((row, c)));

    private static void Cell(StringBuilder sb, SheetCell? cell, CellRange? merge, AutomaticStyles styles)
    {
        sb.Append("<table:table-cell");
        if (merge is { } m)
        {
            if (m.Right > m.Left)
                sb.Append(CultureInfo.InvariantCulture, $" table:number-columns-spanned=\"{m.Right - m.Left + 1}\"");
            if (m.Bottom > m.Top)
                sb.Append(CultureInfo.InvariantCulture, $" table:number-rows-spanned=\"{m.Bottom - m.Top + 1}\"");
        }
        if (cell is null)
        {
            sb.Append("/>");
            return;
        }

        var number = cell.Value ?? (cell.IsNumber && double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
        string? dataStyle = null;
        if (cell.IsBoolean && number is { } flag)
        {
            sb.Append(CultureInfo.InvariantCulture, $" office:value-type=\"boolean\" office:boolean-value=\"{(flag != 0 ? "true" : "false")}\"");
        }
        else if (number is { } value && double.IsFinite(value))
        {
            var (name, valueType) = styles.DataStyle(cell.NumberFormat);
            dataStyle = name;
            sb.Append(Value(value, valueType));
        }
        else if (cell.Text.Length > 0)
        {
            sb.Append(" office:value-type=\"string\"");
        }
        if (styles.Cell(cell.Style, dataStyle) is { } styleName)
            sb.Append(CultureInfo.InvariantCulture, $" table:style-name=\"{styleName}\"");

        if (cell.Text.Length == 0)
        {
            sb.Append("/>");
            return;
        }
        sb.Append('>');
        Paragraphs(sb, cell.Text);
        sb.Append("</table:table-cell>");
    }

    /// <summary>The value attributes of a number cell: dates as ISO dates, times as durations, the rest as numbers.</summary>
    private static string Value(double value, string valueType)
    {
        var number = value.ToString("R", CultureInfo.InvariantCulture);
        switch (valueType)
        {
            case "date" when value is > -657_000 and < 2_958_000:
                var date = NumberFormatter.FromSerial(value);
                var iso = date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString(date.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
                return $" office:value-type=\"date\" office:date-value=\"{iso}\"";
            case "time" when Math.Abs(value) < 1e7:
                var duration = TimeSpan.FromDays(Math.Abs(value));
                return string.Create(CultureInfo.InvariantCulture,
                    $" office:value-type=\"time\" office:time-value=\"{(value < 0 ? "-" : "")}PT{(long)duration.TotalHours}H{duration.Minutes:00}M{duration.Seconds + duration.Milliseconds / 1000.0:00.###}S\"");
            case "percentage" or "currency":
                return $" office:value-type=\"{valueType}\" office:value=\"{number}\"";
            default:
                return $" office:value-type=\"float\" office:value=\"{number}\"";
        }
    }

    /// <summary>One text:p per line; runs of spaces and tabs as text:s and text:tab, which XML would collapse.</summary>
    private static void Paragraphs(StringBuilder sb, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            sb.Append("<text:p>");
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == ' ')
                {
                    var run = 1;
                    while (i + run < line.Length && line[i + run] == ' ')
                        run++;
                    // The first space of a run is kept as text unless the line starts with it.
                    var extra = i == 0 ? run : run - 1;
                    if (i > 0)
                        sb.Append(' ');
                    if (extra > 0)
                        sb.Append(extra == 1 ? "<text:s/>" : string.Create(CultureInfo.InvariantCulture, $"<text:s text:c=\"{extra}\"/>"));
                    i += run - 1;
                }
                else if (ch == '\t')
                    sb.Append("<text:tab/>");
                else if (ch >= ' ')
                    sb.Append(Escape(ch.ToString()));
            }
            sb.Append("</text:p>");
        }
    }

    private static string Repeated(string what, int count) =>
        count > 1 ? string.Create(CultureInfo.InvariantCulture, $" table:number-{what}-repeated=\"{count}\"") : "";

    private static string Settings(List<(string Name, int FrozenRows)> frozen)
    {
        var sb = new StringBuilder(Declaration);
        sb.Append(CultureInfo.InvariantCulture, $"<office:document-settings xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" office:version=\"{Version}\">");
        sb.Append("<office:settings><config:config-item-set config:name=\"ooo:view-settings\"><config:config-item-map-indexed config:name=\"Views\"><config:config-item-map-entry>");
        sb.Append("<config:config-item config:name=\"ViewId\" config:type=\"string\">view1</config:config-item><config:config-item-map-named config:name=\"Tables\">");
        foreach (var (name, rows) in frozen)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<config:config-item-map-entry config:name=\"{Escape(name)}\">");
            void Item(string item, string type, int value) =>
                sb.Append(CultureInfo.InvariantCulture, $"<config:config-item config:name=\"{item}\" config:type=\"{type}\">{value}</config:config-item>");
            Item("HorizontalSplitMode", "short", 0);
            Item("VerticalSplitMode", "short", 2);
            Item("HorizontalSplitPosition", "int", 0);
            Item("VerticalSplitPosition", "int", rows);
            Item("ActiveSplitRange", "short", 2);
            Item("PositionLeft", "int", 0);
            Item("PositionRight", "int", 0);
            Item("PositionTop", "int", 0);
            Item("PositionBottom", "int", rows);
            sb.Append("</config:config-item-map-entry>");
        }
        sb.Append("</config:config-item-map-named></config:config-item-map-entry></config:config-item-map-indexed></config:config-item-set></office:settings></office:document-settings>");
        return sb.ToString();
    }

    private static string Escape(string text) =>
        SecurityElement.Escape(new string([.. text.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ')])) ?? "";

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>office:automatic-styles of content.xml: one style per distinct column width, row height, cell look and number format.</summary>
    private sealed class AutomaticStyles
    {
        private readonly StringBuilder _columns = new();
        private readonly StringBuilder _rows = new();
        private readonly StringBuilder _data = new();
        private readonly StringBuilder _cells = new();
        private readonly Dictionary<double, string> _columnNames = [];
        private readonly Dictionary<double, string> _rowNames = [];
        private readonly Dictionary<string, (string? Name, string ValueType)> _dataNames = [];
        private readonly Dictionary<(CellStyle, string?), string?> _cellNames = [];

        public string Table() => "ta1";

        public string Column(double width)
        {
            if (!_columnNames.TryGetValue(width, out var name))
            {
                _columnNames[width] = name = "co" + (_columnNames.Count + 1).ToString(CultureInfo.InvariantCulture);
                _columns.Append(CultureInfo.InvariantCulture,
                    $"<style:style style:name=\"{name}\" style:family=\"table-column\"><style:table-column-properties fo:break-before=\"auto\" style:column-width=\"{width * OdsReader.PointsPerCharacter:0.###}pt\"/></style:style>");
            }
            return name;
        }

        /// <summary>A row style: a custom height, or (null) the height that fits the content.</summary>
        public string Row(double? height)
        {
            var key = height ?? -1;
            if (!_rowNames.TryGetValue(key, out var name))
            {
                _rowNames[key] = name = "ro" + (_rowNames.Count + 1).ToString(CultureInfo.InvariantCulture);
                _rows.Append(CultureInfo.InvariantCulture,
                    $"<style:style style:name=\"{name}\" style:family=\"table-row\"><style:table-row-properties style:row-height=\"{height ?? 15:0.##}pt\" fo:break-before=\"auto\" style:use-optimal-row-height=\"{(height is null ? "true" : "false")}\"/></style:style>");
            }
            return name;
        }

        /// <summary>The data style of an Excel format code (null name = General) and the value type of its cells.</summary>
        public (string? Name, string ValueType) DataStyle(string? format)
        {
            if (format is null)
                return (null, "float");
            if (!_dataNames.TryGetValue(format, out var result))
            {
                var name = "N" + (_dataNames.Count + 1).ToString(CultureInfo.InvariantCulture);
                if (OdsNumberStyles.ToOdf(format, name) is var (xml, valueType))
                {
                    _data.Append(xml);
                    result = (name, valueType);
                }
                else
                {
                    result = (null, "float");
                }
                _dataNames[format] = result;
            }
            return result;
        }

        /// <summary>The cell style for a look and data style; null for the default look without a number format.</summary>
        public string? Cell(CellStyle style, string? dataStyle)
        {
            if (style == CellStyle.Default && dataStyle is null)
                return null;
            if (_cellNames.TryGetValue((style, dataStyle), out var name))
                return name;
            name = "ce" + (_cellNames.Count + 1).ToString(CultureInfo.InvariantCulture);
            _cellNames[(style, dataStyle)] = name;

            _cells.Append(CultureInfo.InvariantCulture, $"<style:style style:name=\"{name}\" style:family=\"table-cell\" style:parent-style-name=\"Default\"");
            if (dataStyle is not null)
                _cells.Append(CultureInfo.InvariantCulture, $" style:data-style-name=\"{dataStyle}\"");
            _cells.Append("><style:table-cell-properties");
            if (style.Fill is { } fill)
                _cells.Append(CultureInfo.InvariantCulture, $" fo:background-color=\"{fill}\"");
            if (style.Left == style.Right && style.Left == style.Top && style.Left == style.Bottom)
            {
                if (style.Left.IsVisible)
                    _cells.Append(CultureInfo.InvariantCulture, $" fo:border=\"{Border(style.Left)}\"");
            }
            else
            {
                foreach (var (side, border) in new[] { ("left", style.Left), ("right", style.Right), ("top", style.Top), ("bottom", style.Bottom) })
                    _cells.Append(CultureInfo.InvariantCulture, $" fo:border-{side}=\"{(border.IsVisible ? Border(border) : "none")}\"");
            }
            if (style.VerticalAlign != CellVerticalAlign.Bottom)
                _cells.Append(CultureInfo.InvariantCulture, $" style:vertical-align=\"{(style.VerticalAlign == CellVerticalAlign.Top ? "top" : "middle")}\"");
            if (style.Wrap)
                _cells.Append(" fo:wrap-option=\"wrap\"");
            if (style.Align != CellAlign.General)
                _cells.Append(" style:text-align-source=\"fix\" style:repeat-content=\"false\"");
            _cells.Append("/>");
            if (style.Align != CellAlign.General)
            {
                var align = style.Align switch { CellAlign.Left => "start", CellAlign.Center => "center", _ => "end" };
                _cells.Append(CultureInfo.InvariantCulture, $"<style:paragraph-properties fo:text-align=\"{align}\"/>");
            }

            var text = new StringBuilder();
            if (style.Bold)
                text.Append(" fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\"");
            if (style.Italic)
                text.Append(" fo:font-style=\"italic\" style:font-style-asian=\"italic\" style:font-style-complex=\"italic\"");
            if (style.Underline)
                text.Append(" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"");
            if (style.Strike)
                text.Append(" style:text-line-through-style=\"solid\" style:text-line-through-type=\"single\"");
            if (style.FontSize is { } size)
                text.Append(CultureInfo.InvariantCulture, $" fo:font-size=\"{size:0.##}pt\" style:font-size-asian=\"{size:0.##}pt\" style:font-size-complex=\"{size:0.##}pt\"");
            if (style.Color is { } color)
                text.Append(CultureInfo.InvariantCulture, $" fo:color=\"{color}\"");
            if (text.Length > 0)
                _cells.Append("<style:text-properties").Append(text).Append("/>");
            _cells.Append("</style:style>");
            return name;
        }

        public string Xml() =>
            _columns.ToString() + _rows +
            "<style:style style:name=\"ta1\" style:family=\"table\"><style:table-properties table:display=\"true\" style:writing-mode=\"lr-tb\"/></style:style>" +
            _data + _cells;

        /// <summary>An Excel border as an ODF border: widths LibreOffice uses when it imports the same Excel style.</summary>
        private static string Border(CellBorder border)
        {
            var (width, pattern) = border.Style switch
            {
                "hair" => (0.1, "solid"),
                "medium" => (1.75, "solid"),
                "thick" => (2.5, "solid"),
                "double" => (2.25, "double"),
                "dotted" => (0.75, "dotted"),
                "dashed" => (0.75, "dashed"),
                "mediumDashed" => (1.75, "dashed"),
                "dashDot" => (0.75, "dot-dash"),
                "mediumDashDot" or "slantDashDot" => (1.75, "dot-dash"),
                "dashDotDot" => (0.75, "dot-dot-dash"),
                "mediumDashDotDot" => (1.75, "dot-dot-dash"),
                _ => (0.75, "solid"),
            };
            return string.Create(CultureInfo.InvariantCulture, $"{width}pt {pattern} {border.Color ?? "#000000"}");
        }
    }
}
