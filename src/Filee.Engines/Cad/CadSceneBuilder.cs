// Turns an ACadSharp document into a CadScene: walks model space (or the active layout when model space is
// empty), resolves layers, colours, line weights and line types, expands block references and converts every
// supported entity into geometry in WCS. Text and hatches live in the partial files next to this one.

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using SkiaSharp;
using AcadColor = ACadSharp.Color;

namespace Filee.Engines.Cad;

/// <summary>Builds the display list of a drawing.</summary>
internal sealed partial class CadSceneBuilder
{
    /// <summary>Primitive budget: beyond this a drawing is cut short rather than exhausting memory.</summary>
    private const int MaxPrimitives = 1_000_000;

    /// <summary>Deepest block nesting followed (also stops self-referencing blocks).</summary>
    private const int MaxDepth = 16;

    /// <summary>Largest MINSERT array expanded (rows × columns).</summary>
    private const int MaxArrayItems = 10_000;

    /// <summary>Line weight used for "Default" (AutoCAD's LWDEFAULT is 0.25 mm; a thinner line reads better on screen).</summary>
    private const double DefaultLineWeightMm = 0.18;

    /// <summary>Line weight 0 ("thinnest"), drawn as a fine line instead of a zero-width hairline.</summary>
    private const double ThinnestLineWeightMm = 0.1;

    private readonly CadScene _scene = new();
    private readonly CancellationToken _cancellationToken;
    private readonly double _lineTypeScale;
    private readonly bool _pointsVisible;
    private int _visited;

    private CadSceneBuilder(CadDocument document, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        var header = document.Header;
        _lineTypeScale = header is not null && header.LineTypeScale > 0 && double.IsFinite(header.LineTypeScale) ? header.LineTypeScale : 1;
        _pointsVisible = header?.PointDisplayMode != 1;
    }

    /// <summary>
    /// Builds the scene of <paramref name="document"/>: model space, or the active layout (paper space) when model
    /// space has nothing visible. Viewports are not followed.
    /// </summary>
    public static CadScene Build(CadDocument document, CancellationToken cancellationToken)
    {
        var builder = new CadSceneBuilder(document, cancellationToken);
        builder.DrawEntities(document.ModelSpace.Entities, DrawContext.Root);
        if (builder._scene.Items.Count == 0 && TryGetPaperSpace(document) is { } paper)
        {
            builder._scene.Space = paper.Layout?.Name ?? paper.Name;
            builder.DrawEntities(paper.Entities, DrawContext.Root);
        }
        return builder._scene;
    }

    private static BlockRecord? TryGetPaperSpace(CadDocument document) =>
        document.BlockRecords.TryGetValue(BlockRecord.PaperSpaceName, out var paper) ? paper : null;

    /// <summary>
    /// What the entities inside a block reference inherit from it: the transform to WCS, the insert's layer
    /// (entities on layer "0" take it over), and the colour, line weight and line type used by BYBLOCK.
    /// </summary>
    private readonly record struct DrawContext(
        Affine3 Transform,
        Layer? InsertLayer,
        SKColor BlockColor,
        double BlockWidthMm,
        LineType? BlockLineType,
        int Depth)
    {
        /// <summary>Model space: BYBLOCK entities outside blocks are drawn like colour 7 (black on paper).</summary>
        public static DrawContext Root => new(Affine3.Identity, null, SKColors.Black, DefaultLineWeightMm, null, 0);
    }

    // ───────────────────────── Walking ─────────────────────────

