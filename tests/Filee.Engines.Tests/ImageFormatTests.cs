// The less common raster formats ImageMagick (Magick.NET Q8) reads and writes for Filee: every one is checked against
// the coders the bundled build really has, then converted there and back. Inputs are generated in the tests.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class ImageFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Formats Filee reads and writes with ImageMagick's own coders (APNG is Filee's, see ApngTests).</summary>
    private static readonly string[] ReadWrite = ["mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff"];

    private static readonly string[] ReadOnly = ["dcm", "pcd", "exr", "hdr"];

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    [Fact]
    public void The_bundled_imagemagick_has_a_coder_for_every_format()
    {
        var formats = MagickNET.SupportedFormats.ToDictionary(f => f.Format);
        foreach (var id in ReadWrite.Concat(ReadOnly))
        {
            var info = formats[ImageEncoder.ToMagickFormat(id)];
            Assert.True(info.SupportsReading, $"{id} read");
            Assert.Equal(ReadWrite.Contains(id), FormatRegistry.Get(id).Writable);
            if (ReadWrite.Contains(id))
                Assert.True(info.SupportsWriting, $"{id} write");
        }
        Assert.Equal(ReadWrite.Order(), ImageEncoder.Writable.Intersect(ReadWrite.Concat(ReadOnly)).Order());
        Assert.Equal(ReadWrite.Concat(ReadOnly).Order(), ImageEncoder.Readable.Intersect(ReadWrite.Concat(ReadOnly)).Order());
    }

    /// <summary>
    /// PNG → format → PNG. <paramref name="maxError"/> is the root mean square error (0–1) of the round trip against the
    /// source, both flattened onto white like formats without transparency do: lossless formats give 0, DDS (DXT
    /// blocks) a little, the 1-bit XBM and WBMP a lot.
    /// </summary>
    [Theory]
    [InlineData("mng", 0.001)]
    [InlineData("qoi", 0.001)]
    [InlineData("dds", 0.05)]
    [InlineData("pcx", 0.001)]
    [InlineData("cur", 0.02)] // scaled to 256 px
    [InlineData("xpm", 0.02)]
    [InlineData("xbm", 0.7)]
    [InlineData("wbmp", 0.7)]
    [InlineData("pam", 0.001)]
    [InlineData("pfm", 0.01)]
    [InlineData("sgi", 0.001)]
    [InlineData("ras", 0.001)]
    [InlineData("miff", 0.001)]
    public async Task Png_converts_to_the_format_and_back(string format, double maxError)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var to = await fx.ConvertAsync([source], new Preset { TargetFormat = format });

        AssertDone(to);
        var converted = to.Outputs.Single();
        Assert.Equal("." + FormatRegistry.Get(format).PrimaryExtension, Path.GetExtension(converted));
        var expectedWidth = format == "cur" ? 256u : 320u;
        using (var image = new MagickImage(converted, new MagickReadSettings { Format = ImageEncoder.ToMagickFormat(format) }))
            Assert.Equal(expectedWidth, image.Width);

        var back = await fx.ConvertAsync([converted], new Preset { TargetFormat = "png", Output = { FileNamePattern = "{name}_back" } });

        AssertDone(back);
        using var result = new MagickImage(back.Outputs.Single());
        using var original = new MagickImage(source);
        Assert.Equal((expectedWidth, original.Height * expectedWidth / original.Width), (result.Width, result.Height));
        if (format == "cur")
            original.Resize(result.Width, result.Height);
        Assert.InRange(OnWhite(original).Compare(OnWhite(result), ErrorMetric.RootMeanSquared), 0, maxError);
    }

    private static MagickImage OnWhite(MagickImage image)
    {
        var flat = (MagickImage)image.Clone();
        flat.BackgroundColor = MagickColors.White;
        flat.Alpha(AlphaOption.Remove);
        flat.ColorSpace = ColorSpace.sRGB;
        return flat;
    }

    [Fact]
    public async Task An_animated_gif_becomes_an_animated_mng_and_back()
    {
        var dir = fx.NewFolder();
        var gif = Path.Combine(dir, "anim.gif");
        using (var frames = new MagickImageCollection())
        {
            foreach (var (color, delay) in new[] { (MagickColors.Red, 20u), (MagickColors.Lime, 30u), (MagickColors.Blue, 40u) })
                frames.Add(new MagickImage(color, 64, 48) { AnimationDelay = delay });
            frames.Write(gif, MagickFormat.Gif);
        }

        var mng = await fx.ConvertAsync([gif], new Preset { TargetFormat = "mng" });
        AssertDone(mng);
        var back = await fx.ConvertAsync([mng.Outputs.Single()], new Preset { TargetFormat = "gif", Output = { FileNamePattern = "{name}_back" } });

        AssertDone(back);
        using var again = new MagickImageCollection(back.Outputs.Single());
        Assert.Equal(3, again.Count);
        Assert.Equal([20u, 30u, 40u], again.Select(f => f.AnimationDelay));
    }

    [Theory]
    [InlineData("exr", MagickFormat.Exr, 320u)]
    [InlineData("hdr", MagickFormat.Hdr, 320u)]
    [InlineData("pcd", MagickFormat.Pcd, 768u)] // Photo CD's base resolution
    public async Task Read_only_formats_convert_to_png(string format, MagickFormat magickFormat, uint expectedWidth)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "picture." + format, magickFormat);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var png = new MagickImage(job.Outputs.Single());
        Assert.Equal(expectedWidth, png.Width);
    }

    [Fact]
    public async Task Sgi_files_named_rgb_are_read_as_sgi_not_as_raw_pixels()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "texture.rgb", MagickFormat.Sgi);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var png = new MagickImage(job.Outputs.Single());
        Assert.Equal((320u, 200u), (png.Width, png.Height));
    }

    [Fact]
    public async Task Large_pictures_become_cursors_of_at_most_256_pixels()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "big.png", MagickFormat.Png, 600, 300);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "cur" });

        AssertDone(job);
        using var cursor = new MagickImage(job.Outputs.Single(), new MagickReadSettings { Format = MagickFormat.Cur });
        Assert.Equal((256u, 128u), (cursor.Width, cursor.Height));
    }
}
