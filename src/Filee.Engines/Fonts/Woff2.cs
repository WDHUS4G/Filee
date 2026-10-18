// WOFF 2.0 (W3C Recommendation): all tables in one Brotli stream, glyf/loca and hmtx transformed for better
// compression. Writes single fonts; reads single fonts and the first font of a WOFF2 collection.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Filee.Engines.Fonts;

/// <summary>Encodes and decodes WOFF 2.0 files.</summary>
internal static class Woff2
{
    /// <summary>'wOF2'.</summary>
    public const uint Signature = 0x774F4632;

    private const int HeaderSize = 48;
    private const int BrotliQuality = 11;
    private const int BrotliWindow = 22;
    private const int ArbitraryTag = 63;

    /// <summary>Tags with a 6-bit code in the table directory (section 4.1 of the spec); others are spelled out.</summary>
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ",
        "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS",
        "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc",
        "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
        "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    /// <summary>One table directory entry.</summary>
    private sealed record Entry(string Tag, int Version, uint OrigLength, uint StoredLength, bool Transformed)
    {
        public int Offset { get; set; }
    }

    /// <summary>
    /// Encodes the font. TrueType glyphs get the glyf/loca transform (and hmtx when possible), which is what makes
    /// WOFF2 smaller than WOFF; like other encoders, the DSIG signature is dropped because the rebuilt tables no
    /// longer match it, and head.flags bit 11 marks the font as losslessly transformed.
    /// </summary>
    public static byte[] Encode(SfntFont source, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var font = source.Clone();
        font.Tables.Remove("DSIG");

        TransformedGlyf? glyf = null;
        byte[]? hmtx = null;
        if (!font.HasCffOutlines && font["glyf"] is not null && font["loca"] is not null && font["head"] is not null)
            glyf = Woff2Transforms.TryTransformGlyf(font, cancellationToken);
        if (glyf is not null)
        {
            var head = FontTables.WithIndexToLocFormat(font.Require("head"), glyf.LongLoca);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(16), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16)) | 1 << 11));
            font.Tables["head"] = head;
            if (font["hmtx"] is { } metrics && font["hhea"] is { Length: >= 36 } hhea)
                hmtx = Woff2Transforms.TryTransformHmtx(metrics, font.GlyphCount, FontTables.NumberOfMetrics(hhea), glyf.XMins);
        }

        var entries = new List<Entry>();
        var stream = new BigEndianWriter();
        long sfntSize = 12 + 16 * font.Tables.Count;
        foreach (var tag in font.Tables.Keys.Order(StringComparer.Ordinal))
        {
            var data = font.Tables[tag];
            Entry entry;
            switch (tag)
            {
                case "glyf" when glyf is not null:
                    entry = new Entry(tag, 0, (uint)glyf.GlyfLength, (uint)glyf.Data.Length, true);
                    data = glyf.Data;
                    break;
                case "loca" when glyf is not null:
                    entry = new Entry(tag, 0, (uint)glyf.LocaLength, 0, true);
                    data = [];
                    break;
                case "glyf" or "loca":
                    entry = new Entry(tag, 3, (uint)data.Length, (uint)data.Length, false);
                    break;
                case "hmtx" when hmtx is not null:
                    entry = new Entry(tag, 1, (uint)data.Length, (uint)hmtx.Length, true);
                    data = hmtx;
                    break;
                default:
                    entry = new Entry(tag, 0, (uint)data.Length, (uint)data.Length, false);
                    break;
            }
            entries.Add(entry);
            stream.Bytes(data);
            sfntSize += (entry.OrigLength + 3) & ~3u;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compressed = Compress(stream.Written, cancellationToken, progress);

        var w = new BigEndianWriter(HeaderSize + entries.Count * 10 + compressed.Length + 4);
        w.U32(Signature);
        w.U32(font.Flavor);
        w.U32(0); // length, patched below
        w.U16(entries.Count);
        w.U16(0); // reserved
        w.U32((uint)Math.Min(sfntSize, uint.MaxValue));
        w.U32((uint)compressed.Length);
        var (major, minor) = Woff.FontVersion(font);
        w.U16(major);
        w.U16(minor);
        w.Zeros(20); // no extended metadata, no private data
        foreach (var entry in entries)
        {
            var index = Array.IndexOf(KnownTags, entry.Tag);
            w.U8((index >= 0 ? index : ArbitraryTag) | entry.Version << 6);
            if (index < 0)
                w.U32(SfntFont.TagValue(entry.Tag));
            w.UIntBase128(entry.OrigLength);
            if (entry.Transformed)
                w.UIntBase128(entry.StoredLength);
        }
        w.Bytes(compressed);
        w.Pad4();
        w.SetU32(8, (uint)w.Length);
        return w.ToArray();
    }

    /// <summary>Decodes a WOFF2 file (the first font of a collection) into an SFNT font.</summary>
    public static SfntFont Decode(byte[] data, CancellationToken cancellationToken = default)
    {
        var r = new BigEndianReader(data);
        if (r.U32() != Signature)
            throw new InvalidDataException("This is not a WOFF2 file.");
        var flavor = r.U32();
        r.Skip(4); // length
        int tableCount = r.U16();
        r.Skip(6); // reserved, totalSfntSize
        var compressedLength = r.U32();
        r.Position = HeaderSize;

        var entries = new List<Entry>(tableCount);
        long streamLength = 0;
        for (var i = 0; i < tableCount; i++)
        {
            var flags = r.U8();
            var tag = (flags & 0x3F) == ArbitraryTag ? SfntFont.TagName(r.U32()) : KnownTags[flags & 0x3F];
            var version = flags >> 6;
            var origLength = r.UIntBase128();
            // glyf and loca use version 3 for "not transformed"; every other table uses version 0.
            var transformed = tag is "glyf" or "loca" ? version != 3 : version != 0;
            if (transformed && !(tag is "glyf" or "loca" && version == 0) && !(tag == "hmtx" && version == 1))
                throw new InvalidDataException($"The WOFF2 table '{tag.Trim()}' uses an unknown transform.");
            var storedLength = transformed ? r.UIntBase128() : origLength;
            if (tag == "loca" && transformed && storedLength != 0)
                throw new InvalidDataException("The WOFF2 'loca' table is damaged.");
            entries.Add(new Entry(tag, version, origLength, storedLength, transformed) { Offset = (int)Math.Min(streamLength, int.MaxValue) });
            streamLength += storedLength;
        }
        if (streamLength > int.MaxValue)
            throw new InvalidDataException("The WOFF2 file is too large.");

        // A collection lists which tables belong to each font; only the first font is converted.
        var selected = entries;
        if (flavor == SfntFont.CollectionTag)
        {
            r.U32(); // collection version
            int fonts = r.Read255UInt16();
            if (fonts == 0)
                throw new InvalidDataException("The WOFF2 font collection is empty.");
            for (var member = 0; member < fonts; member++)
            {
                int count = r.Read255UInt16();
                var fontFlavor = r.U32();
                var indices = Enumerable.Range(0, count).Select(_ => (int)r.Read255UInt16()).ToList();
                if (indices.Any(i => i >= entries.Count))
                    throw new InvalidDataException("The WOFF2 font collection is damaged.");
                if (member == 0)
                {
                    flavor = fontFlavor;
                    selected = [.. indices.Select(i => entries[i])];
                }
            }
        }
        if (flavor is not (SfntFont.TrueTypeFlavor or SfntFont.CffFlavor or SfntFont.AppleTrueTypeFlavor))
            throw new InvalidDataException("The WOFF2 file contains an unknown kind of font.");

        if (compressedLength > data.Length - r.Position)
            throw new InvalidDataException("The WOFF2 file is truncated.");
        var stream = Decompress(data.AsSpan(r.Position, (int)compressedLength), (int)streamLength);
        cancellationToken.ThrowIfCancellationRequested();

        var font = new SfntFont(flavor == SfntFont.AppleTrueTypeFlavor ? SfntFont.TrueTypeFlavor : flavor);
        foreach (var entry in selected.Where(e => !e.Transformed))
            font.Tables[entry.Tag] = stream.AsSpan(entry.Offset, (int)entry.StoredLength).ToArray();

        var glyfEntry = selected.FirstOrDefault(e => e.Tag == "glyf");
        var locaEntry = selected.FirstOrDefault(e => e.Tag == "loca");
        if ((glyfEntry is null) != (locaEntry is null) || glyfEntry?.Transformed != locaEntry?.Transformed)
            throw new InvalidDataException("The WOFF2 'glyf' and 'loca' tables don't match.");

        short[]? xMins = null;
        if (glyfEntry is { Transformed: true })
        {
            var (glyf, loca, longLoca, mins) = Woff2Transforms.ReconstructGlyf(stream, glyfEntry.Offset, (int)glyfEntry.StoredLength, cancellationToken);
            font.Tables["glyf"] = glyf;
            font.Tables["loca"] = loca;
            font.Tables["head"] = FontTables.WithIndexToLocFormat(font.Require("head"), longLoca);
            xMins = mins;
        }
        if (selected.FirstOrDefault(e => e is { Tag: "hmtx", Transformed: true }) is { } hmtx)
        {
            if (xMins is null)
                throw new InvalidDataException("The WOFF2 'hmtx' table is transformed without 'glyf'.");
            font.Tables["hmtx"] = Woff2Transforms.ReconstructHmtx(stream, hmtx.Offset, (int)hmtx.StoredLength,
                font.GlyphCount, FontTables.NumberOfMetrics(font.Require("hhea")), xMins);
        }
        return font;
    }

    /// <summary>
    /// Brotli at the highest quality, like Google's encoder. That takes about 10 s per MB, so the data is fed in
    /// chunks to report progress and honour cancellation (the result is the same as compressing in one call).
    /// </summary>
    private static byte[] Compress(ReadOnlySpan<byte> data, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        const int Chunk = 256 * 1024;
        using var encoder = new BrotliEncoder(BrotliQuality, BrotliWindow);
        using var output = new MemoryStream(data.Length / 2 + 64);
        var buffer = new byte[Chunk];
        var remaining = data;
        while (true)
        {
            var take = Math.Min(Chunk, remaining.Length);
            var final = take == remaining.Length;
            var input = remaining[..take];
            OperationStatus status;
            do
            {
                status = encoder.Compress(input, buffer, out var consumed, out var written, final);
                if (status == OperationStatus.InvalidData)
                    throw new InvalidOperationException("Brotli compression failed.");
                output.Write(buffer, 0, written);
                input = input[consumed..];
            }
            while (status == OperationStatus.DestinationTooSmall || (final && status != OperationStatus.Done));
            remaining = remaining[take..];
            if (final)
                return output.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1 - (double)remaining.Length / data.Length);
        }
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data, int length)
    {
        var result = new byte[length];
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(data, result, out _, out var written);
        if (status != OperationStatus.Done || written != length)
            throw new InvalidDataException("The WOFF2 font data can't be decompressed (the file is damaged).");
        return result;
    }
}
