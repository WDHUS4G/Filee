// TrueType glyph outlines (the 'glyf' and 'loca' tables): parse a glyph into points / components and write it back in
// the compact standard encoding. Used by the WOFF2 glyf transform and the CFF → TrueType conversion.

using System.Buffers.Binary;

namespace Filee.Engines.Fonts;

/// <summary>One point of a TrueType contour in font units.</summary>
internal readonly record struct GlyphPoint(int X, int Y, bool OnCurve);

/// <summary>A TrueType glyph: a simple glyph (contours of points) or a composite glyph (raw component records).</summary>
internal sealed class TrueTypeGlyph
{
    // Simple glyph point flags.
    private const byte OnCurveFlag = 0x01;
    private const byte XShortFlag = 0x02;
    private const byte YShortFlag = 0x04;
    private const byte RepeatFlag = 0x08;
    private const byte XSameFlag = 0x10;
    private const byte YSameFlag = 0x20;
    private const byte OverlapSimpleFlag = 0x40;
    private const byte CubicFlag = 0x80;

    // Composite glyph component flags.
    public const ushort ArgsAreWords = 0x0001;
    public const ushort HasScale = 0x0008;
    public const ushort MoreComponents = 0x0020;
    public const ushort HasXYScale = 0x0040;
    public const ushort HasTwoByTwo = 0x0080;
    public const ushort HasInstructions = 0x0100;

    /// <summary>Contour count; -1 for a composite glyph.</summary>
    public short NumberOfContours { get; set; }

    public short XMin { get; set; }
    public short YMin { get; set; }
    public short XMax { get; set; }
    public short YMax { get; set; }

    /// <summary>Index of the last point of each contour.</summary>
    public ushort[] EndPoints { get; set; } = [];

    /// <summary>Points in absolute font units.</summary>
    public GlyphPoint[] Points { get; set; } = [];

    /// <summary>TrueType hinting instructions.</summary>
    public byte[] Instructions { get; set; } = [];

    /// <summary>OVERLAP_SIMPLE on the first point: contours overlap (tells rasterizers to use non-zero fill carefully).</summary>
    public bool OverlapSimple { get; set; }

    /// <summary>Points use the cubic flag of the newer glyf spec, which WOFF2's transform can't carry.</summary>
    public bool HasCubicPoints { get; private set; }

    /// <summary>Component records of a composite glyph, as stored in the font (without instructions).</summary>
    public byte[] Components { get; set; } = [];

    public bool IsComposite => NumberOfContours < 0;

    /// <summary>Parses the glyph at <paramref name="offset"/>; returns null for an empty glyph (no outline).</summary>
    public static TrueTypeGlyph? Parse(byte[] glyf, int offset, int length)
    {
        if (length == 0)
            return null;
        var r = new BigEndianReader(glyf, offset, offset + length);
        var glyph = new TrueTypeGlyph
        {
            NumberOfContours = r.I16(),
            XMin = r.I16(),
            YMin = r.I16(),
            XMax = r.I16(),
            YMax = r.I16(),
        };
        if (glyph.NumberOfContours >= 0)
            glyph.ParseSimple(r);
        else if (glyph.NumberOfContours == -1)
            glyph.ParseComposite(r);
        else
            throw new InvalidDataException("Invalid TrueType glyph (negative contour count).");
        return glyph;
    }

    private void ParseSimple(BigEndianReader r)
    {
        var contours = NumberOfContours;
        EndPoints = new ushort[contours];
        for (var i = 0; i < contours; i++)
        {
            EndPoints[i] = r.U16();
            if (i > 0 && EndPoints[i] <= EndPoints[i - 1])
                throw new InvalidDataException("Invalid TrueType glyph (contour end points out of order).");
        }
        Instructions = r.Bytes(r.U16());
        var count = contours == 0 ? 0 : EndPoints[^1] + 1;

        var flags = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var flag = r.U8();
            flags[i] = flag;
            if ((flag & RepeatFlag) == 0)
                continue;
            for (int repeat = r.U8(); repeat > 0; repeat--)
            {
                if (++i >= count)
                    throw new InvalidDataException("Invalid TrueType glyph (too many point flags).");
                flags[i] = flag;
            }
        }

