// Converts an OpenType font with CFF (PostScript, cubic) outlines to TrueType (quadratic) outlines: builds glyf,
// loca, maxp 1.0 and post, updates head, hhea, hmtx and vmtx, and keeps every other table (cmap, name, OS/2, GSUB,
// GPOS, kern, ...) unchanged. Hints are not converted; a gasp/prep pair asks rasterizers to smooth instead.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Fonts.Cff;

namespace Filee.Engines.Fonts;

/// <summary>CFF → TrueType outline conversion.</summary>
internal static class CffToTrueType
{
    /// <summary>
    /// Maximum distance between a converted curve and the original, in em units (1 unit at 1000 units per em), as
    /// fontTools' otf2ttf uses.
    /// </summary>
    private const double ToleranceEm = 0.001;

    /// <summary>prep that switches on dropout control (SCANCTRL 511, SCANTYPE 4), as recommended for unhinted fonts.</summary>
    private static readonly byte[] UnhintedPrep = [0xB8, 0x01, 0xFF, 0x85, 0xB0, 0x04, 0x8D];

    /// <summary>gasp version 1 with one range: grid-fit and smooth (symmetrically) at every size.</summary>
    private static readonly byte[] SmoothGasp = [0, 1, 0, 1, 0xFF, 0xFF, 0, 0x0F];

