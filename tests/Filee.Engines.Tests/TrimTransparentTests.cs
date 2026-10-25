// "Trim transparent borders" (issue #332): a copy cropped to the visible pixels, run through the real image engine.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class TrimTransparentTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Preset Trim => BuiltInData.CreatePresets().Single(p => p.Id == "trim-transparent");

    private async Task<string> RunAsync(string input)
    {
        var job = await fx.ConvertAsync([input], Trim);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    /// <summary>A transparent <paramref name="width"/> × <paramref name="height"/> picture with an opaque box.</summary>
    private static string MakePicture(string folder, string name, MagickFormat format, uint width, uint height, int x, int y, uint w, uint h)
    {
        using var image = new MagickImage(MagickColors.Transparent, width, height);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Coral).Rectangle(x, y, x + w - 1, y + h - 1));
        var path = Path.Combine(folder, name);
        image.Write(path, format);
        return path;
    }

    [Theory]
    [InlineData("logo.png", MagickFormat.Png)]
    [InlineData("logo.webp", MagickFormat.WebP)]
    public async Task The_transparent_padding_goes_and_the_format_stays(string name, MagickFormat format)
    {
        var folder = fx.NewFolder();
        var source = MakePicture(folder, name, format, 200, 150, 30, 20, 50, 40);

        var output = await RunAsync(source);

        Assert.Equal(Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + "_trimmed" + Path.GetExtension(name)), output);
        using var trimmed = new MagickImage(output);
        Assert.Equal((50u, 40u), (trimmed.Width, trimmed.Height));
        Assert.Equal((0, 0), (trimmed.Page.X, trimmed.Page.Y)); // no leftover offset
        Assert.True(File.Exists(source)); // the source is never overwritten
    }

    [Fact]
    public async Task Content_touching_a_corner_keeps_what_isnt_transparent()
    {
        // ImageMagick's own Trim would treat the coral corner as the border colour and cut the coral away.
        var folder = fx.NewFolder();
        var source = MakePicture(folder, "corner.png", MagickFormat.Png, 120, 80, 0, 0, 60, 30);

        using var trimmed = new MagickImage(await RunAsync(source));

        Assert.Equal((60u, 30u), (trimmed.Width, trimmed.Height));
    }

    [Fact]
    public async Task An_animation_keeps_its_frames_aligned()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "spinner.gif");
        using (var frames = new MagickImageCollection())
        {
            foreach (var x in new[] { 10, 60 })
            {
                var frame = new MagickImage(MagickColors.Transparent, 100, 50) { AnimationDelay = 20 };
                frame.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Coral).Rectangle(x, 10, x + 20, 30));
                frames.Add(frame);
            }
            frames.Write(path, MagickFormat.Gif);
        }

        using var output = new MagickImageCollection(await RunAsync(path));

        Assert.Equal(2, output.Count);
        Assert.All(output, f => Assert.Equal((100u, 50u), (f.Page.Width, f.Page.Height)));
    }

    [Fact]
    public void A_picture_without_transparency_or_fully_transparent_is_left_alone()
    {
        using var opaque = new MagickImage(MagickColors.Teal, 40, 30);
        Filee.Engines.Magick.ImageEncoder.TrimTransparentBorders(opaque);
        Assert.Equal((40u, 30u), (opaque.Width, opaque.Height));

        using var empty = new MagickImage(MagickColors.Transparent, 40, 30);
        Filee.Engines.Magick.ImageEncoder.TrimTransparentBorders(empty);
        Assert.Equal((40u, 30u), (empty.Width, empty.Height));
    }

    [Fact]
    public async Task A_jpg_has_nothing_to_trim_and_is_not_re_encoded()
    {
        var folder = fx.NewFolder();
        var photo = Path.Combine(folder, "photo.jpg");
        using (var image = new MagickImage(MagickColors.Teal, 60, 40))
            image.Write(photo, MagickFormat.Jpeg);

        var job = await fx.ConvertAsync([photo], Trim);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.no_transparency", job.Files[0].ErrorKey);
        Assert.Equal(["photo.jpg"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.False(Filee.Engines.Magick.ImageEncoder.HoldsAlpha("jpg"));
        Assert.True(Filee.Engines.Magick.ImageEncoder.HoldsAlpha("png"));
    }
}
