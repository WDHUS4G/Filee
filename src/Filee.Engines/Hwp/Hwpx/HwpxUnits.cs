// Unit conversions shared by the HWPX readers and writer. HWPUNIT = 1/7200 inch.

using System.Globalization;

namespace Filee.Engines.Hwp.Hwpx;

internal static class HwpxUnits
{
    public const double PerMm = 7200 / 25.4;
    public const double PerPixel = 7200 / 96.0;

    /// <summary>Word twips (1/1440 inch) → HWPUNIT.</summary>
    public static int FromTwips(double twips) => (int)Math.Round(twips * 5);

    /// <summary>DrawingML EMU (1/914400 inch) → HWPUNIT.</summary>
    public static int FromEmu(double emu) => (int)Math.Round(emu / 127);

    /// <summary>Points → HWPUNIT.</summary>
    public static int FromPoints(double points) => (int)Math.Round(points * 100);

    /// <summary>Parses a CSS-like length ("2in", "300px", "12pt", "50%") into HWPUNIT.</summary>
    public static int? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim().ToLowerInvariant();
        var split = 0;
        while (split < text.Length && (char.IsDigit(text[split]) || text[split] is '.' or '-'))
            split++;
        if (!double.TryParse(text[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        var mm = text[split..].Trim() switch
        {
            "in" => number * 25.4,
            "cm" => number * 10,
            "mm" => number,
            "pt" => number * 25.4 / 72,
            "pc" => number * 25.4 / 6,
            "emu" => number / 36000,
            "%" => number * 1.5, // % of a ~150 mm text width
            _ => number * 25.4 / 96, // px or unitless
        };
        return (int)Math.Round(mm * PerMm);
    }
}
