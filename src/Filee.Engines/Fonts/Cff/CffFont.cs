// Parser for the Compact Font Format version 1 (Adobe Technical Note #5176), the 'CFF ' table of OpenType fonts
// with PostScript outlines: charstrings, subroutines, private dictionaries, charsets and CID font dictionaries.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Fonts.Cff;

/// <summary>Values from a Private DICT that charstrings need (widths are not: hmtx has them).</summary>
internal sealed class CffPrivate
{
    /// <summary>Local subroutines.</summary>
    public byte[][] Subrs { get; init; } = [];
}

/// <summary>A parsed CFF font (the first font of the table's FontSet).</summary>
internal sealed class CffFont
{
    // DICT operators (two-byte operators are 1200 + second byte).
    private const int OpCharset = 15;
    private const int OpCharStrings = 17;
    private const int OpPrivate = 18;
    private const int OpSubrs = 19;
    private const int OpCharstringType = 1206;
    private const int OpFontMatrix = 1207;
    private const int OpRos = 1230;
    private const int OpFdArray = 1236;
    private const int OpFdSelect = 1237;

    private static readonly double[] DefaultMatrix = [0.001, 0, 0, 0.001, 0, 0];

    private byte[] _fdSelect = [];
    private Dictionary<int, int>? _glyphBySid;

    /// <summary>Type 2 charstring of every glyph.</summary>
    public byte[][] CharStrings { get; private init; } = [];

    /// <summary>Global subroutines.</summary>
    public byte[][] GlobalSubrs { get; private init; } = [];

    /// <summary>Private DICT of each font dictionary (one for name-keyed fonts, one per FD for CID fonts).</summary>
    public CffPrivate[] Privates { get; private init; } = [];

    /// <summary>FontMatrix of the Top DICT combined with each font dictionary's own (CID fonts), per FD.</summary>
    public double[][] FontMatrices { get; private init; } = [];

    /// <summary>True for CID-keyed fonts (glyphs identified by CID, no glyph names).</summary>
    public bool IsCid { get; private init; }

    /// <summary>SID (name-keyed) or CID of each glyph; null when the charset is a predefined expert charset.</summary>
    public ushort[]? Charset { get; private init; }

    /// <summary>Strings of the String INDEX (SID 391 and up).</summary>
    public string[] Strings { get; private init; } = [];

    public int GlyphCount => CharStrings.Length;

    /// <summary>Index of the font dictionary a glyph uses.</summary>
    public int FontDictIndex(int glyph) => glyph < _fdSelect.Length ? _fdSelect[glyph] : 0;

    /// <summary>Glyph name from the charset; null for CID fonts or unknown charsets.</summary>
    public string? GlyphName(int glyph)
    {
        if (IsCid || Charset is null || glyph >= Charset.Length)
            return null;
        int sid = Charset[glyph];
        return sid < CffStandardData.StandardStrings.Length ? CffStandardData.StandardStrings[sid]
            : sid - CffStandardData.StandardStrings.Length < Strings.Length ? Strings[sid - CffStandardData.StandardStrings.Length]
            : null;
    }

    /// <summary>The glyph whose charset entry is <paramref name="sid"/>, or -1.</summary>
    public int GlyphBySid(int sid)
    {
        if (Charset is null || IsCid)
            return -1;
        _glyphBySid ??= Charset.Select((s, gid) => (s, gid)).GroupBy(p => (int)p.s).ToDictionary(g => g.Key, g => g.First().gid);
        return _glyphBySid.TryGetValue(sid, out var glyph) ? glyph : -1;
    }

