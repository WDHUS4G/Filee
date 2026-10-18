// Writes HEIC files (ISO/IEC 23008-12, HEIF with HEVC pictures): a primary picture that is either one coded picture
// or, like an iPhone photo, a grid of 512 × 512 tiles, with its colour profile and Exif. FFmpeg only encodes the
// HEVC pictures; it has no HEIF muxer.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Heic;

/// <summary>One picture to store as HEIC.</summary>
/// <param name="Width">Picture width; the tiles may extend beyond it (the grid crops them).</param>
/// <param name="Height">Picture height.</param>
/// <param name="Stream">Coded tiles, row by row (one picture when <paramref name="Columns"/> × <paramref name="Rows"/> is 1).</param>
/// <param name="TileWidth">Width of every coded tile.</param>
/// <param name="TileHeight">Height of every coded tile.</param>
/// <param name="IccProfile">ICC profile, or null to declare sRGB.</param>
/// <param name="Exif">Exif data starting with a TIFF header (an "Exif\0\0" prefix is skipped), or null.</param>
internal sealed record HeifImage(int Width, int Height, HevcStream Stream, int TileWidth, int TileHeight, int Columns, int Rows,
    byte[]? IccProfile = null, byte[]? Exif = null)
{
    /// <summary>True when the picture is a grid item rather than the coded picture itself.</summary>
    public bool IsGrid => Columns * Rows > 1 || TileWidth != Width || TileHeight != Height;
}

/// <summary>HEIF container writing.</summary>
internal static class HeifFile
{
    private const int Primary = 1;

