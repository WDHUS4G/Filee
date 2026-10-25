// Tajima DST embroidery designs (issue #397): the stitch records decoded, drawn as SVG and rendered to PNG through the
// vector engine. The test designs are written here record by record, as embroidery software writes them.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using ImageMagick;

namespace Filee.Engines.Tests;

public class DstEmbroideryTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A DST file: the 512-byte header, then one record per (dx, dy, kind), then the end record.</summary>
    private static byte[] Dst(params (int Dx, int Dy, char Kind)[] records)
    {
        var bytes = new List<byte>();
        var header = "LA:TEST            \rST:0000010\rCO:  1\r";
        bytes.AddRange(header.Select(c => (byte)c));
        bytes.AddRange(Enumerable.Repeat((byte)' ', 512 - header.Length));
        foreach (var (dx, dy, kind) in records)
        {
            var x = Ternary(dx);
            var y = Ternary(dy);
            int b0 = Bits(x[0], 0x01, 0x02) | Bits(x[2], 0x04, 0x08) | Bits(y[0], 0x80, 0x40) | Bits(y[2], 0x20, 0x10);
            int b1 = Bits(x[1], 0x01, 0x02) | Bits(x[3], 0x04, 0x08) | Bits(y[1], 0x80, 0x40) | Bits(y[3], 0x20, 0x10);
            int b2 = 0x03 | Bits(x[4], 0x04, 0x08) | Bits(y[4], 0x20, 0x10) | kind switch { 'j' => 0x80, 'c' => 0xC0, 'q' => 0x40, _ => 0 };
            bytes.AddRange([(byte)b0, (byte)b1, (byte)b2]);
        }
        bytes.AddRange([0x00, 0x00, 0xF3]);
        return [.. bytes];

        static int Bits(int digit, int plus, int minus) => digit > 0 ? plus : digit < 0 ? minus : 0;
    }

    /// <summary>Balanced ternary digits of <paramref name="value"/> for 1, 3, 9, 27 and 81.</summary>
    private static int[] Ternary(int value)
    {
        var digits = new int[5];
        for (var k = 0; k < 5; k++)
        {
            var r = ((value % 3) + 3) % 3;
            digits[k] = r == 2 ? -1 : r;
            value = (value - digits[k]) / 3;
        }
        return digits;
    }

    /// <summary>Two 10 mm squares in two threads, joined by a jump, as a design has them.</summary>
    private static byte[] TwoSquares() => Dst(
        (100, 0, 's'), (0, 100, 's'), (-100, 0, 's'), (0, -100, 's'),
        (121, 0, 'j'), (29, 0, 'c'),
        (100, 0, 's'), (0, 100, 's'), (-100, 0, 's'), (0, -100, 's'));

    [Fact]
    public void Stitches_jumps_and_colour_changes_are_decoded()
    {
        var threads = DstEmbroidery.Read(TwoSquares());

        Assert.Equal(2, threads.Count);
        Assert.Equal([(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)], Assert.Single(threads[0]));
        Assert.Equal([(150, 0), (250, 0), (250, 100), (150, 100), (150, 0)], Assert.Single(threads[1]));
    }

    [Fact]
    public void A_sequin_mode_switch_moves_without_a_thread()
    {
        var runs = Assert.Single(DstEmbroidery.Read(Dst((10, 0, 's'), (50, 0, 'q'), (10, 0, 's'))));

        Assert.Equal([[(0, 0), (10, 0)], [(60, 0), (70, 0)]], runs);
    }

    [Fact]
    public void Every_step_size_is_read_both_ways()
    {
        foreach (var d in new[] { 1, -1, 3, -3, 9, -9, 27, -27, 81, -81, 121, -121, 40, -77 })
        {
            var run = Assert.Single(Assert.Single(DstEmbroidery.Read(Dst((d, -d, 's')))));
            Assert.Equal((d, -d), run[^1]);
        }
    }

    [Fact]
    public async Task A_design_becomes_an_svg_and_a_png_of_its_threads()
    {
        var path = Path.Combine(fx.NewFolder(), "badge.dst");
        await File.WriteAllBytesAsync(path, TwoSquares(), TestContext.Current.CancellationToken);
        Assert.Equal("dst", FormatRegistry.Detect(path)?.Id);

        var svgJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "svg" });
        Assert.True(svgJob.State == JobState.Completed, string.Join("; ", svgJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var svg = await File.ReadAllTextAsync(svgJob.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("width=\"29mm\"", svg); // 25 mm of design and 2 mm on each side
        Assert.Contains("stroke=\"#1D3557\"", svg);
        Assert.Contains("stroke=\"#E63946\"", svg);

        var pngJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.True(pngJob.State == JobState.Completed, string.Join("; ", pngJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var png = new MagickImage(pngJob.Outputs.Single());
        Assert.True(png.Width > png.Height); // two squares side by side
        Assert.True(png.Histogram().Count > 2, "the threads are drawn in colour");
    }

    [Fact]
    public async Task An_autocad_sheet_set_with_the_same_extension_fails_with_a_reason()
    {
        var path = Path.Combine(fx.NewFolder(), "project.dst");
        await File.WriteAllTextAsync(path, "<?xml version=\"1.0\"?><AcSmDatabase/>", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("LA:", job.Files[0].ErrorDetail);
    }
}
