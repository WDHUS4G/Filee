// The OpenType / TrueType container (SFNT): a flavour plus a set of tables. Reads TTF / OTF (and the first font of a
// TTC collection) and writes them back with correct checksums, head.checkSumAdjustment and search fields.

using System.Buffers.Binary;
using System.Numerics;

namespace Filee.Engines.Fonts;

/// <summary>An OpenType font as a set of raw tables, independent of the container it came in.</summary>
internal sealed class SfntFont
{
    /// <summary>sfntVersion of fonts with TrueType outlines.</summary>
    public const uint TrueTypeFlavor = 0x00010000;

    /// <summary>sfntVersion 'OTTO': fonts with CFF outlines.</summary>
    public const uint CffFlavor = 0x4F54544F;

    /// <summary>sfntVersion 'true', used by old Apple TrueType fonts.</summary>
    public const uint AppleTrueTypeFlavor = 0x74727565;

    /// <summary>'ttcf': a TrueType collection header.</summary>
    public const uint CollectionTag = 0x74746366;

    /// <summary>checkSumAdjustment = this value minus the checksum of the whole file.</summary>
    private const uint ChecksumMagic = 0xB1B0AFBA;

    /// <summary>Data order recommended by the OpenType spec for TrueType and CFF fonts; other tables follow by tag.</summary>
    private static readonly string[] TrueTypeOrder =
        ["head", "hhea", "maxp", "OS/2", "hmtx", "LTSH", "VDMX", "hdmx", "cmap", "fpgm", "prep", "cvt ", "loca", "glyf", "kern", "name", "post", "gasp", "PCLT"];

    private static readonly string[] CffOrder = ["head", "hhea", "maxp", "OS/2", "name", "cmap", "post", "CFF "];

    public SfntFont(uint flavor) => Flavor = flavor;

    /// <summary>sfntVersion: <see cref="TrueTypeFlavor"/> or <see cref="CffFlavor"/>.</summary>
    public uint Flavor { get; set; }

    /// <summary>Table data by four-character tag (e.g. "head", "CFF ", "OS/2").</summary>
    public Dictionary<string, byte[]> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>Returns a table or null.</summary>
    public byte[]? this[string tag] => Tables.GetValueOrDefault(tag);

    /// <summary>True when the glyphs are CFF (PostScript) outlines.</summary>
    public bool HasCffOutlines => Tables.ContainsKey("CFF ") && !Tables.ContainsKey("glyf");

    /// <summary>Returns a table or throws a readable error when a required table is missing.</summary>
    public byte[] Require(string tag) =>
        this[tag] ?? throw new InvalidDataException($"The font has no '{tag.Trim()}' table.");

    /// <summary>Number of glyphs (maxp.numGlyphs).</summary>
    public int GlyphCount => BinaryPrimitives.ReadUInt16BigEndian(Require("maxp").AsSpan(4));

    /// <summary>Converts a tag string to its 32-bit value.</summary>
    public static uint TagValue(string tag) =>
        (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];

    /// <summary>Converts a 32-bit tag to its four-character string.</summary>
    public static string TagName(uint tag) =>
        string.Create(4, tag, static (span, t) =>
        {
            for (var i = 0; i < 4; i++)
                span[i] = (char)((t >> (24 - 8 * i)) & 0xFF);
        });

    /// <summary>The OpenType table checksum: the sum of big-endian uint32s, the last one zero-padded.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var whole = data.Length & ~3;
        for (var i = 0; i < whole; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (whole < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            last.Clear();
            data[whole..].CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }
        return sum;
    }

    /// <summary>Checksum of a table as stored in the table directory (head counts with checkSumAdjustment = 0).</summary>
    public static uint TableChecksum(string tag, byte[] data)
    {
        if (tag != "head" || data.Length < 12)
            return Checksum(data);
        var copy = (byte[])data.Clone();
        copy.AsSpan(8, 4).Clear();
        return Checksum(copy);
    }

