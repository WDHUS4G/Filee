// Geospatial CSV in and out. Reading finds the location of each row in a WKT column (wkt, geom, geometry, the_geom,
// shape) or in a pair of longitude / latitude columns (lon, lng, long, longitude, x and lat, latitude, y; a decimal
// comma is accepted); the other columns become attributes, typed as numbers or true / false when they look like them.
// Rows with no usable location stay in the data without a geometry. Writing gives longitude and latitude columns (and
// elevation) when every geometry is a point, otherwise one WKT column; the attributes follow. CSV has no coordinate
// system, so it is always WGS 84.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Engines.Data;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Geo;

internal static class GeoCsvFormat
{
    private static readonly string[] WktNames = ["wktgeo", "wkt", "geom", "geometry", "thegeom", "wktgeom", "wktgeometry", "geometrywkt", "shape", "geometria"];
    private static readonly string[] LonNames = ["longitude", "lon", "lng", "long", "lons", "longitud", "longitudine", "lond", "xcoord", "xcoordinate", "x", "easting", "lonx"];
    private static readonly string[] LatNames = ["latitude", "lat", "lats", "latitud", "latitudine", "latd", "ycoord", "ycoordinate", "y", "northing", "laty"];
    private static readonly string[] HeightNames = ["elevation", "elev", "altitude", "alt", "z", "height", "ele"];

    /// <summary>Reads a CSV with a location in each row; <see cref="ConversionRefusedException"/> if it has none.</summary>
    public static GeoDataset Read(string path, CancellationToken cancellationToken)
    {
        var rows = CsvData.ReadRows(path);
        if (rows.Count == 0)
            throw new ConversionRefusedException("error.geo_empty");
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var keys = header.Select(Key).ToList();

        var wkt = Find(keys, WktNames);
        var lon = Find(keys, LonNames);
        var lat = Find(keys, LatNames);
        if (wkt < 0 && (lon < 0 || lat < 0))
            throw new ConversionRefusedException("error.geo_csv_no_coordinates", string.Join(", ", header.Take(8)));
        var height = wkt < 0 ? Find(keys, HeightNames) : -1;

        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        var names = UniqueNames(header);
        var used = wkt >= 0 ? new HashSet<int> { wkt } : new HashSet<int> { lon, lat };
        var located = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];
            string Cell(int column) => column < row.Count ? row[column].Trim() : "";

            GeoGeometry? geometry = null;
            if (wkt >= 0)
            {
                var text = Cell(wkt);
                if (text.Length > 0)
                {
                    try
                    {
                        geometry = WktFormat.Parse(text);
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidDataException($"Row {r + 1}: the {header[wkt]} column is not valid WKT ({ex.Message}).", ex);
                    }
                }
            }
            else if (Number(Cell(lon)) is { } x && Number(Cell(lat)) is { } y)
            {
                var z = height >= 0 && Number(Cell(height)) is { } elevation ? elevation : double.NaN;
                geometry = new GeoPoint(new Coord(x, y, z));
            }
            if (geometry is not null)
                located++;
            var properties = new List<KeyValuePair<string, object?>>();
            // A row shorter than the header has nothing in the columns it lacks: those are not made up for every row
            // (a wide header over many short rows would otherwise take rows x columns of memory).
            for (var c = 0; c < Math.Min(header.Count, row.Count); c++)
            {
                if (!used.Contains(c))
                    properties.Add(new(names[c], GeoText.Typed(Cell(c))));
            }
            dataset.Features.Add(new GeoFeature(geometry, properties));
        }
        if (dataset.Features.Count == 0)
            throw new ConversionRefusedException("error.geo_empty");
        if (located == 0)
            throw new ConversionRefusedException("error.geo_csv_no_locations", wkt >= 0 ? header[wkt] : $"{header[lon]}, {header[lat]}");
        dataset.RowsWithoutLocation = located < dataset.Features.Count;
        return dataset;
    }

    private static int Find(List<string> keys, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var index = keys.IndexOf(candidate);
            if (index >= 0)
                return index;
        }
        return -1;
    }

    private static string Key(string header) => new([.. header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>Column names with blanks named "columnN" and repeats numbered, so every attribute has its own name.</summary>
    private static List<string> UniqueNames(List<string> header)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The next number to try for a repeated name: a header of 100,000 identical names costs 100,000 steps, not 5 billion.
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Length == 0 ? $"column{i + 1}" : header[i];
            var unique = name;
            if (!seen.Add(unique))
            {
                var n = next.GetValueOrDefault(name, 2);
                while (!seen.Add(unique = $"{name}_{n}"))
                    n++;
                next[name] = n + 1;
            }
            names.Add(unique);
        }
        return names;
    }

    /// <summary>A coordinate: "12.5", "-3", or "12,5" with a decimal comma; null for blank or anything else.</summary>
    private static double? Number(string text)
    {
        if (text.Length == 0)
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value;
        if (text.Count(c => c == ',') == 1 && !text.Contains('.', StringComparison.Ordinal)
            && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
            return value;
        return null;
    }

    // ---- Writing ----

    /// <summary>Writes the dataset (in longitude / latitude) as CSV.</summary>
    public static void Write(GeoDataset dataset, string path, CancellationToken cancellationToken)
    {
        var columns = dataset.Columns();
        var geometries = dataset.Features.Select(f => f.Geometry).OfType<GeoGeometry>().ToList();
        // An attribute that already is a longitude or latitude column would be mistaken for the geometry when the CSV is
        // read again, so such a dataset gets its geometry as WKT.
        var ambiguous = columns.Any(c => LonNames.Contains(Key(c)) || LatNames.Contains(Key(c)));
        var points = !ambiguous && geometries.Count > 0 && geometries.All(g => g is GeoPoint);
        var withHeight = points && geometries.Any(g => g.HasZ);

        string[] geometryColumns = !points ? ["wkt"] : withHeight ? ["longitude", "latitude", "elevation"] : ["longitude", "latitude"];
        // An attribute that already has one of these names keeps it; the geometry column gets "_geo" after it.
        var taken = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var geometryNames = geometryColumns.Select(name =>
        {
            var unique = name;
            while (!taken.Add(unique))
                unique += "_geo";
            return unique;
        }).ToList();

        List<List<string>> rows = [[.. geometryNames, .. columns]];
        foreach (var feature in dataset.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new List<string>(geometryNames.Count + columns.Count);
            if (points)
            {
                if (feature.Geometry is GeoPoint point)
                {
                    row.Add(GeoText.Degrees(point.Position.X));
                    row.Add(GeoText.Degrees(point.Position.Y));
                    if (withHeight)
                        row.Add(point.Position.HasZ ? GeoText.Height(point.Position.Z) : "");
                }
                else
                {
                    row.AddRange(Enumerable.Repeat("", geometryNames.Count));
                }
            }
            else
            {
                row.Add(feature.Geometry is { } geometry ? WktFormat.Write(geometry) : "");
            }
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in feature.Properties)
                values.TryAdd(key, value);
            foreach (var column in columns)
                row.Add(values.TryGetValue(column, out var value) ? GeoText.ToText(value) : "");
            rows.Add(row);
        }
        CsvFormat.WriteRows(rows, path);
    }
}
