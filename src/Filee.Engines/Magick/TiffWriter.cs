// A small baseline TIFF writer (TIFF 6.0) for pictures deeper than ImageMagick's Q8 build stores: 8-bit, 16-bit or 32-bit
// floating-point samples, grey or RGB, little-endian, in strips, uncompressed or with LZW or Deflate (the preset's TIFF
// compression; integer samples with the horizontal predictor). Used for FITS → TIFF (FitsRendering).

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Core.Presets;

namespace Filee.Engines.Magick;

/// <summary>How <see cref="TiffWriter"/> stores a sample.</summary>
internal enum TiffSampleKind
{
    UInt8,
    UInt16,
    Float32,
}

/// <summary>Writes planes of samples as a TIFF.</summary>
internal static class TiffWriter
{
    /// <summary>Uncompressed bytes per strip, about: small enough to compress strip by strip, large enough to be quick.</summary>
    private const int StripBytes = 256 * 1024;

    private const ushort Short = 3, Long = 4, Rational = 5;

    /// <summary>
    /// Writes one (grey) or three (RGB) planes of <paramref name="width"/> × <paramref name="height"/> values. Integer
    /// kinds are rounded and clamped to their range; floating point is written as it is.
    /// </summary>
    public static void Write(string path, IReadOnlyList<float[]> planes, int width, int height, TiffSampleKind kind,
        TiffCompression compression, int dpi, CancellationToken cancellationToken = default)
    {
        var samples = planes.Count;
        var bytesPerSample = kind switch { TiffSampleKind.UInt8 => 1, TiffSampleKind.UInt16 => 2, _ => 4 };
        var rowBytes = (long)width * samples * bytesPerSample;
        var rowsPerStrip = (int)Math.Clamp(StripBytes / rowBytes, 1, height);
        var strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        // The horizontal predictor (differences along a row) helps integer samples compress; it is not for floats.
        var predictor = compression != TiffCompression.None && kind != TiffSampleKind.Float32;
        var offsets = new uint[strips];
        var counts = new uint[strips];

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        file.Write("II*\0"u8);
        file.Write(new byte[4]); // the IFD's offset, written at the end
        var strip = new byte[rowsPerStrip * rowBytes];
        var lzw = compression == TiffCompression.Lzw ? new LzwEncoder() : null;
        for (var s = 0; s < strips; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstRow = s * rowsPerStrip;
            var rows = Math.Min(rowsPerStrip, height - firstRow);
            var data = strip.AsSpan(0, (int)(rows * rowBytes));
            for (var r = 0; r < rows; r++)
                FillRow(data.Slice((int)(r * rowBytes), (int)rowBytes), planes, (firstRow + r) * width, width, kind, predictor);

            byte[] encoded = compression switch
            {
                TiffCompression.Lzw => lzw!.Encode(data),
                TiffCompression.Zip => Deflate(data),
                _ => data.ToArray(),
            };
            offsets[s] = Offset(file.Position);
            counts[s] = (uint)encoded.Length;
            file.Write(encoded);
            if ((file.Position & 1) != 0)
                file.WriteByte(0);
        }

        var bits = (ushort)(bytesPerSample * 8);
        var format = (ushort)(kind == TiffSampleKind.Float32 ? 3 : 1);
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (256, Long, 1, UInt32s((uint)width)),
            (257, Long, 1, UInt32s((uint)height)),
            (258, Short, (uint)samples, UInt16s([.. Enumerable.Repeat(bits, samples)])),
            (259, Short, 1, UInt16s(compression switch { TiffCompression.Lzw => 5, TiffCompression.Zip => 8, _ => 1 })),
            (262, Short, 1, UInt16s((ushort)(samples == 3 ? 2 : 1))), // RGB, or grey with 0 = black
            (273, Long, (uint)strips, UInt32s(offsets)),
            (277, Short, 1, UInt16s((ushort)samples)),
            (278, Long, 1, UInt32s((uint)rowsPerStrip)),
            (279, Long, (uint)strips, UInt32s(counts)),
            (282, Rational, 1, UInt32s((uint)Math.Max(1, dpi), 1)),
            (283, Rational, 1, UInt32s((uint)Math.Max(1, dpi), 1)),
            (284, Short, 1, UInt16s(1)), // samples of a pixel side by side
            (296, Short, 1, UInt16s(2)), // inches
        };
        if (predictor)
            entries.Add((317, Short, 1, UInt16s(2)));
        entries.Add((339, Short, (uint)samples, UInt16s([.. Enumerable.Repeat(format, samples)])));

