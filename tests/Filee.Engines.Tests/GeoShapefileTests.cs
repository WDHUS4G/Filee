// Shapefile in and out. Reading: polygons and holes in every arrangement real files have, shape types, dBASE types and
// encodings, deleted rows, the .prj rules and damaged files. Writing: the files are parsed here from the ESRI
// specification rather than read back with the engine, so the header, index, attribute table and ring directions are
// checked independently; field names and text that don't fit; files split by geometry kind; and name collisions.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class GeoShapefileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static GeoShapefileTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Wgs84Prj = """GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]""";

    // Shapefile rings: outer clockwise, holes counter-clockwise.
    private static (double X, double Y)[] Outer(double min, double max) => [(min, min), (min, max), (max, max), (max, min), (min, min)];

    private static (double X, double Y)[] Hole(double min, double max) => [(min, min), (max, min), (max, max), (min, max), (min, min)];

    private string NewShapefile(ShapefileBuilder builder, string? prj = Wgs84Prj, DbfBuilder? dbf = null)
    {
        var folder = fx.NewFolder();
        return builder.Save(folder, "data", prj, dbf);
    }

    private async Task<JsonDocument> GeoJsonAsync(string shp)
    {
        var output = (await fx.ConvertOkAsync(shp, "geojson")).Single();
        return GeoFixtures.ReadJson(output);
    }

    private static JsonElement Geometry(JsonDocument doc, int index = 0) => doc.RootElement.GetProperty("features")[index].GetProperty("geometry");

    private static string Type(JsonElement geometry) => geometry.ValueKind == JsonValueKind.Null ? "null" : geometry.GetProperty("type").GetString()!;

    private static int Rings(JsonElement polygon) => polygon.GetProperty("coordinates").GetArrayLength();

    // ---- Reading polygons ----

    [Fact]
    public async Task A_hole_counter_clockwise_inside_an_outer_ring_clockwise_is_one_polygon()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, Outer(0, 10), Hole(2, 4))));
        Assert.Equal("Polygon", Type(Geometry(doc)));
        Assert.Equal(2, Rings(Geometry(doc)));
    }

    [Fact]
    public async Task Holes_are_given_to_the_outer_ring_that_contains_them_whatever_the_order_of_the_parts()
    {
        var a = Outer(0, 10);
        var b = Outer(20, 30);
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, a, b, Hole(22, 24), Hole(2, 4), Hole(6, 8))));
        var geometry = Geometry(doc);
        Assert.Equal("MultiPolygon", Type(geometry));
        var polygons = geometry.GetProperty("coordinates").EnumerateArray().ToList();
        Assert.Equal(2, polygons.Count);
        Assert.Equal(3, polygons[0].GetArrayLength()); // outer 0..10 with two holes
        Assert.Equal(2, polygons[1].GetArrayLength()); // outer 20..30 with one
    }

    [Fact]
    public async Task An_island_inside_a_hole_is_a_polygon_of_its_own()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, Outer(0, 80), Hole(10, 70), Outer(30, 50))));
        var polygons = Geometry(doc).GetProperty("coordinates").EnumerateArray().ToList();
        Assert.Equal(2, polygons.Count);
        Assert.Equal(2, polygons[0].GetArrayLength());
        Assert.Equal(1, polygons[1].GetArrayLength());
    }

    [Fact]
    public async Task A_hole_outside_every_outer_ring_is_kept_as_a_ring_of_its_own()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, Outer(0, 10), Hole(50, 60))));
        Assert.Equal("MultiPolygon", Type(Geometry(doc)));
        Assert.Equal(2, Geometry(doc).GetProperty("coordinates").GetArrayLength());
    }

    [Fact]
    public async Task When_every_ring_runs_counter_clockwise_they_are_all_outer_rings()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, Hole(0, 10), Hole(20, 30))));
        Assert.Equal("MultiPolygon", Type(Geometry(doc)));
        Assert.Equal(2, Geometry(doc).GetProperty("coordinates").GetArrayLength());
    }

    [Fact]
    public async Task Open_degenerate_and_zero_area_rings_are_repaired_or_dropped()
    {
        var open = new (double X, double Y)[] { (0, 0), (0, 10), (10, 10), (10, 0) };
        var twoPoints = new (double X, double Y)[] { (50, 50), (60, 60) };
        var flat = new (double X, double Y)[] { (70, 70), (80, 70), (90, 70), (70, 70) };
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, open, twoPoints, flat)));
        Assert.Equal("Polygon", Type(Geometry(doc)));
        Assert.Equal(1, Rings(Geometry(doc)));
        Assert.Equal(5, Geometry(doc).GetProperty("coordinates")[0].GetArrayLength());
    }

    [Fact]
    public async Task A_polygon_with_only_degenerate_rings_has_no_geometry()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(5).Parts(5, [(1, 1), (2, 2)])));
        Assert.Equal("null", Type(Geometry(doc)));
    }

    // ---- Reading other shape types ----

    [Fact]
    public async Task Points_multipoints_lines_and_null_shapes_are_read()
    {
        using (var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(8).MultiPoint((1, 2), (3, 4)).MultiPoint((5, 6)))))
        {
            Assert.Equal("MultiPoint", Type(Geometry(doc, 0)));
            Assert.Equal("Point", Type(Geometry(doc, 1)));
        }
        using (var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(3).Parts(3, [(0, 0), (1, 1)], [(5, 5), (6, 6), (7, 5)], [(9, 9)]).Null())))
        {
            Assert.Equal("MultiLineString", Type(Geometry(doc, 0)));
            Assert.Equal(2, Geometry(doc, 0).GetProperty("coordinates").GetArrayLength()); // the one-point part is dropped
            Assert.Equal("null", Type(Geometry(doc, 1)));
        }
    }

    [Fact]
    public async Task PointZ_height_is_kept_and_no_data_is_not_a_height()
    {
        var folder = fx.NewFolder();
        var withZ = new ShapefileBuilder(11).PointZ(1, 2, 345.5).Save(folder, "z", Wgs84Prj);
        using var doc = await GeoJsonAsync(withZ);
        Assert.Equal([1.0, 2.0, 345.5], GeoFixtures.Position(Geometry(doc)));

        var noData = new ShapefileBuilder(11).PointZ(1, 2, -1e39).Save(fx.NewFolder(), "nz", Wgs84Prj);
        using var doc2 = await GeoJsonAsync(noData);
        Assert.Equal([1.0, 2.0], GeoFixtures.Position(Geometry(doc2)));
    }

    [Fact]
    public async Task An_empty_point_written_as_NaN_has_no_geometry()
    {
        var nan = ShapefileBuilder.Le(1).Concat(ShapefileBuilder.Dbl(double.NaN)).Concat(ShapefileBuilder.Dbl(double.NaN)).ToArray();
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Raw(nan)));
        Assert.Equal("null", Type(Geometry(doc)));
    }

    // ---- The attribute table ----

    [Fact]
    public async Task Dbf_field_types_are_read_as_text_numbers_booleans_and_dates()
    {
        var dbf = new DbfBuilder()
            .Field("NAME", 'C', 10).Field("N0", 'N', 5).Field("N3", 'N', 10, 3).Field("F2", 'F', 8, 2)
            .Field("FLAG", 'L', 1).Field("DAY", 'D', 8).Field("MEMO", 'M', 10).Field("NEG", 'N', 6, 1)
            .Row("abc", "   42", "     1.500", "    3.14", "T", "20240229", "0000000001", "  -2.5")
            .Row("", "", "", "", "?", "20241399", "", "*****")
            .Row("ünï", "12.0", "0", "  -0.5", "n", "     ", "", "   0.0");
        var builder = new ShapefileBuilder(1).Point(1, 1).Point(2, 2).Point(3, 3);
        using var doc = await GeoJsonAsync(NewShapefile(builder, dbf: dbf));
        var rows = doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties")).ToList();

        Assert.Equal("abc", rows[0].GetProperty("NAME").GetString());
        Assert.Equal(42, rows[0].GetProperty("N0").GetInt64());
        Assert.Equal(1.5, rows[0].GetProperty("N3").GetDouble());
        Assert.Equal(3.14, rows[0].GetProperty("F2").GetDouble());
        Assert.True(rows[0].GetProperty("FLAG").GetBoolean());
        Assert.Equal("2024-02-29", rows[0].GetProperty("DAY").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("MEMO").ValueKind);
        Assert.Equal(-2.5, rows[0].GetProperty("NEG").GetDouble());

        Assert.Equal("", rows[1].GetProperty("NAME").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("N0").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("N3").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("FLAG").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("DAY").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("NEG").ValueKind);

        Assert.Equal(12, rows[2].GetProperty("N0").GetInt64());
        Assert.False(rows[2].GetProperty("FLAG").GetBoolean());
        Assert.Equal(-0.5, rows[2].GetProperty("F2").GetDouble());
    }

    [Fact]
    public async Task Rows_marked_as_deleted_in_the_dbf_are_left_out()
    {
        var dbf = new DbfBuilder().Field("ID", 'N', 3).Row("1").Row(true, "2").Row("3");
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2).Point(3, 3), dbf: dbf));
        var ids = doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties").GetProperty("ID").GetInt64()).ToArray();
        Assert.Equal([1L, 3L], ids);
    }

    [Fact]
    public async Task A_shapefile_without_a_dbf_converts_with_no_attributes()
    {
        using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Point(1, 1)));
        Assert.Empty(doc.RootElement.GetProperty("features")[0].GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task A_dbf_with_more_rows_than_shapes_is_used_and_one_with_fewer_is_refused()
    {
        var more = new DbfBuilder().Field("ID", 'N', 3).Row("1").Row("2").Row("3");
        using (var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2), dbf: more)))
            Assert.Equal(2, doc.RootElement.GetProperty("features").GetArrayLength());

        var fewer = new DbfBuilder().Field("ID", 'N', 3).Row("1");
        var (key, detail) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2), dbf: fewer), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("1 rows but the .shp has 2", detail);
    }

    [Theory]
    // text, code page of the bytes, language driver byte, .cpg text (null = no .cpg file)
    [InlineData("서울특별시 종로구", 949, (byte)0x4E, null)]
    [InlineData("서울특별시 종로구", 949, (byte)0, "949")]
    [InlineData("서울특별시 종로구", 949, (byte)0, "CP949")]
    [InlineData("서울특별시 종로구", 949, (byte)0, null)]               // told by nothing: not UTF-8, valid CP949
    [InlineData("서울 Ünïcödé 北京", 65001, (byte)0, "UTF-8")]
    [InlineData("서울 Ünïcödé 北京", 65001, (byte)0, null)]            // told by nothing: valid UTF-8
    [InlineData("Müller – Café", 1252, (byte)0x57, null)]
    [InlineData("Müller – Café", 1252, (byte)0x03, null)]
    [InlineData("Müller – Café", 1252, (byte)0, "1252")]
    [InlineData("Müller", 1252, (byte)0, "ISO-8859-1")]
    [InlineData("Müller", 1252, (byte)0, "LATIN1")]
    [InlineData("Müller", 1252, (byte)0, null)]                        // told by nothing: neither UTF-8 nor CP949
    [InlineData("北京市朝阳区", 936, (byte)0x4D, null)]
    [InlineData("北京市朝阳区", 936, (byte)0, "GBK")]
    [InlineData("日本語テキスト", 932, (byte)0x13, null)]
    [InlineData("Привет мир", 1251, (byte)0xC9, null)]
    [InlineData("Привет мир", 1251, (byte)0, "windows-1251")]
    public async Task Text_is_decoded_with_the_cpg_the_language_driver_or_by_detection(string text, int codePage, byte driver, string? cpg)
    {
        var encoding = Encoding.GetEncoding(codePage);
        if (codePage == 1252)
            text = text.Replace("–", "-", StringComparison.Ordinal); // not every character exists in every code page
        var dbf = new DbfBuilder { Encoding = encoding, LanguageDriver = driver }.Field("NAME", 'C', 40).Row(text);
        var folder = fx.NewFolder();
        var shp = new ShapefileBuilder(1).Point(1, 1).Save(folder, "enc", Wgs84Prj, dbf);
        if (cpg is not null)
            File.WriteAllText(Path.Combine(folder, "enc.cpg"), cpg);
        using var doc = await GeoJsonAsync(shp);
        Assert.Equal(text, doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("NAME").GetString());
    }

    [Fact]
    public async Task Sidecar_files_are_found_whatever_the_letter_case()
    {
        var folder = fx.NewFolder();
        new ShapefileBuilder(1).Point(1, 2).Save(folder, "mixed", Wgs84Prj, new DbfBuilder().Field("ID", 'N', 3).Row("7"));
        File.Move(Path.Combine(folder, "mixed.dbf"), Path.Combine(folder, "mixed.DBF"));
        File.Move(Path.Combine(folder, "mixed.prj"), Path.Combine(folder, "mixed.Prj"));
        using var doc = await GeoJsonAsync(Path.Combine(folder, "mixed.shp"));
        Assert.Equal(7, doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("ID").GetInt64());
    }

    // ---- Coordinate systems ----

    [Fact]
    public async Task Prj_with_a_projection_converts_the_positions_and_a_bom_or_newline_does_not_matter()
    {
        // Vienna in UTM 33N.
        const string utm = """PROJCS["WGS_1984_UTM_Zone_33N",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",15.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""";
        foreach (var prj in new[] { utm, "﻿" + utm + "\r\n", utm + "\n\n" })
        {
            using var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Point(602065.2074110014, 5340353.594303575), prj));
            var position = GeoFixtures.Position(Geometry(doc));
            Assert.Equal(16.3738, position[0], 8);
            Assert.Equal(48.2082, position[1], 8);
        }
    }

    [Fact]
    public async Task No_prj_is_fine_for_degrees_and_refused_for_anything_else()
    {
        using (var doc = await GeoJsonAsync(NewShapefile(new ShapefileBuilder(1).Point(16.37, 48.2), prj: null)))
            Assert.Equal([16.37, 48.2], GeoFixtures.Position(Geometry(doc)));
        var (key, _) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(1).Point(602065.2, 5340353.6), prj: null), "geojson");
        Assert.Equal("error.geo_unknown_crs", key);
    }

    [Fact]
    public async Task A_projection_filee_cannot_convert_is_refused_naming_it()
    {
        const string laea = """PROJCS["ETRS89_LAEA_Europe",GEOGCS["GCS_ETRS_1989",DATUM["D_ETRS_1989",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Azimuthal_Equal_Area"],PARAMETER["False_Easting",4321000.0],PARAMETER["False_Northing",3210000.0],PARAMETER["Central_Meridian",10.0],PARAMETER["Latitude_Of_Origin",52.0],UNIT["Meter",1.0]]""";
        var (key, detail) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(1).Point(4321000, 3210000), laea), "geojson");
        Assert.Equal("error.geo_unsupported_crs", key);
        Assert.Contains("ETRS89_LAEA_Europe", detail);
        var (garbage, _) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(1).Point(1, 1), "hello"), "geojson");
        Assert.Equal("error.geo_unsupported_crs", garbage);
    }

    [Fact]
    public async Task A_position_far_outside_where_the_projection_works_is_refused_instead_of_placed_somewhere()
    {
        const string utm = """PROJCS["WGS_1984_UTM_Zone_33N",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",15.0],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""";
        var (key, _) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(1).Point(5e9, 5e9), utm), "geojson");
        Assert.Equal("error.geo_bad_coordinates", key);
    }

    [Fact]
    public async Task A_datum_that_is_not_shifted_leaves_a_note_on_the_file()
    {
        const string dhdn = """PROJCS["DHDN_3_Degree_Gauss_Zone_3",GEOGCS["GCS_Deutsches_Hauptdreiecksnetz",DATUM["D_Deutsches_Hauptdreiecksnetz",SPHEROID["Bessel_1841",6377397.155,299.1528128]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Gauss_Kruger"],PARAMETER["False_Easting",3500000.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",9.0],PARAMETER["Scale_Factor",1.0],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]""";
        var job = await fx.ConvertAsync(NewShapefile(new ShapefileBuilder(1).Point(3500000, 5400000), dhdn), "geojson");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.geo_datum_assumed", job.Files.Single().NoticeKey);
    }

    // ---- Damaged and unsupported files ----

    [Fact]
    public async Task MultiPatch_is_refused_by_name()
    {
        var folder = fx.NewFolder();
        var shp = new ShapefileBuilder(31).Raw(ShapefileBuilder.Le(31).Concat(new byte[60]).ToArray()).Save(folder, "patch", Wgs84Prj);
        var (key, detail) = await fx.ConvertFailsAsync(shp, "geojson");
        Assert.Equal("error.geo_unsupported_shape", key);
        Assert.Contains("MultiPatch", detail);
    }

    [Fact]
    public async Task Damaged_shp_files_fail_with_a_reason_for_the_user()
    {
        var good = NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2));
        var bytes = File.ReadAllBytes(good);

        (string Name, byte[] Data, string Expect)[] cases =
        [
            ("short1", bytes[..1], "shapefile header"),
            ("short", bytes[..60], "shapefile header"),
            ("magic", [.. bytes.Select((b, i) => i == 3 ? (byte)0 : b)], "shapefile header"),
            ("cut", bytes[..^7], "cut off"),
            ("type", [.. bytes.Select((b, i) => i == 32 ? (byte)99 : b)], "unknown shape type"),
        ];
        foreach (var (name, data, expect) in cases)
        {
            var path = Path.Combine(fx.NewFolder(), name + ".shp");
            File.WriteAllBytes(path, data);
            var (key, detail) = await fx.ConvertFailsAsync(path, "geojson");
            Assert.Equal("error.conversion_failed", key);
            Assert.Contains(expect, detail);
        }
    }

    [Fact]
    public async Task Counts_and_part_indexes_that_do_not_fit_the_record_are_refused_without_allocating()
    {
        var record = new List<byte>();
        record.AddRange(ShapefileBuilder.Le(5));
        record.AddRange(new byte[32]);
        record.AddRange(ShapefileBuilder.Le(int.MaxValue)); // parts
        record.AddRange(ShapefileBuilder.Le(int.MaxValue)); // points
        var (key, detail) = await fx.ConvertFailsAsync(NewShapefile(new ShapefileBuilder(5).Raw([.. record])), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("do not fit", detail);

        var badPart = new ShapefileBuilder(5).Parts(5, Outer(0, 10));
        var shp = NewShapefile(badPart);
        var bytes = File.ReadAllBytes(shp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(100 + 8 + 44), 99); // the first part starts after the last point
        File.WriteAllBytes(shp, bytes);
        var (_, partDetail) = await fx.ConvertFailsAsync(shp, "geojson");
        Assert.Contains("starts outside", partDetail);
    }

    [Fact]
    public async Task A_wrong_length_in_the_header_does_not_hide_the_shapes_or_make_the_file_fail()
    {
        var shp = NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2));
        var bytes = File.ReadAllBytes(shp);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24), 1_000_000); // claims far more than the file holds
        File.WriteAllBytes(shp, bytes);
        using (var doc = await GeoJsonAsync(shp))
            Assert.Equal(2, doc.RootElement.GetProperty("features").GetArrayLength());

        // Trailing bytes after the declared length (some tools pad the file) are ignored.
        var padded = NewShapefile(new ShapefileBuilder(1).Point(1, 1));
        File.WriteAllBytes(padded, [.. File.ReadAllBytes(padded), .. new byte[5]]);
        using var doc2 = await GeoJsonAsync(padded);
        Assert.Equal(1, doc2.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task A_damaged_dbf_fails_with_a_reason()
    {
        var dbf = new DbfBuilder().Field("ID", 'N', 3).Row("1");
        var shp = NewShapefile(new ShapefileBuilder(1).Point(1, 1), dbf: dbf);
        var dbfPath = Path.ChangeExtension(shp, "dbf");
        var good = File.ReadAllBytes(dbfPath);

        (string Name, byte[] Data, string Expect)[] cases =
        [
            ("tiny", good[..10], "too short"),
            ("header", [.. good.Select((b, i) => i is 8 or 9 ? (byte)0 : b)], "damaged"),
            ("fields", [.. good.Select((b, i) => i == 10 ? (byte)1 : b)], "do not fit"),
        ];
        foreach (var (name, data, expect) in cases)
        {
            File.WriteAllBytes(dbfPath, data);
            var (key, detail) = await fx.ConvertFailsAsync(shp, "geojson");
            Assert.Equal("error.conversion_failed", key);
            Assert.Contains(expect, detail);
        }
    }

    [Fact]
    public async Task A_dbf_that_ends_early_gives_the_rows_that_are_there_and_then_a_mismatch_error()
    {
        var dbf = new DbfBuilder().Field("ID", 'N', 3).Row("1").Row("2").Row("3");
        var shp = NewShapefile(new ShapefileBuilder(1).Point(1, 1).Point(2, 2).Point(3, 3), dbf: dbf);
        var path = Path.ChangeExtension(shp, "dbf");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^5]); // cuts the end marker and most of the last row
        var (_, detail) = await fx.ConvertFailsAsync(shp, "geojson");
        Assert.Contains("2 rows but the .shp has 3", detail);
    }

    // ---- Writing: the files are parsed here from the specification ----

    private sealed record ShpFile(int FileLengthBytes, int Type, List<byte[]> Records, int[] BoxBytes);

    private static ShpFile ParseShp(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(9994, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28)));
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24)) * 2;
        Assert.Equal(bytes.Length, length);
        var records = new List<byte[]>();
        var position = 100;
        while (position < bytes.Length)
        {
            Assert.Equal(records.Count + 1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position)));
            var content = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position + 4)) * 2;
            records.Add(bytes[(position + 8)..(position + 8 + content)]);
            position += 8 + content;
        }
        Assert.Equal(bytes.Length, position);
        return new ShpFile(length, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)), records, []);
    }

    private static void CheckShx(string shxPath, string shpPath)
    {
        var shx = File.ReadAllBytes(shxPath);
        var shp = File.ReadAllBytes(shpPath);
        Assert.Equal(9994, BinaryPrimitives.ReadInt32BigEndian(shx));
        Assert.Equal(shx.Length, BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(24)) * 2);
        Assert.Equal(shp.AsSpan(32, 4).ToArray(), shx.AsSpan(32, 4).ToArray());
        Assert.Equal(shp.AsSpan(36, 64).ToArray(), shx.AsSpan(36, 64).ToArray()); // same bounding box
        var count = (shx.Length - 100) / 8;
        var position = 100;
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(100 + 8 * i)) * 2;
            var words = BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(104 + 8 * i));
            Assert.Equal(position, offset);
            Assert.Equal(words, BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(offset + 4)));
            position += 8 + words * 2;
        }
    }

    private sealed record DbfInfo(int Rows, int RecordLength, List<(string Name, char Type, int Length, int Decimals)> Fields, byte[] Bytes);

    private static DbfInfo ParseDbf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(3, bytes[0]);
        var rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        var fields = new List<(string, char, int, int)>();
        var at = 32;
        while (bytes[at] != 0x0D)
        {
            var name = Encoding.UTF8.GetString(bytes, at, 11).TrimEnd('\0');
            fields.Add((name, (char)bytes[at + 11], bytes[at + 16], bytes[at + 17]));
            at += 32;
        }
        Assert.Equal(headerLength, at + 1);
        Assert.Equal(1 + fields.Sum(f => f.Item3), recordLength);
        Assert.Equal(headerLength + rows * recordLength + 1, bytes.Length);
        Assert.Equal(0x1A, bytes[^1]);
        return new DbfInfo(rows, recordLength, fields, bytes);
    }

    private static string Cell(DbfInfo dbf, int row, int field)
    {
        var start = (int)BinaryPrimitives.ReadUInt16LittleEndian(dbf.Bytes.AsSpan(8)) + row * dbf.RecordLength + 1 + dbf.Fields.Take(field).Sum(f => f.Length);
        return Encoding.UTF8.GetString(dbf.Bytes, start, dbf.Fields[field].Length);
    }

    private async Task<(string Shp, List<string> Files)> ToShapefileAsync(string json, string name = "in.geojson", Action<Preset>? tweak = null)
    {
        var files = await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, name, json), "shp", tweak);
        return (files.First(f => f.EndsWith(".shp", StringComparison.Ordinal)), files);
    }

    private static string FeatureCollection(params string[] features) => """{"type":"FeatureCollection","features":[""" + string.Join(',', features) + "]}";

    private static string Feature(string geometry, string properties = "{}") => $$"""{"type":"Feature","properties":{{properties}},"geometry":{{geometry}}}""";

    [Fact]
    public async Task Points_make_a_complete_set_of_five_consistent_files()
    {
        var (shp, files) = await ToShapefileAsync(FeatureCollection(
            Feature("""{"type":"Point","coordinates":[10.5,20.25]}""", """{"name":"A","n":1}"""),
            Feature("""{"type":"Point","coordinates":[-5,40]}""", """{"name":"B","n":2}""")));
        Assert.Equal(["cpg", "dbf", "prj", "shp", "shx"], files.Select(f => Path.GetExtension(f)[1..]).Order().ToArray());
        Assert.Single(files.Select(f => Path.GetFileNameWithoutExtension(f)).Distinct());

        var parsed = ParseShp(shp);
        Assert.Equal(1, parsed.Type);
        Assert.Equal(2, parsed.Records.Count);
        Assert.All(parsed.Records, r => Assert.Equal(20, r.Length));
        Assert.Equal(10.5, BitConverter.ToDouble(parsed.Records[0], 4));
        Assert.Equal(20.25, BitConverter.ToDouble(parsed.Records[0], 12));
        var bytes = File.ReadAllBytes(shp);
        Assert.Equal(-5, BitConverter.ToDouble(bytes, 36)); // xmin
        Assert.Equal(20.25, BitConverter.ToDouble(bytes, 44)); // ymin
        Assert.Equal(10.5, BitConverter.ToDouble(bytes, 52)); // xmax
        Assert.Equal(40, BitConverter.ToDouble(bytes, 60)); // ymax
        CheckShx(Path.ChangeExtension(shp, "shx"), shp);

        var dbf = ParseDbf(Path.ChangeExtension(shp, "dbf"));
        Assert.Equal(2, dbf.Rows);
        Assert.Equal([("name", 'C', 1, 0), ("n", 'N', 1, 0)], dbf.Fields.ToArray());
        Assert.Equal("B", Cell(dbf, 1, 0));
        Assert.Equal("UTF-8", File.ReadAllText(Path.ChangeExtension(shp, "cpg")));
        Assert.StartsWith("GEOGCS[\"GCS_WGS_1984\"", File.ReadAllText(Path.ChangeExtension(shp, "prj")));
    }

    [Fact]
    public async Task Polygons_are_written_clockwise_with_holes_counter_clockwise_and_closed_rings()
    {
        const string polygon = """{"type":"Polygon","coordinates":[[[0,0],[4,0],[4,4],[0,4],[0,0]],[[1,1],[1,2],[2,2],[2,1],[1,1]]]}""";
        var (shp, _) = await ToShapefileAsync(FeatureCollection(Feature(polygon)));
        var parsed = ParseShp(shp);
        Assert.Equal(5, parsed.Type);
        var record = parsed.Records.Single();
        Assert.Equal(2, BitConverter.ToInt32(record, 36));
        Assert.Equal(10, BitConverter.ToInt32(record, 40));
        Assert.Equal([0, 5], new[] { BitConverter.ToInt32(record, 44), BitConverter.ToInt32(record, 48) });
        double Area(int from, int to)
        {
            var sum = 0.0;
            for (var i = from; i < to - 1; i++)
            {
                var (x1, y1) = (BitConverter.ToDouble(record, 52 + 16 * i), BitConverter.ToDouble(record, 60 + 16 * i));
                var (x2, y2) = (BitConverter.ToDouble(record, 52 + 16 * (i + 1)), BitConverter.ToDouble(record, 60 + 16 * (i + 1)));
                sum += x1 * y2 - x2 * y1;
            }
            return sum / 2;
        }
        Assert.Equal(-16, Area(0, 5));
        Assert.Equal(1, Area(5, 10));
        CheckShx(Path.ChangeExtension(shp, "shx"), shp);
    }

    [Fact]
    public async Task Height_makes_the_z_shape_types_and_a_missing_height_is_zero()
    {
        var (points, _) = await ToShapefileAsync(FeatureCollection(
            Feature("""{"type":"Point","coordinates":[1,2,30]}"""), Feature("""{"type":"Point","coordinates":[3,4]}""")), "z1.geojson");
        var parsed = ParseShp(points);
        Assert.Equal(11, parsed.Type);
        Assert.Equal(30, BitConverter.ToDouble(parsed.Records[0], 20));
        Assert.Equal(0, BitConverter.ToDouble(parsed.Records[1], 20));
        Assert.All(parsed.Records, r => Assert.Equal(36, r.Length));

        var (lines, _) = await ToShapefileAsync(FeatureCollection(Feature("""{"type":"LineString","coordinates":[[0,0,5],[1,1,6]]}""")), "z2.geojson");
        Assert.Equal(13, ParseShp(lines).Type);
        var (polygons, _) = await ToShapefileAsync(FeatureCollection(Feature("""{"type":"Polygon","coordinates":[[[0,0,1],[1,0,1],[1,1,1],[0,0,1]]]}""")), "z3.geojson");
        Assert.Equal(15, ParseShp(polygons).Type);
        CheckShx(Path.ChangeExtension(lines, "shx"), lines);
    }

    [Fact]
    public async Task Multipoints_make_a_multipoint_file_and_single_points_a_point_file()
    {
        var (multi, _) = await ToShapefileAsync(FeatureCollection(Feature("""{"type":"MultiPoint","coordinates":[[1,1],[2,2],[3,3]]}""")), "m.geojson");
        Assert.Equal(8, ParseShp(multi).Type);
        var (single, _) = await ToShapefileAsync(FeatureCollection(Feature("""{"type":"MultiPoint","coordinates":[[1,1]]}""")), "s.geojson");
        Assert.Equal(1, ParseShp(single).Type);
    }

    [Fact]
    public async Task Mixed_geometry_is_split_into_one_shapefile_per_kind_with_the_note()
    {
        var json = FeatureCollection(
            Feature("""{"type":"Point","coordinates":[1,1]}""", """{"a":1}"""),
            Feature("""{"type":"LineString","coordinates":[[0,0],[1,1]]}""", """{"a":2}"""),
            Feature("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""", """{"a":3}"""),
            Feature("""{"type":"GeometryCollection","geometries":[{"type":"Point","coordinates":[5,5]},{"type":"LineString","coordinates":[[2,2],[3,3]]}]}""", """{"a":4}"""),
            Feature("null", """{"a":5}"""));
        var job = await fx.ConvertAsync(GeoFixtures.WriteText(fx, "mixed.geojson", json), "shp");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.geo_shapefile_split", job.Files.Single().NoticeKey);
        var shapes = job.Outputs.Where(f => f.EndsWith(".shp", StringComparison.Ordinal)).OrderBy(f => f).ToList();
        Assert.Equal(["mixed_lines.shp", "mixed_points.shp", "mixed_polygons.shp"], shapes.Select(f => Path.GetFileName(f)!).ToArray());
        Assert.Equal(15, job.Outputs.Count());

        var lines = ParseShp(shapes[0]);
        Assert.Equal(3, lines.Type);
        Assert.Equal(2, lines.Records.Count); // the line and the line inside the collection
        var points = ParseShp(shapes[1]);
        Assert.Equal(1, points.Type);
        Assert.Equal(3, points.Records.Count); // the point, the point in the collection, and the feature without geometry (in the first file)
        Assert.Equal(0, BitConverter.ToInt32(points.Records[2], 0));
        Assert.Equal(5, ParseShp(shapes[2]).Type);
        foreach (var shp in shapes)
            CheckShx(Path.ChangeExtension(shp, "shx"), shp);
        // The attributes of a collection are repeated for each of its members.
        var dbf = ParseDbf(Path.ChangeExtension(shapes[0], "dbf"));
        Assert.Equal(["2", "4"], Enumerable.Range(0, dbf.Rows).Select(r => Cell(dbf, r, 0).Trim()).ToArray());
    }

    [Fact]
    public async Task Features_without_any_geometry_make_a_point_shapefile_of_null_shapes()
    {
        var (shp, _) = await ToShapefileAsync(FeatureCollection(Feature("null", """{"a":1}"""), Feature("null", """{"a":2}""")));
        var parsed = ParseShp(shp);
        Assert.Equal(2, parsed.Records.Count);
        Assert.All(parsed.Records, r => Assert.Equal(0, BitConverter.ToInt32(r, 0)));
        Assert.Equal(2, ParseDbf(Path.ChangeExtension(shp, "dbf")).Rows);
    }

    [Fact]
    public async Task Attribute_columns_get_the_dbf_type_that_holds_their_values()
    {
        var rows = new[]
        {
            """{"i":-12,"f":1.5,"b":true,"s":"text","mixed":1,"nums":2,"allnull":null,"big":123456789012,"tiny":0.0000001}""",
            """{"i":7,"f":-0.125,"b":false,"s":"länger","mixed":"x","nums":3.25,"allnull":null,"big":5,"tiny":2}""",
            """{"i":null,"f":null,"b":null,"s":null,"mixed":null,"nums":null,"allnull":null,"big":null,"tiny":null}""",
        };
        var (shp, _) = await ToShapefileAsync(FeatureCollection(rows.Select(r => Feature("""{"type":"Point","coordinates":[1,1]}""", r)).ToArray()));
        var dbf = ParseDbf(Path.ChangeExtension(shp, "dbf"));
        var fields = dbf.Fields.ToDictionary(f => f.Name);
        Assert.Equal(('N', 3, 0), (fields["i"].Type, fields["i"].Length, fields["i"].Decimals));
        Assert.Equal('N', fields["f"].Type);
        Assert.Equal(3, fields["f"].Decimals);
        Assert.Equal('L', fields["b"].Type);
        Assert.Equal(('C', 7), (fields["s"].Type, fields["s"].Length)); // "länger" is 7 bytes
        Assert.Equal('C', fields["mixed"].Type);
        Assert.Equal(('N', 2), (fields["nums"].Type, fields["nums"].Decimals));
        Assert.Equal(('C', 1), (fields["allnull"].Type, fields["allnull"].Length));
        Assert.Equal(('N', 12, 0), (fields["big"].Type, fields["big"].Length, fields["big"].Decimals));
        Assert.Equal(('N', 7), (fields["tiny"].Type, fields["tiny"].Decimals));

        var names = dbf.Fields.Select(f => f.Name).ToList();
        Assert.Equal("-12", Cell(dbf, 0, names.IndexOf("i")).Trim());
        Assert.Equal("-0.125", Cell(dbf, 1, names.IndexOf("f")).Trim());
        Assert.Equal("T", Cell(dbf, 0, names.IndexOf("b")));
        Assert.Equal("F", Cell(dbf, 1, names.IndexOf("b")));
        Assert.Equal("?", Cell(dbf, 2, names.IndexOf("b")));
        Assert.Equal("", Cell(dbf, 2, names.IndexOf("i")).Trim());
        Assert.Equal("länger", Cell(dbf, 1, names.IndexOf("s")).TrimEnd());
        Assert.Equal("0.0000001", Cell(dbf, 0, names.IndexOf("tiny")).Trim());
    }

    [Fact]
    public async Task Field_names_are_cut_to_ten_bytes_made_unique_and_cleaned_with_a_note()
    {
        var props = """{"population_2020":1,"population_2021":2,"population_2022":3,"my col!":4,"도시이름도시이름도시":5,"short":6,"Short":7,"":8}""";
        var job = await fx.ConvertAsync(GeoFixtures.WriteText(fx, "names.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""", props))), "shp");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.geo_field_names", job.Files.Single().NoticeKey);
        var dbf = ParseDbf(Path.ChangeExtension(job.Outputs.First(f => f.EndsWith(".shp", StringComparison.Ordinal)), "dbf"));
        var names = dbf.Fields.Select(f => f.Name).ToArray();
        Assert.Equal(["population", "populati_1", "populati_2", "my_col_", "도시이", "short", "Short_1", "FIELD"], names);
        Assert.All(names, n => Assert.True(Encoding.UTF8.GetByteCount(n) <= 10, n));
    }

    [Fact]
    public async Task Text_longer_than_254_bytes_is_cut_between_characters_with_a_note()
    {
        var korean = new string('한', 100); // 300 bytes
        var job = await fx.ConvertAsync(GeoFixtures.WriteText(fx, "long.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""", $$"""{"t":"{{korean}}","e":"{{new string('x', 300)}}"}"""))), "shp");
        Assert.Equal("notice.geo_text_cut", job.Files.Single().NoticeKey);
        var shp = job.Outputs.First(f => f.EndsWith(".shp", StringComparison.Ordinal));
        var dbf = ParseDbf(Path.ChangeExtension(shp, "dbf"));
        Assert.Equal(254, dbf.Fields[0].Length);
        Assert.Equal(254, dbf.Fields[1].Length);
        Assert.Equal(new string('한', 84), Cell(dbf, 0, 0).TrimEnd()); // 84 characters = 252 bytes; none broken
        Assert.Equal(new string('x', 254), Cell(dbf, 0, 1));
        // GDAL's view of the same bytes is in the cross-check run; here the engine reads its own output back.
        using var doc = await GeoJsonAsync(shp);
        Assert.Equal(new string('한', 84), doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("t").GetString());
    }

    [Fact]
    public async Task More_than_255_columns_keep_the_first_255_with_a_note()
    {
        var props = "{" + string.Join(',', Enumerable.Range(1, 300).Select(i => $"\"c{i}\":{i}")) + "}";
        var job = await fx.ConvertAsync(GeoFixtures.WriteText(fx, "wide.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""", props))), "shp");
        Assert.Equal("notice.geo_columns_dropped", job.Files.Single().NoticeKey);
        var dbf = ParseDbf(Path.ChangeExtension(job.Outputs.First(f => f.EndsWith(".shp", StringComparison.Ordinal)), "dbf"));
        Assert.Equal(255, dbf.Fields.Count);
        Assert.Equal("c255", dbf.Fields[^1].Name);
    }

    [Fact]
    public async Task Features_without_attributes_get_an_FID_column_because_a_dbf_needs_a_field()
    {
        var (shp, _) = await ToShapefileAsync(FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}"""), Feature("""{"type":"Point","coordinates":[2,2]}""")));
        var dbf = ParseDbf(Path.ChangeExtension(shp, "dbf"));
        Assert.Equal("FID", dbf.Fields.Single().Name);
        Assert.Equal(["0", "1"], Enumerable.Range(0, 2).Select(r => Cell(dbf, r, 0).Trim()).ToArray());
    }

    [Fact]
    public async Task Many_random_positions_survive_a_round_trip_at_full_precision()
    {
        var random = new Random(5);
        var points = Enumerable.Range(0, 5000).Select(_ => (X: random.NextDouble() * 360 - 180, Y: random.NextDouble() * 180 - 90)).ToList();
        var json = FeatureCollection([.. points.Select((p, i) => Feature($$"""{"type":"Point","coordinates":[{{p.X.ToString("R", CultureInfo.InvariantCulture)}},{{p.Y.ToString("R", CultureInfo.InvariantCulture)}}]}""", $$"""{"i":{{i}}}"""))]);
        var (shp, _) = await ToShapefileAsync(json, "random.geojson");
        using var doc = await GeoJsonAsync(shp);
        var features = doc.RootElement.GetProperty("features");
        Assert.Equal(points.Count, features.GetArrayLength());
        for (var i = 0; i < points.Count; i++)
        {
            var position = GeoFixtures.Position(features[i].GetProperty("geometry"));
            Assert.True(Math.Abs(position[0] - points[i].X) < 1e-9 && Math.Abs(position[1] - points[i].Y) < 1e-9, $"point {i}");
            Assert.Equal(i, features[i].GetProperty("properties").GetProperty("i").GetInt64());
        }
    }

    // ---- File names ----

    private static Action<Preset> WithConflict(ConflictPolicy policy) => preset => preset.Output.Conflict = policy;

    [Fact]
    public async Task Converting_twice_keeps_the_first_shapefile_and_names_the_second_set_consistently()
    {
        var input = GeoFixtures.WriteText(fx, "twice.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""", """{"a":1}""")));
        var first = await fx.ConvertOkAsync(input, "shp");
        var second = await fx.ConvertOkAsync(input, "shp");
        Assert.Equal(["twice.cpg", "twice.dbf", "twice.prj", "twice.shp", "twice.shx"], first.Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Equal(["twice (2).cpg", "twice (2).dbf", "twice (2).prj", "twice (2).shp", "twice (2).shx"], second.Select(f => Path.GetFileName(f)!).Order().ToArray());
        CheckShx(Path.ChangeExtension(second.First(f => f.EndsWith(".shp", StringComparison.Ordinal)), "shx"), second.First(f => f.EndsWith(".shp", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_leftover_sibling_file_is_never_overwritten_the_next_free_name_is_used()
    {
        var input = GeoFixtures.WriteText(fx, "orphan.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""")));
        var folder = Path.GetDirectoryName(input)!;
        File.WriteAllText(Path.Combine(folder, "orphan.dbf"), "someone else's file");
        var files = await fx.ConvertOkAsync(input, "shp");
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(folder, "orphan.dbf")));
        Assert.All(files, f => Assert.StartsWith("orphan_2.", Path.GetFileName(f)));
    }

    [Fact]
    public async Task The_overwrite_policy_replaces_the_whole_set_and_skip_leaves_it_alone()
    {
        var input = GeoFixtures.WriteText(fx, "policy.geojson", FeatureCollection(Feature("""{"type":"Point","coordinates":[1,1]}""", """{"a":1}""")));
        var first = await fx.ConvertOkAsync(input, "shp");
        var shp = first.First(f => f.EndsWith(".shp", StringComparison.Ordinal));
        File.WriteAllText(input, FeatureCollection(Feature("""{"type":"Point","coordinates":[2,2]}""", """{"a":1}"""), Feature("""{"type":"Point","coordinates":[3,3]}""", """{"a":2}""")));

        var skipped = await fx.ConvertAsync(input, "shp", WithConflict(ConflictPolicy.Skip));
        Assert.Equal(FileState.Skipped, skipped.Files.Single().State);
        Assert.Single(ParseShp(shp).Records);

        var replaced = await fx.ConvertOkAsync(input, "shp", WithConflict(ConflictPolicy.Overwrite));
        Assert.Equal(first.Order().ToArray(), replaced.Order().ToArray());
        Assert.Equal(2, ParseShp(shp).Records.Count);
        Assert.Equal(2, ParseDbf(Path.ChangeExtension(shp, "dbf")).Rows);
        CheckShx(Path.ChangeExtension(shp, "shx"), shp);
    }

    [Fact]
    public async Task A_failed_conversion_leaves_no_partial_files_behind()
    {
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "broken.geojson");
        File.WriteAllText(input, """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[1]}}]}""");
        var (_, _) = await fx.ConvertFailsAsync(input, "shp");
        Assert.Equal(["broken.geojson"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray());
    }
}
