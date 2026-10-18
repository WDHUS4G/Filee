// Builds CAD drawings for the tests with ACadSharp, so no sample DWG/DXF files are needed.

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace Filee.Engines.Tests;

/// <summary>Test drawings: one with every commonly used entity, plus small special-purpose ones.</summary>
internal static class CadBuilder
{
    /// <summary>ACI 5: everything that must be visible is blue.</summary>
    public static readonly Color Visible = new(5);

    /// <summary>ACI 1: everything on hidden layers is red, so a red pixel means a hidden layer was drawn.</summary>
    public static readonly Color Hidden = new(1);

    public const string KoreanText = "도면 제목 Filee";

    /// <summary>
    /// A drawing with lines, a circle, an arc, a polyline with a bulge, TEXT (Korean), MTEXT, a block with an
    /// attribute inserted twice (rotated and scaled), a solid and a pattern hatch, a dashed line, a spline, a partial
    /// ellipse, an aligned dimension, and entities on an OFF and a frozen layer.
    /// </summary>
    public static CadDocument Sample()
    {
        var doc = new CadDocument();
        var drawing = new Layer("Drawing") { Color = Visible };
        var off = new Layer("Off") { Color = Hidden, IsOn = false };
        var frozen = new Layer("Frozen") { Color = Hidden, Flags = LayerFlags.Frozen };
        doc.Layers.Add(drawing);
        doc.Layers.Add(off);
        doc.Layers.Add(frozen);

        Add(doc, drawing, new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
        Add(doc, drawing, new Line(new XYZ(100, 0, 0), new XYZ(100, 60, 0)));
        Add(doc, drawing, new Circle(new XYZ(30, 30, 0), 12));
        Add(doc, drawing, new Arc(new XYZ(70, 30, 0), 10, 0, Math.PI));
        Add(doc, drawing, new LwPolyline(
            new LwPolyline.Vertex(0, 60) { Bulge = 0.5 },
            new LwPolyline.Vertex(40, 60),
            new LwPolyline.Vertex(40, 80)));
        Add(doc, drawing, new TextEntity(KoreanText) { InsertPoint = new XYZ(5, 5, 0), Height = 5 });
        Add(doc, drawing, new MText("{\\H1.5x;MTEXT}\\P두 번째 줄 \\S1/2;") { InsertPoint = new XYZ(50, 80, 0), Height = 3, RectangleWidth = 60 });

        var block = new BlockRecord("BOLT");
        block.Entities.Add(new Circle(new XYZ(0, 0, 0), 3));
        block.Entities.Add(new Line(new XYZ(-5, 0, 0), new XYZ(5, 0, 0)));
        block.Entities.Add(new AttributeDefinition { Tag = "ID", Value = "B?", InsertPoint = new XYZ(4, 4, 0), Height = 2 });
        doc.BlockRecords.Add(block);
        // A new Insert gets one attribute per definition (placed at the definition); set the values per reference.
        var bolt1 = new Insert(block) { InsertPoint = new XYZ(80, 50, 0) };
        bolt1.Attributes.Single().Value = "B1";
        bolt1.Attributes.Single().InsertPoint = new XYZ(84, 54, 0);
        Add(doc, drawing, bolt1);
        var bolt2 = new Insert(block) { InsertPoint = new XYZ(80, 15, 0), XScale = 2, YScale = 2, Rotation = Math.PI / 6 };
        bolt2.Attributes.Single().Value = "B2";
        bolt2.Attributes.Single().InsertPoint = new XYZ(84, 19, 0);
        Add(doc, drawing, bolt2);

        var hatch = new Hatch { IsSolid = true };
        hatch.Paths.Add(Rectangle(110, 0, 20, 20));
        Add(doc, drawing, hatch);

        // ANSI31-like pattern: 45° lines 2 units apart, stored as AutoCAD stores them (already scaled and rotated).
        var pattern = new HatchPattern("ANSI31");
        pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(-Math.Sqrt(2), Math.Sqrt(2)) });
        var patterned = new Hatch { IsSolid = false, Pattern = pattern };
        patterned.Paths.Add(Rectangle(110, 30, 20, 20));
        Add(doc, drawing, patterned);

