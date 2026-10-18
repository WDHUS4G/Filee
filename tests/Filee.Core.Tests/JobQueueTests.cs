using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class JobQueueTests
{
    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Test");
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task Converts_every_file()
    {
        using var dir = new TempDir();
        var a = dir.File("a.jpg");
        var b = dir.File("b.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var job = await Run(queue, [a, b], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "a.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b.png")));
        Assert.Equal(1, job.Progress);
    }

    [Fact]
    public async Task Rotate_needs_an_engine_that_turns_images_and_turns_once_on_a_longer_route()
    {
        using var dir = new TempDir();
        var pdf = dir.File("doc.pdf");
        var eps = dir.File("art.eps");
        var turner = new FakeTurner("magick", new ConversionEdge("png", "jpg"));
        var renderer = new FakeTurner("ghostscript", new ConversionEdge("eps", "png"));
        await using var queue = new JobQueue(new ConverterCatalog(
            [new FakeConverter("pdfsharp", new ConversionEdge("pdf", "pdf")), turner, renderer]));

        // PDF → PDF only copies pages: not "rotated" without turning anything.
        var copied = await Run(queue, [pdf], new Preset { TargetFormat = BuiltInData.SameAsSource, Image = { Rotation = ImageRotation.Left90 } });
        Assert.Equal(JobState.Failed, copied.State);
        Assert.Equal("error.cannot_turn", copied.Files.Single().ErrorKey);
        Assert.False(File.Exists(Path.Combine(dir.Path, "doc (1).pdf")));

        // EPS → PNG → JPG: both engines turn images, only the first one is asked to (not 90° twice = 180°).
        var turned = await Run(queue, [eps], new Preset { TargetFormat = "jpg", Image = { Rotation = ImageRotation.Left90 } });
        Assert.Equal(JobState.Completed, turned.State);
        Assert.Equal([("png", ImageRotation.Left90)], renderer.Asked);
        Assert.Equal([("jpg", ImageRotation.None)], turner.Asked);

        // Without rotation nothing changes.
        var plain = await Run(queue, [pdf], new Preset { TargetFormat = BuiltInData.SameAsSource });
        Assert.Equal(JobState.Completed, plain.State);
    }

    [Fact]
    public async Task A_step_before_the_turning_one_gets_the_preset_without_rotation()
    {
        // STL → PNG (an engine that renders with the image options but doesn't say it turns) → HEIC (one that turns):
        // only the HEIC step turns, or the picture would be turned twice.
        using var dir = new TempDir();
        var model = dir.File("part.stl");
        var renderer = new FakeRecorder("model3d", new ConversionEdge("stl", "png"));
        var heic = new FakeTurner("ffmpeg", new ConversionEdge("png", "heic"));
        await using var queue = new JobQueue(new ConverterCatalog([renderer, heic]));

        var job = await Run(queue, [model], new Preset { TargetFormat = "heic", Image = { Rotation = ImageRotation.Left90 } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([("png", ImageRotation.None)], renderer.Asked);
        Assert.Equal([("heic", ImageRotation.Left90)], heic.Asked);
    }

    [Fact]
    public async Task Keep_dates_gives_outputs_the_dates_of_their_source()
    {
        using var dir = new TempDir();
        var taken = dir.File("taken.jpg");
        var plain = dir.File("plain.jpg");
        var created = new DateTime(2019, 7, 1, 9, 30, 0, DateTimeKind.Utc);
        var modified = new DateTime(2019, 7, 2, 18, 5, 0, DateTimeKind.Utc);
        foreach (var source in new[] { taken, plain })
        {
            File.SetCreationTimeUtc(source, created);
            File.SetLastWriteTimeUtc(source, modified);
        }
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var keep = new Preset { TargetFormat = "png", Output = { KeepDates = true } };
        await Run(queue, [taken], keep);
        await Run(queue, [plain], new Preset { TargetFormat = "png" });

        var output = Path.Combine(dir.Path, "taken.png");
        // Linux file systems keep no creation time a program can set: there only the modification time is kept.
        if (!OperatingSystem.IsLinux())
            Assert.Equal(created, File.GetCreationTimeUtc(output));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(output));
        // Without the option the output has the time it was made.
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(dir.Path, "plain.png")) > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Multi_step_route_keeps_intermediates_out_of_the_output_folder()
    {
        using var dir = new TempDir();
        var source = dir.File("doc.hwpx");
        var catalog = new ConverterCatalog([
            new FakeConverter("rhwp", new ConversionEdge("hwpx", "pdf")),
            new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 2 },
        ]);
        await using var queue = new JobQueue(catalog);

        var job = await Run(queue, [source], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["doc.hwpx", "doc_p1.png", "doc_p2.png"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Failures_are_isolated_per_file()
    {
        using var dir = new TempDir();
        var ok = dir.File("a.jpg");
        var unsupported = dir.File("b.xyz");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var job = await Run(queue, [ok, unsupported], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal("error.unsupported_source", job.Files[1].ErrorKey);
    }

    [Fact]
    public async Task No_route_is_reported()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([]));

        var job = await Run(queue, [dir.File("a.docx")], new Preset { TargetFormat = "hwpx" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.no_route", job.Files[0].ErrorKey);
    }

    [Fact]
    public async Task Engine_exceptions_mark_the_file_failed()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("x", new ConversionEdge("jpg", "png")) { Throw = true }]));

        var job = await Run(queue, [dir.File("a.jpg")], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.conversion_failed", job.Files[0].ErrorKey);
    }

    [Fact]
    public async Task Same_as_source_target_reencodes_in_place_format()
    {
        using var dir = new TempDir();
        var source = dir.File("a.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));

        var job = await Run(queue, [source], new Preset { TargetFormat = BuiltInData.SameAsSource, Output = { FileNamePattern = "{name}_50" } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "a_50.jpg")));
        Assert.Equal("x", File.ReadAllText(source)); // source untouched
    }

    [Fact]
    public async Task Merge_converts_to_pdf_then_merges_in_order()
    {
        using var dir = new TempDir();
        var first = dir.File("1.jpg");
        var second = dir.File("2.pdf");
        var third = dir.File("3.png");
        var merger = new FakeMerger();
        var catalog = new ConverterCatalog([new FakeConverter("pdfsharp", new ConversionEdge("jpg", "pdf"), new ConversionEdge("png", "pdf"))]);
        await using var queue = new JobQueue(catalog, merger);

        var job = await Run(queue, [first, second, third],
            new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true }, Output = { FileNamePattern = "{name}_merged" } });

        Assert.Equal(JobState.Completed, job.State);
        var merged = Path.Combine(dir.Path, "1_merged.pdf");
        Assert.Equal("1.pdf|2.pdf|3.pdf", File.ReadAllText(merged));
        Assert.Equal(second, merger.Inputs[1]); // existing PDFs are merged as-is
    }

    [Fact]
    public async Task Multi_page_tiff_converts_every_file_to_pages_then_writes_one_tiff()
    {
        using var dir = new TempDir();
        var scan = dir.File("scan.pdf");
        var photo = dir.File("photo.jpg");
        var fax = dir.File("fax.tiff");
        var merger = new FakeTiffMerger();
        var catalog = new ConverterCatalog([
            new FakeConverter("pdfium", new ConversionEdge("pdf", "tiff")) { OutputsPerInput = 2 },
            new FakeConverter("magick", new ConversionEdge("jpg", "tiff")),
        ]);
        await using var queue = new JobQueue(catalog, tiffMerger: merger);

        var job = await Run(queue, [scan, photo, fax], new Preset { TargetFormat = "tiff", Image = { MultiPageTiff = true } });

        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(Path.Combine(dir.Path, "scan.tiff"), output);
        // Both PDF pages, then the photo, then the TIFF as it is.
        Assert.Equal(["scan_p1.tiff", "scan_p2.tiff", "photo.tiff", "fax.tiff"], merger.Inputs.Select(Path.GetFileName));
        Assert.Equal(fax, merger.Inputs[^1]);
    }

    [Fact]
    public async Task Merging_or_joining_pages_with_a_rotation_never_passes_a_file_through_unturned()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var doc = dir.File("doc.pdf");
        var fax = dir.File("fax.tiff");
        var turner = new FakeTurner("magick", new ConversionEdge("jpg", "pdf"), new ConversionEdge("jpg", "tiff"), new ConversionEdge("tiff", "tiff"));
        var pdfMerger = new FakeMerger();
        var tiffMerger = new FakeTiffMerger();
        await using var queue = new JobQueue(new ConverterCatalog([turner]), pdfMerger, tiffMerger: tiffMerger);

        // A PDF can't be turned: merged unturned it would say "done"; it fails, the photo is still merged.
        var merged = await Run(queue, [photo, doc], new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true }, Image = { Rotation = ImageRotation.Right90 } });
        Assert.Equal("error.cannot_turn", merged.Files[1].ErrorKey);
        Assert.Equal(["photo.pdf"], pdfMerger.Inputs.Select(Path.GetFileName));

        // A TIFF is turned like the photo before the pages are joined, not added as it is.
        await Run(queue, [photo, fax], new Preset { TargetFormat = "tiff", Image = { MultiPageTiff = true, Rotation = ImageRotation.Right90 } });
        Assert.DoesNotContain(fax, tiffMerger.Inputs);
        Assert.Contains(("tiff", ImageRotation.Right90), turner.Asked);
    }

    [Fact]
    public async Task Combine_packs_every_file_as_is_even_one_or_of_unknown_type()
    {
        using var dir = new TempDir();
        var a = dir.File("report.docx");
        var b = dir.File("data.xyz");
        var combiner = new FakeCombiner();
        // No converter at all: combining must not need a route.
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: combiner);
        var preset = new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true }, Output = { FileNamePattern = "{name}_files" } };

        var job = await Run(queue, [a, b], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([a, b], combiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "report_files.zip"), job.Outputs.Single());
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));

        var single = await Run(queue, [b], preset);
        Assert.Equal(Path.Combine(dir.Path, "data_files.zip"), single.Outputs.Single());
    }

    [Fact]
    public async Task Combine_uses_the_two_part_extension_and_reports_failures_for_every_file()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: new FakeCombiner());

        var job = await Run(queue, [a], new Preset { TargetFormat = "tgz", Archive = { CombineIntoOne = true } });
        Assert.Equal(Path.Combine(dir.Path, "a.tar.gz"), job.Outputs.Single());

        await using var failing = new JobQueue(new ConverterCatalog([]), combiner: new FakeCombiner { Throw = true });
        var failed = await Run(failing, [a, dir.File("b.txt")], new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true } });
        Assert.Equal(JobState.Failed, failed.State);
        Assert.All(failed.Files, f => Assert.Equal("boom", f.ErrorDetail));
    }

    [Fact]
    public async Task Without_combine_archive_targets_convert_file_by_file()
    {
        using var dir = new TempDir();
        var combiner = new FakeCombiner();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("archive", new ConversionEdge("rar", "zip"))]), combiner: combiner);

        var job = await Run(queue, [dir.File("a.rar"), dir.File("b.rar")], new Preset { TargetFormat = "zip" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Outputs.Count());
        Assert.Empty(combiner.Inputs);
    }

    [Fact]
    public async Task Parallel_files_with_the_same_name_do_not_collide()
    {
        using var dir = new TempDir();
        var a = dir.File("same.jpg");
        var b = dir.File("same.png");
        var catalog = new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "webp"), new ConversionEdge("png", "webp"))]);
        await using var queue = new JobQueue(catalog);

        var job = await Run(queue, [a, b], new Preset { TargetFormat = "webp" });

        Assert.Equal(2, job.Outputs.Distinct().Count());
    }

    [Fact]
    public async Task Enqueue_runs_in_background_and_raises_finished()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var finished = new TaskCompletionSource<ConversionJob>();
        queue.JobFinished += (_, job) => finished.TrySetResult(job);

        queue.Enqueue([dir.File("a.jpg")], new Preset { TargetFormat = "png" }, "PNG");
        var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, done.State);
    }
}