    private void DrawEntities(IEnumerable<Entity> entities, DrawContext context)
    {
        foreach (var entity in entities)
        {
            if ((++_visited & 1023) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            if (_scene.Truncated)
                return;
            if (entity.IsInvisible)
                continue;

            var layer = EffectiveLayer(entity, context);
            try
            {
                if (entity is Insert insert)
                {
                    DrawInsert(insert, layer, context);
                    continue;
                }
                if (!IsShown(layer))
                    continue;
                DrawEntity(entity, layer, context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken entity (missing references, degenerate data) must not lose the whole drawing.
                Skip(entity.ObjectName + " (invalid)");
            }
        }
    }

    private void DrawEntity(Entity entity, Layer layer, DrawContext context)
    {
        switch (entity)
        {
            case Line line:
                Stroke(entity, layer, context, Figure2(context.Transform.Point(line.StartPoint), context.Transform.Point(line.EndPoint)));
                break;
            case Arc arc:
                DrawArc(arc, layer, context);
                break;
            case Circle circle:
                DrawCircle(circle, layer, context);
                break;
            case Ellipse ellipse:
                DrawEllipse(ellipse, layer, context);
                break;
            case LwPolyline polyline:
                DrawLwPolyline(polyline, layer, context);
                break;
            case PolyfaceMesh mesh:
                DrawPolyfaceMesh(mesh, layer, context);
                break;
            case PolygonMesh mesh:
                DrawPolygonMesh(mesh, layer, context);
                break;
            case Polyline2D polyline:
                DrawPolyline2D(polyline, layer, context);
                break;
            case Polyline3D polyline:
                DrawPolyline3D(polyline, layer, context);
                break;
            case Spline spline:
                DrawSpline(spline, layer, context);
                break;
            case AttributeDefinition definition:
                // Inside blocks, definitions are replaced by the insert's attributes; alone they show their tag.
                if (context.Depth == 0)
                    DrawText(definition, definition.Tag, layer, context);
                break;
            case TextEntity text:
                if (text is not AttributeEntity attribute || !attribute.Flags.HasFlag(AttributeFlags.Hidden))
                    DrawText(text, text.Value, layer, context);
                break;
            case MText mtext:
                DrawMText(mtext, layer, context);
                break;
            case Dimension dimension:
                DrawDimension(dimension, layer, context);
                break;
            case Leader leader:
                DrawLeader(leader, layer, context);
                break;
            case MultiLeader multiLeader:
                DrawMultiLeader(multiLeader, layer, context);
                break;
            case Hatch hatch:
                DrawHatch(hatch, layer, context);
                break;
            case Solid solid:
                DrawSolid(solid, layer, context);
                break;
            case Face3D face:
                DrawFace(face, layer, context);
                break;
            case Point point:
                if (_pointsVisible)
                    Add(new PointPrimitive
                    {
                        Location = context.Transform.Point(point.Location),
                        Color = ResolveColor(entity, layer, context),
                        WidthMm = ResolveWidth(entity, layer, context),
                    }, context.Transform.Point(point.Location));
                break;
            case XLine xline:
                DrawInfinite(entity, layer, context, xline.FirstPoint, xline.Direction, isRay: false);
                break;
            case Ray ray:
                DrawInfinite(entity, layer, context, ray.StartPoint, ray.Direction, isRay: true);
                break;
            case MLine mline:
                DrawMLine(mline, layer, context);
                break;
            case Mesh mesh:
                DrawMesh(mesh, layer, context);
                break;
            case Viewport:
                break; // layout viewports show model space through a view: not followed
            case Seqend:
                break; // end marker of old-style polylines and attributes, nothing to draw
            default:
                Skip(entity.ObjectName);
                break;
        }
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void DrawInsert(Insert insert, Layer layer, DrawContext context)
    {
        // A frozen layer hides the whole reference; an OFF (or non-plotting) layer only hides what inherits it,
        // i.e. the block's entities on layer "0" (that is how AutoCAD shows it).
        if (IsFrozen(layer))
            return;
        var block = insert.Block;
        if (block is null)
        {
            Skip("INSERT (missing block)");
            return;
        }
        if (context.Depth >= MaxDepth)
        {
            _scene.Truncated = true;
            return;
        }

        var inherited = Inherit(insert, layer, context);
        var basePoint = block.BlockEntity?.BasePoint ?? default;
        var columns = Math.Max(1, (int)insert.ColumnCount);
        var rows = Math.Max(1, (int)insert.RowCount);
        if ((long)columns * rows > MaxArrayItems)
        {
            _scene.Truncated = true;
            columns = Math.Min(columns, MaxArrayItems);
            rows = Math.Max(1, Math.Min(rows, MaxArrayItems / columns));
        }

        var placement = context.Transform
            .Multiply(Affine3.Ocs(insert.Normal))
            .Multiply(Affine3.Translation(insert.InsertPoint))
            .Multiply(Affine3.RotationZ(insert.Rotation));
        var scale = Affine3.Scale(insert.XScale, insert.YScale, insert.ZScale)
            .Multiply(Affine3.Translation(new Vec3(-basePoint.X, -basePoint.Y, -basePoint.Z)));

        for (var row = 0; row < rows && !_scene.Truncated; row++)
        {
            for (var column = 0; column < columns && !_scene.Truncated; column++)
            {
                var cell = placement
                    .Multiply(Affine3.Translation(new Vec3(column * insert.ColumnSpacing, row * insert.RowSpacing, 0)))
                    .Multiply(scale);
                DrawEntities(block.Entities, inherited with { Transform = cell });
            }
        }

        // Attributes belong to the reference, not the block: they are positioned in the parent's coordinates, but
        // like the block's contents they take over the insert's layer when on layer "0" and its BYBLOCK properties.
        var attributeContext = inherited with { Transform = context.Transform, Depth = context.Depth };
        foreach (var attribute in insert.Attributes)
        {
            var attributeLayer = EffectiveLayer(attribute, attributeContext);
            if (!attribute.IsInvisible && IsShown(attributeLayer))
                DrawEntity(attribute, attributeLayer, attributeContext);
        }
    }

    /// <summary>The context for the contents of a block reference, dimension or multileader block.</summary>
    private DrawContext Inherit(Entity reference, Layer layer, DrawContext context) => context with
    {
        InsertLayer = layer,
        BlockColor = ResolveColor(reference, layer, context),
        BlockWidthMm = ResolveWidth(reference, layer, context),
        BlockLineType = ResolveLineType(reference, layer, context),
        Depth = context.Depth + 1,
    };

    private void DrawDimension(Dimension dimension, Layer layer, DrawContext context)
    {
        var block = dimension.Block;
        if (block is null || block.Entities.Count == 0)
        {
            // Some writers omit the dimension picture; let ACadSharp generate one from the definition points.
            try
            {
                dimension.UpdateBlock();
                block = dimension.Block;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                block = null;
            }
        }
        if (block is null || block.Entities.Count == 0)
        {
            Skip("DIMENSION (no block)");
            return;
        }
        if (context.Depth >= MaxDepth)
            return;

        // The picture is stored in drawing coordinates (each entity carries its own extrusion), so it is drawn in
        // place, like ezdxf does; only colour, layer and line properties come from the dimension (BYBLOCK).
        DrawEntities(block.Entities, Inherit(dimension, layer, context));
    }

    // ───────────────────────── Curves ─────────────────────────

    private void DrawArc(Arc arc, Layer layer, DrawContext context)
    {
        var m = context.Transform.Multiply(Affine3.Ocs(arc.Normal));
        var start = arc.StartAngle;
        var end = arc.EndAngle;
        while (end <= start)
            end += 2 * Math.PI;
        var figure = ArcFigure(m, arc.Center, arc.Radius, arc.Radius, start, end);
        Stroke(arc, layer, context, figure);
    }

    private void DrawCircle(Circle circle, Layer layer, DrawContext context)
    {
        var m = context.Transform.Multiply(Affine3.Ocs(circle.Normal));
        var figure = ArcFigure(m, circle.Center, circle.Radius, circle.Radius, 0, 2 * Math.PI);
        figure.Closed = true;
        Stroke(circle, layer, context, figure);
    }

    private void DrawEllipse(Ellipse ellipse, Layer layer, DrawContext context)
    {
        // ELLIPSE is in WCS: the minor axis is the major axis turned by 90° around the normal.
        Vec3 major = ellipse.MajorAxisEndPoint;
        var minor = Vec3.Cross(((Vec3)ellipse.Normal).Normalized(), major) * ellipse.RadiusRatio;
        var start = ellipse.StartParameter;
        var end = ellipse.EndParameter;
        while (end <= start)
            end += 2 * Math.PI;
        var m = context.Transform;
        var center = m.Point(ellipse.Center);
        var u = m.Vector(major);
        var v = m.Vector(minor);
        var figure = new Figure(center + u * Math.Cos(start) + v * Math.Sin(start));
        figure.ArcTo(center, u, v, start, end);
        figure.Closed = Math.Abs(end - start - 2 * Math.PI) < 1e-9;
        Stroke(ellipse, layer, context, figure);
    }

    /// <summary>An arc of the circle/ellipse with radii <paramref name="rx"/>, <paramref name="ry"/> in <paramref name="m"/>'s plane.</summary>
    private static Figure ArcFigure(Affine3 m, Vec3 center, double rx, double ry, double start, double end)
    {
        var c = m.Point(center);
        var u = m.Vector(rx, 0);
        var v = m.Vector(0, ry);
        var figure = new Figure(c + u * Math.Cos(start) + v * Math.Sin(start));
        figure.ArcTo(c, u, v, start, end);
        return figure;
    }

    private void DrawSpline(Spline spline, Layer layer, DrawContext context)
    {
        List<Vec3> points;
        if (spline.ControlPoints.Count >= 2)
        {
            var weights = spline.Weights.Count == spline.ControlPoints.Count ? spline.Weights : null;
            points = CadCurves.SampleNurbs(spline.Degree, spline.ControlPoints.Select(p => (Vec3)p).ToList(), spline.Knots, weights);
        }
        else
        {
            points = CadCurves.ThroughPoints(spline.FitPoints.Select(p => (Vec3)p).ToList(), spline.Flags.HasFlag(SplineFlags.Closed));
        }
        if (points.Count >= 2)
            Stroke(spline, layer, context, PolylineFigure(context.Transform, points, closed: false));
    }

    private static Figure PolylineFigure(Affine3 m, IReadOnlyList<Vec3> points, bool closed)
    {
        var figure = new Figure(m.Point(points[0]));
        for (var i = 1; i < points.Count; i++)
            figure.LineTo(m.Point(points[i]));
        figure.Closed = closed;
        return figure;
    }

    private static Figure Figure2(Vec2 a, Vec2 b)
    {
        var figure = new Figure(a);
        figure.LineTo(b);
        return figure;
    }

    // ───────────────────────── Polylines ─────────────────────────

    /// <summary>A polyline vertex in the entity's OCS with the width at the start and end of the following segment.</summary>
    private readonly record struct PolyVertex(Vec2 Location, double Bulge, double StartWidth, double EndWidth);

    private void DrawLwPolyline(LwPolyline polyline, Layer layer, DrawContext context)
    {
        if (polyline.Vertices.Count == 0)
            return;
        var constant = polyline.ConstantWidth;
        var vertices = polyline.Vertices.Select(v => new PolyVertex(
            new Vec2(v.Location.X, v.Location.Y),
            v.Bulge,
            v.StartWidth > 0 || v.EndWidth > 0 ? v.StartWidth : constant,
            v.StartWidth > 0 || v.EndWidth > 0 ? v.EndWidth : constant)).ToList();
        var m = context.Transform.Multiply(Affine3.Ocs(polyline.Normal)).Multiply(Affine3.Translation(new Vec3(0, 0, polyline.Elevation)));
        DrawPolyline(polyline, layer, context, m, vertices, polyline.IsClosed);
    }

    private void DrawPolyline2D(Polyline2D polyline, Layer layer, DrawContext context)
    {
        // R12 files can give meshes as plain polylines whose face records look like vertices at the origin.
        if ((polyline.Flags & (PolylineFlags.PolyfaceMesh | PolylineFlags.PolygonMesh)) != 0)
        {
            Skip(polyline.ObjectName + " (mesh)");
            return;
        }
        var smoothed = polyline.Flags.HasFlag(PolylineFlags.SplineFit);
        var list = polyline.Vertices
            // Spline-fit polylines keep their frame (control points) next to the fitted vertices: draw the fit.
            .Where(v => !(smoothed && v.Flags.HasFlag(VertexFlags.SplineFrameControlPoint)))
            .ToList();
        if (list.Count == 0)
            return;
        var anyVertexWidth = list.Any(v => v.StartWidth > 0 || v.EndWidth > 0);
        var vertices = list.Select(v => new PolyVertex(
            new Vec2(v.Location.X, v.Location.Y),
            smoothed ? 0 : v.Bulge,
            anyVertexWidth ? v.StartWidth : polyline.StartWidth,
            anyVertexWidth ? v.EndWidth : polyline.EndWidth)).ToList();
        var m = context.Transform.Multiply(Affine3.Ocs(polyline.Normal)).Multiply(Affine3.Translation(new Vec3(0, 0, polyline.Elevation)));
        DrawPolyline(polyline, layer, context, m, vertices, polyline.IsClosed);
    }

    private void DrawPolyline3D(Polyline3D polyline, Layer layer, DrawContext context)
    {
        if ((polyline.Flags & (PolylineFlags.PolyfaceMesh | PolylineFlags.PolygonMesh)) != 0)
        {
            Skip(polyline.ObjectName + " (mesh)");
            return;
        }
        var smoothed = polyline.Flags.HasFlag(PolylineFlags.SplineFit);
        var points = polyline.Vertices
            .Where(v => !(smoothed && v.Flags.HasFlag(VertexFlags.SplineFrameControlPoint)))
            .Select(v => (Vec3)v.Location)
            .ToList();
        if (points.Count == 0)
            return;
        if (points.Count == 1)
            points.Add(points[0]);
        Stroke(polyline, layer, context, PolylineFigure(context.Transform, points, polyline.IsClosed));
    }

    /// <summary>
    /// Draws a 2D polyline in plane <paramref name="m"/>. Thin polylines become one stroked figure with bulge
    /// arcs; a constant width becomes a wide stroke; varying widths (arrows, tapers) become filled bands.
    /// </summary>
    private void DrawPolyline(Entity entity, Layer layer, DrawContext context, Affine3 m, List<PolyVertex> vertices, bool closed)
    {
        var segmentCount = closed ? vertices.Count : vertices.Count - 1;
        var figure = new Figure(m.Point(vertices[0].Location.X, vertices[0].Location.Y));
        if (segmentCount <= 0)
        {
            figure.LineTo(figure.Start); // a single vertex shows as a dot
            Stroke(entity, layer, context, figure);
            return;
        }

        var widths = Enumerable.Range(0, segmentCount).Select(i => (vertices[i].StartWidth, vertices[i].EndWidth)).ToList();
        var constant = widths[0].StartWidth;
        var isConstant = widths.All(w => Math.Abs(w.StartWidth - constant) < 1e-12 && Math.Abs(w.EndWidth - constant) < 1e-12);

        if (isConstant || widths.All(w => w.StartWidth <= 0 && w.EndWidth <= 0))
        {
            for (var i = 0; i < segmentCount; i++)
                AppendSegment(figure, m, vertices[i], vertices[(i + 1) % vertices.Count]);
            figure.Closed = closed;
            Stroke(entity, layer, context, figure, constant > 0 ? constant * m.PlanScale : 0);
            return;
        }

        // Varying widths: each wide segment is a filled band, zero-width segments stay thin lines.
        var color = ResolveColor(entity, layer, context);
        for (var i = 0; i < segmentCount; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            if (a.StartWidth <= 0 && a.EndWidth <= 0)
            {
                var thin = new Figure(m.Point(a.Location.X, a.Location.Y));
                AppendSegment(thin, m, a, b);
                Stroke(entity, layer, context, thin);
                continue;
            }
            var band = new FillPrimitive { Color = color };
            band.Figures.Add(Band(m, a, b));
            AddFill(band);
        }
    }

    private static void AppendSegment(Figure figure, Affine3 m, PolyVertex a, PolyVertex b)
    {
        var end = m.Point(b.Location.X, b.Location.Y);
        if (Math.Abs(a.Bulge) < 1e-10 || (b.Location - a.Location).Length < 1e-12)
        {
            figure.LineTo(end);
            return;
        }
        var (center, radius, start, sweep) = CadCurves.BulgeArc(a.Location, b.Location, a.Bulge);
        figure.ArcTo(m.Point(center.X, center.Y), m.Vector(radius, 0), m.Vector(0, radius), start, start + sweep);
    }

    /// <summary>The outline of a wide polyline segment whose width changes linearly from start to end.</summary>
    private static Figure Band(Affine3 m, PolyVertex a, PolyVertex b)
    {
        var left = new List<Vec2>();
        var right = new List<Vec2>();
        if (Math.Abs(a.Bulge) < 1e-10)
        {
            var normal = (b.Location - a.Location).Normalized().Perp;
            left.Add(a.Location + normal * (a.StartWidth / 2));
            left.Add(b.Location + normal * (a.EndWidth / 2));
            right.Add(a.Location - normal * (a.StartWidth / 2));
            right.Add(b.Location - normal * (a.EndWidth / 2));
        }
        else
        {
            var (center, radius, start, sweep) = CadCurves.BulgeArc(a.Location, b.Location, a.Bulge);
            var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 18)));
            for (var s = 0; s <= steps; s++)
            {
                var f = (double)s / steps;
                var angle = start + sweep * f;
                var half = (a.StartWidth + (a.EndWidth - a.StartWidth) * f) / 2;
                var direction = new Vec2(Math.Cos(angle), Math.Sin(angle));
                left.Add(center + direction * (radius + half));
                right.Add(center + direction * Math.Max(radius - half, 0));
            }
        }
        var figure = new Figure(m.Point(left[0].X, left[0].Y));
        foreach (var p in left.Skip(1))
            figure.LineTo(m.Point(p.X, p.Y));
        for (var i = right.Count - 1; i >= 0; i--)
            figure.LineTo(m.Point(right[i].X, right[i].Y));
        figure.Closed = true;
        return figure;
    }

