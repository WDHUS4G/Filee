// DICOM: Filee's reader of uncompressed images (Magick/Dicom.cs) and ImageMagick for 8-bit ones. The files are built
// element by element in the tests (DicomBuilder), as scanners write them.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class DicomTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static byte Grey(IMagickImage<byte> image, int x, int y) => image.GetPixels().GetPixel(x, y).ToColor()!.R;

    private string Save(byte[] dicom, string name = "scan.dcm")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, dicom);
        return path;
    }

    [Fact]
    public async Task A_12_bit_scan_converts_to_png_with_its_full_range()
    {
        // 12 bits in 16-bit words, as CT, MR and X-ray scanners store them; ImageMagick's Q8 build can't read these.
        var source = Save(new DicomBuilder { Ramp = (0, 4095) }.Build());

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var png = new MagickImage(job.Outputs.Single());
        Assert.Equal((64u, 32u), (png.Width, png.Height));
        Assert.Equal(0, Grey(png, 0, 5));
        Assert.Equal(255, Grey(png, 63, 5));
        Assert.InRange(Grey(png, 32, 5), 120, 136);
    }

    [Fact]
    public void Rescale_and_window_apply_like_a_viewer()
    {
        // CT: signed values, Hounsfield units = stored − 1024; a soft-tissue window (centre 40, width 400).
        using var frames = Dicom.TryRead(Save(new DicomBuilder
        {
            Signed = true,
            Ramp = (-200, 1000),
            Intercept = -1024,
            Window = (40, 400),
        }.Build()))!;

        var image = Assert.Single(frames);
        // −1224 HU on the left is below the window (black), −24 HU on the right is inside it:
        // ((−24 − (40 − 0.5)) / 399 + 0.5) × 255 ≈ 87.
        Assert.Equal(0, Grey(image, 0, 0));
        Assert.InRange(Grey(image, 63, 0), 85, 89);
    }

    [Fact]
    public void Monochrome1_is_inverted_and_frames_are_kept()
    {
        using var frames = Dicom.TryRead(Save(new DicomBuilder
        {
            Photometric = "MONOCHROME1",
            Frames = 3,
            Ramp = (0, 255),
            BitsAllocated = 8,
            BitsStored = 8,
        }.Build()))!;

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(255, Grey(f, 0, 0))); // 0 is white in MONOCHROME1
        Assert.All(frames, f => Assert.Equal(0, Grey(f, 63, 0)));
    }

    [Fact]
    public void Implicit_vr_files_without_a_preamble_and_big_endian_files_are_read()
    {
        using (var implicitVr = Dicom.TryRead(Save(new DicomBuilder { ImplicitVr = true, Preamble = false, Ramp = (0, 1000) }.Build()))!)
            Assert.Equal(255, Grey(implicitVr.Single(), 63, 0));
        using (var bigEndian = Dicom.TryRead(Save(new DicomBuilder { BigEndian = true, Ramp = (0, 1000) }.Build()))!)
            Assert.Equal(255, Grey(bigEndian.Single(), 63, 0));
        using (var deflated = Dicom.TryRead(Save(new DicomBuilder { Deflated = true, Ramp = (0, 1000) }.Build()))!)
            Assert.Equal(255, Grey(deflated.Single(), 63, 0));
    }

    [Fact]
    public void Ybr_full_becomes_rgb()
    {
        // Y 128, Cb 128, Cr 200: a reddish grey. R = Y + 1.402 (Cr − 128) ≈ 229, G ≈ 77, B = 128.
        using var frames = Dicom.TryRead(Save(new DicomBuilder { Rgb = true, Ybr = (128, 128, 200), BitsAllocated = 8, BitsStored = 8 }.Build()))!;
        var pixel = frames.Single().GetPixels().GetPixel(3, 3).ToColor()!;
        Assert.Equal((229, 77, 128), (pixel.R, pixel.G, pixel.B));
    }

    [Fact]
    public void Sequences_of_undefined_length_are_skipped()
    {
        using var frames = Dicom.TryRead(Save(new DicomBuilder { NestedSequence = true, Ramp = (0, 4095) }.Build()))!;
        Assert.Equal(255, Grey(frames.Single(), 63, 0));
    }

    [Fact]
    public void Rgb_planes_are_interleaved()
    {
        using var frames = Dicom.TryRead(Save(new DicomBuilder { Rgb = true, BitsAllocated = 8, BitsStored = 8 }.Build()))!;
        var pixel = frames.Single().GetPixels().GetPixel(10, 10).ToColor()!;
        Assert.Equal((200, 100, 50), (pixel.R, pixel.G, pixel.B));
    }

    [Fact]
    public void Compressed_pixel_data_is_left_to_imagemagick()
    {
        var path = Save(new DicomBuilder { TransferSyntax = "1.2.840.10008.1.2.4.70" }.Build()); // JPEG lossless
        Assert.Null(Dicom.TryRead(path));
    }
}

