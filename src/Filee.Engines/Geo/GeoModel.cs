// The in-memory model every geospatial vector format is read into and written from: features (a geometry and an
// ordered list of attributes) in longitude / latitude. Each reader produces it and each writer consumes it, so GeoJSON,
// KML, KMZ, GPX, Shapefile and CSV convert to each other through this one shape. See docs/ENGINES.md, "Geospatial".

using System.Globalization;

namespace Filee.Engines.Geo;

/// <summary>One position: X = longitude and Y = latitude in degrees once a file is read, <see cref="Z"/> the height or NaN.</summary>
internal readonly record struct Coord(double X, double Y, double Z = double.NaN)
{
    /// <summary>True if the position has a height.</summary>
    public bool HasZ => !double.IsNaN(Z);

    /// <summary>True if both X and Y are real numbers.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>A geometry: a point, line, polygon, a collection of them, or a collection of mixed kinds.</summary>
internal abstract class GeoGeometry
{
    /// <summary>The OGC name of the type, e.g. <c>"Polygon"</c>.</summary>
    public abstract string TypeName { get; }

    /// <summary>Every position of the geometry, in order.</summary>
    public abstract IEnumerable<Coord> Coordinates();

    /// <summary>The same geometry with <paramref name="map"/> applied to every position.</summary>
    public abstract GeoGeometry Map(Func<Coord, Coord> map);

    /// <summary>True if any position has a height.</summary>
    public bool HasZ => Coordinates().Any(c => c.HasZ);

    /// <summary>The simple parts of the geometry: itself unless it is a collection or a Multi* geometry.</summary>
    public virtual IEnumerable<GeoGeometry> Parts() => [this];

    internal static List<Coord> Map(IEnumerable<Coord> line, Func<Coord, Coord> map) => [.. line.Select(map)];
}

internal sealed class GeoPoint(Coord position) : GeoGeometry
{
    public Coord Position { get; } = position;
    public override string TypeName => "Point";
    public override IEnumerable<Coord> Coordinates() => [Position];
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoPoint(map(Position));
}

internal sealed class GeoMultiPoint(IReadOnlyList<Coord> points) : GeoGeometry
{
    public IReadOnlyList<Coord> Points { get; } = points;
    public override string TypeName => "MultiPoint";
    public override IEnumerable<Coord> Coordinates() => Points;
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoMultiPoint(Map(Points, map));
    public override IEnumerable<GeoGeometry> Parts() => Points.Select(p => new GeoPoint(p));
}

internal sealed class GeoLineString(IReadOnlyList<Coord> points) : GeoGeometry
{
    public IReadOnlyList<Coord> Points { get; } = points;
    public override string TypeName => "LineString";
    public override IEnumerable<Coord> Coordinates() => Points;
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoLineString(Map(Points, map));
}

internal sealed class GeoMultiLineString(IReadOnlyList<IReadOnlyList<Coord>> lines) : GeoGeometry
{
    public IReadOnlyList<IReadOnlyList<Coord>> Lines { get; } = lines;
    public override string TypeName => "MultiLineString";
    public override IEnumerable<Coord> Coordinates() => Lines.SelectMany(l => l);
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoMultiLineString([.. Lines.Select(l => (IReadOnlyList<Coord>)Map(l, map))]);
    public override IEnumerable<GeoGeometry> Parts() => Lines.Select(l => new GeoLineString(l));
}

/// <summary>A polygon: the first ring is the outer boundary, the others are holes. Rings are closed (first = last).</summary>
internal sealed class GeoPolygon(IReadOnlyList<IReadOnlyList<Coord>> rings) : GeoGeometry
{
    public IReadOnlyList<IReadOnlyList<Coord>> Rings { get; } = rings;
    public override string TypeName => "Polygon";
    public override IEnumerable<Coord> Coordinates() => Rings.SelectMany(r => r);
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoPolygon([.. Rings.Select(r => (IReadOnlyList<Coord>)Map(r, map))]);
}

internal sealed class GeoMultiPolygon(IReadOnlyList<GeoPolygon> polygons) : GeoGeometry
{
    public IReadOnlyList<GeoPolygon> Polygons { get; } = polygons;
    public override string TypeName => "MultiPolygon";
    public override IEnumerable<Coord> Coordinates() => Polygons.SelectMany(p => p.Coordinates());
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoMultiPolygon([.. Polygons.Select(p => (GeoPolygon)p.Map(map))]);
    public override IEnumerable<GeoGeometry> Parts() => Polygons;
}

/// <summary>Geometries of any kinds in one feature (GeoJSON GeometryCollection, KML MultiGeometry).</summary>
internal sealed class GeoCollection(IReadOnlyList<GeoGeometry> geometries) : GeoGeometry
{
    public IReadOnlyList<GeoGeometry> Geometries { get; } = geometries;
    public override string TypeName => "GeometryCollection";
    public override IEnumerable<Coord> Coordinates() => Geometries.SelectMany(g => g.Coordinates());
    public override GeoGeometry Map(Func<Coord, Coord> map) => new GeoCollection([.. Geometries.Select(g => g.Map(map))]);
    public override IEnumerable<GeoGeometry> Parts() => Geometries.SelectMany(g => g.Parts());
}

/// <summary>
/// One feature: a geometry (null when the record has no location) and its attributes in file order. A value is a
/// <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or null.
/// </summary>
internal sealed class GeoFeature(GeoGeometry? geometry, List<KeyValuePair<string, object?>> properties)
{
    public GeoGeometry? Geometry { get; } = geometry;
    public List<KeyValuePair<string, object?>> Properties { get; } = properties;

    /// <summary>The value of the first attribute called <paramref name="names"/> (compared without case), or null.</summary>
    public object? Find(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in Properties)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }
        return null;
    }

    /// <summary>Like <see cref="Find"/>, as text; null for a missing or empty value.</summary>
    public string? FindText(params string[] names) =>
        Find(names) is { } value && GeoText.ToText(value) is { Length: > 0 } text ? text : null;
}

