// Logic behind the donut editor's extension chips and the mixed-selection check list (no UI needed).

using Filee.App.Controls;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Profiles;

namespace Filee.App.Tests;

/// <summary>Returns keys (and "key|arg|arg" for formats) so tests can check which message was chosen.</summary>
internal sealed class KeyLocalizer : ILocalizer
{
    public string Language => "en";
    public string this[string key] => key;
    public string Format(string key, params object[] args) => string.Join("|", [key, .. args.Select(a => a.ToString())]);
    public event EventHandler? LanguageChanged { add { } remove { } }
}

public class ExtensionInputTests
{
    [Theory]
    [InlineData("jpg", "jpg")]
    [InlineData(" .JPG ", "jpg")]
    [InlineData("*.png", "png")]
    [InlineData("**..webp", "webp")]
    [InlineData("*.TAR.GZ", "tar.gz")]
    [InlineData("tar.xz", "tar.xz")]
    [InlineData("dvr-ms", "dvr-ms")]
    [InlineData("xyz", "xyz")]
    [InlineData("jpg.", "jpg")]
    public void Normalizes_extensions(string input, string expected) => Assert.Equal(expected, ExtensionInput.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*.")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData(@"C:\x")]
    [InlineData("a..b")]
    [InlineData("photo.jpg")] // two-part extensions only when Filee knows them
    [InlineData("averyveryverylongextension")]
    public void Rejects_what_cannot_be_an_extension(string input) => Assert.Null(ExtensionInput.Normalize(input));

    [Fact]
    public void Suggestions_start_with_the_typed_extension()
    {
        var suggestions = ExtensionInput.Suggest("jp", [], _ => null, new KeyLocalizer());

        Assert.Equal(".jpg", suggestions[0].Text);
        Assert.All(suggestions.Take(3), s => Assert.StartsWith("jp", s.Value));
        Assert.Contains("JPG", suggestions[0].Detail);
        Assert.Contains("format.category.Image", suggestions[0].Detail);
    }

    [Fact]
    public void An_exact_match_comes_first() =>
        Assert.Equal("tar", ExtensionInput.Suggest(".tar", [], _ => null, new KeyLocalizer())[0].Value);

    [Fact]
    public void Suggestions_leave_out_existing_extensions_and_name_the_owner()
    {
        var suggestions = ExtensionInput.Suggest("jp", ["jpg"], e => e == "jpeg" ? "Images" : null, new KeyLocalizer());

        Assert.DoesNotContain(suggestions, s => s.Value == "jpg");
        Assert.Equal("toolbar.ext_in_profile|Images", suggestions.Single(s => s.Value == "jpeg").Note);
    }

    [Fact]
    public void Format_names_and_categories_are_searched_too()
    {
        var loc = new KeyLocalizer();
        Assert.Contains(ExtensionInput.Suggest("JPEG XL", [], _ => null, loc), s => s.Value == "jxl");
        Assert.Contains(ExtensionInput.Suggest("markdown", [], _ => null, loc), s => s.Value == "md");

        var video = ExtensionInput.Suggest("video", [], _ => null, loc, max: 50);
        Assert.Contains(video, s => s.Value == "mp4");
        Assert.Contains(video, s => s.Value == "mkv");
    }

    [Fact]
    public void Nothing_typed_nothing_suggested() => Assert.Empty(ExtensionInput.Suggest(" ", [], _ => null, new KeyLocalizer()));

    [Fact]
    public void Suggestions_are_limited() => Assert.Equal(8, ExtensionInput.Suggest("m", [], _ => null, new KeyLocalizer()).Count);
}

public class ExtensionTagsViewModelTests
{
    private readonly ToolbarProfile _images = new() { Id = "images", Name = "Images", Extensions = ["jpg", "png"] };
    private readonly ToolbarProfile _mine = new() { Id = "mine", Name = "Mine", Extensions = ["webp"] };
    private readonly ToolbarProfile _later = new() { Id = "later", Name = "Later", Extensions = ["gif"] };
    private readonly ToolbarProfile _mixed = new() { Id = "mixed", Name = "Mixed", IsFallback = true, Extensions = ["bmp"] };
    private int _saves;

