// A Shapefile dragged on its own or with its companion files: "Compress into one archive" packs the whole set, a
// conversion of the set converts the .shp once without failing on the .dbf, .shx, ..., and the geospatial donut has the
// archive slice.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class GeoCompanionTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Preset ZipAll => BuiltInData.CreatePresets().Single(p => p.Id == "zip-all");

    /// <summary>The GDAL reference Shapefile with ESRI's index and metadata files beside it, as ArcGIS leaves them.</summary>
    private string ShapefileSet()
    {
        var shp = GeoFixtures.Copy(fx, "gdal_points", "shp");
        var folder = Path.GetDirectoryName(shp)!;
        File.WriteAllText(Path.Combine(folder, "gdal_points.sbn"), "index");
        File.WriteAllText(Path.Combine(folder, "gdal_points.sbx"), "index");
        File.WriteAllText(Path.Combine(folder, "gdal_points.shp.xml"), "<metadata/>");
        File.WriteAllText(Path.Combine(folder, "other.dbf"), "not part of it");
        return shp;
    }

    private static string[] Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private static readonly string[] Expected =
    [
        "gdal_points.cpg", "gdal_points.dbf", "gdal_points.prj", "gdal_points.sbn", "gdal_points.sbx", "gdal_points.shp",
        "gdal_points.shp.xml", "gdal_points.shx",
    ];

    [Fact]
    public async Task Compressing_a_dragged_shp_packs_its_companion_files_with_it()
    {
        var shp = ShapefileSet();
        var job = await fx.ConvertAsync([shp], ZipAll);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(Expected, Entries(job.Outputs.Single()));
    }

    [Fact]
    public async Task Compressing_a_shp_selected_with_its_companions_packs_each_file_once()
    {
        var shp = ShapefileSet();
        var folder = Path.GetDirectoryName(shp)!;
        string[] selection = [shp, .. new[] { "dbf", "shx", "prj", "cpg" }.Select(e => Path.Combine(folder, "gdal_points." + e))];

        var job = await fx.ConvertAsync(selection, ZipAll);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Single(job.Files); // the companions are part of the .shp
        Assert.Equal(Expected, Entries(job.Outputs.Single()));
    }

    [Fact]
    public async Task Two_shapefiles_and_another_file_go_into_one_archive_with_all_their_parts()
    {
        var a = ShapefileSet();
        var b = GeoFixtures.Copy(fx, "gdal_polygons", "shp");
        var note = GeoFixtures.WriteText(fx, "readme.txt", "two layers");

        var job = await fx.ConvertAsync([a, b, note], ZipAll);

        Assert.Equal(JobState.Completed, job.State);
        var entries = Entries(job.Outputs.Single());
        Assert.Equal(Expected.Length + 5 + 1, entries.Length);
        Assert.Contains("gdal_polygons.dbf", entries);
        Assert.Contains("readme.txt", entries);
        Assert.DoesNotContain("other.dbf", entries);
    }

    [Fact]
    public async Task Converting_a_shp_selected_with_its_companions_converts_it_once_without_failures()
    {
        var shp = ShapefileSet();
        var folder = Path.GetDirectoryName(shp)!;
        string[] selection = [shp, .. new[] { "dbf", "shx", "prj", "cpg", "sbn", "shp.xml" }.Select(e => Path.Combine(folder, "gdal_points." + e))];

        var job = await fx.ConvertAsync(selection, new Preset { TargetFormat = "geojson" });

        Assert.Equal(JobState.Completed, job.State);
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Done, file.State);
        using var doc = GeoFixtures.ReadJson(file.Outputs.Single());
        Assert.Equal(3, doc.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public void The_geospatial_donut_offers_compress_into_one_archive()
    {
        Assert.Contains("zip-all", BuiltInData.GeoProfile().BackPresetIds);
    }
}