    private void DrawPolyfaceMesh(PolyfaceMesh mesh, Layer layer, DrawContext context)
    {
        var vertices = mesh.Vertices.Select(v => (Vec3)v.Location).ToList();
        var edges = new StrokeCollector(this, mesh, layer, context);
        foreach (var face in mesh.Faces)
        {
            var indices = new[] { face.Index1, face.Index2, face.Index3, face.Index4 }.Where(i => i != 0).ToList();
            for (var i = 0; i < indices.Count; i++)
            {
                var from = indices[i];
                var to = indices[(i + 1) % indices.Count];
                // A negative index hides the edge that starts at that vertex.
                if (from < 0 || Math.Abs(from) > vertices.Count || Math.Abs(to) > vertices.Count)
                    continue;
                edges.Line(vertices[from - 1], vertices[Math.Abs(to) - 1]);
            }
        }
        edges.Flush();
    }

    private void DrawPolygonMesh(PolygonMesh mesh, Layer layer, DrawContext context)
    {
        var vertices = mesh.Vertices.Select(v => (Vec3)v.Location).ToList();
        int m = mesh.MVertexCount, n = mesh.NVertexCount;
        if (m <= 0 || n <= 0 || (long)m * n > vertices.Count)
        {
            Skip(mesh.ObjectName);
            return;
        }
        var edges = new StrokeCollector(this, mesh, layer, context);
        for (var i = 0; i < m; i++)
            for (var j = 0; j < n; j++)
            {
                if (j + 1 < n)
                    edges.Line(vertices[i * n + j], vertices[i * n + j + 1]);
                if (i + 1 < m)
                    edges.Line(vertices[i * n + j], vertices[(i + 1) * n + j]);
            }
        edges.Flush();
    }

