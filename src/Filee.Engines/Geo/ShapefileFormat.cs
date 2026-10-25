// ESRI Shapefile in and out, in managed code. A shapefile is several files with the same name: .shp (shapes), .shx
// (index), .dbf (attributes), .prj (coordinate system) and .cpg (text encoding of the .dbf). Reading takes the .shp and
// finds the others next to it (the .shx is not needed). Point, MultiPoint, PolyLine and Polygon shapes are read with their
// Z, MultiPatch is refused. Polygon rings are sorted into polygons and holes by their direction (outer rings clockwise),
// with holes assigned to the ring that contains them. Writing makes one shapefile per kind of geometry (a shapefile holds
// only one), in WGS 84 with a .prj, polygons clockwise as the format requires.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Geo;

/// <summary>The kinds of shape a shapefile can hold; a file holds one.</summary>
internal enum ShapeKind
{
    Point,
    Line,
    Polygon,
}

/// <summary>The records that go into one shapefile.</summary>
internal sealed class ShapefileGroup(ShapeKind kind, List<GeoFeature> records)
{
    public ShapeKind Kind { get; } = kind;

    /// <summary>One record each; a record's geometry is null for a feature without location.</summary>
    public List<GeoFeature> Records { get; } = records;
}

internal static class ShapefileFormat
{
    /// <summary>ESRI's definition of WGS 84, written to the .prj of every shapefile Filee makes.</summary>
    internal const string Wgs84Prj =
        "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";

    private const int HeaderLength = 100;
    private const double NoData = -1e39;

    // ---- Reading ----

