// DOCX formatting: styles (with basedOn inheritance), document defaults, theme fonts and list numbering,
// resolved to the same property bags as direct formatting so each paragraph and run gets one merged format.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    /// <summary>
    /// 한글 line spacing is a percentage of the font size, Word's "multiple" of the font's natural line height
    /// (about 1.3 em for the fonts Korean documents use). Word "single" therefore corresponds to about 130%.
    /// </summary>
    private const double WordLineToHwpPercent = 130;

    // ───────────────────────── Property bags ─────────────────────────

    /// <summary>Paragraph properties; later layers (style → numbering → direct) override earlier ones.</summary>
    private sealed class ParaProps
    {
        public string? Jc;
        public int? Left, Right, FirstLine, Hanging, Before, After, Line;
        public string? LineRule;
        public bool? KeepNext, KeepLines, PageBreakBefore, WidowControl, SnapToGrid;
        public int? OutlineLevel;
        public string? NumId;
        public int? Ilvl;
        public List<(int Position, string Kind, string Leader)> Tabs = [];

        public ParaProps Clone()
        {
            var copy = (ParaProps)MemberwiseClone();
            copy.Tabs = [.. Tabs];
            return copy;
        }

        public void Apply(XElement? pPr)
        {
            if (pPr is null)
                return;
            if (pPr.Element(W + "jc") is { } jc)
                Jc = Val(jc);
            if (pPr.Element(W + "ind") is { } ind)
            {
                Left = Twips(ind, "left") ?? Twips(ind, "start") ?? Left;
                Right = Twips(ind, "right") ?? Twips(ind, "end") ?? Right;
                if (Twips(ind, "hanging") is { } hanging)
                    (Hanging, FirstLine) = (hanging, null);
                if (Twips(ind, "firstLine") is { } firstLine)
                    (FirstLine, Hanging) = (firstLine, null);
            }
            if (pPr.Element(W + "spacing") is { } spacing)
            {
                Before = Bool(spacing, "beforeAutospacing") == true ? 280 : Twips(spacing, "before") ?? Before;
                After = Bool(spacing, "afterAutospacing") == true ? 280 : Twips(spacing, "after") ?? After;
                if (Twips(spacing, "line") is { } line)
                    (Line, LineRule) = (line, Attr(spacing, "lineRule") ?? "auto");
            }
            KeepNext = Toggle(pPr, "keepNext") ?? KeepNext;
            KeepLines = Toggle(pPr, "keepLines") ?? KeepLines;
            PageBreakBefore = Toggle(pPr, "pageBreakBefore") ?? PageBreakBefore;
            WidowControl = Toggle(pPr, "widowControl") ?? WidowControl;
            SnapToGrid = Toggle(pPr, "snapToGrid") ?? SnapToGrid;
            if (pPr.Element(W + "outlineLvl") is { } outline && int.TryParse(Val(outline), out var level))
                OutlineLevel = level;
            if (pPr.Element(W + "numPr") is { } numPr)
            {
                if (numPr.Element(W + "numId") is { } numId)
                    NumId = Val(numId);
                if (numPr.Element(W + "ilvl") is { } ilvl && int.TryParse(Val(ilvl), out var ilvlValue))
                    Ilvl = ilvlValue;
            }
            if (pPr.Element(W + "tabs") is { } tabs)
            {
                foreach (var tab in tabs.Elements(W + "tab"))
                {
                    var position = Twips(tab, "pos") ?? 0;
                    Tabs.RemoveAll(t => t.Position == position);
                    if (Val(tab) is { } kind and not "clear")
                        Tabs.Add((position, kind, Attr(tab, "leader") ?? "none"));
                }
                Tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
            }
        }
    }

    /// <summary>Run (character) properties.</summary>
    private sealed class RunProps
    {
        public bool? Bold, Italic, Underline, Strike, Hidden;
        public string? VertAlign, Color, Highlight, Shade, Ascii, EastAsia, AsciiTheme, EastAsiaTheme;
        public int? Size, Spacing;

        public RunProps Clone() => (RunProps)MemberwiseClone();

        public void Apply(XElement? rPr)
        {
            if (rPr is null)
                return;
            Bold = Toggle(rPr, "b") ?? Bold;
            Italic = Toggle(rPr, "i") ?? Italic;
            Strike = Toggle(rPr, "strike") ?? Toggle(rPr, "dstrike") ?? Strike;
            Hidden = Toggle(rPr, "vanish") ?? Hidden;
            if (rPr.Element(W + "u") is { } u)
                Underline = Val(u) is not ("none" or null);
            if (rPr.Element(W + "vertAlign") is { } vertAlign)
                VertAlign = Val(vertAlign);
            if (rPr.Element(W + "color") is { } color)
                Color = Val(color) is { Length: 6 } hex ? "#" + hex.ToUpperInvariant() : null;
            if (rPr.Element(W + "highlight") is { } highlight)
                Highlight = HighlightColor(Val(highlight));
            if (rPr.Element(W + "shd") is { } shd)
                Shade = ShadingColor(shd);
            if (rPr.Element(W + "sz") is { } sz && int.TryParse(Val(sz), out var halfPoints))
                Size = halfPoints;
            if (rPr.Element(W + "spacing") is { } spacing && int.TryParse(Val(spacing), out var twips))
                Spacing = twips;
            if (rPr.Element(W + "rFonts") is { } fonts)
            {
                // A theme reference wins over an explicit name in Word, so a later explicit name clears it.
                if ((Attr(fonts, "ascii") ?? Attr(fonts, "hAnsi")) is { } ascii)
                    (Ascii, AsciiTheme) = (ascii, null);
                if ((Attr(fonts, "asciiTheme") ?? Attr(fonts, "hAnsiTheme")) is { } asciiTheme)
                    AsciiTheme = asciiTheme;
                if (Attr(fonts, "eastAsia") is { } eastAsia)
                    (EastAsia, EastAsiaTheme) = (eastAsia, null);
                if (Attr(fonts, "eastAsiaTheme") is { } eastAsiaTheme)
                    EastAsiaTheme = eastAsiaTheme;
            }
        }
    }

    private sealed class Style
    {
        public required string Id;
        public required string Type;
        public string? Name, BasedOn;
        public XElement? PPr, RPr, TblPr, TcPr;
        public List<XElement> TableConditions = [];
    }

    private sealed class NumberingLevel
    {
        public int Start = 1;
        public string Format = "decimal";
        public string Text = "%1.";
        public XElement? PPr, RPr;
    }

    // ───────────────────────── Loading ─────────────────────────

    private readonly Dictionary<string, Style> _styles = [];
    private readonly Dictionary<string, (ParaProps Para, RunProps Run)> _resolvedStyles = [];
    private readonly Dictionary<string, NumberingLevel[]> _numberingLevels = [];
    private readonly Dictionary<string, HNumbering> _numberings = [];
    private readonly ParaProps _defaultPara = new();
    private readonly RunProps _defaultRun = new();
    private string? _defaultParagraphStyle;
    private string? _defaultTableStyle;
    private (string? Latin, string? EastAsian) _minorFont, _majorFont;

    private void LoadStyles(Part? part)
    {
        var root = part?.Xml.Root;
        if (root is null)
            return;
        var defaults = root.Element(W + "docDefaults");
        _defaultPara.Apply(defaults?.Element(W + "pPrDefault")?.Element(W + "pPr"));
        _defaultRun.Apply(defaults?.Element(W + "rPrDefault")?.Element(W + "rPr"));

        foreach (var element in root.Elements(W + "style"))
        {
            var style = new Style
            {
                Id = Attr(element, "styleId") ?? "",
                Type = Attr(element, "type") ?? "paragraph",
                Name = Val(element.Element(W + "name")),
                BasedOn = Val(element.Element(W + "basedOn")),
                PPr = element.Element(W + "pPr"),
                RPr = element.Element(W + "rPr"),
                TblPr = element.Element(W + "tblPr"),
                TcPr = element.Element(W + "tcPr"),
                TableConditions = [.. element.Elements(W + "tblStylePr")],
            };
            _styles[style.Id] = style;
            if (Bool(element, "default") == true)
            {
                if (style.Type == "paragraph")
                    _defaultParagraphStyle = style.Id;
                else if (style.Type == "table")
                    _defaultTableStyle = style.Id;
            }
        }
    }

    private void LoadTheme(Part? part)
    {
        var fonts = part?.Xml.Descendants(A + "fontScheme").FirstOrDefault();
        (string?, string?) Read(string name)
        {
            var font = fonts?.Element(A + name);
            var latin = Attr(font?.Element(A + "latin"), "typeface");
            var eastAsian = Attr(font?.Element(A + "ea"), "typeface");
            if (string.IsNullOrEmpty(eastAsian))
                eastAsian = Attr(font?.Elements(A + "font").FirstOrDefault(f => Attr(f, "script") == "Hang"), "typeface");
            return (NullIfEmpty(latin), NullIfEmpty(eastAsian));
        }
        _minorFont = Read("minorFont");
        _majorFont = Read("majorFont");
    }

    private void LoadNumbering(Part? part)
    {
        var root = part?.Xml.Root;
        if (root is null)
            return;
        var abstracts = root.Elements(W + "abstractNum")
            .ToDictionary(a => Attr(a, "abstractNumId") ?? "", a => a);
        foreach (var num in root.Elements(W + "num"))
        {
            var numId = Attr(num, "numId") ?? "";
            if (!abstracts.TryGetValue(Val(num.Element(W + "abstractNumId")) ?? "", out var abstractNum))
                continue;
            var levels = new NumberingLevel[9];
            for (var i = 0; i < levels.Length; i++)
                levels[i] = new NumberingLevel { Text = $"%{i + 1}." };
            foreach (var lvl in abstractNum.Elements(W + "lvl"))
                ApplyLevel(levels, lvl);
            foreach (var over in num.Elements(W + "lvlOverride"))
            {
                if (!int.TryParse(Attr(over, "ilvl"), out var index) || index is < 0 or > 8)
                    continue;
                if (over.Element(W + "lvl") is { } lvl)
                    ApplyLevel(levels, lvl);
                if (int.TryParse(Val(over.Element(W + "startOverride")), out var start))
                    levels[index].Start = start;
            }
            _numberingLevels[numId] = levels;
        }

        static void ApplyLevel(NumberingLevel[] levels, XElement lvl)
        {
            if (!int.TryParse(Attr(lvl, "ilvl"), out var index) || index is < 0 or > 8)
                return;
            var level = levels[index];
            if (int.TryParse(Val(lvl.Element(W + "start")), out var start))
                level.Start = start;
            level.Format = Val(lvl.Element(W + "numFmt")) ?? level.Format;
            level.Text = Val(lvl.Element(W + "lvlText")) ?? level.Text;
            level.PPr = lvl.Element(W + "pPr") ?? level.PPr;
            level.RPr = lvl.Element(W + "rPr") ?? level.RPr;
        }
    }

    // ───────────────────────── Resolution ─────────────────────────

    /// <summary>Merged properties of a paragraph or character style including everything it is based on.</summary>
    private (ParaProps Para, RunProps Run) ResolveStyle(string? styleId)
    {
        if (styleId is null || !_styles.TryGetValue(styleId, out var style))
            return (_defaultPara.Clone(), _defaultRun.Clone());
        if (_resolvedStyles.TryGetValue(styleId, out var cached))
            return (cached.Para.Clone(), cached.Run.Clone());

        // Guard against basedOn cycles in damaged files.
        _resolvedStyles[styleId] = (_defaultPara.Clone(), _defaultRun.Clone());
        var (para, run) = style.BasedOn is { } parent && parent != styleId ? ResolveStyle(parent) : (_defaultPara.Clone(), _defaultRun.Clone());
        para.Apply(style.PPr);
        run.Apply(style.RPr);
        _resolvedStyles[styleId] = (para.Clone(), run.Clone());
        return (para, run);
    }

    /// <summary>Properties of a paragraph: defaults → style chain → list level → direct formatting.</summary>
    private (ParaProps Para, RunProps Mark) ParagraphProperties(XElement? pPr)
    {
        var styleId = Val(pPr?.Element(W + "pStyle")) ?? _defaultParagraphStyle;
        var (para, run) = ResolveStyle(styleId);

        var direct = new ParaProps();
        direct.Apply(pPr);
        var numId = direct.NumId ?? para.NumId;
        var ilvl = direct.Ilvl ?? para.Ilvl ?? 0;
        if (numId is not null && _numberingLevels.TryGetValue(numId, out var levels))
            para.Apply(levels[Math.Clamp(ilvl, 0, 8)].PPr);
        para.Apply(pPr);
        para.NumId = numId;
        para.Ilvl = ilvl;

        run.Apply(pPr?.Element(W + "rPr"));
        return (para, run);
    }

    /// <summary>Properties of a run inside a paragraph whose style gave <paramref name="paragraphRun"/>.</summary>
    private RunProps RunProperties(XElement? rPr, RunProps paragraphRun)
    {
        var run = paragraphRun.Clone();
        if (Val(rPr?.Element(W + "rStyle")) is { } characterStyle && _styles.TryGetValue(characterStyle, out var style))
        {
            // Character styles only carry run properties; apply their chain on top of the paragraph's.
            var chain = new List<Style>();
            for (var current = style; current is not null && chain.Count < 20; current = current.BasedOn is { } b ? _styles.GetValueOrDefault(b) : null)
                chain.Add(current);
            for (var i = chain.Count - 1; i >= 0; i--)
                run.Apply(chain[i].RPr);
        }
        run.Apply(rPr);
        return run;
    }

    /// <summary>Heading level 1..9 from the outline level (Word's heading styles set it), else 0.</summary>
    private int HeadingLevel(ParaProps para, XElement? pPr)
    {
        if (para.OutlineLevel is >= 0 and <= 8 and var level)
            return level + 1;
        var name = _styles.GetValueOrDefault(Val(pPr?.Element(W + "pStyle")) ?? "")?.Name ?? "";
        return name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(name.AsSpan(8), out var byName) && byName is >= 1 and <= 9 ? byName : 0;
    }

    private HParaFormat ParaFormat(ParaProps p)
    {
        var firstLine = p.Hanging is { } hanging ? -hanging : p.FirstLine ?? 0;
        HLineSpacing? lineSpacing = p.Line is not { } line ? null : p.LineRule switch
        {
            "exact" => new HLineSpacing(HLineSpacingKind.Fixed, HwpxUnits.FromTwips(line)),
            "atLeast" => new HLineSpacing(HLineSpacingKind.AtLeast, HwpxUnits.FromTwips(line)),
            _ => new HLineSpacing(HLineSpacingKind.Percent, (int)Math.Round(line / 240.0 * WordLineToHwpPercent)),
        };
        return new HParaFormat(
            Align: p.Jc switch
            {
                "center" => HAlign.Center,
                "right" or "end" => HAlign.Right,
                "both" or "lowKashida" or "mediumKashida" or "highKashida" or "thaiDistribute" => HAlign.Justify,
                "distribute" => HAlign.Distribute,
                _ => HAlign.Left,
            },
            Left: HwpxUnits.FromTwips(p.Left ?? 0),
            Right: HwpxUnits.FromTwips(p.Right ?? 0),
            FirstLine: HwpxUnits.FromTwips(firstLine),
            Before: HwpxUnits.FromTwips(p.Before ?? 0),
            After: HwpxUnits.FromTwips(p.After ?? 0),
            LineSpacing: lineSpacing ?? new HLineSpacing(HLineSpacingKind.Percent, (int)WordLineToHwpPercent),
            KeepWithNext: p.KeepNext ?? false,
            KeepLines: p.KeepLines ?? false,
            WidowOrphan: p.WidowControl ?? true,
            Tabs: p.Tabs.Count == 0 ? null : p.Tabs
                .Where(t => t.Kind is not ("bar" or "num"))
                .Select(t => new HTabStop(HwpxUnits.FromTwips(t.Position), t.Kind switch
                {
                    "right" or "end" => HTabKind.Right,
                    "center" => HTabKind.Center,
                    "decimal" => HTabKind.Decimal,
                    _ => HTabKind.Left,
                }, t.Leader switch
                {
                    "dot" or "middleDot" => "DOT",
                    "hyphen" => "DASH",
                    "underscore" or "heavy" => "SOLID",
                    _ => "NONE",
                }))
                .ToList());
    }

    private HCharFormat CharFormat(RunProps r)
    {
        var (latin, eastAsian) = Fonts(r);
        var size = r.Size ?? 20;
        return new HCharFormat(
            Bold: r.Bold ?? false,
            Italic: r.Italic ?? false,
            Underline: r.Underline ?? false,
            Strike: r.Strike ?? false,
            Superscript: r.VertAlign == "superscript",
            Subscript: r.VertAlign == "subscript",
            Size: size * 50,
            Font: latin,
            EastAsianFont: eastAsian,
            Color: r.Color ?? "#000000",
            Shade: r.Highlight ?? r.Shade,
            // Word letter spacing is absolute (twips); 한글's is a percentage of the font size.
            Spacing: r.Spacing is { } twips and not 0 ? Math.Clamp((int)Math.Round(twips * 10.0 / size), -50, 50) : 0);
    }

    private (string? Latin, string? EastAsian) Fonts(RunProps r)
    {
        string? Theme(string? theme) => theme switch
        {
            null => null,
            _ when theme.StartsWith("major", StringComparison.Ordinal) => theme.EndsWith("EastAsia", StringComparison.Ordinal) ? _majorFont.EastAsian : _majorFont.Latin,
            _ => theme.EndsWith("EastAsia", StringComparison.Ordinal) ? _minorFont.EastAsian : _minorFont.Latin,
        };
        var latin = Theme(r.AsciiTheme) ?? r.Ascii;
        var eastAsian = Theme(r.EastAsiaTheme) ?? r.EastAsia;
        return (latin, eastAsian ?? latin);
    }

    /// <summary>List definition for a Word numbering instance (one per numId, shared by all its paragraphs).</summary>
    private HNumbering? Numbering(string? numId)
    {
        if (numId is null or "0" || !_numberingLevels.TryGetValue(numId, out var levels))
            return null;
        if (_numberings.TryGetValue(numId, out var existing))
            return existing;

        var numbering = new HNumbering();
        foreach (var level in levels)
        {
            var bullet = level.Format == "bullet";
            numbering.Levels.Add(bullet
                ? new HNumberingLevel("DIGIT", BulletText(level.Text), level.Start, Bullet: true)
                : new HNumberingLevel(NumberFormat(level.Format), NumberText(level.Text, level.Format), level.Start, Bullet: false));
        }
        _numberings[numId] = numbering;
        return numbering;
    }

    private static string NumberFormat(string wordFormat) => wordFormat switch
    {
        "upperRoman" => "ROMAN_CAPITAL",
        "lowerRoman" => "ROMAN_SMALL",
        "upperLetter" => "LATIN_CAPITAL",
        "lowerLetter" => "LATIN_SMALL",
        "ganada" => "HANGUL_SYLLABLE",
        "chosung" => "HANGUL_JAMO",
        "decimalEnclosedCircle" or "decimalEnclosedCircleChinese" => "CIRCLED_DIGIT",
        "koreanDigital" or "koreanCounting" or "koreanLegal" or "koreanDigital2" => "HANGUL_PHONETIC",
        "ideographDigital" or "chineseCounting" or "chineseCountingThousand" or "japaneseCounting" or "ideographTraditional" => "IDEOGRAPH",
        _ => "DIGIT",
    };

    /// <summary>"%1.%2." → "^1.^2."; a "none" level keeps only its literal text.</summary>
    private static string NumberText(string text, string format)
    {
        if (format == "none")
            return string.Concat(text.Where(c => c != '%' && !char.IsDigit(c)));
        var chars = text.ToCharArray();
        for (var i = 0; i + 1 < chars.Length; i++)
        {
            if (chars[i] == '%' && char.IsDigit(chars[i + 1]))
                chars[i] = '^';
        }
        return new string(chars);
    }

    /// <summary>Word bullets often use Symbol/Wingdings private-use code points; map them to Unicode.</summary>
    private static string BulletText(string text) => text switch
    {
        "" or "\uF0B7" or "\uF09F" or "•" => "●",
        "o" or "\uF06F" => "○",
        "\uF0A7" or "\uF06E" or "▪" => "■",
        "\uF0D8" => "➢",
        "\uF076" => "❖",
        "\uF0FC" => "✓",
        "\uF071" => "❑",
        "\uF075" or "\uF0A8" => "◆",
        "-" or "–" or "\uF02D" => "-",
        _ when text.Length > 0 && text[0] >= '\uF000' => "●",
        _ => text,
    };

    /// <summary>
    /// Colour of a w:shd: the fill, the pattern colour for "solid", and for "pctN" the pattern colour mixed into the
    /// fill at N percent (Word's classic grey header cells are "pct10" or "pct15" with automatic colours).
    /// </summary>
    private static string? ShadingColor(XElement shd)
    {
        static (int R, int G, int B)? Rgb(string? hex) =>
            hex is { Length: 6 } && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                ? ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
                : null;
        static string Hex((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        var fill = Rgb(Attr(shd, "fill"));
        var color = Rgb(Attr(shd, "color"));
        var pattern = Val(shd);
        if (pattern is null or "clear" or "nil")
            return fill is { } f ? Hex(f) : null;
        if (pattern == "solid")
            return Hex(color ?? (0, 0, 0));
        if (pattern.StartsWith("pct", StringComparison.Ordinal) && int.TryParse(pattern.AsSpan(3), out var percent))
        {
            var background = fill ?? (255, 255, 255);
            var foreground = color ?? (0, 0, 0);
            var t = Math.Clamp(percent, 0, 100) / 100.0;
            return Hex(((int)Math.Round(background.R + (foreground.R - background.R) * t),
                        (int)Math.Round(background.G + (foreground.G - background.G) * t),
                        (int)Math.Round(background.B + (foreground.B - background.B) * t)));
        }
        return fill is { } other ? Hex(other) : null;
    }

    private static string? HighlightColor(string? name) => name switch
    {
        "yellow" => "#FFFF00",
        "green" => "#00FF00",
        "cyan" => "#00FFFF",
        "magenta" => "#FF00FF",
        "blue" => "#0000FF",
        "red" => "#FF0000",
        "darkBlue" => "#000080",
        "darkCyan" => "#008080",
        "darkGreen" => "#008000",
        "darkMagenta" => "#800080",
        "darkRed" => "#800000",
        "darkYellow" => "#808000",
        "darkGray" => "#808080",
        "lightGray" => "#C0C0C0",
        "black" => "#000000",
        "white" => "#FFFFFF",
        _ => null,
    };

    // ───────────────────────── XML helpers ─────────────────────────

    private static string? Attr(XElement? element, string name) =>
        (string?)element?.Attribute(W + name) ?? (string?)element?.Attribute(name);

    private static string? Attr(XElement? element, string name, XNamespace ns) => (string?)element?.Attribute(ns + name);

    private static string? Val(XElement? element) => Attr(element, "val");

    private static int? Twips(XElement? element, string name) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? (int)value : null;

    private static long? Long(XElement? element, string name) =>
        long.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Bool(XElement? element, string name) => Attr(element, name) switch
    {
        null => null,
        "0" or "false" or "off" => false,
        _ => true,
    };

    /// <summary>An on/off property element: present = on unless its val says otherwise; missing = inherit.</summary>
    private static bool? Toggle(XElement parent, string name) =>
        parent.Element(W + name) is not { } element ? null : Val(element) is not ("0" or "false" or "off" or "none");

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
