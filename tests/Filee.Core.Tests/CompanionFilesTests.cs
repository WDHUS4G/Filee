// A Shapefile's companion files: found next to the .shp in any letter case and in a fixed order, ignored for other
// formats, and taken out of a selection that also holds their .shp (so they don't count as files of their own).

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public class CompanionFilesTests
{
    [Fact]
    public void A_shapefile_has_the_files_of_its_name_that_belong_to_it_in_a_fixed_order()
    {
        using var dir = new TempDir();
        var shp = dir.File("roads.shp");
        foreach (var name in new[] { "roads.prj", "roads.DBF", "roads.shx", "roads.cpg", "roads.sbn", "roads.sbx", "roads.shp.xml", "roads.qix" })
            dir.File(name);
        // Not companions: another dataset, a name that only starts the same, an unrelated extension, a file of the name.
        foreach (var name in new[] { "roads2.dbf", "roads.old.dbf", "roads.txt", "roads.zip", "roadsdbf" })
            dir.File(name);

        var companions = CompanionFiles.Of(shp).Select(f => Path.GetFileName(f)!).ToArray();

        Assert.Equal(["roads.shx", "roads.DBF", "roads.prj", "roads.cpg", "roads.sbn", "roads.sbx", "roads.qix", "roads.shp.xml"], companions);
    }

    [Fact]
    public void Other_formats_and_missing_folders_have_no_companions()
    {
        using var dir = new TempDir();
        dir.File("roads.dbf");
        Assert.Empty(CompanionFiles.Of(dir.File("roads.geojson")));
        Assert.Empty(CompanionFiles.Of(Path.Combine(dir.Path, "missing", "roads.shp")));
        Assert.Empty(CompanionFiles.Of(dir.File("lonely.shp")));
    }

    [Fact]
    public void Companions_selected_with_their_shapefile_are_taken_out_and_alone_they_stay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "x");
        string F(string name) => Path.Combine(folder, name);
        string[] selection = [F("roads.shp"), F("ROADS.DBF"), F("roads.shx"), F("roads.prj"), F("roads.shp.xml"), F("rivers.dbf"), F("photo.jpg")];

        Assert.Equal([F("roads.shp"), F("rivers.dbf"), F("photo.jpg")], CompanionFiles.WithoutCompanions(selection));
        Assert.Equal([F("roads.dbf")], CompanionFiles.WithoutCompanions([F("roads.dbf")])); // no .shp: a file like any other
        // Web addresses (files still in a browser) are not files on disk: left as they are.
        string[] web = [F("roads.shp"), "https://example.com/data/roads.dbf", "https://example.com/data/roads.shp"];
        Assert.Equal(web, CompanionFiles.WithoutCompanions(web));
        Assert.Empty(CompanionFiles.Of("https://example.com/data/roads.shp"));
        // The same name in another folder is another dataset.
        string[] two = [F("roads.shp"), Path.Combine(folder, "sub", "roads.dbf")];
        Assert.Equal(two, CompanionFiles.WithoutCompanions(two));
    }

    [Fact]
    public void A_job_and_the_donut_count_a_dropped_shapefile_set_as_one_file()
    {
        using var dir = new TempDir();
        string[] dropped = [dir.File("roads.shp"), dir.File("roads.dbf"), dir.File("roads.shx"), dir.File("roads.prj")];

        var job = new ConversionJob(dropped, new Preset { TargetFormat = "geojson" }, "GeoJSON");
        Assert.Equal([dropped[0]], job.Sources);
        Assert.Single(job.Files);

        // Without the companions the selection is a .shp, so the geospatial donut opens, not the one for mixed files.
        var profiles = BuiltInData.CreateProfiles();
        Assert.Equal("mixed", ProfileSelector.Select(profiles, dropped)!.Id);
        Assert.Equal("geo", ProfileSelector.Select(profiles, CompanionFiles.WithoutCompanions(dropped))!.Id);
    }
}
