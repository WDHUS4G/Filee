// Colours in Office Open XML: theme colours (theme1.xml), Excel's tint and indexed palette, and DrawingML colour
// elements with their modifiers (lumMod, lumOff, tint, shade).

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Office.Ooxml;

/// <summary>Resolves colours to "#RRGGBB".</summary>
internal sealed class OoxmlColors
{
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Theme slots in clrScheme order.</summary>
    private static readonly string[] Slots = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    /// <summary>Office 2013+ default theme, used when a file has none.</summary>
    private static readonly Dictionary<string, string> DefaultTheme = new()
    {
        ["dk1"] = "#000000",
        ["lt1"] = "#FFFFFF",
        ["dk2"] = "#44546A",
        ["lt2"] = "#E7E6E6",
        ["accent1"] = "#4472C4",
        ["accent2"] = "#ED7D31",
        ["accent3"] = "#A5A5A5",
        ["accent4"] = "#FFC000",
        ["accent5"] = "#5B9BD5",
        ["accent6"] = "#70AD47",
        ["hlink"] = "#0563C1",
        ["folHlink"] = "#954F72",
    };

    /// <summary>Excel's legacy indexed colours 0-63 (8-63 is the default 56-colour palette).</summary>
    private static readonly string[] Indexed =
    [
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080",
        "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
        "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
        "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
        "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
        "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333",
    ];

    private readonly Dictionary<string, string> _theme;
    private readonly IReadOnlyDictionary<string, string>? _map;

    private OoxmlColors(Dictionary<string, string> theme, IReadOnlyDictionary<string, string>? map = null)
    {
        _theme = theme;
        _map = map;
    }

    /// <summary>
    /// The same colours with a PresentationML colour map (p:clrMap bg1="lt1" tx1="dk1" ...), which decides what the
    /// aliases tx1 / bg1 / tx2 / bg2 mean.
    /// </summary>
    public OoxmlColors WithMap(IReadOnlyDictionary<string, string>? map) => new(_theme, map);

    /// <summary>Reads the colour scheme of a theme part (null = Office default colours).</summary>
    public static OoxmlColors FromTheme(XDocument? theme)
    {
        var colors = new Dictionary<string, string>(DefaultTheme);
        var scheme = theme?.Descendants(A + "clrScheme").FirstOrDefault();
        foreach (var slot in Slots)
        {
            var element = scheme?.Element(A + slot);
            var value = (string?)element?.Element(A + "srgbClr")?.Attribute("val")
                        ?? (string?)element?.Element(A + "sysClr")?.Attribute("lastClr");
            if (value is { Length: 6 })
                colors[slot] = "#" + value.ToUpperInvariant();
        }
        return new OoxmlColors(colors);
    }

    /// <summary>A theme slot or alias (tx1, bg1, ...) as "#RRGGBB".</summary>
    public string Theme(string name) => _theme.GetValueOrDefault(
        _map?.GetValueOrDefault(name) ?? name switch
        {
            "tx1" => "dk1",
            "bg1" => "lt1",
            "tx2" => "dk2",
            "bg2" => "lt2",
            _ => name,
        }, "#000000");

