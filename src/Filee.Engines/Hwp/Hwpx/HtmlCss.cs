// The small part of CSS the HTML reader understands: inline style="" declarations, simple stylesheet rules (tag,
// .class, tag.class), colours and lengths. Enough for the formatting e-books and saved web pages rely on (bold and
// italic classes, centred text, page breaks, first-line indents) without a full CSS engine.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Property → value pairs of one declaration block, lower-case property names.</summary>
internal sealed class CssDeclarations : Dictionary<string, string>
{
    public CssDeclarations() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>Parses "color: red; font-weight: bold" (later declarations win, !important is ignored).</summary>
    public static CssDeclarations Parse(string? text)
    {
        var result = new CssDeclarations();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        foreach (var declaration in text.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (name.Length > 0 && value.Length > 0)
                result[name] = value;
        }
        return result;
    }

    public string? Get(string name) => TryGetValue(name, out var value) ? value : null;
}

/// <summary>Rules of the document's style sheets that the reader can match without a full selector engine.</summary>
internal sealed partial class CssStyleSheet
{
    /// <summary>Properties taken from style sheets. Colours and sizes are not: whole-book rules (body colour, base
    /// font size) would otherwise be copied onto every run.</summary>
    private static readonly HashSet<string> SheetProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-weight", "font-style", "text-decoration", "text-decoration-line", "vertical-align", "text-align",
        "display", "page-break-before", "page-break-after", "break-before", "break-after", "white-space",
        "text-indent", "list-style", "list-style-type", "font-variant",
    };

    private readonly List<Rule> _rules = [];

    /// <param name="Tag">Element name, or null for any element.</param>
    /// <param name="Classes">Classes the element must have (all of them).</param>
    /// <param name="Specificity">Classes count more than the tag name.</param>
    private sealed record Rule(string? Tag, string[] Classes, int Specificity, int Order, CssDeclarations Declarations);

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Adds the rules of a style sheet. @media, @font-face and other at-rules are skipped.</summary>
    public void Add(string css)
    {
        css = Comment().Replace(css, "");
        var position = 0;
        while (position < css.Length)
        {
            var open = css.IndexOf('{', position);
            if (open < 0)
                break;
            var selectors = css[position..open].Trim();
            var close = MatchingBrace(css, open);
            var body = css[(open + 1)..Math.Max(open + 1, close)];
            position = close < 0 ? css.Length : close + 1;
            if (selectors.StartsWith('@') || body.Contains('{'))
                continue; // at-rules (nested blocks): not applied

            var declarations = CssDeclarations.Parse(body);
            foreach (var key in declarations.Keys.Where(k => !SheetProperties.Contains(k)).ToList())
                declarations.Remove(key);
            if (declarations.Count == 0)
                continue;
            foreach (var selector in selectors.Split(','))
            {
                if (ParseSelector(selector.Trim()) is { } rule)
                    _rules.Add(rule with { Order = _rules.Count, Declarations = declarations });
            }
        }
    }

    /// <summary>Declarations that apply to an element, in cascade order (the last one wins).</summary>
    public CssDeclarations Match(string tag, IReadOnlyCollection<string> classes)
    {
        var result = new CssDeclarations();
        if (_rules.Count == 0)
            return result;
        foreach (var rule in _rules.Where(r => (r.Tag is null || r.Tag == tag) && r.Classes.All(classes.Contains))
                                   .OrderBy(r => r.Specificity).ThenBy(r => r.Order))
        {
            foreach (var (name, value) in rule.Declarations)
                result[name] = value;
        }
        return result;
    }

    /// <summary>"p", ".note", "span.bold", "p.a.b"; anything else (descendants, ids, pseudo classes) is skipped.</summary>
    private static Rule? ParseSelector(string selector)
    {
        if (!SimpleSelector().IsMatch(selector))
            return null;
        var parts = selector.Split('.');
        var tag = parts[0].Length == 0 || parts[0] == "*" ? null : parts[0].ToLowerInvariant();
        var classes = parts.Skip(1).ToArray();
        return new Rule(tag, classes, classes.Length * 10 + (tag is null ? 0 : 1), 0, []);
    }

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
                depth++;
            else if (css[i] == '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^(\*|[A-Za-z][\w-]*)?(\.[\w-]+)*$")]
    private static partial Regex SimpleSelector();
}

