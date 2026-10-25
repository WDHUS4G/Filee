// JPEG XR (.jxr) through the Windows Imaging Component: 8-bit pictures and HDR screenshots (half-float scRGB, tone-mapped
// to SDR). The pictures are written in the tests with Windows' own JPEG XR encoder.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Wic;
using ImageMagick;

namespace Filee.Engines.Tests;

public class JpegXrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>SDR white of the test screenshots: 200 nits (scRGB 1.0 = 80 nits).</summary>
    private const float White = 2.5f;

    /// <summary>Linear value of sRGB 128 (middle grey on screen).</summary>
    private const float Grey = 0.2158f;

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    private static IMagickColor<byte> Pixel(IMagickImage<byte> image, int x, int y) =>
        image.GetPixels().GetPixel(x, y).ToColor()!;

    [Fact]
    public void Jpeg_xr_is_read_with_the_windows_codec_and_on_windows_only()
    {
        // No ImageMagick build has a JPEG XR coder (it would need jxrlib); with one, other platforms could read it too.
        Assert.DoesNotContain(Enum.GetNames<MagickFormat>(), name => name is "Jxr" or "Wdp" or "Hdp");

        var status = fx.Catalog.StatusOf(fx.Converters.Single(c => c is WicImageConverter));
        Assert.Equal(OperatingSystem.IsWindows(), status.IsAvailable);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("engine.reason.unsupported_platform", status.ReasonKey);
        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal(OperatingSystem.IsWindows(), planner.CanConvert("jxr", "png"));
        if (!OperatingSystem.IsWindows())
            return;
        // Targets other engines write are reached through a lossless PNG.
        foreach (var target in new[] { "pdf", "icns", "svg" })
            Assert.Equal(("wic", "png"), planner.Plan("jxr", target)!.Steps.Select(s => (s.Converter.Id, s.To)).First());
        Assert.Equal(("wic", "png"), fx.Catalog.CreatePlanner(["ffmpeg"]).Plan("jxr", "heic")!.Steps.Select(s => (s.Converter.Id, s.To)).First());
    }

    [Fact]
    public async Task An_8_bit_jpeg_xr_converts_to_png_and_jpg()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "JPEG XR is read with the Windows Imaging Component.");
        var dir = fx.NewFolder();
        var jxr = Path.Combine(dir, "photo.jxr");
        JpegXrBuilder.WriteBgr(jxr, 64, 48, (x, _) => x < 32 ? ((byte)230, (byte)40, (byte)30) : ((byte)20, (byte)60, (byte)200));

        foreach (var target in new[] { "png", "jpg" })
        {
            var job = await fx.ConvertAsync([jxr], new Preset { TargetFormat = target });

            AssertDone(job);
            using var image = new MagickImage(job.Outputs.Single());
            Assert.Equal((64u, 48u), (image.Width, image.Height));
            Assert.False(image.HasAlpha);
            var left = Pixel(image, 8, 24);
            var right = Pixel(image, 56, 24);
            Assert.True(Math.Abs(left.R - 230) < 12 && Math.Abs(left.G - 40) < 12 && Math.Abs(left.B - 30) < 12, $"{target} left: {left}");
            Assert.True(Math.Abs(right.R - 20) < 12 && Math.Abs(right.G - 60) < 12 && Math.Abs(right.B - 200) < 12, $"{target} right: {right}");
        }
    }

    [Fact]
    public async Task Several_jpeg_xr_pictures_are_read_at_once()
    {
        // WIC gives every reader of a pixel format the same component info, and .NET one wrapper for it: a reader
        // that made a final release of it cut the others off ("COM object that has been separated from its RCW").
        Assert.SkipUnless(OperatingSystem.IsWindows(), "JPEG XR is read with the Windows Imaging Component.");
        var dir = fx.NewFolder();
        var sdr = Path.Combine(dir, "photo.jxr");
        JpegXrBuilder.WriteBgr(sdr, 64, 48, (x, y) => ((byte)x, (byte)y, 128));
        var hdr = Path.Combine(dir, "screenshot.jxr");
        JpegXrBuilder.WriteHdr(hdr, 64, 48, (x, _) => (x / 32f, 0.5f, 0.25f));

        var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                return;
            for (var i = 0; i < 40; i++)
            {
                using var image = WicImageReader.Read((reader + i) % 2 == 0 ? sdr : hdr);
                Assert.Equal((64u, 48u), (image.Width, image.Height));
            }
        }, TestContext.Current.CancellationToken));

        await Task.WhenAll(readers);
    }

    [Fact]
    public async Task An_hdr_screenshot_of_sdr_content_comes_out_as_it_was_drawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "JPEG XR is read with the Windows Imaging Component.");
        var dir = fx.NewFolder();
        var jxr = Path.Combine(dir, "desktop.jxr");
        // A desktop at 200 nits SDR brightness: white windows and a grey panel, nothing brighter than SDR white.
        JpegXrBuilder.WriteHdr(jxr, 100, 100, (x, y) => x < 50 && y < 50 ? (Grey * White, Grey * White, Grey * White) : (White, White, White));

        var job = await fx.ConvertAsync([jxr], new Preset { TargetFormat = "png", Image = { Resize = ResizeMode.Percent, ResizePercent = 50 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal((50u, 50u), (image.Width, image.Height)); // the preset applies as to any picture
        Assert.False(image.HasAlpha); // the half-float alpha channel is opaque everywhere
        // Clipping at scRGB 1.0 would make the grey 194 and the white 255; divided by the white level it is 128 again.
        Assert.InRange(Pixel(image, 10, 10).R, 124, 132);
        Assert.InRange(Pixel(image, 40, 40).R, 253, 255);
    }

    [Fact]
    public async Task Hdr_highlights_are_rolled_off_instead_of_clipping_or_darkening_the_picture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "JPEG XR is read with the Windows Imaging Component.");
        var dir = fx.NewFolder();
        var jxr = Path.Combine(dir, "game.jxr");
        // The same desktop with a small 1000-nit highlight (scRGB 12.5) and a 400-nit one next to it.
        JpegXrBuilder.WriteHdr(jxr, 100, 100, (x, y) => (x, y) switch
        {
            ( < 50, < 50) => (Grey * White, Grey * White, Grey * White),
            ( >= 80 and < 85, >= 80 and < 85) => (12.5f, 12.5f, 12.5f),
            ( >= 90 and < 95, >= 80 and < 85) => (5f, 5f, 5f),
            _ => (White, White, White),
        });

        var job = await fx.ConvertAsync([jxr], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        var grey = Pixel(image, 20, 20).R;
        var white = Pixel(image, 70, 20).R;
        var bright = Pixel(image, 92, 82).R;
        var brightest = Pixel(image, 82, 82).R;
        Assert.InRange(grey, 122, 134); // below the shoulder: unchanged, not darkened by the 1000-nit peak
        Assert.InRange(white, 232, 250); // white gives up a little room to the highlights
        Assert.True(white < bright && bright <= brightest, $"white {white}, 400 nits {bright}, 1000 nits {brightest}");
        Assert.InRange(brightest, 250, 255);
    }

    [Fact]
    public void The_tone_curve_keeps_sdr_values_and_rolls_highlights_off_to_white()
    {
        // Nothing above white: the identity, so SDR content comes out unchanged.
        var sdr = new HdrToneMap(White, White);
        Assert.Equal(0.5f, sdr.Curve(0.5f));
        Assert.Equal(1f, sdr.Curve(1f), 4);

        // With a peak of 10× white: the identity up to the knee, then rising smoothly to exactly 1 at the peak.
        var hdr = new HdrToneMap(White, 10 * White);
        Assert.Equal(HdrToneMap.Knee, hdr.Curve(HdrToneMap.Knee));
        var previous = HdrToneMap.Knee;
        for (var value = HdrToneMap.Knee + 0.1f; value <= 10; value += 0.1f)
        {
            var mapped = hdr.Curve(value);
            Assert.True(mapped > previous && mapped <= 1, $"{value} → {mapped}");
            previous = mapped;
        }
        Assert.Equal(1f, hdr.Curve(10), 4);
        Assert.Equal(1f, hdr.Curve(50));

        // Hue is kept: a bright orange stays orange (R > G > B) instead of clipping towards yellow.
        Span<byte> rgba = stackalloc byte[4];
        hdr.Map(20, 10, 2, 1, rgba);
        Assert.True(rgba[0] > rgba[1] && rgba[1] > rgba[2], $"{rgba[0]} {rgba[1]} {rgba[2]}");
        hdr.Map(-0.5f, float.NaN, float.PositiveInfinity, float.NaN, rgba);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba.ToArray());
    }

    [Fact]
    public void The_reference_white_is_the_99th_percentile_between_1_and_4()
    {
        float[] Picture(float common, float rare, int rarePerMille) =>
            [.. Enumerable.Range(0, 1000).Select(i => i < rarePerMille ? rare : common)];

        Assert.Equal(White, HdrToneMap.ReferenceWhite(Picture(White, 12.5f, 5))); // a small highlight is not white
        Assert.Equal(4f, HdrToneMap.ReferenceWhite(Picture(White, 12.5f, 300))); // a large bright area is, up to 320 nits
        Assert.Equal(1f, HdrToneMap.ReferenceWhite(Picture(0.4f, 0.2f, 500))); // a dim picture keeps scRGB's own white
        Assert.Equal(1f, HdrToneMap.ReferenceWhite([]));
    }
}