    /// <summary>
    /// A SpreadsheetML colour element (&lt;color rgb="FFRRGGBB"/&gt;, theme="n" tint="x" or indexed="n"), or null
    /// for "automatic".
    /// </summary>
    public string? Spreadsheet(XElement? color)
    {
        if (color is null || (string?)color.Attribute("auto") is "1" or "true")
            return null;
        string? hex = null;
        if ((string?)color.Attribute("rgb") is { Length: >= 6 } argb)
            hex = "#" + argb[^6..].ToUpperInvariant();
        else if (int.TryParse((string?)color.Attribute("theme"), out var theme) && theme is >= 0 and < 12)
            // SpreadsheetML swaps the first two pairs: 0 = lt1, 1 = dk1, 2 = lt2, 3 = dk2.
            hex = Theme(theme switch { 0 => "lt1", 1 => "dk1", 2 => "lt2", 3 => "dk2", _ => Slots[theme] });
        else if (int.TryParse((string?)color.Attribute("indexed"), out var index) && index is >= 0 and < 64)
            hex = "#" + Indexed[index];
        if (hex is null)
            return null;
        return double.TryParse((string?)color.Attribute("tint"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tint) && tint != 0
            ? Tint(hex, tint)
            : hex;
    }

    /// <summary>
    /// The first DrawingML colour child of <paramref name="parent"/> (srgbClr, schemeClr, sysClr, prstClr) with its
    /// modifiers applied; <paramref name="placeholderColor"/> replaces schemeClr "phClr" (style references).
    /// </summary>
    public string? Drawing(XElement? parent, string? placeholderColor = null)
    {
        foreach (var element in parent?.Elements() ?? [])
        {
            string? hex = element.Name.LocalName switch
            {
                "srgbClr" => (string?)element.Attribute("val") is { Length: 6 } rgb ? "#" + rgb.ToUpperInvariant() : null,
                "schemeClr" => (string?)element.Attribute("val") is "phClr" ? placeholderColor : Theme((string?)element.Attribute("val") ?? ""),
                "sysClr" => (string?)element.Attribute("lastClr") is { Length: 6 } last ? "#" + last.ToUpperInvariant() : "#000000",
                "prstClr" => PresetColor((string?)element.Attribute("val")),
                _ => null,
            };
            if (element.Name.LocalName is not ("srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr" or "hslClr"))
                continue;
            return hex is null ? null : Modify(hex, element);
        }
        return null;
    }

    private static string Modify(string hex, XElement color)
    {
        double Value(string name) => int.TryParse((string?)color.Element(A + name)?.Attribute("val"), out var v) ? v / 100000.0 : double.NaN;
        var (h, s, l) = ToHsl(hex);
        var lumMod = Value("lumMod");
        var lumOff = Value("lumOff");
        if (!double.IsNaN(lumMod) || !double.IsNaN(lumOff))
            l = Math.Clamp(l * (double.IsNaN(lumMod) ? 1 : lumMod) + (double.IsNaN(lumOff) ? 0 : lumOff), 0, 1);
        var result = FromHsl(h, s, l);
        var (r, g, b) = Rgb(result);
        var tint = Value("tint");
        if (!double.IsNaN(tint))
            (r, g, b) = (r + (1 - r) * (1 - tint), g + (1 - g) * (1 - tint), b + (1 - b) * (1 - tint));
        var shade = Value("shade");
        if (!double.IsNaN(shade))
            (r, g, b) = (r * shade, g * shade, b * shade);
        return Hex(r, g, b);
    }

    /// <summary>Excel's tint: lightens (tint &gt; 0) or darkens (tint &lt; 0) the luminance.</summary>
    internal static string Tint(string hex, double tint)
    {
        var (h, s, l) = ToHsl(hex);
        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
        return FromHsl(h, s, Math.Clamp(l, 0, 1));
    }

    private static string? PresetColor(string? name) => name switch
    {
        "black" => "#000000",
        "white" => "#FFFFFF",
        "red" => "#FF0000",
        "green" => "#008000",
        "blue" => "#0000FF",
        "yellow" => "#FFFF00",
        "gray" or "grey" => "#808080",
        "lightGray" => "#D3D3D3",
        "darkGray" => "#A9A9A9",
        "orange" => "#FFA500",
        _ => null,
    };

    private static (double R, double G, double B) Rgb(string hex) =>
        (Convert.ToInt32(hex[1..3], 16) / 255.0, Convert.ToInt32(hex[3..5], 16) / 255.0, Convert.ToInt32(hex[5..7], 16) / 255.0);

    private static string Hex(double r, double g, double b) =>
        $"#{(int)Math.Round(Math.Clamp(r, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(g, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(b, 0, 1) * 255):X2}";

    private static (double H, double S, double L) ToHsl(string hex)
    {
        var (r, g, b) = Rgb(hex);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        if (s == 0)
            return Hex(l, l, l);
        double Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Hex(Channel(p, q, h + 1 / 3.0), Channel(p, q, h), Channel(p, q, h - 1 / 3.0));
    }
}