        // The IFD, then the values that don't fit into its entries' four bytes.
        var ifd = file.Position;
        var extra = ifd + 2 + entries.Count * 12 + 4;
        var directory = new MemoryStream();
        var values = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(number, (ushort)entries.Count);
        directory.Write(number[..2]);
        foreach (var (tag, type, count, value) in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(number, tag);
            directory.Write(number[..2]);
            BinaryPrimitives.WriteUInt16LittleEndian(number, type);
            directory.Write(number[..2]);
            BinaryPrimitives.WriteUInt32LittleEndian(number, count);
            directory.Write(number);
            if (value.Length <= 4)
            {
                directory.Write(value);
                directory.Write(new byte[4 - value.Length]);
                continue;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(number, Offset(extra + values.Length));
            directory.Write(number);
            values.Write(value);
            if ((values.Length & 1) != 0)
                values.WriteByte(0);
        }
        directory.Write(new byte[4]); // no next IFD
        directory.WriteTo(file);
        values.WriteTo(file);
        Offset(file.Position);
        file.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(number, Offset(ifd));
        file.Write(number);
    }

    /// <summary>One row of samples, pixel by pixel, little-endian; with the predictor, each sample minus the one before.</summary>
    private static void FillRow(Span<byte> row, IReadOnlyList<float[]> planes, int start, int width, TiffSampleKind kind, bool predictor)
    {
        var samples = planes.Count;
        switch (kind)
        {
            case TiffSampleKind.UInt8:
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < samples; c++)
                        row[x * samples + c] = (byte)Math.Clamp(Math.Round(Finite(planes[c][start + x])), 0, 255);
                }
                if (predictor)
                {
                    for (var i = row.Length - 1; i >= samples; i--)
                        row[i] = unchecked((byte)(row[i] - row[i - samples]));
                }
                break;
            case TiffSampleKind.UInt16:
                Span<ushort> previous = stackalloc ushort[3];
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < samples; c++)
                    {
                        var value = (ushort)Math.Clamp(Math.Round(Finite(planes[c][start + x])), 0, 65535);
                        var stored = value;
                        if (predictor)
                        {
                            stored = unchecked((ushort)(value - previous[c]));
                            previous[c] = value;
                        }
                        BinaryPrimitives.WriteUInt16LittleEndian(row[((x * samples + c) * 2)..], stored);
                    }
                }
                break;
            default:
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < samples; c++)
                        BinaryPrimitives.WriteSingleLittleEndian(row[((x * samples + c) * 4)..], planes[c][start + x]);
                }
                break;
        }
    }

    private static double Finite(float value) => float.IsFinite(value) ? value : 0;

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>A classic TIFF addresses at most 4 GB.</summary>
    private static uint Offset(long position) =>
        position <= uint.MaxValue ? (uint)position : throw new InvalidOperationException("The picture is too large for a TIFF file (over 4 GB).");

    private static byte[] UInt16s(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static byte[] UInt32s(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    /// <summary>
    /// TIFF's LZW (TIFF 6.0 section 13): codes of 9 to 12 bits, most significant bit first, starting with a clear code,
    /// one bit wider as soon as the next code would not fit ("early change"), a clear code when the table is full.
    /// </summary>
    private sealed class LzwEncoder
    {
        private const int Clear = 256, End = 257, First = 258, Full = 4094;

        // The table: (prefix code << 8 | next byte) → code, valid where its stamp is the current generation.
        private readonly short[] _codes = new short[1 << 20];
        private readonly int[] _stamps = new int[1 << 20];
        private int _generation;

        public byte[] Encode(ReadOnlySpan<byte> data)
        {
            var output = new MemoryStream(data.Length / 2 + 16);
            ulong buffer = 0;
            var buffered = 0;
            void Put(int code, int width)
            {
                buffer = (buffer << width) | (uint)code;
                buffered += width;
                while (buffered >= 8)
                {
                    buffered -= 8;
                    output.WriteByte((byte)(buffer >> buffered));
                }
            }

            var width = 9;
            var next = First;
            _generation++;
            Put(Clear, width);
            if (data.Length > 0)
            {
                int prefix = data[0];
                for (var i = 1; i < data.Length; i++)
                {
                    var key = (prefix << 8) | data[i];
                    if (_stamps[key] == _generation)
                    {
                        prefix = _codes[key];
                        continue;
                    }
                    Put(prefix, width);
                    _stamps[key] = _generation;
                    _codes[key] = (short)next++;
                    prefix = data[i];
                    if (next == Full)
                    {
                        Put(Clear, width);
                        _generation++;
                        next = First;
                        width = 9;
                    }
                    else if (next > (1 << width) - 1)
                    {
                        width++;
                    }
                }
                Put(prefix, width);
                // The reader adds a table entry for the last code too, and may widen before the end code.
                next++;
                if (next == Full)
                {
                    Put(Clear, width);
                    width = 9;
                }
                else if (next > (1 << width) - 1)
                {
                    width++;
                }
            }
            Put(End, width);
            if (buffered > 0)
                output.WriteByte((byte)(buffer << (8 - buffered)));
            return output.ToArray();
        }
    }
}