/// <summary>CSS values: colours and lengths.</summary>
internal static class CssValues
{
    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["white"] = "#FFFFFF",
        ["red"] = "#FF0000",
        ["green"] = "#008000",
        ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["silver"] = "#C0C0C0",
        ["maroon"] = "#800000",
        ["purple"] = "#800080",
        ["fuchsia"] = "#FF00FF",
        ["magenta"] = "#FF00FF",
        ["lime"] = "#00FF00",
        ["olive"] = "#808000",
        ["navy"] = "#000080",
        ["teal"] = "#008080",
        ["aqua"] = "#00FFFF",
        ["cyan"] = "#00FFFF",
        ["orange"] = "#FFA500",
        ["brown"] = "#A52A2A",
        ["pink"] = "#FFC0CB",
        ["gold"] = "#FFD700",
        ["darkred"] = "#8B0000",
        ["darkblue"] = "#00008B",
        ["darkgreen"] = "#006400",
        ["darkgray"] = "#A9A9A9",
        ["darkgrey"] = "#A9A9A9",
        ["lightgray"] = "#D3D3D3",
        ["lightgrey"] = "#D3D3D3",
        ["dimgray"] = "#696969",
        ["dimgrey"] = "#696969",
        ["crimson"] = "#DC143C",
        ["indigo"] = "#4B0082",
        ["violet"] = "#EE82EE",
        ["coral"] = "#FF7F50",
        ["tomato"] = "#FF6347",
        ["steelblue"] = "#4682B4",
        ["royalblue"] = "#4169E1",
        ["lightblue"] = "#ADD8E6",
        ["lightyellow"] = "#FFFFE0",
        ["lightgreen"] = "#90EE90",
        ["whitesmoke"] = "#F5F5F5",
        ["beige"] = "#F5F5DC",
        ["ivory"] = "#FFFFF0",
    };

    /// <summary>"#RRGGBB" for #rgb, #rrggbb, rgb()/rgba() and common colour names; null for anything else
    /// (transparent, inherit, gradients, ...).</summary>
    public static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (NamedColors.TryGetValue(text, out var named))
            return named;
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length is 3 or 4 && hex.All(Uri.IsHexDigit))
                return "#" + string.Concat(hex.Take(3).Select(c => $"{c}{c}")).ToUpperInvariant();
            if (hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit))
                return "#" + hex[..6].ToUpperInvariant();
            return null;
        }
        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && text.IndexOf('(') is var open and > 0 && text.IndexOf(')') is var close && close > open)
        {
            var parts = text[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;
            var channels = new int[3];
            for (var i = 0; i < 3; i++)
            {
                var part = parts[i];
                var percent = part.EndsWith('%');
                if (!double.TryParse(part.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return null;
                channels[i] = (int)Math.Clamp(Math.Round(percent ? number * 2.55 : number), 0, 255);
            }
            return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
        }
        return null;
    }

    /// <summary>The first colour inside a shorthand such as "background: #eee url(x.png)".</summary>
    public static string? FirstColor(string? value)
    {
        if (Color(value) is { } direct)
            return direct;
        foreach (var token in (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Color(token) is { } color)
                return color;
        return null;
    }

    /// <summary>
    /// A length in HWPUNIT. <paramref name="em"/> is the size of 1em (and the base of %) in HWPUNIT; null when the
    /// value is not a length (auto, inherit, ...).
    /// </summary>
    public static int? Length(string? value, double em)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim().ToLowerInvariant();
        var split = 0;
        while (split < text.Length && (char.IsDigit(text[split]) || text[split] is '.' or '-' or '+'))
            split++;
        if (split == 0 || !double.TryParse(text[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        var unit = text[split..].Trim();
        double? result = unit switch
        {
            "em" or "rem" => number * em,
            "ex" or "ch" => number * em / 2,
            "%" => number * em / 100,
            "pt" => number * 100,
            "px" or "" => number * HwpxUnits.PerPixel,
            "in" => number * 7200,
            "cm" => number * HwpxUnits.PerMm * 10,
            "mm" => number * HwpxUnits.PerMm,
            "pc" => number * 1200,
            _ => null,
        };
        return result is null ? null : (int)Math.Round(result.Value);
    }

    /// <summary>A font size in 1/100 pt, relative sizes based on <paramref name="parent"/> (1/100 pt).</summary>
    public static int? FontSize(string? value, int parent)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        double? factor = value.Trim().ToLowerInvariant() switch
        {
            "xx-small" => 0.6,
            "x-small" => 0.75,
            "small" => 0.89,
            "medium" => 1.0,
            "large" => 1.2,
            "x-large" => 1.5,
            "xx-large" => 2.0,
            "xxx-large" => 3.0,
            "smaller" => 0.83,
            "larger" => 1.2,
            _ => null,
        };
        if (factor is { } f)
            return (int)Math.Round(parent * f);
        // HWPUNIT and 1/100 pt are the same scale (1 pt = 100 HWPUNIT), so Length() fits font sizes directly.
        return Length(value, parent) is { } size && size > 0 ? Math.Clamp(size, 100, 20000) : null;
    }
}