    private void DrawMesh(Mesh mesh, Layer layer, DrawContext context)
    {
        var vertices = mesh.Vertices.Select(v => (Vec3)v).ToList();
        var edges = new StrokeCollector(this, mesh, layer, context);
        foreach (var face in mesh.Faces)
            for (var i = 0; i < face.Length; i++)
            {
                int a = face[i], b = face[(i + 1) % face.Length];
                if (a >= 0 && b >= 0 && a < vertices.Count && b < vertices.Count)
                    edges.Line(vertices[a], vertices[b]);
            }
        edges.Flush();
    }

    private void DrawMLine(MLine mline, Layer layer, DrawContext context)
    {
        // Each element of the multiline style is a polyline through the vertices, offset along their miters.
        var elements = mline.Vertices.Count == 0 ? 0 : mline.Vertices.Max(v => v.Segments.Count);
        for (var element = 0; element < elements; element++)
        {
            var points = mline.Vertices
                .Where(v => element < v.Segments.Count && v.Segments[element].Parameters.Count > 0)
                .Select(v => (Vec3)v.Position + ((Vec3)v.Miter) * v.Segments[element].Parameters[0])
                .ToList();
            if (points.Count >= 2)
                Stroke(mline, layer, context, PolylineFigure(context.Transform, points, mline.Flags.HasFlag(MLineFlags.Closed)));
        }
    }