    /// <summary>Parses a 'CFF ' table.</summary>
    /// <exception cref="NotSupportedException">The table is CFF2 or uses Type 1 charstrings.</exception>
    public static CffFont Parse(byte[] data)
    {
        if (data.Length < 4)
            throw new InvalidDataException("The CFF table is damaged.");
        if (data[0] != 1)
            throw new NotSupportedException($"CFF version {data[0]} outlines can't be converted.");
        var position = (int)data[2]; // hdrSize
        ReadIndex(data, ref position); // Name INDEX
        var topDicts = ReadIndex(data, ref position);
        var strings = ReadIndex(data, ref position);
        var globalSubrs = ReadIndex(data, ref position);
        if (topDicts.Length == 0)
            throw new InvalidDataException("The CFF table has no font.");

        var top = ParseDict(topDicts[0]);
        if (Number(top, OpCharstringType, 2) != 2)
            throw new NotSupportedException("The CFF font uses Type 1 charstrings, which aren't supported.");
        if (!top.TryGetValue(OpCharStrings, out var charStringsOffset))
            throw new InvalidDataException("The CFF font has no glyphs.");
        var at = Offset(charStringsOffset[0], data);
        var charStrings = ReadIndex(data, ref at);
        var topMatrix = top.TryGetValue(OpFontMatrix, out var m) && m.Length == 6 ? m : DefaultMatrix;
        var isCid = top.ContainsKey(OpRos);

        CffPrivate[] privates;
        double[][] matrices;
        var fdSelect = Array.Empty<byte>();
        if (isCid)
        {
            if (!top.TryGetValue(OpFdArray, out var fdArrayOffset))
                throw new InvalidDataException("The CID-keyed CFF font has no font dictionaries.");
            var fdAt = Offset(fdArrayOffset[0], data);
            var fontDicts = ReadIndex(data, ref fdAt).Select(ParseDict).ToArray();
            privates = [.. fontDicts.Select(fd => ReadPrivate(data, fd))];
            // A font dictionary's FontMatrix applies first, then the Top DICT's.
            matrices = [.. fontDicts.Select(fd => fd.TryGetValue(OpFontMatrix, out var fm) && fm.Length == 6 ? Multiply(fm, topMatrix) : topMatrix)];
            if (top.TryGetValue(OpFdSelect, out var fdSelectOffset))
                fdSelect = ReadFdSelect(data, Offset(fdSelectOffset[0], data), charStrings.Length, privates.Length);
        }
        else
        {
            privates = [ReadPrivate(data, top)];
            matrices = [topMatrix];
        }

        return new CffFont
        {
            CharStrings = charStrings,
            GlobalSubrs = globalSubrs,
            Privates = privates,
            FontMatrices = matrices,
            IsCid = isCid,
            Charset = ReadCharset(data, (int)Number(top, OpCharset, 0), charStrings.Length),
            Strings = [.. strings.Select(s => Encoding.Latin1.GetString(s))],
            _fdSelect = fdSelect,
        };
    }

    /// <summary>Combines two FontMatrix transforms: <paramref name="first"/> is applied, then <paramref name="then"/>.</summary>
    private static double[] Multiply(double[] first, double[] then) =>
    [
        then[0] * first[0] + then[2] * first[1],
        then[1] * first[0] + then[3] * first[1],
        then[0] * first[2] + then[2] * first[3],
        then[1] * first[2] + then[3] * first[3],
        then[0] * first[4] + then[2] * first[5] + then[4],
        then[1] * first[4] + then[3] * first[5] + then[5],
    ];

