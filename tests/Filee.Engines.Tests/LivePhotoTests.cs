// Live / Motion Photo → video. Every photo is built here: a real video from FFmpeg, appended to a JPEG or boxed in a
// HEIC as Pixel, Samsung and older Google cameras do, or saved next to an iPhone-style photo with a shared identifier.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class LivePhotoTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string LiveId = "6F2C8E8A-1A3B-4C5D-9E0F-112233445566";

    private static (string Ffmpeg, string Ffprobe) Tools()
    {
        var tools = FfmpegConverter.Locate();
        Assert.SkipUnless(tools is not null, "FFmpeg is not installed.");
        return tools.Value;
    }

    private static Preset Extract(string target = "mp4", ConflictPolicy conflict = ConflictPolicy.Rename) =>
        new() { TargetFormat = target, Media = { ExtractMotionPhoto = true }, Output = { Conflict = conflict } };

    /// <summary>A test picture video (2 s, 160×120 unless asked otherwise) with or without a sine tone, as MP4 or MOV.</summary>
    private async Task<byte[]> VideoAsync(bool audio = true, string extension = "mp4", int seconds = 2, string size = "160x120",
        string[]? extra = null)
    {
        var path = Path.Combine(fx.NewFolder(), "clip ü 中文." + extension);
        var result = await FfmpegRunner.RunAsync(Tools().Ffmpeg, [
            .. MediaEncoding.Common, "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30",
            .. (audio ? new[] { "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" } : []),
            "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", .. extra ?? [], path,
        ], null, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
    }

    private static byte[] Jpeg(uint width = 320, uint height = 200, byte[]? makerNote = null)
    {
        using var image = new MagickImage("gradient:#7F77DD-#1D9E75", width, height);
        if (makerNote is not null)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.MakerNote, makerNote);
            image.SetProfile(exif);
        }
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>The JPEG with an XMP segment right after its start marker.</summary>
    private static byte[] JpegWithXmp(byte[] jpeg, string xml)
    {
        var payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xml);
        return [0xff, 0xd8, 0xff, 0xe1, .. U16((ushort)(payload.Length + 2)), .. payload, .. jpeg.Skip(2)];
    }

    /// <summary>Motion Photo 1.0 XMP as a Pixel writes it (a gain map listed before the video), or with elements.</summary>
    private static string MotionXmp(long length, bool elements = false) => elements
        ? $"""
          <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/">
              <GCamera:MotionPhoto>1</GCamera:MotionPhoto>
              <GCamera:MotionPhotoVersion>1</GCamera:MotionPhotoVersion>
              <Container:Directory><rdf:Seq>
                <rdf:li rdf:parseType="Resource"><Container:Item><Item:Mime>image/jpeg</Item:Mime><Item:Semantic>Primary</Item:Semantic></Container:Item></rdf:li>
                <rdf:li rdf:parseType="Resource"><Container:Item><Item:Mime>video/mp4</Item:Mime><Item:Semantic>MotionPhoto</Item:Semantic><Item:Length>{length}</Item:Length></Container:Item></rdf:li>
              </rdf:Seq></Container:Directory>
            </rdf:Description>
          </rdf:RDF></x:xmpmeta>
          """
        : $"""
          <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/"
              GCamera:MotionPhoto="1" GCamera:MotionPhotoVersion="1" GCamera:MotionPhotoPresentationTimestampUs="500000">
              <Container:Directory><rdf:Seq>
                <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/></rdf:li>
                <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="GainMap" Item:Mime="image/jpeg" Item:Length="0"/></rdf:li>
                <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="video/mp4" Item:Semantic="MotionPhoto" Item:Length="{length}" Item:Padding="0"/></rdf:li>
              </rdf:Seq></Container:Directory>
            </rdf:Description>
          </rdf:RDF></x:xmpmeta>
          """;

    /// <summary>
    /// Samsung's SEF trailer as its cameras append it after the video: a "MotionPhoto_Data" block before the video, a
    /// directory after it. The XMP length counts the directory too.
    /// </summary>
    private static (byte[] Before, byte[] After) SamsungTrailer()
    {
        byte[] before = [0, 0, 0x30, 0x0a, .. BitConverter.GetBytes(16), .. "MotionPhoto_Data"u8.ToArray()];
        byte[] after = [.. "SEFH"u8.ToArray(), .. BitConverter.GetBytes(107), .. BitConverter.GetBytes(0)];
        return (before, [.. after, .. BitConverter.GetBytes(after.Length), .. "SEFT"u8.ToArray()]);
    }

    private async Task<string> MotionJpegAsync(string name, string form = "attributes", bool audio = true)
    {
        var video = await VideoAsync(audio);
        var (before, after) = form == "samsung" ? SamsungTrailer() : ([], []);
        var length = video.Length + after.Length;
        var xmp = form == "microvideo"
            ? $"<x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:c='http://ns.google.com/photos/1.0/camera/' c:MicroVideo='1' c:MicroVideoVersion='1' c:MicroVideoOffset='{length}'/>"
            : MotionXmp(length, elements: form == "elements");
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllBytesAsync(path, [.. JpegWithXmp(Jpeg(), xmp), .. before, .. video, .. after], TestContext.Current.CancellationToken);
        return path;
    }

    private static string Output(ConversionJob job)
    {
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static async Task<MediaInfo> ProbeAsync(string path) =>
        await MediaInfo.ProbeAsync(Tools().Ffprobe, path, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("attributes", true)]
    [InlineData("attributes", false)]
    [InlineData("elements", true)]
    [InlineData("samsung", true)]
    [InlineData("microvideo", true)]
    public async Task Android_motion_photo_jpeg_gives_its_video_and_sound(string form, bool audio)
    {
        var photo = await MotionJpegAsync("PXL_20260101_120000000.MP.jpg", form, audio);
        Assert.Equal("jpg", FormatRegistry.Detect(photo)!.Id);
        var original = await File.ReadAllBytesAsync(photo, TestContext.Current.CancellationToken);

        var movie = Output(await fx.ConvertAsync([photo], Extract()));
        Assert.Equal(".mp4", Path.GetExtension(movie));
        var info = await ProbeAsync(movie);
        Assert.Equal(160, info.Video!.Width);
        Assert.Equal(audio, info.Audio is not null);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.9, 2.1);
        Assert.Equal(original, await File.ReadAllBytesAsync(photo, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Samsung_trailer_after_the_video_is_left_out()
    {
        var photo = await MotionJpegAsync("20260101_120000.jpg", "samsung");
        var (_, after) = SamsungTrailer();
        await using var file = File.OpenRead(photo);
        var video = MotionPhoto.FindVideo(file)!.Value;
        Assert.Equal(file.Length - after.Length, video.Offset + video.Length);
    }

    [Fact]
    public async Task Heic_motion_photo_reads_its_mpvd_box_even_with_a_trailer_after_it()
    {
        var video = await VideoAsync();
        var (_, after) = SamsungTrailer();
        var path = Path.Combine(fx.NewFolder(), "20260101_120000.heic");
        await File.WriteAllBytesAsync(path, [.. Box("ftyp", "heic\0\0\0\0mif1heic"u8.ToArray()), .. Box("meta", new byte[12]),
            .. Box("mpvd", video), .. after], TestContext.Current.CancellationToken);

        await using (var file = File.OpenRead(path))
            Assert.Equal(video.Length, MotionPhoto.FindVideo(file)!.Value.Length);
        var movie = Output(await fx.ConvertAsync([path], Extract("mov")));
        Assert.NotNull((await ProbeAsync(movie)).Audio);
    }

    /// <summary>IMG_0001.JPG with Apple's maker note and IMG_0001.MOV with the same content identifier.</summary>
    private async Task<(string Photo, string Movie)> ApplePairAsync(string folder, string movieId = LiveId, string? xmp = null)
    {
        var photo = Path.Combine(folder, "IMG_0001.JPG");
        var jpeg = Jpeg(makerNote: AppleMakerNote(LiveId));
        await File.WriteAllBytesAsync(photo, xmp is null ? jpeg : JpegWithXmp(jpeg, xmp), TestContext.Current.CancellationToken);
        var movie = Path.Combine(folder, "IMG_0001.MOV");
        await File.WriteAllBytesAsync(movie, await VideoAsync(extension: "mov", extra:
            ["-metadata", $"com.apple.quicktime.content.identifier={movieId}", "-movflags", "use_metadata_tags"]),
            TestContext.Current.CancellationToken);
        return (photo, movie);
    }

    [Fact]
    public async Task Iphone_photo_gives_the_mov_next_to_it_only_with_the_same_identifier()
    {
        var (photo, movie) = await ApplePairAsync(fx.NewFolder());
        Assert.Equal(LiveId, AppleLivePhoto.ImageIdentifier(photo), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(LiveId, AppleLivePhoto.MovieIdentifier(movie), StringComparer.OrdinalIgnoreCase);
        var output = Output(await fx.ConvertAsync([photo], Extract()));
        Assert.NotNull((await ProbeAsync(output)).Audio);

        // XMP that can't be read (here a DTD) doesn't hide the MOV.
        var (hostile, _) = await ApplePairAsync(fx.NewFolder(), xmp: "<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>");
        Output(await fx.ConvertAsync([hostile], Extract()));

        // A same-named video of another Live Photo is not this photo's.
        var (other, _) = await ApplePairAsync(fx.NewFolder(), Guid.NewGuid().ToString("D"));
        var failed = await fx.ConvertAsync([other], Extract());
        Assert.Equal("error.no_motion_photo", failed.Files.Single().ErrorKey);
    }

    [Fact]
    public async Task Overwrite_never_writes_over_the_live_photos_own_movie()
    {
        var (photo, movie) = await ApplePairAsync(fx.NewFolder());
        var original = await File.ReadAllBytesAsync(movie, TestContext.Current.CancellationToken);

        var output = Output(await fx.ConvertAsync([photo], Extract("mov", ConflictPolicy.Overwrite)));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(photo)!, "IMG_0001 (2).mov"), output);
        Assert.Equal(original, await File.ReadAllBytesAsync(movie, TestContext.Current.CancellationToken));
        Assert.NotNull((await ProbeAsync(output)).Video);
        // Once more: the numbered file is the output now, and is overwritten.
        Assert.Equal(output, Output(await fx.ConvertAsync([photo], Extract("mov", ConflictPolicy.Overwrite))));
        Assert.Equal(original, await File.ReadAllBytesAsync(movie, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_extraction_leaves_the_file_it_would_replace()
    {
        // Long enough to be cancelled while FFmpeg encodes (VP9): the output is written in the work directory meanwhile.
        var video = await VideoAsync(seconds: 60, size: "1280x720", extra: ["-preset", "ultrafast"]);
        var folder = fx.NewFolder();
        var photo = Path.Combine(folder, "long.jpg");
        await File.WriteAllBytesAsync(photo, [.. JpegWithXmp(Jpeg(), MotionXmp(video.Length)), .. video], TestContext.Current.CancellationToken);
        var existing = Path.Combine(folder, "long.webm");
        await File.WriteAllTextAsync(existing, "the user's own file", TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(30)); // in case no progress arrives at all
        var converter = new FfmpegConverter();
        converter.GetStatus();
        var step = new ConversionStep(photo, "jpg", "webm",
            new Preset { TargetFormat = "webm", Media = { ExtractMotionPhoto = true, Quality = MediaQuality.High } },
            new FixedOutput(existing), fx.NewFolder(), MotionPhoto: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(step, new CancelOnProgress(cancellation), cancellation.Token));
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    /// <summary>Cancels once FFmpeg reports progress (synchronously: Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value)
        {
            if (value > 0.05)
                cancellation.Cancel();
        }
    }

    private async Task<string> LivpAsync(string movieId = LiveId, string prefix = "")
    {
        var path = Path.Combine(fx.NewFolder(), "IMG_3218.livp");
        var movie = await VideoAsync(extension: "mov", extra:
            ["-metadata", $"com.apple.quicktime.content.identifier={movieId}", "-movflags", "use_metadata_tags"]);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // As iCloud names them; a macOS resource fork next to them is no second video.
        foreach (var (name, data) in new[]
                 {
                     ("IMG_3218.JPG.jpeg", Jpeg(240, 180, AppleMakerNote(LiveId))),
                     ("IMG_3218.JPG.mov", movie),
                     ("__MACOSX/._IMG_3218.JPG.mov", new byte[] { 0, 5, 22, 7 }),
                 })
        {
            await using var entry = zip.CreateEntry(prefix + name, CompressionLevel.NoCompression).Open();
            await entry.WriteAsync(data, TestContext.Current.CancellationToken);
        }
        return path;
    }

    [Fact]
    public async Task Livp_gives_its_video_without_the_option_and_its_photo_as_a_picture()
    {
        var livp = await LivpAsync();
        Assert.Equal("livp", FormatRegistry.Detect(livp)!.Id);
        var movie = Output(await fx.ConvertAsync([livp], new Preset { TargetFormat = "mp4" }));
        Assert.InRange((await ProbeAsync(movie)).Duration!.Value.TotalSeconds, 1.9, 2.1);

        var png = Output(await fx.ConvertAsync([livp], new Preset { TargetFormat = "png" }));
        using var still = new MagickImage(png);
        Assert.Equal((240u, 180u), (still.Width, still.Height));
    }

    [Fact]
    public async Task Livp_pair_comes_from_the_package_and_its_names_never_become_paths()
    {
        // Identifiers that don't match are only logged: the package says which photo and video belong together.
        var livp = await LivpAsync(Guid.NewGuid().ToString("D"), prefix: "../../outside/");
        var work = fx.NewFolder();
        var (video, companion) = await LivePhotoSource.ExtractAsync(livp, "livp", work,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Null(companion);
        Assert.Equal(work, Path.GetDirectoryName(video));
        Assert.Single(Directory.GetFiles(work));
        Assert.False(Directory.Exists(Path.Combine(fx.Root, "outside")));

        var two = Path.Combine(fx.NewFolder(), "two.livp");
        using (var zip = ZipFile.Open(two, ZipArchiveMode.Create))
        {
            zip.CreateEntry("a.mov").Open().Dispose();
            zip.CreateEntry("b.mov").Open().Dispose();
        }
        var failed = await fx.ConvertAsync([two], new Preset { TargetFormat = "mp4" });
        Assert.Equal("error.no_motion_photo", failed.Files.Single().ErrorKey);
        Assert.Null(failed.Files.Single().ErrorDetail);
    }

    [Fact]
    public async Task Motion_photo_is_an_ordinary_jpg_without_the_option()
    {
        var photo = await MotionJpegAsync("PXL_20260101_120000000.MP.jpg");
        using (var png = new MagickImage(Output(await fx.ConvertAsync([photo], new Preset { TargetFormat = "png" }))))
            Assert.Equal((320u, 200u), (png.Width, png.Height));
        // A video of the still picture, as for any photo.
        var still = Output(await fx.ConvertAsync([photo], new Preset { TargetFormat = "mp4" }));
        Assert.Null((await ProbeAsync(still)).Audio);

        var plain = EngineFixture.MakeImage(fx.NewFolder(), "plain.jpg", MagickFormat.Jpeg);
        var failed = await fx.ConvertAsync([plain], Extract());
        Assert.Equal("error.no_motion_photo", failed.Files.Single().ErrorKey);
    }

    [Theory]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("png", MagickFormat.Png)]
    [InlineData("webp", MagickFormat.WebP)]
    public async Task The_option_leaves_other_pictures_to_their_usual_route(string format, MagickFormat magick)
    {
        Tools();
        var picture = EngineFixture.MakeImage(fx.NewFolder(), "picture." + format, magick);
        Assert.False(Extract().ExtractsMotionOf(format));
        Assert.NotNull((await ProbeAsync(Output(await fx.ConvertAsync([picture], Extract())))).Video);
    }

    [Fact]
    public void Motion_edges_are_taken_only_when_asked_for()
    {
        var planner = fx.Catalog.CreatePlanner(["ffmpeg"]);
        var step = Assert.Single(planner.Plan("jpg", "mp4", motionPhoto: true)!.Steps);
        Assert.True(step.MotionPhoto);
        Assert.All(planner.Plan("jpg", "mp4")!.Steps, s => Assert.False(s.MotionPhoto));
        Assert.Equal(2, planner.Plan("jpg", "mp4")!.Steps.Count);
        Assert.Null(planner.Plan("png", "mp4", motionPhoto: true));
        Assert.Null(planner.Plan("jpg", "webm", motionPhoto: true));
        Assert.Null(planner.Plan("jpg", "png", motionPhoto: true));
        Assert.Equal("magick", Assert.Single(planner.Plan("livp", "png")!.Steps).Converter.Id);
        Assert.Equal("jpg", FormatRegistry.Detect("PXL_20260101.MP.jpg")!.Id);
        Assert.True(new Preset { TargetFormat = "mov" }.ExtractsMotionOf("livp"));
        Assert.False(new Preset { TargetFormat = "mov" }.ExtractsMotionOf("jpg"));
    }

    [Fact]
    public async Task Stale_disabled_or_hostile_metadata_is_no_motion_photo()
    {
        var jpeg = Jpeg();
        var video = Box("ftyp", "isom\0\0\0\0isom"u8.ToArray());
        // The XMP says there's a video, but an editor rewrote the picture without it.
        using var stale = new MemoryStream(JpegWithXmp(jpeg, MotionXmp(video.Length)));
        Assert.Throws<InvalidDataException>(() => MotionPhoto.FindVideo(stale));
        // A video without a movie header is not one either.
        using var headless = new MemoryStream([.. JpegWithXmp(jpeg, MotionXmp(video.Length)), .. video]);
        Assert.Throws<InvalidDataException>(() => MotionPhoto.FindVideo(headless));
        using var disabled = new MemoryStream([.. JpegWithXmp(jpeg, MotionXmp(video.Length).Replace("GCamera:MotionPhoto=\"1\"", "GCamera:MotionPhoto=\"0\"")), .. video]);
        Assert.Null(MotionPhoto.FindVideo(disabled));
        // An XMP packet padded with NUL bytes after its end (as some editors write it) is read.
        byte[] movie = [.. video, .. Box("moov", new byte[8])];
        using var padded = new MemoryStream([.. JpegWithXmp(jpeg, MotionXmp(movie.Length) + "\0\0\0"), .. movie]);
        Assert.Equal(movie.Length, MotionPhoto.FindVideo(padded)!.Value.Length);
        using var hostile = new MemoryStream(JpegWithXmp(jpeg, "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///secret'>]><x>&leak;</x>"));
        Assert.Throws<System.Xml.XmlException>(() => MotionPhoto.FindVideo(hostile));

        // Through a conversion (FFmpeg's edge): the user gets the plain message, not the parser's.
        Tools();
        var path = Path.Combine(fx.NewFolder(), "hostile.jpg");
        await File.WriteAllBytesAsync(path, hostile.ToArray(), TestContext.Current.CancellationToken);
        var job = await fx.ConvertAsync([path], Extract());
        Assert.Equal("error.no_motion_photo", job.Files.Single().ErrorKey);
        Assert.Null(job.Files.Single().ErrorDetail);
    }

    [Fact]
    public void Invalid_and_extended_media_boxes_are_bounded()
    {
        using var beyond = new MemoryStream([.. U32(100), .. "mpvd"u8.ToArray()]);
        Assert.Throws<InvalidDataException>(() => MotionPhoto.FindVideo(beyond));
        using var huge = new MemoryStream([.. U32(1), .. "mpvd"u8.ToArray(), .. U64(ulong.MaxValue)]);
        Assert.Throws<InvalidDataException>(() => MotionPhoto.FindVideo(huge));
        byte[] movie = [.. Box("ftyp", "isom\0\0\0\0isom"u8.ToArray()), .. Box("moov", new byte[8])];
        using var large = new MemoryStream([.. Box("ftyp", "heic\0\0\0\0heic"u8.ToArray()), .. U32(1), .. "mpvd"u8.ToArray(), .. U64((ulong)movie.Length + 16), .. movie]);
        Assert.Equal(movie.Length, MotionPhoto.FindVideo(large)!.Value.Length);
    }

    /// <summary>Apple's maker note with only tag 17, the content identifier (offsets count from the note's start).</summary>
    private static byte[] AppleMakerNote(string identifier)
    {
        var id = Encoding.ASCII.GetBytes(identifier.ToUpperInvariant() + "\0");
        return [.. "Apple iOS\0"u8.ToArray(), .. U16(1), .. "MM"u8.ToArray(), .. U16(1),
            .. U16(17), .. U16(2), .. U32((uint)id.Length), .. U32(32), .. U32(0), .. id];
    }

    private static byte[] Box(string type, byte[] payload) =>
        [.. U32((uint)(payload.Length + 8)), .. Encoding.Latin1.GetBytes(type), .. payload];

    private static byte[] U16(ushort value)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, value);
        return data;
    }

    private static byte[] U32(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        return data;
    }

    private static byte[] U64(ulong value)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, value);
        return data;
    }
}