        var xs = ReadCoordinates(r, flags, XShortFlag, XSameFlag);
        var ys = ReadCoordinates(r, flags, YShortFlag, YSameFlag);
        Points = new GlyphPoint[count];
        for (var i = 0; i < count; i++)
        {
            Points[i] = new GlyphPoint(xs[i], ys[i], (flags[i] & OnCurveFlag) != 0);
            HasCubicPoints |= (flags[i] & CubicFlag) != 0;
        }
        OverlapSimple = count > 0 && (flags[0] & OverlapSimpleFlag) != 0;
    }

    private static int[] ReadCoordinates(BigEndianReader r, byte[] flags, byte shortFlag, byte sameFlag)
    {
        var values = new int[flags.Length];
        var value = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i];
            if ((flag & shortFlag) != 0)
                value += (flag & sameFlag) != 0 ? r.U8() : -r.U8();
            else if ((flag & sameFlag) == 0)
                value += r.I16();
            values[i] = value;
        }
        return values;
    }

    private void ParseComposite(BigEndianReader r)
    {
        var start = r.Position;
        var instructions = SkipComponents(r);
        Components = r.Data.AsSpan(start, r.Position - start).ToArray();
        if (instructions)
            Instructions = r.Bytes(r.U16());
    }

    /// <summary>Moves past the component records; returns true if any component announces instructions.</summary>
    public static bool SkipComponents(BigEndianReader r)
    {
        var instructions = false;
        ushort flags;
        do
        {
            flags = r.U16();
            r.Skip(2); // glyph index
            r.Skip((flags & ArgsAreWords) != 0 ? 4 : 2);
            if ((flags & HasScale) != 0)
                r.Skip(2);
            else if ((flags & HasXYScale) != 0)
                r.Skip(4);
            else if ((flags & HasTwoByTwo) != 0)
                r.Skip(8);
            instructions |= (flags & HasInstructions) != 0;
        }
        while ((flags & MoreComponents) != 0);
        return instructions;
    }

    /// <summary>Bounding box of the points (all zero without points).</summary>
    public (short XMin, short YMin, short XMax, short YMax) ComputeBounds()
    {
        if (Points.Length == 0)
            return (0, 0, 0, 0);
        int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
        foreach (var p in Points)
        {
            xMin = Math.Min(xMin, p.X);
            yMin = Math.Min(yMin, p.Y);
            xMax = Math.Max(xMax, p.X);
            yMax = Math.Max(yMax, p.Y);
        }
        return ((short)xMin, (short)yMin, (short)xMax, (short)yMax);
    }

    /// <summary>
    /// Writes the glyph in the standard compact form: repeated flags, one-byte deltas where they fit, "same as
    /// before" flags for zero deltas.
    /// </summary>
    public byte[] Serialize()
    {
        var w = new BigEndianWriter(16 + Points.Length * 5 + Components.Length + Instructions.Length);
        w.I16(NumberOfContours);
        w.I16(XMin);
        w.I16(YMin);
        w.I16(XMax);
        w.I16(YMax);
        if (IsComposite)
        {
            w.Bytes(Components);
            if (ComponentsHaveInstructions())
            {
                w.U16(Instructions.Length);
                w.Bytes(Instructions);
            }
            return w.ToArray();
        }

        foreach (var end in EndPoints)
            w.U16(end);
        w.U16(Instructions.Length);
        w.Bytes(Instructions);

        var flags = new BigEndianWriter(Points.Length);
        var xs = new BigEndianWriter(Points.Length * 2);
        var ys = new BigEndianWriter(Points.Length * 2);
        int lastX = 0, lastY = 0, lastFlag = -1, repeat = 0;
        for (var i = 0; i < Points.Length; i++)
        {
            var p = Points[i];
            var flag = p.OnCurve ? OnCurveFlag : 0;
            if (i == 0 && OverlapSimple)
                flag |= OverlapSimpleFlag;
            flag |= Delta(xs, p.X - lastX, XShortFlag, XSameFlag);
            flag |= Delta(ys, p.Y - lastY, YShortFlag, YSameFlag);
            lastX = p.X;
            lastY = p.Y;

            if (flag == lastFlag && repeat < 255)
            {
                // Mark the previous flag byte as repeated; its count is written when the run ends.
                if (repeat == 0)
                    flags.Written[^1] |= RepeatFlag;
                repeat++;
            }
            else
            {
                if (repeat > 0)
                    flags.U8(repeat);
                flags.U8(flag);
                repeat = 0;
            }
            lastFlag = flag;
        }
        if (repeat > 0)
            flags.U8(repeat);

        w.Bytes(flags.Written);
        w.Bytes(xs.Written);
        w.Bytes(ys.Written);
        return w.ToArray();
    }

    private static int Delta(BigEndianWriter coordinates, int delta, byte shortFlag, byte sameFlag)
    {
        if (delta == 0)
            return sameFlag;
        if (delta is > -256 and < 256)
        {
            coordinates.U8(Math.Abs(delta));
            return shortFlag | (delta > 0 ? sameFlag : 0);
        }
        coordinates.I16(delta);
        return 0;
    }

    /// <summary>True when a component record sets WE_HAVE_INSTRUCTIONS.</summary>
    public bool ComponentsHaveInstructions()
    {
        var r = new BigEndianReader(Components);
        return Components.Length > 0 && SkipComponents(r);
    }
}

