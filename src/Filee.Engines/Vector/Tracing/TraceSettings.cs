// Turns the preset's three tracing options (mode, colours, detail) into the numbers the tracing steps use.

using Filee.Core.Presets;

namespace Filee.Engines.Vector.Tracing;

/// <summary>Parameters of one tracing run, derived from <see cref="TracingOptions"/>.</summary>
internal sealed record TraceSettings
{
    public TracingMode Mode { get; init; } = TracingMode.Color;

    /// <summary>Colours of the palette (colour mode).</summary>
    public int Colors { get; init; } = 16;

    /// <summary>Outlines enclosing fewer square pixels than this are dropped (specks, and holes that get filled).</summary>
    public double SpeckleArea { get; init; } = 6;

    /// <summary>Colour mode: connected regions smaller than this many pixels join the neighbouring region.</summary>
    public int MinRegion { get; init; } = 16;

    /// <summary>
    /// Colour mode: thin bands of in-between colours along edges (anti-aliasing) join the colours on either side.
    /// </summary>
    public bool MergeEdgeBands { get; init; } = true;

    /// <summary>Largest distance, in pixels, between the fitted curves and the smoothed outline.</summary>
    public double CurveTolerance { get; init; } = 0.7;

    /// <summary>A turn sharper than this many degrees within a couple of pixels is kept as a corner.</summary>
    public double CornerAngle { get; init; } = 55;

    /// <summary>A pixel corner between two straight runs at least this long is kept as a corner (rectangles).</summary>
    public int CornerRun { get; init; } = 3;

    /// <summary>Passes of neighbour averaging over the outline before fitting.</summary>
    public int SmoothingPasses { get; init; } = 2;

    /// <summary>The settings for a preset; <paramref name="longEdge"/> is the traced image's longer side in pixels.</summary>
    public static TraceSettings From(TracingOptions options, int longEdge)
    {
        var f = Math.Clamp(options.Detail, 0, 100) / 100.0;
        var coarse = (1 - f) * (1 - f);
        // Specks are counted in pixels, so a larger picture of the same artwork has larger specks.
        var sizeFactor = Math.Clamp(longEdge / 1000.0, 1, 4);
        return new TraceSettings
        {
            Mode = options.Mode,
            Colors = Math.Clamp(options.Colors, TracingOptions.MinColors, TracingOptions.MaxColors),
            SpeckleArea = Math.Round(24 * coarse * sizeFactor),
            MinRegion = (int)Math.Round(1 + 64 * coarse * sizeFactor),
            MergeEdgeBands = f < 0.9,
            CurveTolerance = 0.25 + 0.9 * (1 - f),
            CornerAngle = 70 - 30 * f,
            CornerRun = f > 0.8 ? 2 : 3,
            SmoothingPasses = f < 0.34 ? 3 : f < 0.67 ? 2 : f < 0.9 ? 1 : 0,
        };
    }
}
