// The x86 branch converter ("BCJ") of the LZMA SDK (public domain, Igor Pavlov): the relative targets of CALL and JMP
// instructions (E8/E9) become absolute before compressing, so the many calls to the same function repeat and compress
// far better. Applied to each .exe and .dll of the payload on its own (x64 code uses the same opcodes); undoing it
// gives back the exact bytes, which the file's SHA-256 checks anyway.

namespace Filee.Setup.Payload;

public static class X86Filter
{
    public static void Encode(Span<byte> data) => Convert(data, encoding: true);

    public static void Decode(Span<byte> data) => Convert(data, encoding: false);

    private static bool IsMsByte(byte b) => ((b + 1) & 0xFE) == 0;

    private static void Convert(Span<byte> data, bool encoding)
    {
        if (data.Length < 5)
            return;
        const uint ip = 5;
        var limit = data.Length - 4;
        uint mask = 0;
        var pos = 0;
        while (true)
        {
            var p = pos;
            while (p < limit && (data[p] & 0xFE) != 0xE8)
                p++;
            var distance = p - pos;
            pos = p;
            if (p >= limit)
                return;
            if (distance > 2)
            {
                mask = 0;
            }
            else
            {
                mask >>= distance;
                if (mask != 0 && (mask > 4 || mask == 3 || IsMsByte(data[p + (int)(mask >> 1) + 1])))
                {
                    mask = (mask >> 1) | 4;
                    pos++;
                    continue;
                }
            }
            if (IsMsByte(data[p + 4]))
            {
                var v = ((uint)data[p + 4] << 24) | ((uint)data[p + 3] << 16) | ((uint)data[p + 2] << 8) | data[p + 1];
                var current = ip + (uint)pos;
                pos += 5;
                v = encoding ? v + current : v - current;
                if (mask != 0)
                {
                    var shift = (int)(mask & 6) << 2;
                    if (IsMsByte((byte)(v >> shift)))
                    {
                        v ^= (0x100u << shift) - 1;
                        v = encoding ? v + current : v - current;
                    }
                    mask = 0;
                }
                data[p + 1] = (byte)v;
                data[p + 2] = (byte)(v >> 8);
                data[p + 3] = (byte)(v >> 16);
                data[p + 4] = (byte)(0 - ((v >> 24) & 1));
            }
            else
            {
                mask = (mask >> 1) | 4;
                pos++;
            }
        }
    }
}
