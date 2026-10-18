// The WOFF2 table transforms (W3C WOFF 2.0, section 5): glyf/loca split into streams with a compact triplet encoding
// for points (version 0), and hmtx without the left side bearings that equal the glyph's xMin (version 1).

namespace Filee.Engines.Fonts;

/// <summary>Result of transforming glyf/loca: the transformed data plus what the decoder will rebuild.</summary>
/// <param name="Data">The transformed glyf table.</param>
/// <param name="GlyfLength">Length of the glyf table a decoder reconstructs.</param>
/// <param name="LocaLength">Length of the loca table a decoder reconstructs.</param>
/// <param name="LongLoca">Loca format of the reconstructed font (head.indexToLocFormat must match).</param>
/// <param name="XMins">xMin of every glyph (0 for empty glyphs), needed by the hmtx transform.</param>
internal sealed record TransformedGlyf(byte[] Data, int GlyfLength, int LocaLength, bool LongLoca, short[] XMins);

/// <summary>Forward and reverse WOFF2 transforms of glyf/loca and hmtx.</summary>
internal static class Woff2Transforms
{
    private const int OverlapOption = 1;

    /// <summary>
    /// Transforms glyf/loca. Returns null when the glyphs can't be carried losslessly (damaged data, cubic glyf
    /// extension); the caller then stores glyf and loca untransformed.
    /// </summary>
    public static TransformedGlyf? TryTransformGlyf(SfntFont font, CancellationToken cancellationToken)
    {
        TrueTypeGlyph?[] glyphs;
        try
        {
            glyphs = GlyfTable.ReadAll(font);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (glyphs.Any(g => g is { HasCubicPoints: true }))
            return null;

        var count = glyphs.Length;
        var nContours = new BigEndianWriter(count * 2);
        var nPoints = new BigEndianWriter(count * 4);
        var flags = new BigEndianWriter(count * 32);
        var glyphStream = new BigEndianWriter(count * 64);
        var composites = new BigEndianWriter();
        var bboxBitmap = new byte[4 * ((count + 31) / 32)];
        var bboxes = new BigEndianWriter();
        var instructions = new BigEndianWriter();
        var overlap = new byte[(count + 7) / 8];
        var hasOverlap = false;
        var xMins = new short[count];
        long glyfLength = 0;

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var glyph = glyphs[i];
            if (glyph is null || glyph.NumberOfContours == 0)
            {
                nContours.I16(0);
                continue;
            }
            xMins[i] = glyph.XMin;
            glyfLength += (glyph.Serialize().Length + 3) & ~3;

            if (glyph.IsComposite)
            {
                nContours.I16(-1);
                composites.Bytes(glyph.Components);
                if (glyph.ComponentsHaveInstructions())
                {
                    glyphStream.Write255UInt16(glyph.Instructions.Length);
                    instructions.Bytes(glyph.Instructions);
                }
                WriteBbox(glyph, i, bboxBitmap, bboxes); // composites always carry their box
                continue;
            }

            nContours.I16(glyph.NumberOfContours);
            var previous = -1;
            foreach (var end in glyph.EndPoints)
            {
                nPoints.Write255UInt16(end - previous);
                previous = end;
            }
            int lastX = 0, lastY = 0;
            foreach (var p in glyph.Points)
            {
                WriteTriplet(flags, glyphStream, p.OnCurve, p.X - lastX, p.Y - lastY);
                lastX = p.X;
                lastY = p.Y;
            }
            glyphStream.Write255UInt16(glyph.Instructions.Length);
            instructions.Bytes(glyph.Instructions);
            if (glyph.ComputeBounds() != (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax))
                WriteBbox(glyph, i, bboxBitmap, bboxes);
            if (glyph.OverlapSimple)
            {
                overlap[i >> 3] |= (byte)(0x80 >> (i & 7));
                hasOverlap = true;
            }
        }

        var longLoca = FontTables.IndexToLocFormat(font.Require("head")) != 0 || glyfLength > GlyfTable.MaxShortLocaSize;
        var w = new BigEndianWriter();
        w.U16(0); // reserved
        w.U16(hasOverlap ? OverlapOption : 0);
        w.U16(count);
        w.U16(longLoca ? 1 : 0);
        w.U32((uint)nContours.Length);
        w.U32((uint)nPoints.Length);
        w.U32((uint)flags.Length);
        w.U32((uint)glyphStream.Length);
        w.U32((uint)composites.Length);
        w.U32((uint)(bboxBitmap.Length + bboxes.Length));
        w.U32((uint)instructions.Length);
        w.Bytes(nContours.Written);
        w.Bytes(nPoints.Written);
        w.Bytes(flags.Written);
        w.Bytes(glyphStream.Written);
        w.Bytes(composites.Written);
        w.Bytes(bboxBitmap);
        w.Bytes(bboxes.Written);
        w.Bytes(instructions.Written);
        if (hasOverlap)
            w.Bytes(overlap);
        return new TransformedGlyf(w.ToArray(), (int)glyfLength, (count + 1) * (longLoca ? 4 : 2), longLoca, xMins);
    }