/// <summary>Reads and builds the 'glyf' + 'loca' pair.</summary>
internal static class GlyfTable
{
    /// <summary>Largest glyf size a short (offset / 2) loca can address.</summary>
    public const int MaxShortLocaSize = 0xFFFF * 2;

    /// <summary>Offset and length of every glyph in glyf according to loca.</summary>
    public static (int Offset, int Length)[] ReadLoca(byte[] loca, int glyphCount, bool longFormat, int glyfLength)
    {
        var r = new BigEndianReader(loca);
        var offsets = new long[glyphCount + 1];
        for (var i = 0; i <= glyphCount; i++)
            offsets[i] = longFormat ? r.U32() : r.U16() * 2L;

        var result = new (int, int)[glyphCount];
        for (var i = 0; i < glyphCount; i++)
        {
            var start = Math.Min(offsets[i], glyfLength);
            var end = Math.Min(offsets[i + 1], glyfLength);
            result[i] = ((int)start, (int)Math.Max(0, end - start));
        }
        return result;
    }

    /// <summary>Parses every glyph of a TrueType font (null entries are empty glyphs).</summary>
    public static TrueTypeGlyph?[] ReadAll(SfntFont font)
    {
        var glyf = font.Require("glyf");
        var longLoca = FontTables.IndexToLocFormat(font.Require("head")) != 0;
        var loca = ReadLoca(font.Require("loca"), font.GlyphCount, longLoca, glyf.Length);
        return [.. loca.Select(l => TrueTypeGlyph.Parse(glyf, l.Offset, l.Length))];
    }

    /// <summary>
    /// Concatenates serialized glyphs, each padded to <paramref name="alignment"/> bytes, and builds loca. A short loca
    /// is used when asked for and when every offset fits; the returned flag says which format was written.
    /// </summary>
    public static (byte[] Glyf, byte[] Loca, bool LongFormat) Build(IReadOnlyList<byte[]> glyphs, bool longFormat, int alignment = 4)
    {
        var total = glyphs.Sum(g => (long)Align(g.Length, alignment));
        longFormat |= total > MaxShortLocaSize;
        if (total > int.MaxValue)
            throw new InvalidDataException("The glyph outlines are too large for a TrueType font.");

        var glyf = new byte[total];
        var loca = new BigEndianWriter((glyphs.Count + 1) * (longFormat ? 4 : 2));
        var offset = 0;
        foreach (var glyph in glyphs)
        {
            WriteLocaEntry(loca, offset, longFormat);
            glyph.CopyTo(glyf, offset);
            offset += Align(glyph.Length, alignment);
        }
        WriteLocaEntry(loca, offset, longFormat);
        return (glyf, loca.ToArray(), longFormat);
    }

    private static int Align(int length, int alignment) => (length + alignment - 1) / alignment * alignment;

    private static void WriteLocaEntry(BigEndianWriter loca, int offset, bool longFormat)
    {
        if (longFormat)
            loca.U32((uint)offset);
        else
            loca.U16(offset / 2);
    }
}

/// <summary>Field access for the small fixed-layout tables (head, hhea, maxp, OS/2, post).</summary>
internal static class FontTables
{
    public static int UnitsPerEm(byte[] head) => BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));

    public static int IndexToLocFormat(byte[] head) => BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50));

    /// <summary>Returns a copy of head with indexToLocFormat set.</summary>
    public static byte[] WithIndexToLocFormat(byte[] head, bool longFormat)
    {
        var copy = (byte[])head.Clone();
        BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(50), (short)(longFormat ? 1 : 0));
        return copy;
    }

    /// <summary>hhea.numberOfHMetrics (also vhea.numOfLongVerMetrics, same layout).</summary>
    public static int NumberOfMetrics(byte[] hhea) => BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
}
