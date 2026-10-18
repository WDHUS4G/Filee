// Text of PPTX shapes and table cells. PowerPoint text inherits from a chain of level styles (lvlNpPr):
// the paragraph itself → the shape's list style → the layout placeholder → the master placeholder → the master's
// title / body / other text style → the presentation default. Every property is looked up along that chain.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

internal sealed partial class PptxReader
{
    private const int DefaultSize = 1800; // 18 pt, PowerPoint's default text size

    private readonly record struct BodyLayout(HInsets Padding, HVerticalAlign Anchor);

    /// <summary>Insets and vertical anchoring of a text body, inherited from the placeholders.</summary>
    private static BodyLayout BodyProperties(XElement shape, XElement? layoutPh, XElement? masterPh)
    {
        var chain = new[] { shape, layoutPh, masterPh }.Select(e => e?.Element(P + "txBody")?.Element(A + "bodyPr")).OfType<XElement>().ToList();
        string? Attr(string name) => chain.Select(b => (string?)b.Attribute(name)).FirstOrDefault(v => v is not null);
        int Inset(string name, long fallback) => Hwp(long.TryParse(Attr(name), out var v) ? v : fallback);
        return new BodyLayout(
            new HInsets(Inset("lIns", 91440), Inset("rIns", 91440), Inset("tIns", 45720), Inset("bIns", 45720)),
            Attr("anchor") switch
            {
                "ctr" => HVerticalAlign.Center,
                "b" => HVerticalAlign.Bottom,
                _ => HVerticalAlign.Top,
            });
    }

