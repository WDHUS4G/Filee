// Applies ThemeSettings (light/dark, accent colour, corner radius, font order, reduced motion) at runtime by
// writing resources into Application.Resources. All styles reference these with DynamicResource.

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Services;

/// <summary>Runtime theming.</summary>
public sealed class ThemeService(IPlatformServices platform)
{
    /// <summary>Accent swatches offered in the Theme page.</summary>
    public static readonly IReadOnlyList<string> Swatches =
        ["#7F77DD", "#1D9E75", "#D85A30", "#378ADD", "#D4537E", "#BA7517", "#639922", "#5F5E5A"];

    /// <summary>Embedded Noto fonts (see build/fetch-fonts.ps1). Missing files fall back to system fonts.</summary>
    private const string Latin = "fonts:Filee#Noto Sans";
    private const string Korean = "fonts:Filee#Noto Sans KR";
    private const string Chinese = "fonts:Filee#Noto Sans SC";
    private const string SystemFallbacks = "Segoe UI, Malgun Gothic, Microsoft YaHei, PingFang SC, Apple SD Gothic Neo, sans-serif";

    public void Apply(ThemeSettings theme, string language)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No application.");
        var res = app.Resources;

        app.RequestedThemeVariant = theme.Mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        // Accent colour + derived shades. Fluent controls read the System* keys.
        var accent = Color.TryParse(theme.Accent, out var parsed) ? parsed : Color.Parse("#7F77DD");
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

        // "Softness": one radius drives every control.
        var r = Math.Clamp(theme.CornerRadius, 0, 28);
        res["SoftRadius"] = new CornerRadius(r);
        res["SoftRadiusSmall"] = new CornerRadius(Math.Round(r * 0.55));
        res["SoftRadiusLarge"] = new CornerRadius(Math.Round(r * 1.35));
        res["ControlCornerRadius"] = new CornerRadius(Math.Round(r * 0.55));
        res["OverlayCornerRadius"] = new CornerRadius(r);

        // Han characters look different in Korean and Chinese fonts: put the UI language's font first.
        var cjk = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"{Chinese}, {Korean}"
            : $"{Korean}, {Chinese}";
        res["AppFontFamily"] = FontFamily.Parse($"{Latin}, {cjk}, {SystemFallbacks}");

        Motion.Configure(theme.ReduceMotion, () => platform.PrefersReducedMotion);
    }

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