/// <summary>Writes small DICOM Part 10 files.</summary>
internal sealed class DicomBuilder
{
    public int Width { get; init; } = 64;
    public int Height { get; init; } = 32;
    public int Frames { get; init; } = 1;
    public int BitsAllocated { get; init; } = 16;
    public int BitsStored { get; init; } = 12;
    public bool Signed { get; init; }
    public string Photometric { get; init; } = "MONOCHROME2";
    public bool Rgb { get; init; }
    /// <summary>Stored values from the left column to the right one.</summary>
    public (int Left, int Right) Ramp { get; init; } = (0, 4095);
    public double? Intercept { get; init; }
    public (double Center, double Width)? Window { get; init; }
    public bool ImplicitVr { get; init; }
    public bool BigEndian { get; init; }
    public bool Preamble { get; init; } = true;
    public bool NestedSequence { get; init; }
    public bool Deflated { get; init; }
    /// <summary>With <see cref="Rgb"/>: the planes hold this Y, Cb, Cr (YBR_FULL) instead of R 200, G 100, B 50.</summary>
    public (byte Y, byte Cb, byte Cr)? Ybr { get; init; }
    /// <summary>A compressed transfer syntax: the pixel data becomes one encapsulated fragment.</summary>
    public string? TransferSyntax { get; init; }

