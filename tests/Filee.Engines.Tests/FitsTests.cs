// FITS: Filee's reader (Magick/Fits.cs), the stretch and debayering for pictures (FitsRendering) and the TIFF of the
// file's own depth (TiffWriter). The files are written in the tests (FitsBuilder), card by card as capture programs do.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class FitsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Save(byte[] fits, string name = "light.fits")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, fits);
        return path;
    }

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    private async Task<string> ConvertAsync(string source, string target, Action<ImageOptions>? options = null)
    {
        var preset = new Preset { TargetFormat = target };
        options?.Invoke(preset.Image);
        var job = await fx.ConvertAsync([source], preset);
        AssertDone(job);
        return job.Outputs.Single();
    }

    /// <summary>A sky of about <paramref name="sky"/> with a little noise, the same for every seed.</summary>
    private static double[] Sky(int width, int height, double sky, double noise, int seed = 1)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, width * height).Select(_ => sky + (random.NextDouble() - 0.5) * 2 * noise)];
    }

    private static IPixel<byte> Pixel(IMagickImage<byte> image, int x, int y) => image.GetPixels().GetPixel(x, y);

    [Fact]
    public async Task Unsigned_16_bit_data_stays_16_bit_and_exact_in_a_linear_tiff()
    {
        // Unsigned 16-bit as cameras write it: stored signed, BZERO 32768.
        var values = Enumerable.Range(0, 64 * 8).Select(i => i * 128.0 + (i % 7)).ToArray();
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 64, Height = 8, Values = values, Bzero = 32768 }.Build());

        var tiff = await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None);

        var probe = TiffProbe.Read(tiff);
        Assert.Equal((64, 8, 16, 1, 1), (probe.Width, probe.Height, probe.Bits, probe.SampleFormat, probe.Samples));
        Assert.Equal(values, probe.Values);
        Assert.Contains(values, v => v > 32767); // the upper half survives as unsigned
    }

    [Theory]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Zip)]
    public async Task Compressed_16_bit_tiffs_read_back_in_other_programs(TiffCompression compression)
    {
        var values = Sky(300, 200, 12000, 3000);
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 300, Height = 200, Values = values, Bzero = 32768 }.Build());

        var tiff = await ConvertAsync(source, "tiff", o => o.TiffCompression = compression);

        // libtiff (in ImageMagick) decodes the strips; the Q8 build keeps the top 8 bits of each sample.
        using var image = new MagickImage(tiff);
        Assert.Equal((300u, 200u, 16u), (image.Width, image.Height, image.Depth));
        for (var i = 0; i < values.Length; i += 997)
            Assert.InRange(Pixel(image, i % 300, i / 300).GetChannel(0), Math.Round(values[i]) / 257 - 1, Math.Round(values[i]) / 257 + 1);
    }

    [Fact]
    public async Task Lzw_strips_full_of_noise_reset_the_table_and_read_back_exactly()
    {
        // Random 8-bit data fills the 4096-entry table again and again: every clear code must be where readers expect it.
        var random = new Random(7);
        var values = Enumerable.Range(0, 640 * 480).Select(i => i < 640 * 240 ? random.Next(256) : (double)(i / 640 % 3 * 80)).ToArray();
        var source = Save(new FitsBuilder { Bitpix = 8, Width = 640, Height = 480, Values = values }.Build());

        var tiff = await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.Lzw);

        using var image = new MagickImage(tiff);
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        for (var i = 0; i < values.Length; i++)
            Assert.Equal((byte)values[i], pixels[i * 3]);
        Assert.True(new FileInfo(tiff).Length < 640 * 480, "the repeating half compresses");
    }

    [Fact]
    public async Task Eight_bit_data_stays_8_bit()
    {
        var values = Enumerable.Range(0, 32 * 4).Select(i => (double)(i * 2)).ToArray();
        var source = Save(new FitsBuilder { Bitpix = 8, Width = 32, Height = 4, Values = values }.Build());

        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None));

        Assert.Equal((8, 1), (probe.Bits, probe.SampleFormat));
        Assert.Equal(values, probe.Values);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(-32)]
    [InlineData(-64)]
    public async Task Wide_and_floating_point_data_becomes_32_bit_floating_point(int bitpix)
    {
        // Values far beyond 16 bits (32-bit integers), or fractions (floating point): neither may be cut.
        var values = Enumerable.Range(0, 16 * 4).Select(i => bitpix == 32 ? i * 100_000.0 - 50_000 : i * 0.015625 - 0.25).ToArray();
        var source = Save(new FitsBuilder { Bitpix = bitpix, Width = 16, Height = 4, Values = values }.Build());

        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None));

        Assert.Equal((32, 3), (probe.Bits, probe.SampleFormat));
        Assert.Equal(values.Select(v => (double)(float)v), probe.Values);
    }

    [Fact]
    public async Task Signed_16_bit_data_with_negative_values_is_not_clipped()
    {
        // A calibrated frame without BZERO: negative values don't fit unsigned 16 bits, so the TIFF takes floats.
        double[] values = [-120, -1, 0, 5, 300, 32000, -32768, 7];
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 8, Height = 1, Values = values }.Build());

        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None));

        Assert.Equal((32, 3), (probe.Bits, probe.SampleFormat));
        Assert.Equal(values, probe.Values);
    }

    [Fact]
    public async Task A_stretched_tiff_keeps_the_depth_and_lifts_the_sky()
    {
        var values = Sky(200, 100, 1000, 40);
        values[5050] = 60000; // a star
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 200, Height = 100, Values = values, Bzero = 32768 }.Build());

        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o =>
        {
            o.TiffCompression = TiffCompression.None;
            o.FitsTiff = FitsTiffMode.Stretched;
        }));

        Assert.Equal((16, 1), (probe.Bits, probe.SampleFormat));
        var sorted = probe.Values.Order().ToArray();
        // The sky's median lands at about 0.2 of the range, the star at the top.
        Assert.InRange(sorted[sorted.Length / 2], 0.17 * 65535, 0.23 * 65535);
        Assert.Equal(65535, probe.Values[5050]);
    }

    [Fact]
    public async Task A_mono_frame_becomes_a_stretched_grey_png()
    {
        var values = Sky(120, 80, 2000, 60);
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 120, Height = 80, Values = values, Bzero = 32768 }.Build());

        using var png = new MagickImage(await ConvertAsync(source, "png"));

        Assert.Equal((120u, 80u), (png.Width, png.Height));
        Assert.Equal(ColorType.Grayscale, png.ColorType);
        var grey = png.GetPixels().ToByteArray(PixelMapping.RGB)!.Where((_, i) => i % 3 == 0).Order().ToArray();
        Assert.InRange(grey[grey.Length / 2], 45, 58); // 0.2 × 255 ≈ 51
    }

    /// <summary>
    /// A colour camera's mosaic in the RGGB layout: a red nebula left of the middle (its red pixels bright, from an
    /// eighth to three eighths of the width and the middle third of the height), neutral sky around it.
    /// </summary>
    private static double[] RedNebula(int width, int height)
    {
        var values = Sky(width, height, 1500, 50, seed: 3);
        for (var y = height / 3; y < height * 2 / 3; y++)
        {
            for (var x = width / 8; x < width * 3 / 8; x++)
            {
                if (y % 2 == 0 && x % 2 == 0)
                    values[y * width + x] += 6000;
            }
        }
        return values;
    }

    [Theory]
    [InlineData("RGGB", 0, 0, "BAYERPAT", 0)] // red
    [InlineData("BGGR", 0, 0, "BAYERPAT", 2)] // the same pixels read as blue
    [InlineData("GRBG", 1, 0, "BAYERPAT", 0)] // shifted by one column: RGGB again
    [InlineData("GBRG", 0, 1, "BAYERPAT", 0)] // shifted by one row: RGGB again
    [InlineData("RGGB", 0, 0, "COLORTYP", 0)] // the other keyword
    public async Task A_colour_camera_frame_is_debayered_with_the_header_pattern(string pattern, int offsetX, int offsetY, string keyword, int bright)
    {
        var builder = new FitsBuilder { Bitpix = 16, Width = 80, Height = 60, Values = RedNebula(80, 60), Bzero = 32768 };
        builder.Cards.Add((keyword, $"'{pattern}'"));
        if (offsetX != 0)
            builder.Cards.Add(("XBAYROFF", offsetX.ToString(CultureInfo.InvariantCulture)));
        if (offsetY != 0)
            builder.Cards.Add(("YBAYROFF", offsetY.ToString(CultureInfo.InvariantCulture)));
        var source = Save(builder.Build());

        using var png = new MagickImage(await ConvertAsync(source, "png"));

        var nebula = Pixel(png, 20, 30);
        var others = Enumerable.Range(0, 3).Where(c => c != bright).Select(c => (int)nebula.GetChannel((uint)c)).ToArray();
        Assert.True(nebula.GetChannel((uint)bright) > others.Max() + 60, $"nebula {nebula.ToColor()}");
        // The unlinked stretch leaves the sky neutral.
        var sky = Pixel(png, 60, 30);
        Assert.InRange(Math.Abs(sky.GetChannel(0) - sky.GetChannel(1)) + Math.Abs(sky.GetChannel(2) - sky.GetChannel(1)), 0, 40);
    }

    [Fact]
    public async Task A_colour_camera_frame_stays_mono_when_the_preset_says_so()
    {
        var builder = new FitsBuilder { Bitpix = 16, Width = 80, Height = 60, Values = RedNebula(80, 60), Bzero = 32768 };
        builder.Cards.Add(("BAYERPAT", "'RGGB'"));
        var source = Save(builder.Build());

        using var jpg = new MagickImage(await ConvertAsync(source, "jpg", o => o.FitsDebayer = false));

        // The mosaic as it is: the red pixels bright, their neighbours dark, all grey.
        var rgb = jpg.GetPixels().ToByteArray(PixelMapping.RGB)!;
        Assert.All(Enumerable.Range(0, rgb.Length / 3), i => Assert.True(rgb[i * 3] == rgb[i * 3 + 1] && rgb[i * 3] == rgb[i * 3 + 2]));
        Assert.True(Pixel(jpg, 20, 30).GetChannel(0) > Pixel(jpg, 21, 30).GetChannel(0) + 60);
    }

    [Fact]
    public async Task A_linear_tiff_of_a_colour_camera_frame_keeps_the_mosaic()
    {
        var values = RedNebula(40, 20);
        var builder = new FitsBuilder { Bitpix = 16, Width = 40, Height = 20, Values = values, Bzero = 32768 };
        builder.Cards.Add(("BAYERPAT", "'RGGB'"));
        var source = Save(builder.Build());

        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None));

        Assert.Equal(1, probe.Samples);
        Assert.Equal(values.Select(v => Math.Round(v)), probe.Values);
    }

    [Fact]
    public async Task Three_planes_are_red_green_and_blue()
    {
        const int width = 50, height = 40;
        var red = Sky(width, height, 800, 30, 1);
        var green = Sky(width, height, 800, 30, 2);
        var blue = Sky(width, height, 800, 30, 3);
        for (var y = 10; y < 30; y++)
        {
            for (var x = 5; x < 20; x++)
                blue[y * width + x] += 5000;
        }
        var source = Save(new FitsBuilder
        {
            Bitpix = -32,
            Width = width,
            Height = height,
            Planes = 3,
            Values = [.. red, .. green, .. blue],
        }.Build());

        using var png = new MagickImage(await ConvertAsync(source, "png"));
        var probe = TiffProbe.Read(await ConvertAsync(source, "tiff", o => o.TiffCompression = TiffCompression.None));

        var nebula = Pixel(png, 10, 20);
        Assert.True(nebula.GetChannel(2) > Math.Max(nebula.GetChannel(0), nebula.GetChannel(1)) + 60, $"{nebula.ToColor()}");
        Assert.Equal((3, 32, 3), (probe.Samples, probe.Bits, probe.SampleFormat));
        Assert.Equal((float)blue[20 * width + 10], probe.Values[(20 * width + 10) * 3 + 2]);
    }

    [Fact]
    public async Task Bottom_up_files_are_turned_upright_and_presets_turn_tiffs()
    {
        // A bright first stored row: the bottom of the picture when the file says BOTTOM-UP, the top otherwise.
        var values = Enumerable.Range(0, 30 * 20).Select(i => i < 30 ? 50000.0 : 1000 + i % 13).ToArray();
        var bottomUp = new FitsBuilder { Bitpix = 16, Width = 30, Height = 20, Values = values, Bzero = 32768 };
        bottomUp.Cards.Add(("ROWORDER", "'BOTTOM-UP'"));
        var topDown = new FitsBuilder { Bitpix = 16, Width = 30, Height = 20, Values = values, Bzero = 32768 };
        topDown.Cards.Add(("ROWORDER", "'TOP-DOWN'"));

        using (var png = new MagickImage(await ConvertAsync(Save(bottomUp.Build()), "png")))
        {
            Assert.Equal(255, Pixel(png, 3, 19).GetChannel(0));
            Assert.True(Pixel(png, 3, 0).GetChannel(0) < 200);
        }
        using (var png = new MagickImage(await ConvertAsync(Save(topDown.Build()), "png")))
            Assert.Equal(255, Pixel(png, 3, 0).GetChannel(0));

        // Turned right: the top row becomes the right-hand column.
        var probe = TiffProbe.Read(await ConvertAsync(Save(topDown.Build()), "tiff", o =>
        {
            o.TiffCompression = TiffCompression.None;
            o.Rotation = ImageRotation.Right90;
        }));
        Assert.Equal((20, 30), (probe.Width, probe.Height));
        Assert.All(Enumerable.Range(0, 30), y => Assert.Equal(50000, probe.Values[y * 20 + 19]));
        Assert.True(probe.Values[0] < 2000);
    }

    [Fact]
    public void The_header_is_read_like_the_standard_says()
    {
        var builder = new FitsBuilder { Bitpix = 16, Width = 4, Height = 2, Values = [0, 1, 2, 3, 4, 5, 6, 7] };
        builder.Cards.Add(("BZERO", "3.2768D+04 / unsigned, Fortran exponent"));
        builder.Cards.Add(("BSCALE", "1.0"));
        builder.Cards.Add(("BAYERPAT", "'gbrg    '  / lower case and padded"));
        builder.Cards.Add(("OBJECT", "'M 31 ''Andromeda'''"));
        builder.Cards.Add(("XBAYROFF", "-1"));
        builder.Cards.Add(("BZERO", "0")); // repeated: the first one counts
        var path = Save(builder.Build(writeScale: false));

        var image = Fits.Read(path, TestContext.Current.CancellationToken);

        Assert.Equal((4, 2, 16), (image.Width, image.Height, image.Bitpix));
        Assert.Equal("GBRG", image.BayerPattern);
        Assert.Equal(1, image.BayerOffsetX);
        Assert.Equal(32768f, image.Planes[0][0]);
        Assert.True(image.Integral);
    }

    [Fact]
    public async Task A_picture_in_an_image_extension_is_found()
    {
        // fpack-free pipelines write an empty primary HDU and the picture in an IMAGE extension.
        var values = Sky(40, 30, 500, 20);
        var source = Save(new FitsBuilder { Bitpix = -32, Width = 40, Height = 30, Values = values, InExtension = true }.Build());

        using var png = new MagickImage(await ConvertAsync(source, "png"));

        Assert.Equal((40u, 30u), (png.Width, png.Height));
    }

    [Fact]
    public async Task Tile_compressed_and_damaged_files_fail_with_a_reason()
    {
        var compressed = Save(new FitsBuilder { CompressedTable = true }.Build(), "packed.fits");
        var notFits = Save(Encoding.ASCII.GetBytes(new string('x', 3000)), "fake.fts");
        var cut = new FitsBuilder { Bitpix = 16, Width = 64, Height = 64, Values = Sky(64, 64, 100, 5) }.Build();
        var truncated = Save(cut[..(2880 + 1000)], "cut.fits");

        Assert.Contains("fpack", Assert.Throws<InvalidDataException>(() => Fits.Read(compressed, TestContext.Current.CancellationToken)).Message);
        Assert.Contains("not a FITS", Assert.Throws<InvalidDataException>(() => Fits.Read(notFits, TestContext.Current.CancellationToken)).Message);
        Assert.Throws<EndOfStreamException>(() => Fits.Read(truncated, TestContext.Current.CancellationToken));

        var job = await fx.ConvertAsync([compressed], new Preset { TargetFormat = "png" });
        Assert.Equal(JobState.Failed, job.State);
    }

    [Fact]
    public void The_stretch_takes_the_median_to_the_target_background()
    {
        var values = Sky(100, 100, 0.1, 0.01).Select(v => (float)v).ToArray();

        var (shadows, midtones) = FitsRendering.StretchParameters(values);

        var median = values.Order().ElementAt(values.Length / 2);
        Assert.Equal(FitsRendering.TargetBackground, FitsRendering.Midtones(midtones, (median - shadows) / (1 - shadows)), 2);
        Assert.Equal(0.5, FitsRendering.Midtones(0.3, 0.3), 6);
        Assert.Equal((0.0, 1.0), (FitsRendering.Midtones(0.3, 0), FitsRendering.Midtones(0.3, 1)));
    }

    [Fact]
    public async Task Fits_reaches_pdf_and_jpg_through_the_stretched_picture()
    {
        var source = Save(new FitsBuilder { Bitpix = 16, Width = 64, Height = 48, Values = Sky(64, 48, 3000, 100), Bzero = 32768 }.Build(), "m42.fts");

        var pdf = await ConvertAsync(source, "pdf");
        var planner = fx.Catalog.CreatePlanner();

        Assert.Equal(".pdf", Path.GetExtension(pdf));
        Assert.DoesNotContain(planner.Plan("fits", "pdf")!.Steps, s => s.To == "tiff");
        Assert.Single(planner.Plan("fits", "tiff")!.Steps);
    }
}

