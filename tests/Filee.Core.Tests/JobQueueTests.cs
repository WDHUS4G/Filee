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
    public async Task Missing_fonts_from_every_step_are_combined_without_blocking_conversion()
    {
        using var dir = new TempDir();
        var first = new FakeConverter("first", new ConversionEdge("jpg", "png"))
        { FontsToReport = ["Tahoma", "Arial"] };
        var second = new FakeConverter("second", new ConversionEdge("png", "webp"))
        { FontsToReport = ["tahoma", "Noto Serif SC"] };
        await using var queue = new JobQueue(new ConverterCatalog([first, second]));

        var source = dir.File("photo.jpg");
        var job = await Run(queue, [source], new Preset { TargetFormat = "webp" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["Tahoma", "Arial", "Noto Serif SC"], job.Files.Single().MissingFonts);
        Assert.Equal([source], first.OriginalSources);
        Assert.Equal([source], second.OriginalSources);
        Assert.True(File.Exists(Path.Combine(dir.Path, "photo.webp")));
    }

    [Fact]
    public async Task Is_busy_from_enqueueing_until_the_job_has_finished()
    {
        // The app frees memory only while the queue is idle: a waiting or running job must count.
        using var dir = new TempDir();
        var gate = new TaskCompletionSource();
        await using var queue = new JobQueue(new ConverterCatalog([new GatedConverter(gate.Task, new ConversionEdge("jpg", "png"))]));
        var busyWhenFinished = new TaskCompletionSource<bool>();
        queue.JobFinished += (_, _) => busyWhenFinished.TrySetResult(queue.IsBusy);
        Assert.False(queue.IsBusy);

        queue.Enqueue([dir.File("a.jpg")], new Preset { TargetFormat = "png" }, "Test");
        Assert.True(queue.IsBusy);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(queue.IsBusy);

        gate.SetResult();
        Assert.True(await busyWhenFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (queue.IsBusy && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(queue.IsBusy);
    }

    /// <summary>Converts like <see cref="FakeConverter"/> once the gate opens.</summary>
    private sealed class GatedConverter(Task gate, params ConversionEdge[] edges) : IConverter
    {
        private readonly FakeConverter _inner = new("gated", edges);
        public string Id => "gated";
        public string DisplayName => "gated";
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await gate;
            return await _inner.ConvertAsync(step, progress, cancellationToken);
        }
    }

    [Fact]
    public async Task Text_that_needs_shaping_avoids_an_unshaped_engine_or_is_refused_without_another()
    {
        // Issues #185 and #202: Gujarati or Arabic DOCX → PDF through rhwp came out broken.
        using var dir = new TempDir();
        var arabic = dir.File("arabic.docx");
        var english = dir.File("english.docx");
        var writer = new FakeConverter("hwpx-writer", new ConversionEdge("docx", "hwpx"));
        var rhwp = new ShapingCheck("rhwp", path => Path.GetFileName(path).StartsWith("arabic", StringComparison.Ordinal) ? "Arabic" : null,
            new ConversionEdge("hwpx", "pdf", Unshaped: true));
        var office = new FakeConverter("libreoffice", new ConversionEdge("docx", "pdf", 25));
        var preset = new Preset { TargetFormat = "pdf" };

        await using (var queue = new JobQueue(new ConverterCatalog([writer, rhwp, office])))
        {
            var job = await Run(queue, [arabic, english], preset);
            Assert.Equal(JobState.Completed, job.State);
            Assert.Equal(1, rhwp.Converted); // English only
            Assert.Equal(["arabic.docx", "english.docx"], rhwp.Checked.Order().Select(Path.GetFileName));
        }

        office.Available = false;
        await using (var queue = new JobQueue(new ConverterCatalog([writer, rhwp, office])))
        {
            var job = await Run(queue, [dir.File("arabic-2.docx")], preset);
            var file = Assert.Single(job.Files);
            Assert.Equal(FileState.Failed, file.State);
            Assert.Equal("error.needs_shaping", file.ErrorKey);
            Assert.Equal("Arabic", file.ErrorDetail);
            Assert.False(File.Exists(Path.Combine(dir.Path, "arabic-2.pdf")));
        }
    }

    /// <summary>An engine with unshaped edges that finds a script in the files <paramref name="script"/> names one for.</summary>
    private sealed class ShapingCheck(string id, Func<string, string?> script, params ConversionEdge[] edges) : IConverter, ITextShapingCheck
    {
        private readonly FakeConverter _inner = new(id, edges);
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        /// <summary>Files <see cref="ScanScripts"/> was called for.</summary>
        public List<string> Checked { get; } = [];
        /// <summary>The <see cref="TextScripts.WithoutWidths"/> answer for a file (default: everything is measured).</summary>
        public Func<string, string?> WithoutWidths { get; set; } = _ => null;
        private int _converted;
        public int Converted => _converted;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public TextScripts ScanScripts(string sourcePath, string sourceFormat)
        {
            lock (Checked)
                Checked.Add(sourcePath);
            return new TextScripts(script(sourcePath), WithoutWidths(sourcePath));
        }

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _converted);
            return _inner.ConvertAsync(step, progress, cancellationToken);
        }
    }

    [Fact]
    public async Task Text_the_engine_cannot_measure_takes_another_route_but_is_never_refused()
    {
        // rhwp#8143: rhwp's width tables don't cover Cyrillic, so its letters overlap — ugly but readable. Such a
        // file takes a route around the unshaped engine when there is one, and still converts when there isn't.
        using var dir = new TempDir();
        var russian = dir.File("russian.docx");
        var english = dir.File("english.docx");
        var writer = new FakeConverter("hwpx-writer", new ConversionEdge("docx", "hwpx"));
        var rhwp = new ShapingCheck("rhwp", _ => null, new ConversionEdge("hwpx", "pdf", Unshaped: true))
        {
            WithoutWidths = path => Path.GetFileName(path).StartsWith("russian", StringComparison.Ordinal) ? "Cyrillic" : null,
        };
        var office = new FakeConverter("libreoffice", new ConversionEdge("docx", "pdf", 25));
        var preset = new Preset { TargetFormat = "pdf" };

        await using (var queue = new JobQueue(new ConverterCatalog([writer, rhwp, office])))
        {
            var job = await Run(queue, [russian, english], preset);
            Assert.Equal(JobState.Completed, job.State);
            Assert.Equal(1, rhwp.Converted); // English only
            Assert.Equal("russian.docx→pdf by libreoffice", File.ReadAllText(Path.Combine(dir.Path, "russian.pdf")));
            Assert.Equal(["english.docx", "russian.docx"], rhwp.Checked.Order().Select(Path.GetFileName));
        }

        office.Available = false;
        await using (var queue = new JobQueue(new ConverterCatalog([writer, rhwp, office])))
        {
            var job = await Run(queue, [dir.File("russian-2.docx")], preset);
            var file = Assert.Single(job.Files);
            Assert.Equal(FileState.Done, file.State);
            Assert.EndsWith("→pdf by rhwp", File.ReadAllText(Path.Combine(dir.Path, "russian-2.pdf")));
        }
    }

    [Fact]
    public async Task Text_with_shaping_and_unmeasured_scripts_is_refused_for_shaping()
    {
        // A scan finding both (Arabic + Cyrillic): shaping takes precedence, the file fails with its reason and is
        // never converted through the widths escape (a route kept only because the overlap stays readable).
        using var dir = new TempDir();
        var arabic = dir.File("arabic.docx");
        var writer = new FakeConverter("hwpx-writer", new ConversionEdge("docx", "hwpx"));
        var rhwp = new ShapingCheck("rhwp", _ => "Arabic", new ConversionEdge("hwpx", "pdf", Unshaped: true))
        {
            WithoutWidths = _ => "Cyrillic",
        };
        await using var queue = new JobQueue(new ConverterCatalog([writer, rhwp]));

        var job = await Run(queue, [arabic], new Preset { TargetFormat = "pdf" });

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.needs_shaping", file.ErrorKey);
        Assert.Equal("Arabic", file.ErrorDetail);
        Assert.Equal(0, rhwp.Converted);
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
    public async Task Playback_speed_changes_only_in_the_step_that_writes_the_output()
    {
        // MP4 → GIF → WebP: both engines keep the animation, only the last one may change its speed (not 2 × 2 = 4×).
        using var dir = new TempDir();
        var video = dir.File("clip.mp4");
        var ffmpeg = new FakeRecorder("ffmpeg", new ConversionEdge("mp4", "gif"));
        var magick = new FakeRecorder("magick", new ConversionEdge("gif", "webp"));
        await using var queue = new JobQueue(new ConverterCatalog([ffmpeg, magick]));
        var preset = new Preset { TargetFormat = "webp", Image = { AnimationSpeed = 2 } };

        var job = await Run(queue, [video], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([("gif", 1.0)], ffmpeg.Speeds);
        Assert.Equal([("webp", 2.0)], magick.Speeds);
        Assert.Equal(2, preset.Image.AnimationSpeed); // the job's preset itself is left alone
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
    public async Task Every_frame_is_saved_only_for_videos_and_the_frame_targets()
    {
        using var dir = new TempDir();
        var video = dir.File("clip.mp4");
        var photo = dir.File("photo.png");
        var ffmpeg = new FakeRecorder("ffmpeg", new ConversionEdge("mp4", "png"),
            new ConversionEdge("mp4", "png", FrameSequence: true), new ConversionEdge("mp4", "jpg", FrameSequence: true));
        var magick = new FakeTurner("magick", new ConversionEdge("png", "jpg"), new ConversionEdge("png", "webp"));
        await using var queue = new JobQueue(new ConverterCatalog([ffmpeg, magick]));

        // Without "save every frame" a video → JPG is a still frame the image engine turns, as always.
        var turned = await Run(queue, [video], new Preset { TargetFormat = "jpg", Image = { Rotation = ImageRotation.Left90 } });
        Assert.Equal(JobState.Completed, turned.State);
        Assert.Equal([("mp4", "png", false)], ffmpeg.Steps);
        Assert.Equal([("jpg", ImageRotation.Left90)], magick.Asked);

        // With it: one step that saves the frames.
        ffmpeg.Steps.Clear();
        var frames = await Run(queue, [video], new Preset { TargetFormat = "jpg", Media = { SaveEveryFrame = true } });
        Assert.Equal(JobState.Completed, frames.State);
        Assert.Equal([("mp4", "jpg", true)], ffmpeg.Steps);

        // A target frames can't be (left over from an earlier PNG target) or a picture: converted as usual.
        ffmpeg.Steps.Clear();
        await Run(queue, [video], new Preset { TargetFormat = "webp", Media = { SaveEveryFrame = true } });
        await Run(queue, [photo], new Preset { TargetFormat = "jpg", Media = { SaveEveryFrame = true } });
        Assert.Equal([("mp4", "png", false)], ffmpeg.Steps);
    }

    [Fact]
    public async Task A_refused_conversion_says_why_in_the_users_language()
    {
        using var dir = new TempDir();
        var video = dir.File("long.mp4");
        var ffmpeg = new FakeConverter("ffmpeg", new ConversionEdge("mp4", "png", FrameSequence: true))
        {
            Failure = new ConversionRefusedException("error.too_many_frames", "216,000 > 100,000"),
        };
        await using var queue = new JobQueue(new ConverterCatalog([ffmpeg]));

        var job = await Run(queue, [video], new Preset { TargetFormat = "png", Media = { SaveEveryFrame = true } });

        var file = job.Files.Single();
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal(("error.too_many_frames", "216,000 > 100,000"), (file.ErrorKey, file.ErrorDetail));
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
    public async Task An_empty_file_fails_as_empty_before_any_engine_sees_it()
    {
        // A DOCX made with "New" in Explorer is 0 bytes until Word saves it (#157).
        using var dir = new TempDir();
        var empty = dir.File("new.docx", "");
        var ok = dir.File("a.jpg");
        var converter = new FakeConverter("docx-writer", new ConversionEdge("docx", "pdf"), new ConversionEdge("jpg", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([converter]));

        var job = await Run(queue, [empty, ok], new Preset { TargetFormat = "pdf" });

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal(FileState.Failed, job.Files[0].State);
        Assert.Equal("error.empty_file", job.Files[0].ErrorKey);
        Assert.Null(job.Files[0].ErrorDetail);
        Assert.False(File.Exists(Path.Combine(dir.Path, "new.pdf")));
        Assert.Equal(FileState.Done, job.Files[1].State);
    }

    [Fact]
    public async Task An_empty_pdf_fails_as_empty_instead_of_breaking_the_merge()
    {
        using var dir = new TempDir();
        var photo = dir.File("1.jpg");
        var empty = dir.File("2.pdf", "");
        var merger = new FakeMerger();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("pdfsharp", new ConversionEdge("jpg", "pdf"))]), merger);

        var job = await Run(queue, [photo, empty], new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } });

        Assert.Equal("error.empty_file", job.Files[1].ErrorKey);
        Assert.Equal(["1.pdf"], merger.Inputs.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Combine_still_packs_empty_files_and_folders()
    {
        // Packing changes nothing: an empty file goes into the archive as it is.
        using var dir = new TempDir();
        var empty = dir.File("empty.txt", "");
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "folder")).FullName;
        var combiner = new FakeCombiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: combiner);

        var job = await Run(queue, [empty, folder], new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([empty, folder], combiner.Inputs);
    }

    [Fact]
    public async Task A_folder_ending_in_a_separator_is_packed_next_to_itself()
    {
        // The command line keeps "Photos\" as typed: the archive is still Photos_files.zip beside the folder.
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "Photos")).FullName;
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: new FakeCombiner());
        var preset = new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true }, Output = { FileNamePattern = "{name}_files" } };

        var job = await Run(queue, [folder + Path.DirectorySeparatorChar], preset);

        Assert.Equal(Path.Combine(dir.Path, "Photos_files.zip"), job.Outputs.Single());
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

    /// <summary>An engine that writes half a file where it was told to and then fails, or is cancelled.</summary>
    private sealed class HalfWriter(Action<ConversionStep>? whileWriting = null, string from = "jpg") : IConverter
    {
        public string Id => "half";
        public string DisplayName => "half";
        public IReadOnlyList<ConversionEdge> Edges { get; } = [new ConversionEdge(from, "png")];
        public int MaxParallelism => 0;
        public bool Writes { get; init; } = true;
        public bool Available { get; set; } = true;

        /// <summary>Awaited after taking the name and before failing: lets a test run something else meanwhile.</summary>
        public Func<Task>? Hold { get; init; }

        public EngineStatus GetStatus() => Available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_installed");

        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var path = step.Output.Allocate(step.To)!;
            if (Writes)
                await File.WriteAllTextAsync(path, "half", cancellationToken);
            whileWriting?.Invoke(step);
            if (Hold is not null)
                await Hold();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the engine crashed");
        }
    }

    [Fact]
    public async Task A_failed_conversion_leaves_no_half_written_file_and_frees_its_name()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using (var failing = new JobQueue(new ConverterCatalog([new HalfWriter()])))
        {
            var job = await Run(failing, [photo], new Preset { TargetFormat = "png" });

            Assert.Equal(JobState.Failed, job.State);
            Assert.False(File.Exists(Path.Combine(dir.Path, "photo.png")));
        }
    }

    [Fact]
    public async Task A_cancelled_conversion_leaves_no_half_written_file_and_a_retry_gets_the_same_name()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        ConversionJob? running = null;
        var half = new HalfWriter(_ => running!.Cancel());
        var good = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        var catalog = new ConverterCatalog([half, good]);
        await using var queue = new JobQueue(catalog);

        running = new ConversionJob([photo], new Preset { TargetFormat = "png" }, "Test");
        good.Available = false;
        catalog.Refresh();
        await queue.RunAsync(running);
        Assert.Equal(JobState.Cancelled, running.State);
        Assert.False(File.Exists(Path.Combine(dir.Path, "photo.png")));

        // Tried again in the same session: the name the cancelled one had is free again.
        half.Available = false;
        good.Available = true;
        catalog.Refresh();
        var retry = await Run(queue, [photo], new Preset { TargetFormat = "png" });
        Assert.Equal([Path.Combine(dir.Path, "photo.png")], retry.Outputs);
    }

    [Fact]
    public async Task A_name_a_failed_conversion_took_is_free_for_the_next_one()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var half = new HalfWriter { Writes = false };
        var good = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Available = false };
        var catalog = new ConverterCatalog([half, good]);
        await using var queue = new JobQueue(catalog);

        Assert.Equal(JobState.Failed, (await Run(queue, [photo], new Preset { TargetFormat = "png" })).State);
        half.Available = false;
        good.Available = true;
        catalog.Refresh();
        var retry = await Run(queue, [photo], new Preset { TargetFormat = "png" });

        Assert.Equal([Path.Combine(dir.Path, "photo.png")], retry.Outputs);
    }

    [Fact]
    public async Task A_failed_conversion_never_deletes_a_file_it_was_to_overwrite()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var older = dir.File("photo.png", "older");
        await using var queue = new JobQueue(new ConverterCatalog([new HalfWriter { Writes = false }]));

        var job = await Run(queue, [photo], new Preset { TargetFormat = "png", Output = { Conflict = ConflictPolicy.Overwrite } });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("older", File.ReadAllText(older));
    }

    [Fact]
    public async Task A_failed_conversion_never_deletes_the_file_another_one_was_given_the_same_name_for()
    {
        // "Overwrite" hands photo.gif and photo.jpg the same photo.png. The GIF's engine took it first and fails after
        // the JPG's has finished it: that file stays.
        using var dir = new TempDir();
        var took = new TaskCompletionSource();
        var fail = new TaskCompletionSource();
        var half = new HalfWriter(_ => took.TrySetResult(), from: "gif") { Writes = false, Hold = () => fail.Task };
        var good = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([half, good]));
        var preset = new Preset { TargetFormat = "png", Output = { Conflict = ConflictPolicy.Overwrite } };

        var failing = Run(queue, [dir.File("photo.gif")], preset);
        await took.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var finished = await Run(queue, [dir.File("photo.jpg")], preset);
        fail.SetResult();

        Assert.Equal(JobState.Failed, (await failing).State);
        Assert.Equal([Path.Combine(dir.Path, "photo.png")], finished.Outputs);
        Assert.Contains("by magick", File.ReadAllText(Path.Combine(dir.Path, "photo.png")));
    }

    [Fact]
    public async Task A_failed_conversion_removes_the_subfolder_it_made_but_not_one_that_was_there()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new HalfWriter()]));
        var preset = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" } };

        Assert.Equal(JobState.Failed, (await Run(queue, [dir.File("photo.jpg")], preset)).State);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "photo")));

        var mine = Directory.CreateDirectory(Path.Combine(dir.Path, "scan")).FullName;
        Assert.Equal(JobState.Failed, (await Run(queue, [dir.File("scan.jpg")], preset)).State);
        Assert.True(Directory.Exists(mine));
        Assert.Empty(Directory.EnumerateFileSystemEntries(mine));
    }

    [Fact]
    public async Task Subfolder_only_for_several_files_keeps_a_single_output_next_to_its_source()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var report = dir.File("report.pdf");
        var catalog = new ConverterCatalog([
            new FakeConverter("magick", new ConversionEdge("jpg", "png")),
            new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 3 },
        ]);
        await using var queue = new JobQueue(catalog);
        var preset = new Preset
        {
            TargetFormat = "png",
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}", SubfolderOnlyForSeveralFiles = true },
        };

        var job = await Run(queue, [photo, report], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([Path.Combine(dir.Path, "photo.png")], job.Files[0].Outputs);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "photo")));
        Assert.Equal(3, job.Files[1].Outputs.Count);
        Assert.All(job.Files[1].Outputs, o => Assert.Equal(Path.Combine(dir.Path, "report"), Path.GetDirectoryName(o)));
        Assert.All(job.Outputs, o => Assert.True(File.Exists(o)));
    }

    [Fact]
    public async Task Subfolder_only_for_several_files_leaves_a_shared_subfolder_to_the_files_still_in_it()
    {
        using var dir = new TempDir();
        var catalog = new ConverterCatalog([
            new FakeConverter("magick", new ConversionEdge("jpg", "png")),
            new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 2 },
        ]);
        await using var queue = new JobQueue(catalog);
        var preset = new Preset
        {
            TargetFormat = "png",
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "converted", SubfolderOnlyForSeveralFiles = true },
        };

        var job = await Run(queue, [dir.File("a.jpg"), dir.File("b.pdf"), dir.File("c.jpg")], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "a.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "c.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "b_p1.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "b_p2.png")));
    }

    [Fact]
    public async Task Subfolder_only_for_several_files_applies_the_conflict_policy_next_to_the_source()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        dir.File("photo.png", "older");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var preset = new Preset
        {
            TargetFormat = "png",
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}", SubfolderOnlyForSeveralFiles = true },
        };

        var job = await Run(queue, [photo], preset);

        Assert.Equal([Path.Combine(dir.Path, "photo (2).png")], job.Outputs);
        Assert.Equal("older", File.ReadAllText(Path.Combine(dir.Path, "photo.png")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "photo")));
    }

    [Fact]
    public async Task Subfolder_only_for_several_files_saves_a_merged_pdf_next_to_the_first_file()
    {
        using var dir = new TempDir();
        var catalog = new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "pdf"))]);
        await using var queue = new JobQueue(catalog, pdfMerger: new FakeMerger());
        var preset = new Preset
        {
            TargetFormat = "pdf",
            Pdf = { MergeIntoSingle = true },
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}", SubfolderOnlyForSeveralFiles = true },
        };

        var job = await Run(queue, [dir.File("a.jpg"), dir.File("b.jpg")], preset);

        Assert.Equal([Path.Combine(dir.Path, "a.pdf")], job.Outputs);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "a")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subfolder_only_for_several_files_keeps_a_markdown_file_with_its_picture_folder(bool reserved)
    {
        using var dir = new TempDir();
        var converter = new FakeConverter("pandoc", new ConversionEdge("docx", "md")) { PictureFolder = true, ReservesPictureFolder = reserved };
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var preset = new Preset
        {
            TargetFormat = "md",
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}", SubfolderOnlyForSeveralFiles = true },
        };

        var job = await Run(queue, [dir.File("report.docx")], preset);

        // Its links point into the folder beside it: the two stay together in the subfolder.
        Assert.Equal([Path.Combine(dir.Path, "report", "report.md")], job.Outputs);
        Assert.True(File.Exists(Path.Combine(dir.Path, "report", "report_files", "img0001.png")));
    }

    [Fact]
    public async Task Subfolder_only_for_several_files_leaves_the_file_in_the_subfolder_when_skip_meets_an_existing_one()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        dir.File("photo.png", "older");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var preset = new Preset
        {
            TargetFormat = "png",
            Output =
            {
                Location = OutputLocation.Subfolder, SubfolderName = "{name}", SubfolderOnlyForSeveralFiles = true,
                Conflict = ConflictPolicy.Skip,
            },
        };

        var job = await Run(queue, [photo], preset);

        Assert.Equal([Path.Combine(dir.Path, "photo", "photo.png")], job.Outputs);
        Assert.Equal("older", File.ReadAllText(Path.Combine(dir.Path, "photo.png")));
    }

    [Fact]
    public async Task Without_subfolder_only_for_several_files_a_single_output_stays_in_the_subfolder()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var preset = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" } };

        var job = await Run(queue, [dir.File("photo.jpg")], preset);

        Assert.Equal([Path.Combine(dir.Path, "photo", "photo.png")], job.Outputs);
    }

    [Fact]
    public void Work_folders_a_crash_left_behind_are_deleted_and_running_ones_kept()
    {
        using var root = new TempDir();
        var old = DateTime.UtcNow.AddDays(-3);
        string Folder(string name, DateTime written, string? nested = null)
        {
            var folder = Path.Combine(root.Path, name);
            Directory.CreateDirectory(Path.Combine(folder, "a1b2c3d4"));
            var file = Path.Combine(folder, "a1b2c3d4", "page.tiff");
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, nested is null ? written : DateTime.UtcNow);
            Directory.SetLastWriteTimeUtc(Path.Combine(folder, "a1b2c3d4"), written);
            Directory.SetLastWriteTimeUtc(folder, written);
            return folder;
        }

        var crashed = Folder("0123456789", old);
        var running = Folder("abcdef0123", DateTime.UtcNow);
        // Made days ago, but a long encode is still writing into it.
        var encoding = Folder("fedcba9876", old, nested: "still writing");
        var web = Folder("web", old);
        var other = Folder("not-a-job", old);

        JobQueue.DeleteStaleWorkFolders(root.Path, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(crashed));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(encoding));
        Assert.True(Directory.Exists(web)); // WebDrop.DeleteStale's
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void A_link_named_like_a_job_is_never_followed_by_the_work_folder_sweep()
    {
        using var root = new TempDir();
        using var elsewhere = new TempDir();
        var precious = Path.Combine(elsewhere.Path, "keep.txt");
        File.WriteAllText(precious, "the user's");
        File.SetLastWriteTimeUtc(precious, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(elsewhere.Path, DateTime.UtcNow.AddDays(-30));
        var link = Path.Combine(root.Path, "0123456789");
        try
        {
            Directory.CreateSymbolicLink(link, elsewhere.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine doesn't let the test make a symbolic link.");
        }

        JobQueue.DeleteStaleWorkFolders(root.Path, TimeSpan.FromDays(1));

        Assert.True(File.Exists(precious));
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
