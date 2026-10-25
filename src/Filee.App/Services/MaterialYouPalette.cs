// Material Design 3 dynamic tonal palette generator for Filee.
// Generates variant-specific theme resources (surfaces, text, borders, shadows, and accent roles)
// from a seed colour using perceptual HSL tonal mapping. Built strictly on Filee's existing theme resource keys.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.App.Services;

/// <summary>Resolved Material Design 3 theme resources for one variant (Light or Dark).</summary>
public sealed class MaterialYouPalette
{
    public required Color Page { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color SubtleBorder { get; init; }
    public required Color TextPrimary { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }
    public required Color Warning { get; init; }
    public required BoxShadows CardShadow { get; init; }

    public required Color Accent { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentFaint { get; init; }
    public required Color AccentStrong { get; init; }
    public required Color OnAccent { get; init; }

    /// <summary>Creates an Avalonia ResourceDictionary mapping standard Filee theme keys to solid brushes and resources.</summary>
    public ResourceDictionary ToResourceDictionary() => new()
    {
        ["PageBrush"] = new SolidColorBrush(Page),
        ["SurfaceBrush"] = new SolidColorBrush(Surface),
        ["SurfaceAltBrush"] = new SolidColorBrush(SurfaceAlt),
        ["SubtleBorderBrush"] = new SolidColorBrush(SubtleBorder),
        ["TextPrimaryBrush"] = new SolidColorBrush(TextPrimary),
        ["TextSecondaryBrush"] = new SolidColorBrush(TextSecondary),
        ["DangerBrush"] = new SolidColorBrush(Danger),
        ["SuccessBrush"] = new SolidColorBrush(Success),
        ["WarningBrush"] = new SolidColorBrush(Warning),
        ["CardShadow"] = CardShadow,

        ["AccentColor"] = Accent,
        ["AccentBrush"] = new SolidColorBrush(Accent),
        ["AccentSoftBrush"] = new SolidColorBrush(AccentSoft),
        ["AccentFaintBrush"] = new SolidColorBrush(AccentFaint),
        ["AccentStrongBrush"] = new SolidColorBrush(AccentStrong),
        ["OnAccentBrush"] = new SolidColorBrush(OnAccent),
        ["SystemAccentColor"] = Accent,
        ["SystemAccentColorLight1"] = SoftPalette.Shade(Accent, 0.15),
        ["SystemAccentColorLight2"] = SoftPalette.Shade(Accent, 0.30),
        ["SystemAccentColorLight3"] = SoftPalette.Shade(Accent, 0.45),
        ["SystemAccentColorDark1"] = SoftPalette.Shade(Accent, -0.15),
        ["SystemAccentColorDark2"] = SoftPalette.Shade(Accent, -0.30),
        ["SystemAccentColorDark3"] = SoftPalette.Shade(Accent, -0.45),
    };

    /// <summary>True if the colour is monochrome (low saturation or black/white/charcoal).</summary>
    public static bool IsMonochrome(Color c)
    {
        var (_, s, _) = RgbToHsl(c);
        return s < 0.08 ||
               (c.R == c.G && c.G == c.B) ||
               (Math.Abs(c.R - c.G) <= 4 && Math.Abs(c.G - c.B) <= 4 && Math.Abs(c.R - c.B) <= 4);
    }

    /// <summary>Calculates standard sRGB relative luminance.</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Generates Material You tonal resources for the specified seed and variant.</summary>
    public static MaterialYouPalette Generate(Color seed, bool isDark)
    {
        if (IsMonochrome(seed))
            return GenerateMonochrome(isDark);

        var (h, s, l) = RgbToHsl(seed);
        var sat = Math.Clamp(s, 0.20, 0.85);

        if (!isDark)
        {
            var primary = l > 0.60 ? HslToRgb(h, sat, 0.45) : seed;
            var onPrimary = Luminance(primary) > 0.55 ? Color.Parse("#1F1E1D") : Colors.White;
            var primaryContainer = HslToRgb(h, Math.Min(sat * 0.5, 0.40), 0.90);
            var onPrimaryContainer = HslToRgb(h, Math.Min(sat * 0.7, 0.60), 0.22);

            return new MaterialYouPalette
            {
                Page = HslToRgb(h, Math.Min(sat * 0.18, 0.12), 0.97),
                Surface = Colors.White,
                SurfaceAlt = HslToRgb(h, Math.Min(sat * 0.22, 0.16), 0.92),
                SubtleBorder = Color.FromArgb(28, primary.R, primary.G, primary.B),
                TextPrimary = HslToRgb(h, 0.10, 0.12),
                TextSecondary = HslToRgb(h, 0.15, 0.38),
                Danger = Color.Parse("#BA1A1A"),
                Success = Color.Parse("#1B6D24"),
                Warning = Color.Parse("#825500"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #12000000, 0 6 18 0 #0D000000"),

                Accent = primary,
                OnAccent = onPrimary,
                AccentSoft = primaryContainer,
                AccentFaint = HslToRgb(h, Math.Min(sat * 0.35, 0.25), 0.95),
                AccentStrong = onPrimaryContainer,
            };
        }
        else
        {
            var darkPrimary = l < 0.65 ? HslToRgb(h, Math.Min(sat * 0.75, 0.65), 0.75) : seed;
            var onPrimary = Luminance(darkPrimary) > 0.45 ? Color.Parse("#16141A") : Colors.White;
            var primaryContainer = HslToRgb(h, Math.Min(sat * 0.5, 0.38), 0.26);
            var onPrimaryContainer = HslToRgb(h, Math.Min(sat * 0.7, 0.55), 0.88);

            return new MaterialYouPalette
            {
                Page = HslToRgb(h, Math.Min(sat * 0.18, 0.12), 0.08),
                Surface = HslToRgb(h, Math.Min(sat * 0.20, 0.14), 0.13),
                SurfaceAlt = HslToRgb(h, Math.Min(sat * 0.22, 0.16), 0.18),
                SubtleBorder = Color.FromArgb(32, darkPrimary.R, darkPrimary.G, darkPrimary.B),
                TextPrimary = HslToRgb(h, 0.08, 0.92),
                TextSecondary = HslToRgb(h, 0.12, 0.75),
                Danger = Color.Parse("#FFB4AB"),
                Success = Color.Parse("#8CD692"),
                Warning = Color.Parse("#FFBA2F"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #40000000, 0 8 24 0 #33000000"),

                Accent = darkPrimary,
                OnAccent = onPrimary,
                AccentSoft = primaryContainer,
                AccentFaint = HslToRgb(h, Math.Min(sat * 0.30, 0.20), 0.15),
                AccentStrong = onPrimaryContainer,
            };
        }
    }

    private static MaterialYouPalette GenerateMonochrome(bool isDark)
    {
        if (!isDark)
        {
            var accent = Color.Parse("#1F1E1D");
            return new MaterialYouPalette
            {
                Page = Color.Parse("#F7F6F2"),
                Surface = Colors.White,
                SurfaceAlt = Color.Parse("#EEECE5"),
                SubtleBorder = Color.FromArgb(24, 0, 0, 0),
                TextPrimary = Color.Parse("#1F1E1D"),
                TextSecondary = Color.Parse("#6E6C67"),
                Danger = Color.Parse("#C73A39"),
                Success = Color.Parse("#0F7A5A"),
                Warning = Color.Parse("#9A5B0C"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #12000000, 0 6 18 0 #0D000000"),

                Accent = accent,
                OnAccent = Colors.White,
                AccentSoft = Color.FromArgb(32, accent.R, accent.G, accent.B),
                AccentFaint = Color.FromArgb(14, accent.R, accent.G, accent.B),
                AccentStrong = accent,
            };
        }
        else
        {
            var accent = Color.Parse("#F1EFE8");
            return new MaterialYouPalette
            {
                Page = Color.Parse("#161614"),
                Surface = Color.Parse("#222220"),
                SurfaceAlt = Color.Parse("#2C2C29"),
                SubtleBorder = Color.FromArgb(28, 255, 255, 255),
                TextPrimary = Color.Parse("#F1EFE8"),
                TextSecondary = Color.Parse("#AEACA4"),
                Danger = Color.Parse("#F09595"),
                Success = Color.Parse("#5DCAA5"),
                Warning = Color.Parse("#EF9F27"),
                CardShadow = BoxShadows.Parse("0 1 3 0 #40000000, 0 8 24 0 #33000000"),

                Accent = accent,
                OnAccent = Color.Parse("#1F1E1D"),
                AccentSoft = Color.FromArgb(46, accent.R, accent.G, accent.B),
                AccentFaint = Color.FromArgb(20, accent.R, accent.G, accent.B),
                AccentStrong = accent,
            };
        }
    }

    public static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0.0;
        double s = 0.0;
        double l = (max + min) / 2.0;

        if (delta > 1e-5)
        {
            s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

            if (Math.Abs(max - r) < 1e-5)
                h = ((g - b) / delta + (g < b ? 6.0 : 0.0)) / 6.0;
            else if (Math.Abs(max - g) < 1e-5)
                h = ((b - r) / delta + 2.0) / 6.0;
            else
                h = ((r - g) / delta + 4.0) / 6.0;
        }

        return (h, s, l);
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        h = (h % 1.0 + 1.0) % 1.0;
        s = Math.Clamp(s, 0.0, 1.0);
        l = Math.Clamp(l, 0.0, 1.0);

        if (s < 1e-5)
        {
            byte grey = (byte)Math.Clamp(Math.Round(l * 255.0), 0, 255);
            return Color.FromRgb(grey, grey, grey);
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;

        byte r = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255.0), 0, 255);

        return Color.FromRgb(r, g, b);
    }
}