    // ───────────────────────── Other entities ─────────────────────────

    private void DrawSolid(Solid solid, Layer layer, DrawContext context)
    {
        // SOLID / TRACE corners are ordered 1-2-4-3 around the outline; a triangle repeats its third corner.
        var m = context.Transform.Multiply(Affine3.Ocs(solid.Normal));
        var figure = new Figure(m.Point(solid.FirstCorner));
        figure.LineTo(m.Point(solid.SecondCorner));
        figure.LineTo(m.Point(solid.FourthCorner));
        figure.LineTo(m.Point(solid.ThirdCorner));
        figure.Closed = true;
        var fill = new FillPrimitive { Color = ResolveColor(solid, layer, context) };
        fill.Figures.Add(figure);
        AddFill(fill);
    }

    private void DrawFace(Face3D face, Layer layer, DrawContext context)
    {
        var corners = new Vec3[] { face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner };
        var hidden = new[] { InvisibleEdgeFlags.First, InvisibleEdgeFlags.Second, InvisibleEdgeFlags.Third, InvisibleEdgeFlags.Fourth };
        var edges = new StrokeCollector(this, face, layer, context);
        for (var i = 0; i < 4; i++)
            if (!face.Flags.HasFlag(hidden[i]))
                edges.Line(corners[i], corners[(i + 1) % 4]);
        edges.Flush();
    }