    private static void WriteBbox(TrueTypeGlyph glyph, int index, byte[] bitmap, BigEndianWriter bboxes)
    {
        bitmap[index >> 3] |= (byte)(0x80 >> (index & 7));
        bboxes.I16(glyph.XMin);
        bboxes.I16(glyph.YMin);
        bboxes.I16(glyph.XMax);
        bboxes.I16(glyph.YMax);
    }

    /// <summary>
    /// Encodes one point delta as a flag byte (on-curve bit + one of 128 shapes) and 1–4 data bytes, choosing the
    /// smallest shape that fits (the table in section 5.2 of the spec).
    /// </summary>
    private static void WriteTriplet(BigEndianWriter flags, BigEndianWriter data, bool onCurve, int dx, int dy)
    {
        var absX = Math.Abs(dx);
        var absY = Math.Abs(dy);
        var onCurveBit = onCurve ? 0 : 128;
        var xSign = dx < 0 ? 0 : 1;
        var ySign = dy < 0 ? 0 : 1;
        var signs = xSign + 2 * ySign;

        if (dx == 0 && absY < 1280)
        {
            flags.U8(onCurveBit + ((absY & 0xF00) >> 7) + ySign);
            data.U8(absY & 0xFF);
        }
        else if (dy == 0 && absX < 1280)
        {
            flags.U8(onCurveBit + 10 + ((absX & 0xF00) >> 7) + xSign);
            data.U8(absX & 0xFF);
        }
        else if (absX < 65 && absY < 65)
        {
            flags.U8(onCurveBit + 20 + ((absX - 1) & 0x30) + (((absY - 1) & 0x30) >> 2) + signs);
            data.U8((((absX - 1) & 0xF) << 4) | ((absY - 1) & 0xF));
        }
        else if (absX < 769 && absY < 769)
        {
            flags.U8(onCurveBit + 84 + 12 * (((absX - 1) & 0x300) >> 8) + (((absY - 1) & 0x300) >> 6) + signs);
            data.U8((absX - 1) & 0xFF);
            data.U8((absY - 1) & 0xFF);
        }
        else if (absX < 4096 && absY < 4096)
        {
            flags.U8(onCurveBit + 120 + signs);
            data.U8(absX >> 4);
            data.U8(((absX & 0xF) << 4) | (absY >> 8));
            data.U8(absY & 0xFF);
        }
        else
        {
            flags.U8(onCurveBit + 124 + signs);
            data.U16(absX);
            data.U16(absY);
        }
    }