/// <summary>Everything read from one file.</summary>
internal sealed class GeoDataset
{
    /// <summary>A name for the data, e.g. the file name; KML and GPX use it as the document name.</summary>
    public string Name { get; set; } = "";

    public List<GeoFeature> Features { get; } = [];

    /// <summary>The coordinate system the positions are in until <see cref="GeoConverter"/> converts them to WGS 84.</summary>
    public GeoCrs Crs { get; set; } = GeoCrs.Wgs84;

    /// <summary>True if the file said which system it uses (a .prj, a GeoJSON crs member); KML and GPX are always WGS 84.</summary>
    public bool CrsDeclared { get; set; }

    /// <summary>True if some rows of a CSV had no usable location and so have no geometry.</summary>
    public bool RowsWithoutLocation { get; set; }

    /// <summary>The attribute names of all features, in the order they first appear.</summary>
    public List<string> Columns()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<string>();
        foreach (var feature in Features)
        {
            foreach (var (key, _) in feature.Properties)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }
        }
        return columns;
    }
}

/// <summary>Text forms of coordinates and attribute values shared by the writers.</summary>
internal static class GeoText
{
    /// <summary>A coordinate with up to ten decimals (about 0.01 mm of longitude / latitude), no exponent.</summary>
    public static string Degrees(double value) => Trim(value.ToString("0.##########", CultureInfo.InvariantCulture));

    /// <summary>A height with up to six decimals.</summary>
    public static string Height(double value) => Trim(value.ToString("0.######", CultureInfo.InvariantCulture));

    /// <summary>The shortest text that reads back as the same number, never with an exponent a spreadsheet would trip on.</summary>
    public static string Number(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return Trim(text.Contains('E', StringComparison.Ordinal) ? value.ToString("0.####################", CultureInfo.InvariantCulture) : text);
    }

    /// <summary>An attribute value as text: numbers as <see cref="Number"/>, booleans as true / false, null as empty.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        string text => text,
        bool flag => flag ? "true" : "false",
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double real => Number(real),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Text from a file as the most specific attribute value: whole number, number, true / false, else the text.</summary>
    public static object? Typed(string text)
    {
        if (text.Length is 0 or > 24)
            return text.Length == 0 ? null : text;
        // "007" and "+1" are identifiers or phone numbers, not numbers.
        var digits = text[0] == '-' ? text[1..] : text;
        if (digits.Length == 0 || digits.Length > 1 && digits[0] == '0' && digits[1] != '.')
            return text;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            return whole;
        if ((char.IsAsciiDigit(digits[0]) || digits[0] == '.') && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
            return real;
        return text switch
        {
            "true" or "True" or "TRUE" => true,
            "false" or "False" or "FALSE" => false,
            _ => text,
        };
    }

    private static string Trim(string text) => text is "-0" ? "0" : text;
}

/// <summary>Ring helpers: closing, orientation and containment.</summary>
internal static class GeoRings
{
    /// <summary>
    /// The ring closed (first position repeated at the end), or null if it has fewer than three distinct positions
    /// and so encloses nothing.
    /// </summary>
    public static IReadOnlyList<Coord>? Close(IReadOnlyList<Coord> ring)
    {
        if (ring.Count < 3)
            return null;
        var closed = ring.Count > 0 && Same(ring[0], ring[^1]);
        var points = closed ? ring : [.. ring, ring[0]];
        return points.Count < 4 ? null : points;
    }

    /// <summary>Twice the area with a sign: positive when the ring runs counter-clockwise (X right, Y up).</summary>
    public static double SignedArea(IReadOnlyList<Coord> ring)
    {
        var sum = 0.0;
        for (var i = 0; i + 1 < ring.Count; i++)
            sum += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
        return sum;
    }

    /// <summary>The ring in the direction asked for (reversed when it runs the other way).</summary>
    public static IReadOnlyList<Coord> Oriented(IReadOnlyList<Coord> ring, bool counterClockwise)
    {
        var area = SignedArea(ring);
        return area == 0 || area > 0 == counterClockwise ? ring : [.. ring.Reverse()];
    }

    /// <summary>True if <paramref name="point"/> lies inside <paramref name="ring"/> (even-odd rule).</summary>
    public static bool Contains(IReadOnlyList<Coord> ring, Coord point)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if (ring[i].Y > point.Y != ring[j].Y > point.Y
                && point.X < (ring[j].X - ring[i].X) * (point.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static bool Same(Coord a, Coord b) => a.X == b.X && a.Y == b.Y;
}
