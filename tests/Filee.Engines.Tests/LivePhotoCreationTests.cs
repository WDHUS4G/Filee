// Video → Live / Motion Photo. The outputs are taken apart box by box and segment by segment: the structure an iPhone
// and a Pixel write (identifiers, the still image time track, the XMP container directory and the appended MP4), then
// played by ffprobe and read back by Filee's own Live / Motion Photo extraction.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class LivePhotoCreationTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static (string Ffmpeg, string Ffprobe) Tools()
    {
        var tools = FfmpegConverter.Locate();
        Assert.SkipUnless(tools is not null, "FFmpeg is not installed.");
        return tools.Value;
    }

    private static Preset Live(LivePhotoKind kind, string target = "jpg", ConflictPolicy conflict = ConflictPolicy.Rename) =>
        new() { TargetFormat = target, Media = { LivePhoto = kind }, Output = { Conflict = conflict } };

    /// <summary>A test picture video with a sine tone (or without), 30 fps, as <paramref name="name"/> in a new folder.</summary>
    private async Task<string> VideoAsync(string name = "clip.mp4", bool audio = true, int seconds = 4, string size = "320x240",
        string[]? extra = null)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        var result = await FfmpegRunner.RunAsync(Tools().Ffmpeg, [
            .. MediaEncoding.Common, "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30",
            .. (audio ? new[] { "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" } : []),
            "-t", seconds.ToString(CultureInfo.InvariantCulture), .. extra ?? ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"], "-c:a", "aac", path,
        ], null, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private static IReadOnlyList<string> Outputs(ConversionJob job)
    {
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    private static string Probe(string path, params string[] entries)
    {
        var result = FfmpegRunner.RunBriefly(Tools().Ffprobe, ["-v", "error", .. entries, path], TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    // ───────────────────────── iPhone ─────────────────────────

    [Theory]
    [InlineData("jpg", true)]
    [InlineData("jpg", false)]
    [InlineData("heic", true)]
    public async Task Iphone_live_photo_is_a_photo_and_a_mov_sharing_one_identifier(string target, bool audio)
    {
        var video = await VideoAsync(audio: audio);
        var outputs = Outputs(await fx.ConvertAsync([video], Live(LivePhotoKind.Apple, target)));
        Assert.Equal(2, outputs.Count);
        var photo = Assert.Single(outputs, p => p.EndsWith("." + target, StringComparison.Ordinal));
        var movie = Assert.Single(outputs, p => p.EndsWith(".mov", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(video)!, "clip"), Path.ChangeExtension(photo, null));
        Assert.Equal(Path.ChangeExtension(photo, null), Path.ChangeExtension(movie, null));

        // The same identifier in the maker note (tag 17) and the movie's metadata, upper case as Apple writes it.
        var id = AppleLivePhoto.ImageIdentifier(photo);
        Assert.NotNull(id);
        Assert.Equal(id, AppleLivePhoto.MovieIdentifier(movie), StringComparer.OrdinalIgnoreCase);
        var movieBytes = await File.ReadAllBytesAsync(movie, TestContext.Current.CancellationToken);
        Assert.True(movieBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(id.ToUpperInvariant())) >= 0);

        var still = StillImageTrack(movie);
        // The default photo is the middle of the 3-second clip: frame 45 at 30 fps, 1.5 s.
        Assert.Equal(1.5, still.Seconds, 3);
        Assert.Equal(still.VideoTrackId, still.DescribedTrackId);

        // ffprobe plays it: the clip, its sound, and the timed metadata as a mebx data stream.
        var info = await MediaInfo.ProbeAsync(Tools().Ffprobe, movie, TestContext.Current.CancellationToken);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 2.9, 3.1);
        Assert.Equal(audio, info.Audio is not null);
        Assert.Equal((320, 240), (info.Video!.Width, info.Video.Height));
        Assert.Contains("mebx", Probe(movie, "-show_entries", "stream=codec_tag_string", "-of", "csv=p=0"));
        Assert.StartsWith("qt  ", Encoding.Latin1.GetString(await ReadAtAsync(movie, 8, 4)));

        using (var picture = new MagickImage())
        {
            picture.Ping(photo);
            Assert.Equal((320u, 240u), (picture.Width, picture.Height));
        }
        if (target == "jpg")
            Assert.Equal("JFIF", Segments(await File.ReadAllBytesAsync(photo, TestContext.Current.CancellationToken))[0].Name);

        // Filee's own extraction recognises the pair: the photo gives the MOV next to it.
        var extract = new Preset { TargetFormat = "mp4", Media = { ExtractMotionPhoto = true } };
        var extracted = Assert.Single(Outputs(await fx.ConvertAsync([photo], extract)));
        Assert.InRange((await MediaInfo.ProbeAsync(Tools().Ffprobe, extracted, TestContext.Current.CancellationToken)).Duration!.Value.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public async Task Clip_start_length_and_photo_frame_follow_the_preset()
    {
        var video = await VideoAsync(seconds: 6);
        var preset = Live(LivePhotoKind.Apple);
        preset.Media.LivePhotoStart = 2;
        preset.Media.LivePhotoLength = 2.5;
        preset.Media.LivePhotoCover = 0.4;
        var movie = Outputs(await fx.ConvertAsync([video], preset)).Single(p => p.EndsWith(".mov", StringComparison.Ordinal));
        Assert.InRange((await MediaInfo.ProbeAsync(Tools().Ffprobe, movie, TestContext.Current.CancellationToken)).Duration!.Value.TotalSeconds, 2.4, 2.6);
        Assert.Equal(0.4, StillImageTrack(movie).Seconds, 3);

        // A photo time past the end of the clip is its last frame; a start past the end of the video is refused.
        preset.Media.LivePhotoStart = 5;
        preset.Media.LivePhotoCover = 4;
        var shortened = Outputs(await fx.ConvertAsync([video], preset)).Single(p => p.EndsWith(".mov", StringComparison.Ordinal));
        Assert.Equal(29 / 30.0, StillImageTrack(shortened).Seconds, 3);
        preset.Media.LivePhotoStart = 30;
        var refused = await fx.ConvertAsync([video], preset);
        Assert.Equal("error.live_photo_start", refused.Files.Single().ErrorKey);
    }

    [Fact]
    public async Task Portrait_phone_video_stays_upright_in_the_photo_and_the_clip()
    {
        // 320×240 recorded sideways: the display matrix turns it, as phones do.
        var source = await VideoAsync();
        var rotated = Path.Combine(fx.NewFolder(), "portrait.mov");
        var result = await FfmpegRunner.RunAsync(Tools().Ffmpeg, [.. MediaEncoding.Common, "-display_rotation", "90", "-i", source, "-c", "copy", rotated],
            null, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);

        var outputs = Outputs(await fx.ConvertAsync([rotated], Live(LivePhotoKind.Apple)));
        var info = await MediaInfo.ProbeAsync(Tools().Ffprobe, outputs.Single(p => p.EndsWith(".mov", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);
        Assert.Equal((240, 320, 0), (info.Video!.Width, info.Video.Height, info.Video.Rotation));
        using var photo = new MagickImage(outputs.Single(p => p.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Equal((240u, 320u), (photo.Width, photo.Height));
    }

    [Fact]
    public async Task The_pair_keeps_one_name_and_never_takes_the_videos_own()
    {
        // clip.mov is the video itself: the pair becomes "clip (2)", even when told to overwrite.
        var video = await VideoAsync("clip.mov");
        var original = await File.ReadAllBytesAsync(video, TestContext.Current.CancellationToken);
        var outputs = Outputs(await fx.ConvertAsync([video], Live(LivePhotoKind.Apple, conflict: ConflictPolicy.Overwrite)));
        var folder = Path.GetDirectoryName(video)!;
        Assert.Equal(new[] { Path.Combine(folder, "clip (2).jpg"), Path.Combine(folder, "clip (2).mov") }, outputs.Order());
        Assert.Equal(original, await File.ReadAllBytesAsync(video, TestContext.Current.CancellationToken));

        // Another MOV of that name: both files move on to the next number together.
        var other = await VideoAsync("trip.mp4");
        var foreign = Path.Combine(Path.GetDirectoryName(other)!, "trip.mov");
        await File.WriteAllTextAsync(foreign, "someone else's video", TestContext.Current.CancellationToken);
        var renamed = Outputs(await fx.ConvertAsync([other], Live(LivePhotoKind.Apple)));
        Assert.Equal(new[] { "trip (2).jpg", "trip (2).mov" }, renamed.Select(Path.GetFileName).Order());
        Assert.Equal("someone else's video", await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken));

        // Skip: an existing photo skips the whole pair.
        var skipped = await fx.ConvertAsync([other], Live(LivePhotoKind.Apple, conflict: ConflictPolicy.Skip));
        Assert.Equal(FileState.Skipped, skipped.Files.Single().State);
    }

    [Fact]
    public async Task A_failed_live_photo_leaves_the_files_it_would_replace()
    {
        var folder = fx.NewFolder();
        var broken = Path.Combine(folder, "broken.mp4");
        await File.WriteAllBytesAsync(broken, [0, 0, 0, 24, .. "ftypisom"u8.ToArray(), .. new byte[64]], TestContext.Current.CancellationToken);
        var photo = Path.Combine(folder, "broken.jpg");
        var movie = Path.Combine(folder, "broken.mov");
        await File.WriteAllTextAsync(photo, "the user's photo", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(movie, "the user's video", TestContext.Current.CancellationToken);
        Tools();

        var job = await fx.ConvertAsync([broken], Live(LivePhotoKind.Apple, conflict: ConflictPolicy.Overwrite));
        Assert.Equal(FileState.Failed, job.Files.Single().State);
        Assert.Equal("the user's photo", await File.ReadAllTextAsync(photo, TestContext.Current.CancellationToken));
        Assert.Equal("the user's video", await File.ReadAllTextAsync(movie, TestContext.Current.CancellationToken));
        Assert.Equal(3, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Hdr_video_is_tone_mapped_to_sdr()
    {
        var (ffmpeg, _) = Tools();
        Assert.SkipUnless(FfmpegConverter.HasFilter(ffmpeg, "zscale"), "This FFmpeg has no zscale.");
        // HLG as phones record it; the encoder takes the colour tags from the frames.
        var video = await VideoAsync("hlg.mov", extra: ["-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc",
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error"]);
        Assert.True((await MediaInfo.ProbeAsync(Tools().Ffprobe, video, TestContext.Current.CancellationToken)).Video!.IsHdr);

        var movie = Outputs(await fx.ConvertAsync([video], Live(LivePhotoKind.Android))).Single();
        var info = await MediaInfo.ProbeAsync(Tools().Ffprobe, await TailAsync(movie), TestContext.Current.CancellationToken);
        Assert.False(info.Video!.IsHdr);
        Assert.Equal("yuv420p", info.Video.PixelFormat);
    }

    // ───────────────────────── Android ─────────────────────────

    [Theory]
    [InlineData(true, VideoCodec.Default)]
    [InlineData(false, VideoCodec.Hevc)]
    public async Task Android_motion_photo_is_one_jpg_with_the_mp4_at_its_end(bool audio, VideoCodec codec)
    {
        var video = await VideoAsync(audio: audio);
        var preset = Live(LivePhotoKind.Android);
        preset.Media.VideoCodec = codec;
        var photo = Assert.Single(Outputs(await fx.ConvertAsync([video], preset)));
        Assert.Equal("clip.MP.jpg", Path.GetFileName(photo));
        Assert.Equal("jpg", FormatRegistry.Detect(photo)!.Id);

        var bytes = await File.ReadAllBytesAsync(photo, TestContext.Current.CancellationToken);
        var segments = Segments(bytes);
        // JFIF first, then the XMP.
        Assert.Equal("JFIF", segments[0].Name);
        var xmp = segments.Single(s => s.Name == "http://ns.adobe.com/xap/1.0/");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes, xmp.Offset + 29, xmp.Length - 29));
        XNamespace camera = "http://ns.google.com/photos/1.0/camera/", container = "http://ns.google.com/photos/1.0/container/",
            item = "http://ns.google.com/photos/1.0/container/item/";
        var description = xml.Descendants().Single(e => e.Attribute(camera + "MotionPhoto") is not null);
        Assert.Equal("1", description.Attribute(camera + "MotionPhoto")!.Value);
        Assert.Equal("1", description.Attribute(camera + "MotionPhotoVersion")!.Value);
        Assert.Equal("1500000", description.Attribute(camera + "MotionPhotoPresentationTimestampUs")!.Value);
        var items = xml.Descendants(container + "Item").ToList();
        Assert.Equal(new[] { "Primary", "MotionPhoto" }, items.Select(i => i.Attribute(item + "Semantic")!.Value));
        Assert.Equal("image/jpeg", items[0].Attribute(item + "Mime")!.Value);
        Assert.Equal("video/mp4", items[1].Attribute(item + "Mime")!.Value);
        Assert.Equal("0", items[1].Attribute(item + "Padding")!.Value);
        var length = long.Parse(items[1].Attribute(item + "Length")!.Value, CultureInfo.InvariantCulture);
        Assert.Equal(length.ToString(CultureInfo.InvariantCulture), description.Attribute(camera + "MicroVideoOffset")!.Value);

        // The length is the MP4's, which ends the file: it starts with ftyp right after the JPEG's end marker.
        var start = bytes.Length - length;
        Assert.Equal("ftyp", Encoding.Latin1.GetString(bytes, (int)start + 4, 4));
        Assert.Equal(new byte[] { 0xff, 0xd9 }, bytes[((int)start - 2)..(int)start]);
        await using (var file = File.OpenRead(photo))
            Assert.Equal((start, length), MotionPhoto.FindVideo(file));

        var tail = await TailAsync(photo);
        var info = await MediaInfo.ProbeAsync(Tools().Ffprobe, tail, TestContext.Current.CancellationToken);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 2.9, 3.1);
        Assert.Equal(audio, info.Audio is not null);
        Assert.Equal(codec == VideoCodec.Hevc ? "hevc" : "h264", info.Video!.Codec);
        using (var picture = new MagickImage(photo))
            Assert.Equal((320u, 240u), (picture.Width, picture.Height));

        // Filee's own extraction takes the video out again.
        var extract = new Preset { TargetFormat = "mp4", Media = { ExtractMotionPhoto = true } };
        var extracted = Assert.Single(Outputs(await fx.ConvertAsync([photo], extract)));
        Assert.InRange((await MediaInfo.ProbeAsync(Tools().Ffprobe, extracted, TestContext.Current.CancellationToken)).Duration!.Value.TotalSeconds, 2.9, 3.1);
    }

    // ───────────────────────── Without FFmpeg ─────────────────────────

    [Fact]
    public void Live_photo_edges_are_taken_only_when_asked_for()
    {
        var planner = fx.Catalog.CreatePlanner(["ffmpeg"]);
        var step = Assert.Single(planner.Plan("mp4", "jpg", livePhoto: true)!.Steps);
        Assert.True(step.LivePhoto);
        Assert.True(Assert.Single(planner.Plan("mov", "heic", livePhoto: true)!.Steps).LivePhoto);
        Assert.All(planner.Plan("mp4", "jpg")!.Steps, s => Assert.False(s.LivePhoto));
        Assert.Null(planner.Plan("mp4", "png", livePhoto: true));
        Assert.Null(planner.Plan("png", "jpg", livePhoto: true));

        Assert.True(Live(LivePhotoKind.Apple).MakesLivePhotoOf("mp4"));
        Assert.True(Live(LivePhotoKind.Apple, "heic").MakesLivePhotoOf("mov"));
        Assert.False(Live(LivePhotoKind.Android, "heic").MakesLivePhotoOf("mov"));
        Assert.False(Live(LivePhotoKind.Apple).MakesLivePhotoOf("png"));
        Assert.False(Live(LivePhotoKind.None).MakesLivePhotoOf("mp4"));
        // Never every frame and a Live Photo at once.
        var both = Live(LivePhotoKind.Apple);
        both.Media.SaveEveryFrame = true;
        Assert.False(both.SavesFramesOf("mp4"));
        Assert.Equal(2, BuiltInData.LivePhotoPresets().Count(p => p.MakesLivePhotoOf("mp4")));
    }

    [Theory]
    [InlineData(0.0, 3.0, 1.5, 10.0, 0.0, 3.0, 45)]
    [InlineData(0.0, 3.0, 1.5, 1.0, 0.0, 1.0, 29)]
    [InlineData(8.0, 3.0, 1.0, 10.0, 8.0, 2.0, 30)]
    [InlineData(0.0, 60.0, 99.0, null, 0.0, 5.0, 149)]
    [InlineData(-4.0, 0.01, -1.0, 10.0, 0.0, 0.5, 0)]
    public void Clip_is_bounded_by_the_video_and_the_limits(double start, double length, double cover, double? duration,
        double expectedStart, double expectedLength, int expectedFrame)
    {
        var clip = LivePhotoWriter.Clip(new MediaOptions { LivePhotoStart = start, LivePhotoLength = length, LivePhotoCover = cover }, duration);
        Assert.Equal(expectedStart, clip.Start, 6);
        Assert.Equal(expectedLength, clip.Length, 6);
        Assert.Equal(expectedFrame, clip.CoverFrame);
        Assert.Throws<ConversionRefusedException>(() => LivePhotoWriter.Clip(new MediaOptions { LivePhotoStart = 10 }, 10));
    }

    [Fact]
    public void Clip_size_keeps_the_shown_shape_and_limits_the_shorter_side()
    {
        Assert.Equal((2560, 1440), LivePhotoWriter.ClipSize(new VideoStreamInfo(3840, 2160, 1, 0, 30), 1440));
        Assert.Equal((1440, 2560), LivePhotoWriter.ClipSize(new VideoStreamInfo(3840, 2160, 1, -90, 30), 1440));
        Assert.Equal((320, 240), LivePhotoWriter.ClipSize(new VideoStreamInfo(320, 240, 1, 0, 30), 1440));
        // Anamorphic DV: square pixels.
        Assert.Equal((640, 480), LivePhotoWriter.ClipSize(new VideoStreamInfo(720, 480, 8 / 9.0, 0, 30), 1440));
        Assert.Equal((960, 720), LivePhotoWriter.ClipSize(new VideoStreamInfo(1920, 1440, 1, 0, 30), 720));
    }

    [Fact]
    public void Maker_note_is_apples_layout_and_reads_back()
    {
        const string id = "6f2c8e8a-1a3b-4c5d-9e0f-112233445566";
        var note = AppleLivePhoto.MakerNote(id);
        Assert.StartsWith("Apple iOS\0\0\u0001MM", Encoding.Latin1.GetString(note));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(note.AsSpan(14)));
        Assert.Equal((17, 2, 37u, 32u), (BinaryPrimitives.ReadUInt16BigEndian(note.AsSpan(16)), BinaryPrimitives.ReadUInt16BigEndian(note.AsSpan(18)),
            BinaryPrimitives.ReadUInt32BigEndian(note.AsSpan(20)), BinaryPrimitives.ReadUInt32BigEndian(note.AsSpan(24))));
        Assert.Equal(id.ToUpperInvariant() + "\0", Encoding.ASCII.GetString(note, 32, 37));
        Assert.Equal(id, AppleLivePhoto.ReadMakerNote(note));
    }

    [Fact]
    public void Xmp_goes_after_jfif_and_exif()
    {
        using var image = new MagickImage(MagickColors.Teal, 8, 8);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Software, "test");
        image.SetProfile(exif);
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        var segments = Segments(jpeg);
        Assert.Equal(new[] { "JFIF", "Exif" }, segments.Take(2).Select(s => s.Name));
        Assert.Equal(segments[2].Offset - 4, MotionPhoto.XmpPosition(jpeg));
        Assert.Equal(2, MotionPhoto.XmpPosition([0xff, 0xd8, 0xff, 0xdb, 0, 2]));
        Assert.Throws<InvalidDataException>(() => MotionPhoto.XmpPosition([0x89, 0x50, 0x4e, 0x47]));
    }

    [Fact]
    public void Filter_list_is_read_from_ffmpeg_output()
    {
        var filters = FfmpegConverter.ParseFilters("""
            Filters:
              T.. = Timeline support
              ...
             .. libplacebo        N->V       Apply various GPU filters from libplacebo
             .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.
             TSC scale            V->V       Scale the input video size.
            """.Replace("\r", ""));
        Assert.Equal(new[] { "libplacebo", "scale", "zscale" }, filters.Order());
    }

    [Fact]
    public void Pair_allocation_moves_both_names_on_together()
    {
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "a.mov");
        File.WriteAllText(input, "");
        File.WriteAllText(Path.Combine(folder, "a (2).mov"), "");
        var output = new NumberingOutput(folder, "a");
        Assert.Equal(new[] { Path.Combine(folder, "a (3).jpg"), Path.Combine(folder, "a (3).mov") }, LivePhotoWriter.AllocatePair(output, "jpg", input));
        Assert.Null(LivePhotoWriter.AllocatePair(new SkippingOutput(), "jpg", input));
    }

    /// <summary>Hands out "name" + suffix, as the output rules do for a free name.</summary>
    private sealed class NumberingOutput(string folder, string name) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"{name}{suffix}.{extension}");
    }

    private sealed class SkippingOutput : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => null;
    }

    // ───────────────────────── Reading the outputs ─────────────────────────

    private sealed record StillImage(double Seconds, uint VideoTrackId, uint DescribedTrackId);

    /// <summary>
    /// The still image time track of <paramref name="movie"/>, checked against the layout an iPhone writes, with the
    /// time its one sample is shown at.
    /// </summary>
    private static StillImage StillImageTrack(string movie)
    {
        var bytes = File.ReadAllBytes(movie);
        using var stream = new MemoryStream(bytes);
        var moov = MovieBoxes.ReadMovieHeader(stream).Payload;
        var children = MovieBoxes.Children(moov);
        var mvhd = children.Single(c => c.Type == "mvhd").Payload;
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
        var video = children.Single(c => c.Type == "trak" && MovieBoxes.HandlerOf(c.Payload) == "vide").Payload;
        var videoId = BinaryPrimitives.ReadUInt32BigEndian(Child(video, "tkhd").AsSpan(12));
        var track = children.Single(c => c.Type == "trak" && MovieBoxes.HandlerOf(c.Payload) == "meta").Payload;

        var tkhd = Child(track, "tkhd");
        Assert.Equal(0xfu, BinaryPrimitives.ReadUInt32BigEndian(tkhd) & 0xffffff);
        var mdia = Child(track, "mdia");
        Assert.Equal("mhlrmetaappl", Encoding.Latin1.GetString(Child(mdia, "hdlr"), 4, 12));
        Assert.Equal(timescale, BinaryPrimitives.ReadUInt32BigEndian(Child(mdia, "mdhd").AsSpan(12)));
        var minf = Child(mdia, "minf");
        Assert.NotNull(Child(minf, "gmhd"));
        var stbl = Child(minf, "stbl");
        var stsd = Child(stbl, "stsd");
        var entry = MovieBoxes.Children(stsd, 8).Single();
        Assert.Equal("mebx", entry.Type);
        var key = MovieBoxes.Children(MovieBoxes.Children(entry.Payload, 8).Single(c => c.Type == "keys").Payload).Single();
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(Encoding.Latin1.GetBytes(key.Type)));
        var parts = MovieBoxes.Children(key.Payload);
        Assert.Equal("mdta" + AppleLivePhoto.StillImageTime, Encoding.UTF8.GetString(parts.Single(p => p.Type == "keyd").Payload));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 65 }, parts.Single(p => p.Type == "dtyp").Payload);

        // One 9-byte sample: item size, local key 1, int8 -1.
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(Child(stbl, "stsz").AsSpan(12)));
        var offset = BinaryPrimitives.ReadUInt32BigEndian(Child(stbl, "stco").AsSpan(8));
        Assert.Equal(new byte[] { 0, 0, 0, 9, 0, 0, 0, 1, 0xff }, bytes[(int)offset..(int)(offset + 9)]);

        var described = BinaryPrimitives.ReadUInt32BigEndian(Child(Child(track, "tref"), "cdsc"));
        // The edit list: an empty edit up to the still (when it isn't the first frame), then the sample.
        var elst = Child(Child(track, "edts"), "elst");
        var edits = BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(4));
        var before = edits == 2 && BinaryPrimitives.ReadInt32BigEndian(elst.AsSpan(12)) == -1
            ? BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(8))
            : 0;
        return new((double)before / timescale, videoId, described);
    }

    private static byte[] Child(byte[] parent, string type) => MovieBoxes.Children(parent).Single(c => c.Type == type).Payload;

    /// <summary>The JPEG's segments up to the picture data: marker, offset of the payload, its length, its signature.</summary>
    private static List<(byte Marker, int Offset, int Length, string Name)> Segments(byte[] jpeg)
    {
        var segments = new List<(byte, int, int, string)>();
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xff && jpeg[at + 1] != 0xda)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2)) - 2;
            var name = Encoding.Latin1.GetString(jpeg, at + 4, Math.Min(length, 40)).Split('\0')[0];
            segments.Add((jpeg[at + 1], at + 4, length, name));
            at += 4 + length;
        }
        return segments;
    }

    private static async Task<byte[]> ReadAtAsync(string path, long offset, int count)
    {
        await using var file = File.OpenRead(path);
        file.Position = offset;
        var data = new byte[count];
        await file.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        return data;
    }

    /// <summary>The MP4 appended to a Motion Photo, saved on its own.</summary>
    private async Task<string> TailAsync(string photo)
    {
        await using var file = File.OpenRead(photo);
        var (offset, length) = MotionPhoto.FindVideo(file)!.Value;
        var path = Path.Combine(fx.NewFolder(), "tail.mp4");
        file.Position = offset;
        await using var target = File.Create(path);
        await MovieBoxes.CopyRangeAsync(file, target, length, TestContext.Current.CancellationToken);
        return path;
    }
}