    private void DrawInfinite(Entity entity, Layer layer, DrawContext context, Vec3 origin, Vec3 direction, bool isRay)
    {
        var o = context.Transform.Point(origin);
        var d = context.Transform.Vector(direction);
        if (d.Length < 1e-12)
            return;
        Add(new InfiniteLinePrimitive
        {
            Origin = o,
            Direction = d.Normalized(),
            IsRay = isRay,
            Color = ResolveColor(entity, layer, context),
            WidthMm = ResolveWidth(entity, layer, context),
            Dashes = ResolveDashes(entity, layer, context, context.Transform),
        }, o);
    }

    private void DrawLeader(Leader leader, Layer layer, DrawContext context)
    {
        var points = leader.Vertices.Select(v => (Vec3)v).ToList();
        if (points.Count < 2)
            return;
        var path = leader.PathType == LeaderPathType.Spline ? CadCurves.ThroughPoints(points, closed: false) : points;
        Stroke(leader, layer, context, PolylineFigure(context.Transform, path, closed: false));
        if (leader.ArrowHeadEnabled)
        {
            var style = leader.Style;
            var size = style is null ? 0 : style.ArrowSize * (style.ScaleFactor > 0 ? style.ScaleFactor : 1);
            Arrow(leader, layer, context, points[0], points[1], size);
        }
    }

