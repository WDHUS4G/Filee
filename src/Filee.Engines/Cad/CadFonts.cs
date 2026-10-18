// Fonts for CAD text. Drawings name SHX fonts (txt.shx, romans.shx, Korean big fonts) that Filee does not ship, so
// text is drawn with a sans-serif typeface; a TrueType style font (arial.ttf, malgun.ttf ...) is used when available.
// Characters the chosen typeface lacks (Hangul, Hanzi ...) fall back per character to a CJK font.

using System.Collections.Concurrent;
using SkiaSharp;

namespace Filee.Engines.Cad;

/// <summary>A piece of one text line drawn with one typeface.</summary>
/// <param name="Advance">Advance width in em (font size 1).</param>
internal readonly record struct GlyphRun(string Text, SKTypeface Typeface, double Advance);

/// <summary>Typeface lookup and text measurement shared by the scene builder and the renderer.</summary>
internal static class CadFonts
{
    /// <summary>Font size used for measuring; advances are divided by it (small sizes suffer from rounding).</summary>
    private const float MeasureSize = 64;

    /// <summary>Families tried for Latin text, in order.</summary>
    private static readonly string[] SansFamilies =
        ["Noto Sans", "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Segoe UI"];

    /// <summary>
    /// Families tried for characters the main typeface lacks: Korean first (Filee's main audience), then Chinese and
    /// Japanese. Only fonts Skia can find are used; without any, SkiaSharp's own fallback decides.
    /// </summary>
    private static readonly string[] CjkFamilies =
    [
        "Noto Sans KR", "Noto Sans CJK KR", "Malgun Gothic", "Apple SD Gothic Neo", "NanumGothic",
        "Noto Sans SC", "Noto Sans CJK SC", "Microsoft YaHei", "PingFang SC", "Yu Gothic", "MS Gothic",
    ];

