// The display list the CAD renderer draws. The scene builder resolves layers, colours, line weights, line types and
// block transforms once; the result is plain geometry in WCS, so one drawing routine serves PDF, SVG and PNG and
// the page is fitted to what is actually drawn (not to the header's EXTMIN/EXTMAX).

using SkiaSharp;

namespace Filee.Engines.Cad;

/// <summary>Something to draw, in a resolved colour.</summary>
internal abstract class ScenePrimitive
{
    public SKColor Color { get; init; }
}

/// <summary>Stroked figures: lines, arcs, polylines, curves, text-less outlines.</summary>
internal sealed class StrokePrimitive : ScenePrimitive
{
    public List<Figure> Figures { get; } = [];

    /// <summary>Line weight on paper in millimetres.</summary>
    public double WidthMm { get; init; }

    /// <summary>
    /// Width in drawing units for polylines with a constant width (drawn with butt caps); 0 for normal lines,
    /// which use <see cref="WidthMm"/>.
    /// </summary>
    public double WorldWidth { get; init; }

    /// <summary>Dash pattern in drawing units (&gt;0 dash, &lt;0 gap, 0 dot); null for continuous lines.</summary>
    public double[]? Dashes { get; init; }
}

/// <summary>Filled areas: solid hatches, SOLID/TRACE, wide polyline segments. Loops use the even-odd rule.</summary>
internal sealed class FillPrimitive : ScenePrimitive
{
    public List<Figure> Figures { get; } = [];

    /// <summary>Second colour of a gradient hatch, drawn as a linear gradient along <see cref="GradientAngle"/>.</summary>
    public SKColor? GradientColor { get; init; }

    public double GradientAngle { get; init; }
}

/// <summary>Pattern hatch lines, clipped by the hatch boundary when drawn.</summary>
internal sealed class HatchLinesPrimitive : ScenePrimitive
{
    public List<Figure> Boundary { get; } = [];
    public List<(Vec2 A, Vec2 B)> Lines { get; } = [];
    public double WidthMm { get; init; }
}

/// <summary>
/// One run of text in one typeface. Glyph coordinates at font size 1 (Skia convention, y down) map to WCS as
/// <c>Origin + gx·XAxis + gy·YAxis</c>, which covers rotation, width factor, oblique angle and mirrored blocks.
/// </summary>
internal sealed class TextPrimitive : ScenePrimitive
{
    public required string Text { get; init; }
    public required SKTypeface Typeface { get; init; }
    public Vec2 Origin { get; init; }
    public Vec2 XAxis { get; init; }
    public Vec2 YAxis { get; init; }
}

/// <summary>XLINE and RAY: clipped to the page when drawn.</summary>
internal sealed class InfiniteLinePrimitive : ScenePrimitive
{
    public Vec2 Origin { get; init; }
    public Vec2 Direction { get; init; }
    public bool IsRay { get; init; }
    public double WidthMm { get; init; }
    public double[]? Dashes { get; init; }
}

/// <summary>A POINT, drawn as a dot of its line weight.</summary>
internal sealed class PointPrimitive : ScenePrimitive
{
    public Vec2 Location { get; init; }
    public double WidthMm { get; init; }
}

/// <summary>Everything to draw plus what was left out.</summary>
internal sealed class CadScene
{
    public List<ScenePrimitive> Items { get; } = [];

    /// <summary>Bounds of all finite primitives in WCS.</summary>
    public Box2 Extents = Box2.Empty;

    /// <summary>Entity types that were not drawn, with their count (for the log).</summary>
    public SortedDictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

    /// <summary>True when a limit (primitive count, block nesting, array size) cut the drawing short.</summary>
    public bool Truncated { get; set; }

    /// <summary>Which space was drawn: "model" or the layout name.</summary>
    public string Space { get; set; } = "model";
}
