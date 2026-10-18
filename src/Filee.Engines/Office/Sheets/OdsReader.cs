// ODS (OpenDocument spreadsheet, LibreOffice Calc) → Workbook, read directly from content.xml, styles.xml and
// settings.xml without LibreOffice: sheets, repeated rows and columns (the huge empty repeats that end every sheet
// are skipped, not expanded), merged cells, values with the text Calc shows (the cached text:p), number formats as
// Excel codes (OdsNumberStyles), cell styles (font, colour, fill, borders, alignment, wrapping), column widths, row
// heights, hidden rows / columns / sheets and frozen rows. Charts, images, comments and conditional formats are
// not read.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Office.Sheets;

internal sealed class OdsReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Config = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace Style = OdsNumberStyles.Style;
    private static readonly XNamespace Fo = OdsNumberStyles.Fo;
    private static readonly XNamespace Number = OdsNumberStyles.Number;

    /// <summary>Excel's sheet size; ODS sheets repeat their last empty row and column up to (at least) it.</summary>
    private const int MaxRows = 1_048_576;
    private const int MaxColumns = 16_384;

    /// <summary>
    /// Empty but formatted cells repeated more often than this are the formatted rest of a row or sheet (a coloured
    /// column, a bordered page), not content worth a table cell each.
    /// </summary>
    private const int MaxStyledRepeat = 256;

    /// <summary>Points per character of column width (7 pixels of Calibri 11 at 96 dpi), as in the XLSX writer.</summary>
    internal const double PointsPerCharacter = 5.25;

    private readonly Dictionary<(string Family, string Name), XElement> _styles = [];
    private readonly Dictionary<string, XElement> _dataStyles = [];
    private readonly Dictionary<string, (CellStyle Style, string? DataStyle)> _cellStyles = [];
    private readonly Dictionary<string, string?> _formats = [];
    private readonly Dictionary<string, int> _frozenRows = [];
    private readonly NumberFormatter _formatter = new();
    private XElement? _defaultCellStyle;

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (IsEncrypted(zip))
            throw new InvalidDataException("The spreadsheet is protected with a password (encrypted). Remove the password in LibreOffice and convert it again.");
        var content = Load(zip, "content.xml") ?? throw new InvalidDataException("The file is not an OpenDocument spreadsheet (no content.xml).");
        var spreadsheet = content.Root?.Element(Office + "body")?.Element(Office + "spreadsheet")
                          ?? throw new InvalidDataException("The file is not an OpenDocument spreadsheet (no office:spreadsheet).");

        var reader = new OdsReader();
        reader.ReadStyles(Load(zip, "styles.xml"), content);
        reader.ReadSettings(Load(zip, "settings.xml"));
        var book = new Workbook();
        foreach (var table in spreadsheet.Elements(Table + "table"))
        {
            var tableStyle = reader.Find("table", (string?)table.Attribute(Table + "style-name"));
            if ((string?)tableStyle?.Element(Style + "table-properties")?.Attribute(Table + "display") != "false")
                book.Sheets.Add(reader.ReadTable(table));
        }
        return book;
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
            return null;
        using var stream = entry.Open();
        // Whitespace is meaningful inside paragraphs (a space between two spans).
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Encrypted packages keep a readable manifest that lists how each part is encrypted.</summary>
    private static bool IsEncrypted(ZipArchive zip) =>
        Load(zip, "META-INF/manifest.xml")?.Descendants(Manifest + "encryption-data").Any() == true;

    // ───────────────────────── Styles ─────────────────────────

    private void ReadStyles(XDocument? styles, XDocument content)
    {
        var containers = new[]
        {
            styles?.Root?.Element(Office + "styles"),
            styles?.Root?.Element(Office + "automatic-styles"),
            content.Root?.Element(Office + "automatic-styles"),
        };
        foreach (var element in containers.Where(c => c is not null).SelectMany(c => c!.Elements()))
        {
            var name = (string?)element.Attribute(Style + "name");
            if (element.Name == Style + "default-style" && (string?)element.Attribute(Style + "family") == "table-cell")
                _defaultCellStyle = element;
            else if (element.Name == Style + "style" && name is not null)
                _styles[((string?)element.Attribute(Style + "family") ?? "", name)] = element;
            else if (element.Name.Namespace == Number && name is not null)
                _dataStyles[name] = element;
        }
    }

    private XElement? Find(string family, string? name) =>
        name is not null && _styles.TryGetValue((family, name), out var style) ? style : null;

    /// <summary>A cell style with its parents and the default style applied, and its data style name.</summary>
    private (CellStyle Style, string? DataStyle) CellStyleOf(string? name)
    {
        name ??= "Default";
        if (_cellStyles.TryGetValue(name, out var cached))
            return cached;

        var chain = new List<XElement>();
        for (var style = Find("table-cell", name); style is not null && chain.Count < 20; style = Find("table-cell", (string?)style.Attribute(Style + "parent-style-name")))
            chain.Insert(0, style);
        if (_defaultCellStyle is not null)
            chain.Insert(0, _defaultCellStyle);

        var props = new StyleProperties();
        string? dataStyle = null;
        foreach (var style in chain)
        {
            props.Apply(style);
            dataStyle = (string?)style.Attribute(Style + "data-style-name") ?? dataStyle;
        }
        return _cellStyles[name] = (props.ToCellStyle(), dataStyle);
    }

    /// <summary>The Excel format code of a data style (cached), null when there is none.</summary>
    private string? FormatOf(string? dataStyle)
    {
        if (dataStyle is null)
            return null;
        if (!_formats.TryGetValue(dataStyle, out var format))
        {
            _formats[dataStyle] = format = _dataStyles.TryGetValue(dataStyle, out var style)
                ? OdsNumberStyles.ToExcel(style, name => _dataStyles.GetValueOrDefault(name))
                : null;
        }
        return format;
    }

    /// <summary>Formatting properties of one style; unset ones inherit from the parent style.</summary>
    private sealed class StyleProperties
    {
        private bool? _bold, _italic, _underline, _strike, _wrap;
        private double? _size;
        private string? _color, _fill, _textAlign, _alignSource, _verticalAlign;
        private CellBorder? _left, _right, _top, _bottom;

        public void Apply(XElement style)
        {
            if (style.Element(Style + "text-properties") is { } text)
            {
                if ((Attr(text, Fo + "font-weight") ?? Attr(text, Style + "font-weight-asian")) is { } weight)
                    _bold = weight == "bold" || int.TryParse(weight, out var w) && w >= 600;
                if ((Attr(text, Fo + "font-style") ?? Attr(text, Style + "font-style-asian")) is { } italic)
                    _italic = italic is "italic" or "oblique";
                if (Attr(text, Style + "text-underline-style") is { } underline)
                    _underline = underline != "none";
                if (Attr(text, Style + "text-line-through-style") is { } strike)
                    _strike = strike != "none";
                if (Points(Attr(text, Fo + "font-size")) is { } size)
                    _size = size;
                if (Color(Attr(text, Fo + "color")) is { } color)
                    _color = color;
            }
            if (style.Element(Style + "table-cell-properties") is { } cell)
            {
                if (Attr(cell, Fo + "background-color") is { } fill)
                    _fill = fill == "transparent" ? null : Color(fill);
                if (Attr(cell, Fo + "border") is { } all)
                    _left = _right = _top = _bottom = Border(all);
                _left = Border(Attr(cell, Fo + "border-left")) ?? _left;
                _right = Border(Attr(cell, Fo + "border-right")) ?? _right;
                _top = Border(Attr(cell, Fo + "border-top")) ?? _top;
                _bottom = Border(Attr(cell, Fo + "border-bottom")) ?? _bottom;
                _verticalAlign = Attr(cell, Style + "vertical-align") ?? _verticalAlign;
                if (Attr(cell, Fo + "wrap-option") is { } wrap)
                    _wrap = wrap == "wrap";
                _alignSource = Attr(cell, Style + "text-align-source") ?? _alignSource;
            }
            if (Attr(style.Element(Style + "paragraph-properties"), Fo + "text-align") is { } align)
                _textAlign = align;
        }

        public CellStyle ToCellStyle() => new()
        {
            Bold = _bold ?? false,
            Italic = _italic ?? false,
            Underline = _underline ?? false,
            Strike = _strike ?? false,
            FontSize = _size,
            Color = _color,
            Fill = _fill,
            // "value-type" = Calc's automatic alignment (numbers right, text left), like Excel's General.
            Align = _alignSource == "value-type" ? CellAlign.General : _textAlign switch
            {
                "start" or "left" or "justify" => CellAlign.Left,
                "center" => CellAlign.Center,
                "end" or "right" => CellAlign.Right,
                _ => CellAlign.General,
            },
            VerticalAlign = _verticalAlign switch
            {
                "top" => CellVerticalAlign.Top,
                "middle" => CellVerticalAlign.Center,
                _ => CellVerticalAlign.Bottom,
            },
            Wrap = _wrap ?? false,
            Left = _left ?? CellBorder.None,
            Right = _right ?? CellBorder.None,
            Top = _top ?? CellBorder.None,
            Bottom = _bottom ?? CellBorder.None,
        };

        private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

        /// <summary>"0.75pt solid #000000" → the Excel border style of that weight and pattern.</summary>
        private static CellBorder? Border(string? value)
        {
            if (value is null)
                return null;
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = parts.FirstOrDefault(p => p is "solid" or "dotted" or "dashed" or "dot-dash" or "dot-dot-dash" or "double" or "groove" or "ridge" or "inset" or "outset" or "fine-dashed" or "dash-dot" or "dash-dot-dot" or "none" or "hidden");
            if (pattern is null or "none" or "hidden")
                return CellBorder.None;
            var width = parts.Select(Points).FirstOrDefault(p => p is not null) ?? 0.75;
            var color = parts.Select(Color).FirstOrDefault(c => c is not null);
            var style = pattern switch
            {
                "double" => "double",
                "dotted" => "dotted",
                "dashed" or "fine-dashed" => width > 1.1 ? "mediumDashed" : "dashed",
                "dot-dash" or "dash-dot" => width > 1.1 ? "mediumDashDot" : "dashDot",
                "dot-dot-dash" or "dash-dot-dot" => width > 1.1 ? "mediumDashDotDot" : "dashDotDot",
                _ => width < 0.3 ? "hair" : width <= 1.1 ? "thin" : width <= 2.1 ? "medium" : "thick",
            };
            return new CellBorder(style, color);
        }
    }

    /// <summary>"#1f4e79" → "#1F4E79"; null for anything else.</summary>
    private static string? Color(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit) ? value.ToUpperInvariant() : null;

    /// <summary>An ODF length ("2.258cm", "0.889in", "15pt") in points; null for percentages and garbage.</summary>
    internal static double? Points(string? length)
    {
        if (string.IsNullOrEmpty(length))
            return null;
        var unit = length.TrimStart('-', '+', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (!double.TryParse(length.AsSpan(0, length.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return unit switch
        {
            "pt" => value,
            "in" => value * 72,
            "cm" => value * 72 / 2.54,
            "mm" => value * 72 / 25.4,
            "pc" => value * 12,
            "px" => value * 0.75,
            _ => null,
        };
    }

    // ───────────────────────── Settings ─────────────────────────

    /// <summary>Frozen rows per sheet (the view settings Calc saves in settings.xml).</summary>
    private void ReadSettings(XDocument? settings)
    {
        var tables = settings?.Descendants(Config + "config-item-map-named").FirstOrDefault(m => (string?)m.Attribute(Config + "name") == "Tables");
        foreach (var entry in tables?.Elements(Config + "config-item-map-entry") ?? [])
        {
            string? Item(string name) => (string?)entry.Elements(Config + "config-item").FirstOrDefault(i => (string?)i.Attribute(Config + "name") == name);
            if (Item("VerticalSplitMode") == "2" && int.TryParse(Item("VerticalSplitPosition"), out var rows) && rows > 0 && (string?)entry.Attribute(Config + "name") is { } sheet)
                _frozenRows[sheet] = rows;
        }
    }

    // ───────────────────────── Sheets ─────────────────────────

    private sealed record Span(int Start, int Count, double? Size, bool Hidden, string? DefaultStyle);

    private sealed record CellRun(int Column, int Count, SheetCell? Cell, int ColumnSpan, int RowSpan);

    private Worksheet ReadTable(XElement table)
    {
        var name = (string?)table.Attribute(Table + "name") ?? "Sheet";
        var sheet = new Worksheet(name);

        var columns = new List<Span>();
        var c = 0;
        foreach (var (column, _) in Children(table, "table-column", "table-header-columns", "table-column-group", "table-columns"))
        {
            if (c >= MaxColumns)
                break;
            var count = Math.Min(Repeat(column, "number-columns-repeated"), MaxColumns - c);
            var width = Points((string?)Find("table-column", (string?)column.Attribute(Table + "style-name"))?.Element(Style + "table-column-properties")?.Attribute(Style + "column-width"));
            columns.Add(new Span(c, count, width / PointsPerCharacter, IsCollapsed(column), (string?)column.Attribute(Table + "default-cell-style-name")));
            c += count;
        }

        var rows = new List<Span>();
        var r = 0;
        var headerRows = 0;
        foreach (var (row, header) in Children(table, "table-row", "table-header-rows", "table-row-group", "table-rows"))
        {
            if (r >= MaxRows)
                break;
            var count = Math.Min(Repeat(row, "number-rows-repeated"), MaxRows - r);
            var properties = Find("table-row", (string?)row.Attribute(Table + "style-name"))?.Element(Style + "table-row-properties");
            // Calc saves every row's height; only rows not sized to their content have a custom one.
            var height = (string?)properties?.Attribute(Style + "use-optimal-row-height") == "true" ? null : Points((string?)properties?.Attribute(Style + "row-height"));
            rows.Add(new Span(r, count, height, IsCollapsed(row), null));
            if (header && headerRows == r)
                headerRows += count;

            var cells = Cells(row, columns);
            var hasText = cells.Any(run => run.Cell is { Text.Length: > 0 } || run.ColumnSpan > 1 || run.RowSpan > 1);
            var copies = hasText ? count : cells.Any(run => run.Cell is not null) && count <= MaxStyledRepeat ? count : 0;
            for (var copy = 0; copy < copies; copy++)
            {
                foreach (var run in cells)
                {
                    for (var i = 0; i < run.Count; i++)
                    {
                        if (run.Cell is not null)
                            sheet.Set(r + copy, run.Column + i, run.Cell);
                        if (run.ColumnSpan > 1 || run.RowSpan > 1)
                            sheet.Merges.Add(new CellRange(r + copy, run.Column + i, r + copy + run.RowSpan - 1, run.Column + i + run.ColumnSpan - 1));
                    }
                }
            }
            r += count;
        }

        // Widths, heights and hidden flags only matter where the sheet has something; the repeats reach the
        // last row and column of the sheet.
        var used = sheet.UsedRange();
        var right = Math.Max(used?.Right ?? -1, sheet.Merges.Count > 0 ? sheet.Merges.Max(m => m.Right) : -1);
        var bottom = Math.Max(used?.Bottom ?? -1, sheet.Merges.Count > 0 ? sheet.Merges.Max(m => m.Bottom) : -1);
        foreach (var span in columns)
        {
            for (var i = span.Start; i < span.Start + span.Count && i <= right; i++)
            {
                if (span.Size is { } width)
                    sheet.ColumnWidths[i] = Math.Round(width, 4);
                if (span.Hidden)
                    sheet.HiddenColumns.Add(i);
            }
            if (span.Size is { } rest && span.Start <= right + 1 && right + 1 < span.Start + span.Count)
                sheet.DefaultColumnWidth = Math.Round(rest, 4);
        }
        foreach (var span in rows)
        {
            for (var i = span.Start; i < span.Start + span.Count && i <= bottom; i++)
            {
                if (span.Size is { } height)
                    sheet.RowHeights[i] = Math.Round(height, 2);
                if (span.Hidden)
                    sheet.HiddenRows.Add(i);
            }
        }
        sheet.FrozenRows = _frozenRows.TryGetValue(name, out var frozen) ? frozen : headerRows;
        return sheet;
    }

    /// <summary>Rows (or columns) of a table in order, also inside groups; the flag marks header rows.</summary>
    private static IEnumerable<(XElement Element, bool Header)> Children(XElement parent, string item, string header, params string[] groups)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name == Table + item)
                yield return (element, false);
            else if (element.Name == Table + header)
            {
                foreach (var (child, _) in Children(element, item, header, groups))
                    yield return (child, true);
            }
            else if (groups.Any(g => element.Name == Table + g))
            {
                foreach (var child in Children(element, item, header, groups))
                    yield return child;
            }
        }
    }

    private static int Repeat(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(Table + attribute), out var count) && count > 0 ? count : 1;

    private static bool IsCollapsed(XElement element) => (string?)element.Attribute(Table + "visibility") is "collapse" or "filter";

    /// <summary>The cells of one row element as runs of repeated cells; covered cells (inside a merge) stay empty.</summary>
    private List<CellRun> Cells(XElement row, List<Span> columns)
    {
        var runs = new List<CellRun>();
        var rowDefault = (string?)row.Attribute(Table + "default-cell-style-name");
        var c = 0;
        foreach (var element in row.Elements())
        {
            var covered = element.Name == Table + "covered-table-cell";
            if (!covered && element.Name != Table + "table-cell")
                continue;
            if (c >= MaxColumns)
                break;
            var count = Math.Min(Repeat(element, "number-columns-repeated"), MaxColumns - c);
            if (!covered)
            {
                var styleName = (string?)element.Attribute(Table + "style-name") ?? rowDefault ?? columns.FirstOrDefault(s => s.Start <= c && c < s.Start + s.Count)?.DefaultStyle;
                var cell = Cell(element, styleName);
                if (cell is { HasContent: false } || cell is { Text.Length: 0 } && count > MaxStyledRepeat)
                    cell = null;
                var columnSpan = Repeat(element, "number-columns-spanned");
                var rowSpan = Repeat(element, "number-rows-spanned");
                if (cell is not null || columnSpan > 1 || rowSpan > 1)
                    runs.Add(new CellRun(c, count, cell, columnSpan, rowSpan));
            }
            c += count;
        }
        return runs;
    }

    private SheetCell Cell(XElement element, string? styleName)
    {
        var (style, dataStyle) = CellStyleOf(styleName);
        var type = (string?)element.Attribute(Office + "value-type");
        double? value = null;
        switch (type)
        {
            case "float" or "percentage" or "currency":
                if (double.TryParse((string?)element.Attribute(Office + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    value = number;
                break;
            case "date":
                if (DateTimeOffset.TryParse((string?)element.Attribute(Office + "date-value"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    value = NumberFormatter.ToSerial(date.DateTime);
                break;
            case "time":
                try
                {
                    value = XmlConvert.ToTimeSpan((string?)element.Attribute(Office + "time-value") ?? "").TotalDays;
                }
                catch (FormatException) { }
                break;
            case "boolean":
                value = (string?)element.Attribute(Office + "boolean-value") is "true" or "1" ? 1 : 0;
                break;
        }

        var text = Paragraphs(element);
        if (type == "boolean")
            return new SheetCell(text ?? (value == 1 ? "TRUE" : "FALSE"), style) { Value = value, IsBoolean = true };
        if (value is not { } v)
            return new SheetCell(text ?? (string?)element.Attribute(Office + "string-value") ?? "", style);

        var format = FormatOf(dataStyle) ?? type switch
        {
            "date" => v == Math.Floor(v) ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss",
            "time" => "hh:mm:ss",
            "percentage" => "0%",
            _ => null,
        };
        format = NumberFormatter.IsGeneral(format) ? null : format;
        // text:p is what Calc showed when it saved; files written by other programs may leave it out.
        return new SheetCell(text ?? _formatter.Format(v, format), style, IsNumber: true) { Value = v, NumberFormat = format };
    }

    /// <summary>The text of a cell: its paragraphs joined by line breaks; null when it has none.</summary>
    private static string? Paragraphs(XElement cell)
    {
        var paragraphs = cell.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h").ToList();
        if (paragraphs.Count == 0)
            return null;
        var sb = new StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            Inline(paragraphs[i], sb);
        }
        return sb.ToString();
    }

    private static void Inline(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                // ODF collapses white space in text; extra spaces are text:s elements.
                foreach (var ch in text.Value)
                {
                    var c = ch is '\t' or '\r' or '\n' ? ' ' : ch;
                    if (c != ' ' || sb.Length == 0 || sb[^1] != ' ')
                        sb.Append(c);
                }
            }
            else if (node is XElement child)
            {
                if (child.Name == Text + "s")
                    sb.Append(' ', Repeat(child, Text + "c"));
                else if (child.Name == Text + "tab")
                    sb.Append('\t');
                else if (child.Name == Text + "line-break")
                    sb.Append('\n');
                else if (child.Name != Office + "annotation" && child.Name != Text + "note")
                    Inline(child, sb);
            }
        }
    }

    private static int Repeat(XElement element, XName attribute) =>
        int.TryParse((string?)element.Attribute(attribute), out var count) && count > 0 ? count : 1;
}
