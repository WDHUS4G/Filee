// GPX 1.0 / 1.1 in and out. Reading turns every waypoint into a Point, every route into a LineString and every track
// into a LineString (one segment) or MultiLineString (several), with name, comment, description, source, symbol, type,
// time and the point's elevation (as the height). "gpx_type" says which of the three a feature was. Per-point times of
// tracks are not kept. Writing puts Points into waypoints, lines into tracks (or routes when gpx_type says "route"),
// and the outlines of polygons into tracks, since GPX has no areas; only the fields above survive. GPX is WGS 84.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Geo;

internal static class GpxFormat
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    /// <summary>The attributes GPX can hold, in the order of its schema; "elevation" and "time" are written per point.</summary>
    private static readonly string[] Fields = ["name", "cmt", "desc", "src", "sym", "number", "type"];

    public static GeoDataset Read(string path, CancellationToken cancellationToken)
    {
        XDocument document;
        using (var stream = File.OpenRead(path))
            document = GeoXml.Load(stream);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("gpx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is XML but not GPX: the root element is not <gpx>.");

        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        foreach (var element in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element.Name.LocalName)
            {
                case "wpt":
                    {
                        var properties = Properties(element, "waypoint");
                        var point = Position(element);
                        dataset.Features.Add(new GeoFeature(new GeoPoint(point), properties));
                        break;
                    }
                case "rte":
                    {
                        var line = element.Elements().Where(e => e.Name.LocalName == "rtept").Select(Position).ToList();
                        dataset.Features.Add(new GeoFeature(line.Count < 2 ? null : new GeoLineString(line), Properties(element, "route")));
                        break;
                    }
                case "trk":
                    {
                        var segments = element.Elements().Where(e => e.Name.LocalName == "trkseg")
                            .Select(s => s.Elements().Where(e => e.Name.LocalName == "trkpt").Select(Position).ToList())
                            .Where(l => l.Count >= 2).Select(l => (IReadOnlyList<Coord>)l).ToList();
                        GeoGeometry? geometry = segments.Count switch
                        {
                            0 => null,
                            1 => new GeoLineString(segments[0]),
                            _ => new GeoMultiLineString(segments),
                        };
                        dataset.Features.Add(new GeoFeature(geometry, Properties(element, "track")));
                        break;
                    }
            }
        }
        return dataset;
    }

    private static List<KeyValuePair<string, object?>> Properties(XElement element, string kind)
    {
        var properties = new List<KeyValuePair<string, object?>> { new("gpx_type", kind) };
        foreach (var name in new[] { "name", "cmt", "desc", "src", "sym", "number", "type", "time" })
        {
            var value = element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            if (!string.IsNullOrEmpty(value))
                properties.Add(new(name, name == "number" ? GeoText.Typed(value) : value));
        }
        return properties;
    }

    private static Coord Position(XElement point)
    {
        if (!GeoXml.TryNumber((string?)point.Attribute("lon") ?? "", out var lon) || !GeoXml.TryNumber((string?)point.Attribute("lat") ?? "", out var lat))
            throw new InvalidDataException($"A GPX <{point.Name.LocalName}> has no valid lat and lon.");
        if (lat is < -90 or > 90 || lon is < -180 or > 360)
            throw new InvalidDataException($"A GPX <{point.Name.LocalName}> is outside the Earth (lat {lat}, lon {lon}).");
        var elevation = point.Elements().FirstOrDefault(e => e.Name.LocalName == "ele")?.Value;
        return new Coord(lon, lat, elevation is not null && GeoXml.TryNumber(elevation.Trim(), out var z) ? z : double.NaN);
    }

    // ---- Writing ----

    /// <summary>Writes a GPX 1.1 file; returns true if some attributes or polygon areas could not be kept.</summary>
    public static bool Write(GeoDataset dataset, string path, CancellationToken cancellationToken)
    {
        var lossy = false;
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new System.Text.UTF8Encoding(false), CheckCharacters = false };
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("gpx", Gpx.NamespaceName);
        writer.WriteAttributeString("version", "1.1");
        writer.WriteAttributeString("creator", "Filee");
        foreach (var feature in dataset.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feature.Geometry is not { } geometry)
                continue;
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpx_type", "time" };
            foreach (var field in Fields)
                kept.Add(field);
            lossy |= feature.Properties.Any(p => !kept.Contains(p.Key) && GeoText.ToText(p.Value).Length > 0);
            var asRoute = string.Equals(feature.FindText("gpx_type"), "route", StringComparison.OrdinalIgnoreCase);
            // Points become waypoints; every line, and the outline of every polygon, is a segment of the feature's track
            // (or a route of its own), so one feature stays one track.
            var lines = new List<IReadOnlyList<Coord>>();
            var outlines = false;
            foreach (var part in geometry.Parts())
            {
                switch (part)
                {
                    case GeoPoint point:
                        WritePoint(writer, "wpt", point.Position, feature, withFields: true);
                        break;
                    case GeoLineString line:
                        lines.Add(line.Points);
                        break;
                    case GeoPolygon polygon:
                        lossy = outlines = true;
                        lines.AddRange(polygon.Rings);
                        break;
                }
            }
            if (lines.Count > 0)
                WriteLines(writer, lines, feature, asRoute && !outlines);
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
        return lossy;
    }

    private static void WriteLines(XmlWriter writer, IReadOnlyList<IReadOnlyList<Coord>> lines, GeoFeature feature, bool route)
    {
        if (route)
        {
            // A route is one line; extra parts become routes of their own.
            foreach (var line in lines)
            {
                writer.WriteStartElement("rte");
                WriteFields(writer, feature, trackFields: true);
                foreach (var point in line)
                    WritePoint(writer, "rtept", point, feature, withFields: false);
                writer.WriteEndElement();
            }
            return;
        }
        writer.WriteStartElement("trk");
        WriteFields(writer, feature, trackFields: true);
        foreach (var line in lines)
        {
            writer.WriteStartElement("trkseg");
            foreach (var point in line)
                WritePoint(writer, "trkpt", point, feature, withFields: false);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WritePoint(XmlWriter writer, string element, Coord point, GeoFeature feature, bool withFields)
    {
        writer.WriteStartElement(element);
        writer.WriteAttributeString("lat", GeoText.Degrees(point.Y));
        writer.WriteAttributeString("lon", GeoText.Degrees(point.X));
        if (point.HasZ)
            writer.WriteElementString("ele", GeoText.Height(point.Z));
        if (withFields)
        {
            if (Time(feature.FindText("time")) is { } time)
                writer.WriteElementString("time", time);
            WriteFields(writer, feature, trackFields: false);
        }
        writer.WriteEndElement();
    }

    /// <summary>The GPX fields in schema order; "number" belongs to routes and tracks only.</summary>
    private static void WriteFields(XmlWriter writer, GeoFeature feature, bool trackFields)
    {
        foreach (var field in Fields)
        {
            if (field == "number" && !trackFields)
                continue;
            if (field is "sym" && trackFields)
                continue;
            var text = feature.FindText(field);
            if (text is null)
                continue;
            if (field == "number" && !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;
            writer.WriteElementString(field, GeoXml.Clean(text));
        }
    }

    /// <summary>An xsd:dateTime from a time attribute in any common ISO form, or null if it isn't one.</summary>
    private static string? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
}
