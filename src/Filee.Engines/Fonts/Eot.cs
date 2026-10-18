// Embedded OpenType (EOT, W3C Member Submission 2008), the web font format of Internet Explorer 6–8: a little-endian
// header with names and OS/2 fields in front of the font. Writes version 0x00020001 without MicroType Express
// compression or XOR obfuscation; reads uncompressed files of every version.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Fonts;

/// <summary>Encodes and decodes EOT files.</summary>
internal static class Eot
{
    /// <summary>MagicNumber at offset 34 (little-endian 0x504C).</summary>
    private const ushort Magic = 0x504C;

    private const uint Version = 0x00020001;

    /// <summary>TTEMBED_TTCOMPRESSED: the font data is MicroType Express compressed.</summary>
    private const uint CompressedFlag = 0x4;

    /// <summary>TTEMBED_XORENCRYPTDATA: every font byte is XORed with 0x50.</summary>
    private const uint XorFlag = 0x10000000;

    private const byte DefaultCharset = 1;

    /// <summary>True when the data starts like an EOT header.</summary>
    public static bool IsEot(ReadOnlySpan<byte> data) =>
        data.Length >= 82 && BinaryPrimitives.ReadUInt16LittleEndian(data[34..]) == Magic &&
        BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) is 0x00010000 or 0x00020001 or 0x00020002;

    /// <summary>Wraps a TrueType font in an EOT header (the caller converts CFF outlines first).</summary>
    public static byte[] Encode(SfntFont font)
    {
        var sfnt = font.Write();
        var written = SfntFont.Read(sfnt);
        var os2 = written["OS/2"] ?? [];
        var head = written.Require("head");
        var names = NameTable.Read(written["name"]);

        using var output = new MemoryStream(sfnt.Length + 512);
        using (var w = new BinaryWriter(output, Encoding.Unicode, leaveOpen: true)) // BinaryWriter is little-endian
        {
            w.Write(0u); // EOTSize, patched below
            w.Write((uint)sfnt.Length);
            w.Write(Version);
            w.Write(0u); // flags: not compressed, not obfuscated, not a subset
            w.Write(Slice(os2, 32, 10)); // PANOSE
            w.Write(DefaultCharset);
            w.Write((byte)(U16(os2, 62) & 1)); // italic: fsSelection bit 0
            w.Write((uint)U16(os2, 4)); // usWeightClass
            w.Write(U16(os2, 8)); // fsType (embedding permissions)
            w.Write(Magic);
            for (var i = 0; i < 4; i++)
                w.Write(U32(os2, 42 + 4 * i)); // ulUnicodeRange1–4
            for (var i = 0; i < 2; i++)
                w.Write(U32(os2, 78 + 4 * i)); // ulCodePageRange1–2 (OS/2 version 1 and later)
            w.Write(U32(head, 8)); // checkSumAdjustment
            for (var i = 0; i < 4; i++)
                w.Write(0u); // reserved
            w.Write((ushort)0); // padding1
            WriteName(w, names.Get(1)); // family
            w.Write((ushort)0);
            WriteName(w, names.Get(2)); // style
            w.Write((ushort)0);
            WriteName(w, names.Get(5)); // version
            w.Write((ushort)0);
            WriteName(w, names.Get(4)); // full name
            w.Write((ushort)0); // padding5
            w.Write((ushort)0); // RootStringSize: no URL restriction
            w.Write(sfnt);
        }
        var eot = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(eot, (uint)eot.Length);
        return eot;
    }

    /// <summary>Extracts the font from an EOT file.</summary>
    /// <exception cref="NotSupportedException">The font data is MicroType Express compressed.</exception>
    public static SfntFont Decode(byte[] data)
    {
        if (!IsEot(data))
            throw new InvalidDataException("This is not an EOT file.");
        var eotSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var fontSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        if ((flags & CompressedFlag) != 0)
            throw new NotSupportedException(
                "This EOT file is compressed with MicroType Express, which Filee can't unpack. Only uncompressed EOT files can be converted.");
        if (eotSize > data.Length || eotSize < 82 || fontSize > eotSize - 82)
            throw new InvalidDataException("The EOT file is truncated or damaged.");

        // The font data is the last field of every EOT version, so its offset doesn't depend on the header version.
        var font = data.AsSpan((int)(eotSize - fontSize), (int)fontSize).ToArray();
        if ((flags & XorFlag) != 0)
        {
            for (var i = 0; i < font.Length; i++)
                font[i] ^= 0x50;
        }
        return SfntFont.Read(font);
    }

    private static void WriteName(BinaryWriter w, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value); // UTF-16LE
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static byte[] Slice(byte[] table, int offset, int length) =>
        table.Length >= offset + length ? table[offset..(offset + length)] : new byte[length];

    private static ushort U16(byte[] table, int offset) =>
        table.Length >= offset + 2 ? BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(offset)) : (ushort)0;

    private static uint U32(byte[] table, int offset) =>
        table.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset)) : 0;
}

/// <summary>Reads strings from the 'name' table.</summary>
internal sealed class NameTable
{
    private readonly List<(int Platform, int Encoding, int Language, int NameId, string Value)> _records = [];

    public static NameTable Read(byte[]? name)
    {
        var table = new NameTable();
        if (name is null || name.Length < 6)
            return table;
        try
        {
            var r = new BigEndianReader(name);
            r.U16(); // format
            int count = r.U16();
            int stringOffset = r.U16();
            for (var i = 0; i < count; i++)
            {
                int platform = r.U16(), encoding = r.U16(), language = r.U16(), nameId = r.U16(), length = r.U16(), offset = r.U16();
                if (stringOffset + offset + length > name.Length)
                    continue;
                var bytes = name.AsSpan(stringOffset + offset, length);
                string? value = platform switch
                {
                    0 or 3 => Encoding.BigEndianUnicode.GetString(bytes),
                    1 when encoding == 0 => Encoding.Latin1.GetString(bytes), // Mac Roman; ASCII names read the same
                    _ => null,
                };
                if (value is not null)
                    table._records.Add((platform, encoding, language, nameId, value));
            }
        }
        catch (InvalidDataException)
        {
            // A damaged name table only costs the names.
        }
        return table;
    }

    /// <summary>Returns a name, preferring Windows English (US), then any Windows or Unicode entry, then Macintosh.</summary>
    public string Get(int nameId)
    {
        var candidates = _records.Where(r => r.NameId == nameId).ToList();
        return candidates.FirstOrDefault(r => r is { Platform: 3, Language: 0x409 }).Value
               ?? candidates.FirstOrDefault(r => r.Platform == 3).Value
               ?? candidates.FirstOrDefault(r => r.Platform == 0).Value
               ?? candidates.FirstOrDefault().Value
               ?? "";
    }
}
