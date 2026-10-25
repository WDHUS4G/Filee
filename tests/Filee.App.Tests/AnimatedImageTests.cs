// AnimatedImage: frames of GIF and animated WebP decoded by Skia (partial frames drawn over the ones before), the frame
// clock (durations, loops, the last frame staying), pausing while hidden and giving the memory back when closed.
// The pictures are made by Magick.NET in a temp folder for each test.

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;
using ImageMagick;

namespace Filee.App.Tests;

[Collection("Motion")]
public sealed class AnimatedImageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-animated-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>
    /// 20 × 20: red (100 ms), then a 10 × 10 green square at (10, 10) over it (200 ms), then all blue (300 ms).
    /// </summary>
    private string Animation(MagickFormat format, uint iterations = 0)
    {
        var path = Path.Combine(_folder, "anim." + format.ToString().ToLowerInvariant());
        using var frames = new MagickImageCollection();
        frames.Add(new MagickImage(MagickColors.Red, 20, 20) { AnimationDelay = 10, GifDisposeMethod = GifDisposeMethod.None });
        var square = new MagickImage(MagickColors.Lime, 10, 10) { AnimationDelay = 20, GifDisposeMethod = GifDisposeMethod.None };
        square.Page = new MagickGeometry(10, 10, 10, 10);
        frames.Add(square);
        frames.Add(new MagickImage(MagickColors.Blue, 20, 20) { AnimationDelay = 30, GifDisposeMethod = GifDisposeMethod.None });
        foreach (var frame in frames)
            frame.AnimationIterations = iterations;
        if (format == MagickFormat.WebP)
            frames.Write(path, new ImageMagick.Formats.WebPWriteDefines { Lossless = true });
        else
            frames.Write(path, format);
        return path;
    }

    /// <summary>The BGRA pixel at (x, y) of a native buffer.</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(IntPtr buffer, int rowBytes, int x, int y)
    {
        var pixel = new byte[4];
        Marshal.Copy(buffer + y * rowBytes + x * 4, pixel, 0, 4);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    [Theory]
    [InlineData(MagickFormat.Gif)]
    [InlineData(MagickFormat.WebP)]
    public void Frames_are_decoded_over_the_ones_they_need(MagickFormat format)
    {
        using var stream = File.OpenRead(Animation(format));
        using var decoder = AnimatedImageDecoder.Open(stream);
        Assert.NotNull(decoder);
        Assert.Equal((20, 20), (decoder.Width, decoder.Height));
        Assert.Equal(3, decoder.FrameCount);
        Assert.Equal([100, 200, 300], decoder.Durations);
        Assert.Equal(-1, decoder.RepetitionCount); // loops forever

        var rowBytes = decoder.Width * 4;
        var buffer = Marshal.AllocHGlobal(rowBytes * decoder.Height);
        try
        {
            // Straight to the partial frame: the red frame under it is decoded first.
            Assert.True(decoder.Decode(1, buffer, rowBytes, freshBuffer: true));
            Assert.Equal((0, 0, 255, 255), Pixel(buffer, rowBytes, 2, 2));
            Assert.Equal((0, 255, 0, 255), Pixel(buffer, rowBytes, 15, 15));

            Assert.True(decoder.Decode(2, buffer, rowBytes));
            Assert.Equal((255, 0, 0, 255), Pixel(buffer, rowBytes, 2, 2));

            // Back to the start of the loop.
            Assert.True(decoder.Decode(0, buffer, rowBytes));
            Assert.Equal((0, 0, 255, 255), Pixel(buffer, rowBytes, 15, 15));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        decoder.Dispose();
        Assert.True(decoder.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(0, IntPtr.Zero, 0));
    }

    [Fact]
    public void A_still_picture_is_one_frame_and_junk_is_refused()
    {
        var png = Path.Combine(_folder, "still.png");
        using (var image = new MagickImage(MagickColors.Orange, 8, 4))
            image.Write(png);
        using (var stream = File.OpenRead(png))
        using (var decoder = AnimatedImageDecoder.Open(stream))
        {
            Assert.NotNull(decoder);
            Assert.False(decoder.IsAnimated);
            Assert.Equal(1, decoder.FrameCount);
            Assert.Equal(0, decoder.RepetitionCount);
        }
        using var junk = new MemoryStream("not a picture"u8.ToArray());
        Assert.Null(AnimatedImageDecoder.Open(junk));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(10, 100)]
    [InlineData(15, 20)]
    [InlineData(70, 70)]
    public void Frame_durations_follow_what_browsers_do(int claimed, int shown) =>
        Assert.Equal(shown, AnimatedImageDecoder.NormalizeDuration(claimed));

    [Fact]
    public void The_timeline_steps_frames_by_their_durations_and_loops()
    {
        var timeline = new FrameTimeline([100, 200, 300], repetitionCount: -1);
        Assert.False(timeline.Advance(TimeSpan.FromMilliseconds(99)));
        Assert.Equal(0, timeline.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(1), timeline.UntilNextFrame);
        Assert.True(timeline.Advance(TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, timeline.Frame);
        Assert.True(timeline.Advance(TimeSpan.FromMilliseconds(450))); // past frame 1 (200) into frame 2
        Assert.Equal(2, timeline.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(50), timeline.UntilNextFrame);
        Assert.True(timeline.Advance(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(0, timeline.Frame);
        Assert.False(timeline.IsFinished);

        // A long pause (sleep) catches up one loop at most, not hours of frames.
        timeline.Advance(TimeSpan.FromHours(5));
        Assert.InRange(timeline.Frame, 0, 2);
    }

    [Fact]
    public void An_animation_that_plays_twice_stops_on_its_last_frame()
    {
        var timeline = new FrameTimeline([100, 100], repetitionCount: 1);
        timeline.Advance(TimeSpan.FromMilliseconds(150));
        timeline.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, timeline.Frame);
        Assert.False(timeline.IsFinished);
        timeline.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(timeline.IsFinished);
        Assert.Equal(1, timeline.Frame);
        Assert.Equal(Timeout.InfiniteTimeSpan, timeline.UntilNextFrame);
        Assert.False(timeline.Advance(TimeSpan.FromSeconds(1)));

        timeline.Reset();
        Assert.Equal((0, false), (timeline.Frame, timeline.IsFinished));
        Assert.Equal(Timeout.InfiniteTimeSpan, new FrameTimeline([100], -1).UntilNextFrame);
    }

    [AvaloniaFact]
    public void The_control_plays_pauses_while_hidden_and_lets_go_when_closed()
    {
        TestServices.EnsureInitialized("en");
        var now = TimeSpan.Zero;
        var image = new AnimatedImage { Clock = () => now, Source = new Uri(Animation(MagickFormat.WebP)) };
        var window = new Window { Content = image, Width = 120, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(image.HasPicture);
        Assert.Equal(new Avalonia.PixelSize(20, 20), image.PixelSize);
        Assert.Equal(0, image.CurrentFrame);
        Assert.True(image.IsPlaying);

        now += TimeSpan.FromMilliseconds(120);
        image.Tick();
        Assert.Equal(1, image.CurrentFrame);

        // Hidden: time passes, the animation stays where it is and goes on from there.
        image.IsVisible = false;
        Assert.False(image.IsPlaying);
        now += TimeSpan.FromSeconds(10);
        image.Tick();
        Assert.Equal(1, image.CurrentFrame);
        image.IsVisible = true;
        now += TimeSpan.FromMilliseconds(190);
        image.Tick();
        Assert.Equal(2, image.CurrentFrame);

        // Reduced motion: the poster frame, which stays.
        image.AutoPlay = false;
        Assert.Equal(0, image.CurrentFrame);
        Assert.False(image.IsPlaying);
        image.PosterFrame = 1;
        Assert.Equal(1, image.CurrentFrame);
        now += TimeSpan.FromSeconds(1);
        image.Tick();
        Assert.Equal(1, image.CurrentFrame);
        image.PosterFrame = 99; // past the end: the last frame
        Assert.Equal(2, image.CurrentFrame);
        image.PosterFrame = 0;

        // A new source replaces the old frames; closing the window frees them.
        image.AutoPlay = true;
        image.Source = new Uri(Animation(MagickFormat.Gif));
        Assert.True(image.HasPicture);
        Assert.Equal(3, image.FrameCount);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(image.HasPicture);
        Assert.Equal(-1, image.CurrentFrame);
        Assert.False(image.IsPlaying);
    }

    [AvaloniaFact]
    public void A_missing_picture_leaves_the_control_empty()
    {
        TestServices.EnsureInitialized("en");
        var image = new AnimatedImage { Source = new Uri(Path.Combine(_folder, "nothing.webp")) };
        var window = new Window { Content = image, Width = 50, Height = 50 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(image.HasPicture);
        image.Source = new Uri("avares://Filee/Assets/WhatsNew/0.0.0/none.webp");
        Assert.False(image.HasPicture);
        window.Close();
    }
}