/// <summary>Writes FITS files: a primary HDU (or an empty one and an IMAGE extension), big-endian data, 2880-byte blocks.</summary>
internal sealed class FitsBuilder
{
    public int Bitpix { get; init; } = 16;
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public int Planes { get; init; } = 1;

    /// <summary>Physical values, plane after plane, row after row; stored as (value − BZERO) / BSCALE.</summary>
    public double[] Values { get; init; } = [0];

    public double Bzero { get; init; }
    public bool InExtension { get; init; }

    /// <summary>A primary HDU without data and a tile-compressed image (a BINTABLE with ZIMAGE = T), as fpack writes.</summary>
    public bool CompressedTable { get; init; }

    /// <summary>Extra cards (keyword, value as written after "= ").</summary>
    public List<(string Keyword, string Value)> Cards { get; } = [];

    public byte[] Build(bool writeScale = true)
    {
        var file = new MemoryStream();
        if (CompressedTable)
        {
            WriteHeader(file, [("SIMPLE", "T"), ("BITPIX", "8"), ("NAXIS", "0")]);
            WriteHeader(file, [("XTENSION", "'BINTABLE'"), ("BITPIX", "8"), ("NAXIS", "2"), ("NAXIS1", "8"), ("NAXIS2", "1"),
                ("PCOUNT", "0"), ("GCOUNT", "1"), ("TFIELDS", "1"), ("ZIMAGE", "T")]);
            file.Write(new byte[8]);
            Pad(file, 8, 0);
            return file.ToArray();
        }

        var image = new List<(string, string)>();
        var axes = new List<(string, string)>
        {
            ("BITPIX", Bitpix.ToString(CultureInfo.InvariantCulture)),
            ("NAXIS", Planes > 1 ? "3" : "2"),
            ("NAXIS1", Width.ToString(CultureInfo.InvariantCulture)),
            ("NAXIS2", Height.ToString(CultureInfo.InvariantCulture)),
        };
        if (Planes > 1)
            axes.Add(("NAXIS3", Planes.ToString(CultureInfo.InvariantCulture)));
        if (InExtension)
        {
            WriteHeader(file, [("SIMPLE", "T"), ("BITPIX", "16"), ("NAXIS", "0"), ("EXTEND", "T")]);
            image.Add(("XTENSION", "'IMAGE   '"));
            image.AddRange(axes);
            image.Add(("PCOUNT", "0"));
            image.Add(("GCOUNT", "1"));
        }
        else
        {
            image.Add(("SIMPLE", "T"));
            image.AddRange(axes);
        }
        if (writeScale && Bzero != 0)
        {
            image.Add(("BZERO", Bzero.ToString(CultureInfo.InvariantCulture)));
            image.Add(("BSCALE", "1"));
        }
        image.AddRange(Cards);
        WriteHeader(file, image);

        var size = Math.Abs(Bitpix) / 8;
        var bytes = new byte[size];
        foreach (var value in Values)
        {
            var stored = value - Bzero;
            switch (Bitpix)
            {
                case 8: bytes[0] = (byte)stored; break;
                case 16: BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)Math.Round(stored))); break;
                case 32: BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)Math.Round(stored))); break;
                case 64: BinaryPrimitives.WriteInt64BigEndian(bytes, (long)Math.Round(stored)); break;
                case -32: BinaryPrimitives.WriteSingleBigEndian(bytes, (float)stored); break;
                default: BinaryPrimitives.WriteDoubleBigEndian(bytes, stored); break;
            }
            file.Write(bytes);
        }
        Pad(file, Values.Length * size, 0);
        return file.ToArray();
    }

    private static void WriteHeader(Stream file, IEnumerable<(string Keyword, string Value)> cards)
    {
        var text = new StringBuilder();
        foreach (var (keyword, value) in cards)
            text.Append($"{keyword,-8}= {value,20}".PadRight(80));
        text.Append("END".PadRight(80));
        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        file.Write(bytes);
        Pad(file, bytes.Length, (byte)' ');
    }

    private static void Pad(Stream file, long length, byte fill)
    {
        for (var i = length % 2880; i is > 0 and < 2880; i++)
            file.WriteByte(fill);
    }
}