    /// <summary>True when the data starts like a TrueType/OpenType font or collection.</summary>
    public static bool IsSfnt(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(data) is TrueTypeFlavor or CffFlavor or AppleTrueTypeFlavor or CollectionTag;

    /// <summary>Reads a TTF/OTF, or the first font of a TTC collection.</summary>
    public static SfntFont Read(byte[] data, int offset = 0)
    {
        var reader = new BigEndianReader(data, offset);
        var flavor = reader.U32();
        if (flavor == CollectionTag)
        {
            if (offset != 0)
                throw new InvalidDataException("Nested font collection.");
            reader.Skip(4); // version
            if (reader.U32() == 0)
                throw new InvalidDataException("The font collection is empty.");
            return Read(data, checked((int)reader.U32()));
        }
        if (flavor is not (TrueTypeFlavor or CffFlavor or AppleTrueTypeFlavor))
            throw new InvalidDataException("This is not a TrueType or OpenType font.");

        var font = new SfntFont(flavor == AppleTrueTypeFlavor ? TrueTypeFlavor : flavor);
        int count = reader.U16();
        reader.Skip(6); // searchRange, entrySelector, rangeShift
        for (var i = 0; i < count; i++)
        {
            var tag = TagName(reader.U32());
            reader.Skip(4); // checksum
            var tableOffset = reader.U32();
            var length = reader.U32();
            if (tableOffset > data.Length || length > data.Length - tableOffset)
                throw new InvalidDataException($"The font's '{tag.Trim()}' table lies outside the file.");
            font.Tables[tag] = data.AsSpan((int)tableOffset, (int)length).ToArray();
        }
        return font;
    }

    /// <summary>
    /// Writes the font: directory sorted by tag, table data in the order the OpenType spec recommends, every table
    /// 4-byte aligned, checksums and head.checkSumAdjustment computed.
    /// </summary>
    public byte[] Write()
    {
        var tags = Tables.Keys.Order(StringComparer.Ordinal).ToList();
        var order = Flavor == CffFlavor ? CffOrder : TrueTypeOrder;
        var dataOrder = tags
            .OrderBy(t => t == "DSIG" ? int.MaxValue : Array.IndexOf(order, t) is var i and >= 0 ? i : order.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .ToList();

        var headerSize = 12 + 16 * tags.Count;
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = headerSize;
        foreach (var tag in dataOrder)
        {
            offsets[tag] = position;
            position += (Tables[tag].Length + 3) & ~3;
        }

        var output = new byte[position];
        var writer = new BigEndianWriter(headerSize);
        WriteOffsetTable(writer, Flavor, tags.Count);
        foreach (var tag in tags)
        {
            var data = Tables[tag];
            writer.U32(TagValue(tag));
            writer.U32(TableChecksum(tag, data));
            writer.U32((uint)offsets[tag]);
            writer.U32((uint)data.Length);
            data.CopyTo(output, offsets[tag]);
        }
        writer.Written.CopyTo(output);

        if (offsets.TryGetValue("head", out var head) && Tables["head"].Length >= 12)
        {
            output.AsSpan(head + 8, 4).Clear();
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(head + 8), ChecksumMagic - Checksum(output));
        }
        return output;
    }

    /// <summary>Writes sfntVersion, numTables and the binary-search fields derived from it.</summary>
    private static void WriteOffsetTable(BigEndianWriter writer, uint flavor, int tableCount)
    {
        var entrySelector = tableCount == 0 ? 0 : BitOperations.Log2((uint)tableCount);
        var searchRange = (1 << entrySelector) * 16;
        writer.U32(flavor);
        writer.U16(tableCount);
        writer.U16(searchRange);
        writer.U16(entrySelector);
        writer.U16(tableCount * 16 - searchRange);
    }

    /// <summary>A copy with its own table dictionary (table arrays are shared until replaced).</summary>
    public SfntFont Clone()
    {
        var copy = new SfntFont(Flavor);
        foreach (var (tag, data) in Tables)
            copy.Tables[tag] = data;
        return copy;
    }
}
