// bzip2 decompression in managed code, written for ALZip archives: ALZ stores bzip2 blocks without the "BZh" stream
// header, marks each block with "DLZ\x01" (instead of the 48-bit block magic, the block CRC and the "randomised" bit)
// and ends with "DLZ\x02". Standard bzip2 streams (used by EGG) are read too. The block format itself (Huffman
// coded MTF/RLE2 symbols, Burrows-Wheeler transform, RLE1) is plain bzip2.

namespace Filee.Engines.Archives;

/// <summary>Decodes standard or ALZ-framed bzip2 data.</summary>
internal sealed class Bzip2Decoder
{
    private const ulong BlockMagic = 0x314159265359;
    private const ulong StreamEndMagic = 0x177245385090;
    private const uint AlzBlockMarker = 0x444C5A01; // "DLZ\x01"
    private const uint AlzEndMarker = 0x444C5A02;   // "DLZ\x02"
    private const int MaxCodeLength = 20;
    private const int GroupSize = 50;
    private const int MaxSelectors = 18002;

    private static readonly uint[] BlockCrcTable = CreateBlockCrcTable();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly long _maxOutput;
    private readonly byte[] _inBuffer = new byte[64 * 1024];
    private int _inPos;
    private int _inLength;
    private ulong _bits;
    private int _bitCount;
    private long _written;
    private int[] _tt = [];

    private Bzip2Decoder(Stream input, Stream output, long maxOutput)
    {
        _input = input;
        _output = output;
        _maxOutput = maxOutput;
    }

    /// <summary>Decompresses <paramref name="input"/> to <paramref name="output"/>.</summary>
    /// <param name="alzFraming">True for ALZ's variant of the stream framing (see the file comment).</param>
    /// <param name="maxOutput">Stops with an error when the data would unpack to more bytes than this.</param>
    /// <returns>Number of bytes written.</returns>
    public static long Decompress(Stream input, Stream output, bool alzFraming, long maxOutput, CancellationToken cancellationToken)
    {
        var decoder = new Bzip2Decoder(input, output, maxOutput);
        if (alzFraming)
            decoder.DecodeAlz(cancellationToken);
        else
            decoder.DecodeStandard(cancellationToken);
        return decoder._written;
    }

    private void DecodeAlz(CancellationToken cancellationToken)
    {
        // ALZip always writes 900k blocks and leaves out the stream header.
        while (TryReadBits(32, out var marker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (marker == AlzEndMarker)
                return;
            if (marker != AlzBlockMarker)
                throw new InvalidDataException("bzip2 block marker missing");
            DecodeBlock(9, expectedCrc: null);
        }
    }

    private void DecodeStandard(CancellationToken cancellationToken)
    {
        // Several streams may follow each other (pbzip2, "cat a.bz2 b.bz2").
        var first = true;
        while (true)
        {
            if (!TryReadBits(8, out var b))
            {
                if (first)
                    throw new InvalidDataException("empty bzip2 data");
                return;
            }
            // Some writers leave out the "BZ" signature and start with "h9".
            if (b == 'B')
            {
                if (ReadBits(8) != 'Z')
                    throw new InvalidDataException("not bzip2 data");
                b = ReadBits(8);
            }
            if (b != 'h')
                throw new InvalidDataException("not bzip2 data");
            var level = (int)ReadBits(8) - '0';
            if (level is < 1 or > 9)
                throw new InvalidDataException("invalid bzip2 block size");
            first = false;

            uint combined = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var magic = ((ulong)ReadBits(24) << 24) | ReadBits(24);
                var crc = ReadBits(32);
                if (magic == StreamEndMagic)
                {
                    if (crc != combined)
                        throw new InvalidDataException("bzip2 stream checksum mismatch");
                    break;
                }
                if (magic != BlockMagic)
                    throw new InvalidDataException("bzip2 block magic missing");
                if (ReadBits(1) != 0)
                    throw new InvalidDataException("randomised bzip2 blocks (bzip2 0.9.0) are not supported");
                DecodeBlock(level, crc);
                combined = ((combined << 1) | (combined >> 31)) ^ crc;
            }
            // The next stream starts on a byte boundary.
            _bitCount -= _bitCount % 8;
        }
    }

