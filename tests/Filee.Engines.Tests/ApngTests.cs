// APNG: Filee's own reader and writer (Magick/Apng.cs). Animations are generated in the tests: GIFs with ImageMagick,
// and APNGs chunk by chunk (ApngBuilder) to cover the dispose and blend operations of the specification.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class ApngTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    /// <summary>A 3-frame animated GIF (red, green, blue; 20, 30 and 40 hundredths of a second, looping).</summary>
    private static string MakeAnimatedGif(string folder)
    {
        using var frames = new MagickImageCollection();
        foreach (var (color, delay) in new[] { (MagickColors.Red, 20u), (MagickColors.Lime, 30u), (MagickColors.Blue, 40u) })
            frames.Add(new MagickImage(color, 64, 48) { AnimationDelay = delay });
        var path = Path.Combine(folder, "anim.gif");
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    private static IMagickColor<byte> Pixel(IMagickImage<byte> image, int x, int y) => image.GetPixels().GetPixel(x, y).ToColor()!;

    [Fact]
    public async Task Animated_gif_becomes_an_apng_and_back()
    {
        var dir = fx.NewFolder();
        var toApng = await fx.ConvertAsync([MakeAnimatedGif(dir)], new Preset { TargetFormat = "apng" });

        AssertDone(toApng);
        var apng = toApng.Outputs.Single();
        Assert.Equal(".apng", Path.GetExtension(apng));
        var chunks = ApngBuilder.Chunks(File.ReadAllBytes(apng));
        Assert.Equal(["IHDR", "acTL"], chunks.Select(c => c.Type).Where(t => t is "IHDR" or "acTL"));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(chunks.Single(c => c.Type == "acTL").Data));
        Assert.Equal(3, chunks.Count(c => c.Type == "fcTL"));
        Assert.True(chunks.FindIndex(c => c.Type == "fcTL") < chunks.FindIndex(c => c.Type == "IDAT"), "the default image is the first frame");
        // A viewer without APNG support shows the first frame.
        using (var still = new MagickImage(apng, new MagickReadSettings { Format = MagickFormat.Png }))
            Assert.True(Pixel(still, 32, 24).R > 200, "first frame is red");

        var back = await fx.ConvertAsync([apng], new Preset { TargetFormat = "gif", Output = { FileNamePattern = "{name}_back" } });

        AssertDone(back);
        using var gif = new MagickImageCollection(back.Outputs.Single());
        Assert.Equal(3, gif.Count);
        Assert.Equal([20u, 30u, 40u], gif.Select(f => f.AnimationDelay));
        Assert.True(Pixel(gif[1], 32, 24).G > 200, "second frame is green");
        Assert.True(Pixel(gif[2], 32, 24).B > 200, "third frame is blue");
    }

    [Fact]
    public async Task Apng_becomes_an_animated_webp_and_a_still_picture()
    {
        var dir = fx.NewFolder();
        var apng = (await fx.ConvertAsync([MakeAnimatedGif(dir)], new Preset { TargetFormat = "apng" })).Outputs.Single();

        var webp = await fx.ConvertAsync([apng], new Preset { TargetFormat = "webp", Image = { WebpLossless = true } });
        var jpg = await fx.ConvertAsync([apng], new Preset { TargetFormat = "jpg" });

        AssertDone(webp);
        using (var frames = new MagickImageCollection(webp.Outputs.Single()))
        {
            Assert.Equal(3, frames.Count);
            Assert.Equal([20u, 30u, 40u], frames.Select(f => f.AnimationDelay));
        }
        AssertDone(jpg);
        using var first = new MagickImage(jpg.Outputs.Single());
        Assert.True(Pixel(first, 32, 24).R > 200, "a still format takes the first frame");
    }

    [Fact]
    public async Task An_animated_png_saved_as_png_stays_animated()
    {
        var dir = fx.NewFolder();
        var apng = (await fx.ConvertAsync([MakeAnimatedGif(dir)], new Preset { TargetFormat = "apng" })).Outputs.Single();
        var png = Path.Combine(dir, "sticker.png");
        File.Copy(apng, png);

        var job = await fx.ConvertAsync([png], new Preset { TargetFormat = "gif" });

        AssertDone(job);
        using var gif = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(3, gif.Count);
    }

    [Fact]
    public void Frames_are_composed_with_their_dispose_and_blend_operations()
    {
        var dir = fx.NewFolder();
        using var red = new MagickImage(MagickColors.Red, 40, 40);
        using var clear = new MagickImage(MagickColors.Transparent, 10, 10);
        using var blue = new MagickImage(new MagickColor(0, 0, 255, 128), 10, 10);
        using var green = new MagickImage(MagickColors.Lime, 1, 1);
        using var yellow = new MagickImage(MagickColors.Yellow, 40, 40);
        var path = Path.Combine(dir, "ops.apng");
        File.WriteAllBytes(path, ApngBuilder.Build(40, 40, plays: 2, hiddenDefault: yellow,
        [
            new(red, 0, 0, 5, 0, ApngBuilder.DisposeNone, ApngBuilder.BlendSource),           // 5/100 s (denominator 0)
            new(clear, 0, 0, 1, 10, ApngBuilder.DisposeBackground, ApngBuilder.BlendSource),   // punches a hole
            new(blue, 30, 30, 1, 10, ApngBuilder.DisposePrevious, ApngBuilder.BlendOver),      // shown once, then undone
            new(green, 20, 20, 1, 1, ApngBuilder.DisposeNone, ApngBuilder.BlendOver),
        ]));

        using var frames = Apng.Read(path);

        Assert.Equal(4, frames.Count); // the hidden default image (yellow) is not a frame
        Assert.Equal([5u, 10u, 10u, 100u], frames.Select(f => f.AnimationDelay));
        Assert.All(frames, f => Assert.Equal(2u, f.AnimationIterations));
        Assert.Equal(255, Pixel(frames[0], 5, 5).R);
        Assert.Equal(0, Pixel(frames[1], 5, 5).A); // SOURCE replaces, transparency included
        Assert.Equal(255, Pixel(frames[1], 35, 35).R);
        var mixed = Pixel(frames[2], 35, 35); // OVER: half blue over red
        Assert.InRange(mixed.R, 100, 160);
        Assert.InRange(mixed.B, 100, 160);
        Assert.Equal(0, Pixel(frames[2], 5, 5).A); // BACKGROUND cleared the hole's region
        Assert.Equal(255, Pixel(frames[3], 35, 35).R); // PREVIOUS restored the red
        Assert.Equal(0, Pixel(frames[3], 35, 35).B);
        Assert.Equal(255, Pixel(frames[3], 20, 20).G);
        Assert.Equal(0, Pixel(frames[3], 5, 5).A);
    }

    [Fact]
    public void Unchanged_frames_are_merged_and_changed_ones_store_only_what_changed()
    {
        var dir = fx.NewFolder();
        using var first = new MagickImage(MagickColors.Red, 64, 48) { AnimationDelay = 10 };
        using var same = new MagickImage(MagickColors.Red, 64, 48) { AnimationDelay = 15 };
        using var dot = new MagickImage(MagickColors.Red, 64, 48) { AnimationDelay = 20 };
        using (var pixels = dot.GetPixels())
            pixels.SetArea(10, 20, 4, 3, Enumerable.Repeat(new byte[] { 0, 0, 255 }, 12).SelectMany(p => p).ToArray());
        var path = Path.Combine(dir, "merged.apng");

        Apng.Write([first, same, dot], path);

        var controls = ApngBuilder.Chunks(File.ReadAllBytes(path)).Where(c => c.Type == "fcTL").Select(c => c.Data).ToList();
        Assert.Equal(2, controls.Count);
        Assert.Equal(25, BinaryPrimitives.ReadUInt16BigEndian(controls[0].AsSpan(20))); // 10 + 15
        Assert.Equal((4u, 3u, 10u, 20u), (BinaryPrimitives.ReadUInt32BigEndian(controls[1].AsSpan(4)),
            BinaryPrimitives.ReadUInt32BigEndian(controls[1].AsSpan(8)), BinaryPrimitives.ReadUInt32BigEndian(controls[1].AsSpan(12)),
            BinaryPrimitives.ReadUInt32BigEndian(controls[1].AsSpan(16))));

        using var frames = Apng.Read(path);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0d, frames[1].Compare(dot, ErrorMetric.Absolute));
    }

    [Fact]
    public void Frames_of_different_sizes_share_one_canvas()
    {
        var dir = fx.NewFolder();
        using var big = new MagickImage(MagickColors.Red, 80, 60);
        using var small = new MagickImage(MagickColors.Blue, 40, 30);
        var path = Path.Combine(dir, "sizes.apng");

        Apng.Write([big, small], path);

        using var frames = Apng.Read(path);
        Assert.All(frames, f => Assert.Equal((80u, 60u), (f.Width, f.Height)));
        Assert.True(Pixel(frames[1], 10, 10).B > 200);
        Assert.Equal(0, Pixel(frames[1], 70, 50).A);
    }

    [Fact]
    public void Damaged_files_fail_with_a_clear_message()
    {
        var dir = fx.NewFolder();
        using var red = new MagickImage(MagickColors.Red, 10, 10);
        var path = Path.Combine(dir, "bad.apng");
        File.WriteAllBytes(path, ApngBuilder.Build(10, 10, plays: 0, hiddenDefault: null,
            [new(red, 5, 5, 1, 10, ApngBuilder.DisposeNone, ApngBuilder.BlendSource)])); // outside the 10 × 10 canvas

        var error = Assert.Throws<InvalidDataException>(() => Apng.Read(path));
        Assert.Contains("outside", error.Message);
        Assert.False(Apng.IsAnimated(Path.Combine(dir, "missing.png")));
    }
}