    /// <summary>Rebuilds glyf and loca from a transformed glyf table (section 5.1 of the spec).</summary>
    public static (byte[] Glyf, byte[] Loca, bool LongLoca, short[] XMins) ReconstructGlyf(
        byte[] data, int offset, int length, CancellationToken cancellationToken)
    {
        var end = offset + length;
        var header = new BigEndianReader(data, offset, end);
        header.U16(); // reserved
        var options = header.U16();
        int count = header.U16();
        var longLoca = header.U16() != 0;
        var sizes = new uint[7];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = header.U32();

        var position = (long)header.Position;
        BigEndianReader NextStream(long size)
        {
            if (size > end - position)
                throw new InvalidDataException("The WOFF2 glyph data is damaged.");
            var stream = new BigEndianReader(data, (int)position, (int)(position + size));
            position += size;
            return stream;
        }

        var nContours = NextStream(sizes[0]);
        var nPoints = NextStream(sizes[1]);
        var flagStream = NextStream(sizes[2]);
        var glyphStream = NextStream(sizes[3]);
        var composites = NextStream(sizes[4]);
        var bboxStream = NextStream(sizes[5]);
        var instructions = NextStream(sizes[6]);
        var bboxBitmap = bboxStream.Span(4 * ((count + 31) / 32)).ToArray();
        var overlap = (options & OverlapOption) != 0 ? NextStream((count + 7) / 8).Span((count + 7) / 8).ToArray() : null;

        var glyphs = new byte[count][];
        var xMins = new short[count];
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int contours = nContours.I16();
            var hasBbox = (bboxBitmap[i >> 3] & (0x80 >> (i & 7))) != 0;
            TrueTypeGlyph glyph;
            if (contours == 0)
            {
                if (hasBbox)
                    throw new InvalidDataException("The WOFF2 glyph data is damaged (bounding box on an empty glyph).");
                glyphs[i] = [];
                continue;
            }
            if (contours == -1)
            {
                if (!hasBbox)
                    throw new InvalidDataException("The WOFF2 glyph data is damaged (composite glyph without bounding box).");
                var start = composites.Position;
                var hasInstructions = TrueTypeGlyph.SkipComponents(composites);
                glyph = new TrueTypeGlyph
                {
                    NumberOfContours = -1,
                    Components = data.AsSpan(start, composites.Position - start).ToArray(),
                };
                if (hasInstructions)
                    glyph.Instructions = instructions.Bytes(glyphStream.Read255UInt16());
            }
            else if (contours > 0)
            {
                glyph = new TrueTypeGlyph { NumberOfContours = (short)contours, EndPoints = new ushort[contours] };
                var total = 0;
                for (var c = 0; c < contours; c++)
                {
                    total += nPoints.Read255UInt16();
                    if (total > 0xFFFF)
                        throw new InvalidDataException("The WOFF2 glyph data is damaged (too many points).");
                    glyph.EndPoints[c] = (ushort)(total - 1);
                }
                glyph.Points = ReadPoints(flagStream, glyphStream, total);
                glyph.Instructions = instructions.Bytes(glyphStream.Read255UInt16());
                glyph.OverlapSimple = overlap is not null && (overlap[i >> 3] & (0x80 >> (i & 7))) != 0;
            }
            else
            {
                throw new InvalidDataException("The WOFF2 glyph data is damaged (invalid contour count).");
            }

            if (hasBbox)
            {
                glyph.XMin = bboxStream.I16();
                glyph.YMin = bboxStream.I16();
                glyph.XMax = bboxStream.I16();
                glyph.YMax = bboxStream.I16();
            }
            else
            {
                (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax) = glyph.ComputeBounds();
            }
            xMins[i] = glyph.XMin;
            glyphs[i] = glyph.Serialize();
        }