    /// <summary>Decodes one block, starting at origPtr (after magic, CRC and randomised bit).</summary>
    private void DecodeBlock(int level, uint? expectedCrc)
    {
        var blockMax = level * 100_000;
        if (_tt.Length < blockMax)
            _tt = new int[blockMax];
        var tt = _tt;

        var origPtr = (int)ReadBits(24);

        // Symbols used in this block, as a 16x16 bitmap.
        var seqToUnseq = new byte[256];
        var inUseCount = 0;
        var inUse16 = ReadBits(16);
        for (var i = 0; i < 16; i++)
        {
            if ((inUse16 & (0x8000u >> i)) == 0)
                continue;
            var bits = ReadBits(16);
            for (var j = 0; j < 16; j++)
                if ((bits & (0x8000u >> j)) != 0)
                    seqToUnseq[inUseCount++] = (byte)(i * 16 + j);
        }
        if (inUseCount == 0)
            throw new InvalidDataException("bzip2 block uses no symbols");
        var alphaSize = inUseCount + 2;

        var groupCount = (int)ReadBits(3);
        var selectorCount = (int)ReadBits(15);
        if (groupCount is < 2 or > 6 || selectorCount < 1)
            throw new InvalidDataException("invalid bzip2 block header");

        // Selectors (which Huffman table each group of 50 symbols uses), MTF and unary coded.
        var selectors = new byte[Math.Min(selectorCount, MaxSelectors)];
        Span<byte> mtfGroups = stackalloc byte[6];
        for (var i = 0; i < groupCount; i++)
            mtfGroups[i] = (byte)i;
        for (var i = 0; i < selectorCount; i++)
        {
            var j = 0;
            while (ReadBits(1) == 1)
                if (++j >= groupCount)
                    throw new InvalidDataException("invalid bzip2 selector");
            var value = mtfGroups[j];
            for (; j > 0; j--)
                mtfGroups[j] = mtfGroups[j - 1];
            mtfGroups[0] = value;
            if (i < selectors.Length)
                selectors[i] = value; // bzip2 1.0.8 reads but ignores selectors beyond the limit
        }

        // Code lengths, delta coded, and the canonical Huffman decode tables built from them.
        var limit = new int[groupCount, MaxCodeLength + 2];
        var baseTable = new int[groupCount, MaxCodeLength + 2];
        var perm = new int[groupCount, 258];
        var minLengths = new int[groupCount];
        var lengths = new int[alphaSize];
        for (var t = 0; t < groupCount; t++)
        {
            var current = (int)ReadBits(5);
            for (var i = 0; i < alphaSize; i++)
            {
                while (true)
                {
                    if (current is < 1 or > MaxCodeLength)
                        throw new InvalidDataException("invalid bzip2 code length");
                    if (ReadBits(1) == 0)
                        break;
                    current += ReadBits(1) == 0 ? 1 : -1;
                }
                lengths[i] = current;
            }
            minLengths[t] = BuildDecodeTable(lengths, alphaSize, t, limit, baseTable, perm);
        }

        // MTF / RLE2 symbols → block bytes.
        Span<byte> mtf = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
            mtf[i] = (byte)i;
        Span<int> counts = stackalloc int[256];
        counts.Clear();
        var endOfBlock = inUseCount + 1;
        var length = 0;
        var groupIndex = -1;
        var groupLeft = 0;
        var table = 0;

        var symbol = NextSymbol();
        while (symbol != endOfBlock)
        {
            if (symbol <= 1)
            {
                // RUNA / RUNB: a bijective base-2 run length of the symbol at the MTF front.
                var run = 0;
                var weight = 1;
                do
                {
                    if (weight > blockMax)
                        throw new InvalidDataException("bzip2 run too long");
                    run += (symbol + 1) * weight;
                    weight <<= 1;
                    symbol = NextSymbol();
                } while (symbol <= 1);

                var value = seqToUnseq[mtf[0]];
                if (length + run > blockMax)
                    throw new InvalidDataException("bzip2 block too long");
                counts[value] += run;
                tt.AsSpan(length, run).Fill(value);
                length += run;
                continue;
            }

            if (length >= blockMax)
                throw new InvalidDataException("bzip2 block too long");
            var index = symbol - 1;
            if (index >= inUseCount)
                throw new InvalidDataException("invalid bzip2 symbol");
            var front = mtf[index];
            mtf[..index].CopyTo(mtf[1..]);
            mtf[0] = front;
            var b = seqToUnseq[front];
            counts[b]++;
            tt[length++] = b;
            symbol = NextSymbol();
        }
        if (origPtr < 0 || origPtr >= Math.Max(length, 1))
            throw new InvalidDataException("invalid bzip2 origin pointer");

        // Inverse Burrows-Wheeler transform: link every position to its successor.
        Span<int> starts = stackalloc int[256];
        var sum = 0;
        for (var i = 0; i < 256; i++)
        {
            starts[i] = sum;
            sum += counts[i];
        }
        for (var i = 0; i < length; i++)
        {
            var b = tt[i] & 0xFF;
            tt[starts[b]++] |= i << 8;
        }

        // Walk the chain and undo the initial run-length coding (4 equal bytes + a count byte).
        var crc = 0xFFFFFFFFu;
        var buffer = new byte[64 * 1024];
        var used = 0;
        var position = length == 0 ? 0 : tt[origPtr] >> 8;
        var previous = -1;
        var repeat = 0;
        for (var i = 0; i < length; i++)
        {
            var entry = tt[position];
            var b = entry & 0xFF;
            position = entry >> 8;
            if (repeat == 4)
            {
                for (var k = 0; k < b; k++)
                    Emit((byte)previous);
                repeat = 0;
                previous = -1;
                continue;
            }
            repeat = b == previous ? repeat + 1 : 1;
            previous = b;
            Emit((byte)b);
        }
        Flush();

        if (expectedCrc is { } expected && ~crc != expected)
            throw new InvalidDataException("bzip2 block checksum mismatch");

        void Emit(byte value)
        {
            crc = (crc << 8) ^ BlockCrcTable[(crc >> 24) ^ value];
            buffer[used++] = value;
            if (used == buffer.Length)
                Flush();
        }

        void Flush()
        {
            if (used == 0)
                return;
            _written += used;
            if (_written > _maxOutput)
                throw new InvalidDataException("the data unpacks to more than the archive declares");
            _output.Write(buffer, 0, used);
            used = 0;
        }

        int NextSymbol()
        {
            if (groupLeft == 0)
            {
                if (++groupIndex >= selectors.Length)
                    throw new InvalidDataException("bzip2 selectors exhausted");
                table = selectors[groupIndex];
                groupLeft = GroupSize;
            }
            groupLeft--;
            var bitsUsed = minLengths[table];
            var code = (int)ReadBits(bitsUsed);
            while (bitsUsed <= MaxCodeLength && code > limit[table, bitsUsed])
            {
                bitsUsed++;
                code = (code << 1) | (int)ReadBits(1);
            }
            if (bitsUsed > MaxCodeLength)
                throw new InvalidDataException("invalid bzip2 Huffman code");
            var slot = code - baseTable[table, bitsUsed];
            if (slot is < 0 or >= 258)
                throw new InvalidDataException("invalid bzip2 Huffman code");
            return perm[table, slot];
        }
    }