    private void DrawMultiLeader(MultiLeader leader, Layer layer, DrawContext context)
    {
        var data = leader.ContextData;
        if (data is null)
        {
            Skip(leader.ObjectName);
            return;
        }

        var lineContext = context with { BlockColor = ResolveColor(leader, layer, context) };
        var lineColor = ResolveColor(leader.LineColor, layer, lineContext);
        var arrowSize = data.ArrowheadSize > 0 ? data.ArrowheadSize : leader.ArrowheadSize * (leader.ScaleFactor > 0 ? leader.ScaleFactor : 1);
        var width = ResolveWidth(leader, layer, context);
        foreach (var root in data.LeaderRoots)
        {
            foreach (var line in root.Lines)
            {
                var points = line.Points.Select(p => (Vec3)p).ToList();
                points.Add(root.ConnectionPoint);
                if (points.Count < 2)
                    continue;
                AddStroke(new StrokePrimitive { Color = lineColor, WidthMm = width }, PolylineFigure(context.Transform, points, closed: false));
                if (arrowSize > 0)
                    ArrowPrimitive(lineColor, context, points[0], points[1], arrowSize);
            }
            if (leader.EnableLanding && root.LandingDistance > 0)
            {
                Vec3 start = root.ConnectionPoint;
                var end = start + ((Vec3)root.Direction).Normalized() * root.LandingDistance;
                AddStroke(new StrokePrimitive { Color = lineColor, WidthMm = width }, Figure2(context.Transform.Point(start), context.Transform.Point(end)));
            }
        }

        if (data.HasTextContents && !string.IsNullOrEmpty(data.TextLabel))
        {
            var textColor = ResolveColor(data.TextColor, layer, lineContext);
            var attachment = data.TextAttachmentPoint switch
            {
                TextAttachmentPointType.Center => AttachmentPointType.TopCenter,
                TextAttachmentPointType.Right => AttachmentPointType.TopRight,
                _ => AttachmentPointType.TopLeft,
            };
            var height = data.TextHeight > 0 ? data.TextHeight : 2.5;
            DrawMTextCore(data.TextLabel, data.TextLocation, data.Direction, data.TextNormal, height, data.BoundaryWidth,
                attachment, data.LineSpacingFactor, data.TextStyle ?? leader.TextStyle, textColor, context);
        }
        else if (data.HasContentsBlock && data.BlockContent is { } block && context.Depth < MaxDepth)
        {
            Vec3 scale = data.BlockContentScale;
            var basePoint = block.BlockEntity?.BasePoint ?? default;
            var transform = context.Transform
                .Multiply(Affine3.Ocs(data.BlockContentNormal))
                .Multiply(Affine3.Translation(data.BlockContentLocation))
                .Multiply(Affine3.RotationZ(data.BlockContentRotation))
                .Multiply(Affine3.Scale(scale.X == 0 ? 1 : scale.X, scale.Y == 0 ? 1 : scale.Y, scale.Z == 0 ? 1 : scale.Z))
                .Multiply(Affine3.Translation(new Vec3(-basePoint.X, -basePoint.Y, -basePoint.Z)));
            DrawEntities(block.Entities, Inherit(leader, layer, context) with { Transform = transform });
        }
    }

    /// <summary>A filled closed arrowhead at <paramref name="tip"/> pointing away from <paramref name="from"/>.</summary>
    private void Arrow(Entity entity, Layer layer, DrawContext context, Vec3 tip, Vec3 from, double size) =>
        ArrowPrimitive(ResolveColor(entity, layer, context), context, tip, from, size);

    private void ArrowPrimitive(SKColor color, DrawContext context, Vec3 tip, Vec3 from, double size)
    {
        var direction = tip - from;
        var length = direction.Length;
        if (size <= 0 || length < 1e-12)
            return;
        var d = direction * (1 / length);
        var side = Vec3.Cross(new Vec3(0, 0, 1), d).Normalized() * (size / 6);
        var back = tip - d * size;
        var m = context.Transform;
        var figure = new Figure(m.Point(tip));
        figure.LineTo(m.Point(back + side));
        figure.LineTo(m.Point(back - side));
        figure.Closed = true;
        var fill = new FillPrimitive { Color = color };
        fill.Figures.Add(figure);
        AddFill(fill);
    }

    // ───────────────────────── Properties ─────────────────────────

    /// <summary>Entities on layer "0" inside a block take on the layer of the block reference.</summary>
    private static Layer EffectiveLayer(Entity entity, DrawContext context)
    {
        var layer = entity.Layer;
        if (context.InsertLayer is not null && (layer is null || layer.Name == Layer.DefaultName))
            return context.InsertLayer;
        return layer ?? context.InsertLayer ?? Layer.Default;
    }

    /// <summary>Visible on a plot: on, thawed and plottable (the Defpoints layer never plots).</summary>
    private static bool IsShown(Layer layer) => layer.IsOn && !IsFrozen(layer) && layer.PlotFlag;

    private static bool IsFrozen(Layer layer) => layer.Flags.HasFlag(LayerFlags.Frozen);

    private static SKColor ResolveColor(Entity entity, Layer layer, DrawContext context) => ResolveColor(entity.Color, layer, context);

    /// <summary>
    /// BYLAYER / BYBLOCK resolution, ACI index or true colour to RGB. White and near-white (ACI 7 draws white on
    /// AutoCAD's black screen) become black, because the page is white.
    /// </summary>
    private static SKColor ResolveColor(AcadColor color, Layer layer, DrawContext context)
    {
        if (color.IsByBlock)
            return context.BlockColor;
        if (color.IsByLayer)
            color = layer.Color;
        if (color.IsByLayer || color.IsByBlock || color.Index == 257)
            return SKColors.Black;

        var rgb = color.GetRgb();
        if (rgb.Length < 3)
            return SKColors.Black;
        var (r, g, b) = (rgb[0], rgb[1], rgb[2]);
        if (Math.Min(r, Math.Min(g, b)) >= 224)
            return SKColors.Black;
        return new SKColor(r, g, b);
    }