    public byte[] Build()
    {
        var syntax = TransferSyntax ?? (ImplicitVr ? "1.2.840.10008.1.2" : BigEndian ? "1.2.840.10008.1.2.2"
            : Deflated ? "1.2.840.10008.1.2.1.99" : "1.2.840.10008.1.2.1");
        using var body = new MemoryStream();
        Element(body, 0x0008, 0x0016, "UI", Text("1.2.840.10008.5.1.4.1.1.7"));
        if (NestedSequence)
        {
            // (0008,1115) Referenced Series Sequence of undefined length with one item of undefined length.
            Header(body, 0x0008, 0x1115, "SQ", 0xFFFFFFFF);
            ItemHeader(body, 0xE000, 0xFFFFFFFF);
            Element(body, 0x0020, 0x000E, "UI", Text("1.2.3"));
            ItemHeader(body, 0xE00D, 0);
            ItemHeader(body, 0xE0DD, 0);
        }
        Element(body, 0x0028, 0x0002, "US", UShort(Rgb ? 3 : 1));
        Element(body, 0x0028, 0x0004, "CS", Text(Ybr is not null ? "YBR_FULL" : Rgb ? "RGB" : Photometric));
        if (Rgb)
            Element(body, 0x0028, 0x0006, "US", UShort(1)); // planar: all red, then green, then blue
        if (Frames > 1)
            Element(body, 0x0028, 0x0008, "IS", Text(Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Element(body, 0x0028, 0x0010, "US", UShort(Height));
        Element(body, 0x0028, 0x0011, "US", UShort(Width));
        Element(body, 0x0028, 0x0100, "US", UShort(BitsAllocated));
        Element(body, 0x0028, 0x0101, "US", UShort(BitsStored));
        Element(body, 0x0028, 0x0102, "US", UShort(BitsStored - 1));
        Element(body, 0x0028, 0x0103, "US", UShort(Signed ? 1 : 0));
        if (Window is { } window)
        {
            Element(body, 0x0028, 0x1050, "DS", Text($"{window.Center}\\{window.Center + 10}"));
            Element(body, 0x0028, 0x1051, "DS", Text($"{window.Width}\\{window.Width + 10}"));
        }
        if (Intercept is { } intercept)
        {
            Element(body, 0x0028, 0x1052, "DS", Text(intercept.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Element(body, 0x0028, 0x1053, "DS", Text("1"));
        }
        if (TransferSyntax is not null)
        {
            // Encapsulated: undefined length, an empty offset table and one fragment.
            Header(body, 0x7FE0, 0x0010, "OB", 0xFFFFFFFF);
            ItemHeader(body, 0xE000, 0);
            ItemHeader(body, 0xE000, 4);
            body.Write([0xFF, 0xD8, 0xFF, 0xD9]);
            ItemHeader(body, 0xE0DD, 0);
        }
        else
        {
            Element(body, 0x7FE0, 0x0010, BitsAllocated == 8 ? "OB" : "OW", Pixels());
        }

        using var meta = new MemoryStream();
        Write(meta, 0x0002, 0x0001, "OB", [0, 1], littleEndian: true, explicitVr: true);
        Write(meta, 0x0002, 0x0010, "UI", Text(syntax), littleEndian: true, explicitVr: true);

        using var file = new MemoryStream();
        if (Preamble)
        {
            file.Write(new byte[128]);
            file.Write("DICM"u8);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)meta.Length);
            Write(file, 0x0002, 0x0000, "UL", length, littleEndian: true, explicitVr: true);
            meta.WriteTo(file);
        }
        if (Deflated)
        {
            using var deflate = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
            body.WriteTo(deflate);
        }
        else
        {
            body.WriteTo(file);
        }
        return file.ToArray();
    }

    private byte[] Pixels()
    {
        var bytesPerSample = BitsAllocated / 8;
        if (Rgb)
        {
            var (first, second, third) = Ybr ?? (200, 100, 50);
            var planes = new byte[Width * Height * 3];
            Array.Fill(planes, first, 0, Width * Height);
            Array.Fill(planes, second, Width * Height, Width * Height);
            Array.Fill(planes, third, 2 * Width * Height, Width * Height);
            return planes;
        }
        var frame = new byte[Width * Height * bytesPerSample];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var value = Ramp.Left + (Ramp.Right - Ramp.Left) * x / (Width - 1);
                var stored = (ushort)(value & ((1 << BitsStored) - 1)); // two's complement in the stored bits
                var at = (y * Width + x) * bytesPerSample;
                if (bytesPerSample == 1)
                    frame[at] = (byte)stored;
                else if (BigEndian)
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(at), stored);
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), stored);
            }
        return Enumerable.Repeat(frame, Frames).SelectMany(f => f).ToArray();
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value.Length % 2 == 0 ? value : value + " ");

    private byte[] UShort(int value)
    {
        var bytes = new byte[2];
        if (BigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        return bytes;
    }

    private void Element(Stream stream, ushort group, ushort element, string vr, byte[] value) =>
        Write(stream, group, element, vr, value, !BigEndian, !ImplicitVr);

    private void Header(Stream stream, ushort group, ushort element, string vr, uint length) =>
        WriteHeader(stream, group, element, vr, length, !BigEndian, !ImplicitVr);

    private void ItemHeader(Stream stream, ushort element, uint length)
    {
        var header = new byte[8];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header, 0xFFFE);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), element);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), length);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header, 0xFFFE);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), element);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length);
        }
        stream.Write(header);
    }

    private static void Write(Stream stream, ushort group, ushort element, string vr, byte[] value, bool littleEndian, bool explicitVr)
    {
        WriteHeader(stream, group, element, vr, (uint)value.Length, littleEndian, explicitVr);
        stream.Write(value);
    }

    private static void WriteHeader(Stream stream, ushort group, ushort element, string vr, uint length, bool littleEndian, bool explicitVr)
    {
        var longForm = vr is "OB" or "OW" or "SQ" or "UN";
        var header = new byte[!explicitVr || longForm ? (explicitVr ? 12 : 8) : 8];
        void U16(int at, ushort v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(at), v);
        }
        void U32(int at, uint v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), v);
        }
        U16(0, group);
        U16(2, element);
        if (!explicitVr)
        {
            U32(4, length);
        }
        else
        {
            Encoding.ASCII.GetBytes(vr, header.AsSpan(4));
            if (longForm)
                U32(8, length);
            else
                U16(6, (ushort)length);
        }
        stream.Write(header);
    }
}