    private ExtensionTagsViewModel Create() => new(_mine, [_images, _mine, _later, _mixed], new KeyLocalizer(), () => _saves++);

    [Fact]
    public void Adds_normalized_extensions_and_saves()
    {
        var vm = Create();
        vm.AddCommand.Execute(" *.AVIF ");

        Assert.Equal(["webp", "avif"], _mine.Extensions);
        Assert.Equal(1, _saves);
        Assert.Equal([".webp", ".avif"], vm.Tags.Select(t => t.Text));
        Assert.All(vm.Tags, t => Assert.Equal(TagChipKind.Normal, t.Kind));
        Assert.Null(vm.InputError);
    }

    [Fact]
    public void Refuses_duplicates_and_invalid_text()
    {
        var vm = Create();
        vm.AddCommand.Execute(".WEBP");
        Assert.Equal("toolbar.ext_duplicate|.webp", vm.InputError);

        vm.AddCommand.Execute("a/b");
        Assert.Equal("toolbar.ext_invalid|a/b", vm.InputError);

        Assert.Equal(["webp"], _mine.Extensions);
        Assert.Equal(0, _saves);

        vm.Text = "g"; // typing again dismisses the message
        Assert.Null(vm.InputError);
    }

    [Fact]
    public void Warns_when_an_earlier_profile_opens_those_files()
    {
        var vm = Create();
        vm.AddCommand.Execute("jpg");
        vm.AddCommand.Execute("png");

        Assert.Equal(TagChipKind.Warning, vm.Tags.Single(t => t.Value == "jpg").Kind);
        Assert.Equal("toolbar.ext_also_in|Images", vm.Tags.Single(t => t.Value == "jpg").ToolTip);
        var note = Assert.Single(vm.Notes, n => n.IsWarning);
        Assert.Equal("toolbar.ext_claimed_first|.jpg, .png|Images", note.Text);
    }

    [Fact]
    public void Tells_when_this_profile_takes_files_from_a_later_one()
    {
        var vm = Create();
        vm.AddCommand.Execute("gif");
        Assert.Equal("toolbar.ext_claimed_later|.gif|Later", Assert.Single(vm.Notes).Text);
    }

    [Fact]
    public void Extensions_checked_on_mixed_profiles_are_not_claims()
    {
        var vm = Create();
        vm.AddCommand.Execute("bmp");

        Assert.Equal(TagChipKind.Normal, vm.Tags.Single(t => t.Value == "bmp").Kind);
        Assert.Empty(vm.Notes);
    }

    [Fact]
    public void Unknown_extensions_are_kept_but_marked()
    {
        var vm = Create();
        vm.AddCommand.Execute("xyz");

        Assert.Contains("xyz", _mine.Extensions);
        Assert.Equal(TagChipKind.Muted, vm.Tags.Single(t => t.Value == "xyz").Kind);
        Assert.Equal("toolbar.ext_unknown|.xyz", Assert.Single(vm.Notes).Text);
    }

    [Fact]
    public void Removes_extensions()
    {
        var vm = Create();
        vm.RemoveCommand.Execute(vm.Tags[0]);

        Assert.Empty(_mine.Extensions);
        Assert.Empty(vm.Tags);
        Assert.Equal(1, _saves);
        Assert.Equal("toolbar.ext_none", Assert.Single(vm.Notes).Text);
    }

    [Fact]
    public void Suggestions_follow_the_text_and_skip_what_is_there()
    {
        var vm = Create();
        vm.Text = "web";

        Assert.DoesNotContain(vm.Suggestions, s => s.Value == "webp");
        Assert.Contains(vm.Suggestions, s => s.Value == "webm");
        vm.Text = "jp";
        Assert.Equal("toolbar.ext_in_profile|Images", vm.Suggestions.Single(s => s.Value == "jpg").Note);
    }
}

public class MixedExtensionsViewModelTests
{
    private readonly ToolbarProfile _images = new() { Id = "images", Name = "Images", Extensions = ["jpg", "png"] };
    private readonly ToolbarProfile _pdf = new() { Id = "pdf", Name = "PDF", Extensions = ["pdf"] };
    private readonly ToolbarProfile _photos = new() { Id = "photos", Name = "Photos", Extensions = ["jpg", "heic"] };
    private readonly ToolbarProfile _mixed = new() { Id = "mixed", Name = "Mixed", IsFallback = true };
    private readonly ToolbarProfile _scans = new() { Id = "scans", Name = "Scans", IsFallback = true };
    private int _saves;

