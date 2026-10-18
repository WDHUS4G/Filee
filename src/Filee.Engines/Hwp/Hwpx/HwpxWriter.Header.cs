// header.xml: fonts, character and paragraph shapes, list numberings, border/fill definitions and tab stops.
// Everything is created on demand and cached, so equal formatting shares one definition like 한글 does.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxWriter
{
    private const string HwpUnitCharNamespace = "http://www.hancom.co.kr/hwpml/2016/HwpUnitChar";

    /// <summary>Schema order of charPr children. New elements are inserted accordingly.</summary>
    private static readonly string[] CharPrOrder =
        ["fontRef", "ratio", "spacing", "relSz", "offset", "italic", "bold", "underline", "strikeout", "outline", "shadow", "emboss", "engrave", "supscript", "subscript"];

    /// <summary>Line widths 한글 offers (mm); others are snapped to the nearest.</summary>
    private static readonly double[] LineWidths = [0.1, 0.12, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0];

    private static readonly string[] Languages = ["HANGUL", "LATIN", "HANJA", "JAPANESE", "OTHER", "SYMBOL", "USER"];

    private readonly XElement _head;
    private readonly Dictionary<(string BaseId, HCharFormat Format, bool Link), string> _charPrCache = [];
    private readonly Dictionary<(string BaseId, HParaFormat Format, string? NumberingId, int Level, bool Numbered), string> _paraPrCache = [];
    private readonly Dictionary<HNumbering, string> _numberingIds = [];
    private readonly Dictionary<(HBorders Borders, string? Fill), string> _borderFills = [];
    private readonly Dictionary<string, string> _tabPrIds = [];
    private readonly Dictionary<(string Language, string Face), int> _fontIds = [];
    private readonly Dictionary<int, (string StyleId, string ParaPrId, string CharPrId)> _headings = [];
    private int _maxCharPr;
    private int _maxParaPr;
    private string _normalStyleId = "0";
    private string _normalParaPrId = "0";
    private string _normalCharPrId = "0";
    private (string StyleId, string ParaPrId, string CharPrId)? _footnoteStyle;

    private XElement RefList(string name) =>
        _head.Descendants(Hh + name).FirstOrDefault() ?? throw new InvalidDataException($"header.xml has no {name}.");

    private void InitializeHeader()
    {
        _maxCharPr = MaxId(_head.Descendants(Hh + "charPr"));
        _maxParaPr = MaxId(_head.Descendants(Hh + "paraPr"));

        // Numbering definitions live in refList, before paraProperties.
        if (_head.Descendants(Hh + "numberings").FirstOrDefault() is null)
        {
            var refList = _head.Element(Hh + "refList") ?? _head;
            var numberings = new XElement(Hh + "numberings", new XAttribute("itemCnt", 0));
            if (refList.Element(Hh + "paraProperties") is { } paraProperties)
                paraProperties.AddBeforeSelf(numberings);
            else
                refList.Add(numberings);
        }

        var styles = _head.Descendants(Hh + "style").ToList();
        (string, string, string) Ids(XElement style) =>
            ((string?)style.Attribute("id") ?? _normalStyleId,
             (string?)style.Attribute("paraPrIDRef") ?? _normalParaPrId,
             (string?)style.Attribute("charPrIDRef") ?? _normalCharPrId);

        var normal = styles.FirstOrDefault(s => (string?)s.Attribute("id") == "0") ?? styles.FirstOrDefault();
        if (normal is not null)
            (_normalStyleId, _normalParaPrId, _normalCharPrId) = Ids(normal);

        // "Heading 1".."Heading 9" by English style name; fall back to outline levels of paragraph properties.
        foreach (var style in styles)
        {
            var name = (string?)style.Attribute("engName") ?? "";
            if (name.StartsWith("Heading ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan(8), out var level) && level is >= 1 and <= 9)
                _headings[level] = Ids(style);
            else if (name.Contains("footnote", StringComparison.OrdinalIgnoreCase))
                _footnoteStyle = Ids(style);
        }
        if (_headings.Count == 0)
        {
            foreach (var paraPr in _head.Descendants(Hh + "paraPr"))
            {
                var heading = paraPr.Element(Hh + "heading");
                if ((string?)heading?.Attribute("type") != "OUTLINE" || !int.TryParse((string?)heading.Attribute("level"), out var outline))
                    continue;
                var paraPrId = (string?)paraPr.Attribute("id") ?? "0";
                if (styles.FirstOrDefault(s => (string?)s.Attribute("paraPrIDRef") == paraPrId) is { } style)
                    _headings.TryAdd(outline + 1, Ids(style));
            }
        }
    }

    private string SerializeHeader(int sectionCount)
    {
        void Count(string container, string item)
        {
            if (_head.Descendants(Hh + container).FirstOrDefault() is { } element)
                element.SetAttributeValue("itemCnt", element.Elements(Hh + item).Count());
        }
        Count("charProperties", "charPr");
        Count("paraProperties", "paraPr");
        Count("numberings", "numbering");
        Count("borderFills", "borderFill");
        Count("tabProperties", "tabPr");
        _head.SetAttributeValue("secCnt", sectionCount);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + _head.ToString(SaveOptions.DisableFormatting);
    }

    // ───────────────────────── Fonts ─────────────────────────

    /// <summary>Index of <paramref name="face"/> in the font list of <paramref name="language"/> (added if missing).</summary>
    private int FontId(string language, string face)
    {
        if (_fontIds.TryGetValue((language, face), out var cached))
            return cached;
        var fontface = _head.Descendants(Hh + "fontface").FirstOrDefault(f => (string?)f.Attribute("lang") == language);
        if (fontface is null)
            return 0;
        var existing = fontface.Elements(Hh + "font").FirstOrDefault(f => string.Equals((string?)f.Attribute("face"), face, StringComparison.OrdinalIgnoreCase));
        int id;
        if (existing is not null)
        {
            id = (int?)existing.Attribute("id") ?? 0;
        }
        else
        {
            id = fontface.Elements(Hh + "font").Count();
            fontface.Add(new XElement(Hh + "font", new XAttribute("id", id), new XAttribute("face", face), new XAttribute("type", "TTF"), new XAttribute("isEmbedded", 0)));
            fontface.SetAttributeValue("fontCnt", id + 1);
        }
        _fontIds[(language, face)] = id;
        return id;
    }

    // ───────────────────────── Character shapes ─────────────────────────

    /// <summary>Character shape id for <paramref name="baseId"/> with <paramref name="format"/> applied (created on demand).</summary>
    private string CharPr(string baseId, HCharFormat format, bool link = false)
    {
        // Links without their own colour get the usual blue underline.
        link = link && format.Color is null;
        if (format.IsEmpty && !link)
            return baseId;
        if (_charPrCache.TryGetValue((baseId, format, link), out var cached))
            return cached;

        var source = _head.Descendants(Hh + "charPr").FirstOrDefault(c => (string?)c.Attribute("id") == baseId)
                     ?? _head.Descendants(Hh + "charPr").FirstOrDefault();
        if (source is null)
            return baseId;

        var node = new XElement(source);
        var id = (++_maxCharPr).ToString(CultureInfo.InvariantCulture);
        node.SetAttributeValue("id", id);

        if (format.Size is { } size)
            node.SetAttributeValue("height", Math.Clamp(size, 100, 409600));
        if (format.Color is { } color)
            node.SetAttributeValue("textColor", color);
        if (link)
            node.SetAttributeValue("textColor", "#0000FF");
        if (format.Shade is { } shade)
            node.SetAttributeValue("shadeColor", shade);
        if (format.Font is not null || format.EastAsianFont is not null)
        {
            var fontRef = EnsureChild(node, "fontRef");
            foreach (var language in Languages)
            {
                var eastAsian = language is "HANGUL" or "HANJA" or "JAPANESE";
                var face = eastAsian ? format.EastAsianFont ?? format.Font : format.Font ?? format.EastAsianFont;
                if (face is not null)
                    fontRef.SetAttributeValue(language.ToLowerInvariant(), FontId(language, face));
            }
        }
        if (format.Spacing is { } spacing)
        {
            var element = EnsureChild(node, "spacing");
            foreach (var language in Languages)
                element.SetAttributeValue(language.ToLowerInvariant(), spacing);
        }
        SetFlag(node, "bold", format.Bold);
        SetFlag(node, "italic", format.Italic);
        if (format.Underline is { } underlined || link)
        {
            var underline = EnsureChild(node, "underline");
            var on = link || format.Underline == true;
            underline.SetAttributeValue("type", on ? "BOTTOM" : "NONE");
            underline.SetAttributeValue("shape", "SOLID");
            underline.SetAttributeValue("color", link ? "#0000FF" : format.Color ?? "#000000");
        }
        if (format.Strike is { } strike)
        {
            var strikeout = EnsureChild(node, "strikeout");
            strikeout.SetAttributeValue("shape", strike ? "SOLID" : "NONE");
            strikeout.SetAttributeValue("color", format.Color ?? "#000000");
        }
        if (format.Superscript == true)
        {
            node.Element(Hh + "subscript")?.Remove();
            EnsureChild(node, "supscript");
        }
        else if (format.Subscript == true)
        {
            node.Element(Hh + "supscript")?.Remove();
            EnsureChild(node, "subscript");
        }
        else if (format.Superscript == false || format.Subscript == false)
        {
            node.Element(Hh + "supscript")?.Remove();
            node.Element(Hh + "subscript")?.Remove();
        }

        RefList("charProperties").Add(node);
        _charPrCache[(baseId, format, link)] = id;
        return id;
    }

    private static void SetFlag(XElement charPr, string name, bool? value)
    {
        if (value == true)
            EnsureChild(charPr, name);
        else if (value == false)
            charPr.Element(Hh + name)?.Remove();
    }

    /// <summary>Returns the child element, inserting it at its schema position if missing.</summary>
    private static XElement EnsureChild(XElement charPr, string name)
    {
        if (charPr.Element(Hh + name) is { } existing)
            return existing;
        var element = new XElement(Hh + name);
        var rank = Array.IndexOf(CharPrOrder, name);
        var after = charPr.Elements().LastOrDefault(e => Array.IndexOf(CharPrOrder, e.Name.LocalName) is var r && r >= 0 && r < rank);
        if (after is not null)
            after.AddAfterSelf(element);
        else
            charPr.AddFirst(element);
        return element;
    }

    // ───────────────────────── Paragraph shapes ─────────────────────────

    /// <summary>Paragraph shape id for <paramref name="baseId"/> with a format and list numbering applied.</summary>
    private string ParaPr(string baseId, HParaFormat format, HListRef? list)
    {
        var numberingId = list is null ? null : NumberingId(list.Numbering);
        var key = (baseId, format, numberingId, list?.Level ?? 0, list?.Numbered ?? false);
        if (format.IsEmpty && list is null)
            return baseId;
        if (_paraPrCache.TryGetValue(key, out var cached))
            return cached;

        var source = _head.Descendants(Hh + "paraPr").FirstOrDefault(p => (string?)p.Attribute("id") == baseId)
                     ?? _head.Descendants(Hh + "paraPr").First();
        var node = new XElement(source);
        var id = (++_maxParaPr).ToString(CultureInfo.InvariantCulture);
        node.SetAttributeValue("id", id);

        if (format.Align is { } align)
        {
            var element = node.Element(Hh + "align") ?? AddFirst(node, new XElement(Hh + "align", new XAttribute("vertical", "BASELINE")));
            element.SetAttributeValue("horizontal", align switch
            {
                HAlign.Center => "CENTER",
                HAlign.Right => "RIGHT",
                HAlign.Justify => "JUSTIFY",
                HAlign.Distribute => "DISTRIBUTE",
                _ => "LEFT",
            });
        }

        if (list is not null)
        {
            var heading = node.Element(Hh + "heading");
            if (heading is null)
            {
                heading = new XElement(Hh + "heading");
                if (node.Element(Hh + "align") is { } alignElement)
                    alignElement.AddAfterSelf(heading);
                else
                    node.AddFirst(heading);
            }
            heading.SetAttributeValue("type", list.Numbered ? "NUMBER" : "NONE");
            heading.SetAttributeValue("idRef", list.Numbered ? numberingId : "0");
            heading.SetAttributeValue("level", list.Numbered ? list.Level : 0);
        }

        if (node.Element(Hh + "breakSetting") is { } breakSetting)
        {
            if (format.KeepWithNext is { } keepWithNext)
                breakSetting.SetAttributeValue("keepWithNext", keepWithNext ? 1 : 0);
            if (format.KeepLines is { } keepLines)
                breakSetting.SetAttributeValue("keepLines", keepLines ? 1 : 0);
            if (format.WidowOrphan is { } widowOrphan)
                breakSetting.SetAttributeValue("widowOrphan", widowOrphan ? 1 : 0);
        }

        // List items without their own indentation hang the bullet in front of the text.
        var left = format.Left;
        var firstLine = format.FirstLine;
        if (list is not null && left is null)
        {
            const int Hanging = 2000;
            left = (list.Level + 1) * Hanging;
            firstLine ??= list.Numbered ? -Hanging : 0;
        }
        SetSpacing(node, left, format.Right, firstLine, format.Before, format.After, format.LineSpacing);

        if (format.Tabs is { Count: > 0 } tabs)
            node.SetAttributeValue("tabPrIDRef", TabPrId(tabs));

        RefList("paraProperties").Add(node);
        _paraPrCache[key] = id;
        return id;
    }

    private static XElement AddFirst(XElement parent, XElement child)
    {
        parent.AddFirst(child);
        return child;
    }

    /// <summary>
    /// Rewrites margins and line spacing. 한글 stores them twice: in hp:case (HwpUnitChar-aware readers, true
    /// HWPUNIT) and in hp:default for older readers, where lengths are doubled (percentages are not).
    /// </summary>
    private static void SetSpacing(XElement paraPr, int? left, int? right, int? firstLine, int? before, int? after, HLineSpacing? lineSpacing)
    {
        var existingCase = paraPr.Descendants(Hp + "case").FirstOrDefault();
        int Current(string name) => (int?)existingCase?.Descendants(Hc + name).FirstOrDefault()?.Attribute("value") ?? 0;
        var existingLine = existingCase?.Element(Hh + "lineSpacing") ?? paraPr.Descendants(Hh + "lineSpacing").FirstOrDefault();

        // The model (like Word) puts the first line at left + firstLine. 한글 differs for hanging indents: a
        // negative "intent" keeps the first line at "left" and moves the other lines right by its size, so the
        // left margin is reduced by the hanging amount (a positive intent works like Word's first-line indent).
        var hwpLeft = left ?? Current("left");
        if (left is not null && firstLine is < 0)
            hwpLeft += firstLine.Value;
        var values = new (string Name, int Value)[]
        {
            ("intent", firstLine ?? Current("intent")),
            ("left", hwpLeft),
            ("right", right ?? Current("right")),
            ("prev", before ?? Current("prev")),
            ("next", after ?? Current("next")),
        };
        var lineType = lineSpacing is { } ls ? ls.Kind switch
        {
            HLineSpacingKind.Fixed => "FIXED",
            HLineSpacingKind.AtLeast => "AT_LEAST",
            _ => "PERCENT",
        } : (string?)existingLine?.Attribute("type") ?? "PERCENT";
        var lineValue = lineSpacing?.Value ?? (int?)existingLine?.Attribute("value") ?? 160;

        XElement Branch(XName name, int factor)
        {
            var line = lineType == "PERCENT" ? lineValue : lineValue * factor;
            return new XElement(name,
                new XElement(Hh + "margin", values.Select(v => new XElement(Hc + v.Name, new XAttribute("value", v.Value * factor), new XAttribute("unit", "HWPUNIT")))),
                new XElement(Hh + "lineSpacing", new XAttribute("type", lineType), new XAttribute("value", line), new XAttribute("unit", "HWPUNIT")));
        }

        var switchElement = new XElement(Hp + "switch",
            new XElement(Branch(Hp + "case", 1)).WithAttribute(Hp + "required-namespace", HwpUnitCharNamespace),
            Branch(Hp + "default", 2));

        paraPr.Elements(Hp + "switch").Remove();
        paraPr.Elements(Hh + "margin").Remove();
        paraPr.Elements(Hh + "lineSpacing").Remove();
        // Order: align, heading, breakSetting, autoSpacing, switch, border.
        var anchor = paraPr.Element(Hh + "autoSpacing") ?? paraPr.Element(Hh + "breakSetting") ?? paraPr.Element(Hh + "heading") ?? paraPr.Element(Hh + "align");
        if (anchor is not null)
            anchor.AddAfterSelf(switchElement);
        else
            paraPr.AddFirst(switchElement);
    }

    // ───────────────────────── Numbering ─────────────────────────

    private string NumberingId(HNumbering numbering)
    {
        if (_numberingIds.TryGetValue(numbering, out var cached))
            return cached;
        var numberings = RefList("numberings");
        var id = (MaxId(numberings.Elements(Hh + "numbering")) + 1).ToString(CultureInfo.InvariantCulture);
        var element = new XElement(Hh + "numbering", new XAttribute("id", id),
            new XAttribute("start", numbering.Levels.FirstOrDefault()?.Start ?? 1));
        for (var i = 0; i < numbering.Levels.Count && i < 10; i++)
        {
            var level = numbering.Levels[i];
            element.Add(new XElement(Hh + "paraHead",
                new XAttribute("start", level.Start), new XAttribute("level", i + 1),
                new XAttribute("align", "LEFT"), new XAttribute("useInstWidth", 1), new XAttribute("autoIndent", 0),
                new XAttribute("widthAdjust", 0), new XAttribute("textOffsetType", "PERCENT"), new XAttribute("textOffset", 50),
                new XAttribute("numFormat", level.Format), new XAttribute("charPrIDRef", 4294967295u), new XAttribute("checkable", 0),
                level.Text));
        }
        numberings.Add(element);
        _numberingIds[numbering] = id;
        return id;
    }

    // ───────────────────────── Borders and fills ─────────────────────────

    private string BorderFillId(HBorders borders, string? fill)
    {
        if (_borderFills.TryGetValue((borders, fill), out var cached))
            return cached;
        var container = RefList("borderFills");
        var id = (MaxId(container.Elements(Hh + "borderFill")) + 1).ToString(CultureInfo.InvariantCulture);

        XElement Line(string name, HBorder border) => new(Hh + name,
            new XAttribute("type", LineType(border.Style)),
            new XAttribute("width", LineWidth(border.WidthMm)),
            new XAttribute("color", border.Color));

        var element = new XElement(Hh + "borderFill",
            new XAttribute("id", id), new XAttribute("threeD", 0), new XAttribute("shadow", 0),
            new XAttribute("centerLine", "NONE"), new XAttribute("breakCellSeparateLine", 0),
            new XElement(Hh + "slash", new XAttribute("type", "NONE"), new XAttribute("Crooked", 0), new XAttribute("isCounter", 0)),
            new XElement(Hh + "backSlash", new XAttribute("type", "NONE"), new XAttribute("Crooked", 0), new XAttribute("isCounter", 0)),
            Line("leftBorder", borders.Left), Line("rightBorder", borders.Right),
            Line("topBorder", borders.Top), Line("bottomBorder", borders.Bottom),
            new XElement(Hh + "diagonal", new XAttribute("type", "SOLID"), new XAttribute("width", "0.1 mm"), new XAttribute("color", "#000000")),
            new XElement(Hc + "fillBrush",
                new XElement(Hc + "winBrush", new XAttribute("faceColor", fill ?? "none"), new XAttribute("hatchColor", "#000000"), new XAttribute("alpha", 0))));
        container.Add(element);
        _borderFills[(borders, fill)] = id;
        return id;
    }

    private static string LineType(HBorderStyle style) => style switch
    {
        HBorderStyle.None => "NONE",
        HBorderStyle.Dash => "DASH",
        HBorderStyle.Dot => "DOT",
        HBorderStyle.DashDot => "DASH_DOT",
        HBorderStyle.Double => "DOUBLE_SLIM",
        _ => "SOLID",
    };

    /// <summary>Snaps a width in mm to the nearest width 한글 offers, e.g. "0.12 mm".</summary>
    internal static string LineWidth(double mm)
    {
        var nearest = LineWidths.MinBy(w => Math.Abs(w - mm));
        return nearest.ToString(nearest >= 1 ? "0.0" : "0.0#", CultureInfo.InvariantCulture) + " mm";
    }

    // ───────────────────────── Tab stops ─────────────────────────

    private string TabPrId(IReadOnlyList<HTabStop> tabs)
    {
        var key = string.Join(';', tabs.Select(t => $"{t.Position},{t.Kind},{t.Leader}"));
        if (_tabPrIds.TryGetValue(key, out var cached))
            return cached;
        var container = RefList("tabProperties");
        var id = (MaxId(container.Elements(Hh + "tabPr")) + 1).ToString(CultureInfo.InvariantCulture);
        var element = new XElement(Hh + "tabPr", new XAttribute("id", id), new XAttribute("autoTabLeft", 0), new XAttribute("autoTabRight", 0));
        foreach (var tab in tabs)
        {
            var type = tab.Kind switch
            {
                HTabKind.Right => "RIGHT",
                HTabKind.Center => "CENTER",
                HTabKind.Decimal => "DECIMAL",
                _ => "LEFT",
            };
            // Same doubling rule as paragraph margins: hp:default holds twice the value and no unit.
            element.Add(new XElement(Hp + "switch",
                new XElement(Hp + "case", new XAttribute(Hp + "required-namespace", HwpUnitCharNamespace),
                    new XElement(Hh + "tabItem", new XAttribute("pos", tab.Position), new XAttribute("type", type), new XAttribute("leader", tab.Leader), new XAttribute("unit", "HWPUNIT"))),
                new XElement(Hp + "default",
                    new XElement(Hh + "tabItem", new XAttribute("pos", tab.Position * 2), new XAttribute("type", type), new XAttribute("leader", tab.Leader)))));
        }
        container.Add(element);
        _tabPrIds[key] = id;
        return id;
    }
}

internal static class XElementExtensions
{
    public static XElement WithAttribute(this XElement element, XName name, object value)
    {
        element.SetAttributeValue(name, value);
        return element;
    }
}