    private static double ResolveWidth(Entity entity, Layer layer, DrawContext context) =>
        entity.LineWeight switch
        {
            LineWeightType.ByLayer => WidthOf(layer.LineWeight, context),
            LineWeightType.ByBlock => context.BlockWidthMm,
            var own => WidthOf(own, context),
        };

    private static double WidthOf(LineWeightType weight, DrawContext context) => weight switch
    {
        LineWeightType.ByBlock => context.BlockWidthMm,
        LineWeightType.W0 => ThinnestLineWeightMm,
        > 0 and <= LineWeightType.W211 => Math.Max((int)weight / 100.0, ThinnestLineWeightMm),
        _ => DefaultLineWeightMm, // Default, ByLayer on a layer, ByDIPs
    };

    private static LineType? ResolveLineType(Entity entity, Layer layer, DrawContext context)
    {
        var type = entity.LineType;
        if (type is null || type.Name.Equals(LineType.ByLayerName, StringComparison.OrdinalIgnoreCase))
            return layer.LineType;
        if (type.Name.Equals(LineType.ByBlockName, StringComparison.OrdinalIgnoreCase))
            return context.BlockLineType;
        return type;
    }

    /// <summary>
    /// The entity's dash pattern in drawing units: line type segments × LTSCALE × the entity's scale × the block
    /// scale. Shapes and text inside complex line types are left out (their space stays empty).
    /// </summary>
    private double[]? ResolveDashes(Entity entity, Layer layer, DrawContext context, Affine3 m)
    {
        var type = ResolveLineType(entity, layer, context);
        if (type is null || !type.IsComplex)
            return null;
        var segments = type.Segments.Select(s => s.Length).ToArray();
        if (segments.Length < 2 || segments.All(s => s >= 0) || segments.Any(s => !double.IsFinite(s)))
            return null;
        var entityScale = entity.LineTypeScale > 0 && double.IsFinite(entity.LineTypeScale) ? entity.LineTypeScale : 1;
        var scale = _lineTypeScale * entityScale * m.PlanScale;
        if (!(scale > 0) || segments.Sum(Math.Abs) * scale <= 0)
            return null;
        return [.. segments.Select(s => s * scale)];
    }

    // ───────────────────────── Output ─────────────────────────

    private void Stroke(Entity entity, Layer layer, DrawContext context, Figure figure, double worldWidth = 0) =>
        AddStroke(new StrokePrimitive
        {
            Color = ResolveColor(entity, layer, context),
            WidthMm = ResolveWidth(entity, layer, context),
            WorldWidth = worldWidth,
            Dashes = ResolveDashes(entity, layer, context, context.Transform),
        }, figure);

    private void AddStroke(StrokePrimitive primitive, Figure figure)
    {
        primitive.Figures.Add(figure);
        if (Reserve())
        {
            figure.AddTo(ref _scene.Extents);
            _scene.Items.Add(primitive);
        }
    }

    private void AddFill(FillPrimitive fill)
    {
        if (!Reserve())
            return;
        foreach (var figure in fill.Figures)
            figure.AddTo(ref _scene.Extents);
        _scene.Items.Add(fill);
    }

    private void Add(ScenePrimitive primitive, Vec2 extentPoint)
    {
        if (!Reserve())
            return;
        _scene.Extents.Add(extentPoint);
        _scene.Items.Add(primitive);
    }

    private bool Reserve()
    {
        if (_scene.Items.Count < MaxPrimitives)
            return true;
        _scene.Truncated = true;
        return false;
    }

    private void Skip(string type) =>
        _scene.Skipped[type] = _scene.Skipped.TryGetValue(type, out var count) ? count + 1 : 1;

    /// <summary>Collects the edges of a mesh or face into one stroke.</summary>
    private sealed class StrokeCollector(CadSceneBuilder builder, Entity entity, Layer layer, DrawContext context)
    {
        private readonly List<Figure> _figures = [];

        public void Line(Vec3 a, Vec3 b) => _figures.Add(Figure2(context.Transform.Point(a), context.Transform.Point(b)));

        public void Flush()
        {
            if (_figures.Count == 0 || !builder.Reserve())
                return;
            var primitive = new StrokePrimitive
            {
                Color = ResolveColor(entity, layer, context),
                WidthMm = ResolveWidth(entity, layer, context),
                Dashes = builder.ResolveDashes(entity, layer, context, context.Transform),
            };
            foreach (var figure in _figures)
            {
                figure.AddTo(ref builder._scene.Extents);
                primitive.Figures.Add(figure);
            }
            builder._scene.Items.Add(primitive);
        }
    }
}
