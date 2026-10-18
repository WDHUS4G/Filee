// Which donut opens for a set of files: normal profiles first, then mixed-selection profiles limited to checked
// extensions, then the catch-all.

using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public class ProfileSelectorTests
{
    private readonly List<ToolbarProfile> _builtIn = BuiltInData.CreateProfiles();

    private static ToolbarProfile Normal(string id, params string[] extensions) => new() { Id = id, Extensions = [.. extensions] };

    private static ToolbarProfile Mixed(string id, params string[] extensions) =>
        new() { Id = id, IsFallback = true, Extensions = [.. extensions] };

    private static string? Pick(IReadOnlyList<ToolbarProfile> profiles, params string[] files) =>
        ProfileSelector.Select(profiles, files)?.Id;

    [Fact]
    public void Picks_profile_matching_all_files() => Assert.Equal("images", Pick(_builtIn, "a.jpg", "b.PNG"));

    [Theory]
    [InlineData("pdf", "a.pdf")]
    [InlineData("archives", "a.tar.gz", "b.zip")]
    [InlineData("video", @"C:\clips\a.MP4", @"C:\clips\b.mkv")]
    [InlineData("text", "notes.md", "readme.txt")]
    [InlineData("text", "todo.org", "paper.typ", "guide.adoc")]
    [InlineData("ebooks", "novel.kepub.epub", "novel.epub")]
    [InlineData("office", "letter.fodt", "memo.602")]
    [InlineData("spreadsheets", "budget.xlt", "flat.fods")]
    [InlineData("data", "a.json", "b.YML", "c.toml", "d.xml")]
    [InlineData("data", "contacts.vcf")]
    [InlineData("spreadsheets", "table.csv")]
    [InlineData("office", "mail.msg", "mail.eml")]
    public void Built_in_profiles_take_their_files(string expected, params string[] files) =>
        Assert.Equal(expected, Pick(_builtIn, files));

    [Fact]
    public void Mixed_files_use_the_fallback() => Assert.Equal("mixed", Pick(_builtIn, "a.jpg", "b.docx"));

    [Fact]
    public void Unknown_files_use_the_fallback() => Assert.Equal("mixed", Pick(_builtIn, "a.xyz"));

    [Fact]
    public void No_files_use_the_fallback() => Assert.Equal("mixed", Pick(_builtIn));

    [Fact]
    public void Folders_and_files_without_extension_use_the_fallback()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Mixed("mixed"), Mixed("jpg-and-more", "jpg", "pdf")];
        Assert.Equal("mixed", Pick(profiles, @"C:\photos", "a.jpg"));
    }

    [Fact]
    public void A_mixed_profile_takes_a_selection_within_its_checked_extensions()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg", "png"), Normal("pdf", "pdf"), Mixed("mixed"), Mixed("scans", "jpg", "png", "pdf")];

        Assert.Equal("scans", Pick(profiles, "a.jpg", "b.pdf"));
        Assert.Equal("scans", Pick(profiles, "a.jpg", "b.png", "c.pdf"));
    }

    [Fact]
    public void Every_file_must_be_checked_on_the_mixed_profile()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("pdf", "pdf"), Normal("office", "docx"), Mixed("mixed"), Mixed("scans", "jpg", "pdf")];
        Assert.Equal("mixed", Pick(profiles, "a.jpg", "b.pdf", "c.docx"));
    }

    [Fact]
    public void A_normal_profile_wins_over_a_mixed_one_listing_the_same_extensions()
    {
        // Single-type selections are unchanged: the mixed profile only takes what no normal profile takes.
        List<ToolbarProfile> profiles = [Mixed("scans", "jpg", "pdf"), Normal("images", "jpg"), Mixed("mixed")];
        Assert.Equal("images", Pick(profiles, "a.jpg", "b.JPG"));

        // A normal profile holding every extension of the selection wins as well.
        profiles.Insert(0, Normal("mine", "jpg", "pdf"));
        Assert.Equal("mine", Pick(profiles, "a.jpg", "b.pdf"));
    }

    [Fact]
    public void The_first_matching_mixed_profile_in_list_order_wins()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("pdf", "pdf"), Mixed("broad", "jpg", "png", "pdf", "docx"), Mixed("narrow", "jpg", "pdf")];
        Assert.Equal("broad", Pick(profiles, "a.jpg", "b.pdf"));

        profiles.Reverse();
        Assert.Equal("narrow", Pick(profiles, "a.jpg", "b.pdf"));
    }

    [Fact]
    public void The_catch_all_position_does_not_matter_for_limited_profiles()
    {
        List<ToolbarProfile> profiles = [Mixed("mixed"), Normal("images", "jpg"), Normal("pdf", "pdf"), Mixed("scans", "jpg", "pdf")];
        Assert.Equal("scans", Pick(profiles, "a.jpg", "b.pdf"));
    }

    [Fact]
    public void Checked_extensions_are_compared_without_case_and_dots()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("pdf", "pdf"), Mixed("mixed"), Mixed("scans", "JPG", "Pdf")];
        Assert.Equal("scans", Pick(profiles, "A.Jpg", "b.PDF"));
    }

    [Fact]
    public void Two_part_extensions_are_matched_as_a_whole()
    {
        List<ToolbarProfile> profiles = [Normal("archives", "zip", "tar.gz"), Normal("images", "png"), Mixed("mixed"), Mixed("backup", "tar.gz", "png")];

        Assert.Equal("backup", Pick(profiles, "site.tar.gz", "logo.png"));
        Assert.Equal("mixed", Pick(profiles, "site.gz", "logo.png")); // plain .gz is not .tar.gz
    }

    [Fact]
    public void Unknown_extensions_can_be_checked_on_a_mixed_profile()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Mixed("mixed"), Mixed("raw-and-sidecar", "jpg", "xmp")];

        Assert.Equal("raw-and-sidecar", Pick(profiles, "a.jpg", "a.xmp"));
        Assert.Equal("raw-and-sidecar", Pick(profiles, "a.xmp")); // no normal profile takes .xmp
    }

    [Fact]
    public void A_normal_profile_without_extensions_is_never_picked()
    {
        List<ToolbarProfile> profiles = [Normal("new"), Normal("images", "jpg"), Mixed("mixed")];

        Assert.Equal("images", Pick(profiles, "a.jpg"));
        Assert.Equal("mixed", Pick(profiles, "a.xyz"));
    }

    [Fact]
    public void The_catch_all_is_the_first_mixed_profile_with_nothing_checked()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Mixed("scans", "jpg", "pdf"), Mixed("mixed"), Mixed("second")];

        Assert.Equal("mixed", Pick(profiles, "a.jpg", "b.docx"));
        Assert.Equal("mixed", ProfileSelector.CatchAll(profiles)!.Id);
    }

    [Fact]
    public void Without_a_catch_all_the_first_mixed_profile_takes_the_rest()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("office", "docx"), Mixed("scans", "jpg", "pdf"), Mixed("office-mix", "docx", "xlsx")];
        Assert.Equal("scans", Pick(profiles, "a.jpg", "b.docx"));
    }

    [Fact]
    public void Without_any_mixed_profile_the_first_profile_is_used()
    {
        List<ToolbarProfile> profiles = [Normal("images", "jpg"), Normal("office", "docx")];
        Assert.Equal("images", Pick(profiles, "a.jpg", "b.docx"));
    }

    [Fact]
    public void Nothing_to_pick_from_is_null() => Assert.Null(Pick([], "a.jpg"));
}
