// Inputs a review of the geospatial engine fed it: geometry nested far deeper than any real file, a prime meridian the
// projection ignored, tens of thousands of rings in one record, a ZIP that claims to hold less than it does, a huge
// attribute table next to a few shapes, and the odd details of dBASE, .cpg, .prj and GeoJSON numbers. Each of these once
// crashed the process, took minutes, used gigabytes or gave a wrong answer.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class GeoHostileInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static GeoHostileInputTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Wgs84Prj = """GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""";

    private static string Nested(string tag, int levels, string inner) =>
        string.Concat(Enumerable.Repeat($"<{tag}>", levels)) + inner + string.Concat(Enumerable.Repeat($"</{tag}>", levels));

    // ---- Nesting ----

    [Theory]
    [InlineData(40)]
    [InlineData(5_000)]
    [InlineData(100_000)]
    public async Task KML_geometry_nested_beyond_the_limit_is_refused_not_a_stack_overflow(int levels)
    {
        var kml = "<kml><Placemark>" + Nested("MultiGeometry", levels, "<Point><coordinates>1,2</coordinates></Point>") + "</Placemark></kml>";
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "deep.kml", kml), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("levels deep", detail);
    }

    [Fact]
    public async Task KML_geometry_nested_within_the_limit_is_read()
    {
        var kml = "<kml><Placemark>" + Nested("MultiGeometry", 30, "<Point><coordinates>1,2</coordinates></Point>") + "</Placemark></kml>";
        var output = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "ok.kml", kml), "geojson")).Single();
        using var doc = GeoFixtures.ReadJson(output);
        Assert.Equal([1.0, 2.0], GeoFixtures.Position(doc.RootElement.GetProperty("features")[0].GetProperty("geometry")));
    }

    [Fact]
    public void A_file_of_countless_tiny_elements_is_refused_before_it_fills_the_memory()
    {
        // 40 million characters of "<a/>" in a KMZ of 150 KB took 1.1 GB: the number of elements is limited.
        var xml = "<kml><Placemark>" + string.Concat(Enumerable.Repeat("<a/>", 1000)) + "</Placemark></kml>";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            Assert.Equal("The file has more than 100 elements.", Assert.Throws<InvalidDataException>(() => GeoXml.Load(stream, maxElements: 100)).Message);
        using var again = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.NotNull(GeoXml.Load(again)); // within the real limit it loads
        Assert.Equal(3_000_000, GeoXml.MaxElements);
    }

    [Fact]
    public async Task Folders_nested_within_the_limit_are_read()
    {
        var kml = "<kml>" + Nested("Folder", 200, "<Placemark><Point><coordinates>3,4</coordinates></Point></Placemark>") + "</kml>";
        var output = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "folders.kml", kml), "geojson")).Single();
        using var doc = GeoFixtures.ReadJson(output);
        Assert.Equal(1, doc.RootElement.GetProperty("features").GetArrayLength());
    }

    [Theory]
    [InlineData("kml", "<kml>", "<Folder>", "</Folder>", "</kml>")]
    [InlineData("gpx", """<gpx><wpt lat="1" lon="2"><extensions>""", "<x>", "</x>", "</extensions></wpt></gpx>")]
    public async Task Elements_nested_very_deep_in_any_part_of_the_file_are_refused_at_once(string format, string head, string open, string close, string tail)
    {
        // XDocument.Load needs about 48 seconds for a file like this and a recursive walk could end the process: the
        // engine stops reading at 256 levels, which the message proves (a slow regression would still pass this test,
        // but could not give this message).
        var text = head + string.Concat(Enumerable.Repeat(open, 100_000)) + string.Concat(Enumerable.Repeat(close, 100_000)) + tail;
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "deep." + format, text), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("256 levels deep", detail);
    }

    [Fact]
    public async Task Namespaces_prefixes_and_attributes_still_read_as_before_with_the_tree_built_by_the_engine()
    {
        const string gpx = """<?xml version="1.0"?><g:gpx xmlns:g="http://www.topografix.com/GPX/1/1" xmlns:x="urn:x" x:flag="1"><g:wpt lat="47.5" lon="8.25"><g:name>A &amp; B</g:name><!-- comment --><g:ele><![CDATA[412.5]]></g:ele></g:wpt></g:gpx>""";
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "prefixed.gpx", gpx), "geojson")).Single());
        var feature = doc.RootElement.GetProperty("features")[0];
        Assert.Equal([8.25, 47.5, 412.5], GeoFixtures.Position(feature.GetProperty("geometry")));
        Assert.Equal("A & B", feature.GetProperty("properties").GetProperty("name").GetString());
    }

    // ---- Quadratic and wide inputs ----

    [Fact]
    public async Task A_csv_header_of_a_hundred_thousand_identical_names_gets_unique_names_at_once()
    {
        var header = "lat,lon," + string.Join(',', Enumerable.Repeat("a", 100_000));
        var row = "1,2," + string.Join(',', Enumerable.Repeat("x", 100_000));
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "wide.csv", header + "\n" + row + "\n"), "geojson")).Single());
        var properties = doc.RootElement.GetProperty("features")[0].GetProperty("properties");
        Assert.Equal(100_000, properties.EnumerateObject().Count());
        Assert.True(properties.TryGetProperty("a", out _));
        Assert.True(properties.TryGetProperty("a_2", out _));
        Assert.True(properties.TryGetProperty("a_100000", out _));
    }

    [Fact]
    public async Task A_wide_csv_header_over_many_short_rows_does_not_make_rows_times_columns_attributes()
    {
        // 20,000 columns and 20,000 rows of two cells: 400 million empty attributes if each row were padded to the header.
        var text = new StringBuilder("lat,lon," + string.Join(',', Enumerable.Range(0, 20_000).Select(i => "c" + i)) + "\n");
        for (var i = 0; i < 20_000; i++)
            text.Append("1,2\n");
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "short.csv", text.ToString()), "geojson")).Single());
        Assert.Equal(20_000, doc.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task A_long_run_of_white_space_in_a_kml_coordinates_element_is_read_in_linear_time()
    {
        var spaces = new string(' ', 2_000_000);
        // Three positions separated by long runs of spaces, the last written "2, 2"; and a point with spaces around its comma.
        var kml = $"<kml><Placemark><LineString><coordinates>0,0{spaces}1,1{spaces}2, 2</coordinates></LineString></Placemark>"
                  + $"<Placemark><Point><coordinates>5{spaces},{spaces}6</coordinates></Point></Placemark></kml>";
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "spaces.kml", kml), "geojson")).Single());
        var features = doc.RootElement.GetProperty("features");
        var line = features[0].GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(3, line.GetArrayLength());
        Assert.Equal([2.0, 2.0], [line[2][0].GetDouble(), line[2][1].GetDouble()]);
        Assert.Equal([5.0, 6.0], GeoFixtures.Position(features[1].GetProperty("geometry")));
    }

    // ---- Prime meridian ----

    [Fact]
    public async Task A_shapefile_in_NTF_Paris_Lambert_is_refused_instead_of_landing_170_km_west()
    {
        const string paris = """PROJCS["NTF_Paris_Lambert_Zone_II",GEOGCS["GCS_NTF_Paris",DATUM["Nouvelle_Triangulation_Francaise_(Paris)",SPHEROID["Clarke_1880_IGN",6378249.2,293.466021293627]],PRIMEM["Paris",2.33722917],UNIT["Grad",0.0157079632679489]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",600000.0],PARAMETER["False_Northing",2200000.0],PARAMETER["Central_Meridian",0.0],PARAMETER["Standard_Parallel_1",52.0],PARAMETER["Scale_Factor",0.99987742],PARAMETER["Latitude_Of_Origin",52.0],UNIT["Meter",1.0]]""";
        var shp = new ShapefileBuilder(1).Point(600000, 2200000).Save(fx.NewFolder(), "paris", paris);
        var (key, detail) = await fx.ConvertFailsAsync(shp, "geojson");
        Assert.Equal("error.geo_unsupported_crs", key);
        Assert.Contains("prime meridian", detail);
    }

    // ---- Rings ----

    /// <summary>Squares on a grid, each with a hole inside: all the outer rings first, then all the holes.</summary>
    private static (double X, double Y)[][] SquaresWithHoles(int count)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        var outers = new List<(double, double)[]>();
        var holes = new List<(double, double)[]>();
        for (var i = 0; i < count; i++)
        {
            double x = i % side * 10, y = i / side * 10;
            outers.Add([(x, y), (x, y + 8), (x + 8, y + 8), (x + 8, y), (x, y)]); // clockwise
            holes.Add([(x + 2, y + 2), (x + 6, y + 2), (x + 6, y + 6), (x + 2, y + 6), (x + 2, y + 2)]); // counter-clockwise
        }
        return [.. outers, .. holes];
    }

    [Fact]
    public void A_record_of_twenty_thousand_rings_is_sorted_into_polygons_and_holes_without_crawling()
    {
        const int count = 10_000;
        var shp = new ShapefileBuilder(5).Parts(5, SquaresWithHoles(count)).Save(fx.NewFolder(), "rings", Wgs84Prj);
        // Before the fix this took 79 s for a file of this size; a timeout of a minute keeps a slow machine honest
        // without asserting a speed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var dataset = ShapefileFormat.Read(shp, timeout.Token);

        var polygons = Assert.IsType<GeoMultiPolygon>(Assert.Single(dataset.Features).Geometry).Polygons;
        Assert.Equal(count, polygons.Count);
        Assert.All(polygons, p => Assert.Equal(2, p.Rings.Count)); // each outer ring with the one hole inside it
        Assert.Equal(1, polygons[0].Rings[1][0].X - polygons[0].Rings[0][0].X - 1); // the hole belongs to the first square
        var last = polygons[^1];
        Assert.True(last.Rings[1][0].X > last.Rings[0].Min(p => p.X) && last.Rings[1][0].X < last.Rings[0].Max(p => p.X));
    }

    [Fact]
    public void Sorting_the_rings_of_a_record_honours_the_cancellation_token()
    {
        var rings = SquaresWithHoles(500).Select(r => r.Select(p => new Coord(p.X, p.Y)).ToList()).ToList();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ShapefileFormat.Polygons(rings, cancelled.Token));
        Assert.NotNull(ShapefileFormat.Polygons(rings, CancellationToken.None));
    }

    // ---- ZIP bombs ----

    [Fact]
    public void A_kmz_whose_kml_is_declared_larger_than_the_limit_is_refused_before_it_is_unpacked()
    {
        var path = Path.Combine(fx.NewFolder(), "big.kmz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("doc.kml").Open()))
            writer.Write(GeoFormatTests.KmlSample);
        var error = Assert.Throws<InvalidDataException>(() => KmlFormat.Read(path, zipped: true, CancellationToken.None, maxKmlBytes: 100));
        Assert.Contains("too large", error.Message);
        Assert.NotNull(KmlFormat.Read(path, zipped: true, CancellationToken.None)); // within the real limit it reads
    }

    [Fact]
    public void A_stream_stops_at_its_limit_however_much_is_behind_it()
    {
        // What counts when a ZIP header understates the unpacked size: the bytes really read.
        using (var exact = new LimitedStream(new MemoryStream(new byte[100]), 100, "limit"))
            Assert.Equal(100, ReadAll(exact));
        using var over = new LimitedStream(new MemoryStream(new byte[1000]), 100, "limit reached");
        Assert.Equal("limit reached", Assert.Throws<InvalidDataException>(() => ReadAll(over)).Message);
    }

    private static int ReadAll(Stream stream)
    {
        var total = 0;
        var buffer = new byte[16];
        int read;
        while ((read = stream.Read(buffer)) > 0)
            total += read;
        return total;
    }

    // ---- dBASE ----

    [Fact]
    public async Task A_big_attribute_table_next_to_a_few_shapes_is_read_only_as_far_as_the_shapes()
    {
        var dbf = new DbfBuilder().Field("ID", 'N', 8).Field("NAME", 'C', 30);
        for (var i = 0; i < 50_000; i++)
            dbf.Row(i.ToString(), "row " + i);
        var folder = fx.NewFolder();
        var shp = new ShapefileBuilder(1).Point(1, 1).Save(folder, "one", Wgs84Prj, dbf);

        var table = DbfFormat.Read(Path.ChangeExtension(shp, "dbf"), null, 1, CancellationToken.None);
        Assert.Single(table.Rows);
        Assert.Empty(DbfFormat.Read(Path.ChangeExtension(shp, "dbf"), null, 0, CancellationToken.None).Rows);
        Assert.Equal(50_000, DbfFormat.Read(Path.ChangeExtension(shp, "dbf"), null, int.MaxValue, CancellationToken.None).Rows.Count);

        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(shp, "geojson")).Single());
        var features = doc.RootElement.GetProperty("features");
        Assert.Equal(1, features.GetArrayLength());
        Assert.Equal("row 0", features[0].GetProperty("properties").GetProperty("NAME").GetString());
    }

    [Fact]
    public void Fields_of_length_zero_of_every_type_are_read_as_empty_not_a_crash()
    {
        var dbf = new DbfBuilder();
        foreach (var type in "CNFLDMIBO+TGY")
            dbf.Field("F" + type, type, 0);
        dbf.Field("REAL", 'C', 4).Row("", "", "", "", "", "", "", "", "", "", "", "", "", "ok").Row();
        var path = Path.Combine(fx.NewFolder(), "zero.dbf");
        dbf.Save(path);

        var table = DbfFormat.Read(path, null, 10, CancellationToken.None);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("ok", table.Rows[0]![^1]);
        Assert.All(table.Rows[0]!.Take(table.Fields.Count - 1), value => Assert.True(value is null or ""));
    }

    [Theory]
    [InlineData("CP949")]
    [InlineData("cp949")]
    [InlineData("UHC")]
    [InlineData("MS949")]
    [InlineData("windows-949")]
    [InlineData("KS_C_5601-1987")]
    public async Task Korean_code_page_names_in_a_cpg_are_understood(string cpg)
    {
        const string text = "서울특별시 종로구";
        var dbf = new DbfBuilder { Encoding = Encoding.GetEncoding(949) }.Field("NAME", 'C', 40).Row(text);
        var folder = fx.NewFolder();
        var shp = new ShapefileBuilder(1).Point(1, 1).Save(folder, "kr", Wgs84Prj, dbf);
        File.WriteAllText(Path.Combine(folder, "kr.cpg"), cpg);
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(shp, "geojson")).Single());
        Assert.Equal(text, doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("NAME").GetString());
    }

    // ---- .prj ----

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    [InlineData("﻿")]
    public async Task An_empty_prj_counts_as_no_prj(string prj)
    {
        // Degrees convert as they are...
        var folder = fx.NewFolder();
        var shp = new ShapefileBuilder(1).Point(16.37, 48.2).Save(folder, "empty", prj);
        using (var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(shp, "geojson")).Single()))
            Assert.Equal([16.37, 48.2], GeoFixtures.Position(doc.RootElement.GetProperty("features")[0].GetProperty("geometry")));
        // ...and metres, with nothing to say what they are, are refused as for a missing .prj (not as an unsupported system).
        var projected = new ShapefileBuilder(1).Point(602065.2, 5340353.6).Save(fx.NewFolder(), "metres", prj);
        var (key, _) = await fx.ConvertFailsAsync(projected, "geojson");
        Assert.Equal("error.geo_unknown_crs", key);
    }

    // ---- GeoJSON numbers ----

    [Fact]
    public async Task GeoJson_numbers_beyond_the_double_range_are_no_height_no_position_and_text_in_attributes()
    {
        var height = """{"type":"Feature","properties":{"big":1e400,"n":1},"geometry":{"type":"Point","coordinates":[1,2,1e400]}}""";
        using (var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "z.geojson", height), "geojson")).Single()))
        {
            var feature = doc.RootElement.GetProperty("features")[0];
            Assert.Equal([1.0, 2.0], GeoFixtures.Position(feature.GetProperty("geometry")));
            Assert.Equal("1e400", feature.GetProperty("properties").GetProperty("big").GetString());
            Assert.Equal(1, feature.GetProperty("properties").GetProperty("n").GetInt64());
        }
        var position = """{"type":"Point","coordinates":[1e400,2]}""";
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "x.geojson", position), "kml");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("too large", detail);
    }
}
