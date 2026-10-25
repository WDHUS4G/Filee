// Spreadsheet print metadata and an immutable export plan for the interactive XLSX → PDF studio. The source
// workbook is never edited: SpreadsheetPrintPackage applies one plan to a temporary copy for LibreOffice to render.

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Sheets;

public enum SpreadsheetPageOrientation
{
    Source,
    Portrait,
    Landscape,
}

public enum SpreadsheetScaleMode
{
    Source,
    Percent,
    FitToPages,
}

public enum SpreadsheetPageOrder
{
    Source,
    DownThenOver,
    OverThenDown,
}

/// <summary>A standard paper size understood by both SpreadsheetML and LibreOffice.</summary>
public sealed record SpreadsheetPaper(string Id, string Name, int OoxmlCode, double WidthMm, double HeightMm)
{
    public static readonly SpreadsheetPaper Source = new("source", "From workbook", 0, 0, 0);
    public static readonly SpreadsheetPaper A5 = new("a5", "A5", 11, 148, 210);
    public static readonly SpreadsheetPaper A4 = new("a4", "A4", 9, 210, 297);
    public static readonly SpreadsheetPaper A3 = new("a3", "A3", 8, 297, 420);
    public static readonly SpreadsheetPaper Letter = new("letter", "Letter", 1, 215.9, 279.4);
    public static readonly SpreadsheetPaper Legal = new("legal", "Legal", 5, 215.9, 355.6);
    public static readonly SpreadsheetPaper Tabloid = new("tabloid", "Tabloid", 3, 279.4, 431.8);

    public static IReadOnlyList<SpreadsheetPaper> All { get; } = [Source, A5, A4, A3, Letter, Legal, Tabloid];

    public static SpreadsheetPaper FromCode(int? code) => All.FirstOrDefault(p => p.OoxmlCode == code) ?? Source;
}

/// <summary>Page settings found in the source sheet.</summary>
public sealed record SpreadsheetSourcePageSetup(
    SpreadsheetPaper Paper,
    SpreadsheetPageOrientation Orientation,
    int? ScalePercent,
    int? FitWidth,
    int? FitHeight,
    SpreadsheetPageOrder Order,
    double MarginLeftMm,
    double MarginRightMm,
    double MarginTopMm,
    double MarginBottomMm);

/// <summary>One worksheet as the studio sees it, including settings which explain a surprising PDF.</summary>
public sealed record SpreadsheetSheetInfo(
    int Index,
    string Name,
    string PartPath,
    bool IsHidden,
    string UsedRange,
    string? PrintArea,
    string? RepeatRows,
    int UsedRows,
    int UsedColumns,
    int HiddenRows,
    int HiddenColumns,
    SpreadsheetSourcePageSetup PageSetup)
{
    public string InitialRange => PrintArea ?? UsedRange;

    public bool HasSuspiciousScale => PageSetup.ScalePercent is > 0 and <= 25;

    public bool HasWideUnboundedRange => PrintArea is null && UsedColumns > 60;
}

/// <summary>Workbook metadata needed by the print studio.</summary>
public sealed record SpreadsheetWorkbookInfo(string Path, string WorkbookPartPath, IReadOnlyList<SpreadsheetSheetInfo> Sheets);

