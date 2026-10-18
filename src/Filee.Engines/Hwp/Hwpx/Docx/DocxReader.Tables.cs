// DOCX tables: grid column widths, merged cells (gridSpan / vMerge), borders from the table style, the table and
// each cell, cell shading, vertical alignment, row heights and repeated header rows.

using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    private HTable ReadTable(XElement tbl)
    {
        var tblPr = tbl.Element(W + "tblPr");
        var style = _styles.GetValueOrDefault(Val(tblPr?.Element(W + "tblStyle")) ?? _defaultTableStyle ?? "");
        var styleChain = StyleChain(style);

        var grid = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(c => HwpxUnits.FromTwips(Twips(c, "w") ?? 0)).ToArray() ?? [];
        var table = new HTable
        {
            ColumnWidths = grid.Length > 0 && grid.All(w => w > 0) ? grid : null,
            ColumnCount = grid.Length,
            Align = Val(tblPr?.Element(W + "jc")) switch
            {
                "center" => HAlign.Center,
                "right" or "end" => HAlign.Right,
                _ => HAlign.Left,
            },
            Indent = HwpxUnits.FromTwips(Twips(tblPr?.Element(W + "tblInd"), "w") ?? 0),
        };

        // Borders and cell margins: table style chain, then the table's own properties.
        var borders = new Dictionary<string, HBorder>();
        HInsets? margins = null;
        foreach (var properties in styleChain.Select(s => s.TblPr).Append(tblPr))
        {
            ApplyBorders(properties?.Element(W + "tblBorders"), borders);
            if (properties?.Element(W + "tblCellMar") is { } cellMar)
                margins = CellMargins(cellMar, margins);
        }
        table.CellMargin = margins;
        table.Borders = HBorders.Empty;

        // Header row look from the table style ("firstRow" conditional formatting), when the table enables it.
        var look = tblPr?.Element(W + "tblLook");
        var firstRowLook = Attr(look, "firstRow") is { } firstRow ? firstRow is "1" or "true"
            : int.TryParse(Val(look), System.Globalization.NumberStyles.HexNumber, null, out var mask) && (mask & 0x20) != 0;
        var firstRowStyle = firstRowLook
            ? styleChain.SelectMany(s => s.TableConditions).LastOrDefault(c => Attr(c, "type") == "firstRow")
            : null;

        var rows = tbl.Elements().SelectMany(e => e.Name == W + "tr" ? [e] : FlattenRows(e)).ToList();
        // vMerge: the cell that restarts a vertical merge, per grid column.
        var mergeOrigin = new Dictionary<int, HCell>();
        for (var r = 0; r < rows.Count; r++)
        {
            var tr = rows[r];
            var trPr = tr.Element(W + "trPr");
            var row = new HRow { Header = trPr?.Element(W + "tblHeader") is { } header && Val(header) is not ("0" or "false") };
            if (trPr?.Element(W + "trHeight") is { } height && Twips(height, "val") is > 0 and var h)
                row.Height = HwpxUnits.FromTwips(h);

            var column = Twips(trPr?.Element(W + "gridBefore"), "val") ?? 0;
            foreach (var tc in tr.Elements().SelectMany(e => e.Name == W + "tc" ? [e] : FlattenCells(e)))
            {
                var tcPr = tc.Element(W + "tcPr");
                var span = Math.Max(1, Twips(tcPr?.Element(W + "gridSpan"), "val") ?? 1);
                var vMerge = tcPr?.Element(W + "vMerge");
                if (vMerge is not null && Val(vMerge) is not "restart" && mergeOrigin.TryGetValue(column, out var origin))
                {
                    origin.RowSpan++;
                    column += span;
                    continue;
                }

                var cell = new HCell { Column = column, ColSpan = span };
                ReadBlocks(tc.Elements(), cell.Blocks);
                cell.VerticalAlign = Val(tcPr?.Element(W + "vAlign")) switch
                {
                    "center" => HVerticalAlign.Center,
                    "bottom" => HVerticalAlign.Bottom,
                    _ => HVerticalAlign.Top,
                };

                // Cell edges: outer table borders on the outside, inside borders between cells, cell overrides.
                var isFirstRow = r == 0;
                var isLastRow = r == rows.Count - 1;
                var cellBorders = new Dictionary<string, HBorder>
                {
                    ["left"] = borders.GetValueOrDefault(column == 0 ? "left" : "insideV", HBorder.None),
                    ["right"] = borders.GetValueOrDefault(column + span >= grid.Length ? "right" : "insideV", HBorder.None),
                    ["top"] = borders.GetValueOrDefault(isFirstRow ? "top" : "insideH", HBorder.None),
                    ["bottom"] = borders.GetValueOrDefault(isLastRow ? "bottom" : "insideH", HBorder.None),
                };
                string? fill = null;
                foreach (var properties in styleChain.Select(s => s.TcPr).Append(r == 0 ? firstRowStyle?.Element(W + "tcPr") : null).Append(tcPr))
                {
                    ApplyBorders(properties?.Element(W + "tcBorders"), cellBorders);
                    if (properties?.Element(W + "shd") is { } shd)
                        fill = ShadingColor(shd) ?? fill;
                }
                cell.Borders = new HBorders(cellBorders["left"], cellBorders["right"], cellBorders["top"], cellBorders["bottom"]);
                cell.Fill = fill;

                if (r == 0 && firstRowStyle?.Element(W + "rPr")?.Element(W + "b") is not null)
                    Embolden(cell.Blocks);

                if (vMerge is not null)
                    mergeOrigin[column] = cell;
                else
                    mergeOrigin.Remove(column);
                row.Cells.Add(cell);
                column += span;
            }
            table.Rows.Add(row);
            table.ColumnCount = Math.Max(table.ColumnCount, column);
        }
        return table;
    }

    private List<Style> StyleChain(Style? style)
    {
        var chain = new List<Style>();
        for (var current = style; current is not null && chain.Count < 20; current = current.BasedOn is { } b ? _styles.GetValueOrDefault(b) : null)
            chain.Insert(0, current);
        return chain;
    }

    private static IEnumerable<XElement> FlattenRows(XElement element) =>
        element.Name == W + "sdt" ? element.Element(W + "sdtContent")?.Elements().SelectMany(e => e.Name == W + "tr" ? [e] : FlattenRows(e)) ?? []
        : element.Name == W + "customXml" ? element.Elements().SelectMany(e => e.Name == W + "tr" ? [e] : FlattenRows(e))
        : [];

    private static IEnumerable<XElement> FlattenCells(XElement element) =>
        element.Name == W + "sdt" ? element.Element(W + "sdtContent")?.Elements().SelectMany(e => e.Name == W + "tc" ? [e] : FlattenCells(e)) ?? []
        : element.Name == W + "customXml" ? element.Elements().SelectMany(e => e.Name == W + "tc" ? [e] : FlattenCells(e))
        : [];

    private static void ApplyBorders(XElement? borders, Dictionary<string, HBorder> target)
    {
        foreach (var edge in borders?.Elements() ?? [])
        {
            var name = edge.Name.LocalName switch
            {
                "start" => "left",
                "end" => "right",
                var other => other,
            };
            target[name] = Border(edge);
        }
    }

    /// <summary>A Word border (w:val style, w:sz in eighths of a point, w:color).</summary>
    private static HBorder Border(XElement edge)
    {
        var style = Val(edge) switch
        {
            null or "nil" or "none" => HBorderStyle.None,
            "dotted" => HBorderStyle.Dot,
            "dashed" or "dashSmallGap" => HBorderStyle.Dash,
            "dotDash" or "dotDotDash" => HBorderStyle.DashDot,
            "double" or "triple" or "thinThickSmallGap" or "thickThinSmallGap" => HBorderStyle.Double,
            _ => HBorderStyle.Solid,
        };
        var eighths = Twips(edge, "sz") ?? 4;
        var color = Attr(edge, "color") is { Length: 6 } hex ? "#" + hex.ToUpperInvariant() : "#000000";
        return new HBorder(style, eighths / 8.0 * 25.4 / 72, color);
    }

    private static HInsets CellMargins(XElement cellMar, HInsets? current)
    {
        int Get(string name, string alternative, int fallback) =>
            HwpxUnits.FromTwips(Twips(cellMar.Element(W + name), "w") ?? Twips(cellMar.Element(W + alternative), "w") ?? fallback / 5);
        var existing = current ?? new HInsets(540, 540, 0, 0);
        return new HInsets(Get("left", "start", existing.Left), Get("right", "end", existing.Right), Get("top", "top", existing.Top), Get("bottom", "bottom", existing.Bottom));
    }

    private static void Embolden(List<HBlock> blocks)
    {
        foreach (var paragraph in blocks.OfType<HParagraph>())
        {
            for (var i = 0; i < paragraph.Inlines.Count; i++)
            {
                if (paragraph.Inlines[i] is HText text)
                    paragraph.Inlines[i] = new HText(text.Text, text.Format with { Bold = true });
            }
        }
    }
}