    private static CffPrivate ReadPrivate(byte[] data, Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(OpPrivate, out var entry) || entry.Length < 2)
            return new CffPrivate();
        var size = (int)entry[0];
        var offset = Offset(entry[1], data);
        if (size < 0 || size > data.Length - offset)
            throw new InvalidDataException("The CFF private dictionary lies outside the table.");
        var priv = ParseDict(data.AsSpan(offset, size).ToArray());
        var subrs = Array.Empty<byte[]>();
        if (priv.TryGetValue(OpSubrs, out var subrsOffset))
        {
            var at = Offset(offset + subrsOffset[0], data);
            subrs = ReadIndex(data, ref at);
        }
        return new CffPrivate { Subrs = subrs };
    }

    private static ushort[]? ReadCharset(byte[] data, int offset, int glyphCount)
    {
        var charset = new ushort[glyphCount];
        if (offset == 0)
        {
            // ISOAdobe: glyph i is SID i.
            for (var i = 0; i < glyphCount; i++)
                charset[i] = (ushort)i;
            return charset;
        }
        if (offset is 1 or 2)
            return null; // Expert charsets: only used by old expert fonts; names are not needed to convert them.

        var r = new BigEndianReader(data, Offset(offset, data));
        var format = r.U8();
        var glyph = 1;
        while (glyph < glyphCount)
        {
            switch (format)
            {
                case 0:
                    charset[glyph++] = r.U16();
                    break;
                case 1 or 2:
                    var first = r.U16();
                    var left = format == 1 ? r.U8() : r.U16();
                    for (var i = 0; i <= left && glyph < glyphCount; i++)
                        charset[glyph++] = (ushort)(first + i);
                    break;
                default:
                    throw new InvalidDataException($"Unknown CFF charset format {format}.");
            }
        }
        return charset;
    }

    private static byte[] ReadFdSelect(byte[] data, int offset, int glyphCount, int fdCount)
    {
        var fds = new byte[glyphCount];
        var r = new BigEndianReader(data, offset);
        var format = r.U8();
        if (format == 0)
        {
            for (var i = 0; i < glyphCount; i++)
                fds[i] = r.U8();
        }
        else if (format == 3)
        {
            int ranges = r.U16();
            int first = r.U16();
            for (var i = 0; i < ranges; i++)
            {
                var fd = r.U8();
                int next = r.U16();
                for (var g = first; g < next && g < glyphCount; g++)
                    fds[g] = fd;
                first = next;
            }
        }
        else
        {
            throw new InvalidDataException($"Unknown CFF FDSelect format {format}.");
        }
        if (fds.Any(fd => fd >= fdCount))
            throw new InvalidDataException("The CFF FDSelect refers to a missing font dictionary.");
        return fds;
    }

    /// <summary>Reads an INDEX (count, offset size, 1-based offsets, data) and advances past it.</summary>
    internal static byte[][] ReadIndex(byte[] data, ref int position)
    {
        var r = new BigEndianReader(data, position);
        int count = r.U16();
        if (count == 0)
        {
            position = r.Position;
            return [];
        }
        int offSize = r.U8();
        if (offSize is < 1 or > 4)
            throw new InvalidDataException("The CFF table is damaged (invalid INDEX).");
        var offsets = new long[count + 1];
        for (var i = 0; i <= count; i++)
        {
            long value = 0;
            for (var b = 0; b < offSize; b++)
                value = value << 8 | r.U8();
            offsets[i] = value;
        }
        var dataStart = (long)r.Position - 1; // offsets are 1-based
        var items = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            long start = dataStart + offsets[i], end = dataStart + offsets[i + 1];
            if (start < r.Position || end < start || end > data.Length)
                throw new InvalidDataException("The CFF table is damaged (INDEX data outside the table).");
            items[i] = data.AsSpan((int)start, (int)(end - start)).ToArray();
        }
        position = (int)(dataStart + offsets[count]);
        return items;
    }

    /// <summary>Parses a DICT into operator → operands.</summary>
    internal static Dictionary<int, double[]> ParseDict(byte[] data)
    {
        var dict = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var r = new BigEndianReader(data);
        while (r.Remaining > 0)
        {
            int b0 = r.U8();
            if (b0 <= 21)
            {
                var op = b0 == 12 ? 1200 + r.U8() : b0;
                dict[op] = [.. operands];
                operands.Clear();
            }
            else if (b0 == 28)
            {
                operands.Add(r.I16());
            }
            else if (b0 == 29)
            {
                operands.Add(r.I32());
            }
            else if (b0 == 30)
            {
                operands.Add(ReadReal(r));
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
            }
            else if (b0 is >= 247 and <= 250)
            {
                operands.Add((b0 - 247) * 256 + r.U8() + 108);
            }
            else if (b0 is >= 251 and <= 254)
            {
                operands.Add(-(b0 - 251) * 256 - r.U8() - 108);
            }
            else
            {
                throw new InvalidDataException("The CFF table is damaged (invalid DICT data).");
            }
        }
        return dict;
    }

    /// <summary>A real number: nibbles for digits, '.', 'E', 'E-' and '-', ended by 0xF.</summary>
    private static double ReadReal(BigEndianReader r)
    {
        var text = new StringBuilder();
        while (true)
        {
            var b = r.U8();
            foreach (var nibble in new[] { b >> 4, b & 0xF })
            {
                switch (nibble)
                {
                    case <= 9:
                        text.Append((char)('0' + nibble));
                        break;
                    case 0xA:
                        text.Append('.');
                        break;
                    case 0xB:
                        text.Append('E');
                        break;
                    case 0xC:
                        text.Append("E-");
                        break;
                    case 0xE:
                        text.Append('-');
                        break;
                    case 0xF:
                        return double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
                }
            }
        }
    }

    private static double Number(Dictionary<int, double[]> dict, int op, double fallback) =>
        dict.TryGetValue(op, out var values) && values.Length > 0 ? values[0] : fallback;

    private static int Offset(double value, byte[] data)
    {
        if (value < 0 || value >= data.Length)
            throw new InvalidDataException("The CFF table is damaged (offset outside the table).");
        return (int)value;
    }
}
