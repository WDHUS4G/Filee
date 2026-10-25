// Helpers for the geospatial tests: reference files made by GDAL (tests/Filee.Engines.Tests/Geo), and a small shapefile
// and dBASE writer built straight from the ESRI specification, independent of the engine, for the cases GDAL won't
// write on purpose (a wrong .prj, a truncated record, MultiPatch, deleted rows, other encodings).

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

internal static class GeoFixtures
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Geo");

    /// <summary>Copies every file of the reference set <paramref name="name"/> into a fresh folder; returns the path of its <paramref name="extension"/> file.</summary>
    public static string Copy(EngineFixture fx, string name, string extension)
    {
        var folder = fx.NewFolder();
        foreach (var file in Directory.EnumerateFiles(Folder, name + ".*"))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return Path.Combine(folder, name + "." + extension);
    }

    /// <summary>Two points, A (10.5, 20.25) and B (-5, 40), written the way GDAL writes them (see Geo/make-test-geo.py for the Shapefiles).</summary>
    internal const string PointsGeoJson = """
        {
        "type": "FeatureCollection",
        "name": "points",
        "crs": { "type": "name", "properties": { "name": "urn:ogc:def:crs:OGC:1.3:CRS84" } },
        "features": [
        { "type": "Feature", "properties": { "name": "A", "n": 1 }, "geometry": { "type": "Point", "coordinates": [ 10.5, 20.25 ] } },
        { "type": "Feature", "properties": { "name": "B", "n": 2 }, "geometry": { "type": "Point", "coordinates": [ -5.0, 40.0 ] } }
        ]
        }
        """;

    internal const string PointsKml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <kml xmlns="http://www.opengis.net/kml/2.2">
        <Document id="root_doc">
        <Folder><name>points</name>
          <Placemark>
        	<name>A</name>
        	<description>first</description>
              <Point><coordinates>10.5,20.25</coordinates></Point>
          </Placemark>
          <Placemark>
        	<name>B</name>
        	<description>second</description>
              <Point><coordinates>-5,40</coordinates></Point>
          </Placemark>
        </Folder>
        </Document></kml>
        """;

    internal const string PointsGpx = """
        <?xml version="1.0"?>
        <gpx version="1.1" creator="GDAL 3.9.1" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://www.topografix.com/GPX/1/1" xsi:schemaLocation="http://www.topografix.com/GPX/1/1 http://www.topografix.com/GPX/1/1/gpx.xsd">
        <metadata>
        <bounds minlat="20.250000000000000" minlon="-5.000000000000000" maxlat="40.000000000000000" maxlon="10.500000000000000"/>
        </metadata>
        <wpt lat="20.25" lon="10.5">
          <ele>12.5</ele>
          <name>A</name>
        </wpt>
        <wpt lat="40.0" lon="-5.0">
          <ele>0.0</ele>
          <name>B</name>
        </wpt>
        </gpx>
        """;

    /// <summary>The two points as a file of <paramref name="extension"/> (geojson, kml or gpx) named points.&lt;extension&gt; in a fresh folder.</summary>
    public static string Points(EngineFixture fx, string extension) => WriteText(fx, "points." + extension, extension switch
    {
        "geojson" => PointsGeoJson,
        "kml" => PointsKml,
        "gpx" => PointsGpx,
        _ => throw new ArgumentOutOfRangeException(nameof(extension)),
    });

    /// <summary>Converts <paramref name="input"/> to <paramref name="target"/> as the app would.</summary>
    public static async Task<ConversionJob> ConvertAsync(this EngineFixture fx, string input, string target, Action<Preset>? tweak = null)
    {
        var preset = new Preset { TargetFormat = target };
        tweak?.Invoke(preset);
        return await fx.ConvertAsync([input], preset);
    }

    /// <summary>Converts and expects success; returns the files written.</summary>
    public static async Task<List<string>> ConvertOkAsync(this EngineFixture fx, string input, string target, Action<Preset>? tweak = null)
    {
        var job = await fx.ConvertAsync(input, target, tweak);
        Assert.True(job.State == JobState.Completed, Describe(job));
        return [.. job.Outputs];
    }

    /// <summary>Converts and expects the file to fail; returns its error key and detail.</summary>
    public static async Task<(string Key, string Detail)> ConvertFailsAsync(this EngineFixture fx, string input, string target)
    {
        var job = await fx.ConvertAsync(input, target);
        Assert.Equal(JobState.Failed, job.State);
        var file = job.Files.Single();
        Assert.Empty(file.Outputs);
        return (file.ErrorKey ?? "", file.ErrorDetail ?? "");
    }

    public static string Describe(ConversionJob job) => string.Join("; ", job.Files.Select(f => $"{f.State} {f.ErrorKey} {f.ErrorDetail}"));

    public static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllBytes(path));

    public static string WriteText(EngineFixture fx, string name, string text)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>The coordinates [x, y(, z)] of a GeoJSON point geometry.</summary>
    public static double[] Position(JsonElement geometry) =>
        [.. geometry.GetProperty("coordinates").EnumerateArray().Select(n => n.GetDouble())];
}

/// <summary>A shapefile written from the specification: header, records, index and attribute table.</summary>
internal sealed class ShapefileBuilder(int shapeType)
{
    private readonly List<byte[]> _records = [];

    public int ShapeType { get; set; } = shapeType;

    public ShapefileBuilder Null() => Add(Le(0));

    public ShapefileBuilder Point(double x, double y) => Add([.. Le(1), .. Dbl(x), .. Dbl(y)]);

    public ShapefileBuilder PointZ(double x, double y, double z) => Add([.. Le(11), .. Dbl(x), .. Dbl(y), .. Dbl(z), .. Dbl(-1e39)]);

    public ShapefileBuilder MultiPoint(params (double X, double Y)[] points) =>
        Add([.. Le(8), .. Box(points), .. Le(points.Length), .. points.SelectMany(p => Dbl(p.X).Concat(Dbl(p.Y)))]);

    /// <summary>A PolyLine (type 3) or Polygon (type 5) record from its parts; the caller sets the ring direction.</summary>
    public ShapefileBuilder Parts(int type, params (double X, double Y)[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        var starts = new int[parts.Length];
        for (var i = 1; i < parts.Length; i++)
            starts[i] = starts[i - 1] + parts[i - 1].Length;
        return Add([.. Le(type), .. Box(all), .. Le(parts.Length), .. Le(all.Length), .. starts.SelectMany(Le),
                    .. all.SelectMany(p => Dbl(p.X).Concat(Dbl(p.Y)))]);
    }

    /// <summary>A record exactly as given (to build damaged files).</summary>
    public ShapefileBuilder Raw(byte[] content) => Add(content);

    private ShapefileBuilder Add(byte[] content)
    {
        _records.Add(content);
        return this;
    }

    /// <summary>Writes name.shp and name.shx; the .prj when <paramref name="prj"/> is given; the .dbf when <paramref name="dbf"/> is.</summary>
    public string Save(string folder, string name, string? prj = null, DbfBuilder? dbf = null)
    {
        var shpPath = Path.Combine(folder, name + ".shp");
        var shp = new List<byte>();
        var shx = new List<byte>();
        foreach (var (content, index) in _records.Select((c, i) => (c, i)))
        {
            shx.AddRange(Be((100 + shp.Count) / 2));
            shx.AddRange(Be(content.Length / 2));
            shp.AddRange(Be(index + 1));
            shp.AddRange(Be(content.Length / 2));
            shp.AddRange(content);
        }
        File.WriteAllBytes(shpPath, [.. Header(100 + shp.Count), .. shp]);
        File.WriteAllBytes(Path.Combine(folder, name + ".shx"), [.. Header(100 + shx.Count), .. shx]);
        if (prj is not null)
            File.WriteAllText(Path.Combine(folder, name + ".prj"), prj);
        dbf?.Save(Path.Combine(folder, name + ".dbf"));
        return shpPath;
    }

    private byte[] Header(int bytes)
    {
        var header = new byte[100];
        BinaryPrimitives.WriteInt32BigEndian(header, 9994);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), bytes / 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), ShapeType);
        return header;
    }

    private static byte[] Box(IEnumerable<(double X, double Y)> points)
    {
        var list = points.ToList();
        return [.. Dbl(list.Min(p => p.X)), .. Dbl(list.Min(p => p.Y)), .. Dbl(list.Max(p => p.X)), .. Dbl(list.Max(p => p.Y))];
    }

    public static byte[] Le(int value) => BitConverter.GetBytes(value);

    public static byte[] Be(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] Dbl(double value) => BitConverter.GetBytes(value);
}

/// <summary>A dBASE III table from fields and the exact text of each cell.</summary>
internal sealed class DbfBuilder
{
    private readonly List<(string Name, char Type, int Length, int Decimals)> _fields = [];
    private readonly List<(bool Deleted, string[] Cells)> _rows = [];

    public byte LanguageDriver { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);

    public DbfBuilder Field(string name, char type, int length, int decimals = 0)
    {
        _fields.Add((name, type, length, decimals));
        return this;
    }

    public DbfBuilder Row(params string[] cells) => Row(false, cells);

    public DbfBuilder Row(bool deleted, params string[] cells)
    {
        _rows.Add((deleted, cells));
        return this;
    }

    public void Save(string path)
    {
        var recordLength = 1 + _fields.Sum(f => f.Length);
        var headerLength = 32 + 32 * _fields.Count + 1;
        var bytes = new List<byte>();
        var header = new byte[32];
        header[0] = 3;
        header[1] = 124;
        header[2] = 1;
        header[3] = 1;
        BitConverter.GetBytes(_rows.Count).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)headerLength).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)recordLength).CopyTo(header, 10);
        header[29] = LanguageDriver;
        bytes.AddRange(header);
        foreach (var (name, type, length, decimals) in _fields)
        {
            var descriptor = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(descriptor, 0);
            descriptor[11] = (byte)type;
            descriptor[16] = (byte)length;
            descriptor[17] = (byte)decimals;
            bytes.AddRange(descriptor);
        }
        bytes.Add(0x0D);
        foreach (var (deleted, cells) in _rows)
        {
            bytes.Add(deleted ? (byte)'*' : (byte)' ');
            for (var f = 0; f < _fields.Count; f++)
            {
                var cell = new byte[_fields[f].Length];
                Array.Fill(cell, (byte)' ');
                var text = Encoding.GetBytes(f < cells.Length ? cells[f] : "");
                if (_fields[f].Type is 'N' or 'F')
                    text.CopyTo(cell, Math.Max(0, cell.Length - text.Length));
                else
                    text.AsSpan(0, Math.Min(text.Length, cell.Length)).CopyTo(cell);
                bytes.AddRange(cell);
            }
        }
        bytes.Add(0x1A);
        File.WriteAllBytes(path, [.. bytes]);
    }
}