/// <summary>Settings for one independently rendered worksheet region.</summary>
public sealed record SpreadsheetPrintRegion(
    SpreadsheetSheetInfo Sheet,
    string Range,
    SpreadsheetPaper Paper,
    SpreadsheetPageOrientation Orientation,
    SpreadsheetScaleMode ScaleMode,
    int ScalePercent,
    int FitWidth,
    int FitHeight,
    SpreadsheetPageOrder Order,
    double MarginLeftMm,
    double MarginRightMm,
    double MarginTopMm,
    double MarginBottomMm,
    bool IncludeHidden,
    bool KeepManualBreaks,
    bool WritePrintArea)
{
    public static SpreadsheetPrintRegion FromSource(SpreadsheetSheetInfo sheet) => new(
        sheet,
        sheet.InitialRange,
        sheet.PageSetup.Paper,
        sheet.PageSetup.Orientation,
        SpreadsheetScaleMode.Source,
        sheet.PageSetup.ScalePercent ?? 100,
        Math.Max(1, sheet.PageSetup.FitWidth ?? 1),
        Math.Max(0, sheet.PageSetup.FitHeight ?? 0),
        sheet.PageSetup.Order,
        sheet.PageSetup.MarginLeftMm,
        sheet.PageSetup.MarginRightMm,
        sheet.PageSetup.MarginTopMm,
        sheet.PageSetup.MarginBottomMm,
        IncludeHidden: false,
        KeepManualBreaks: true,
        WritePrintArea: sheet.PrintArea is not null);
}

