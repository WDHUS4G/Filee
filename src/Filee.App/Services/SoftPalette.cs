// The resources behind Filee's look that are computed rather than written in Styles/Soft.axaml: the accent colour
// with its shades, the corner radii, the UI font order and the reading direction. ThemeService applies them with the user's choices; the
// installer (src/Filee.Setup links this file) applies the defaults, so Setup looks like the app.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.App.Services;

internal static class SoftPalette
{
    /// <summary>The default accent colour (the first swatch of the Theme page).</summary>
    public const string DefaultAccent = "#7F77DD";

    /// <summary>The default corner radius (ThemeSettings.CornerRadius).</summary>
    public const double DefaultCornerRadius = 16;

    /// <summary>Embedded Noto fonts (see build/fetch-fonts.ps1). Missing files fall back to system fonts.</summary>
    private const string Latin = "fonts:Filee#Noto Sans";
    private const string Arabic = "fonts:Filee#Noto Sans Arabic";
    private const string Devanagari = "fonts:Filee#Noto Sans Devanagari";
    private const string Thai = "fonts:Filee#Noto Sans Thai";

    /// <summary>Each CJK region's embedded font and its system fonts (Windows, then macOS).</summary>
    private static readonly (string Embedded, string System) Japanese =
        ("fonts:Filee#Noto Sans JP", "Yu Gothic UI, Meiryo, Hiragino Sans");
    private static readonly (string Embedded, string System) Korean =
        ("fonts:Filee#Noto Sans KR", "Malgun Gothic, Apple SD Gothic Neo");
    private static readonly (string Embedded, string System) Chinese =
        ("fonts:Filee#Noto Sans SC", "Microsoft YaHei, PingFang SC");

    /// <summary>
    /// Traditional Chinese has no embedded font (two more Noto CJK files would add 11 MB): Windows and macOS always carry
    /// these. Listed before the embedded Noto Sans SC, whose Han shapes differ, so zh-TW text takes Traditional shapes.
    /// </summary>
    private const string TraditionalChineseSystem = "Microsoft JhengHei UI, Microsoft JhengHei, PingFang TC";

    /// <summary>All resource keys written by ApplyAccent that must be cleared from top level when per-variant dictionaries are used.</summary>
    public static readonly string[] AccentResourceKeys =
    [
        "AccentColor",
        "AccentBrush",
        "AccentSoftBrush",
        "AccentFaintBrush",
        "AccentStrongBrush",
        "OnAccentBrush",
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
    ];

    /// <summary>Removes top-level accent keys so ThemeDictionaries take effect without being shadowed.</summary>
    public static void ClearTopLevelAccent(IResourceDictionary res)
    {
        foreach (var key in AccentResourceKeys)
            res.Remove(key);
    }

    /// <summary>Accent colour + derived shades. Fluent controls read the System* keys.</summary>
    public static void ApplyAccent(IResourceDictionary res, Color accent)
    {
        res["AccentColor"] = accent;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentSoftBrush"] = new SolidColorBrush(accent, 0.18);
        res["AccentFaintBrush"] = new SolidColorBrush(accent, 0.09);
        res["AccentStrongBrush"] = new SolidColorBrush(Shade(accent, -0.25));
        res["OnAccentBrush"] = new SolidColorBrush(Luminance(accent) > 0.6 ? Color.Parse("#1F1E1D") : Colors.White);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Shade(accent, 0.15);
        res["SystemAccentColorLight2"] = Shade(accent, 0.30);
        res["SystemAccentColorLight3"] = Shade(accent, 0.45);
        res["SystemAccentColorDark1"] = Shade(accent, -0.15);
        res["SystemAccentColorDark2"] = Shade(accent, -0.30);
        res["SystemAccentColorDark3"] = Shade(accent, -0.45);
    }

    /// <summary>
    /// Applies variant-specific monochrome accent resources into a theme dictionary so selection
    /// maintains high contrast in both light (dark charcoal) and dark (silvery white) modes.
    /// </summary>
    public static void ApplyMonochromeAccent(IResourceDictionary dict, bool isDark)
    {
        var accent = isDark ? Color.Parse("#F1EFE8") : Color.Parse("#1F1E1D");
        var onAccent = isDark ? Color.Parse("#1F1E1D") : Colors.White;
        dict["AccentColor"] = accent;
        dict["AccentBrush"] = new SolidColorBrush(accent);
        dict["AccentSoftBrush"] = new SolidColorBrush(accent, isDark ? 0.18 : 0.12);
        dict["AccentFaintBrush"] = new SolidColorBrush(accent, isDark ? 0.08 : 0.05);
        dict["AccentStrongBrush"] = new SolidColorBrush(accent);
        dict["OnAccentBrush"] = new SolidColorBrush(onAccent);
        dict["SystemAccentColor"] = accent;
        dict["SystemAccentColorLight1"] = Shade(accent, 0.15);
        dict["SystemAccentColorLight2"] = Shade(accent, 0.30);
        dict["SystemAccentColorLight3"] = Shade(accent, 0.45);
        dict["SystemAccentColorDark1"] = Shade(accent, -0.15);
        dict["SystemAccentColorDark2"] = Shade(accent, -0.30);
        dict["SystemAccentColorDark3"] = Shade(accent, -0.45);
    }