    /// <summary>Canonical Huffman decode tables as in the reference bzip2 (limit / base / perm per code length).</summary>
    /// <returns>The shortest code length.</returns>
    private static int BuildDecodeTable(int[] lengths, int alphaSize, int t, int[,] limit, int[,] baseTable, int[,] perm)
    {
        var minLength = MaxCodeLength;
        var maxLength = 0;
        for (var i = 0; i < alphaSize; i++)
        {
            minLength = Math.Min(minLength, lengths[i]);
            maxLength = Math.Max(maxLength, lengths[i]);
        }

        var p = 0;
        for (var len = minLength; len <= maxLength; len++)
            for (var symbol = 0; symbol < alphaSize; symbol++)
                if (lengths[symbol] == len)
                    perm[t, p++] = symbol;

        Span<int> countAtLength = stackalloc int[MaxCodeLength + 2];
        countAtLength.Clear();
        for (var i = 0; i < alphaSize; i++)
            countAtLength[lengths[i] + 1]++;
        for (var i = 1; i < MaxCodeLength + 2; i++)
            countAtLength[i] += countAtLength[i - 1];

        for (var i = 0; i < MaxCodeLength + 2; i++)
        {
            limit[t, i] = -1; // lengths outside min..max never match, so decoding continues to longer codes
            baseTable[t, i] = countAtLength[i];
        }

        var code = 0;
        for (var len = minLength; len <= maxLength; len++)
        {
            code += countAtLength[len + 1] - countAtLength[len];
            limit[t, len] = code - 1;
            code <<= 1;
        }
        for (var len = minLength + 1; len <= maxLength; len++)
            baseTable[t, len] = ((limit[t, len - 1] + 1) << 1) - countAtLength[len];
        return minLength;
    }

    private uint ReadBits(int count)
    {
        if (!TryReadBits(count, out var value))
            throw new InvalidDataException("bzip2 data ends unexpectedly");
        return value;
    }

    /// <summary>Reads up to 32 bits, most significant first; false when the input is exhausted.</summary>
    private bool TryReadBits(int count, out uint value)
    {
        while (_bitCount < count)
        {
            if (_inPos == _inLength)
            {
                _inLength = _input.Read(_inBuffer, 0, _inBuffer.Length);
                _inPos = 0;
                if (_inLength == 0)
                {
                    value = 0;
                    return false;
                }
            }
            _bits = (_bits << 8) | _inBuffer[_inPos++];
            _bitCount += 8;
        }
        _bitCount -= count;
        value = (uint)((_bits >> _bitCount) & ((1UL << count) - 1));
        return true;
    }

    /// <summary>bzip2's block CRC: CRC-32 with the polynomial 0x04C11DB7, most significant bit first.</summary>
    private static uint[] CreateBlockCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return table;
    }
}
