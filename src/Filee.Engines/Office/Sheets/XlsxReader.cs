// XLSX (SpreadsheetML) → Workbook, read directly (no Excel, no LibreOffice): cell values formatted the way Excel
// shows them (number formats through ExcelNumberFormat, MIT), fonts, fills, borders, alignment, merged cells,
// column widths, custom row heights, hidden rows / columns / sheets and frozen header rows.
// Charts, pictures and conditional formatting are not read.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Sheets;

internal sealed class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Days between the 1900 and 1904 date systems (1 January 1904 is serial 1462 in the 1900 system).</summary>
    private const double Date1904Offset = 1462;

    private readonly OpcPackage _package;
    private readonly List<string> _sharedStrings = [];
    private readonly List<(CellStyle Style, string Format)> _cellFormats = [];
    private NumberFormatter _formatter = new();
    private bool _date1904;

    private XlsxReader(OpcPackage package) => _package = package;

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        using var package = new OpcPackage(path);
        return new XlsxReader(package).ReadWorkbook();
    }

    private Workbook ReadWorkbook()
    {
        var workbookPath = _package.MainPartPath ?? "xl/workbook.xml";
        var workbook = _package.Part(workbookPath)?.Root ?? throw new InvalidDataException("The file is not an Excel workbook (no xl/workbook.xml).");
        _date1904 = (string?)workbook.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";
        _formatter = new NumberFormatter(_date1904);

        var colors = OoxmlColors.FromTheme(_package.TargetOfType(workbookPath, "/theme") is { } theme ? _package.Part(theme) : null);
        if (_package.TargetOfType(workbookPath, "/sharedStrings") is { } shared)
            ReadSharedStrings(_package.Part(shared));
        if (_package.TargetOfType(workbookPath, "/styles") is { } styles)
            ReadStyles(_package.Part(styles), colors);

        var result = new Workbook();
        foreach (var sheet in workbook.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden")
                continue;
            var target = _package.Target(workbookPath, (string?)sheet.Attribute(R + "id"));
            if (target is null || _package.Part(target)?.Root is not { } xml || xml.Name != S + "worksheet")
                continue; // chart sheets and dialog sheets have no cells
            result.Sheets.Add(ReadSheet((string?)sheet.Attribute("name") ?? "Sheet", xml));
        }
        return result;
    }

    private void ReadSharedStrings(XDocument? xml)
    {
        foreach (var item in xml?.Root?.Elements(S + "si") ?? [])
            _sharedStrings.Add(RichText(item));
    }

    /// <summary>Text of a shared or inline string: plain &lt;t&gt; or rich text runs; phonetic hints skipped.</summary>
    private static string RichText(XElement item)
    {
        if (item.Element(S + "t") is { } plain)
            return plain.Value;
        var sb = new StringBuilder();
        foreach (var run in item.Elements(S + "r"))
            sb.Append(run.Element(S + "t")?.Value);
        return sb.ToString();
    }

    // ───────────────────────── Styles ─────────────────────────

    private void ReadStyles(XDocument? xml, OoxmlColors colors)
    {
        var root = xml?.Root;
        if (root is null)
            return;
        var formats = root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => int.TryParse((string?)f.Attribute("numFmtId"), out _))
            .ToDictionary(f => int.Parse((string)f.Attribute("numFmtId")!, CultureInfo.InvariantCulture), f => (string?)f.Attribute("formatCode") ?? "General")
            ?? [];

        var fonts = (root.Element(S + "fonts")?.Elements(S + "font") ?? []).Select(f => new
        {
            Bold = On(f.Element(S + "b")),
            Italic = On(f.Element(S + "i")),
            Underline = f.Element(S + "u") is { } u && (string?)u.Attribute("val") != "none",
            Strike = On(f.Element(S + "strike")),
            Size = double.TryParse((string?)f.Element(S + "sz")?.Attribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : (double?)null,
            Color = colors.Spreadsheet(f.Element(S + "color")),
        }).ToList();

        var fills = (root.Element(S + "fills")?.Elements(S + "fill") ?? []).Select(f =>
        {
            var pattern = f.Element(S + "patternFill");
            var type = (string?)pattern?.Attribute("patternType");
            return type is null or "none" or "gray125" ? null
                : colors.Spreadsheet(pattern!.Element(S + "fgColor")) ?? colors.Spreadsheet(pattern.Element(S + "bgColor"));
        }).ToList();

        CellBorder Side(XElement? side) => side is null || (string?)side.Attribute("style") is not { } style
            ? CellBorder.None
            : new CellBorder(style, colors.Spreadsheet(side.Element(S + "color")));
        var borders = (root.Element(S + "borders")?.Elements(S + "border") ?? []).Select(b => (
            Left: Side(b.Element(S + "left") ?? b.Element(S + "start")),
            Right: Side(b.Element(S + "right") ?? b.Element(S + "end")),
            Top: Side(b.Element(S + "top")),
            Bottom: Side(b.Element(S + "bottom")))).ToList();

        foreach (var xf in root.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var font = Index(xf, "fontId") is var fi && fi < fonts.Count ? fonts[fi] : null;
            var fill = Index(xf, "fillId") is var fl && fl < fills.Count ? fills[fl] : null;
            var border = Index(xf, "borderId") is var bi && bi < borders.Count ? borders[bi] : (CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None);
            var alignment = xf.Element(S + "alignment");
            var style = new CellStyle
            {
                Bold = font?.Bold ?? false,
                Italic = font?.Italic ?? false,
                Underline = font?.Underline ?? false,
                Strike = font?.Strike ?? false,
                FontSize = font?.Size,
                Color = font?.Color,
                Fill = fill,
                Align = (string?)alignment?.Attribute("horizontal") switch
                {
                    "left" => CellAlign.Left,
                    "center" or "centerContinuous" => CellAlign.Center,
                    "right" => CellAlign.Right,
                    _ => CellAlign.General,
                },
                VerticalAlign = (string?)alignment?.Attribute("vertical") switch
                {
                    "top" => CellVerticalAlign.Top,
                    "center" => CellVerticalAlign.Center,
                    _ => CellVerticalAlign.Bottom,
                },
                Wrap = On(alignment?.Attribute("wrapText")),
                Left = border.Item1,
                Right = border.Item2,
                Top = border.Item3,
                Bottom = border.Item4,
            };
            var formatId = Index(xf, "numFmtId");
            _cellFormats.Add((style, formats.GetValueOrDefault(formatId) ?? NumberFormatter.BuiltInFormats.GetValueOrDefault(formatId, "General")));
        }
    }

    private static int Index(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), out var value) && value >= 0 ? value : 0;

    /// <summary>A boolean element or attribute: present without val, or val="1"/"true".</summary>
    private static bool On(XObject? node) => node switch
    {
        XElement element => (string?)element.Attribute("val") is null or "1" or "true",
        XAttribute attribute => attribute.Value is "1" or "true",
        _ => false,
    };

    // ───────────────────────── Sheets ─────────────────────────

    private Worksheet ReadSheet(string name, XElement xml)
    {
        var sheet = new Worksheet(name);
        if (double.TryParse((string?)xml.Element(S + "sheetFormatPr")?.Attribute("defaultColWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var defaultWidth))
            sheet.DefaultColumnWidth = defaultWidth;

        var pane = xml.Element(S + "sheetViews")?.Elements(S + "sheetView").FirstOrDefault()?.Element(S + "pane");
        if ((string?)pane?.Attribute("state") is "frozen" or "frozenSplit" && double.TryParse((string?)pane.Attribute("ySplit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var frozen))
            sheet.FrozenRows = (int)frozen;

        foreach (var col in xml.Element(S + "cols")?.Elements(S + "col") ?? [])
        {
            var min = Index(col, "min");
            var max = Math.Min(Index(col, "max"), min + 16384);
            var hasWidth = double.TryParse((string?)col.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width);
            for (var c = min; c <= max && c > 0; c++)
            {
                if (hasWidth)
                    sheet.ColumnWidths[c - 1] = width;
                if (On(col.Attribute("hidden")) || hasWidth && width == 0)
                    sheet.HiddenColumns.Add(c - 1);
            }
        }

        var nextRow = 0;
        foreach (var row in xml.Element(S + "sheetData")?.Elements(S + "row") ?? [])
        {
            var r = int.TryParse((string?)row.Attribute("r"), out var number) ? number - 1 : nextRow;
            nextRow = r + 1;
            if (On(row.Attribute("hidden")))
                sheet.HiddenRows.Add(r);
            if (On(row.Attribute("customHeight")) && double.TryParse((string?)row.Attribute("ht"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                sheet.RowHeights[r] = height;

            var nextColumn = 0;
            foreach (var cell in row.Elements(S + "c"))
            {
                var c = CellRange.TryParseCell((string?)cell.Attribute("r") ?? "", out _, out var column) ? column : nextColumn;
                nextColumn = c + 1;
                var (style, format) = Index(cell, "s") is var s && s < _cellFormats.Count ? _cellFormats[s] : (CellStyle.Default, "General");
                var value = Value(cell, style, format);
                if (value.HasContent)
                    sheet.Set(r, c, value);
            }
        }

        foreach (var merge in xml.Element(S + "mergeCells")?.Elements(S + "mergeCell") ?? [])
            if (CellRange.Parse((string?)merge.Attribute("ref")) is { } range && (range.Bottom > range.Top || range.Right > range.Left))
                sheet.Merges.Add(range);

        // <brk id="20"/>: the break is above 0-based row 20, which starts the next page.
        foreach (var brk in xml.Element(S + "rowBreaks")?.Elements(S + "brk") ?? [])
            if (int.TryParse((string?)brk.Attribute("id"), out var breakRow) && breakRow > 0)
                sheet.RowBreaks.Add(breakRow);
        return sheet;
    }

    /// <summary>
    /// The displayed text of a cell, and for numbers (dates included) and booleans the value behind it. Dates in a
    /// 1904-based workbook are moved to the 1900 date system, so the value means the same in every workbook.
    /// </summary>
    private SheetCell Value(XElement cell, CellStyle style, string format)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        var raw = cell.Element(S + "v")?.Value;
        switch (type)
        {
            case "s":
                return new SheetCell(int.TryParse(raw, out var index) && index >= 0 && index < _sharedStrings.Count ? _sharedStrings[index] : "", style);
            case "inlineStr":
                return new SheetCell(cell.Element(S + "is") is { } inline ? RichText(inline) : raw ?? "", style);
            case "str":
            case "e":
                return new SheetCell(raw ?? "", style);
            case "b":
                var on = raw is "1" or "true";
                return new SheetCell(on ? "TRUE" : "FALSE", style) { Value = on ? 1 : 0, IsBoolean = true };
            case "d":
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? Number(NumberFormatter.ToSerial(date) - (_date1904 ? Date1904Offset : 0), style, format)
                    : new SheetCell(raw ?? "", style);
            default:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? Number(number, style, format)
                    : new SheetCell(raw ?? "", style);
        }
    }

    /// <param name="value">The stored number, in the workbook's own date system.</param>
    private SheetCell Number(double value, CellStyle style, string format) => new(_formatter.Format(value, format), style, IsNumber: true)
    {
        Value = _date1904 && _formatter.IsDate(format) ? value + Date1904Offset : value,
        NumberFormat = NumberFormatter.IsGeneral(format) ? null : format,
    };
}