        var dashed = new LineType("DASHED");
        dashed.AddSegment(new LineType.Segment { Length = 5 });
        dashed.AddSegment(new LineType.Segment { Length = -2.5 });
        doc.LineTypes.Add(dashed);
        Add(doc, drawing, new Line(new XYZ(0, -5, 0), new XYZ(100, -5, 0)) { LineType = dashed });

        var spline = new Spline { Degree = 3 };
        spline.ControlPoints.AddRange([new XYZ(0, -20, 0), new XYZ(20, -10, 0), new XYZ(40, -30, 0), new XYZ(60, -20, 0)]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
        Add(doc, drawing, spline);
        Add(doc, drawing, new Ellipse { Center = new XYZ(85, -20, 0), MajorAxisEndPoint = new XYZ(12, 0, 0), RadiusRatio = 0.5, StartParameter = 0, EndParameter = 1.5 * Math.PI });

        // The default dimension style is sized for inches (0.18 text); this drawing is in millimetres.
        var dimension = new DimensionAligned
        {
            FirstPoint = new XYZ(0, 60, 0),
            SecondPoint = new XYZ(100, 60, 0),
            Style = new DimensionStyle("Filee") { TextHeight = 2.5, ArrowSize = 2.5 },
        };
        dimension.Offset = 8;
        Add(doc, drawing, dimension);
        dimension.UpdateBlock();

        Add(doc, off, new Line(new XYZ(0, 20, 0), new XYZ(100, 20, 0)));
        Add(doc, off, new Circle(new XYZ(60, 60, 0), 8));
        Add(doc, frozen, new Line(new XYZ(0, 40, 0), new XYZ(100, 40, 0)));
        return doc;
    }

    /// <summary>A few lines around (1 000 000, 500 000) with header extents that point elsewhere.</summary>
    public static CadDocument FarFromOrigin()
    {
        var doc = new CadDocument();
        var layer = new Layer("Far") { Color = new Color(7) };
        doc.Layers.Add(layer);
        const double X = 1_000_000, Y = 500_000;
        Add(doc, layer, new Line(new XYZ(X, Y, 0), new XYZ(X + 400, Y, 0)));
        Add(doc, layer, new Line(new XYZ(X + 400, Y, 0), new XYZ(X + 400, Y + 200, 0)));
        Add(doc, layer, new Line(new XYZ(X + 400, Y + 200, 0), new XYZ(X, Y + 200, 0)));
        Add(doc, layer, new Line(new XYZ(X, Y + 200, 0), new XYZ(X, Y, 0)));
        doc.Header.ModelSpaceExtMin = new XYZ(0, 0, 0);
        doc.Header.ModelSpaceExtMax = new XYZ(10, 10, 0);
        return doc;
    }

    public static Hatch.BoundaryPath Rectangle(double x, double y, double width, double height)
    {
        var path = new Hatch.BoundaryPath();
        path.Edges.Add(new Hatch.BoundaryPath.Polyline(
            [new XYZ(x, y, 0), new XYZ(x + width, y, 0), new XYZ(x + width, y + height, 0), new XYZ(x, y + height, 0)]));
        return path;
    }

    public static T Add<T>(CadDocument doc, Layer layer, T entity) where T : Entity
    {
        entity.Layer = layer;
        doc.Entities.Add(entity);
        return entity;
    }

    public static string SaveDxf(CadDocument doc, string folder, string name, bool binary = false)
    {
        var path = Path.Combine(folder, name);
        DxfWriter.Write(path, doc, binary);
        return path;
    }

    /// <summary>Model space entity counts by DXF type name.</summary>
    public static Dictionary<string, int> Census(CadDocument doc) =>
        doc.Entities.GroupBy(e => e.ObjectName).ToDictionary(g => g.Key, g => g.Count());
}