    /// <summary>Finds a file next to <paramref name="shpPath"/> with the same name and another extension (any letter case).</summary>
    internal static string? Sidecar(string shpPath, string extension)
    {
        var full = Path.GetFullPath(shpPath);
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var exact = Path.Combine(directory, name + "." + extension);
        if (File.Exists(exact))
            return exact;
        // On a case-sensitive file system the sidecar may be "NAME.DBF" or "Name.Dbf".
        foreach (var candidate in Directory.EnumerateFiles(directory, name + ".*"))
        {
            if (Path.GetFileNameWithoutExtension(candidate).Equals(name, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(candidate).TrimStart('.').Equals(extension, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    /// <summary>Reads the shapes of <paramref name="path"/> and their attributes and coordinate system.</summary>
    public static GeoDataset Read(string path, CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderLength || BinaryPrimitives.ReadInt32BigEndian(bytes) != 9994)
            throw new InvalidDataException("The .shp file does not start with a shapefile header.");
        var headerType = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32));
        if (headerType == 31)
            throw new ConversionRefusedException("error.geo_unsupported_shape", "MultiPatch");
        if (headerType is not (0 or 1 or 3 or 5 or 8 or 11 or 13 or 15 or 18 or 21 or 23 or 25 or 28))
            throw new InvalidDataException($"The .shp has an unknown shape type ({headerType}).");

        var declared = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)) * 2L;
        // Some writers leave the length wrong; what is really in the file decides.
        var end = declared is >= HeaderLength && declared <= bytes.Length ? (int)declared : bytes.Length;

        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        var geometries = new List<GeoGeometry?>();
        var position = HeaderLength;
        while (position + 8 <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position + 4)) * 2L;
            position += 8;
            if (contentLength < 4 || position + contentLength > end)
                throw new InvalidDataException($"The .shp is cut off or damaged at shape {geometries.Count + 1}.");
            geometries.Add(ReadShape(bytes.AsSpan(position, (int)contentLength), geometries.Count + 1, cancellationToken));
            position += (int)contentLength;
        }

        DbfTable? table = null;
        if (Sidecar(path, "dbf") is { } dbfPath)
        {
            var cpgPath = Sidecar(path, "cpg");
            // Only as many rows as there are shapes: a table of millions of rows next to a few shapes is not read.
            table = DbfFormat.Read(dbfPath, cpgPath is null ? null : File.ReadAllText(cpgPath), geometries.Count, cancellationToken);
            if (table.Rows.Count < geometries.Count)
                throw new InvalidDataException($"The .dbf has {table.Rows.Count} rows but the .shp has {geometries.Count} shapes.");
        }
        for (var i = 0; i < geometries.Count; i++)
        {
            var properties = new List<KeyValuePair<string, object?>>();
            if (table is not null)
            {
                if (table.Rows[i] is not { } row)
                    continue; // marked as deleted in the .dbf
                for (var f = 0; f < table.Fields.Count; f++)
                    properties.Add(new(table.Fields[f].Name, row[f]));
            }
            dataset.Features.Add(new GeoFeature(geometries[i], properties));
        }

        // An empty .prj says nothing about the coordinate system, so it counts as no .prj.
        if (Sidecar(path, "prj") is { } prjPath && File.ReadAllText(prjPath) is { } wkt && !string.IsNullOrWhiteSpace(wkt))
        {
            dataset.Crs = GeoCrs.FromWkt(wkt);
            dataset.CrsDeclared = true;
        }
        return dataset;
    }

    private static GeoGeometry? ReadShape(ReadOnlySpan<byte> c, int number, CancellationToken cancellationToken)
    {
        var type = BinaryPrimitives.ReadInt32LittleEndian(c);
        switch (type)
        {
            case 0:
                return null;
            case 1 or 11 or 21:
                {
                    Need(c, 20, number);
                    var x = Double(c, 4);
                    var y = Double(c, 12);
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                        return null; // an empty point is written as NaN
                    var z = type == 11 && c.Length >= 28 ? Height(Double(c, 20)) : double.NaN;
                    return new GeoPoint(new Coord(x, y, z));
                }
            case 8 or 18 or 28:
                {
                    Need(c, 40, number);
                    var count = BinaryPrimitives.ReadInt32LittleEndian(c[36..]);
                    if (count < 0 || 40L + 16L * count > c.Length)
                        throw new InvalidDataException($"Shape {number}: the point count does not fit the record.");
                    var zAt = 40 + 16 * count + 16;
                    var hasZ = type == 18 && zAt + 8L * count <= c.Length;
                    var points = new List<Coord>(count);
                    for (var i = 0; i < count; i++)
                        points.Add(new Coord(Double(c, 40 + 16 * i), Double(c, 48 + 16 * i), hasZ ? Height(Double(c, zAt + 8 * i)) : double.NaN));
                    points.RemoveAll(p => !p.IsFinite);
                    return points.Count == 0 ? null : points.Count == 1 ? new GeoPoint(points[0]) : new GeoMultiPoint(points);
                }
            case 3 or 13 or 23 or 5 or 15 or 25:
                return ReadParts(c, type, number, cancellationToken);
            default:
                if (type == 31)
                    throw new ConversionRefusedException("error.geo_unsupported_shape", "MultiPatch");
                throw new InvalidDataException($"Shape {number} has an unknown shape type ({type}).");
        }
    }

    private static GeoGeometry? ReadParts(ReadOnlySpan<byte> c, int type, int number, CancellationToken cancellationToken)
    {
        Need(c, 44, number);
        var parts = BinaryPrimitives.ReadInt32LittleEndian(c[36..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(c[40..]);
        var pointsAt = 44L + 4L * parts;
        if (parts < 0 || count < 0 || pointsAt + 16L * count > c.Length)
            throw new InvalidDataException($"Shape {number}: the part and point counts do not fit the record.");
        var zAt = (int)pointsAt + 16 * count + 16;
        var hasZ = type is 13 or 15 && zAt + 8L * count <= c.Length;

        var starts = new int[parts + 1];
        for (var p = 0; p < parts; p++)
        {
            starts[p] = BinaryPrimitives.ReadInt32LittleEndian(c[(44 + 4 * p)..]);
            if (starts[p] < 0 || starts[p] > count || p > 0 && starts[p] < starts[p - 1])
                throw new InvalidDataException($"Shape {number}: the part {p + 1} starts outside the points.");
        }
        starts[parts] = count;

        var lines = new List<List<Coord>>(parts);
        for (var p = 0; p < parts; p++)
        {
            if (p % 1024 == 1023)
                cancellationToken.ThrowIfCancellationRequested();
            var line = new List<Coord>(starts[p + 1] - starts[p]);
            for (var i = starts[p]; i < starts[p + 1]; i++)
            {
                var at = (int)pointsAt + 16 * i;
                var point = new Coord(Double(c, at), Double(c, at + 8), hasZ ? Height(Double(c, zAt + 8 * i)) : double.NaN);
                if (point.IsFinite)
                    line.Add(point);
            }
            lines.Add(line);
        }

        if (type is 3 or 13 or 23)
        {
            var kept = lines.Where(l => l.Count >= 2).Select(l => (IReadOnlyList<Coord>)l).ToList();
            return kept.Count switch
            {
                0 => null,
                1 => new GeoLineString(kept[0]),
                _ => new GeoMultiLineString(kept),
            };
        }
        return Polygons(lines, cancellationToken);
    }

    /// <summary>
    /// Sorts rings into polygons with holes: outer rings run clockwise, holes counter-clockwise. Every ring's area and
    /// bounds are worked out once, and a hole is tried against the outer rings smallest first, so it stops at the first
    /// (smallest) one that contains it; a file of tens of thousands of rings takes well under a second, and the
    /// <paramref name="cancellationToken"/> is honoured while it is sorted.
    /// </summary>
    internal static GeoGeometry? Polygons(List<List<Coord>> lines, CancellationToken cancellationToken)
    {
        var rings = new List<(IReadOnlyList<Coord> Ring, double Area)>();
        for (var l = 0; l < lines.Count; l++)
        {
            if (l % 1024 == 1023)
                cancellationToken.ThrowIfCancellationRequested();
            if (GeoRings.Close(lines[l]) is { } ring && GeoRings.SignedArea(ring) is var area && area != 0)
                rings.Add((ring, area));
        }
        if (rings.Count == 0)
            return null;
        var outers = rings.Where(r => r.Area < 0).ToList();
        var holes = rings.Where(r => r.Area > 0).ToList();
        if (outers.Count == 0)
        {
            // Every ring runs counter-clockwise: the writer used the other convention, so they are all outer rings.
            outers = holes;
            holes = [];
        }
        var bounds = new (double MinX, double MinY, double MaxX, double MaxY)[outers.Count];
        for (var i = 0; i < outers.Count; i++)
        {
            if (i % 1024 == 1023)
                cancellationToken.ThrowIfCancellationRequested();
            bounds[i] = Bounds(outers[i].Ring);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var smallestFirst = Enumerable.Range(0, outers.Count).OrderBy(i => Math.Abs(outers[i].Area)).ToArray();
        var assigned = outers.Select(o => new List<IReadOnlyList<Coord>> { o.Ring }).ToList();
        var holeCount = 0;
        foreach (var (hole, _) in holes)
        {
            if (++holeCount % 64 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var probe = hole[0];
            var found = false;
            foreach (var i in smallestFirst)
            {
                var box = bounds[i];
                if (probe.X < box.MinX || probe.X > box.MaxX || probe.Y < box.MinY || probe.Y > box.MaxY)
                    continue;
                if (!GeoRings.Contains(outers[i].Ring, probe))
                    continue;
                assigned[i].Add(hole);
                found = true;
                break;
            }
            if (!found)
                assigned.Add([hole]); // a hole outside every outer ring is a ring of its own
        }
        var polygons = assigned.Select(rs => new GeoPolygon(rs)).ToList();
        return polygons.Count == 1 ? polygons[0] : new GeoMultiPolygon(polygons);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Coord> ring)
    {
        var (minX, minY, maxX, maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var p in ring)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return (minX, minY, maxX, maxY);
    }

    private static double Double(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadDoubleLittleEndian(c[at..]);

    /// <summary>Heights below -1e38 mean "no data".</summary>
    private static double Height(double z) => double.IsFinite(z) && z > -1e38 ? z : double.NaN;

    private static void Need(ReadOnlySpan<byte> c, int length, int number)
    {
        if (c.Length < length)
            throw new InvalidDataException($"Shape {number} is shorter than its type needs.");
    }

    // ---- Writing ----

    /// <summary>
    /// Sorts the features into the shapefiles to write: one per kind of geometry present. Collections are split into
    /// their members (each a record with the same attributes); features without geometry go to the first file.
    /// </summary>
    public static List<ShapefileGroup> Plan(GeoDataset dataset)
    {
        ShapeKind[] kinds = [ShapeKind.Point, ShapeKind.Line, ShapeKind.Polygon];
        var buckets = kinds.ToDictionary(kind => kind, _ => new List<GeoFeature>());
        var withoutGeometry = new List<GeoFeature>();
        foreach (var feature in dataset.Features)
        {
            if (feature.Geometry is null)
            {
                withoutGeometry.Add(feature);
                continue;
            }
            foreach (var part in Flatten(feature.Geometry))
                buckets[KindOf(part)].Add(new GeoFeature(part, feature.Properties));
        }
        var groups = kinds.Where(kind => buckets[kind].Count > 0).Select(kind => new ShapefileGroup(kind, buckets[kind])).ToList();
        if (groups.Count == 0)
            groups.Add(new ShapefileGroup(ShapeKind.Point, []));
        groups[0].Records.AddRange(withoutGeometry);
        return groups;
    }

    private static IEnumerable<GeoGeometry> Flatten(GeoGeometry geometry) =>
        geometry is GeoCollection collection ? collection.Geometries.SelectMany(Flatten) : [geometry];

    private static ShapeKind KindOf(GeoGeometry geometry) => geometry switch
    {
        GeoPoint or GeoMultiPoint => ShapeKind.Point,
        GeoLineString or GeoMultiLineString => ShapeKind.Line,
        _ => ShapeKind.Polygon,
    };

    /// <summary>Writes <paramref name="group"/> as <paramref name="shpPath"/> with its .shx, .dbf, .prj and .cpg beside it.</summary>
    public static DbfLosses Write(ShapefileGroup group, string shpPath, CancellationToken cancellationToken)
    {
        var shapes = group.Records;
        var hasZ = shapes.Any(r => r.Geometry?.HasZ == true);
        var multiPoint = group.Kind == ShapeKind.Point && shapes.Any(r => r.Geometry is GeoMultiPoint { Points.Count: not 1 });
        var shapeType = group.Kind switch
        {
            ShapeKind.Point => multiPoint ? (hasZ ? 18 : 8) : (hasZ ? 11 : 1),
            ShapeKind.Line => hasZ ? 13 : 3,
            _ => hasZ ? 15 : 5,
        };

        var bounds = new Extent();
        long length = HeaderLength;
        var index = new List<(int Offset, int Length)>(shapes.Count);
        using (var shp = new FileStream(shpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            shp.Write(new byte[HeaderLength]);
            var recordNumber = 0;
            Span<byte> head = stackalloc byte[8];
            foreach (var record in shapes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = Encode(record.Geometry, shapeType, bounds);
                length += 8 + content.Length;
                if (length > int.MaxValue)
                    throw new ConversionRefusedException("error.geo_too_large");
                BinaryPrimitives.WriteInt32BigEndian(head, ++recordNumber);
                BinaryPrimitives.WriteInt32BigEndian(head[4..], content.Length / 2);
                shp.Write(head);
                shp.Write(content);
                index.Add(((int)(length - 8 - content.Length) / 2, content.Length / 2));
            }
            shp.Position = 0;
            shp.Write(Header(shapeType, (int)(length / 2), bounds));
        }

        using (var shx = new FileStream(Path.ChangeExtension(shpPath, "shx"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            shx.Write(Header(shapeType, (HeaderLength + 8 * index.Count) / 2, bounds));
            Span<byte> entry = stackalloc byte[8];
            foreach (var (offset, words) in index)
            {
                BinaryPrimitives.WriteInt32BigEndian(entry, offset);
                BinaryPrimitives.WriteInt32BigEndian(entry[4..], words);
                shx.Write(entry);
            }
        }

        var losses = DbfFormat.Write(Path.ChangeExtension(shpPath, "dbf"), shapes, cancellationToken);
        File.WriteAllText(Path.ChangeExtension(shpPath, "prj"), Wgs84Prj, new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(shpPath, "cpg"), "UTF-8", new UTF8Encoding(false));
        return losses;
    }

    private sealed class Extent
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;
        public double MinZ = double.MaxValue, MaxZ = double.MinValue;

        public bool IsEmpty => MinX > MaxX;

        public void Add(Coord c)
        {
            MinX = Math.Min(MinX, c.X);
            MaxX = Math.Max(MaxX, c.X);
            MinY = Math.Min(MinY, c.Y);
            MaxY = Math.Max(MaxY, c.Y);
            var z = c.HasZ ? c.Z : 0;
            MinZ = Math.Min(MinZ, z);
            MaxZ = Math.Max(MaxZ, z);
        }
    }

    private static byte[] Header(int shapeType, int lengthInWords, Extent bounds)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, 9994);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), lengthInWords);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), shapeType);
        double[] values = bounds.IsEmpty
            ? [0, 0, 0, 0, 0, 0, 0, 0]
            : [bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, bounds.MinZ, bounds.MaxZ, 0, 0];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(36 + 8 * i), values[i]);
        return header;
    }

    /// <summary>The content of one record: its shape type, then the shape.</summary>
    private static byte[] Encode(GeoGeometry? geometry, int fileType, Extent bounds)
    {
        var z = fileType is 11 or 13 or 15 or 18;
        if (geometry is null)
            return BitConverter.GetBytes(0);

        switch (geometry)
        {
            case GeoPoint { Position: var p }:
                return EncodePoints(fileType, [p], bounds, z);
            case GeoMultiPoint { Points: var points }:
                return EncodePoints(fileType, points, bounds, z);
            case GeoLineString line:
                return EncodeParts(fileType, [line.Points], bounds, z);
            case GeoMultiLineString lines:
                return EncodeParts(fileType, lines.Lines, bounds, z);
            case GeoPolygon polygon:
                return EncodeParts(fileType, Rings([polygon]), bounds, z);
            case GeoMultiPolygon polygons:
                return EncodeParts(fileType, Rings(polygons.Polygons), bounds, z);
            default:
                throw new InvalidDataException($"A {geometry.TypeName} can't be written to a shapefile record.");
        }
    }

    /// <summary>Outer rings clockwise, holes counter-clockwise.</summary>
    private static List<IReadOnlyList<Coord>> Rings(IReadOnlyList<GeoPolygon> polygons)
    {
        var rings = new List<IReadOnlyList<Coord>>();
        foreach (var polygon in polygons)
        {
            for (var i = 0; i < polygon.Rings.Count; i++)
                rings.Add(GeoRings.Oriented(polygon.Rings[i], counterClockwise: i != 0));
        }
        return rings;
    }

    private static byte[] EncodePoints(int fileType, IReadOnlyList<Coord> points, Extent bounds, bool z)
    {
        foreach (var point in points)
            bounds.Add(point);
        if (fileType is 1 or 11)
        {
            var size = fileType == 1 ? 20 : 36;
            var content = new byte[size];
            BinaryPrimitives.WriteInt32LittleEndian(content, fileType);
            BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(4), points[0].X);
            BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(12), points[0].Y);
            if (fileType == 11)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(20), points[0].HasZ ? points[0].Z : 0);
                BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(28), NoData);
            }
            return content;
        }
        var multi = new byte[40 + 16 * points.Count + (z ? 16 + 8 * points.Count : 0)];
        BinaryPrimitives.WriteInt32LittleEndian(multi, fileType);
        WriteBox(multi, points);
        BinaryPrimitives.WriteInt32LittleEndian(multi.AsSpan(36), points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(multi.AsSpan(40 + 16 * i), points[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(multi.AsSpan(48 + 16 * i), points[i].Y);
        }
        if (z)
            WriteHeights(multi, 40 + 16 * points.Count, points);
        return multi;
    }

    private static byte[] EncodeParts(int fileType, IReadOnlyList<IReadOnlyList<Coord>> parts, Extent bounds, bool z)
    {
        var all = parts.SelectMany(p => p).ToList();
        foreach (var point in all)
            bounds.Add(point);
        var content = new byte[44 + 4 * parts.Count + 16 * all.Count + (z ? 16 + 8 * all.Count : 0)];
        BinaryPrimitives.WriteInt32LittleEndian(content, fileType);
        WriteBox(content, all);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(36), parts.Count);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(40), all.Count);
        var start = 0;
        for (var p = 0; p < parts.Count; p++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(44 + 4 * p), start);
            start += parts[p].Count;
        }
        var pointsAt = 44 + 4 * parts.Count;
        for (var i = 0; i < all.Count; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(pointsAt + 16 * i), all[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(pointsAt + 16 * i + 8), all[i].Y);
        }
        if (z)
            WriteHeights(content, pointsAt + 16 * all.Count, all);
        return content;
    }

    private static void WriteBox(byte[] content, IReadOnlyList<Coord> points)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(4), points.Min(p => p.X));
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(12), points.Min(p => p.Y));
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(20), points.Max(p => p.X));
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(28), points.Max(p => p.Y));
    }

    private static void WriteHeights(byte[] content, int at, IReadOnlyList<Coord> points)
    {
        var heights = points.Select(p => p.HasZ ? p.Z : 0).ToList();
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(at), heights.Min());
        BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(at + 8), heights.Max());
        for (var i = 0; i < heights.Count; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(content.AsSpan(at + 16 + 8 * i), heights[i]);
    }
}
