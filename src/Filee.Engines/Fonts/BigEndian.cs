// Big-endian readers and writers for font data (SFNT, WOFF, WOFF2, CFF all store numbers big-endian), including the
// variable-length integers of WOFF2 (UIntBase128 and 255UInt16).

using System.Buffers.Binary;

namespace Filee.Engines.Fonts;

/// <summary>Reads big-endian values from a byte array; every read is bounds-checked.</summary>
internal sealed class BigEndianReader(byte[] data, int offset = 0, int? end = null)
{
    private readonly int _end = end ?? data.Length;

    /// <summary>The underlying array (positions are absolute indices into it).</summary>
    public byte[] Data => data;

    /// <summary>Absolute read position.</summary>
    public int Position { get; set; } = offset;

    /// <summary>Bytes left before the end of this reader's window.</summary>
    public int Remaining => _end - Position;

    public byte U8()
    {
        Need(1);
        return data[Position++];
    }

    public ushort U16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Position));
        Position += 2;
        return value;
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Position));
        Position += 4;
        return value;
    }

    public int I32() => (int)U32();

    /// <summary>Returns the next <paramref name="count"/> bytes without copying.</summary>
    public ReadOnlySpan<byte> Span(int count)
    {
        Need(count);
        var span = data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte[] Bytes(int count) => Span(count).ToArray();

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    /// <summary>WOFF2 UIntBase128: up to five bytes of 7 bits each, most significant first, no leading zeros.</summary>
    public uint UIntBase128()
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = U8();
            if (i == 0 && b == 0x80)
                throw new InvalidDataException("Invalid WOFF2 number (leading zeros).");
            if ((value & 0xFE000000) != 0)
                throw new InvalidDataException("Invalid WOFF2 number (overflow).");
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Invalid WOFF2 number (longer than five bytes).");
    }

    /// <summary>WOFF2 255UInt16: one byte below 253, else a marker byte followed by one or two bytes.</summary>
    public ushort Read255UInt16()
    {
        var code = U8();
        return code switch
        {
            253 => U16(),
            254 => (ushort)(U8() + 253 * 2),
            255 => (ushort)(U8() + 253),
            _ => code,
        };
    }

    private void Need(int count)
    {
        if (count < 0 || Position < 0 || Position > _end - count)
            throw new InvalidDataException("The font file is truncated or damaged.");
    }
}

/// <summary>A growable buffer that writes big-endian values.</summary>
internal sealed class BigEndianWriter(int capacity = 256)
{
    private byte[] _buffer = new byte[Math.Max(16, capacity)];

    /// <summary>Number of bytes written so far (also the current write position).</summary>
    public int Length { get; private set; }

    /// <summary>The bytes written so far.</summary>
    public Span<byte> Written => _buffer.AsSpan(0, Length);

    public void U8(int value)
    {
        Grow(1);
        _buffer[Length++] = (byte)value;
    }

    public void U16(int value)
    {
        Grow(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(Length), (ushort)value);
        Length += 2;
    }

    public void I16(int value) => U16((ushort)(short)value);

    public void U32(uint value)
    {
        Grow(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Length), value);
        Length += 4;
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        Grow(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(Length));
        Length += bytes.Length;
    }

    public void Zeros(int count)
    {
        Grow(count);
        _buffer.AsSpan(Length, count).Clear();
        Length += count;
    }

    /// <summary>Pads with zeros to the next multiple of four.</summary>
    public void Pad4() => Zeros((4 - (Length & 3)) & 3);

    public void SetU32(int position, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(position), value);

    /// <summary>Writes a WOFF2 UIntBase128 (see <see cref="BigEndianReader.UIntBase128"/>).</summary>
    public void UIntBase128(uint value)
    {
        var groups = 1;
        for (var v = value >> 7; v != 0; v >>= 7)
            groups++;
        for (var i = groups - 1; i >= 0; i--)
        {
            var bits = (int)(value >> (7 * i)) & 0x7F;
            U8(i > 0 ? bits | 0x80 : bits);
        }
    }

    /// <summary>Writes a WOFF2 255UInt16 in its shortest form.</summary>
    public void Write255UInt16(int value)
    {
        if (value < 253)
        {
            U8(value);
        }
        else if (value < 506)
        {
            U8(255);
            U8(value - 253);
        }
        else if (value < 762)
        {
            U8(254);
            U8(value - 506);
        }
        else
        {
            U8(253);
            U16(value);
        }
    }

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    private void Grow(int count)
    {
        if (Length + count <= _buffer.Length)
            return;
        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
    }
}
