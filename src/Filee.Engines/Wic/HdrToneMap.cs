// HDR → SDR for linear scRGB pictures, the float JPEG XR files Windows saves for screenshots on an HDR display (Xbox
// Game Bar, Win + Alt + PrtScn, Snipping Tool). In scRGB 1.0 is SDR white at 80 nits, but Windows draws SDR content
// (the desktop, windows, a game's menus) at the "SDR content brightness" of the display, 80 to 480 nits (1.0 to 6.0),
// and HDR highlights go well beyond that. Clipping at 1.0 blows out every picture taken with SDR brightness above the
// minimum; dividing by the brightest pixel makes the picture dark when a small highlight is very bright.
//
// So, simple and predictable:
//  1. Reference white: the luminance 99 % of the pixels stay below, at least 1.0 (scRGB's own SDR white) and at most
//     4.0 (320 nits). The white of a desktop screenshot lands there (SDR brightness up to 320 nits), so its SDR parts
//     come out as they were drawn; in a game, the brightest 1 % is treated as white. Pixels are divided by it.
//  2. Up to 80 % of that white nothing changes. Above it an extended Reinhard shoulder takes the range up to the
//     brightest pixel smoothly into the last 20 %: highlights keep their detail instead of clipping, and a picture
//     without anything brighter than white is left as it is. The curve works on the largest of R, G and B and scales
//     the three by the same factor, so colours keep their hue and nothing clips.
//  3. Negative values (colours outside the sRGB gamut) are clipped to 0, then the sRGB transfer curve and 8 bits.

namespace Filee.Engines.Wic;

/// <summary>Maps linear scRGB pixels to 8-bit sRGB.</summary>
internal sealed class HdrToneMap
{
    /// <summary>The share of pixels at or below the reference white.</summary>
    public const double WhitePercentile = 0.99;

    /// <summary>Highest reference white, in scRGB (4.0 = 320 nits).</summary>
    public const float MaxWhite = 4f;

    /// <summary>Where the shoulder starts, as a fraction of the reference white.</summary>
    public const float Knee = 0.8f;

    /// <summary>At most this many luminance samples choose the reference white.</summary>
    private const int MaxSamples = 1 << 16;

    private readonly float _white;
    private readonly float _range;

    /// <param name="white">Reference white (scRGB), see <see cref="ReferenceWhite"/>.</param>
    /// <param name="peak">Largest R, G or B value of the picture (scRGB).</param>
    public HdrToneMap(float white, float peak)
    {
        _white = white;
        // The shoulder's end in its own units (0 at the knee, 1 at white): extended Reinhard maps it exactly to 1.
        _range = Math.Max(1, (peak / white - Knee) / (1 - Knee));
    }

    /// <summary>
    /// The reference white of a picture from the luminance of (a sample of) its pixels, between 1 and <see cref="MaxWhite"/>.
    /// </summary>
    public static float ReferenceWhite(IReadOnlyList<float> luminances)
    {
        if (luminances.Count == 0)
            return 1;
        var sorted = luminances.ToArray();
        Array.Sort(sorted);
        var white = sorted[(int)Math.Round(WhitePercentile * (sorted.Length - 1))];
        return float.IsNaN(white) ? 1 : Math.Clamp(white, 1, MaxWhite);
    }

    /// <summary>How many pixels to skip between two luminance samples of a <paramref name="pixels"/> pixel picture.</summary>
    public static long SampleStep(long pixels) => Math.Max(1, (pixels + MaxSamples - 1) / MaxSamples);

    /// <summary>Rec. 709 / sRGB luminance of a linear pixel, its negative (out of gamut) channels taken as 0.</summary>
    public static float Luminance(float r, float g, float b) =>
        0.2126f * Positive(r) + 0.7152f * Positive(g) + 0.0722f * Positive(b);

    /// <summary>Largest channel of a linear pixel, at least 0.</summary>
    public static float MaxChannel(float r, float g, float b) => Math.Max(Positive(r), Math.Max(Positive(g), Positive(b)));

    /// <summary>Tone curve on values relative to the reference white: the identity up to the knee, then the shoulder.</summary>
    public float Curve(float relative)
    {
        if (relative <= Knee)
            return relative;
        var x = (relative - Knee) / (1 - Knee);
        var shoulder = x * (1 + x / (_range * _range)) / (1 + x);
        return Math.Min(1, Knee + (1 - Knee) * shoulder);
    }

    /// <summary>Maps one linear RGBA pixel to 8-bit sRGB RGBA.</summary>
    public void Map(float r, float g, float b, float a, Span<byte> rgba)
    {
        r = Positive(r) / _white;
        g = Positive(g) / _white;
        b = Positive(b) / _white;
        var max = Math.Max(r, Math.Max(g, b));
        var scale = max > Knee ? Curve(max) / max : 1;
        rgba[0] = Encode(r * scale);
        rgba[1] = Encode(g * scale);
        rgba[2] = Encode(b * scale);
        rgba[3] = (byte)Math.Round(Math.Clamp(float.IsNaN(a) ? 1 : a, 0, 1) * 255);
    }

    /// <summary>sRGB transfer curve (IEC 61966-2-1) and 8 bits, from a table: a 4K screenshot has 25 million values.</summary>
    public static byte Encode(float linear) => EncodeTable[(int)(Math.Clamp(linear, 0, 1) * (EncodeTable.Length - 1) + 0.5f)];

    /// <summary>
    /// 8-bit sRGB of 65,536 linear steps. The darkest step is 1/20 of an 8-bit level, so the table loses nothing.
    /// </summary>
    private static readonly byte[] EncodeTable = [.. Enumerable.Range(0, 65536).Select(i =>
    {
        var linear = i / 65535.0;
        var encoded = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(encoded * 255);
    })];

    /// <summary>The value, 0 for negative values and NaN, at most the largest half float (infinity included).</summary>
    private static float Positive(float value) => value > 0 ? Math.Min(value, 65504f) : 0;
}