    public static byte[] Write(HeifImage image)
    {
        var tiles = image.Stream.Pictures;
        if (tiles.Count != image.Columns * image.Rows)
            throw new InvalidDataException($"{tiles.Count} coded pictures for {image.Columns} × {image.Rows} tiles.");
        if (image.Columns > 256 || image.Rows > 256)
            throw new InvalidDataException("A HEIF grid holds at most 256 × 256 tiles.");

        // Item ids: 1 is the picture (the grid, or the only coded picture), then the tiles, then Exif.
        var grid = image.IsGrid;
        var tileIds = grid ? Enumerable.Range(Primary + 1, tiles.Count).ToList() : [Primary];
        var exif = ExifPayload(image.Exif);
        var exifId = exif is null ? 0 : tileIds[^1] + 1;

        var items = new List<(int Id, byte[] Data)>();
        if (grid)
            items.Add((Primary, GridDescription(image)));
        items.AddRange(tileIds.Select((id, i) => (id, tiles[i])));
        if (exif is not null)
            items.Add((exifId, exif));

        var ftyp = Box("ftyp", w =>
        {
            w.Text("heic");
            w.U32(0);
            w.Text("mif1");
            w.Text("heic");
        });
        // The item locations hold absolute offsets, so the header is built twice: its size does not depend on them.
        var header = Meta(image, items, tileIds, exifId, new Dictionary<int, long>());
        var offsets = new Dictionary<int, long>();
        long at = ftyp.Length + header.Length + 8; // + mdat box header
        foreach (var (id, data) in items)
        {
            offsets[id] = at;
            at += data.Length;
        }
        if (at > uint.MaxValue)
            throw new InvalidDataException("The picture is too large for a HEIC file.");
        header = Meta(image, items, tileIds, exifId, offsets);

        using var file = new MemoryStream();
        file.Write(ftyp);
        file.Write(header);
        var mdatSize = 8 + items.Sum(i => (long)i.Data.Length);
        Span<byte> mdatHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, (uint)mdatSize);
        Encoding.ASCII.GetBytes("mdat", mdatHeader[4..]);
        file.Write(mdatHeader);
        foreach (var (_, data) in items)
            file.Write(data);
        return file.ToArray();
    }

    private static byte[] Meta(HeifImage image, List<(int Id, byte[] Data)> items, List<int> tileIds, int exifId, Dictionary<int, long> offsets) =>
        FullBox("meta", 0, 0, w =>
        {
            w.Raw(FullBox("hdlr", 0, 0, h =>
            {
                h.U32(0); // pre_defined
                h.Text("pict");
                h.Zeros(12);
                h.Raw([0]); // empty name
            }));
            // Data reference: everything is in this file.
            w.Raw(Box("dinf", d => d.Raw(FullBox("dref", 0, 0, r =>
            {
                r.U32(1);
                r.Raw(FullBox("url ", 0, 1, _ => { }));
            }))));
            w.Raw(FullBox("pitm", 0, 0, p => p.U16(Primary)));
            w.Raw(FullBox("iloc", 0, 0, l =>
            {
                l.Raw([0x44, 0x00]); // offset_size 4, length_size 4, base_offset_size 0
                l.U16(items.Count);
                foreach (var (id, data) in items)
                {
                    l.U16(id);
                    l.U16(0); // data_reference_index: this file
                    l.U16(1); // one extent
                    l.U32((uint)offsets.GetValueOrDefault(id));
                    l.U32((uint)data.Length);
                }
            }));
            w.Raw(FullBox("iinf", 0, 0, l =>
            {
                l.U16(items.Count);
                foreach (var (id, _) in items)
                {
                    var type = id == exifId ? "Exif" : id == Primary && image.IsGrid ? "grid" : "hvc1";
                    // Tiles are hidden: viewers show the grid, not its pieces.
                    var hidden = image.IsGrid && id != Primary && id != exifId;
                    l.Raw(FullBox("infe", 2, hidden ? 1 : 0, e =>
                    {
                        e.U16(id);
                        e.U16(0); // not protected
                        e.Text(type);
                        e.Raw([0]); // empty name
                    }));
                }
            }));
            if (image.IsGrid || exifId != 0)
            {
                w.Raw(FullBox("iref", 0, 0, r =>
                {
                    if (image.IsGrid)
                        r.Raw(Box("dimg", d => References(d, Primary, tileIds)));
                    if (exifId != 0)
                        r.Raw(Box("cdsc", d => References(d, exifId, [Primary])));
                }));
            }
            w.Raw(Properties(image, tileIds));
        });

    private static void References(BoxWriter w, int from, List<int> to)
    {
        w.U16(from);
        w.U16(to.Count);
        foreach (var id in to)
            w.U16(id);
    }

    /// <summary>Item properties (ipco) and which item has which (ipma, 1-based indexes; hvcC is essential).</summary>
    private static byte[] Properties(HeifImage image, List<int> tileIds)
    {
        var depth = image.Stream.BitDepth;
        var properties = new List<byte[]>
        {
            Box("hvcC", w => w.Raw(image.Stream.Configuration)),
            FullBox("ispe", 0, 0, w => { w.U32((uint)image.TileWidth); w.U32((uint)image.TileHeight); }),
            FullBox("pixi", 0, 0, w => w.Raw([3, (byte)depth, (byte)depth, (byte)depth])),
            image.IccProfile is { } icc
                ? Box("colr", w => { w.Text("prof"); w.Raw(icc); })
                // sRGB (BT.709 primaries, sRGB transfer) stored as BT.601 full-range YCbCr, like JPEG.
                : Box("colr", w => { w.Text("nclx"); w.U16(1); w.U16(13); w.U16(6); w.Raw([0x80]); }),
        };
        const int hvcC = 1, tileSize = 2, pixi = 3, colr = 4;
        var associations = new List<(int Item, (int Index, bool Essential)[] Properties)>();
        if (image.IsGrid)
        {
            properties.Add(FullBox("ispe", 0, 0, w => { w.U32((uint)image.Width); w.U32((uint)image.Height); }));
            associations.Add((Primary, [(properties.Count, false), (pixi, false), (colr, false)]));
            associations.AddRange(tileIds.Select(id => (id, new[] { (hvcC, true), (tileSize, false) })));
        }
        else
        {
            associations.Add((Primary, [(hvcC, true), (tileSize, false), (pixi, false), (colr, false)]));
        }

        return Box("iprp", w =>
        {
            w.Raw(Box("ipco", c =>
            {
                foreach (var property in properties)
                    c.Raw(property);
            }));
            w.Raw(FullBox("ipma", 0, 0, m =>
            {
                m.U32((uint)associations.Count);
                foreach (var (item, list) in associations)
                {
                    m.U16(item);
                    m.Raw([(byte)list.Length]);
                    foreach (var (index, essential) in list)
                        m.Raw([(byte)((essential ? 0x80 : 0) | index)]);
                }
            }));
        });
    }

    /// <summary>ImageGrid (23008-12, 6.6.2.3): rows, columns and the size the tiles are cropped to.</summary>
    private static byte[] GridDescription(HeifImage image)
    {
        var wide = image.Width > ushort.MaxValue || image.Height > ushort.MaxValue;
        var w = new BoxWriter();
        w.Raw([0, (byte)(wide ? 1 : 0), (byte)(image.Rows - 1), (byte)(image.Columns - 1)]);
        if (wide)
        {
            w.U32((uint)image.Width);
            w.U32((uint)image.Height);
        }
        else
        {
            w.U16(image.Width);
            w.U16(image.Height);
        }
        return w.ToArray();
    }

    /// <summary>The Exif item: the offset of the TIFF header ("II*\0" or "MM\0*") in the data, then the data.</summary>
    internal static byte[]? ExifPayload(byte[]? exif)
    {
        if (exif is null)
            return null;
        var tiff = -1;
        for (var i = 0; i + 3 < exif.Length && i < 16 && tiff < 0; i++)
        {
            if ((exif[i] == 'I' && exif[i + 1] == 'I' && exif[i + 2] == 42 && exif[i + 3] == 0) ||
                (exif[i] == 'M' && exif[i + 1] == 'M' && exif[i + 2] == 0 && exif[i + 3] == 42))
                tiff = i;
        }
        if (tiff < 0)
            return null;
        var payload = new byte[4 + exif.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)tiff);
        exif.CopyTo(payload, 4);
        return payload;
    }

    private static byte[] Box(string type, Action<BoxWriter> body)
    {
        var content = new BoxWriter();
        body(content);
        var payload = content.ToArray();
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] FullBox(string type, int version, int flags, Action<BoxWriter> body) =>
        Box(type, w =>
        {
            w.U32((uint)((version << 24) | flags));
            body(w);
        });

    /// <summary>Big-endian field writer.</summary>
    private sealed class BoxWriter
    {
        private readonly MemoryStream _stream = new();

        public void Raw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void Text(string fourCc) => _stream.Write(Encoding.ASCII.GetBytes(fourCc));

        public void Zeros(int count) => _stream.Write(new byte[count]);

        public void U16(int value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
            _stream.Write(bytes);
        }

        public void U32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
