// header.xml: fonts, character and paragraph shapes, numberings and bullets, border/fill definitions, tab stops and
// styles. Every shape a paragraph or run refers to is complete (no inheritance), so each id maps to one format.
//
// Lengths in paragraph shapes and tab stops come in two scales: hp:case branches for HwpUnitChar-aware readers hold
// true HWPUNIT, while hp:default branches and shapes without a switch (older files) hold them doubled, as HwpxWriter
// and 한글 write them.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    private readonly Dictionary<(string Language, string Id), string> _fonts = [];
    private readonly Dictionary<string, XElement> _charPrs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _paraPrs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _borderFillElements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _tabPrs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _numberingElements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _bulletElements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);

    private readonly Dictionary<string, HCharFormat> _charFormats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParaShape> _paraShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HNumbering?> _numberings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HBorders Borders, string? Fill)?> _borderFills = new(StringComparer.Ordinal);

    /// <summary>A paragraph shape: format plus the list or outline it belongs to.</summary>
    /// <param name="HeadingType">OWPML heading type: NONE, OUTLINE, NUMBER or BULLET.</param>
    /// <param name="HeadingLevel">0-based level of the list or outline.</param>
    private sealed record ParaShape(HParaFormat Format, string HeadingType, string? HeadingId, int HeadingLevel, bool PageBreakBefore);

    private void LoadHeader(XElement head)
    {
        foreach (var element in head.Descendants())
        {
            var id = Attr(element, "id");
            var parent = element.Parent?.Name.LocalName;
            switch (element.Name.LocalName)
            {
                case "font" when parent == "fontface" && id is not null:
                    _fonts[(Attr(element.Parent, "lang") ?? "", id)] = Attr(element, "face") ?? "";
                    break;
                case "charPr" when id is not null:
                    _charPrs.TryAdd(id, element);
                    break;
                case "paraPr" when id is not null:
                    _paraPrs.TryAdd(id, element);
                    break;
                case "borderFill" when id is not null:
                    _borderFillElements.TryAdd(id, element);
                    break;
                case "tabPr" when id is not null:
                    _tabPrs.TryAdd(id, element);
                    break;
                case "numbering" when parent == "numberings" && id is not null:
                    _numberingElements.TryAdd(id, element);
                    break;
                case "bullet" when parent == "bullets" && id is not null:
                    _bulletElements.TryAdd(id, element);
                    break;
                case "style" when id is not null:
                    _styles.TryAdd(id, element);
                    break;
            }
        }
    }

    // ───────────────────────── Character shapes ─────────────────────────

    /// <summary>The complete character format of a charPr id (every member set, so the look survives other styles).</summary>
    private HCharFormat CharFormat(string? id)
    {
        id ??= "0";
        if (_charFormats.TryGetValue(id, out var cached))
            return cached;
        var charPr = _charPrs.GetValueOrDefault(id) ?? _charPrs.GetValueOrDefault("0");
        if (charPr is null)
            return _charFormats[id] = default;

        var fontRef = Child(charPr, "fontRef");
        string? Face(string language) =>
            Attr(fontRef, language.ToLowerInvariant()) is { } fontId && _fonts.TryGetValue((language, fontId), out var face) && face.Length > 0 ? face : null;

        var strike = Attr(Child(charPr, "strikeout"), "shape");
        var format = new HCharFormat(
            Bold: Child(charPr, "bold") is not null,
            Italic: Child(charPr, "italic") is not null,
            Underline: Attr(Child(charPr, "underline"), "type") == "BOTTOM",
            // "3D" is 한글's placeholder for "no strikeout" in some versions, like NONE.
            Strike: strike is not (null or "NONE" or "3D"),
            Superscript: Child(charPr, "supscript") is not null,
            Subscript: Child(charPr, "subscript") is not null,
            Size: Int(charPr, "height", 1000),
            Font: Face("LATIN"),
            EastAsianFont: Face("HANGUL"),
            Color: Color(Attr(charPr, "textColor")) ?? "#000000",
            Shade: Color(Attr(charPr, "shadeColor")),
            Spacing: Int(Child(charPr, "spacing"), "hangul"));
        return _charFormats[id] = format;
    }

    // ───────────────────────── Paragraph shapes ─────────────────────────

    private ParaShape ParaShapeOf(string? id)
    {
        id ??= "0";
        if (_paraShapes.TryGetValue(id, out var cached))
            return cached;
        var paraPr = _paraPrs.GetValueOrDefault(id) ?? _paraPrs.GetValueOrDefault("0");
        if (paraPr is null)
            return _paraShapes[id] = new ParaShape(default, "NONE", null, 0, false);

        var heading = Child(paraPr, "heading");
        var breaks = Child(paraPr, "breakSetting");
        var (intent, left, right, before, after, lineSpacing) = Spacing(paraPr);
        var format = new HParaFormat(
            Align: Attr(Child(paraPr, "align"), "horizontal") switch
            {
                "CENTER" => HAlign.Center,
                "RIGHT" => HAlign.Right,
                "JUSTIFY" => HAlign.Justify,
                "DISTRIBUTE" or "DISTRIBUTE_SPACE" => HAlign.Distribute,
                _ => HAlign.Left,
            },
            // A negative "intent" (hanging indent) keeps the first line at "left" and moves the other lines right;
            // the model, like Word, measures "left" to the other lines and puts the first line at left + firstLine.
            Left: intent < 0 ? left - intent : left,
            Right: right,
            FirstLine: intent,
            Before: before,
            After: after,
            LineSpacing: lineSpacing,
            KeepWithNext: Flag(breaks, "keepWithNext"),
            KeepLines: Flag(breaks, "keepLines"),
            WidowOrphan: Flag(breaks, "widowOrphan"),
            Tabs: TabStops(Attr(paraPr, "tabPrIDRef")));
        var shape = new ParaShape(format, Attr(heading, "type") ?? "NONE", Attr(heading, "idRef"), Math.Max(0, Int(heading, "level")), Flag(breaks, "pageBreakBefore"));
        return _paraShapes[id] = shape;
    }

    /// <summary>Margins and line spacing from the switch branch with exact values, else the doubled ones halved.</summary>
    private static (int Intent, int Left, int Right, int Before, int After, HLineSpacing LineSpacing) Spacing(XElement paraPr)
    {
        XElement? margin = null, line = null;
        bool marginExact = false, lineExact = false;
        foreach (var child in paraPr.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "switch":
                    {
                        var (branch, exact) = Branch(child);
                        if (branch?.Elements().FirstOrDefault(e => e.Name.LocalName == "margin") is { } m)
                            (margin, marginExact) = (m, exact);
                        if (branch?.Elements().FirstOrDefault(e => e.Name.LocalName == "lineSpacing") is { } l)
                            (line, lineExact) = (l, exact);
                        break;
                    }
                case "margin" when margin is null:
                    margin = child;
                    break;
                case "lineSpacing" when line is null:
                    line = child;
                    break;
            }
        }

        int Length(string name, string attribute)
        {
            // <hc:left value="..."/> children, or attributes on hh:margin in older files.
            var element = margin?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            var value = element is not null ? Int(element, "value") : Int(margin, attribute);
            return marginExact ? value : (int)Math.Round(value / 2.0);
        }

        var type = Attr(line, "type") ?? "PERCENT";
        var value = Int(line, "value", 160);
        var length = lineExact ? value : (int)Math.Round(value / 2.0);
        var lineSpacing = type switch
        {
            "FIXED" => new HLineSpacing(HLineSpacingKind.Fixed, length),
            "AT_LEAST" or "MINIMUM" => new HLineSpacing(HLineSpacingKind.AtLeast, length),
            // "Space between lines only": a 10 pt line plus the gap, as the model has no such kind.
            "BETWEEN_LINES" or "SPACE_ONLY" or "SPACEONLY" => new HLineSpacing(HLineSpacingKind.AtLeast, 1000 + length),
            _ => new HLineSpacing(HLineSpacingKind.Percent, value),
        };
        return (Length("intent", "indent"), Length("left", "left"), Length("right", "right"), Length("prev", "prev"), Length("next", "next"), lineSpacing);
    }

    // ───────────────────────── Tab stops ─────────────────────────

    private IReadOnlyList<HTabStop>? TabStops(string? tabPrId)
    {
        if (tabPrId is null || !_tabPrs.TryGetValue(tabPrId, out var tabPr))
            return null;
        var stops = new List<HTabStop>();
        foreach (var child in tabPr.Elements())
        {
            IEnumerable<XElement> items;
            bool exact;
            if (child.Name.LocalName == "switch")
            {
                var (branch, branchExact) = Branch(child);
                (items, exact) = (branch?.Elements() ?? [], branchExact);
            }
            else if (child.Name.LocalName == "tabItem")
            {
                // A tab item outside a switch is exact when it says so (unit="HWPUNIT"), else in the doubled scale.
                (items, exact) = ([child], Attr(child, "unit") == "HWPUNIT");
            }
            else
            {
                continue;
            }
            foreach (var item in items.Where(e => e.Name.LocalName == "tabItem"))
            {
                var position = Int(item, "pos");
                stops.Add(new HTabStop(exact ? position : position / 2, Attr(item, "type") switch
                {
                    "RIGHT" => HTabKind.Right,
                    "CENTER" => HTabKind.Center,
                    "DECIMAL" => HTabKind.Decimal,
                    _ => HTabKind.Left,
                }, Attr(item, "leader") ?? "NONE"));
            }
        }
        return stops.Count == 0 ? null : stops;
    }

    // ───────────────────────── Numbering and bullets ─────────────────────────

    /// <summary>List definition of a numbering (hh:numbering) id; each paraHead is one level ("^1." style text).</summary>
    private HNumbering? Numbering(string? id)
    {
        if (id is null)
            return null;
        if (_numberings.TryGetValue("n" + id, out var cached))
            return cached;
        HNumbering? numbering = null;
        if (_numberingElements.TryGetValue(id, out var element))
        {
            numbering = new HNumbering();
            var start = Int(element, "start", 1);
            foreach (var head in Children(element, "paraHead").OrderBy(h => Int(h, "level", 1)))
            {
                var text = head.Value;
                // A level without a counter (^n) is a symbol: Filee writes bullets that way.
                var bullet = text.Length > 0 && !HasCounter(text);
                numbering.Levels.Add(new HNumberingLevel(Attr(head, "numFormat") ?? "DIGIT", text, Int(head, "start", Math.Max(start, 1)), bullet));
            }
        }
        return _numberings["n" + id] = numbering;
    }

    /// <summary>List definition of a bullet (hh:bullet) id: the same symbol on every level.</summary>
    private HNumbering? Bullet(string? id)
    {
        if (id is null)
            return null;
        if (_numberings.TryGetValue("b" + id, out var cached))
            return cached;
        HNumbering? numbering = null;
        if (_bulletElements.TryGetValue(id, out var element))
        {
            var symbol = Attr(element, "char") is { Length: > 0 } c && !Flag(element, "useImage") ? BulletSymbol(c) : "●";
            numbering = new HNumbering();
            for (var i = 0; i < 10; i++)
                numbering.Levels.Add(new HNumberingLevel("DIGIT", symbol, 1, Bullet: true));
        }
        return _numberings["b" + id] = numbering;
    }

    private static bool HasCounter(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '^' && char.IsDigit(text[i + 1]))
                return true;
        }
        return false;
    }

    /// <summary>Symbol-font bullets use private-use code points that other programs do not show.</summary>
    private static string BulletSymbol(string text) => text[0] is >= '' and <= '' ? "●" : text;

    // ───────────────────────── Borders and fills ─────────────────────────

    /// <summary>Borders and background of a borderFill id; null when the id is unknown.</summary>
    private (HBorders Borders, string? Fill)? BorderFill(string? id)
    {
        if (id is null)
            return null;
        if (_borderFills.TryGetValue(id, out var cached))
            return cached;
        (HBorders, string?)? result = null;
        if (_borderFillElements.TryGetValue(id, out var element))
        {
            HBorder Edge(string name) => Border(Child(element, name));
            var brush = Child(element, "fillBrush");
            result = (new HBorders(Edge("leftBorder"), Edge("rightBorder"), Edge("topBorder"), Edge("bottomBorder")),
                Color(Attr(Child(brush, "winBrush"), "faceColor"))
                ?? Children(Child(brush, "gradation"), "color").Select(c => Color(Attr(c, "value"))).FirstOrDefault(c => c is not null));
        }
        return _borderFills[id] = result;
    }

    /// <summary>A cell or page border: line type, width like "0.12 mm" and colour.</summary>
    private static HBorder Border(XElement? edge)
    {
        var style = LineStyle(Attr(edge, "type"));
        if (edge is null || style == HBorderStyle.None)
            return HBorder.None;
        var width = Attr(edge, "width") ?? "0.12";
        var number = new string(width.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return new HBorder(style,
            double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0 ? mm : 0.12,
            Color(Attr(edge, "color")) ?? "#000000");
    }

    /// <summary>OWPML line types mapped onto the few styles the model (and Word) share.</summary>
    private static HBorderStyle LineStyle(string? type) => type switch
    {
        null or "NONE" => HBorderStyle.None,
        "DASH" or "LONG_DASH" => HBorderStyle.Dash,
        "DOT" or "CIRCLE" => HBorderStyle.Dot,
        "DASH_DOT" or "DASH_DOT_DOT" => HBorderStyle.DashDot,
        "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM" or "DOUBLE_WAVE" => HBorderStyle.Double,
        _ => HBorderStyle.Solid,
    };

    // ───────────────────────── Styles ─────────────────────────

    /// <summary>Heading level 1..9 of a style named "Heading n" (as Filee writes them), else 0.</summary>
    private int StyleHeadingLevel(string? styleId)
    {
        if (styleId is null || !_styles.TryGetValue(styleId, out var style))
            return 0;
        var name = Attr(style, "engName") ?? "";
        return name.StartsWith("Heading ", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(name.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 9
            ? level
            : 0;
    }
}