/// <summary>Reads what the tests check of a TIFF: the first IFD's layout and, uncompressed, its samples.</summary>
internal sealed record TiffProbe(int Width, int Height, int Bits, int SampleFormat, int Samples, double[] Values)
{
    public static TiffProbe Read(string path)
    {
        var file = File.ReadAllBytes(path);
        Assert.Equal("II*\0", Encoding.ASCII.GetString(file, 0, 4));
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd));
        var tags = new Dictionary<int, uint[]>();
        for (var e = 0; e < count; e++)
        {
            var entry = file.AsSpan(ifd + 2 + e * 12, 12);
            var (tag, type, n) = (BinaryPrimitives.ReadUInt16LittleEndian(entry), BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
            var size = type == 3 ? 2 : 4;
            var at = n * size <= 4 && type != 5 ? entry[8..] : file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
            var numbers = new uint[type == 5 ? 1 : n];
            for (var i = 0; i < numbers.Length; i++)
                numbers[i] = size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(at[(i * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(at[(i * 4)..]);
            tags[tag] = numbers;
        }
        var (width, height, bits, samples) = ((int)tags[256][0], (int)tags[257][0], (int)tags[258][0], (int)tags[277][0]);
        var format = tags.TryGetValue(339, out var f) ? (int)f[0] : 1;
        var values = new List<double>();
        if (tags[259][0] == 1)
        {
            for (var s = 0; s < tags[273].Length; s++)
            {
                var strip = file.AsSpan((int)tags[273][s], (int)tags[279][s]);
                for (var i = 0; i < strip.Length; i += bits / 8)
                {
                    values.Add(bits switch
                    {
                        8 => strip[i],
                        16 => BinaryPrimitives.ReadUInt16LittleEndian(strip[i..]),
                        _ => BinaryPrimitives.ReadSingleLittleEndian(strip[i..]),
                    });
                }
            }
        }
        return new TiffProbe(width, height, bits, format, samples, [.. values]);
    }
}