    /// <summary>"Softness": one radius drives every control.</summary>
    public static void ApplyCornerRadius(IResourceDictionary res, double radius)
    {
        var r = Math.Clamp(radius, 0, 28);
        res["SoftRadius"] = new CornerRadius(r);
        res["SoftRadiusSmall"] = new CornerRadius(Math.Round(r * 0.55));
        res["SoftRadiusLarge"] = new CornerRadius(Math.Round(r * 1.35));
        res["ControlCornerRadius"] = new CornerRadius(Math.Round(r * 0.55));
        res["OverlayCornerRadius"] = new CornerRadius(r);
        res["SoftRadiusPill"] = new CornerRadius(Math.Round(r * 2.5));
    }

    /// <summary>
    /// The UI font chain. Han characters look different in Japanese, Korean and Chinese fonts, so the UI language's
    /// CJK fonts come first, embedded and system ones alike; other languages keep Korean first.
    /// </summary>
    public static string FontFamilies(string language)
    {
        (string Embedded, string System)[] cjk =
            language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? [Japanese, Korean, Chinese]
            : language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? [Chinese, Korean, Japanese]
            : [Korean, Chinese, Japanese];
        var traditional = AppLanguages.IsTraditionalChinese(language) ? $"{TraditionalChineseSystem}, " : "";
        // Noto Sans comes first: the first face also draws the trimming ellipsis, which Noto Sans Arabic lacks.
        return $"{Latin}, {Arabic}, {Devanagari}, {Thai}, {traditional}{string.Join(", ", cjk.Select(f => f.Embedded))}, Segoe UI, Nirmala UI, Leelawadee UI, "
            + $"{string.Join(", ", cjk.Select(f => f.System))}, Kohinoor Devanagari, Geeza Pro, Thonburi, sans-serif";
    }

    /// <summary>
    /// The font chain from Windows' own UI fonts only (no embedded fonts), with the UI language's CJK font first: what
    /// the installer uses. Every Windows 10 1809+ has these, with bold faces.
    /// </summary>
    public static string SystemFontFamilies(string language)
    {
        (string Embedded, string System)[] cjk =
            language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? [Japanese, Korean, Chinese]
            : language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? [Chinese, Korean, Japanese]
            : [Korean, Chinese, Japanese];
        var traditional = AppLanguages.IsTraditionalChinese(language) ? $"{TraditionalChineseSystem}, " : "";
        return $"Segoe UI, Nirmala UI, Leelawadee UI, {traditional}{string.Join(", ", cjk.Select(f => f.System))}, sans-serif";
    }

    /// <summary>The resource Soft.axaml's Window style takes <see cref="Visual.FlowDirection"/> from.</summary>
    public const string FlowDirectionKey = "AppFlowDirection";

    /// <summary>"→" between a name and what it becomes, "←" in a right-to-left language (arrows don't mirror).</summary>
    public const string ForwardArrowKey = "AppForwardArrow";

    /// <summary>
    /// Arabic reads right to left: every window mirrors its layout (Soft.axaml's Window style). Controls that draw a
    /// picture or a circle (the donut, the crop canvas, animated images) keep left to right themselves.
    /// </summary>
    public static void ApplyFlowDirection(IResourceDictionary res, string language)
    {
        var rightToLeft = IsRightToLeft(language);
        res[FlowDirectionKey] = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        res[ForwardArrowKey] = rightToLeft ? "←" : "→";
    }

    /// <summary>True for a language written right to left.</summary>
    public static bool IsRightToLeft(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language).TextInfo.IsRightToLeft;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The UI font for <paramref name="language"/>.</summary>
    public static FontFamily FontFamilyFor(string language) => FontFamily.Parse(FontFamilies(language));

    /// <summary>Lightens (amount &gt; 0) or darkens (amount &lt; 0) a colour.</summary>
    public static Color Shade(Color c, double amount)
    {
        byte Mix(byte v) => amount >= 0
            ? (byte)Math.Round(v + (255 - v) * amount)
            : (byte)Math.Round(v * (1 + amount));
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>Relative luminance 0..1 (sRGB approximation).</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