        var (glyf, loca, actualLong) = GlyfTable.Build(glyphs, longLoca);
        return (glyf, loca, actualLong, xMins);
    }

    private static GlyphPoint[] ReadPoints(BigEndianReader flags, BigEndianReader data, int count)
    {
        var points = new GlyphPoint[count];
        int x = 0, y = 0;
        for (var i = 0; i < count; i++)
        {
            int flag = flags.U8();
            var onCurve = (flag & 0x80) == 0;
            flag &= 0x7F;
            int dx, dy;
            if (flag < 10)
            {
                dx = 0;
                dy = WithSign(flag, ((flag & 14) << 7) + data.U8());
            }
            else if (flag < 20)
            {
                dx = WithSign(flag, (((flag - 10) & 14) << 7) + data.U8());
                dy = 0;
            }
            else if (flag < 84)
            {
                var b0 = flag - 20;
                int b1 = data.U8();
                dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
                dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            }
            else if (flag < 120)
            {
                var b0 = flag - 84;
                int b1 = data.U8();
                int b2 = data.U8();
                dx = WithSign(flag, 1 + ((b0 / 12) << 8) + b1);
                dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2);
            }
            else if (flag < 124)
            {
                int b1 = data.U8();
                int b2 = data.U8();
                int b3 = data.U8();
                dx = WithSign(flag, (b1 << 4) + (b2 >> 4));
                dy = WithSign(flag >> 1, ((b2 & 0x0F) << 8) + b3);
            }
            else
            {
                dx = WithSign(flag, data.U16());
                dy = WithSign(flag >> 1, data.U16());
            }
            x += dx;
            y += dy;
            points[i] = new GlyphPoint(x, y, onCurve);
        }
        return points;
    }

    private static int WithSign(int flag, int value) => (flag & 1) != 0 ? value : -value;

    /// <summary>
    /// Transforms hmtx by dropping the left side bearings that equal the glyph's xMin. Returns null when neither
    /// array can be dropped (the table is then stored as-is).
    /// </summary>
    public static byte[]? TryTransformHmtx(byte[] hmtx, int glyphCount, int metricsCount, short[] xMins)
    {
        if (metricsCount < 1 || metricsCount > glyphCount || hmtx.Length != 4 * metricsCount + 2 * (glyphCount - metricsCount))
            return null;
        var r = new BigEndianReader(hmtx);
        var advances = new ushort[metricsCount];
        var lsbs = new short[glyphCount];
        for (var i = 0; i < metricsCount; i++)
        {
            advances[i] = r.U16();
            lsbs[i] = r.I16();
        }
        for (var i = metricsCount; i < glyphCount; i++)
            lsbs[i] = r.I16();

        var proportional = Enumerable.Range(0, metricsCount).All(i => lsbs[i] == xMins[i]);
        var monospaced = Enumerable.Range(metricsCount, glyphCount - metricsCount).All(i => lsbs[i] == xMins[i]);
        if (!proportional && !monospaced)
            return null;

        var w = new BigEndianWriter(hmtx.Length);
        w.U8((proportional ? 1 : 0) | (monospaced ? 2 : 0));
        foreach (var advance in advances)
            w.U16(advance);
        if (!proportional)
        {
            for (var i = 0; i < metricsCount; i++)
                w.I16(lsbs[i]);
        }
        if (!monospaced)
        {
            for (var i = metricsCount; i < glyphCount; i++)
                w.I16(lsbs[i]);
        }
        return w.ToArray();
    }

    /// <summary>Rebuilds hmtx from its transformed form, filling missing side bearings with each glyph's xMin.</summary>
    public static byte[] ReconstructHmtx(byte[] data, int offset, int length, int glyphCount, int metricsCount, short[] xMins)
    {
        if (metricsCount < 1 || metricsCount > glyphCount || xMins.Length < glyphCount)
            throw new InvalidDataException("The WOFF2 metrics are damaged.");
        var r = new BigEndianReader(data, offset, offset + length);
        var flags = r.U8();
        if ((flags & 0xFC) != 0)
            throw new InvalidDataException("The WOFF2 metrics use unknown flags.");
        var advances = new ushort[metricsCount];
        for (var i = 0; i < metricsCount; i++)
            advances[i] = r.U16();
        var lsbs = xMins[..glyphCount];
        if ((flags & 1) == 0)
        {
            for (var i = 0; i < metricsCount; i++)
                lsbs[i] = r.I16();
        }
        if ((flags & 2) == 0)
        {
            for (var i = metricsCount; i < glyphCount; i++)
                lsbs[i] = r.I16();
        }

        var w = new BigEndianWriter(4 * metricsCount + 2 * (glyphCount - metricsCount));
        for (var i = 0; i < glyphCount; i++)
        {
            if (i < metricsCount)
                w.U16(advances[i]);
            w.I16(lsbs[i]);
        }
        return w.ToArray();
    }
}
