using Filee.Core.Formats;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class OutputPathResolverTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 14, 5, 9);

    private static OutputPathResolver.Tokens Tokens(string source, int index = 1) => new(source, "PNG", index, Now);

    [Fact]
    public void Default_pattern_keeps_the_source_name()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");

        var path = OutputPathResolver.Resolve(new OutputRule(), Tokens(source), "png", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "photo.png"), path);
    }

    [Fact]
    public void Two_part_extensions_are_one_extension()
    {
        using var dir = new TempDir();
        var source = dir.File("backup.tar.gz");

        var path = OutputPathResolver.Resolve(new OutputRule(), Tokens(source), "zip", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "backup.zip"), path);
        Assert.Equal("tgz", FormatRegistry.Detect(source)!.Id);
        Assert.Equal("tar.gz", FormatRegistry.ExtensionOf(source));
        Assert.Equal("gz", FormatRegistry.ExtensionOf("notes.txt.gz"));
        Assert.Equal("notes.txt", FormatRegistry.NameWithoutExtension("notes.txt.gz"));
        Assert.Equal("mp4", FormatRegistry.Detect("clip.MP4")!.Id);
        Assert.Null(FormatRegistry.Detect("README"));
    }

    [Fact]
    public void An_empty_extension_asks_for_a_folder()
    {
        using var dir = new TempDir();
        var source = dir.File("photos.zip");
        Directory.CreateDirectory(Path.Combine(dir.Path, "photos"));

        var path = OutputPathResolver.Resolve(new OutputRule(), Tokens(source), "", p => File.Exists(p) || Directory.Exists(p));

        Assert.Equal(Path.Combine(dir.Path, "photos (2)"), path);
    }

    [Fact]
    public void Existing_file_gets_a_number_by_default()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        dir.File("photo.png");

        var path = OutputPathResolver.Resolve(new OutputRule(), Tokens(source), "png", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "photo (2).png"), path);
    }

    [Fact]
    public void Overwrite_and_skip_policies()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");
        var existing = dir.File("photo.png");

        Assert.Equal(existing, OutputPathResolver.Resolve(new OutputRule { Conflict = ConflictPolicy.Overwrite }, Tokens(source), "png", File.Exists));
        Assert.Null(OutputPathResolver.Resolve(new OutputRule { Conflict = ConflictPolicy.Skip }, Tokens(source), "png", File.Exists));
    }

    [Theory]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Rename)]
    public void The_source_file_is_never_overwritten(ConflictPolicy policy)
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg");

        var path = OutputPathResolver.Resolve(new OutputRule { Conflict = policy }, Tokens(source), "jpg", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "photo (2).jpg"), path);
    }

    [Fact]
    public void Tokens_are_expanded_and_invalid_characters_removed()
    {
        var rule = new OutputRule { FileNamePattern = "{name}_{preset}_{date}_{index}:?" };
        var path = OutputPathResolver.Resolve(rule, Tokens(Path.Combine("C:", "x", "a.jpg"), 3), "png", _ => false);

        Assert.Equal("a_PNG_20260930_3__.png", Path.GetFileName(path));
    }

    [Fact]
    public void Subfolder_name_supports_tokens()
    {
        using var dir = new TempDir();
        var source = dir.File("report.pdf");
        var rule = new OutputRule { Location = OutputLocation.Subfolder, SubfolderName = "{name}" };

        var path = OutputPathResolver.Resolve(rule, Tokens(source), "pdf", File.Exists, "_p1");

        Assert.Equal(Path.Combine(dir.Path, "report", "report_p1.pdf"), path);
    }

    [Fact]
    public void Custom_folder_is_used_when_set()
    {
        var rule = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = Path.Combine("D:", "out") };

        var path = OutputPathResolver.Resolve(rule, Tokens(Path.Combine("C:", "in", "a.jpg")), "png", _ => false);

        Assert.Equal(Path.Combine("D:", "out", "a.png"), path);
    }

    [Theory]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Rename)]
    public void A_case_alias_of_the_source_is_never_overwritten(ConflictPolicy policy)
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg", "original");
        var rule = new OutputRule { FileNamePattern = "PHOTO", Conflict = policy };

        var path = OutputPathResolver.Resolve(rule, Tokens(source), "jpg", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "PHOTO (2).jpg"), path);
        Assert.Equal("original", File.ReadAllText(source));
    }

}
