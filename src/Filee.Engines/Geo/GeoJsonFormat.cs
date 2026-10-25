// GeoJSON (RFC 7946) in and out: FeatureCollection, Feature or a bare geometry on the way in; one feature per line on
// the way out, polygons in the RFC's orientation (outer ring counter-clockwise). Attributes that are objects or lists
// are kept as their JSON text. Positions beyond longitude / latitude / height are ignored. A "crs" member (GeoJSON
// 2008) is honoured for the systems GeoCrs knows; any other is refused rather than read as degrees.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Geo;

internal static class GeoJsonFormat
{
    /// <summary>Reads a GeoJSON file; <see cref="InvalidDataException"/> says what is wrong with it.</summary>
    public static GeoDataset Read(string path, CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(path);
        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        try
        {
            using var document = Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The file is JSON but not GeoJSON: the top level has no \"type\".");
            ReadCrs(root, dataset);
            switch (type.GetString())
            {
                case "FeatureCollection":
                    if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("The FeatureCollection has no \"features\" list.");
                    var index = 0;
                    foreach (var feature in features.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        dataset.Features.Add(ReadFeature(feature, index++));
                    }
                    break;
                case "Feature":
                    dataset.Features.Add(ReadFeature(root, 0));
                    break;
                default:
                    dataset.Features.Add(new GeoFeature(ReadGeometry(root, "geometry"), []));
                    break;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The file is not valid JSON ({ex.Message})", ex);
        }
        return dataset;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 };
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        // UTF-8 is parsed as it is (the usual, and the cheap, case). UTF-16 and text in an old code page are decoded first;
        // the parser alone would accept invalid UTF-8 inside strings and fail later, in an unhelpful way.
        if (Utf8.IsValid(bytes.AsSpan(skip)))
            return JsonDocument.Parse(bytes.AsMemory(skip), options);
        return JsonDocument.Parse(HwpxConverter.DecodeText(bytes), options);
    }

    private static void ReadCrs(JsonElement root, GeoDataset dataset)
    {
        if (!root.TryGetProperty("crs", out var crs) || crs.ValueKind != JsonValueKind.Object)
            return;
        string? name = null;
        if (crs.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            name = nameElement.GetString();
        if (name is null)
            return;
        dataset.Crs = GeoCrs.FromName(name) ?? throw GeoCrs.Unsupported(name);
        dataset.CrsDeclared = true;
    }

    private static GeoFeature ReadFeature(JsonElement feature, int index)
    {
        if (feature.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Feature {index + 1} is not an object.");
        var properties = new List<KeyValuePair<string, object?>>();
        var hasProperties = feature.TryGetProperty("properties", out var attributes) && attributes.ValueKind == JsonValueKind.Object;
        if (feature.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number
            && !(hasProperties && attributes.TryGetProperty("id", out _)))
            properties.Add(new("id", Value(id)));
        if (hasProperties)
        {
            foreach (var property in attributes.EnumerateObject())
                properties.Add(new(property.Name, Value(property.Value)));
        }
        GeoGeometry? geometry = null;
        if (feature.TryGetProperty("geometry", out var geometryElement) && geometryElement.ValueKind == JsonValueKind.Object)
            geometry = ReadGeometry(geometryElement, $"feature {index + 1}");
        return new GeoFeature(geometry, properties);
    }

    private static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : double.IsFinite(element.GetDouble()) ? element.GetDouble() : element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    private static GeoGeometry? ReadGeometry(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The geometry of {where} has no \"type\".");
        var type = typeElement.GetString();
        if (type == "GeometryCollection")
        {
            if (!element.TryGetProperty("geometries", out var members) || members.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The GeometryCollection of {where} has no \"geometries\" list.");
            var parts = members.EnumerateArray().Select(m => ReadGeometry(m, where)).OfType<GeoGeometry>().ToList();
            return parts.Count == 0 ? null : new GeoCollection(parts);
        }
        if (!element.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The {type} of {where} has no \"coordinates\".");
        switch (type)
        {
            case "Point":
                return coordinates.GetArrayLength() == 0 ? null : new GeoPoint(Position(coordinates, where));
            case "MultiPoint":
                {
                    var points = Line(coordinates, where);
                    return points.Count == 0 ? null : new GeoMultiPoint(points);
                }
            case "LineString":
                {
                    var line = Line(coordinates, where);
                    return line.Count < 2 ? null : new GeoLineString(line);
                }
            case "MultiLineString":
                {
                    var lines = coordinates.EnumerateArray().Select(l => Line(l, where)).Where(l => l.Count >= 2).ToList();
                    return lines.Count == 0 ? null : new GeoMultiLineString([.. lines.Select(l => (IReadOnlyList<Coord>)l)]);
                }
            case "Polygon":
                return Polygon(coordinates, where);
            case "MultiPolygon":
                {
                    var polygons = coordinates.EnumerateArray().Select(p => Polygon(p, where)).OfType<GeoPolygon>().ToList();
                    return polygons.Count == 0 ? null : new GeoMultiPolygon(polygons);
                }
            default:
                throw new InvalidDataException($"Unknown geometry type \"{type}\" in {where}.");
        }
    }

    private static GeoPolygon? Polygon(JsonElement rings, string where)
    {
        if (rings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"A polygon of {where} is not a list of rings.");
        var closed = rings.EnumerateArray().Select(r => GeoRings.Close(Line(r, where))).OfType<IReadOnlyList<Coord>>().ToList();
        return closed.Count == 0 ? null : new GeoPolygon(closed);
    }

    private static List<Coord> Line(JsonElement positions, string where)
    {
        if (positions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"A list of positions in {where} is not a list.");
        var line = new List<Coord>(positions.GetArrayLength());
        foreach (var position in positions.EnumerateArray())
            line.Add(Position(position, where));
        return line;
    }

    private static Coord Position(JsonElement position, string where)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
            throw new InvalidDataException($"A position in {where} needs at least longitude and latitude.");
        double? x = null, y = null;
        var z = double.NaN;
        var i = 0;
        foreach (var number in position.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException($"A position in {where} holds something that is not a number.");
            switch (i++)
            {
                case 0: x = number.GetDouble(); break;
                case 1: y = number.GetDouble(); break;
                case 2: z = number.GetDouble(); break;
            }
        }
        // 1e400 reads as infinity: as a longitude or latitude that is no position, as a height it is no height.
        if (!double.IsFinite(x!.Value) || !double.IsFinite(y!.Value))
            throw new InvalidDataException($"A position in {where} holds a number too large for a coordinate.");
        return new Coord(x.Value, y!.Value, double.IsFinite(z) ? z : double.NaN);
    }

    // ---- Writing ----

    /// <summary>Writes the dataset (in longitude / latitude) as a FeatureCollection, one feature per line.</summary>
    public static void Write(GeoDataset dataset, string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        stream.Write("{\"type\":\"FeatureCollection\",\"features\":[\n"u8);
        var buffer = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        for (var i = 0; i < dataset.Features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Clear();
            writer.Reset(buffer);
            WriteFeature(writer, dataset.Features[i]);
            writer.Flush();
            if (i > 0)
                stream.Write(",\n"u8);
            stream.Write(buffer.WrittenSpan);
        }
        stream.Write("\n]}\n"u8);
    }

    private static void WriteFeature(Utf8JsonWriter writer, GeoFeature feature)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WriteStartObject("properties");
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in feature.Properties)
        {
            // A repeated name (two columns that only differ in case elsewhere) would make duplicate JSON keys.
            if (!written.Add(key))
                continue;
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }
        writer.WriteEndObject();
        writer.WritePropertyName("geometry");
        if (feature.Geometry is { } geometry)
            WriteGeometry(writer, geometry);
        else
            writer.WriteNullValue();
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case long whole:
                writer.WriteNumberValue(whole);
                break;
            case double real when double.IsFinite(real):
                writer.WriteNumberValue(real);
                break;
            case double:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(GeoText.ToText(value));
                break;
        }
    }

    private static void WriteGeometry(Utf8JsonWriter writer, GeoGeometry geometry)
    {
        writer.WriteStartObject();
        writer.WriteString("type", geometry.TypeName);
        if (geometry is GeoCollection collection)
        {
            writer.WriteStartArray("geometries");
            foreach (var member in collection.Geometries)
                WriteGeometry(writer, member);
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }
        var hasZ = geometry.HasZ;
        writer.WritePropertyName("coordinates");
        switch (geometry)
        {
            case GeoPoint point:
                WritePosition(writer, point.Position, hasZ);
                break;
            case GeoMultiPoint multiPoint:
                WriteLine(writer, multiPoint.Points, hasZ);
                break;
            case GeoLineString line:
                WriteLine(writer, line.Points, hasZ);
                break;
            case GeoMultiLineString lines:
                writer.WriteStartArray();
                foreach (var part in lines.Lines)
                    WriteLine(writer, part, hasZ);
                writer.WriteEndArray();
                break;
            case GeoPolygon polygon:
                WritePolygon(writer, polygon, hasZ);
                break;
            case GeoMultiPolygon polygons:
                writer.WriteStartArray();
                foreach (var part in polygons.Polygons)
                    WritePolygon(writer, part, hasZ);
                writer.WriteEndArray();
                break;
        }
        writer.WriteEndObject();
    }

    private static void WritePolygon(Utf8JsonWriter writer, GeoPolygon polygon, bool hasZ)
    {
        writer.WriteStartArray();
        for (var i = 0; i < polygon.Rings.Count; i++)
            WriteLine(writer, GeoRings.Oriented(polygon.Rings[i], counterClockwise: i == 0), hasZ);
        writer.WriteEndArray();
    }

    private static void WriteLine(Utf8JsonWriter writer, IReadOnlyList<Coord> points, bool hasZ)
    {
        writer.WriteStartArray();
        foreach (var point in points)
            WritePosition(writer, point, hasZ);
        writer.WriteEndArray();
    }

    private static void WritePosition(Utf8JsonWriter writer, Coord position, bool hasZ)
    {
        writer.WriteStartArray();
        writer.WriteRawValue(GeoText.Degrees(position.X));
        writer.WriteRawValue(GeoText.Degrees(position.Y));
        if (hasZ)
            writer.WriteRawValue(GeoText.Height(position.HasZ ? position.Z : 0));
        writer.WriteEndArray();
    }
}
