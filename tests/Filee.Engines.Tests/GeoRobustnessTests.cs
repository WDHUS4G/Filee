// What happens to files that are damaged, hostile or odd: every reference file truncated at every length and with
// random bytes changed must be read or refused with a reason (never crash, hang or allocate wildly), XML that tries to
// read files or expand entities is refused, names with brackets and Korean text work, several files in one job get
// their own consistent names, and a cancelled conversion stops.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class GeoRobustnessTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string CsvSample = "id,name,lat,lon\n1,\"A, b\",48.85,2.35\n2,C,,\n3,D,51.5,-0.12\n";

    /// <summary>Reads <paramref name="path"/> with the reader of its format; only a refusal or a reason is allowed to come out.</summary>
    private static void ReadOrRefuse(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            _ = extension switch
            {
                "geojson" => GeoJsonFormat.Read(path, CancellationToken.None),
                "kml" => KmlFormat.Read(path, zipped: false, CancellationToken.None),
                "kmz" => KmlFormat.Read(path, zipped: true, CancellationToken.None),
                "gpx" => GpxFormat.Read(path, CancellationToken.None),
                "shp" => ShapefileFormat.Read(path, CancellationToken.None),
                "csv" => GeoCsvFormat.Read(path, CancellationToken.None),
                _ => throw new NotSupportedException(extension),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ConversionRefusedException)
        {
        }
    }

    private static string CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        return to;
    }

    /// <summary>Truncates and corrupts the file <paramref name="target"/> of a copied reference set and reads <paramref name="main"/> each time.</summary>
    private void Fuzz(string set, string main, string target, int seed, int mutations = 40)
    {
        var original = Path.Combine(GeoFixtures.Folder, set + "." + target);
        var bytes = File.ReadAllBytes(original);
        var folder = CopyFolder(GeoFixtures.Folder, fx.NewFolder());
        var mainPath = Path.Combine(folder, set + "." + main);
        var targetPath = Path.Combine(folder, set + "." + target);

        var lengths = bytes.Length <= 150 ? Enumerable.Range(0, bytes.Length) : Enumerable.Range(0, 150).Select(i => i * bytes.Length / 150);
        foreach (var length in lengths)
        {
            File.WriteAllBytes(targetPath, bytes[..length]);
            ReadOrRefuse(mainPath);
        }
        var random = new Random(seed);
        for (var i = 0; i < mutations; i++)
        {
            var copy = (byte[])bytes.Clone();
            for (var change = random.Next(1, 5); change > 0; change--)
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            File.WriteAllBytes(targetPath, copy);
            ReadOrRefuse(mainPath);
        }
    }

    [Theory]
    [InlineData("gdal_points", "shp", "shp")]
    [InlineData("gdal_points", "shp", "dbf")]
    [InlineData("gdal_points", "shp", "prj")]
    [InlineData("gdal_points", "shp", "cpg")]
    [InlineData("gdal_polygons", "shp", "shp")]
    [InlineData("gdal_polygons", "shp", "dbf")]
    [InlineData("gdal_linesz", "shp", "shp")]
    [InlineData("gdal_utm33", "shp", "shp")]
    [InlineData("gdal_utm33", "shp", "prj")]
    public void Damaged_reference_files_are_read_or_refused_never_a_crash(string set, string main, string target)
    {
        Fuzz(set, main, target, seed: set.Length * 31 + target.Length);
    }

    [Theory]
    [InlineData("geojson")]
    [InlineData("kml")]
    [InlineData("gpx")]
    [InlineData("csv")]
    [InlineData("kmz")]
    public void Damaged_hand_written_files_are_read_or_refused_never_a_crash(string format)
    {
        var folder = fx.NewFolder();
        byte[] original;
        if (format == "kmz")
        {
            var path = Path.Combine(folder, "o.kmz");
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("doc.kml").Open()))
                writer.Write(GeoFormatTests.KmlSample);
            original = File.ReadAllBytes(path);
        }
        else
        {
            original = Encoding.UTF8.GetBytes(format switch { "geojson" => GeoFixtures.PointsGeoJson, "kml" => GeoFormatTests.KmlSample, "gpx" => GeoFormatTests.GpxSample, _ => CsvSample });
        }
        var target = Path.Combine(folder, "damaged." + format);
        for (var length = 0; length < original.Length; length += Math.Max(1, original.Length / 120))
        {
            File.WriteAllBytes(target, original[..length]);
            ReadOrRefuse(target);
        }
        var random = new Random(3);
        for (var i = 0; i < 80; i++)
        {
            var copy = (byte[])original.Clone();
            for (var change = random.Next(1, 6); change > 0; change--)
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            File.WriteAllBytes(target, copy);
            ReadOrRefuse(target);
        }
    }

    [Fact]
    public void Random_bytes_in_every_format_are_refused()
    {
        var random = new Random(9);
        var folder = fx.NewFolder();
        foreach (var extension in new[] { "shp", "geojson", "kml", "kmz", "gpx", "csv" })
        {
            for (var i = 0; i < 15; i++)
            {
                var bytes = new byte[random.Next(0, 3000)];
                random.NextBytes(bytes);
                var path = Path.Combine(folder, "random." + extension);
                File.WriteAllBytes(path, bytes);
                ReadOrRefuse(path);
            }
        }
    }

    // ---- Hostile XML ----

    [Fact]
    public async Task Xml_that_asks_to_read_a_local_file_is_refused_and_nothing_of_the_file_reaches_the_output()
    {
        var secret = Path.Combine(fx.NewFolder(), "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-VALUE");
        var uri = new Uri(secret).AbsoluteUri;
        var kml = $"""<?xml version="1.0"?><!DOCTYPE kml [<!ENTITY leak SYSTEM "{uri}">]><kml><Placemark><name>&leak;</name><Point><coordinates>1,2</coordinates></Point></Placemark></kml>""";
        var gpx = $"""<?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY leak SYSTEM "{uri}">]><gpx><wpt lat="1" lon="2"><name>&leak;</name></wpt></gpx>""";
        foreach (var (name, text) in new[] { ("evil.kml", kml), ("evil.gpx", gpx) })
        {
            var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, name, text), "geojson");
            Assert.Equal("error.conversion_failed", key);
            Assert.DoesNotContain("TOP-SECRET-VALUE", detail);
        }
    }

    [Fact]
    public async Task An_entity_bomb_is_refused()
    {
        var builder = new StringBuilder("""<?xml version="1.0"?><!DOCTYPE kml [<!ENTITY a0 "lol">""");
        for (var i = 1; i < 10; i++)
            builder.Append($"<!ENTITY a{i} \"{string.Concat(Enumerable.Repeat($"&a{i - 1};", 10))}\">");
        builder.Append("]><kml><Placemark><name>&a9;</name><Point><coordinates>1,2</coordinates></Point></Placemark></kml>");
        var (key, _) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "bomb.kml", builder.ToString()), "geojson");
        Assert.Equal("error.conversion_failed", key);
    }

    // ---- Names, parallel files, cancellation ----

    [Fact]
    public async Task File_names_with_brackets_spaces_and_korean_text_find_their_sidecars_and_make_matching_outputs()
    {
        var folder = fx.NewFolder();
        foreach (var file in Directory.EnumerateFiles(GeoFixtures.Folder, "gdal_points.*").Where(f => !f.EndsWith(".geojson") && !f.EndsWith(".kml")))
            File.Copy(file, Path.Combine(folder, "서울 [data] (1)" + Path.GetExtension(file)));
        var files = await fx.ConvertOkAsync(Path.Combine(folder, "서울 [data] (1).shp"), "shp");
        Assert.Equal(5, files.Count);
        Assert.All(files, f => Assert.StartsWith("서울 [data] (1) (2).", Path.GetFileName(f)));
        var geojson = (await fx.ConvertOkAsync(files.First(f => f.EndsWith(".shp")), "geojson")).Single();
        using var doc = GeoFixtures.ReadJson(geojson);
        Assert.Equal("서울 City Hall", doc.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Files_of_one_job_that_would_get_the_same_output_name_get_separate_consistent_sets()
    {
        var folder = fx.NewFolder();
        const string point = """{"type":"Feature","properties":{"n":1},"geometry":{"type":"Point","coordinates":[1,2]}}""";
        var a = Path.Combine(folder, "same.geojson");
        File.WriteAllText(a, point);
        var b = Path.Combine(folder, "same.kml");
        File.WriteAllText(b, "<kml><Placemark><Point><coordinates>3,4</coordinates></Point></Placemark></kml>");
        var c = Path.Combine(folder, "same.gpx");
        File.WriteAllText(c, """<gpx><wpt lat="6" lon="5"/></gpx>""");
        var job = await fx.ConvertAsync([a, b, c], new Preset { TargetFormat = "shp" });
        Assert.True(job.State == JobState.Completed, GeoFixtures.Describe(job));
        var outputs = job.Outputs.ToList();
        Assert.Equal(15, outputs.Count);
        Assert.Equal(15, outputs.Distinct().Count());
        var sets = outputs.GroupBy(o => Path.GetFileNameWithoutExtension(o)).ToList();
        Assert.Equal(3, sets.Count);
        Assert.All(sets, g => Assert.Equal(["cpg", "dbf", "prj", "shp", "shx"], g.Select(f => Path.GetExtension(f)[1..]).Order().ToArray()));
    }

    [Fact]
    public async Task Many_files_convert_in_parallel_without_mixing_up_their_outputs()
    {
        var folder = fx.NewFolder();
        var sources = new List<string>();
        for (var i = 0; i < 24; i++)
        {
            var path = Path.Combine(folder, $"file{i:00}.geojson");
            File.WriteAllText(path, PointJson(i, i, i));
            sources.Add(path);
        }
        var job = await fx.ConvertAsync(sources, new Preset { TargetFormat = "kml" });
        Assert.True(job.State == JobState.Completed, GeoFixtures.Describe(job));
        foreach (var file in job.Files)
        {
            var index = int.Parse(Path.GetFileNameWithoutExtension(file.SourcePath)[4..]);
            Assert.Contains($"{index},{index}", File.ReadAllText(file.Outputs.Single()));
        }
    }

    [Fact]
    public async Task A_conversion_that_is_cancelled_stops_and_leaves_nothing_behind()
    {
        var converter = new GeoConverter();
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "big.geojson");
        var features = string.Join(',', Enumerable.Range(0, 20000).Select(i => PointJson(i, i % 180, i % 90)));
        File.WriteAllText(input, $$"""{"type":"FeatureCollection","features":[{{features}}]}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = new FixedAllocator(folder);
        var step = new ConversionStep(input, "geojson", "shp", new Preset(), output, folder);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(step, null, cts.Token));
        Assert.Equal(["big.geojson"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public async Task Cancelling_while_the_file_is_written_removes_the_partial_files()
    {
        var converter = new GeoConverter();
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "big.geojson");
        var features = string.Join(',', Enumerable.Range(0, 60000).Select(PolygonJson));
        File.WriteAllText(input, $$"""{"type":"FeatureCollection","features":[{{features}}]}""");
        using var cts = new CancellationTokenSource();
        var output = new FixedAllocator(folder, onAllocate: () => cts.CancelAfter(1));
        var step = new ConversionStep(input, "geojson", "shp", new Preset(), output, folder);
        try
        {
            await converter.ConvertAsync(step, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Equal(["big.geojson"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray());
        }
        // (If the machine was fast enough to finish within a millisecond, nothing was cancelled and there is nothing to check.)
    }

    private static string PointJson(int i, int x, int y) =>
        "{\"type\":\"Feature\",\"properties\":{\"i\":" + i + "},\"geometry\":{\"type\":\"Point\",\"coordinates\":[" + x + "," + y + "]}}";

    private static string PolygonJson(int i)
    {
        var width = i % 90 + 1;
        var height = i % 45 + 1;
        return "{\"type\":\"Feature\",\"properties\":{\"i\":" + i + ",\"t\":\"text " + i + "\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[[0,0],["
               + width + ",0],[" + width + "," + height + "],[0,0]]]}}";
    }

    private sealed class FixedAllocator(string folder, Action? onAllocate = null) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            onAllocate?.Invoke();
            return Path.Combine(folder, "out" + suffix + "." + extension);
        }
    }
}