/// <summary>Writes APNG files chunk by chunk, independent of Filee's writer.</summary>
internal static class ApngBuilder
{
    public const byte DisposeNone = 0, DisposeBackground = 1, DisposePrevious = 2;
    public const byte BlendSource = 0, BlendOver = 1;

    public sealed record Frame(IMagickImage<byte> Image, uint X, uint Y, ushort DelayNumerator, ushort DelayDenominator, byte Dispose, byte Blend);

    public sealed record Chunk(string Type, byte[] Data);

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="hiddenDefault">A default image (IDAT) before the first frame, not part of the animation.</param>
    public static byte[] Build(uint width, uint height, uint plays, IMagickImage<byte>? hiddenDefault, IReadOnlyList<Frame> frames)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Write(output, "IHDR", header);
        var control = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(control, (uint)frames.Count);
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), plays);
        Write(output, "acTL", control);
        if (hiddenDefault is not null)
            Write(output, "IDAT", ImageData(hiddenDefault));

        var sequence = 0u;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), frame.Image.Width);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), frame.Image.Height);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), frame.X);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), frame.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), frame.DelayNumerator);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), frame.DelayDenominator);
            fctl[24] = frame.Dispose;
            fctl[25] = frame.Blend;
            Write(output, "fcTL", fctl);
            var data = ImageData(frame.Image);
            if (i == 0 && hiddenDefault is null)
            {
                Write(output, "IDAT", data);
                continue;
            }
            var fdat = new byte[data.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
            data.CopyTo(fdat.AsSpan(4));
            Write(output, "fdAT", fdat);
        }
        Write(output, "IEND", []);
        return output.ToArray();
    }

    public static List<Chunk> Chunks(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(Signature));
        var chunks = new List<Chunk>();
        for (var position = 8; position + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length).ToArray();
            var crc = new Crc32();
            crc.Append(png.AsSpan(position + 4, 4 + length));
            Assert.Equal(crc.Value, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length)));
            chunks.Add(new Chunk(type, data));
            position += 12 + length;
        }
        return chunks;
    }

    /// <summary>The zlib stream of an 8-bit RGBA PNG of <paramref name="image"/>.</summary>
    private static byte[] ImageData(IMagickImage<byte> image) =>
        Chunks(image.ToByteArray(MagickFormat.Png32)).Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();

    private static void Write(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc.Value);
        stream.Write(buffer);
    }
}
