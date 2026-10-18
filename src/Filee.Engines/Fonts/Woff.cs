// WOFF 1.0 (W3C Recommendation, 2012): an SFNT whose tables are zlib-compressed one by one. Lossless both ways.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Filee.Engines.Fonts;

/// <summary>Encodes and decodes WOFF 1.0 files.</summary>
internal static class Woff
{
    /// <summary>'wOFF'.</summary>
    public const uint Signature = 0x774F4646;

    private const int HeaderSize = 44;
    private const int EntrySize = 20;

    /// <summary>
    /// Wraps the font: each table zlib-compressed, or stored as-is when compressing doesn't make it smaller (as the
    /// spec requires). Extended metadata and private data are not written.
    /// </summary>
    public static byte[] Encode(SfntFont font, CancellationToken cancellationToken = default)
    {
        // Normalize through the SFNT writer so head.checkSumAdjustment matches the font a decoder rebuilds.
        var sfnt = SfntFont.Read(font.Write());
        var tags = sfnt.Tables.Keys.Order(StringComparer.Ordinal).ToList();

        var compressed = new byte[tags.Count][];
        long sfntSize = 12 + 16 * tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = sfnt.Tables[tags[i]];
            var packed = Compress(data);
            compressed[i] = packed.Length < data.Length ? packed : data;
            sfntSize += (data.Length + 3) & ~3;
        }

        var w = new BigEndianWriter(HeaderSize + EntrySize * tags.Count + compressed.Sum(c => c.Length + 3));
        w.U32(Signature);
        w.U32(sfnt.Flavor);
        w.U32(0); // length, patched below
        w.U16(tags.Count);
        w.U16(0); // reserved
        w.U32((uint)sfntSize);
        var (major, minor) = FontVersion(sfnt);
        w.U16(major);
        w.U16(minor);
        w.Zeros(20); // metaOffset, metaLength, metaOrigLength, privOffset, privLength

        var offset = HeaderSize + EntrySize * tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            var data = sfnt.Tables[tags[i]];
            w.U32(SfntFont.TagValue(tags[i]));
            w.U32((uint)offset);
            w.U32((uint)compressed[i].Length);
            w.U32((uint)data.Length);
            w.U32(SfntFont.TableChecksum(tags[i], data));
            offset += (compressed[i].Length + 3) & ~3;
        }
        foreach (var data in compressed)
        {
            w.Bytes(data);
            w.Pad4();
        }
        w.SetU32(8, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>Unpacks a WOFF file into its font. Extended metadata and private data are dropped.</summary>
    public static SfntFont Decode(byte[] data)
    {
        var r = new BigEndianReader(data);
        if (r.U32() != Signature)
            throw new InvalidDataException("This is not a WOFF file.");
        var font = new SfntFont(r.U32());
        if (font.Flavor is not (SfntFont.TrueTypeFlavor or SfntFont.CffFlavor or SfntFont.AppleTrueTypeFlavor))
            throw new InvalidDataException("The WOFF file contains an unknown kind of font.");
        if (font.Flavor == SfntFont.AppleTrueTypeFlavor)
            font.Flavor = SfntFont.TrueTypeFlavor;
        r.Skip(4); // length
        int count = r.U16();
        r.Position = HeaderSize;

        for (var i = 0; i < count; i++)
        {
            var tag = SfntFont.TagName(r.U32());
            var offset = r.U32();
            var compLength = r.U32();
            var origLength = r.U32();
            r.Skip(4); // origChecksum
            if (offset > data.Length || compLength > data.Length - offset || compLength > origLength)
                throw new InvalidDataException($"The WOFF table '{tag.Trim()}' is damaged.");
            var stored = data.AsSpan((int)offset, (int)compLength);
            font.Tables[tag] = compLength == origLength ? stored.ToArray() : Decompress(stored, (int)origLength, tag);
        }
        return font;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2 + 16);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data, int length, string tag)
    {
        var result = new byte[length];
        using var zlib = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        try
        {
            zlib.ReadExactly(result);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            throw new InvalidDataException($"The WOFF table '{tag.Trim()}' can't be decompressed.", e);
        }
        return result;
    }

    /// <summary>WOFF's informational version: head.fontRevision as major.minor (e.g. 2.015 → 2, 15).</summary>
    internal static (int Major, int Minor) FontVersion(SfntFont font)
    {
        if (font["head"] is not { Length: >= 8 } head)
            return (0, 0);
        var revision = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4)) / 65536.0;
        var major = (int)Math.Floor(revision);
        return (Math.Clamp(major, 0, 0xFFFF), Math.Clamp((int)Math.Round((revision - major) * 1000), 0, 0xFFFF));
    }
}
