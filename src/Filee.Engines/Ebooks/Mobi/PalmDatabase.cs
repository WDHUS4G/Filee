// The Palm database (PDB) container of MOBI, AZW, AZW3, AZW4 and PRC books: a 78-byte header with the type and
// creator ("BOOKMOBI", "TEXtREAd"), then a table of record offsets. Written from the PalmOS database format
// description; all numbers are big-endian.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Ebooks;

internal sealed class PalmDatabase
{
    private readonly byte[] _data;
    private readonly int[] _offsets;

    private PalmDatabase(byte[] data, string name, string typeCreator, int[] offsets)
    {
        _data = data;
        Name = name;
        TypeCreator = typeCreator;
        _offsets = offsets;
    }

    /// <summary>Database name from the header (a short title, often truncated).</summary>
    public string Name { get; }

    /// <summary>Type and creator, e.g. "BOOKMOBI" (Mobipocket / Kindle) or "TEXtREAd" (PalmDOC).</summary>
    public string TypeCreator { get; }

    public int Count => _offsets.Length;

    public static PalmDatabase Open(byte[] data)
    {
        if (data.Length < 78)
            throw new InvalidDataException("The file is too short to be an e-book.");
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(76));
        if (count == 0 || 78 + count * 8 > data.Length)
            throw new InvalidDataException("The e-book's record table is damaged.");
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(78 + i * 8));
            if (offset > data.Length || (i > 0 && offset < offsets[i - 1]))
                throw new InvalidDataException("The e-book's record table is damaged.");
            offsets[i] = (int)offset;
        }
        var name = Encoding.Latin1.GetString(data, 0, 32).TrimEnd('\0');
        return new PalmDatabase(data, name, Encoding.ASCII.GetString(data, 60, 8), offsets);
    }

    /// <summary>Record <paramref name="index"/> (empty when out of range).</summary>
    public ReadOnlySpan<byte> Record(int index)
    {
        if (index < 0 || index >= _offsets.Length)
            return [];
        var end = index + 1 < _offsets.Length ? _offsets[index + 1] : _data.Length;
        return _data.AsSpan(_offsets[index], Math.Max(0, end - _offsets[index]));
    }

    public byte[] RecordArray(int index) => Record(index).ToArray();

    // Big-endian helpers with bounds checks: damaged books must fail with a clear error, not an exception deep inside.

    public static int U16(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) : 0;

    /// <summary>A 32-bit field; 0xFFFFFFFF ("none" in MOBI headers) becomes -1.</summary>
    public static long U32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return -1;
        var value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return value == uint.MaxValue ? -1 : value;
    }
}

/// <summary>PalmDOC compression (a simple LZ77 variant used by MOBI and PalmDOC text records).</summary>
internal static class PalmDocCompression
{
    /// <summary>Decompresses one text record.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length * 2);
        for (var i = 0; i < input.Length;)
        {
            var c = input[i++];
            if (c is >= 1 and <= 8)
            {
                // 1..8: that many literal bytes follow.
                for (var n = 0; n < c && i < input.Length; n++)
                    output.Add(input[i++]);
            }
            else if (c < 0x80)
            {
                output.Add(c); // 0 and 9..0x7F: the byte itself
            }
            else if (c >= 0xC0)
            {
                output.Add((byte)' '); // a space followed by an ASCII character
                output.Add((byte)(c ^ 0x80));
            }
            else if (i < input.Length)
            {
                // 0x80..0xBF: two bytes = 11-bit distance back and a length of 3..10.
                var pair = (c << 8) | input[i++];
                var distance = (pair >> 3) & 0x7FF;
                var length = (pair & 7) + 3;
                if (distance == 0 || distance > output.Count)
                    continue; // damaged: skip rather than fail the whole book
                var start = output.Count - distance;
                for (var n = 0; n < length; n++)
                    output.Add(output[start + n]);
            }
        }
        return [.. output];
    }
}
