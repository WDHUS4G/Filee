// KML and KMZ in and out. Reading takes every Placemark wherever it sits (Document, Folder, nested folders) with its
// name, description and ExtendedData (Data and SimpleData) as attributes, and Point, LineString, LinearRing, Polygon,
// MultiGeometry and Google's gx:Track as geometry. Styles, icons, overlays, network links and time spans are not data
// of the features and are left out. KMZ is a ZIP with the .kml inside (doc.kml first). Writing makes one Document with
// a Placemark per feature; attributes go to ExtendedData. KML positions are always WGS 84 longitude, latitude, height.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Geo;

internal static class KmlFormat
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    /// <summary>The largest .kml a KMZ may hold, declared or really unpacked: a ZIP of a few KB can claim far more.</summary>
    internal const long MaxKmlBytes = 256L << 20;

    /// <summary>How deep MultiGeometry may nest before the file is refused (real files nest one level).</summary>
    private const int MaxGeometryDepth = 32;

    /// <summary>Reads a .kml or a .kmz file; <paramref name="maxKmlBytes"/> limits the .kml inside a KMZ.</summary>
    public static GeoDataset Read(string path, bool zipped, CancellationToken cancellationToken, long maxKmlBytes = MaxKmlBytes)
    {
        XDocument document;
        if (zipped)
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(path);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"The file is not a valid KMZ archive ({ex.Message})", ex);
            }
            using (archive)
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The KMZ has no .kml file inside.");
                if (entry.Length > maxKmlBytes)
                    throw new InvalidDataException($"The .kml inside the KMZ is too large ({entry.Length / (1 << 20)} MB, the limit is {maxKmlBytes / (1 << 20)} MB).");
                // The size in a ZIP header can be wrong: the unpacked bytes are counted as they are read.
                using var stream = new LimitedStream(entry.Open(), maxKmlBytes, "The .kml inside the KMZ is larger than " + maxKmlBytes / (1 << 20) + " MB.");
                document = GeoXml.Load(stream);
            }
        }
        else
        {
            using var stream = File.OpenRead(path);
            document = GeoXml.Load(stream);
        }

        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("kml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is XML but not KML: the root element is not <kml>.");

        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        foreach (var placemark in root.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataset.Features.Add(ReadPlacemark(placemark));
        }
        return dataset;
    }

    private static GeoFeature ReadPlacemark(XElement placemark)
    {
        var properties = new List<KeyValuePair<string, object?>>();
        if (Text(placemark, "name") is { Length: > 0 } name)
            properties.Add(new("name", name));
        if (Text(placemark, "description") is { Length: > 0 } description)
            properties.Add(new("description", description));
        var names = new HashSet<string>(properties.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var data in placemark.Descendants().Where(e => e.Name.LocalName is "Data" or "SimpleData"))
        {
            var key = (string?)data.Attribute("name");
            if (string.IsNullOrEmpty(key))
                continue;
            var value = data.Name.LocalName == "Data" ? Text(data, "value") ?? data.Value : data.Value;
            if (names.Add(key))
                properties.Add(new(key, GeoText.Typed(value.Trim())));
        }
        var geometries = placemark.Elements().Select(e => ReadGeometry(e, 0)).OfType<GeoGeometry>().ToList();
        GeoGeometry? geometry = geometries.Count switch
        {
            0 => null,
            1 => geometries[0],
            _ => new GeoCollection(geometries),
        };
        return new GeoFeature(geometry, properties);
    }

    private static GeoGeometry? ReadGeometry(XElement element, int depth)
    {
        if (depth > MaxGeometryDepth)
            throw new InvalidDataException($"The KML nests geometries more than {MaxGeometryDepth} levels deep.");
        switch (element.Name.LocalName)
        {
            case "Point":
                {
                    var points = Coordinates(element);
                    return points.Count == 0 ? null : new GeoPoint(points[0]);
                }
            case "LineString" or "LinearRing":
                {
                    var line = Coordinates(element);
                    return line.Count < 2 ? null : new GeoLineString(line);
                }
            case "Track" or "MultiTrack":
                {
                    // gx:Track lists <gx:coord>lon lat alt</gx:coord>; a MultiTrack holds several.
                    var lines = element.Name.LocalName == "Track" ? [element] : element.Elements().Where(e => e.Name.LocalName == "Track").ToList();
                    var parsed = lines.Select(TrackCoordinates).Where(l => l.Count >= 2).Select(l => (IReadOnlyList<Coord>)l).ToList();
                    return parsed.Count switch
                    {
                        0 => null,
                        1 => new GeoLineString(parsed[0]),
                        _ => new GeoMultiLineString(parsed),
                    };
                }
            case "Polygon":
                {
                    var rings = new List<IReadOnlyList<Coord>>();
                    foreach (var boundary in element.Elements().Where(e => e.Name.LocalName is "outerBoundaryIs" or "innerBoundaryIs")
                                 .OrderBy(e => e.Name.LocalName == "outerBoundaryIs" ? 0 : 1))
                    {
                        var ring = boundary.Descendants().FirstOrDefault(e => e.Name.LocalName == "LinearRing");
                        if (ring is not null && GeoRings.Close(Coordinates(ring)) is { } closed)
                            rings.Add(closed);
                    }
                    return rings.Count == 0 ? null : new GeoPolygon(rings);
                }
            case "MultiGeometry":
                {
                    var parts = element.Elements().Select(e => ReadGeometry(e, depth + 1)).OfType<GeoGeometry>().ToList();
                    return parts.Count switch
                    {
                        0 => null,
                        1 => parts[0],
                        _ when parts.All(p => p is GeoPoint) => new GeoMultiPoint([.. parts.Cast<GeoPoint>().Select(p => p.Position)]),
                        _ when parts.All(p => p is GeoLineString) => new GeoMultiLineString([.. parts.Cast<GeoLineString>().Select(p => p.Points)]),
                        _ when parts.All(p => p is GeoPolygon) => new GeoMultiPolygon([.. parts.Cast<GeoPolygon>()]),
                        _ => new GeoCollection(parts),
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>The positions of the &lt;coordinates&gt; child: "lon,lat[,alt]" tuples separated by white space.</summary>
    private static List<Coord> Coordinates(XElement element)
    {
        var coordinates = element.Elements().FirstOrDefault(e => e.Name.LocalName == "coordinates");
        var line = new List<Coord>();
        if (coordinates is null)
            return line;
        // Some writers put spaces after the commas ("1, 2, 3"): glue those back before splitting on white space.
        var text = GlueCommas(coordinates.Value);
        foreach (var tuple in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2 || !GeoXml.TryNumber(parts[0], out var x) || !GeoXml.TryNumber(parts[1], out var y))
                throw new InvalidDataException($"A KML position is not \"longitude,latitude\": \"{Shorten(tuple)}\".");
            var z = parts.Length > 2 && GeoXml.TryNumber(parts[2], out var height) ? height : double.NaN;
            line.Add(new Coord(x, y, z));
        }
        return line;
    }

    /// <summary>
    /// The text without the white space next to commas (a run of white space elsewhere shrinks to its first character).
    /// Linear: a regular expression for this takes quadratic time on a long run of spaces.
    /// </summary>
    private static string GlueCommas(string text)
    {
        if (!text.Contains(',', StringComparison.Ordinal))
            return text;
        var builder = new System.Text.StringBuilder(text.Length);
        var space = '\0'; // white space seen since the last character: kept unless a comma is next to it
        var afterComma = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!afterComma && space == '\0')
                    space = ch;
                continue;
            }
            if (ch != ',' && space != '\0')
                builder.Append(space);
            space = '\0';
            builder.Append(ch);
            afterComma = ch == ',';
        }
        if (space != '\0')
            builder.Append(space);
        return builder.ToString();
    }

    private static List<Coord> TrackCoordinates(XElement track)
    {
        var line = new List<Coord>();
        foreach (var coord in track.Elements().Where(e => e.Name.LocalName == "coord"))
        {
            var parts = coord.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !GeoXml.TryNumber(parts[0], out var x) || !GeoXml.TryNumber(parts[1], out var y))
                throw new InvalidDataException($"A KML track position is not \"longitude latitude\": \"{Shorten(coord.Value)}\".");
            line.Add(new Coord(x, y, parts.Length > 2 && GeoXml.TryNumber(parts[2], out var height) ? height : double.NaN));
        }
        return line;
    }

    private static string? Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    // ---- Writing ----

    /// <summary>Writes a .kml, or a .kmz (a ZIP holding doc.kml) when <paramref name="zipped"/>.</summary>
    public static void Write(GeoDataset dataset, string path, bool zipped, CancellationToken cancellationToken)
    {
        if (!zipped)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            WriteDocument(dataset, stream, cancellationToken);
            return;
        }
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("doc.kml", CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        WriteDocument(dataset, entryStream, cancellationToken);
    }

    private static void WriteDocument(GeoDataset dataset, Stream stream, CancellationToken cancellationToken)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new System.Text.UTF8Encoding(false),
            CheckCharacters = false,
        };
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("kml", Kml.NamespaceName);
        writer.WriteStartElement("Document");
        writer.WriteElementString("name", GeoXml.Clean(dataset.Name));
        foreach (var feature in dataset.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WritePlacemark(writer, feature);
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WritePlacemark(XmlWriter writer, GeoFeature feature)
    {
        writer.WriteStartElement("Placemark");
        var name = feature.FindText("name", "title", "label");
        if (name is not null)
            writer.WriteElementString("name", GeoXml.Clean(name));
        var description = feature.FindText("description", "desc", "comment");
        if (description is not null)
            writer.WriteElementString("description", GeoXml.Clean(description));
        var extra = feature.Properties.Where(p => !IsNameOrDescription(p.Key, name, description, feature)).ToList();
        if (extra.Count > 0)
        {
            writer.WriteStartElement("ExtendedData");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in extra)
            {
                if (!used.Add(key))
                    continue;
                writer.WriteStartElement("Data");
                writer.WriteAttributeString("name", GeoXml.Clean(key));
                writer.WriteElementString("value", GeoXml.Clean(GeoText.ToText(value)));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        if (feature.Geometry is { } geometry)
            WriteGeometry(writer, geometry);
        writer.WriteEndElement();
    }

    /// <summary>True for the attributes already written as the Placemark's name and description.</summary>
    private static bool IsNameOrDescription(string key, string? name, string? description, GeoFeature feature)
    {
        if (name is not null && key.Equals(FirstKey(feature, "name", "title", "label"), StringComparison.OrdinalIgnoreCase))
            return true;
        return description is not null && key.Equals(FirstKey(feature, "description", "desc", "comment"), StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstKey(GeoFeature feature, params string[] names)
    {
        foreach (var wanted in names)
        {
            var found = feature.Properties.FirstOrDefault(p => p.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase) && GeoText.ToText(p.Value).Length > 0);
            if (found.Key is not null)
                return found.Key;
        }
        return null;
    }

    private static void WriteGeometry(XmlWriter writer, GeoGeometry geometry)
    {
        switch (geometry)
        {
            case GeoPoint point:
                writer.WriteStartElement("Point");
                WriteCoordinates(writer, [point.Position]);
                writer.WriteEndElement();
                break;
            case GeoLineString line:
                writer.WriteStartElement("LineString");
                WriteCoordinates(writer, line.Points);
                writer.WriteEndElement();
                break;
            case GeoPolygon polygon:
                WritePolygon(writer, polygon);
                break;
            default:
                // Several parts of one feature: MultiGeometry holds the simple geometries.
                writer.WriteStartElement("MultiGeometry");
                foreach (var part in geometry.Parts())
                    WriteGeometry(writer, part);
                writer.WriteEndElement();
                break;
        }
    }

    private static void WritePolygon(XmlWriter writer, GeoPolygon polygon)
    {
        writer.WriteStartElement("Polygon");
        for (var i = 0; i < polygon.Rings.Count; i++)
        {
            writer.WriteStartElement(i == 0 ? "outerBoundaryIs" : "innerBoundaryIs");
            writer.WriteStartElement("LinearRing");
            WriteCoordinates(writer, GeoRings.Oriented(polygon.Rings[i], counterClockwise: i == 0));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteCoordinates(XmlWriter writer, IReadOnlyList<Coord> points)
    {
        var hasZ = points.Any(p => p.HasZ);
        writer.WriteStartElement("coordinates");
        writer.WriteString(string.Join(' ', points.Select(p =>
            hasZ ? $"{GeoText.Degrees(p.X)},{GeoText.Degrees(p.Y)},{GeoText.Height(p.HasZ ? p.Z : 0)}" : $"{GeoText.Degrees(p.X)},{GeoText.Degrees(p.Y)}")));
        writer.WriteEndElement();
    }
}

/// <summary>XML helpers shared by the KML and GPX formats.</summary>
internal static class GeoXml
{
    /// <summary>How many levels of elements a KML or GPX file may nest (real files nest a dozen).</summary>
    internal const int MaxElementDepth = 256;

    /// <summary>
    /// How many elements a KML or GPX file may hold. Each costs about 100 bytes in the tree whatever its size, so a
    /// file of tiny elements ("&lt;a/&gt;" repeated) would use 25 bytes of memory per character: 40 million characters
    /// in a 150 KB KMZ took 1.1 GB. Real files hold a few elements per feature; this allows a million features.
    /// </summary>
    internal const int MaxElements = 3_000_000;

    /// <summary>
    /// Parses XML without DTDs or external entities, so a hostile file can't read files or expand into gigabytes, and
    /// refuses elements nested more than <see cref="MaxElementDepth"/> levels. The tree is built here from the reader
    /// rather than by <c>XDocument.Load</c>, which takes time quadratic in the depth (a 1.4 MB file nested 100,000 deep
    /// kept it busy for 48 seconds, the reader alone needs a quarter of a second) and so couldn't be allowed to see such
    /// a file. Namespace declarations and prefixes are not kept; names are the local name and namespace.
    /// </summary>
    public static XDocument Load(Stream stream, int maxElements = MaxElements)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            CheckCharacters = false,
            // About 1 GB of memory at the most: the tree takes some 16 bytes for every character.
            MaxCharactersInDocument = 1L << 26,
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            var document = new XDocument();
            XContainer current = document;
            var depth = 0;
            var elements = 0;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (++depth > MaxElementDepth)
                            throw new InvalidDataException($"The file nests elements more than {MaxElementDepth} levels deep.");
                        if (++elements > maxElements)
                            throw new InvalidDataException("The file has more than " + maxElements.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " elements.");
                        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                        var empty = reader.IsEmptyElement;
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/")
                                    element.SetAttributeValue(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value);
                            }
                            reader.MoveToElement();
                        }
                        current.Add(element);
                        if (empty)
                            depth--;
                        else
                            current = element;
                        break;
                    case XmlNodeType.EndElement:
                        current = current.Parent ?? (XContainer)document;
                        depth--;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        // Text outside the root element (white space between declarations) is no content.
                        if (current is XElement)
                            current.Add(new XText(reader.Value));
                        break;
                }
            }
            return document;
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The file is not valid XML ({ex.Message})", ex);
        }
    }

    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Text without the control characters XML 1.0 can't hold (they would make the file unreadable).</summary>
    public static string Clean(string text) =>
        text.Any(c => c < ' ' && c is not ('\t' or '\n' or '\r') || c is '￾' or '￿')
            ? new string([.. text.Where(c => !(c < ' ' && c is not ('\t' or '\n' or '\r') || c is '￾' or '￿'))])
            : text;
}