    private List<ToolbarProfile> All => [_images, _pdf, _photos, _mixed, _scans];

    private MixedExtensionsViewModel Create(ToolbarProfile profile, List<ToolbarProfile>? all = null) =>
        new(profile, all ?? All, new KeyLocalizer(), () => _saves++);

    [Fact]
    public void Groups_follow_the_normal_profiles_and_start_collapsed()
    {
        var vm = Create(_scans);

        Assert.Equal(["Images", "PDF", "Photos"], vm.Groups.Select(g => g.Name));
        Assert.All(vm.Groups, g => Assert.False(g.IsExpanded));
        Assert.All(vm.Groups, g => Assert.Equal(false, g.State));
        Assert.Equal("0 / 2", vm.Groups[0].Count);
    }

    [Fact]
    public void The_group_box_checks_all_then_none()
    {
        var vm = Create(_scans);
        var images = vm.Groups[0];

        images.ToggleAllCommand.Execute(null);
        Assert.Equal(["jpg", "png"], _scans.Extensions);
        Assert.Equal(true, images.State);
        Assert.Equal("2 / 2", images.Count);

        images.ToggleAllCommand.Execute(null);
        Assert.Empty(_scans.Extensions);
        Assert.Equal(false, images.State);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void Single_extensions_make_a_group_partly_checked_and_stay_in_step_across_groups()
    {
        var vm = Create(_scans);
        var images = vm.Groups[0];
        var photos = vm.Groups[2];

        images.Extensions.Single(e => e.Extension == "jpg").IsChecked = true;

        Assert.Equal(["jpg"], _scans.Extensions);
        Assert.Null(images.State);
        Assert.True(photos.Extensions.Single(e => e.Extension == "jpg").IsChecked); // same extension in "Photos"
        Assert.Equal("1 / 2", photos.Count);

        // A partly checked group box checks the rest.
        images.ToggleAllCommand.Execute(null);
        Assert.Equal(["jpg", "png"], _scans.Extensions);
    }

    [Fact]
    public void Checked_extensions_no_profile_has_get_their_own_group()
    {
        _scans.Extensions = ["jpg", "xmp"];
        var vm = Create(_scans);

        var other = vm.Groups[^1];
        Assert.Equal("toolbar.mixed_other_group", other.Name);
        Assert.Equal([".xmp"], other.Extensions.Select(e => e.Text));
        Assert.Equal(true, other.State);
        Assert.Equal("toolbar.ext_unknown_tip", other.Extensions[0].ToolTip);
    }

    [Fact]
    public void Hint_explains_the_catch_all()
    {
        Assert.Equal("toolbar.mixed_catch_all", Create(_mixed).Hint);

        var scans = Create(_scans);
        Assert.Equal("toolbar.mixed_shadowed|Mixed", scans.Hint);
        Assert.True(scans.HintIsWarning);

        scans.Groups[1].ToggleAllCommand.Execute(null);
        Assert.Equal("toolbar.mixed_others|Mixed", scans.Hint);
        Assert.False(scans.HintIsWarning);
    }

    [Fact]
    public void Hint_warns_when_an_earlier_mixed_profile_covers_every_check()
    {
        _mixed.Extensions = ["jpg", "png", "pdf"];
        _scans.Extensions = ["jpg", "pdf"];
        var vm = Create(_scans);

        Assert.Equal("toolbar.mixed_covered|Mixed", vm.Hint);
        Assert.True(vm.HintIsWarning);
    }

    [Fact]
    public void Hint_says_so_when_no_catch_all_is_left()
    {
        List<ToolbarProfile> all = [_images, _pdf, _mixed];
        var vm = Create(_mixed, all);
        vm.Groups[0].ToggleAllCommand.Execute(null);

        Assert.Equal("toolbar.mixed_others_self", vm.Hint);
    }
}