    /// <summary>Returns a TrueType-outline copy of <paramref name="source"/>.</summary>
    /// <exception cref="NotSupportedException">CFF2 (variable) outlines.</exception>
    public static SfntFont Convert(SfntFont source, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        if (source["CFF2"] is not null)
            throw new NotSupportedException(
                "This is a variable font with CFF2 outlines. Converting CFF2 to TrueType isn't supported; WOFF, WOFF2 and OTF work.");
        var cff = CffFont.Parse(source.Require("CFF "));
        var glyphCount = source.GlyphCount;
        if (cff.GlyphCount != glyphCount)
            throw new InvalidDataException("The font's CFF outlines and glyph count don't match.");

        var head = source.Require("head");
        var upem = FontTables.UnitsPerEm(head);
        var tolerance = Math.Max(upem, 16) * ToleranceEm;
        var matrices = cff.FontMatrices.Select(m => ToFontUnits(m, upem)).ToArray();

        var interpreter = new Type2Interpreter(cff);
        var glyphs = new TrueTypeGlyph?[glyphCount];
        for (var gid = 0; gid < glyphCount; gid++)
        {
            if (gid % 64 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)gid / glyphCount);
            }
            var contours = interpreter.Run(gid);
            if (matrices[Math.Min(cff.FontDictIndex(gid), matrices.Length - 1)] is { } matrix)
                contours = [.. contours.Select(c => Transform(c, matrix))];
            glyphs[gid] = BuildGlyph(contours, tolerance);
        }

        var font = source.Clone();
        foreach (var tag in new[] { "CFF ", "VORG", "DSIG" })
            font.Tables.Remove(tag);
        font.Flavor = SfntFont.TrueTypeFlavor;

        var (glyf, loca, longLoca) = GlyfTable.Build([.. glyphs.Select(g => g?.Serialize() ?? [])], longFormat: false, alignment: 2);
        font.Tables["glyf"] = glyf;
        font.Tables["loca"] = loca;
        font.Tables["head"] = UpdateHead(head, glyphs, longLoca);
        font.Tables["maxp"] = BuildMaxp(glyphs);
        font.Tables["post"] = BuildPost(source["post"], Enumerable.Range(0, glyphCount).Select(cff.GlyphName).ToArray());
        if (font["hhea"] is { Length: >= 36 } hhea && font["hmtx"] is { } hmtx)
            (font.Tables["hhea"], font.Tables["hmtx"]) = UpdateHorizontalMetrics(hhea, hmtx, glyphs);
        if (source["VORG"] is { } vorg && font["vhea"] is { Length: >= 36 } vhea && font["vmtx"] is { } vmtx)
            (font.Tables["vhea"], font.Tables["vmtx"]) = UpdateVerticalMetrics(vhea, vmtx, vorg, glyphs);
        if (!font.Tables.ContainsKey("gasp") && !font.Tables.ContainsKey("prep"))
        {
            font.Tables["gasp"] = SmoothGasp;
            font.Tables["prep"] = UnhintedPrep;
        }
        progress?.Report(1);
        return font;
    }

    /// <summary>FontMatrix scaled to font units; null when it is the identity (the usual 1/unitsPerEm matrix).</summary>
    private static double[]? ToFontUnits(double[] matrix, int upem)
    {
        var m = matrix.Select(v => v * upem).ToArray();
        double[] identity = [1, 0, 0, 1, 0, 0];
        return m.Zip(identity).All(p => Math.Abs(p.First - p.Second) < 1e-9) ? null : m;
    }

    private static PathContour Transform(PathContour contour, double[] m)
    {
        Vec2 Apply(Vec2 p) => new(m[0] * p.X + m[2] * p.Y + m[4], m[1] * p.X + m[3] * p.Y + m[5]);
        var result = new PathContour(Apply(contour.Start));
        foreach (var s in contour.Segments)
            result.Segments.Add(new PathSegment(s.IsCurve, Apply(s.C1), Apply(s.C2), Apply(s.End)));
        return result;
    }

    /// <summary>
    /// Builds a TrueType glyph: curves become quadratic splines, points are rounded to integers, and every contour is
    /// reversed because TrueType fills clockwise outer contours while PostScript draws them counter-clockwise.
    /// </summary>
    private static TrueTypeGlyph? BuildGlyph(List<PathContour> contours, double tolerance)
    {
        var endPoints = new List<ushort>();
        var points = new List<GlyphPoint>();
        foreach (var contour in contours)
        {
            var raw = new List<(Vec2 P, bool On)> { (contour.Start, true) };
            var current = contour.Start;
            foreach (var segment in contour.Segments)
            {
                if (segment.IsCurve)
                {
                    foreach (var control in CubicToQuadratic.Convert(current, segment.C1, segment.C2, segment.End, tolerance))
                        raw.Add((control, false));
                }
                raw.Add((segment.End, true));
                current = segment.End;
            }

            var rounded = Clean([.. raw.Select(p => new GlyphPoint(Round(p.P.X), Round(p.P.Y), p.On))]);
            if (rounded.Count < 3)
                continue; // nothing left to fill
            points.Add(rounded[0]);
            for (var i = rounded.Count - 1; i > 0; i--)
                points.Add(rounded[i]);
            if (points.Count > 0xFFFF)
                throw new InvalidDataException("A glyph has more points than TrueType allows.");
            endPoints.Add((ushort)(points.Count - 1));
        }
        if (endPoints.Count == 0)
            return null;

        var glyph = new TrueTypeGlyph
        {
            NumberOfContours = (short)endPoints.Count,
            EndPoints = [.. endPoints],
            Points = [.. points],
        };
        (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax) = glyph.ComputeBounds();
        return glyph;
    }

    /// <summary>
    /// Drops the closing point when it repeats the start (the contour closes by itself) and on-curve points that
    /// repeat their predecessor after rounding (zero-length lines).
    /// </summary>
    private static List<GlyphPoint> Clean(List<GlyphPoint> points)
    {
        var result = new List<GlyphPoint>(points.Count);
        foreach (var p in points)
        {
            if (result.Count > 0 && p.OnCurve && result[^1].OnCurve && result[^1].X == p.X && result[^1].Y == p.Y)
                continue;
            result.Add(p);
        }
        while (result.Count > 1 && result[^1].OnCurve && result[^1].X == result[0].X && result[^1].Y == result[0].Y)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static int Round(double value) => (int)Math.Floor(value + 0.5);

    private static byte[] UpdateHead(byte[] head, TrueTypeGlyph?[] glyphs, bool longLoca)
    {
        var result = FontTables.WithIndexToLocFormat(head, longLoca);
        var outlines = glyphs.OfType<TrueTypeGlyph>().ToList();
        if (outlines.Count > 0)
        {
            BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(36), outlines.Min(g => g.XMin));
            BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(38), outlines.Min(g => g.YMin));
            BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(40), outlines.Max(g => g.XMax));
            BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(42), outlines.Max(g => g.YMax));
        }
        BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(52), 0); // glyphDataFormat
        return result;
    }

    /// <summary>maxp version 1.0: outline maxima; no hinting instructions beyond the dropout-control prep.</summary>
    private static byte[] BuildMaxp(TrueTypeGlyph?[] glyphs)
    {
        var w = new BigEndianWriter(32);
        w.U32(0x00010000);
        w.U16(glyphs.Length);
        w.U16(glyphs.Max(g => g?.Points.Length ?? 0)); // maxPoints
        w.U16(glyphs.Max(g => (int?)g?.NumberOfContours ?? 0)); // maxContours
        w.U16(0); // maxCompositePoints
        w.U16(0); // maxCompositeContours
        w.U16(1); // maxZones: no twilight zone
        w.U16(0); // maxTwilightPoints
        w.U16(0); // maxStorage
        w.U16(0); // maxFunctionDefs
        w.U16(0); // maxInstructionDefs
        w.U16(1); // maxStackElements (the prep pushes one value at a time)
        w.U16(0); // maxSizeOfInstructions
        w.U16(0); // maxComponentElements
        w.U16(0); // maxComponentDepth
        return w.ToArray();
    }

    /// <summary>
    /// post format 2.0 with the CFF glyph names when the font has them (name-keyed CFF), else format 3.0 (CID-keyed
    /// fonts have no names). The header fields (italic angle, underline) are kept.
    /// </summary>
    internal static byte[] BuildPost(byte[]? old, string?[] names)
    {
        var w = new BigEndianWriter(64 + names.Length * 10);
        var header = new byte[32];
        old?.AsSpan(0, Math.Min(32, old.Length)).CopyTo(header);
        var usable = names.All(n => n is { Length: > 0 and < 256 } && n.All(c => c is > ' ' and < (char)127));
        BinaryPrimitives.WriteUInt32BigEndian(header, usable ? 0x00020000u : 0x00030000u);
        w.Bytes(header);
        if (!usable)
            return w.ToArray();

        var standard = CffStandardData.MacGlyphNames.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        var custom = new Dictionary<string, int>(StringComparer.Ordinal);
        var extra = new List<string>();
        w.U16(names.Length);
        foreach (var name in names)
        {
            if (standard.TryGetValue(name!, out var index))
            {
                w.U16(index);
                continue;
            }
            if (!custom.TryGetValue(name!, out index))
            {
                index = custom[name!] = standard.Count + extra.Count;
                extra.Add(name!);
            }
            w.U16(index);
        }
        foreach (var name in extra)
        {
            w.U8(name.Length);
            w.Bytes(Encoding.ASCII.GetBytes(name));
        }
        return w.ToArray();
    }

    /// <summary>Sets each left side bearing to the new xMin and recomputes hhea's extremes.</summary>
    private static (byte[] Hhea, byte[] Hmtx) UpdateHorizontalMetrics(byte[] hhea, byte[] hmtx, TrueTypeGlyph?[] glyphs)
    {
        var metrics = FontTables.NumberOfMetrics(hhea);
        var advances = ReadMetrics(hmtx, metrics, glyphs.Length, out _);
        var lsbs = glyphs.Select(g => (int)(g?.XMin ?? 0)).ToArray();
        var result = WriteMetrics(advances, lsbs, metrics);

        var header = (byte[])hhea.Clone();
        var withOutlines = Enumerable.Range(0, glyphs.Length).Where(i => glyphs[i] is not null).ToList();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)advances.DefaultIfEmpty(0).Max());
        if (withOutlines.Count > 0)
        {
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(12), (short)withOutlines.Min(i => glyphs[i]!.XMin));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(14), (short)withOutlines.Min(i => advances[i] - glyphs[i]!.XMax));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(16), (short)withOutlines.Max(i => glyphs[i]!.XMax));
        }
        return (header, result);
    }

    /// <summary>
    /// TrueType has no VORG: the vertical origin is yMax + top side bearing. Set each top side bearing so the origin
    /// stays where VORG put it, and recompute vhea's extremes.
    /// </summary>
    private static (byte[] Vhea, byte[] Vmtx) UpdateVerticalMetrics(byte[] vhea, byte[] vmtx, byte[] vorg, TrueTypeGlyph?[] glyphs)
    {
        var metrics = FontTables.NumberOfMetrics(vhea);
        var heights = ReadMetrics(vmtx, metrics, glyphs.Length, out var tsbs);
        var r = new BigEndianReader(vorg);
        r.Skip(4); // version
        int defaultOrigin = r.I16();
        int count = r.U16();
        var origins = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
            origins[r.U16()] = r.I16();

        for (var i = 0; i < glyphs.Length; i++)
        {
            if (glyphs[i] is { } glyph)
                tsbs[i] = origins.GetValueOrDefault(i, defaultOrigin) - glyph.YMax;
        }
        var result = WriteMetrics(heights, tsbs, metrics);

        var header = (byte[])vhea.Clone();
        var withOutlines = Enumerable.Range(0, glyphs.Length).Where(i => glyphs[i] is not null).ToList();
        if (withOutlines.Count > 0)
        {
            int Extent(int i) => tsbs[i] + glyphs[i]!.YMax - glyphs[i]!.YMin;
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(12), (short)withOutlines.Min(i => tsbs[i]));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(14), (short)withOutlines.Min(i => heights[i] - Extent(i)));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(16), (short)withOutlines.Max(Extent));
        }
        return (header, result);
    }

    /// <summary>Reads hmtx/vmtx: the advance of every glyph (the last one repeats) and the side bearings.</summary>
    private static int[] ReadMetrics(byte[] table, int metrics, int glyphCount, out int[] bearings)
    {
        var r = new BigEndianReader(table);
        var advances = new int[glyphCount];
        bearings = new int[glyphCount];
        var last = 0;
        for (var i = 0; i < glyphCount; i++)
        {
            if (i < metrics)
                last = r.U16();
            advances[i] = last;
            bearings[i] = r.Remaining >= 2 ? r.I16() : 0;
        }
        return advances;
    }

    private static byte[] WriteMetrics(int[] advances, int[] bearings, int metrics)
    {
        var w = new BigEndianWriter(advances.Length * 4);
        for (var i = 0; i < advances.Length; i++)
        {
            if (i < metrics)
                w.U16(advances[i]);
            w.I16(bearings[i]);
        }
        return w.ToArray();
    }
}