    /// <summary>Common Windows font files named by text styles, mapped to their family names.</summary>
    private static readonly Dictionary<string, string> FileFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arial"] = "Arial",
        ["arialn"] = "Arial Narrow",
        ["times"] = "Times New Roman",
        ["cour"] = "Courier New",
        ["calibri"] = "Calibri",
        ["tahoma"] = "Tahoma",
        ["verdana"] = "Verdana",
        ["isocpeur"] = "ISOCPEUR",
        ["malgun"] = "Malgun Gothic",
        ["gulim"] = "Gulim",
        ["batang"] = "Batang",
        ["dotum"] = "Dotum",
        ["gungsuh"] = "Gungsuh",
        ["simsun"] = "SimSun",
        ["simhei"] = "SimHei",
        ["msyh"] = "Microsoft YaHei",
        ["msgothic"] = "MS Gothic",
        ["msmincho"] = "MS Mincho",
    };

    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SKTypeface?> Families = new();
    private static readonly ConcurrentDictionary<(int CodePoint, SKTypeface Primary), SKTypeface?> Fallbacks = new();
    private static readonly ConcurrentDictionary<(bool Bold, bool Italic), SKTypeface> Defaults = new();
    private static readonly ConcurrentDictionary<SKTypeface, (double CapHeight, double Descent)> FontMetrics = new();

    [ThreadStatic]
    private static Dictionary<SKTypeface, SKFont>? _measuringFonts;

    /// <summary>
    /// The typeface for a text style: its TrueType family if installed (family name or font file), else the
    /// default sans-serif typeface. Never null.
    /// </summary>
    public static SKTypeface Resolve(string? familyOrFile, bool bold, bool italic)
    {
        var family = FamilyOf(familyOrFile);
        if (family is not null && Find(family, bold, italic) is { } typeface)
            return typeface;
        return Defaults.GetOrAdd((bold, italic), key =>
            SansFamilies.Select(f => Find(f, key.Bold, key.Italic)).FirstOrDefault(t => t is not null)
            ?? SKTypeface.FromFamilyName(null, Style(key.Bold, key.Italic))
            ?? SKTypeface.Default);
    }

    /// <summary>Splits a line into runs by typeface coverage and measures each run in em.</summary>
    public static List<GlyphRun> Layout(string text, SKTypeface primary)
    {
        var runs = new List<GlyphRun>();
        if (text.Length == 0)
            return runs;

        var start = 0;
        SKTypeface? current = null;
        for (var i = 0; i < text.Length;)
        {
            var width = char.IsSurrogatePair(text, i) ? 2 : 1;
            var codePoint = width == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            // Spaces stay in the current run, so a Korean phrase is one run in one typeface.
            var face = char.IsWhiteSpace(text[i]) ? current ?? primary
                : primary.ContainsGlyph(codePoint) ? primary
                : Fallback(codePoint, primary);
            if (current is not null && !ReferenceEquals(face, current))
            {
                runs.Add(Measure(text[start..i], current));
                start = i;
            }
            current = face;
            i += width;
        }
        runs.Add(Measure(text[start..], current!));
        return runs;
    }

    /// <summary>Cap height and descent of a typeface as fractions of the em size.</summary>
    public static (double CapHeight, double Descent) Metrics(SKTypeface typeface) =>
        FontMetrics.GetOrAdd(typeface, face =>
        {
            var metrics = MeasuringFont(face).Metrics;
            var cap = metrics.CapHeight / MeasureSize;
            var descent = metrics.Descent / MeasureSize;
            // Some fonts report no cap height; 0.7 em is typical for sans-serif faces.
            return (cap is > 0.3f and < 1.2f ? cap : 0.7, descent is > 0 and < 1 ? descent : 0.21);
        });

    /// <summary>A font set up for geometric layout: no hinting, so measured and drawn advances agree at any scale.</summary>
    public static SKFont CreateFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Hinting = SKFontHinting.None,
        Subpixel = true,
        LinearMetrics = true,
        Edging = SKFontEdging.Antialias,
    };

    private static GlyphRun Measure(string text, SKTypeface typeface)
    {
        double advance = MeasuringFont(typeface).MeasureText(text) / MeasureSize;
        if (advance <= 0 && text.Trim().Length > 0)
            advance = 0.6 * text.Length; // a typeface without glyphs: keep a plausible width for extents and alignment
        return new GlyphRun(text, typeface, advance);
    }

    /// <summary>
    /// One measuring font per typeface and thread (SKFont is not thread-safe, and creating one per text is slow
    /// for drawings with tens of thousands of texts).
    /// </summary>
    private static SKFont MeasuringFont(SKTypeface typeface)
    {
        _measuringFonts ??= [];
        if (!_measuringFonts.TryGetValue(typeface, out var font))
            _measuringFonts[typeface] = font = CreateFont(typeface, MeasureSize);
        return font;
    }

    private static SKTypeface Fallback(int codePoint, SKTypeface primary) =>
        Fallbacks.GetOrAdd((codePoint, primary), key =>
        {
            using var style = key.Primary.FontStyle;
            var bold = style.Weight >= (int)SKFontStyleWeight.SemiBold;
            foreach (var family in CjkFamilies)
                if (Find(family, bold, false) is { } face && face.ContainsGlyph(key.CodePoint))
                    return face;
            return SKFontManager.Default.MatchCharacter(null, Style(bold, false), ["ko", "zh", "ja"], key.CodePoint);
        }) ?? primary;

    private static SKTypeface? Find(string family, bool bold, bool italic) =>
        Families.GetOrAdd((family, bold, italic), key =>
        {
            // MatchFamily returns null for unknown families (FromFamilyName would silently substitute).
            var face = SKFontManager.Default.MatchFamily(key.Family, Style(key.Bold, key.Italic));
            return face is not null && face.GlyphCount > 0 ? face : null;
        });

    private static string? FamilyOf(string? familyOrFile)
    {
        if (string.IsNullOrWhiteSpace(familyOrFile))
            return null;
        var name = familyOrFile.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Equals(".shx", StringComparison.OrdinalIgnoreCase))
            return null;
        if (extension.Length > 0)
            name = Path.GetFileNameWithoutExtension(name);
        return FileFamilies.TryGetValue(name, out var family) ? family : name;
    }

    private static SKFontStyle Style(bool bold, bool italic) => new(
        bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
        SKFontStyleWidth.Normal,
        italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
}
