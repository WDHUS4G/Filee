// "Replace the original" (#332): the finished result takes the source's place and the source goes to the Trash, or
// both are kept with a note saying why.

using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class ReplaceOriginalTests
{
    private static readonly Preset Replacing = new() { TargetFormat = BuiltInData.SameAsSource, Output = { ReplaceOriginal = true } };

    /// <summary>Moves files into a folder, or refuses with <paramref name="refuse"/>.</summary>
    private sealed class FakeTrash(string folder, TrashRefusal? refuse = null)
    {
        public List<string> Moved { get; } = [];

        /// <summary>What the source's folder held when the Trash was asked.</summary>
        public List<string> SeenBeside { get; } = [];

        public TrashRefusal? Move(string path)
        {
            SeenBeside.AddRange(Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName)!);
            if (refuse is not null)
                return refuse;
            File.Move(path, Path.Combine(folder, Path.GetFileName(path)));
            Moved.Add(path);
            return null;
        }
    }

    private static async Task<ConversionJob> Run(FakeTrash trash, string source, Preset preset, ISourceFetcher? fetcher = null)
    {
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"), new ConversionEdge("jpg", "png"))]),
            moveToTrash: trash.Move);
        var job = new ConversionJob([source], preset, "Test", fetcher);
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task The_result_takes_the_place_of_the_original_which_goes_to_the_trash()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        var job = await Run(trash, source, Replacing);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([source], trash.Moved);
        Assert.Equal([source], job.Outputs);
        Assert.Equal("a.jpg→jpg by magick", File.ReadAllText(source));
        Assert.Equal("x", File.ReadAllText(Path.Combine(bin.Path, "a.jpg")));
        Assert.Equal([source], Directory.GetFiles(dir.Path));
        Assert.Null(job.Files[0].NoticeKey);
    }

    [Fact]
    public async Task When_the_trash_refuses_both_are_kept_with_the_reason()
    {
        using var dir = new TempDir();
        var source = dir.File("a.jpg");

        var job = await Run(new FakeTrash(dir.Path, refuse: new TrashRefusal("trash.in_use")), source, Replacing);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("x", File.ReadAllText(source));
        var output = Assert.Single(job.Outputs);
        Assert.NotEqual(source, output);
        Assert.True(File.Exists(output));
        Assert.Equal("notice.original_kept", job.Files[0].NoticeKey);
        Assert.Equal("trash.in_use", job.Files[0].NoticeReasonKey);
        Assert.Null(job.Files[0].NoticeDetail);
    }

    [Fact]
    public async Task Without_a_trash_the_original_is_kept()
    {
        using var dir = new TempDir();
        var source = dir.File("a.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));
        var job = new ConversionJob([source], Replacing, "Test");

        await queue.RunAsync(job);

        Assert.Equal("x", File.ReadAllText(source));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
        Assert.Equal("notice.original_kept", job.Files[0].NoticeKey);
    }

    [Fact]
    public async Task Another_format_is_saved_beside_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        var job = await Run(trash, source, new Preset { TargetFormat = "png", Output = { ReplaceOriginal = true } });

        Assert.Empty(trash.Moved);
        Assert.Equal("x", File.ReadAllText(source));
        Assert.Equal([Path.Combine(dir.Path, "a.png")], job.Outputs);
        Assert.Null(job.Files[0].NoticeKey);
    }

    [Fact]
    public async Task Off_by_default()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        await Run(trash, source, new Preset { TargetFormat = BuiltInData.SameAsSource });

        Assert.False(new OutputRule().ReplaceOriginal);
        Assert.Empty(trash.Moved);
        Assert.Equal("x", File.ReadAllText(source));
    }

    [Fact]
    public async Task Files_from_a_browser_are_never_replaced()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        await Run(trash, source, Replacing, new LocalFetcher());

        Assert.Empty(trash.Moved);
        Assert.Equal("x", File.ReadAllText(source));
    }

    [Fact]
    public async Task The_result_is_beside_the_original_before_it_goes_and_keeps_its_dates()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var then = new DateTime(2020, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(source, then);
        File.SetLastWriteTimeUtc(source, then);
        var trash = new FakeTrash(bin.Path);
        var preset = new Preset
        {
            TargetFormat = BuiltInData.SameAsSource,
            Output = { ReplaceOriginal = true, KeepDates = true, Location = OutputLocation.CustomFolder, CustomFolder = other.Path },
        };

        var job = await Run(trash, source, preset);

        Assert.Equal([source], job.Outputs);
        Assert.Equal(2, trash.SeenBeside.Count); // the original and the result under its temporary name
        Assert.Empty(Directory.GetFiles(other.Path));
        Assert.Equal(then, File.GetLastWriteTimeUtc(source));
        Assert.Equal("a.jpg→jpg by magick", File.ReadAllText(source));
    }

    [Fact]
    public async Task When_the_trash_refuses_the_result_stays_where_it_was_saved()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        var source = dir.File("a.jpg");
        var preset = new Preset
        {
            TargetFormat = BuiltInData.SameAsSource,
            Output = { ReplaceOriginal = true, Location = OutputLocation.CustomFolder, CustomFolder = other.Path },
        };

        var job = await Run(new FakeTrash(dir.Path, refuse: new TrashRefusal("trash.no_recycle_bin")), source, preset);

        Assert.Equal([Path.Combine(other.Path, "a.jpg")], job.Outputs);
        Assert.Equal([source], Directory.GetFiles(dir.Path));
        Assert.Equal("x", File.ReadAllText(source));
    }

    [Fact]
    public async Task A_script_is_not_replaced_by_text_of_another_kind()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var script = dir.File("run.bat");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("text", new ConversionEdge("txt", "txt"))]), moveToTrash: trash.Move);
        var job = new ConversionJob([script], new Preset { TargetFormat = "txt", Output = { ReplaceOriginal = true } }, "Test");

        await queue.RunAsync(job);

        Assert.Empty(trash.Moved);
        Assert.Equal("x", File.ReadAllText(script));
        Assert.Equal([Path.Combine(dir.Path, "run.txt")], job.Outputs);
    }

    [Fact]
    public async Task Another_kind_of_file_in_the_same_format_is_not_replaced_but_another_spelling_is()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var show = dir.File("talk.pps");
        var trash = new FakeTrash(bin.Path);
        await using (var queue = new JobQueue(new ConverterCatalog([new FakeConverter("office", new ConversionEdge("ppt", "ppt"))]), moveToTrash: trash.Move))
        {
            var job = new ConversionJob([show], new Preset { TargetFormat = "ppt", Output = { ReplaceOriginal = true } }, "Test");
            await queue.RunAsync(job);
            Assert.Equal([Path.Combine(dir.Path, "talk.ppt")], job.Outputs);
        }
        Assert.Empty(trash.Moved);
        Assert.Equal("x", File.ReadAllText(show));

        var photo = dir.File("photo.jpeg");
        var replaced = await Run(trash, photo, new Preset { TargetFormat = "jpg", Output = { ReplaceOriginal = true } });

        Assert.Equal([photo], trash.Moved);
        Assert.Equal([photo], replaced.Outputs);
    }

    [Fact]
    public async Task Only_if_smaller_leaves_an_unchanged_original_alone()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);
        var preset = new Preset { TargetFormat = BuiltInData.SameAsSource, Output = { ReplaceOriginal = true, OnlyIfSmaller = true } };

        var job = await Run(trash, source, preset); // the fake result is larger than "x"

        Assert.Empty(trash.Moved);
        Assert.Equal([source], Directory.GetFiles(dir.Path)); // no copy beside it
        Assert.Equal("x", File.ReadAllText(source));
        Assert.Equal("notice.original_unchanged", job.Files[0].NoticeKey);
    }

    [Fact]
    public void Watch_folders_never_replace_what_they_watch()
    {
        var rule = new WatchRule { Folder = "in", OutputFolder = "out" };

        Assert.False(rule.Apply(Replacing).Output.ReplaceOriginal);
        Assert.True(Replacing.Output.ReplaceOriginal);
    }

    [Fact]
    public void Survives_a_preset_round_trip()
    {
        var copy = Replacing.Clone();

        Assert.True(copy.Output.ReplaceOriginal);
    }

    /// <summary>A browser drop whose files are already on disk.</summary>
    private sealed class LocalFetcher : ISourceFetcher
    {
        public bool Fetches(string source) => false;
        public Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken) => Task.FromResult(source);
        public void Release(ConversionJob job) { }
    }
}
