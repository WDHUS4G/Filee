// GeoJSON, KML, KMZ, GPX and CSV through the geospatial engine: reference files written by GDAL, hand-written files with
// the shapes real files take (folders, ExtendedData, MultiGeometry, tracks, decimal commas, WKT columns), every format
// into every other, the notes a conversion leaves, and the errors for files that can't be converted.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Core.Conversion;

namespace Filee.Engines.Tests;

public class GeoFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static GeoFormatTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static JsonElement Features(JsonDocument doc) => doc.RootElement.GetProperty("features");

    private static double SignedArea(JsonElement ring)
    {
        var points = ring.EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToList();
        var sum = 0.0;
        for (var i = 0; i + 1 < points.Count; i++)
            sum += points[i].X * points[i + 1].Y - points[i + 1].X * points[i].Y;
        return sum;
    }

    private async Task<JsonDocument> ToGeoJsonAsync(string input)
    {
        var output = (await fx.ConvertOkAsync(input, "geojson")).Single();
        return GeoFixtures.ReadJson(output);
    }

    // ---- GDAL reference files ----

    [Fact]
    public async Task Gdal_shapefile_points_keep_geometry_text_numbers_booleans_and_dates()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.Copy(fx, "gdal_points", "shp"));
        var features = Features(doc);
        Assert.Equal("FeatureCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, features.GetArrayLength());

        var first = features[0];
        Assert.Equal([126.978, 37.5665], GeoFixtures.Position(first.GetProperty("geometry")));
        var properties = first.GetProperty("properties");
        Assert.Equal("서울 City Hall", properties.GetProperty("name").GetString());
        Assert.Equal(1, properties.GetProperty("n").GetInt64());
        Assert.Equal(1.5, properties.GetProperty("v").GetDouble());
        Assert.True(properties.GetProperty("ok").GetBoolean());
        Assert.Equal("2024-02-29", properties.GetProperty("day").GetString());

        var second = features[1].GetProperty("properties");
        Assert.Equal("Café Eiffel – Paris", second.GetProperty("name").GetString());
        Assert.Equal(-20, second.GetProperty("n").GetInt64());
        Assert.Equal(0.001, second.GetProperty("v").GetDouble());
        Assert.False(second.GetProperty("ok").GetBoolean());

        var third = features[2].GetProperty("properties");
        Assert.Equal("北京 / London", third.GetProperty("name").GetString());
        Assert.Equal(-12345.6789, third.GetProperty("v").GetDouble());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("day").ValueKind);
    }

    [Fact]
    public async Task Gdal_shapefile_polygons_get_their_holes_and_parts_in_geojson_winding()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.Copy(fx, "gdal_polygons", "shp"));
        var features = Features(doc);

        var withHole = features[0].GetProperty("geometry");
        Assert.Equal("Polygon", withHole.GetProperty("type").GetString());
        var rings = withHole.GetProperty("coordinates");
        Assert.Equal(2, rings.GetArrayLength());
        Assert.True(SignedArea(rings[0]) > 0, "RFC 7946: the outer ring runs counter-clockwise");
        Assert.True(SignedArea(rings[1]) < 0, "RFC 7946: holes run clockwise");
        Assert.Equal(100 - 4, SignedArea(rings[0]) / 2 + SignedArea(rings[1]) / 2);

        var twoParts = features[1].GetProperty("geometry");
        Assert.Equal("MultiPolygon", twoParts.GetProperty("type").GetString());
        Assert.Equal(2, twoParts.GetProperty("coordinates").GetArrayLength());
        Assert.Equal("two parts", features[1].GetProperty("properties").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Gdal_utm_shapefile_is_converted_to_longitude_latitude_using_its_prj()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.Copy(fx, "gdal_utm33", "shp"));
        var expected = new[] { ("Vienna", 16.3738, 48.2082), ("Prague", 14.4378, 50.0755), ("Berlin", 13.4050, 52.5200) };
        var features = Features(doc);
        for (var i = 0; i < expected.Length; i++)
        {
            var position = GeoFixtures.Position(features[i].GetProperty("geometry"));
            Assert.Equal(expected[i].Item1, features[i].GetProperty("properties").GetProperty("name").GetString());
            Assert.Equal(expected[i].Item2, position[0], 8);
            Assert.Equal(expected[i].Item3, position[1], 8);
        }
    }

    [Fact]
    public async Task Gdal_3d_lines_keep_their_heights_in_every_format()
    {
        var shp = GeoFixtures.Copy(fx, "gdal_linesz", "shp");
        using (var doc = await ToGeoJsonAsync(shp))
        {
            var line = Features(doc)[0].GetProperty("geometry");
            Assert.Equal("LineString", line.GetProperty("type").GetString());
            Assert.Equal([[0.0, 0.0, 10.0], [1.0, 1.0, 20.5], [2.0, 0.0, 30.0]],
                line.GetProperty("coordinates").EnumerateArray().Select(p => p.EnumerateArray().Select(n => n.GetDouble()).ToArray()).ToArray());
        }
        var kml = (await fx.ConvertOkAsync(shp, "kml")).Single();
        Assert.Contains("0,0,10 1,1,20.5 2,0,30", File.ReadAllText(kml));
        var gpx = (await fx.ConvertOkAsync(shp, "gpx")).Single();
        Assert.Equal(["10", "20.5", "30"], XDocument.Load(gpx).Descendants().Where(e => e.Name.LocalName == "ele").Select(e => e.Value).ToArray());
        var csv = (await fx.ConvertOkAsync(shp, "csv")).Single();
        Assert.Contains("LINESTRING Z (0 0 10, 1 1 20.5, 2 0 30)", File.ReadAllText(csv));
    }

    [Fact]
    public async Task Kml_name_and_description_become_attributes()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.Points(fx, "kml"));
        var features = Features(doc);
        Assert.Equal(2, features.GetArrayLength());
        Assert.Equal([10.5, 20.25], GeoFixtures.Position(features[0].GetProperty("geometry")));
        Assert.Equal("A", features[0].GetProperty("properties").GetProperty("name").GetString());
        Assert.Equal("second", features[1].GetProperty("properties").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Gpx_waypoint_elevation_becomes_the_height()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.Points(fx, "gpx"));
        var features = Features(doc);
        Assert.Equal(2, features.GetArrayLength());
        Assert.Equal([10.5, 20.25, 12.5], GeoFixtures.Position(features[0].GetProperty("geometry")));
        Assert.Equal("waypoint", features[0].GetProperty("properties").GetProperty("gpx_type").GetString());
        Assert.Equal("B", features[1].GetProperty("properties").GetProperty("name").GetString());
    }

    [Fact]
    public async Task GeoJson_converts_to_the_same_geojson_rewritten_one_feature_per_line()
    {
        var output = (await fx.ConvertOkAsync(GeoFixtures.Points(fx, "geojson"), "geojson")).Single();
        var lines = File.ReadAllLines(output);
        Assert.Equal(4, lines.Length); // the opening, one line for each of the two features, the closing
        Assert.StartsWith("""{"type":"Feature",""", lines[1]);
        using var doc = GeoFixtures.ReadJson(output);
        Assert.Equal(2, Features(doc).GetArrayLength());
    }

    // ---- Every format into every other ----

    [Fact]
    public async Task Every_format_converts_into_every_other_and_back_to_the_same_places_and_names()
    {
        var formats = new[] { "kml", "kmz", "gpx", "shp", "csv", "geojson" };
        var source = GeoFixtures.Points(fx, "geojson");
        var made = new Dictionary<string, string> { ["geojson"] = source };
        foreach (var format in formats.Where(f => f != "geojson"))
            made[format] = (await fx.ConvertOkAsync(source, format)).First(path => path.EndsWith("." + format, StringComparison.Ordinal));

        foreach (var from in formats)
        {
            foreach (var to in formats.Where(t => t != from))
            {
                var step = (await fx.ConvertOkAsync(made[from], to)).First(path => path.EndsWith("." + to, StringComparison.Ordinal));
                var final = to == "geojson" ? step : (await fx.ConvertOkAsync(step, "geojson")).Single();
                using var doc = GeoFixtures.ReadJson(final);
                var features = Features(doc);
                Assert.True(features.GetArrayLength() == 2, $"{from} -> {to}: {features.GetArrayLength()} features");
                Assert.Equal([10.5, 20.25], GeoFixtures.Position(features[0].GetProperty("geometry"))[..2]);
                Assert.Equal([-5.0, 40.0], GeoFixtures.Position(features[1].GetProperty("geometry"))[..2]);
                Assert.True(features[0].GetProperty("properties").GetProperty("name").GetString() == "A", $"{from} -> {to} keeps the name");
            }
        }
    }

    [Fact]
    public async Task A_shapefile_reaches_json_xlsx_and_text_formats_through_csv()
    {
        var shp = GeoFixtures.Copy(fx, "gdal_points", "shp");
        foreach (var target in new[] { "xlsx", "json", "yaml" })
        {
            var output = (await fx.ConvertOkAsync(shp, target)).Single();
            Assert.True(new FileInfo(output).Length > 100, target);
        }
        using var rows = GeoFixtures.ReadJson((await fx.ConvertOkAsync(shp, "json")).Single());
        Assert.Equal(126.978, rows.RootElement[0].GetProperty("longitude").GetDouble());
        Assert.Equal("서울 City Hall", rows.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(3, rows.RootElement.GetArrayLength());
    }

    // ---- GeoJSON ----

    [Fact]
    public async Task GeoJson_input_forms_are_all_accepted()
    {
        var featureCollection = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"a":1}}]}""";
        var feature = """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"a":1}}""";
        var bare = """{"type":"Point","coordinates":[1,2]}""";
        foreach (var text in new[] { featureCollection, feature, bare, "\uFEFF" + feature, "// comment\n" + feature + "\n", feature.Replace("}}", "},}") })
        {
            using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "input.geojson", text));
            Assert.Equal(1, Features(doc).GetArrayLength());
            Assert.Equal([1.0, 2.0], GeoFixtures.Position(Features(doc)[0].GetProperty("geometry")));
        }
    }

    [Fact]
    public async Task GeoJson_in_utf16_either_way_round_and_in_an_old_code_page_is_read()
    {
        const string text = """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"n":"유니코드 서울"}}""";
        foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.GetEncoding(949) })
        {
            var path = Path.Combine(fx.NewFolder(), "text.geojson");
            File.WriteAllText(path, text, encoding);
            using var doc = await ToGeoJsonAsync(path);
            Assert.Equal("유니코드 서울", Features(doc)[0].GetProperty("properties").GetProperty("n").GetString());
        }
    }

    [Fact]
    public async Task GeoJson_attributes_keep_their_types_and_nested_values_become_json_text()
    {
        var text = """{"type":"FeatureCollection","features":[{"type":"Feature","id":"abc-1","properties":{"s":"x","i":42,"big":12345678901234567890,"f":1.25,"b":true,"nil":null,"list":[1,2],"obj":{"k":"v"}},"geometry":{"type":"Point","coordinates":[3,4]}}]}""";
        var output = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "types.geojson", text), "geojson")).Single();
        using var doc = GeoFixtures.ReadJson(output);
        var p = Features(doc)[0].GetProperty("properties");
        Assert.Equal("abc-1", p.GetProperty("id").GetString());
        Assert.Equal(42, p.GetProperty("i").GetInt64());
        Assert.Equal(1.25, p.GetProperty("f").GetDouble());
        Assert.True(p.GetProperty("b").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("nil").ValueKind);
        Assert.Equal("[1,2]", p.GetProperty("list").GetString());
        Assert.Equal("{\"k\":\"v\"}", p.GetProperty("obj").GetString());
        Assert.True(p.GetProperty("big").GetDouble() > 1.2e19);
    }

    [Fact]
    public async Task GeoJson_feature_without_geometry_or_with_empty_coordinates_stays_as_a_feature()
    {
        var text = """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"n":1},"geometry":null},{"type":"Feature","properties":{"n":2}},{"type":"Feature","properties":{"n":3},"geometry":{"type":"Point","coordinates":[]}},{"type":"Feature","properties":{"n":4},"geometry":{"type":"LineString","coordinates":[[1,1]]}}]}""";
        using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "nogeom.geojson", text));
        Assert.Equal(4, Features(doc).GetArrayLength());
        Assert.All(Features(doc).EnumerateArray(), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("geometry").ValueKind));
    }

    [Fact]
    public async Task GeoJson_polygon_rings_are_closed_and_wound_by_the_rfc_on_output()
    {
        // Clockwise outer ring, not closed, with a counter-clockwise hole: both are put right.
        var text = """{"type":"Polygon","coordinates":[[[0,0],[0,4],[4,4],[4,0]],[[1,1],[2,1],[2,2],[1,2],[1,1]]]}""";
        using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "ring.geojson", text));
        var rings = Features(doc)[0].GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(5, rings[0].GetArrayLength());
        Assert.True(SignedArea(rings[0]) > 0);
        Assert.True(SignedArea(rings[1]) < 0);
    }

    [Fact]
    public async Task GeoJson_crs_member_is_honoured_for_known_systems_and_refused_for_others()
    {
        var web = """{"type":"FeatureCollection","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::3857"}},"features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[1113194.9079327357,1118889.9748579597]}}]}""";
        using (var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "web.geojson", web)))
        {
            var position = GeoFixtures.Position(Features(doc)[0].GetProperty("geometry"));
            Assert.Equal(10, position[0], 8);
            Assert.Equal(10, position[1], 8);
        }
        var laea = web.Replace("EPSG::3857", "EPSG::3035");
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "laea.geojson", laea), "kml");
        Assert.Equal("error.geo_unsupported_crs", key);
        Assert.Contains("EPSG::3035", detail);
    }

    [Fact]
    public async Task GeoJson_with_projected_coordinates_and_no_crs_is_refused_instead_of_placed_on_the_wrong_spot()
    {
        var text = """{"type":"Point","coordinates":[602065.2,5340353.6]}""";
        var (key, _) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "utm.geojson", text), "kml");
        Assert.Equal("error.geo_unknown_crs", key);
    }

    [Fact]
    public async Task GeoJson_text_with_unicode_and_control_characters_is_written_safely_to_every_text_format()
    {
        var text = "{\"type\":\"Feature\",\"properties\":{\"name\":\"a\\u0001b <&> \\\"q\\\" 한국어\\n2nd\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}}";
        var path = GeoFixtures.WriteText(fx, "ctl.geojson", text);
        var kml = (await fx.ConvertOkAsync(path, "kml")).Single();
        var name = XDocument.Load(kml).Descendants().First(e => e.Name.LocalName == "name" && e.Parent?.Name.LocalName == "Placemark").Value;
        Assert.Equal("ab <&> \"q\" 한국어\n2nd", name);
        var gpx = (await fx.ConvertOkAsync(path, "gpx")).Single();
        Assert.Equal("ab <&> \"q\" 한국어\n2nd", XDocument.Load(gpx).Descendants().First(e => e.Name.LocalName == "name").Value);
        var csv = File.ReadAllText((await fx.ConvertOkAsync(path, "csv")).Single());
        Assert.Contains("\"a\u0001b <&> \"\"q\"\" 한국어\n2nd\"", csv);
    }

    // ---- KML / KMZ ----

    internal const string KmlSample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2">
          <Document>
            <name>Sample</name>
            <Style id="s"><IconStyle><scale>2</scale></IconStyle></Style>
            <Folder>
              <name>Outer</name>
              <Folder>
                <Placemark>
                  <name>Point with data</name>
                  <description><![CDATA[<b>bold</b> text]]></description>
                  <ExtendedData>
                    <Data name="population"><value>1234</value></Data>
                    <Data name="code"><value>007</value></Data>
                    <SchemaData schemaUrl="#x"><SimpleData name="area">12.5</SimpleData></SchemaData>
                  </ExtendedData>
                  <Point><coordinates>10.5, 20.25, 99</coordinates></Point>
                </Placemark>
              </Folder>
              <Placemark><name>Line</name><LineString><coordinates>
                0,0
                1,1,5
                2,0
              </coordinates></LineString></Placemark>
              <Placemark><name>Polygon</name><Polygon>
                <outerBoundaryIs><LinearRing><coordinates>0,0 4,0 4,4 0,4 0,0</coordinates></LinearRing></outerBoundaryIs>
                <innerBoundaryIs><LinearRing><coordinates>1,1 1,2 2,2 2,1 1,1</coordinates></LinearRing></innerBoundaryIs>
              </Polygon></Placemark>
              <Placemark><name>Several points</name><MultiGeometry><Point><coordinates>1,1</coordinates></Point><Point><coordinates>2,2</coordinates></Point></MultiGeometry></Placemark>
              <Placemark><name>Mixed</name><MultiGeometry><Point><coordinates>1,1</coordinates></Point><LineString><coordinates>0,0 3,3</coordinates></LineString></MultiGeometry></Placemark>
              <Placemark><name>Track</name><gx:Track><gx:coord>5 6 7</gx:coord><gx:coord>6 7 8</gx:coord></gx:Track></Placemark>
              <Placemark><name>No shape</name></Placemark>
            </Folder>
            <GroundOverlay><name>Not a feature</name></GroundOverlay>
          </Document>
        </kml>
        """;

    [Fact]
    public async Task Kml_placemarks_are_read_from_nested_folders_with_all_geometry_kinds_and_data()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "sample.kml", KmlSample));
        var features = Features(doc);
        Assert.Equal(7, features.GetArrayLength());

        var point = features[0];
        var p = point.GetProperty("properties");
        Assert.Equal("Point with data", p.GetProperty("name").GetString());
        Assert.Equal("<b>bold</b> text", p.GetProperty("description").GetString());
        Assert.Equal(1234, p.GetProperty("population").GetInt64());
        Assert.Equal("007", p.GetProperty("code").GetString());
        Assert.Equal(12.5, p.GetProperty("area").GetDouble());
        Assert.Equal([10.5, 20.25, 99.0], GeoFixtures.Position(point.GetProperty("geometry")));

        Assert.Equal("LineString", features[1].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(3, features[1].GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        var polygon = features[2].GetProperty("geometry");
        Assert.Equal(2, polygon.GetProperty("coordinates").GetArrayLength());
        Assert.Equal("MultiPoint", features[3].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal("GeometryCollection", features[4].GetProperty("geometry").GetProperty("type").GetString());
        var track = features[5].GetProperty("geometry");
        Assert.Equal("LineString", track.GetProperty("type").GetString());
        Assert.Equal(7, track.GetProperty("coordinates")[0][2].GetDouble());
        Assert.Equal(JsonValueKind.Null, features[6].GetProperty("geometry").ValueKind);
    }

    [Fact]
    public async Task Kml_of_an_older_namespace_without_a_namespace_and_with_odd_coordinates_is_read()
    {
        foreach (var kml in new[]
                 {
                     """<kml xmlns="http://earth.google.com/kml/2.1"><Placemark><Point><coordinates>1,2</coordinates></Point></Placemark></kml>""",
                     """<kml><Placemark><Point><coordinates>1,2</coordinates></Point></Placemark></kml>""",
                     """<kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><Point><coordinates>  1 , 2  </coordinates></Point></Placemark></kml>""",
                 })
        {
            using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "old.kml", kml));
            Assert.Equal([1.0, 2.0], GeoFixtures.Position(Features(doc)[0].GetProperty("geometry")));
        }
    }

    [Fact]
    public async Task Kmz_is_written_as_a_zip_with_doc_kml_and_read_back_whatever_the_kml_is_called()
    {
        var geojson = GeoFixtures.Points(fx, "geojson");
        var kmz = (await fx.ConvertOkAsync(geojson, "kmz")).Single();
        using (var zip = ZipFile.OpenRead(kmz))
            Assert.Equal(["doc.kml"], zip.Entries.Select(e => e.FullName).ToArray());

        // A KMZ made elsewhere: other files first, the KML under another name.
        var odd = Path.Combine(fx.NewFolder(), "odd.kmz");
        using (var zip = ZipFile.Open(odd, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("images/icon.png").Open()))
                w.Write("not really a picture");
            using var kml = new StreamWriter(zip.CreateEntry("files/places.kml").Open());
            kml.Write(KmlSample);
        }
        using var doc = await ToGeoJsonAsync(odd);
        Assert.Equal(7, Features(doc).GetArrayLength());
    }

    [Fact]
    public async Task Kml_output_has_one_placemark_per_feature_with_extended_data_and_valid_xml()
    {
        var kml = (await fx.ConvertOkAsync(GeoFixtures.Copy(fx, "gdal_points", "shp"), "kml")).Single();
        var document = XDocument.Load(kml);
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var placemarks = document.Descendants(ns + "Placemark").ToList();
        Assert.Equal(3, placemarks.Count);
        Assert.Equal("서울 City Hall", placemarks[0].Element(ns + "name")!.Value);
        var data = placemarks[0].Element(ns + "ExtendedData")!.Elements(ns + "Data").ToDictionary(d => (string)d.Attribute("name")!, d => d.Element(ns + "value")!.Value);
        Assert.Equal("1", data["n"]);
        Assert.Equal("true", data["ok"]);
        Assert.Equal("126.978,37.5665", placemarks[0].Descendants(ns + "coordinates").Single().Value);
        Assert.DoesNotContain("name", data.Keys);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<kml><Placemark>")]
    [InlineData("<html><body/></html>")]
    public async Task Kml_that_is_broken_or_not_kml_fails_with_a_reason(string text)
    {
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "bad.kml", text), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.NotEmpty(detail);
    }

    [Fact]
    public async Task Kml_with_a_position_that_is_not_a_number_names_the_text()
    {
        var kml = """<kml><Placemark><Point><coordinates>abc,2</coordinates></Point></Placemark></kml>""";
        var (_, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "nan.kml", kml), "geojson");
        Assert.Contains("abc,2", detail);
    }

    [Fact]
    public async Task A_kmz_that_is_not_a_zip_or_has_no_kml_fails_with_a_reason()
    {
        var (_, notZip) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "fake.kmz", "this is not a zip"), "geojson");
        Assert.Contains("KMZ", notZip);
        var empty = Path.Combine(fx.NewFolder(), "empty.kmz");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        var (_, noKml) = await fx.ConvertFailsAsync(empty, "geojson");
        Assert.Contains("no .kml", noKml);
    }

    // ---- GPX ----

    internal const string GpxSample = """
        <?xml version="1.0"?>
        <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
          <metadata><name>file</name></metadata>
          <wpt lat="47.5" lon="8.25"><ele>412.5</ele><time>2024-05-01T10:00:00Z</time><name>Summit</name><cmt>comment</cmt><desc>Nice view</desc><sym>Flag</sym><type>peak</type></wpt>
          <rte><name>Route 1</name><rtept lat="1" lon="2"/><rtept lat="3" lon="4"/></rte>
          <trk><name>Track 1</name><number>3</number>
            <trkseg><trkpt lat="10" lon="20"><ele>5</ele></trkpt><trkpt lat="11" lon="21"/></trkseg>
            <trkseg><trkpt lat="12" lon="22"/><trkpt lat="13" lon="23"/></trkseg>
          </trk>
          <trk><name>Empty track</name></trk>
        </gpx>
        """;

    [Fact]
    public async Task Gpx_waypoints_routes_and_tracks_become_features()
    {
        using var doc = await ToGeoJsonAsync(GeoFixtures.WriteText(fx, "sample.gpx", GpxSample));
        var features = Features(doc);
        Assert.Equal(4, features.GetArrayLength());
        var wpt = features[0].GetProperty("properties");
        Assert.Equal("Summit", wpt.GetProperty("name").GetString());
        Assert.Equal("peak", wpt.GetProperty("type").GetString());
        Assert.Equal("Flag", wpt.GetProperty("sym").GetString());
        Assert.Equal("2024-05-01T10:00:00Z", wpt.GetProperty("time").GetString());
        Assert.Equal([8.25, 47.5, 412.5], GeoFixtures.Position(features[0].GetProperty("geometry")));
        Assert.Equal("route", features[1].GetProperty("properties").GetProperty("gpx_type").GetString());
        Assert.Equal("LineString", features[1].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(3, features[2].GetProperty("properties").GetProperty("number").GetInt64());
        Assert.Equal("MultiLineString", features[2].GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, features[3].GetProperty("geometry").ValueKind);
    }

    [Fact]
    public async Task Gpx_output_keeps_waypoint_fields_tracks_and_routes_in_schema_order()
    {
        var geojson = await ToGeoJsonPathAsync(GeoFixtures.WriteText(fx, "sample.gpx", GpxSample));
        var document = XDocument.Load((await fx.ConvertOkAsync(geojson, "gpx")).Single());
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        Assert.Equal("1.1", (string)document.Root!.Attribute("version")!);
        var wpt = document.Root.Element(ns + "wpt")!;
        Assert.Equal(["ele", "time", "name", "cmt", "desc", "sym", "type"], wpt.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("2024-05-01T10:00:00Z", wpt.Element(ns + "time")!.Value);
        Assert.Single(document.Root.Elements(ns + "rte"));
        var track = document.Root.Elements(ns + "trk").Single();
        Assert.Equal(2, track.Elements(ns + "trkseg").Count());
        Assert.Equal(["name", "number"], track.Elements().Take(2).Select(e => e.Name.LocalName).ToArray());
    }

    private async Task<string> ToGeoJsonPathAsync(string input) => (await fx.ConvertOkAsync(input, "geojson")).Single();

    [Fact]
    public async Task Gpx_output_says_when_polygons_and_other_attributes_could_not_be_kept()
    {
        var job = await fx.ConvertAsync(GeoFixtures.Copy(fx, "gdal_polygons", "shp"), "gpx");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.geo_gpx_lossy", job.Files.Single().NoticeKey);
        var tracks = XDocument.Load(job.Outputs.Single()).Descendants().Where(e => e.Name.LocalName == "trk").ToList();
        Assert.Equal(2, tracks.Count); // one track per feature, its rings as segments
        Assert.Equal(2, tracks[0].Elements().Count(e => e.Name.LocalName == "trkseg"));
        Assert.Equal(2, tracks[1].Elements().Count(e => e.Name.LocalName == "trkseg"));
    }

    [Theory]
    [InlineData("""<gpx><wpt lat="91" lon="0"/></gpx>""")]
    [InlineData("""<gpx><wpt lat="0" lon="500"/></gpx>""")]
    [InlineData("""<gpx><wpt lon="0"/></gpx>""")]
    [InlineData("""<gpx><wpt lat="north" lon="0"/></gpx>""")]
    [InlineData("""<notgpx/>""")]
    [InlineData("<gpx><wpt lat=\"1\" lon=\"2\">")]
    public async Task Gpx_with_invalid_positions_or_structure_fails_with_a_reason(string text)
    {
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "bad.gpx", text), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.NotEmpty(detail);
    }

    // ---- CSV ----

    private async Task<(JsonDocument Doc, string? Notice)> CsvToGeoJsonAsync(string csv, string name = "points.csv")
    {
        var job = await fx.ConvertAsync(GeoFixtures.WriteText(fx, name, csv), "geojson");
        Assert.True(job.State == JobState.Completed, GeoFixtures.Describe(job));
        return (GeoFixtures.ReadJson(job.Outputs.Single()), job.Files.Single().NoticeKey);
    }

    [Theory]
    [InlineData("name,lat,lon\nA,48.8566,2.3522\n")]
    [InlineData("Name,Latitude,Longitude\nA,48.8566,2.3522\n")]
    [InlineData("name,LAT,LNG\nA,48.8566,2.3522\n")]
    [InlineData("name,y,x\nA,48.8566,2.3522\n")]
    [InlineData("name;lat;lon\nA;48,8566;2,3522\n")]
    [InlineData("name\tlatitude\tlongitude\nA\t48.8566\t2.3522\n")]
    [InlineData("\uFEFFname,\"lat\",\"long\"\r\n\"A\",\"48.8566\",\"2.3522\"\r\n")]
    public async Task Csv_longitude_and_latitude_columns_are_found_by_name_whatever_the_order_delimiter_or_decimal_mark(string csv)
    {
        var (doc, notice) = await CsvToGeoJsonAsync(csv);
        using (doc)
        {
            var feature = Features(doc)[0];
            Assert.Equal([2.3522, 48.8566], GeoFixtures.Position(feature.GetProperty("geometry")));
            // The coordinate columns are the geometry, not attributes: "name" is the only attribute (its header's case is kept).
            var attribute = Assert.Single(feature.GetProperty("properties").EnumerateObject());
            Assert.Equal("name", attribute.Name, ignoreCase: true);
            Assert.Equal("A", attribute.Value.GetString());
            Assert.Null(notice);
        }
    }

    [Fact]
    public async Task Csv_attribute_values_are_typed_and_identifiers_with_leading_zeros_stay_text()
    {
        var (doc, _) = await CsvToGeoJsonAsync("id,code,height,flag,note,lon,lat,elevation\n1,007,12.5,true,\"a,b\",1,2,300\n");
        using (doc)
        {
            var p = Features(doc)[0].GetProperty("properties");
            Assert.Equal(1, p.GetProperty("id").GetInt64());
            Assert.Equal("007", p.GetProperty("code").GetString());
            Assert.Equal(12.5, p.GetProperty("height").GetDouble());
            Assert.True(p.GetProperty("flag").GetBoolean());
            Assert.Equal("a,b", p.GetProperty("note").GetString());
            Assert.Equal([1.0, 2.0, 300.0], GeoFixtures.Position(Features(doc)[0].GetProperty("geometry")));
        }
    }

    [Fact]
    public async Task Csv_wkt_column_gives_any_geometry()
    {
        var csv = "id,WKT\n1,\"POINT (1 2)\"\n2,\"LINESTRING (0 0, 1 1)\"\n3,\"POLYGON ((0 0, 4 0, 4 4, 0 0))\"\n4,\"SRID=4326;MULTIPOINT ((1 1), (2 2))\"\n5,\n";
        var (doc, notice) = await CsvToGeoJsonAsync(csv);
        using (doc)
        {
            var types = Features(doc).EnumerateArray().Select(f => f.GetProperty("geometry") is { ValueKind: JsonValueKind.Object } g ? g.GetProperty("type").GetString() : null).ToArray();
            Assert.Equal(new string?[] { "Point", "LineString", "Polygon", "MultiPoint", null }, types);
            Assert.Equal("notice.geo_rows_without_location", notice);
        }
    }

    [Fact]
    public async Task Csv_rows_without_a_usable_location_are_kept_without_a_shape_and_noted()
    {
        var (doc, notice) = await CsvToGeoJsonAsync("n,lat,lon\n1,10,20\n2,,\n3,abc,30\n4,5,6\n");
        using (doc)
        {
            Assert.Equal(4, Features(doc).GetArrayLength());
            Assert.Equal(JsonValueKind.Null, Features(doc)[1].GetProperty("geometry").ValueKind);
            Assert.Equal(JsonValueKind.Null, Features(doc)[2].GetProperty("geometry").ValueKind);
            Assert.Equal("notice.geo_rows_without_location", notice);
        }
    }

    [Theory]
    [InlineData("name,value\nA,1\n", "error.geo_csv_no_coordinates")]
    [InlineData("name,lat\nA,1\n", "error.geo_csv_no_coordinates")]
    [InlineData("lat,lon\n,\n,\n", "error.geo_empty")] // rows with nothing in them don't count as rows
    [InlineData("lat,lon\nnorth,east\n", "error.geo_csv_no_locations")]
    [InlineData("lat,lon\n", "error.geo_empty")]
    [InlineData("easting,northing\n602065.2,5340353.6\n", "error.geo_unknown_crs")]
    [InlineData("x,y\n602065.2,5340353.6\n", "error.geo_unknown_crs")]
    [InlineData("lat,lon\n95,10\n", "error.geo_unknown_crs")]
    public async Task Csv_that_cannot_give_locations_is_refused_with_the_reason(string csv, string expectedKey)
    {
        var (key, _) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "bad.csv", csv), "geojson");
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public async Task Csv_with_an_invalid_wkt_cell_names_the_row()
    {
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "badwkt.csv", "id,wkt\n1,POINT (1 2)\n2,POINT (oops)\n"), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("Row 3", detail);
    }

    [Fact]
    public async Task Csv_output_is_longitude_latitude_columns_for_points_and_wkt_otherwise()
    {
        var points = File.ReadAllText((await fx.ConvertOkAsync(GeoFixtures.Points(fx, "geojson"), "csv")).Single()).TrimStart('\uFEFF');
        Assert.StartsWith("longitude,latitude,name,n\r\n10.5,20.25,A,1\r\n-5,40,B,2\r\n", points);

        var polygons = File.ReadAllText((await fx.ConvertOkAsync(GeoFixtures.Copy(fx, "gdal_polygons", "shp"), "csv")).Single()).TrimStart('\uFEFF');
        Assert.StartsWith("wkt,id,label\r\n", polygons);
        Assert.Contains("\"POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 4, 4 4, 4 2, 2 2))\",1,with hole", polygons);
    }

    [Fact]
    public async Task Csv_output_uses_wkt_when_an_attribute_is_already_called_longitude_so_it_reads_back_unchanged()
    {
        var text = """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"LONGITUDE":111.0,"LATITUDE":222.0,"n":1},"geometry":{"type":"Point","coordinates":[1,2]}}]}""";
        var csv = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "attrs.geojson", text), "csv")).Single();
        Assert.StartsWith("wkt,LONGITUDE,LATITUDE,n", File.ReadAllText(csv).TrimStart('\uFEFF'));
        using var doc = await ToGeoJsonAsync(csv);
        Assert.Equal([1.0, 2.0], GeoFixtures.Position(Features(doc)[0].GetProperty("geometry")));
        Assert.Equal(111, Features(doc)[0].GetProperty("properties").GetProperty("LONGITUDE").GetInt64());
    }

    [Fact]
    public async Task Csv_output_quotes_commas_quotes_and_line_breaks()
    {
        var text = "{\"type\":\"Feature\",\"properties\":{\"t\":\"a,b \\\"c\\\"\\nd\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}}";
        var csv = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "quote.geojson", text), "csv")).Single();
        using var doc = await ToGeoJsonAsync(csv);
        Assert.Equal("a,b \"c\"\nd", Features(doc)[0].GetProperty("properties").GetProperty("t").GetString());
    }

    // ---- Empty and degenerate input ----

    [Theory]
    [InlineData("""{"type":"FeatureCollection","features":[]}""", "geojson")]
    [InlineData("""<kml><Document/></kml>""", "kml")]
    [InlineData("""<gpx/>""", "gpx")]
    public async Task A_file_with_no_features_is_refused_with_a_clear_reason(string text, string extension)
    {
        var (key, _) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "empty." + extension, text), extension == "geojson" ? "kml" : "geojson");
        Assert.Equal("error.geo_empty", key);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"a":1}""")]
    [InlineData("""{"type":"FeatureCollection"}""")]
    [InlineData("""{"type":"Banana","coordinates":[1,2]}""")]
    [InlineData("""{"type":"Point"}""")]
    [InlineData("""{"type":"Point","coordinates":"x"}""")]
    [InlineData("""{"type":"Point","coordinates":[1]}""")]
    [InlineData("""{"type":"Point","coordinates":["a","b"]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[1]}""")]
    [InlineData("""{"type":"FeatureCollection","features":[1]}""")]
    [InlineData("{\"type\":\"Point\",\"coordinates\":[1,2")]
    public async Task GeoJson_that_is_not_geojson_fails_with_a_reason(string text)
    {
        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "bad.geojson", text), "kml");
        Assert.Equal("error.conversion_failed", key);
        Assert.NotEmpty(detail);
    }

    [Fact]
    public async Task GeoJson_nested_far_too_deep_fails_instead_of_overflowing_the_stack()
    {
        var text = string.Concat(Enumerable.Repeat("[", 100_000)) + string.Concat(Enumerable.Repeat("]", 100_000));
        var (key, _) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "deep.geojson", text), "kml");
        Assert.Equal("error.conversion_failed", key);
    }
}