/// <summary>Reads worksheet names, content bounds and print settings without opening Excel or LibreOffice.</summary>
public static partial class SpreadsheetPrintInspector
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static SpreadsheetWorkbookInfo Read(string path)
    {
        using var package = new OpcPackage(path);
        using var archive = ZipFile.OpenRead(path);
        var workbookPath = package.MainPartPath ?? "xl/workbook.xml";
        var workbook = package.Part(workbookPath)?.Root
            ?? throw new InvalidDataException("The file is not an Excel workbook (no xl/workbook.xml).");
        var names = workbook.Element(S + "definedNames")?.Elements(S + "definedName").ToList() ?? [];
        var result = new List<SpreadsheetSheetInfo>();
        var index = -1;
        foreach (var sheet in workbook.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            index++;
            var target = package.Target(workbookPath, (string?)sheet.Attribute(R + "id"));
            if (target is null || archive.GetEntry(target) is not { } entry)
                continue;
            var xml = ReadWorksheet(entry);
            var name = (string?)sheet.Attribute("name") ?? $"Sheet {index + 1}";
            var printArea = DefinedRange(names, index, "_xlnm.Print_Area");
            var repeatRows = DefinedRange(names, index, "_xlnm.Print_Titles", rowsOnly: true);
            var scale = xml.Scale;
            var fitWidth = xml.FitWidth;
            var fitHeight = xml.FitHeight;
            var orientation = xml.Orientation switch
            {
                "portrait" => SpreadsheetPageOrientation.Portrait,
                "landscape" => SpreadsheetPageOrientation.Landscape,
                _ => SpreadsheetPageOrientation.Source,
            };
            var order = xml.PageOrder switch
            {
                "downThenOver" => SpreadsheetPageOrder.DownThenOver,
                "overThenDown" => SpreadsheetPageOrder.OverThenDown,
                _ => SpreadsheetPageOrder.Source,
            };
            result.Add(new SpreadsheetSheetInfo(
                index,
                name,
                target,
                (string?)sheet.Attribute("state") is "hidden" or "veryHidden",
                xml.Reference,
                printArea,
                repeatRows,
                xml.Rows,
                xml.Columns,
                xml.HiddenRows,
                xml.HiddenColumns,
                new SpreadsheetSourcePageSetup(
                    SpreadsheetPaper.FromCode(xml.PaperSize),
                    orientation,
                    scale,
                    fitWidth,
                    fitHeight,
                    order,
                    xml.MarginLeftInches * 25.4,
                    xml.MarginRightInches * 25.4,
                    xml.MarginTopInches * 25.4,
                    xml.MarginBottomInches * 25.4)));
        }
        return new SpreadsheetWorkbookInfo(path, workbookPath, result);
    }

    /// <summary>Whether a comma-separated A1 range can safely become an Excel print area.</summary>
    public static bool IsValidRange(string? range) => !string.IsNullOrWhiteSpace(range)
        && SplitAreas(range).All(part =>
        {
            var value = part.Trim().Replace("$", "", StringComparison.Ordinal);
            var cells = value.Split(':');
            return cells.Length is 1 or 2 && cells.All(cell => TryCell(cell, out _, out _))
                || cells.Length == 2 && cells.All(TryColumn)
                || cells.Length == 2 && cells.All(cell => RowPattern().IsMatch(cell)
                    && int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var row)
                    && row is >= 1 and <= 1_048_576);
        });

    private static string? DefinedRange(List<XElement> names, int sheetIndex, string kind, bool rowsOnly = false)
    {
        var value = names.FirstOrDefault(n => (string?)n.Attribute("name") == kind
            && (string?)n.Attribute("localSheetId") == sheetIndex.ToString(CultureInfo.InvariantCulture))?.Value;
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var ranges = SplitAreas(value).Select(part =>
            part[(part.LastIndexOf('!') + 1)..].Replace("$", "", StringComparison.Ordinal).Trim());
        if (rowsOnly)
            ranges = ranges.Where(part => RowRangePattern().IsMatch(part));
        var joined = string.Join(',', ranges);
        return joined.Length > 0 ? joined : null;
    }

    private static WorksheetSummary ReadWorksheet(ZipArchiveEntry entry)
    {
        var top = int.MaxValue;
        var left = int.MaxValue;
        var bottom = -1;
        var right = -1;
        var hiddenRows = 0;
        var hiddenColumns = 0;
        var merges = new List<string>();
        int? paperSize = null, scale = null, fitWidth = null, fitHeight = null;
        string? orientation = null, pageOrder = null;
        var marginLeft = 0.7;
        var marginRight = 0.7;
        var marginTop = 0.75;
        var marginBottom = 0.75;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != S.NamespaceName)
                continue;
            switch (reader.LocalName)
            {
                case "row" when On(reader.GetAttribute("hidden")):
                    hiddenRows++;
                    break;
                case "col" when On(reader.GetAttribute("hidden")):
                    var min = Math.Max(1, Int(reader.GetAttribute("min")) ?? 1);
                    var max = Math.Min(16384, Int(reader.GetAttribute("max")) ?? min);
                    hiddenColumns += Math.Max(0, max - min + 1);
                    break;
                case "c":
                    var reference = reader.GetAttribute("r");
                    var hasContent = false;
                    using (var cell = reader.ReadSubtree())
                    {
                        while (cell.Read())
                        {
                            if (cell.NodeType == XmlNodeType.Element && cell.NamespaceURI == S.NamespaceName
                                && cell.LocalName is "v" or "is" or "f")
                            {
                                hasContent = true;
                                break;
                            }
                        }
                    }
                    // A styled empty cell can push Excel's dimension to XFD1048576.
                    if (hasContent && TryCell(reference, out var row, out var column))
                    {
                        top = Math.Min(top, row);
                        left = Math.Min(left, column);
                        bottom = Math.Max(bottom, row);
                        right = Math.Max(right, column);
                    }
                    break;
                case "mergeCell":
                    if (reader.GetAttribute("ref") is { } merge)
                        merges.Add(merge);
                    break;
                case "pageSetup":
                    paperSize = Int(reader.GetAttribute("paperSize"));
                    scale = Int(reader.GetAttribute("scale"));
                    fitWidth = Int(reader.GetAttribute("fitToWidth"));
                    fitHeight = Int(reader.GetAttribute("fitToHeight"));
                    orientation = reader.GetAttribute("orientation");
                    pageOrder = reader.GetAttribute("pageOrder");
                    break;
                case "pageMargins":
                    marginLeft = Double(reader.GetAttribute("left")) ?? marginLeft;
                    marginRight = Double(reader.GetAttribute("right")) ?? marginRight;
                    marginTop = Double(reader.GetAttribute("top")) ?? marginTop;
                    marginBottom = Double(reader.GetAttribute("bottom")) ?? marginBottom;
                    break;
            }
        }
        foreach (var merge in merges)
        {
            var parts = merge.Split(':');
            if (parts.Length != 2 || !TryCell(parts[0], out var r1, out var c1) || !TryCell(parts[1], out var r2, out var c2))
                continue;
            if (bottom >= 0 && r1 >= top && c1 >= left && r1 <= bottom && c1 <= right)
            {
                bottom = Math.Max(bottom, r2);
                right = Math.Max(right, c2);
            }
        }
        if (bottom < 0)
            return new("A1", 0, 0, hiddenRows, hiddenColumns, paperSize, scale, fitWidth, fitHeight,
                orientation, pageOrder, marginLeft, marginRight, marginTop, marginBottom);
        return new($"{Cell(left, top)}:{Cell(right, bottom)}", bottom - top + 1, right - left + 1,
            hiddenRows, hiddenColumns, paperSize, scale, fitWidth, fitHeight, orientation, pageOrder,
            marginLeft, marginRight, marginTop, marginBottom);
    }

    internal static IEnumerable<string> SplitAreas(string value)
    {
        var start = 0;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
            {
                index++;
                continue;
            }
            if (value[index] == '\'')
                quoted = !quoted;
            else if (value[index] == ',' && !quoted)
            {
                yield return value[start..index];
                start = index + 1;
            }
        }
        yield return value[start..];
    }

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? Double(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static bool On(string? value) => value is "1" or "true";

    private static bool TryCell(string? reference, out int row, out int column)
    {
        row = column = -1;
        if (string.IsNullOrWhiteSpace(reference))
            return false;
        var match = CellPattern().Match(reference.Replace("$", "", StringComparison.Ordinal));
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var number) || number is < 1 or > 1_048_576)
            return false;
        var col = 0;
        foreach (var ch in match.Groups[1].Value)
        {
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            if (col > 16_384)
                return false;
        }
        row = number - 1;
        column = col - 1;
        return true;
    }

    private static bool TryColumn(string value)
    {
        if (!ColumnPattern().IsMatch(value))
            return false;
        var column = 0;
        foreach (var ch in value)
            column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return column <= 16_384;
    }

    private static string Cell(int column, int row)
    {
        var letters = "";
        for (var value = column + 1; value > 0; value = (value - 1) / 26)
            letters = (char)('A' + (value - 1) % 26) + letters;
        return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^([A-Za-z]+)(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"^\d+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex RowRangePattern();

    [GeneratedRegex(@"^[A-Za-z]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnPattern();

    [GeneratedRegex(@"^\d{1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();

    private sealed record WorksheetSummary(string Reference, int Rows, int Columns, int HiddenRows, int HiddenColumns,
        int? PaperSize, int? Scale, int? FitWidth, int? FitHeight, string? Orientation, string? PageOrder,
        double MarginLeftInches, double MarginRightInches, double MarginTopInches, double MarginBottomInches);
}

/// <summary>Writes a temporary XLSX with one printable sheet and one region, preserving every other package part.</summary>
public static partial class SpreadsheetPrintPackage
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const double MillimetresPerInch = 25.4;

    public static void Create(string source, string destination, SpreadsheetWorkbookInfo workbook,
        SpreadsheetPrintRegion region, CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        if (!SpreadsheetPrintInspector.IsValidRange(region.Range))
            throw new ArgumentException("The print area is not a valid A1 cell range.", nameof(region));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = ZipFile.OpenRead(source);
        using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            var written = output.CreateEntry(entry.FullName, compressionLevel);
            written.LastWriteTime = entry.LastWriteTime;
            using var to = written.Open();
            if (entry.FullName.Equals(workbook.WorkbookPartPath, StringComparison.OrdinalIgnoreCase))
                RewriteWorkbook(entry, to, region);
            else if (entry.FullName.Equals(region.Sheet.PartPath, StringComparison.OrdinalIgnoreCase))
                RewriteSheet(entry, to, region);
            else if (workbook.Sheets.Any(sheet => entry.FullName.Equals(sheet.PartPath, StringComparison.OrdinalIgnoreCase)))
                RewriteSheetSelection(entry, to, selected: false);
            else
            {
                using var from = entry.Open();
                from.CopyTo(to);
            }
        }
    }

    private static void RewriteWorkbook(ZipArchiveEntry entry, Stream output, SpreadsheetPrintRegion region)
    {
        var document = Load(entry);
        var root = document.Root!;
        var sheets = root.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? [];
        if (region.Sheet.Index < 0 || region.Sheet.Index >= sheets.Count)
            throw new InvalidDataException("The selected worksheet is no longer in the workbook.");
        var selected = sheets[region.Sheet.Index];
        selected.Attribute("state")?.Remove();
        foreach (var sheet in sheets.Where(sheet => sheet != selected))
            sheet.SetAttributeValue("state", "hidden");
        var bookViews = root.Element(S + "bookViews");
        if (bookViews is null)
        {
            bookViews = new XElement(S + "bookViews");
            root.Element(S + "sheets")?.AddBeforeSelf(bookViews);
        }
        var view = bookViews.Elements(S + "workbookView").FirstOrDefault();
        if (view is null)
        {
            view = new XElement(S + "workbookView");
            bookViews.Add(view);
        }
        view.SetAttributeValue("activeTab", region.Sheet.Index);
        view.SetAttributeValue("firstSheet", region.Sheet.Index);

        var defined = root.Element(S + "definedNames");
        defined?.Elements(S + "definedName").Where(name =>
            (string?)name.Attribute("name") == "_xlnm.Print_Area"
            && (string?)name.Attribute("localSheetId") == region.Sheet.Index.ToString(CultureInfo.InvariantCulture)).Remove();
        if (region.WritePrintArea)
        {
            if (defined is null)
            {
                defined = new XElement(S + "definedNames");
                root.Element(S + "sheets")?.AddAfterSelf(defined);
            }
            var escaped = region.Sheet.Name.Replace("'", "''", StringComparison.Ordinal);
            var qualified = string.Join(',', SpreadsheetPrintInspector.SplitAreas(region.Range).Select(part =>
                $"'{escaped}'!{AbsoluteRange(part)}"));
            defined.Add(new XElement(S + "definedName",
                new XAttribute("name", "_xlnm.Print_Area"),
                new XAttribute("localSheetId", region.Sheet.Index),
                qualified));
        }
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void RewriteSheetSelection(ZipArchiveEntry entry, Stream output, bool selected)
    {
        var document = Load(entry);
        SetSheetSelected(document.Root!, selected);
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void RewriteSheet(ZipArchiveEntry entry, Stream output, SpreadsheetPrintRegion region)
    {
        var document = Load(entry);
        var root = document.Root!;
        SetSheetSelected(root, selected: true);
        if (region.IncludeHidden)
        {
            foreach (var row in root.Element(S + "sheetData")?.Elements(S + "row") ?? [])
                row.Attribute("hidden")?.Remove();
            foreach (var column in root.Element(S + "cols")?.Elements(S + "col") ?? [])
                column.Attribute("hidden")?.Remove();
        }
        if (!region.KeepManualBreaks)
        {
            root.Element(S + "rowBreaks")?.Remove();
            root.Element(S + "colBreaks")?.Remove();
        }

        var margins = Ensure(root, "pageMargins");
        SetNumber(margins, "left", region.MarginLeftMm / MillimetresPerInch);
        SetNumber(margins, "right", region.MarginRightMm / MillimetresPerInch);
        SetNumber(margins, "top", region.MarginTopMm / MillimetresPerInch);
        SetNumber(margins, "bottom", region.MarginBottomMm / MillimetresPerInch);
        if (margins.Attribute("header") is null) margins.SetAttributeValue("header", "0.3");
        if (margins.Attribute("footer") is null) margins.SetAttributeValue("footer", "0.3");

        var setup = Ensure(root, "pageSetup");
        // A cached printerSettings relationship can carry the old paper/orientation into LibreOffice for the first
        // page even after the OOXML attributes below changed. It is printer-specific and safe to drop in the copy.
        if (region.Paper != SpreadsheetPaper.Source || region.Orientation != SpreadsheetPageOrientation.Source
            || region.ScaleMode != SpreadsheetScaleMode.Source || region.Order != SpreadsheetPageOrder.Source)
            setup.Attributes().Where(attribute => attribute.Name.LocalName == "id").Remove();
        if (region.Paper != SpreadsheetPaper.Source)
            setup.SetAttributeValue("paperSize", region.Paper.OoxmlCode);
        SetEnum(setup, "orientation", region.Orientation, SpreadsheetPageOrientation.Source,
            (SpreadsheetPageOrientation.Portrait, "portrait"), (SpreadsheetPageOrientation.Landscape, "landscape"));
        SetEnum(setup, "pageOrder", region.Order, SpreadsheetPageOrder.Source,
            (SpreadsheetPageOrder.DownThenOver, "downThenOver"), (SpreadsheetPageOrder.OverThenDown, "overThenDown"));

        var properties = root.Element(S + "sheetPr") ?? new XElement(S + "sheetPr");
        if (properties.Parent is null)
            root.AddFirst(properties);
        var pageProperties = properties.Element(S + "pageSetUpPr") ?? new XElement(S + "pageSetUpPr");
        if (pageProperties.Parent is null)
            properties.Add(pageProperties);
        switch (region.ScaleMode)
        {
            case SpreadsheetScaleMode.Percent:
                setup.SetAttributeValue("scale", Math.Clamp(region.ScalePercent, 10, 400));
                setup.Attribute("fitToWidth")?.Remove();
                setup.Attribute("fitToHeight")?.Remove();
                pageProperties.SetAttributeValue("fitToPage", "0");
                break;
            case SpreadsheetScaleMode.FitToPages:
                setup.Attribute("scale")?.Remove();
                setup.SetAttributeValue("fitToWidth", Math.Clamp(region.FitWidth, 1, 100));
                setup.SetAttributeValue("fitToHeight", Math.Clamp(region.FitHeight, 0, 100));
                pageProperties.SetAttributeValue("fitToPage", "1");
                break;
        }
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static void SetSheetSelected(XElement root, bool selected)
    {
        foreach (var view in root.Element(S + "sheetViews")?.Elements(S + "sheetView") ?? [])
        {
            if (selected)
                view.SetAttributeValue("tabSelected", "1");
            else
                view.Attribute("tabSelected")?.Remove();
        }
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static XElement Ensure(XElement root, string localName)
    {
        if (root.Element(S + localName) is { } existing)
            return existing;
        var element = new XElement(S + localName);
        if (localName == "pageMargins" && root.Element(S + "pageSetup") is { } setup)
        {
            setup.AddBeforeSelf(element);
            return element;
        }
        var after = localName == "pageMargins" ? "printOptions" : "pageMargins";
        if (root.Element(S + after) is { } previous)
            previous.AddAfterSelf(element);
        else
            root.Add(element);
        return element;
    }

    private static void SetNumber(XElement element, string name, double value) =>
        element.SetAttributeValue(name, value.ToString("0.####", CultureInfo.InvariantCulture));

    private static string AbsoluteRange(string range) => string.Join(':',
        range.Replace("$", "", StringComparison.Ordinal).Trim().Split(':').Select(value =>
        {
            if (ColumnPattern().IsMatch(value)) return $"${value.ToUpperInvariant()}";
            if (RowPattern().IsMatch(value)) return $"${value}";
            var split = value.IndexOfAny("0123456789".ToCharArray());
            return split > 0 ? $"${value[..split].ToUpperInvariant()}${value[split..]}" : value;
        }));

    [GeneratedRegex(@"^[A-Za-z]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnPattern();

    [GeneratedRegex(@"^\d{1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();

    private static void SetEnum<T>(XElement element, string name, T value, T source, params (T Value, string Text)[] choices)
        where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(value, source))
            return;
        element.SetAttributeValue(name, choices.First(choice => EqualityComparer<T>.Default.Equals(choice.Value, value)).Text);
    }
}