    /// <summary>Paragraphs of a text body; an empty list when there is no visible text.</summary>
    /// <param name="defaultColor">Text colour from the shape style (p:style/a:fontRef), if any.</param>
    private List<HBlock> TextBody(SlideContext context, string partPath, XElement? body, XElement? ph, XElement? layoutPh,
        XElement? masterPh, string? defaultColor)
    {
        var blocks = new List<HBlock>();
        if (body is null || !body.Descendants(A + "t").Any(t => t.Value.Trim().Length > 0))
            return blocks;

        // "Shrink text on overflow" stores the scale PowerPoint applied.
        var fontScale = double.TryParse((string?)body.Element(A + "bodyPr")?.Element(A + "normAutofit")?.Attribute("fontScale"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) ? scale / 100000 : 1;
        var counters = new int[10];
        var lastNumbered = new bool[10];

        foreach (var p in body.Elements(A + "p"))
        {
            var pPr = p.Element(A + "pPr");
            var level = Math.Clamp(int.TryParse((string?)pPr?.Attribute("lvl"), out var l) ? l : 0, 0, 8);
            var chain = StyleChain(context, body, ph, layoutPh, masterPh, level + 1);
            var paragraphChain = new List<XElement>();
            if (pPr is not null)
                paragraphChain.Add(pPr);
            paragraphChain.AddRange(chain.Specific);
            paragraphChain.AddRange(chain.General);

            string? Attr(string name) => paragraphChain.Select(e => (string?)e.Attribute(name)).FirstOrDefault(v => v is not null);
            var paragraph = new HParagraph
            {
                Format = new HParaFormat(
                    Align: Attr("algn") switch
                    {
                        "ctr" => HAlign.Center,
                        "r" => HAlign.Right,
                        "just" => HAlign.Justify,
                        "dist" => HAlign.Distribute,
                        _ => HAlign.Left,
                    },
                    Left: long.TryParse(Attr("marL"), out var marL) ? Hwp(marL) : 0,
                    FirstLine: long.TryParse(Attr("indent"), out var indent) ? Hwp(indent) : 0,
                    Before: Spacing(paragraphChain, "spcBef"),
                    After: Spacing(paragraphChain, "spcAft"),
                    LineSpacing: LineSpacing(paragraphChain)),
            };

            var runs = p.Elements().Where(e => e.Name.LocalName is "r" or "br" or "fld").ToList();
            var firstRun = runs.FirstOrDefault(r => r.Name.LocalName != "br");
            var markFormat = RunFormat(context, p.Element(A + "endParaRPr") ?? firstRun?.Element(A + "rPr"), pPr, chain, defaultColor, fontScale);
            paragraph.MarkFormat = markFormat with { Color = null };

            // Bullet or number (not on empty paragraphs, as PowerPoint shows them).
            var hasText = runs.Any(r => r.Element(A + "t")?.Value.Length > 0);
            var bullet = hasText ? Bullet(paragraphChain) : null;
            for (var deeper = level + 1; deeper < counters.Length; deeper++)
            {
                counters[deeper] = 0;
                lastNumbered[deeper] = false;
            }
            if (bullet is { AutoNumber: { } scheme })
            {
                counters[level] = lastNumbered[level] ? counters[level] + 1 : bullet.Value.Start;
                lastNumbered[level] = true;
                paragraph.Inlines.Add(new HText(Number(scheme, counters[level]) + " ", markFormat));
            }
            else
            {
                lastNumbered[level] = false;
                if (bullet is { Char: { } character })
                    paragraph.Inlines.Add(new HText(character + " ", markFormat with { Bold = null, Italic = null, Underline = null }));
            }

            foreach (var run in runs)
            {
                var format = RunFormat(context, run.Element(A + "rPr"), pPr, chain, defaultColor, fontScale);
                if (run.Name.LocalName == "br")
                {
                    paragraph.Inlines.Add(new HLineBreak(format));
                    continue;
                }
                var text = run.Element(A + "t")?.Value ?? "";
                if (text.Length == 0)
                    continue;
                var link = ExternalLink(partPath, (string?)run.Element(A + "rPr")?.Element(A + "hlinkClick")?.Attribute(R + "id"));
                if (link is null)
                {
                    paragraph.Inlines.Add(new HText(text, format));
                }
                else
                {
                    var hLink = new HLink(link);
                    hLink.Content.Add(new HText(text, format));
                    paragraph.Inlines.Add(hLink);
                }
            }
            blocks.Add(paragraph);
        }
        return blocks;
    }

    /// <summary>
    /// Level styles for one paragraph level: specific ones (shape, layout and master placeholder list styles) and
    /// general ones (master text style, presentation default). The shape style's text colour sits between them.
    /// </summary>
    private (List<XElement> Specific, List<XElement> General) StyleChain(SlideContext context, XElement body, XElement? ph,
        XElement? layoutPh, XElement? masterPh, int level)
    {
        var name = A + $"lvl{level}pPr";
        var specific = new[] { body, layoutPh?.Element(P + "txBody"), masterPh?.Element(P + "txBody") }
            .Select(owner => owner?.Element(A + "lstStyle")?.Element(name))
            .OfType<XElement>()
            .ToList();

        var general = new List<XElement>();
        var textStyles = context.Master?.Element(P + "txStyles");
        if (ph is not null)
        {
            var masterStyle = Normalize((string?)ph.Attribute("type")) switch
            {
                "title" => textStyles?.Element(P + "titleStyle"),
                "body" => textStyles?.Element(P + "bodyStyle"),
                _ => textStyles?.Element(P + "otherStyle"),
            };
            if (masterStyle?.Element(name) is { } fromMaster)
                general.Add(fromMaster);
        }
        if (_defaultTextStyle?.Element(name) is { } fromPresentation)
            general.Add(fromPresentation);
        return (specific, general);
    }

    private HCharFormat RunFormat(SlideContext context, XElement? rPr, XElement? pPr,
        (List<XElement> Specific, List<XElement> General) chain, string? defaultColor, double fontScale)
    {
        // Run → paragraph default → specific level styles → [shape style colour] → general level styles.
        var properties = new List<XElement>();
        if (rPr is not null)
            properties.Add(rPr);
        if (pPr?.Element(A + "defRPr") is { } paragraphDefault)
            properties.Add(paragraphDefault);
        properties.AddRange(chain.Specific.Select(s => s.Element(A + "defRPr")).OfType<XElement>());
        var specificCount = properties.Count;
        properties.AddRange(chain.General.Select(s => s.Element(A + "defRPr")).OfType<XElement>());

        string? Attr(string attribute) => properties.Select(e => (string?)e.Attribute(attribute)).FirstOrDefault(v => v is not null);
        string? color = null;
        for (var i = 0; i < properties.Count && color is null; i++)
        {
            if (i == specificCount && defaultColor is not null)
                color = defaultColor;
            else if (properties[i].Element(A + "solidFill") is { } fill)
                color = context.Colors.Drawing(fill);
        }
        color ??= defaultColor ?? context.Colors.Theme("tx1");

        var size = int.TryParse(Attr("sz"), out var sz) ? sz : DefaultSize;
        var latin = Typeface(context, properties.Select(e => (string?)e.Element(A + "latin")?.Attribute("typeface")).FirstOrDefault(v => v is not null));
        var eastAsian = Typeface(context, properties.Select(e => (string?)e.Element(A + "ea")?.Attribute("typeface")).FirstOrDefault(v => v is not null));
        var baseline = int.TryParse(Attr("baseline"), out var b) ? b : 0;
        return new HCharFormat(
            Bold: Attr("b") is "1" or "true" ? true : null,
            Italic: Attr("i") is "1" or "true" ? true : null,
            Underline: Attr("u") is { } u && u != "none" ? true : null,
            Strike: Attr("strike") is { } s && s != "noStrike" ? true : null,
            Superscript: baseline > 0 ? true : null,
            Subscript: baseline < 0 ? true : null,
            Size: Math.Max(100, (int)Math.Round(size * fontScale)),
            Font: latin,
            EastAsianFont: eastAsian,
            Color: color);
    }

    /// <summary>Theme font references (+mj-lt, +mn-ea, ...) resolved against the theme's font scheme.</summary>
    private static string? Typeface(SlideContext context, string? typeface) => typeface switch
    {
        null or "" => null,
        "+mj-lt" => context.Fonts.MajorLatin,
        "+mn-lt" => context.Fonts.MinorLatin,
        "+mj-ea" => context.Fonts.MajorEastAsian,
        "+mn-ea" => context.Fonts.MinorEastAsian,
        _ when typeface.StartsWith('+') => null,
        _ => typeface,
    };

    private readonly record struct BulletInfo(string? Char, string? AutoNumber, int Start);

    private static BulletInfo? Bullet(List<XElement> chain)
    {
        foreach (var style in chain)
        {
            if (style.Element(A + "buNone") is not null)
                return null;
            if (style.Element(A + "buChar") is { } character)
                return new BulletInfo((string?)character.Attribute("char") ?? "•", null, 1);
            if (style.Element(A + "buAutoNum") is { } number)
                return new BulletInfo(null, (string?)number.Attribute("type") ?? "arabicPeriod",
                    int.TryParse((string?)number.Attribute("startAt"), out var start) ? start : 1);
            if (style.Element(A + "buBlip") is not null)
                return new BulletInfo("•", null, 1);
        }
        return null;
    }

    /// <summary>The label of item <paramref name="n"/> in a PowerPoint auto-number scheme (arabicPeriod, alphaLcParenR, ...).</summary>
    internal static string Number(string scheme, int n)
    {
        var value = scheme switch
        {
            _ when scheme.StartsWith("alphaLc", StringComparison.Ordinal) => Letters(n).ToLowerInvariant(),
            _ when scheme.StartsWith("alphaUc", StringComparison.Ordinal) => Letters(n),
            _ when scheme.StartsWith("romanLc", StringComparison.Ordinal) => Roman(n).ToLowerInvariant(),
            _ when scheme.StartsWith("romanUc", StringComparison.Ordinal) => Roman(n),
            _ when scheme.StartsWith("circleNum", StringComparison.Ordinal) && n is >= 1 and <= 20 => ((char)('①' + n - 1)).ToString(),
            _ => n.ToString(CultureInfo.InvariantCulture),
        };
        return scheme switch
        {
            _ when scheme.EndsWith("ParenBoth", StringComparison.Ordinal) => $"({value})",
            _ when scheme.EndsWith("ParenR", StringComparison.Ordinal) => value + ")",
            _ when scheme.EndsWith("Plain", StringComparison.Ordinal) => value,
            _ => value + ".",
        };
    }

    private static string Letters(int n)
    {
        var text = "";
        for (; n > 0; n = (n - 1) / 26)
            text = (char)('A' + (n - 1) % 26) + text;
        return text;
    }

    private static string Roman(int n)
    {
        var text = "";
        foreach (var (value, symbol) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            for (; n >= value; n -= value)
                text += symbol;
        }
        return text;
    }

    /// <summary>Space before / after in HWPUNIT (1/100 pt = 1 HWPUNIT); percentages are taken of an 18 pt line.</summary>
    private static int? Spacing(List<XElement> chain, string name)
    {
        foreach (var style in chain)
        {
            if (style.Element(A + name) is not { } spacing)
                continue;
            if (int.TryParse((string?)spacing.Element(A + "spcPts")?.Attribute("val"), out var points))
                return points;
            if (int.TryParse((string?)spacing.Element(A + "spcPct")?.Attribute("val"), out var percent))
                return (int)(percent / 100000.0 * DefaultSize * 1.2);
        }
        return 0;
    }

    /// <summary>
    /// PowerPoint's "single" line is about 1.2 × the font size, 한글's percentages count the font size itself.
    /// </summary>
    private static HLineSpacing LineSpacing(List<XElement> chain)
    {
        foreach (var style in chain)
        {
            if (style.Element(A + "lnSpc") is not { } spacing)
                continue;
            if (int.TryParse((string?)spacing.Element(A + "spcPct")?.Attribute("val"), out var percent))
                return new HLineSpacing(HLineSpacingKind.Percent, Math.Max(60, (int)Math.Round(percent / 1000.0 * 1.2)));
            if (int.TryParse((string?)spacing.Element(A + "spcPts")?.Attribute("val"), out var points))
                return new HLineSpacing(HLineSpacingKind.Fixed, points);
        }
        return new HLineSpacing(HLineSpacingKind.Percent, 120);
    }

    private string? ExternalLink(string partPath, string? id) =>
        id is not null && _package.Relationships(partPath).TryGetValue(id, out var rel) && rel.External ? rel.Target : null;

    // ───────────────────────── Tables ─────────────────────────

    private void Table(SlideContext context, string partPath, XElement frame, Transform transform, List<HInline> output)
    {
        var tbl = frame.Element(A + "graphic")?.Element(A + "graphicData")?.Element(A + "tbl");
        if (tbl is null || Bounds(frame.Element(P + "xfrm"), transform) is not { } bounds)
            return; // charts, SmartArt and embedded objects are not converted

        var grid = tbl.Element(A + "tblGrid")?.Elements(A + "gridCol").Select(c => Math.Max(1, Hwp(Emu(c.Attribute("w"))))).ToArray() ?? [];
        if (grid.Length == 0)
            return;
        var tablePr = tbl.Element(A + "tblPr");
        var styled = tablePr?.Element(A + "tableStyleId") is not null;
        var firstRow = (string?)tablePr?.Attribute("firstRow") is "1" or "true";
        var banded = (string?)tablePr?.Attribute("bandRow") is "1" or "true";
        // Without the style part, table styles are approximated by PowerPoint's default "Medium Style 2 – Accent 1".
        var accent = context.Colors.Theme("accent1");
        var gridLine = styled ? new HBorder(HBorderStyle.Solid, 0.35, "#FFFFFF") : HBorder.Thin;

        var table = new HTable
        {
            ColumnCount = grid.Length,
            ColumnWidths = grid,
            CellMargin = new HInsets(720, 720, 360, 360),
            Borders = new HBorders(gridLine, gridLine, gridLine, gridLine),
        };
        var rows = tbl.Elements(A + "tr").ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var header = firstRow && r == 0;
            var row = new HRow { Header = header, Height = Math.Max(1, Hwp(Emu(rows[r].Attribute("h")))) };
            var column = 0;
            foreach (var tc in rows[r].Elements(A + "tc"))
            {
                var colSpan = Math.Max(1, int.TryParse((string?)tc.Attribute("gridSpan"), out var gs) ? gs : 1);
                if ((string?)tc.Attribute("hMerge") is "1" or "true" || (string?)tc.Attribute("vMerge") is "1" or "true")
                {
                    column += colSpan;
                    continue; // covered by a merged cell
                }
                var tcPr = tc.Element(A + "tcPr");
                string? fill = tcPr?.Element(A + "solidFill") is { } solid ? context.Colors.Drawing(solid) : null;
                if (fill is null && styled && tcPr?.Element(A + "noFill") is null)
                    fill = header ? accent : banded && (r - (firstRow ? 1 : 0)) % 2 == 0 ? OoxmlColors.Tint(accent, 0.6) : OoxmlColors.Tint(accent, 0.8);

                var cell = new HCell
                {
                    Column = column,
                    ColSpan = colSpan,
                    RowSpan = Math.Max(1, int.TryParse((string?)tc.Attribute("rowSpan"), out var rs) ? rs : 1),
                    Fill = fill,
                    VerticalAlign = (string?)tcPr?.Attribute("anchor") switch
                    {
                        "ctr" => HVerticalAlign.Center,
                        "b" => HVerticalAlign.Bottom,
                        _ => HVerticalAlign.Top,
                    },
                    Borders = new HBorders(
                        CellLine(tcPr?.Element(A + "lnL"), context.Colors, gridLine),
                        CellLine(tcPr?.Element(A + "lnR"), context.Colors, gridLine),
                        CellLine(tcPr?.Element(A + "lnT"), context.Colors, gridLine),
                        CellLine(tcPr?.Element(A + "lnB"), context.Colors, gridLine)),
                };
                var text = TextBody(context, partPath, tc.Element(A + "txBody"), null, null, null,
                    styled && header ? context.Colors.Theme("lt1") : null);
                if (styled && header)
                    Emphasize(text);
                cell.Blocks.AddRange(text.Count > 0 ? text : [new HParagraph()]);
                row.Cells.Add(cell);
                column += colSpan;
            }
            table.Rows.Add(row);
        }

        var box = new HTextBox
        {
            Width = Math.Max(1, Hwp(bounds.W)),
            Height = Math.Max(1, table.Rows.Sum(r => r.Height ?? 0)),
            Anchor = Anchor(Hwp(bounds.X), Hwp(bounds.Y)),
            Line = HBorder.None,
            Padding = new HInsets(0, 0, 0, 0),
        };
        box.Blocks.Add(table);
        output.Add(box);
    }

    private static HBorder CellLine(XElement? line, OoxmlColors colors, HBorder fallback)
    {
        if (line is null)
            return fallback;
        if (line.Element(A + "noFill") is not null)
            return HBorder.None;
        var color = line.Element(A + "solidFill") is { } solid ? colors.Drawing(solid) : null;
        return color is null ? fallback : new HBorder(HBorderStyle.Solid, Math.Max(0.1, Emu(line.Attribute("w")) / 36000.0), color);
    }

    /// <summary>Header cells of styled tables are bold.</summary>
    private static void Emphasize(List<HBlock> blocks)
    {
        foreach (var paragraph in blocks.OfType<HParagraph>())
            for (var i = 0; i < paragraph.Inlines.Count; i++)
                if (paragraph.Inlines[i] is HText text && text.Format.Bold is null)
                    paragraph.Inlines[i] = new HText(text.Text, text.Format with { Bold = true });
    }
}
