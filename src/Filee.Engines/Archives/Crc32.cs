// CRC-32 (IEEE 802.3, as in ZIP, gzip, ALZ, EGG and lzip) for checking what Filee's own archive readers unpack.

namespace Filee.Engines.Archives;

/// <summary>Incremental CRC-32 with the reflected polynomial 0xEDB88320.</summary>
internal sealed class Crc32
{
    private static readonly uint[] Table = CreateTable();
    private uint _crc = 0xFFFFFFFF;

    /// <summary>The checksum of everything passed to <see cref="Append"/> so far.</summary>
    public uint Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    /// <summary>CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
