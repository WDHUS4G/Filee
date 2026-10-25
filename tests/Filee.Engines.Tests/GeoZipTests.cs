// "Save as ZIP" on geospatial presets: the result of each file is one ZIP in place of loose files, named by the output
// rule (conflict policy included), with the files of a Shapefile together under matching names, and nothing left over
// when a conversion fails.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Engines.Tests;

public class GeoZipTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Action<Preset> Zip => preset => preset.Output.ZipResult = true;

    private static string[] Entries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName).Order()];
    }

    private static string[] Names(string folder) => [.. Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order()];

    [Fact]
    public async Task A_shapefile_becomes_one_zip_with_its_five_files_and_nothing_else()
    {
        var input = GeoFixtures.Points(fx, "geojson");
        var folder = Path.GetDirectoryName(input)!;
        var before = Names(folder);
        var outputs = await fx.ConvertOkAsync(input, "shp", Zip);

        var zip = Assert.Single(outputs);
        Assert.EndsWith(".zip", zip);
        Assert.Equal(["points.cpg", "points.dbf", "points.prj", "points.shp", "points.shx"], Entries(zip));
        // The folder gained the ZIP and no loose files: the Shapefile files that were there already are the fixture's own.
        Assert.Equal([.. before.Append("points.zip").Order()], Names(folder));
    }

    [Fact]
    public async Task The_zipped_shapefile_is_the_same_shapefile_as_the_loose_one()
    {
        var input = GeoFixtures.Points(fx, "geojson");
        var loose = await fx.ConvertOkAsync(input, "shp");
        var zip = (await fx.ConvertOkAsync(input, "shp", Zip)).Single();

        using var archive = ZipFile.OpenRead(zip);
        foreach (var file in loose)
        {
            var entry = archive.Entries.Single(e => e.Name.EndsWith(Path.GetExtension(file), StringComparison.Ordinal));
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            // The .dbf holds the date it was written: compare everything after the fixed header's date bytes.
            var expected = File.ReadAllBytes(file);
            var actual = memory.ToArray();
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.AsSpan(4).ToArray(), actual.AsSpan(4).ToArray());
        }
    }

    [Fact]
    public async Task Single_file_results_are_zipped_too_and_mixed_geometry_puts_all_three_sets_in_one_zip()
    {
        var geojson = GeoFixtures.Copy(fx, "gdal_points", "shp");
        var zip = (await fx.ConvertOkAsync(geojson, "geojson", Zip)).Single();
        Assert.Equal(["gdal_points.geojson"], Entries(zip));

        const string mixed = """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[1,1]}},{"type":"Feature","properties":{},"geometry":{"type":"LineString","coordinates":[[0,0],[1,1]]}},{"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}]}""";
        var sets = (await fx.ConvertOkAsync(GeoFixtures.WriteText(fx, "mixed.geojson", mixed), "shp", Zip)).Single();
        Assert.Equal(15, Entries(sets).Length);
        Assert.Equal(3, Entries(sets).Select(Path.GetFileNameWithoutExtension).Distinct().Count());
    }

    [Fact]
    public async Task Each_source_gets_its_own_zip_in_one_job()
    {
        var a = GeoFixtures.Copy(fx, "gdal_points", "shp");
        var b = GeoFixtures.Copy(fx, "gdal_polygons", "shp");
        var job = await fx.ConvertAsync([a, b], new Preset { TargetFormat = "kml", Output = { ZipResult = true } });
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["gdal_points.zip", "gdal_polygons.zip"], job.Outputs.Select(o => Path.GetFileName(o)!).Order().ToArray());
        Assert.All(job.Outputs, o => Assert.Single(Entries(o)));
    }

    [Fact]
    public async Task The_conflict_policy_applies_to_the_zip()
    {
        var input = GeoFixtures.Points(fx, "geojson");
        var first = (await fx.ConvertOkAsync(input, "shp", Zip)).Single();
        var second = (await fx.ConvertOkAsync(input, "shp", Zip)).Single();
        Assert.Equal("points.zip", Path.GetFileName(first));
        Assert.Equal("points (2).zip", Path.GetFileName(second));

        var length = new FileInfo(first).Length;
        File.WriteAllText(first, "stale");
        var skipped = await fx.ConvertAsync(input, "shp", preset => { preset.Output.ZipResult = true; preset.Output.Conflict = ConflictPolicy.Skip; });
        Assert.Equal(FileState.Skipped, skipped.Files.Single().State);
        Assert.Equal("stale", File.ReadAllText(first));

        var replaced = (await fx.ConvertOkAsync(input, "shp", preset => { preset.Output.ZipResult = true; preset.Output.Conflict = ConflictPolicy.Overwrite; })).Single();
        Assert.Equal(first, replaced);
        Assert.Equal(length, new FileInfo(first).Length);
        Assert.Equal(5, Entries(first).Length);
    }

    [Fact]
    public async Task The_name_pattern_names_the_zip()
    {
        var input = GeoFixtures.Points(fx, "geojson");
        var zip = (await fx.ConvertOkAsync(input, "shp", preset =>
        {
            preset.Output.ZipResult = true;
            preset.Output.FileNamePattern = "{name}_wgs84";
        })).Single();
        Assert.Equal("points_wgs84.zip", Path.GetFileName(zip));
        Assert.Contains("points.shp", Entries(zip)); // the files inside keep the source's name
    }

    [Fact]
    public async Task A_failed_conversion_leaves_no_zip()
    {
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "broken.geojson");
        File.WriteAllText(input, """{"type":"Point","coordinates":[1]}""");
        var job = await fx.ConvertAsync(input, "shp", Zip);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(["broken.geojson"], Names(folder));
    }

    [Fact]
    public async Task The_zip_is_the_only_output_when_a_route_has_several_steps()
    {
        // Shapefile → CSV → JSON is not geospatial output, but the option still packs whatever the last step writes.
        var shp = GeoFixtures.Copy(fx, "gdal_points", "shp");
        var zip = (await fx.ConvertOkAsync(shp, "json", Zip)).Single();
        Assert.Equal(["gdal_points.json"], Entries(zip));
    }

    [Fact]
    public void The_app_wide_setting_zips_geospatial_targets_only()
    {
        var settings = new AppSettings { ZipGeoResults = true };
        Assert.True(settings.WithDefaultOutput(new Preset { TargetFormat = "shp" }).Output.ZipResult);
        Assert.True(settings.WithDefaultOutput(new Preset { TargetFormat = "geojson", Output = { Location = OutputLocation.SameFolder } }).Output.ZipResult);
        var png = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.SameFolder } };
        Assert.Same(png, settings.WithDefaultOutput(png));
        var own = new Preset { TargetFormat = "kml", Output = { Location = OutputLocation.SameFolder } };
        Assert.True(settings.WithDefaultOutput(own).Output.ZipResult);
        Assert.False(own.Output.ZipResult); // a copy: the saved preset is unchanged
        Assert.Same(own, new AppSettings().WithDefaultOutput(own));
    }
}
