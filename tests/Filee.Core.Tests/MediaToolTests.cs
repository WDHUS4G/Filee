// The everyday media tools of settings schema 37: their presets, how existing libraries get them, and the queue
// joining videos, audio files and pictures into one file.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MediaToolTests
{
    private static readonly string[] ToolIds =
        ["compress-image", "compress-video", "video-from-images", "extract-audio", "merge-videos", "compress-audio", "merge-audio", "remove-metadata"];

    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Test");
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public void A_new_library_has_every_tool_with_a_name_to_show()
    {
        var presets = BuiltInData.CreatePresets();
        foreach (var id in ToolIds)
            Assert.StartsWith("preset.", Assert.Single(presets, p => p.Id == id).NameKey);
    }

    [Fact]
    public void Only_the_merge_and_slideshow_tools_join_files()
    {
        Assert.Equal(["video-from-images", "merge-videos", "merge-audio"],
            BuiltInData.CreatePresets().Where(p => p.JoinsIntoOneMedia).Select(p => p.Id));
        // A join option on a target that can't hold it converts file by file, as before.
        Assert.False(new Preset { TargetFormat = "webm", Media = { JoinIntoOne = true } }.JoinsIntoOneMedia);
    }

    [Fact]
    public void Remove_metadata_takes_everything_and_keeps_the_format()
    {
        var clean = BuiltIn("remove-metadata");
        Assert.Equal(BuiltInData.SameAsSource, clean.TargetFormat);
        Assert.False(clean.Image.KeepMetadata);
        Assert.False(clean.Image.KeepColorProfile);
        Assert.True(clean.Image.KeepSourceQuality);
        Assert.True(clean.Pdf.RemoveMetadata);
        Assert.True(clean.Media.RemoveMetadata);
        Assert.False(clean.Output.KeepDates);
    }

    [Fact]
    public void Slides_and_fades_stay_within_their_limits()
    {
        var media = new MediaOptions { SlideSeconds = 1.5 };
        Assert.Equal(0.75, media.FadeDuration); // at most half of the time a picture is shown
        Assert.Equal(1, new MediaOptions { SlideSeconds = 3 }.FadeDuration);
        media.Crossfade = false;
        Assert.Equal(0, media.FadeDuration);
        Assert.Equal(MediaOptions.MaxSlideSeconds, new MediaOptions { SlideSeconds = 1e9 }.SlideDuration);
        Assert.Equal(3, new MediaOptions { SlideSeconds = double.NaN }.SlideDuration);
    }

    [Fact]
    public void Options_survive_a_copy()
    {
        var copy = BuiltIn("video-from-images").Clone();
        Assert.True(copy.Media.Slideshow);
        Assert.True(copy.Media.Crossfade);
        Assert.True(BuiltIn("compress-image").Clone().Image.MaxCompression);
    }

    [Fact]
    public void Version_36_libraries_get_the_tools_and_back_faces_with_room_offer_them()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !ToolIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.BackPresetIds!.RemoveAll(ToolIds.Contains);
        // The video donut's back face of schema 36, with the plain M4A slice.
        profiles.Single(p => p.Id == "video").BackPresetIds = ["mp4-av1", "to-webp", "extract-frames-png", "extract-frames-jpg",
            "live-photo-iphone", "motion-photo-android", "to-m4a", "to-wav", "to-flac", "zip-all"];
        // A back face the user filled up stays as it is.
        var pdf = profiles.Single(p => p.Id == "pdf");
        while (pdf.BackPresetIds!.Count < ToolbarProfile.MaxSlices)
            pdf.BackPresetIds.Insert(0, presets.First(p => !pdf.BackPresetIds.Contains(p.Id) && !pdf.PresetIds.Contains(p.Id)).Id);
        var full = pdf.BackPresetIds.ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(36, presets, profiles));

        Assert.All(ToolIds, id => Assert.Single(presets, p => p.Id == id));
        var audio = profiles.Single(p => p.Id == "audio").BackPresetIds!;
        Assert.Equal(["to-alac", "compress-audio", "merge-audio", "remove-metadata", "zip-all"], audio);
        // "Extract audio" takes the M4A slice's place; the new library's back face is what an upgraded one gets.
        var video = profiles.Single(p => p.Id == "video").BackPresetIds!;
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "video").BackPresetIds, video);
        Assert.Equal(full, pdf.BackPresetIds);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds,
            profiles.Single(p => p.Id == "images").BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(36, presets, profiles)); // once only
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void A_new_library_images_back_face_is_full_without_the_live_photo_video()
    {
        var presets = BuiltInData.CreatePresets();
        var images = BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds!;
        Assert.Equal(ToolbarProfile.MaxSlices, images.Count);
        Assert.Contains("compress-image", images);
        Assert.Contains("remove-metadata", images);
        Assert.DoesNotContain("extract-motion", images);
        Assert.Single(presets, p => p.Id == "extract-motion"); // still in the library
    }

    [Fact]
    public void A_schema_36_images_back_face_takes_what_fits_and_never_more_than_the_donut_holds()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !ToolIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        // The images back face as schema 36 wrote it: eleven slices, "Extract Live Photo video" among them.
        var images = profiles.Single(p => p.Id == "images");
        images.BackPresetIds = ["tool-crop", "rotate", "flip", "remove-background", "trim-transparent", "grayscale", "extract-motion",
            "to-avif", "to-gif", "merge-pdf", "zip-all"];

        Assert.True(SettingsMigrations.ApplyToLibrary(36, presets, profiles));

        Assert.Equal(ToolbarProfile.MaxSlices, images.BackPresetIds.Count);
        Assert.Equal(["merge-pdf", "compress-image", "zip-all"], images.BackPresetIds[^3..]);
        Assert.DoesNotContain("remove-metadata", images.BackPresetIds); // no room left, still in the library
        Assert.Single(presets, p => p.Id == "remove-metadata");
        Assert.All(profiles, p => Assert.InRange(p.BackPresetIds!.Count, 0, ToolbarProfile.MaxSlices));
    }

    [Fact]
    public void Extract_audio_comes_onto_a_video_back_face_without_the_m4a_slice_when_there_is_room()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !ToolIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.BackPresetIds = ["mp4-av1", "to-wav", "zip-all"];

        Assert.True(SettingsMigrations.ApplyToLibrary(36, presets, profiles));

        // "Cut a clip" (schema 45) comes in first as well.
        Assert.Equal(["tool-clip", "mp4-av1", "to-wav", "compress-video", "merge-videos", "extract-audio", "zip-all"], video.BackPresetIds);
    }

    [Fact]
    public async Task Merge_videos_joins_the_clips_in_order_into_one_file_named_after_the_first()
    {
        using var dir = new TempDir();
        var second = dir.File("b.mp4");
        var first = dir.File("a.mov");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [first, second], BuiltIn("merge-videos"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([first, second], joiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "a_merged.mp4"), job.Outputs.Single());
    }

    [Fact]
    public async Task A_file_that_is_no_video_fails_and_the_other_clips_still_join()
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mp4");
        var song = dir.File("b.mp3");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [clip, song], BuiltIn("merge-videos"));

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal([clip], joiner.Inputs);
        Assert.Equal("error.no_route", job.Files[1].ErrorKey);
    }

    [Fact]
    public async Task Merge_audio_takes_audio_files_and_the_sound_of_videos()
    {
        using var dir = new TempDir();
        var song = dir.File("a.mp3");
        var clip = dir.File("b.mp4");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [song, clip], BuiltIn("merge-audio"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([song, clip], joiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "a_merged.m4a"), job.Outputs.Single());
    }

    [Fact]
    public async Task Video_from_images_makes_pngs_first_and_takes_a_png_as_it_is()
    {
        using var dir = new TempDir();
        var photo = dir.File("a.jpg");
        var drawing = dir.File("b.png");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]),
            mediaJoiner: joiner);

        var job = await Run(queue, [photo, drawing], BuiltIn("video-from-images"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, joiner.Inputs.Count);
        Assert.EndsWith(".png", joiner.Inputs[0]);
        Assert.NotEqual(photo, joiner.Inputs[0]); // converted in the work directory
        Assert.Equal(drawing, joiner.Inputs[1]);
        Assert.Equal(Path.Combine(dir.Path, "a_video.mp4"), job.Outputs.Single());
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.png"))); // no picture left beside the source
    }

    [Fact]
    public async Task A_slideshow_is_not_shown_as_done_while_its_video_is_made()
    {
        using var dir = new TempDir();
        var photos = new[] { dir.File("a.jpg"), dir.File("b.jpg") };
        var job = new ConversionJob(photos, BuiltIn("video-from-images"), "Test");
        double atJoin = -1;
        var joiner = new FakeJoiner { Joining = () => atJoin = job.Progress };
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]),
            mediaJoiner: joiner);

        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.InRange(atJoin, 0.01, 0.5); // the pictures are ready, the video isn't
        Assert.Equal(1, job.Progress);
    }

    [Fact]
    public async Task Compressing_keeps_the_original_when_the_copy_would_not_be_smaller()
    {
        using var dir = new TempDir();
        // The fake engine writes a short line: smaller than the big file, bigger than the tiny one.
        var big = dir.File("big.jpg", new string('x', 10_000));
        var tiny = dir.File("tiny.jpg", "x");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));

        var job = await Run(queue, [big, tiny], BuiltIn("compress-image"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Null(job.Files[0].NoticeKey);
        Assert.True(new FileInfo(Path.Combine(dir.Path, "big_compressed.jpg")).Length < 10_000);
        Assert.Equal("notice.pdf_not_smaller", job.Files[1].NoticeKey);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir.Path, "tiny_compressed.jpg"))); // the original, copied
    }

    [Fact]
    public async Task Compressing_into_a_zip_keeps_the_original_when_the_copy_would_not_be_smaller()
    {
        using var dir = new TempDir();
        var big = dir.File("big.jpg", new string('x', 10_000));
        var tiny = dir.File("tiny.jpg", "x");
        var preset = BuiltIn("compress-image").Clone();
        preset.Output.ZipResult = true;
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));

        var job = await Run(queue, [big, tiny], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Null(job.Files[0].NoticeKey);
        Assert.True(ZipEntry(job.Files[0].Outputs.Single()).Length < 10_000);
        Assert.Equal("notice.pdf_not_smaller", job.Files[1].NoticeKey);
        Assert.Equal("x", ZipEntry(job.Files[1].Outputs.Single())); // the original, packed
        Assert.Equal("x", File.ReadAllText(tiny));

        static string ZipEntry(string zip)
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            using var reader = new StreamReader(Assert.Single(archive.Entries).Open());
            return reader.ReadToEnd();
        }
    }

    [Fact]
    public async Task A_failed_join_frees_its_name_and_removes_the_subfolder_it_made()
    {
        using var dir = new TempDir();
        var a = dir.File("a.mp4");
        var b = dir.File("b.mp4");
        var preset = BuiltIn("merge-videos").Clone();
        preset.Output.Location = OutputLocation.Subfolder;
        preset.Output.SubfolderName = "joined";
        var joiner = new FakeJoiner { Throw = true };
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        Assert.Equal(JobState.Failed, (await Run(queue, [a, b], preset)).State);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "joined")));

        joiner.Throw = false;
        var again = await Run(queue, [a, b], preset);
        Assert.Equal(Path.Combine(dir.Path, "joined", "a_merged.mp4"), again.Outputs.Single()); // not "a_merged (2)"
    }

    [Fact]
    public async Task A_joined_file_is_one_file_for_subfolder_only_for_several_files()
    {
        using var dir = new TempDir();
        var preset = BuiltIn("merge-videos").Clone();
        preset.Output.Location = OutputLocation.Subfolder;
        preset.Output.SubfolderName = "{name}";
        preset.Output.SubfolderOnlyForSeveralFiles = true;
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: new FakeJoiner());

        var job = await Run(queue, [dir.File("a.mp4"), dir.File("b.mp4")], preset);

        Assert.Equal(Path.Combine(dir.Path, "a_merged.mp4"), job.Outputs.Single());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "a")));
    }

    [Fact]
    public async Task Too_many_pictures_fail_before_any_of_them_is_converted()
    {
        using var dir = new TempDir();
        var photos = Enumerable.Range(1, MediaOptions.MaxSlides + 1).Select(i => dir.File($"p{i:D3}.jpg")).ToList();
        // A conversion that ran would fail as "conversion failed", not as "too many pictures".
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Throw = true };
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([converter]), mediaJoiner: joiner);

        var job = await Run(queue, photos, BuiltIn("video-from-images"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.All(job.Files, f => Assert.Equal("error.too_many_pictures", f.ErrorKey));
        Assert.All(job.Files, f => Assert.Equal($"{MediaOptions.MaxSlides + 1} > {MediaOptions.MaxSlides}", f.ErrorDetail));
        Assert.Empty(joiner.Inputs);
    }

    [Fact]
    public async Task Too_many_pictures_counts_only_the_ones_that_were_saved()
    {
        using var dir = new TempDir();
        var photos = Enumerable.Range(1, MediaOptions.MaxSlides + 1).Select(i => dir.File($"p{i:D3}.jpg")).ToList();
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Throw = true };
        await using var queue = new JobQueue(new ConverterCatalog([converter]), mediaJoiner: new FakeJoiner());
        var job = new ConversionJob([.. photos, "web/lost.jpg"], BuiltIn("video-from-images"), "Test", new FailingFetcher());

        await queue.RunAsync(job);

        Assert.Equal("error.web_download_failed", job.Files[^1].ErrorKey);
        Assert.All(job.Files.SkipLast(1), f => Assert.Equal($"{MediaOptions.MaxSlides + 1} > {MediaOptions.MaxSlides}", f.ErrorDetail));
    }

    /// <summary>A browser download that never arrives.</summary>
    private sealed class FailingFetcher : ISourceFetcher
    {
        public bool Fetches(string source) => source.StartsWith("web/", StringComparison.Ordinal);

        public Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken) =>
            throw new IOException("gone");

        public void Release(ConversionJob job)
        {
        }
    }

    [Fact]
    public async Task A_kept_original_goes_next_to_its_source_like_a_compressed_copy()
    {
        using var dir = new TempDir();
        var tiny = dir.File("tiny.jpg", "x");
        var preset = BuiltIn("compress-image").Clone();
        preset.Output.Location = OutputLocation.Subfolder;
        preset.Output.SubfolderName = "{name}";
        preset.Output.SubfolderOnlyForSeveralFiles = true;
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));

        var job = await Run(queue, [tiny], preset);

        var output = Path.Combine(dir.Path, "tiny_compressed.jpg");
        Assert.Equal(output, job.Outputs.Single());
        Assert.Equal("x", File.ReadAllText(output)); // the original, copied
        Assert.Equal("notice.pdf_not_smaller", job.Files[0].NoticeKey);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "tiny")));
    }

    [Fact]
    public async Task A_failed_join_fails_every_file_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var a = dir.File("a.mp4");
        var b = dir.File("b.mp4");
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: new FakeJoiner { Throw = true });

        var job = await Run(queue, [a, b], BuiltIn("merge-videos"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.All(job.Files, f => Assert.Equal("error.conversion_failed", f.ErrorKey));
        Assert.Empty(job.Outputs);
    }
}
