// Playback speed of animations (ImageOptions.AnimationSpeed): frame delays rewritten in each format's units, frames
// dropped where a delay would get shorter than players show. Animations are generated in the tests.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Tests;

public class AnimationSpeedTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    /// <summary>A frame of its own colour for every delay (hundredths of a second), playing <paramref name="loops"/> times.</summary>
    private static string MakeGif(string folder, uint[] delays, uint loops = 3, string name = "anim.gif")
    {
        using var frames = new MagickImageCollection();
        for (var i = 0; i < delays.Length; i++)
            frames.Add(new MagickImage(Colour(i), 32, 24) { AnimationDelay = delays[i], AnimationIterations = loops });
        var path = Path.Combine(folder, name);
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    private static MagickColor Colour(int i) => new((byte)(i * 30 % 256), (byte)(255 - i * 30 % 256), (byte)(i * 70 % 256));

    private static bool IsColour(IMagickImage<byte> frame, int i)
    {
        var pixel = frame.GetPixels().GetPixel(16, 12).ToColor()!;
        var expected = Colour(i);
        return Math.Abs(pixel.R - expected.R) < 12 && Math.Abs(pixel.G - expected.G) < 12 && Math.Abs(pixel.B - expected.B) < 12;
    }

    private static Preset Speed(string target, double speed) =>
        new() { TargetFormat = target, Image = { AnimationSpeed = speed }, Output = { FileNamePattern = "{name}_" + speed } };

    /// <summary>The duration of every ANMF chunk of an animated WebP, in milliseconds.</summary>
    private static List<int> WebpDurations(string path)
    {
        var file = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(file, 0, 4));
        var durations = new List<int>();
        for (var position = 12; position + 8 <= file.Length;)
        {
            var type = Encoding.ASCII.GetString(file, position, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4));
            if (type == "ANMF")
                durations.Add(file[position + 20] | file[position + 21] << 8 | file[position + 22] << 16);
            position += 8 + size + (size & 1);
        }
        return durations;
    }

    [Theory]
    [InlineData(2.0, new uint[] { 5, 10, 15, 20 })]
    [InlineData(0.5, new uint[] { 20, 40, 60, 80 })]
    [InlineData(1.0, new uint[] { 10, 20, 30, 40 })]
    public async Task Gif_frame_delays_follow_the_speed(double speed, uint[] expected)
    {
        var dir = fx.NewFolder();
        var gif = MakeGif(dir, [10, 20, 30, 40]);

        var job = await fx.ConvertAsync([gif], Speed("gif", speed));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(expected, frames.Select(f => f.AnimationDelay));
        Assert.All(frames, f => Assert.Equal(3u, f.AnimationIterations)); // the loop count stays
        Assert.True(IsColour(frames[3], 3), "every frame is kept, in order");
    }

    [Fact]
    public async Task Webp_and_apng_get_the_speed_in_milliseconds()
    {
        var dir = fx.NewFolder();
        var gif = MakeGif(dir, [10, 20, 30, 40]);

        // 1.5×: 66.7, 133.3, 200, 266.7 ms, rounded on the running total so the animation lasts 667 ms.
        var webp = await fx.ConvertAsync([gif], Speed("webp", 1.5));
        AssertDone(webp);
        Assert.Equal([67, 133, 200, 267], WebpDurations(webp.Outputs.Single()));

        // 0.75×: 133.3, 266.7, 400, 533.3 ms.
        var apng = await fx.ConvertAsync([gif], Speed("apng", 0.75));
        AssertDone(apng);
        var chunks = ApngBuilder.Chunks(File.ReadAllBytes(apng.Outputs.Single()));
        var delays = chunks.Where(c => c.Type == "fcTL")
            .Select(c => (BinaryPrimitives.ReadUInt16BigEndian(c.Data.AsSpan(20)), BinaryPrimitives.ReadUInt16BigEndian(c.Data.AsSpan(22))))
            .ToList();
        Assert.Equal([((ushort)133, (ushort)1000), ((ushort)267, (ushort)1000), ((ushort)400, (ushort)1000), ((ushort)533, (ushort)1000)], delays);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(chunks.Single(c => c.Type == "acTL").Data.AsSpan(4)));
    }

    [Fact]
    public async Task Sped_up_frames_shorter_than_players_show_are_dropped_evenly()
    {
        var dir = fx.NewFolder();
        // Eight frames of 4/100 s at 4×: 1/100 s each, which browsers would show for 10/100 s. Every other frame is
        // dropped instead and the rest get 2/100 s: the animation still lasts 8/100 s.
        var gif = MakeGif(dir, [4, 4, 4, 4, 4, 4, 4, 4]);

        var job = await fx.ConvertAsync([gif], Speed("gif", 4));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal([2u, 2u, 2u, 2u], frames.Select(f => f.AnimationDelay));
        for (var i = 0; i < frames.Count; i++)
            Assert.True(IsColour(frames[i], i * 2), $"frame {i} is the source's frame {i * 2}");

        // WebP keeps milliseconds, with the same 20 ms floor: 40 ms frames at 4× become 20 ms, every other one dropped.
        var webp = await fx.ConvertAsync([gif], Speed("webp", 4));
        AssertDone(webp);
        Assert.Equal([20, 20, 20, 20], WebpDurations(webp.Outputs.Single()));
    }

    [Fact]
    public void Delays_are_rounded_on_the_running_total_and_short_ones_count_as_players_show_them()
    {
        using var images = new MagickImageCollection();
        // 0 and 1 hundredths are shown for 10/100 s by browsers: that is what gets sped up.
        foreach (var delay in new uint[] { 0, 1, 3, 3, 3 })
            images.Add(new MagickImage(MagickColors.Red, 4, 4) { AnimationDelay = delay, AnimationTicksPerSecond = 100 });
        var frames = images.ToList();

        var fast = AnimationTiming.ChangeSpeed(frames, 2, "gif");

        // 5, 5, 1.5, 1.5, 1.5 → starts 0, 5, 10, 11.5 (12), 13, end 14.5 (15): the last frame would start 1/100 s
        // after the one before, so it is dropped and that one stays up until the end.
        Assert.Equal([5u, 5u, 2u, 3u], fast.Select(f => f.AnimationDelay));
        Assert.All(fast, f => Assert.Equal(100, f.AnimationTicksPerSecond));
        Assert.Same(frames[3], fast[3]);

        // One frame, or speed 1: nothing to do.
        Assert.Equal(frames, AnimationTiming.ChangeSpeed(frames, 1, "gif"));
        Assert.Single(AnimationTiming.ChangeSpeed([frames[0]], 3, "webp"));
    }

    [Fact]
    public async Task A_still_target_takes_the_first_frame_whatever_the_speed()
    {
        var dir = fx.NewFolder();
        var gif = MakeGif(dir, [10, 20, 30, 40]);

        var job = await fx.ConvertAsync([gif], Speed("png", 2));

        AssertDone(job);
        using var png = new MagickImage(job.Outputs.Single());
        Assert.True(IsColour(png, 0));
    }

    [Fact]
    public void Video_to_gif_and_apng_changes_the_timestamps_before_the_frame_rate()
    {
        var info = new MediaInfo(TimeSpan.FromSeconds(3), new VideoStreamInfo(1920, 1080, 1, 0, 29.97), null, null);

        var gif = MediaEncoding.Plan("in.mp4", "out.gif", "gif", new MediaOptions(), info, "work", animationSpeed: 2);
        Assert.All(gif, pass => Assert.Contains("[0:V:0]setpts=PTS/2,fps=12,", After(pass.Arguments, "-filter_complex")));

        var apng = MediaEncoding.Plan("in.mp4", "out.apng", "apng", new MediaOptions { Quality = MediaQuality.High }, info, "work",
            animationSpeed: 0.75).Single();
        Assert.StartsWith("setpts=PTS/0.75,fps=15,", After(apng.Arguments, "-vf"));

        var normal = MediaEncoding.Plan("in.mp4", "out.apng", "apng", new MediaOptions(), info, "work").Single();
        Assert.StartsWith("fps=", After(normal.Arguments, "-vf"));
    }

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];
}
