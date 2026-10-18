// HUFF/CDIC compression of MOBI text records (Mobipocket "Huffman dictionary" compression), written from the
// format description on the MobileRead wiki: a canonical Huffman code (HUFF record) selects phrases from the
// dictionary (CDIC records); a phrase may itself be compressed and is unpacked on first use.

using System.Buffers.Binary;

namespace Filee.Engines.Ebooks;

internal sealed class HuffCdic
{
    /// <summary>Per leading byte of a code: length, whether that length is final, and the adjusted max code.</summary>
    private readonly (int Length, bool Terminal, ulong MaxCode)[] _table1 = new (int, bool, ulong)[256];
    private readonly ulong[] _minCode = new ulong[33];
    private readonly ulong[] _maxCode = new ulong[33];
    private readonly List<Phrase> _phrases = [];

    private sealed class Phrase(byte[] data, bool unpacked)
    {
        public byte[] Data { get; set; } = data;
        public bool Unpacked { get; set; } = unpacked;
        public bool Busy { get; set; }
    }

    /// <param name="huff">The HUFF record.</param>
    /// <param name="cdics">The CDIC records that follow it.</param>
    public HuffCdic(ReadOnlySpan<byte> huff, IEnumerable<byte[]> cdics)
    {
        if (huff.Length < 24 || !huff[..4].SequenceEqual("HUFF"u8))
            throw new InvalidDataException("The book's HUFF compression table is damaged.");
        var table1 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff[8..]);
        var table2 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff[12..]);
        if (table1 + 256 * 4 > huff.Length || table2 + 64 * 4 > huff.Length)
            throw new InvalidDataException("The book's HUFF compression table is damaged.");

        for (var i = 0; i < 256; i++)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(huff[(table1 + i * 4)..]);
            var length = (int)(value & 0x1F);
            if (length == 0)
                throw new InvalidDataException("The book's HUFF compression table is damaged.");
            var maxCode = (((ulong)(value >> 8) + 1) << (32 - length)) - 1;
            _table1[i] = (length, (value & 0x80) != 0, maxCode);
        }
        // Code lengths 1..32: the smallest and largest code of each length, left-aligned in 32 bits.
        for (var length = 1; length <= 32; length++)
        {
            var min = BinaryPrimitives.ReadUInt32BigEndian(huff[(table2 + (length - 1) * 8)..]);
            var max = BinaryPrimitives.ReadUInt32BigEndian(huff[(table2 + (length - 1) * 8 + 4)..]);
            _minCode[length] = (ulong)min << (32 - length);
            _maxCode[length] = (((ulong)max + 1) << (32 - length)) - 1;
        }

        foreach (var cdic in cdics)
        {
            if (cdic.Length < 16 || !cdic.AsSpan(0, 4).SequenceEqual("CDIC"u8))
                throw new InvalidDataException("The book's CDIC dictionary is damaged.");
            var total = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(8));
            var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(12));
            var count = Math.Min(1 << Math.Clamp(bits, 0, 20), total - _phrases.Count);
            for (var i = 0; i < count; i++)
            {
                var offset = 16 + PalmDatabase.U16(cdic, 16 + i * 2);
                var header = PalmDatabase.U16(cdic, offset);
                var length = Math.Min(header & 0x7FFF, Math.Max(0, cdic.Length - offset - 2));
                _phrases.Add(new Phrase(cdic.AsSpan(offset + 2, length).ToArray(), (header & 0x8000) != 0));
            }
        }
    }

    /// <summary>Decompresses one text record.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 3);
        Unpack(data, output, 0);
        return [.. output];
    }

    private void Unpack(ReadOnlySpan<byte> data, List<byte> output, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("The book's CDIC dictionary refers to itself.");
        // Read 64 bits at a time; "n" counts the bits of the current 32-bit window not yet consumed.
        Span<byte> padded = new byte[data.Length + 8];
        data.CopyTo(padded);
        long bitsLeft = data.Length * 8L;
        var position = 0;
        var window = BinaryPrimitives.ReadUInt64BigEndian(padded);
        var n = 32;
        while (true)
        {
            if (n <= 0)
            {
                position += 4;
                window = position + 8 <= padded.Length ? BinaryPrimitives.ReadUInt64BigEndian(padded[position..]) : 0;
                n += 32;
            }
            var code = (window >> n) & 0xFFFFFFFF;
            var (length, terminal, maxCode) = _table1[code >> 24];
            if (!terminal)
            {
                while (length < 32 && code < _minCode[length])
                    length++;
                maxCode = _maxCode[length];
            }
            n -= length;
            bitsLeft -= length;
            if (bitsLeft < 0)
                break;

            var index = (int)((maxCode - code) >> (32 - length));
            if (index < 0 || index >= _phrases.Count)
                throw new InvalidDataException("The book's compressed text is damaged.");
            var phrase = _phrases[index];
            if (!phrase.Unpacked)
            {
                if (phrase.Busy)
                    throw new InvalidDataException("The book's CDIC dictionary refers to itself.");
                phrase.Busy = true;
                var unpacked = new List<byte>();
                Unpack(phrase.Data, unpacked, depth + 1);
                phrase.Data = [.. unpacked];
                phrase.Unpacked = true;
                phrase.Busy = false;
            }
            output.AddRange(phrase.Data);
        }
    }
}
