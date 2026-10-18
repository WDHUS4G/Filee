// Font conversion (TTF, OTF, WOFF, WOFF2, EOT) with the built-in font engine. The test fonts in Fonts/ are small
// subsets of Noto Sans and Noto Sans KR (SIL OFL 1.1, see Fonts/OFL.txt) made by Fonts/make-test-fonts.py:
// hinted TrueType with composite glyphs, CID-keyed CFF with subroutines and VORG, and a name-keyed CFF font with a
// seac accent, flex and subroutines; plus the TrueType one compressed by Google's reference WOFF2 encoder.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Fonts;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class FontTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string TrueType = "FileeTest-TrueType.ttf";
    private const string CidKeyed = "FileeTest-CID.otf";
    private const string NameKeyed = "FileeTest-NameKeyed.otf";
    private const string GoogleWoff2 = "FileeTest-TrueType.google.woff2";

    private static readonly string[] Formats = ["ttf", "otf", "woff", "woff2", "eot"];

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Fonts", name);

    private static SfntFont Load(string path) => FontFile.Load(File.ReadAllBytes(path));

    /// <summary>Converts a copy of a sample (so outputs land in a scratch folder) through the job queue.</summary>
    private async Task<string> ConvertAsync(string input, string target)
    {
        var folder = fx.NewFolder();
        var copy = Path.Combine(folder, Path.GetFileName(input));
        File.Copy(input, copy);
        var job = await fx.ConvertAsync([copy], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        Assert.Equal("." + target, Path.GetExtension(output));
        return output;
    }

    private async Task<string> RoundTripAsync(string sample, string via)
    {
        var middle = await ConvertAsync(Sample(sample), via);
        return await ConvertAsync(middle, Path.GetExtension(sample)[1..]);
    }

    // ───────────────────────── Routes and every conversion ─────────────────────────

    [Fact]
    public void Every_font_format_converts_to_every_other_in_one_step()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var from in Formats)
        {
            foreach (var to in Formats.Where(t => t != from))
            {
                var route = planner.Plan(from, to);
                Assert.True(route is not null, $"{from} → {to}");
                Assert.Equal("font", Assert.Single(route.Steps).Converter.Id);
            }
        }
    }

    public static TheoryData<string, string> AllConversions()
    {
        var data = new TheoryData<string, string>();
        foreach (var sample in new[] { TrueType, CidKeyed, NameKeyed })
        {
            foreach (var target in Formats.Where(t => t != Path.GetExtension(sample)[1..]))
                data.Add(sample, target);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllConversions))]
    public async Task Font_converts_and_the_result_loads_in_skia(string sample, string target)
    {
        var source = Load(Sample(sample));
        var output = await ConvertAsync(Sample(sample), target);
        var bytes = File.ReadAllBytes(output);

        var result = FontFile.Load(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(source.GlyphCount, result.GlyphCount);
        Assert.Equal(target is "ttf" or "eot" || !source.HasCffOutlines, !result.HasCffOutlines);

        // Skia on Windows loads SFNT data only (DirectWrite); the web formats are checked after decoding.
        using var typeface = SKTypeface.FromStream(new MemoryStream(target is "ttf" or "otf" ? bytes : result.Write()));
        Assert.NotNull(typeface);
        Assert.Equal(source.GlyphCount, typeface.GlyphCount);
        using var font = new SKFont(typeface, 48);
        var text = sample == NameKeyed ? "Aae" : "Filee";
        Assert.All(font.GetGlyphs(text), g => Assert.NotEqual(0, g));
        Assert.True(font.MeasureText(text) > 48);
        using var path = font.GetGlyphPath(font.GetGlyphs(text)[0]);
        Assert.False(path.IsEmpty);
    }

    // ───────────────────────── Lossless round trips ─────────────────────────

    [Theory]
    [InlineData(TrueType, "woff")]
    [InlineData(TrueType, "eot")]
    [InlineData(TrueType, "otf")]
    [InlineData(CidKeyed, "woff")]
    [InlineData(CidKeyed, "woff2")]
    [InlineData(NameKeyed, "woff2")]
    public async Task Round_trip_keeps_every_table(string sample, string via)
    {
        var back = Load(await RoundTripAsync(sample, via));

        AssertSameTables(Load(Sample(sample)), back);
    }

    [Fact]
    public async Task Ttf_to_woff2_and_back_keeps_glyph_outlines_and_tables()
    {
        var source = Load(Sample(TrueType));
        var back = Load(await RoundTripAsync(TrueType, "woff2"));

        // glyf and loca are rebuilt from the transformed streams, so they are compared glyph by glyph.
        AssertSameTables(source, back, "glyf", "loca");
        AssertSameGlyphs(source, back);
        Assert.Equal(1 << 11, HeadFlags(back) & 1 << 11); // "lossless transform applied"
    }

    [Fact]
    public async Task Woff2_uses_the_glyf_and_hmtx_transforms_and_beats_woff()
    {
        var woff2 = File.ReadAllBytes(await ConvertAsync(Sample(TrueType), "woff2"));
        var woff = File.ReadAllBytes(await ConvertAsync(Sample(TrueType), "woff"));
        var google = File.ReadAllBytes(Sample(GoogleWoff2));

        var directory = Woff2Directory(woff2);
        Assert.Equal((0, true), directory["glyf"]);
        Assert.Equal((0, true), directory["loca"]);
        Assert.Equal((1, true), directory["hmtx"]);
        Assert.Equal((0, false), directory["cmap"]);
        Assert.Equal(0, woff2.Length % 4);
        Assert.Equal(woff2.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(woff2.AsSpan(8)));
        Assert.True(woff2.Length < woff.Length * 0.8, $"WOFF2 {woff2.Length} vs WOFF {woff.Length}");
        Assert.True(woff2.Length < google.Length * 1.05, $"Filee {woff2.Length} vs Google {google.Length}");
    }

    [Fact]
    public async Task Woff2_from_googles_reference_encoder_decodes_to_the_source()
    {
        var source = Load(Sample(TrueType));
        var google = File.ReadAllBytes(Sample(GoogleWoff2));
        Assert.Equal((0, true), Woff2Directory(google)["glyf"]);

        // The encoder build used (wawoff2 2.0.1) doesn't write the overlap bitmap, a later addition to the spec, so
        // OVERLAP_SIMPLE is lost in that file.
        var decoded = FontFile.Load(google, TestContext.Current.CancellationToken);
        AssertSameTables(source, decoded, "glyf", "loca");
        AssertSameGlyphs(source, decoded, overlapBitmap: false);

        // Through the engine as well.
        var ttf = Load(await ConvertAsync(Sample(GoogleWoff2), "ttf"));
        AssertSameGlyphs(source, ttf, overlapBitmap: false);
    }

    [Fact]
    public void Woff2_collection_yields_its_first_font()
    {
        var source = Load(Sample(TrueType));
        var decoded = FontFile.Load(Woff2Collection(source, Load(Sample(NameKeyed))), TestContext.Current.CancellationToken);

        Assert.Equal(SfntFont.TrueTypeFlavor, decoded.Flavor);
        AssertSameTables(source, decoded);
    }

    // ───────────────────────── CFF → TrueType ─────────────────────────

    [Theory]
    [InlineData(CidKeyed)]
    [InlineData(NameKeyed)]
    public async Task Cff_to_ttf_keeps_glyphs_metrics_and_outlines(string sample)
    {
        var source = Load(Sample(sample));
        var output = await ConvertAsync(Sample(sample), "ttf");
        var ttf = Load(output);

        Assert.Equal(SfntFont.TrueTypeFlavor, ttf.Flavor);
        Assert.Null(ttf["CFF "]);
        Assert.Null(ttf["VORG"]);
        Assert.Equal(source.GlyphCount, ttf.GlyphCount);
        Assert.Equal(32, ttf.Require("maxp").Length); // maxp 1.0
        foreach (var tag in new[] { "cmap", "name", "OS/2", "GSUB", "GPOS", "BASE" }.Where(t => source[t] is not null))
            Assert.Equal(source[tag], ttf[tag]);

        var glyphs = GlyfTable.ReadAll(ttf);
        var (advances, lsbs) = Metrics(ttf, "hhea", "hmtx");
        Assert.Equal(Metrics(source, "hhea", "hmtx").Advances, advances);
        for (var i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(glyphs[i]?.XMin ?? 0, lsbs[i]);
            if (glyphs[i] is not { } glyph)
                continue;
            Assert.Equal((glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax), glyph.ComputeBounds());
            // TrueType fills clockwise outer contours: the largest contour must have a negative (clockwise) area.
            var areas = Contours(glyph).Select(SignedArea).ToList();
            Assert.True(areas.MaxBy(Math.Abs) < 0, $"glyph {i} is counter-clockwise");
        }

        // Compare the drawn outlines with the CFF originals (Skia reads both), 1 unit per 1000 plus rounding.
        using var cffFace = SKTypeface.FromFile(Sample(sample));
        using var ttfFace = SKTypeface.FromFile(output);
        using var cffFont = new SKFont(cffFace, 1000) { Hinting = SKFontHinting.None };
        using var ttfFont = new SKFont(ttfFace, 1000) { Hinting = SKFontHinting.None };
        for (ushort gid = 0; gid < glyphs.Length; gid++)
        {
            // macOS's font backend gives no path at all (null) for an empty glyph.
            using var expected = cffFont.GetGlyphPath(gid) ?? new SKPath();
            using var actual = ttfFont.GetGlyphPath(gid) ?? new SKPath();
            Assert.Equal(expected.IsEmpty, actual.IsEmpty);
            if (expected.IsEmpty)
                continue;
            var e = expected.TightBounds;
            var a = actual.TightBounds;
            foreach (var (x, y) in new[] { (e.Left, a.Left), (e.Top, a.Top), (e.Right, a.Right), (e.Bottom, a.Bottom) })
                Assert.True(Math.Abs(x - y) <= 1.6, $"glyph {gid}: bounds {e} vs {a}");
            var (different, covered) = CompareCoverage(expected, actual);
            Assert.True(different <= Math.Max(8, covered / 100), $"glyph {gid}: {different} of {covered} pixels differ");
        }
    }

    [Fact]
    public async Task Cff_glyph_names_become_post_format_2()
    {
        var ttf = Load(await ConvertAsync(Sample(NameKeyed), "ttf"));
        Assert.Equal([".notdef", "space", "A", "a", "e", "acute", "Aacute", "flexbar", "box"], PostNames(ttf));

        var cid = Load(await ConvertAsync(Sample(CidKeyed), "ttf"));
        Assert.Equal(0x00030000u, BinaryPrimitives.ReadUInt32BigEndian(cid.Require("post")));
        Assert.Equal(Load(Sample(CidKeyed)).Require("post").AsSpan(4, 28).ToArray(), cid.Require("post").AsSpan(4, 28).ToArray());
    }

    [Fact]
    public async Task Seac_accent_flex_and_subroutines_are_drawn()
    {
        var glyphs = GlyfTable.ReadAll(Load(await ConvertAsync(Sample(NameKeyed), "ttf")));
        var (a, acute, aacute, flexbar, box) = (glyphs[2]!, glyphs[5]!, glyphs[6]!, glyphs[7]!, glyphs[8]!);

        // Aacute = endchar seac: the contours of A, then those of acute moved by (170, 190).
        Assert.Equal(a.NumberOfContours + acute.NumberOfContours, aacute.NumberOfContours);
        Assert.Equal(
            [.. a.Points, .. acute.Points.Select(p => p with { X = p.X + 170, Y = p.Y + 190 })],
            aacute.Points);

        // flexbar: a rectangle whose top is a flex through (350, 530).
        Assert.Equal((100, 0, 600, 530), ((int)flexbar.XMin, (int)flexbar.YMin, (int)flexbar.XMax, (int)flexbar.YMax));
        Assert.Contains(new GlyphPoint(350, 530, true), flexbar.Points);

        // box: outer square from a local subroutine, hole from a global one, in opposite directions.
        Assert.Equal(2, box.NumberOfContours);
        Assert.Equal((50, 0, 550, 500), ((int)box.XMin, (int)box.YMin, (int)box.XMax, (int)box.YMax));
        var areas = Contours(box).Select(SignedArea).ToList();
        Assert.Equal(-500 * 500, areas[0]);
        Assert.Equal(300 * 300, areas[1]);
    }

    [Fact]
    public async Task Cff_to_ttf_moves_vorg_origins_into_vmtx()
    {
        var source = Load(Sample(CidKeyed));
        var ttf = Load(await ConvertAsync(Sample(CidKeyed), "ttf"));
        var defaultOrigin = BinaryPrimitives.ReadInt16BigEndian(source.Require("VORG").AsSpan(4));

        var glyphs = GlyfTable.ReadAll(ttf);
        var (heights, tsbs) = Metrics(ttf, "vhea", "vmtx");
        Assert.Equal(Metrics(source, "vhea", "vmtx").Advances, heights);
        for (var i = 0; i < glyphs.Length; i++)
        {
            if (glyphs[i] is { } glyph)
                Assert.Equal(defaultOrigin - glyph.YMax, tsbs[i]);
        }
    }

    [Fact]
    public async Task Cff_font_to_eot_carries_truetype_outlines_and_names()
    {
        var eot = File.ReadAllBytes(await ConvertAsync(Sample(NameKeyed), "eot"));

        Assert.Equal(eot.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(eot));
        Assert.Equal(0x00020001u, BinaryPrimitives.ReadUInt32LittleEndian(eot.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(eot.AsSpan(12))); // not compressed, not obfuscated
        Assert.Equal(0x504C, BinaryPrimitives.ReadUInt16LittleEndian(eot.AsSpan(34)));
        var familySize = BinaryPrimitives.ReadUInt16LittleEndian(eot.AsSpan(82));
        Assert.Equal("Filee Test NameKeyed", System.Text.Encoding.Unicode.GetString(eot, 84, familySize));
        var font = FontFile.Load(eot, TestContext.Current.CancellationToken);
        Assert.NotNull(font["glyf"]);
        Assert.Null(font["CFF "]);
    }

    // ───────────────────────── Errors ─────────────────────────

    [Fact]
    public async Task Compressed_eot_fails_with_a_clear_message()
    {
        var eot = File.ReadAllBytes(await ConvertAsync(Sample(TrueType), "eot"));
        eot[12] |= 0x04; // TTEMBED_TTCOMPRESSED
        var path = Path.Combine(fx.NewFolder(), "compressed.eot");
        File.WriteAllBytes(path, eot);

        var error = await FailingConversionAsync(path, "ttf");
        Assert.Contains("MicroType Express", error);
    }

    [Fact]
    public async Task Cff2_font_to_ttf_fails_with_a_clear_message()
    {
        var font = Load(Sample(CidKeyed));
        font.Tables["CFF2"] = font.Tables["CFF "];
        font.Tables.Remove("CFF ");
        var path = Path.Combine(fx.NewFolder(), "variable.otf");
        File.WriteAllBytes(path, font.Write());

        var error = await FailingConversionAsync(path, "ttf");
        Assert.Contains("CFF2", error);
    }

    [Fact]
    public async Task Something_that_is_not_a_font_fails_with_a_clear_message()
    {
        var path = Path.Combine(fx.NewFolder(), "fake.woff2");
        File.WriteAllText(path, "this is not a font");

        var error = await FailingConversionAsync(path, "ttf");
        Assert.Contains("not a font", error);
    }

    private async Task<string> FailingConversionAsync(string path, string target)
    {
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        return file.ErrorDetail ?? "";
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Same tables with the same bytes, except head.checkSumAdjustment and head.flags bit 11 (both depend on the
    /// container) and the tables named in <paramref name="except"/>.
    /// </summary>
    private static void AssertSameTables(SfntFont expected, SfntFont actual, params string[] except)
    {
        Assert.Equal(expected.Flavor, actual.Flavor);
        Assert.Equal(
            expected.Tables.Keys.Where(t => !except.Contains(t) && t != "DSIG").Order(StringComparer.Ordinal),
            actual.Tables.Keys.Where(t => !except.Contains(t)).Order(StringComparer.Ordinal));
        foreach (var tag in expected.Tables.Keys.Where(t => !except.Contains(t) && t != "DSIG"))
        {
            var (e, a) = (expected.Tables[tag], actual.Tables[tag]);
            if (tag == "head")
                (e, a) = (NormalizeHead(e), NormalizeHead(a));
            Assert.True(e.AsSpan().SequenceEqual(a), $"table '{tag}' differs");
        }
    }

    private static byte[] NormalizeHead(byte[] head)
    {
        var copy = (byte[])head.Clone();
        copy.AsSpan(8, 4).Clear();
        copy[16] &= 0xF7; // flags bit 11
        return copy;
    }

    private static int HeadFlags(SfntFont font) => BinaryPrimitives.ReadUInt16BigEndian(font.Require("head").AsSpan(16));

    private static void AssertSameGlyphs(SfntFont expected, SfntFont actual, bool overlapBitmap = true)
    {
        var a = GlyfTable.ReadAll(expected);
        var b = GlyfTable.ReadAll(actual);
        Assert.Equal(a.Length, b.Length);
        Assert.Contains(a, g => g is { IsComposite: true });
        Assert.Contains(a, g => g is { OverlapSimple: true });
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] is not { NumberOfContours: not 0 } e)
            {
                Assert.True(b[i] is null or { NumberOfContours: 0 }, $"glyph {i} should be empty");
                continue;
            }
            var g = b[i];
            Assert.NotNull(g);
            Assert.Equal((e.NumberOfContours, e.XMin, e.YMin, e.XMax, e.YMax), (g.NumberOfContours, g.XMin, g.YMin, g.XMax, g.YMax));
            Assert.Equal(e.EndPoints, g.EndPoints);
            Assert.Equal(e.Points, g.Points);
            Assert.Equal(e.Instructions, g.Instructions);
            Assert.Equal(e.OverlapSimple && overlapBitmap, g.OverlapSimple);
            Assert.Equal(e.Components, g.Components);
        }
    }

    /// <summary>Transform version and "transformed" flag of every table in a WOFF2 directory.</summary>
    private static Dictionary<string, (int Version, bool Transformed)> Woff2Directory(byte[] woff2)
    {
        string[] known = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep"];
        var r = new BigEndianReader(woff2, 12);
        int count = r.U16();
        r.Position = 48;
        var result = new Dictionary<string, (int, bool)>();
        for (var i = 0; i < count; i++)
        {
            var flags = r.U8();
            var tag = (flags & 63) == 63 ? SfntFont.TagName(r.U32()) : (flags & 63) < known.Length ? known[flags & 63] : $"#{flags & 63}";
            var version = flags >> 6;
            r.UIntBase128();
            var transformed = tag is "glyf" or "loca" ? version != 3 : version != 0;
            if (transformed)
                r.UIntBase128();
            result[tag] = (version, transformed);
        }
        return result;
    }

    /// <summary>A WOFF2 collection of two fonts with untransformed tables (written by hand: Filee writes no collections).</summary>
    private static byte[] Woff2Collection(SfntFont first, SfntFont second)
    {
        var tables = new List<(string Tag, byte[] Data)>();
        var directories = new List<(uint Flavor, List<int> Indices)>();
        foreach (var font in new[] { first, second })
        {
            var indices = new List<int>();
            foreach (var tag in font.Tables.Keys.Order(StringComparer.Ordinal))
            {
                indices.Add(tables.Count);
                tables.Add((tag, font.Tables[tag]));
            }
            directories.Add((font.Flavor, indices));
        }

        var stream = new BigEndianWriter();
        foreach (var (_, data) in tables)
            stream.Bytes(data);
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(stream.Length)];
        Assert.True(BrotliEncoder.TryCompress(stream.Written, compressed, out var written, 5, 22));

        var w = new BigEndianWriter();
        w.U32(0x774F4632);
        w.U32(SfntFont.CollectionTag);
        w.U32(0); // length (not checked)
        w.U16(tables.Count);
        w.U16(0);
        w.U32(0);
        w.U32((uint)written);
        w.Zeros(24);
        foreach (var (tag, data) in tables)
        {
            w.U8(tag is "glyf" or "loca" ? 63 | 3 << 6 : 63); // arbitrary tag, null transform
            w.U32(SfntFont.TagValue(tag));
            w.UIntBase128((uint)data.Length);
        }
        w.U32(0x00010000);
        w.Write255UInt16(directories.Count);
        foreach (var (flavor, indices) in directories)
        {
            w.Write255UInt16(indices.Count);
            w.U32(flavor);
            foreach (var index in indices)
                w.Write255UInt16(index);
        }
        w.Bytes(compressed.AsSpan(0, written));
        return w.ToArray();
    }

    /// <summary>Advances (repeating the last long metric) and side bearings of hmtx or vmtx.</summary>
    private static (int[] Advances, int[] Bearings) Metrics(SfntFont font, string header, string table)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.Require(header).AsSpan(34));
        var r = new BigEndianReader(font.Require(table));
        var advances = new int[font.GlyphCount];
        var bearings = new int[font.GlyphCount];
        for (var i = 0; i < advances.Length; i++)
        {
            advances[i] = i < count ? r.U16() : advances[i - 1];
            bearings[i] = r.I16();
        }
        return (advances, bearings);
    }

    private static List<string> PostNames(SfntFont font)
    {
        var r = new BigEndianReader(font.Require("post"));
        Assert.Equal(0x00020000u, r.U32());
        r.Position = 32;
        var indices = Enumerable.Range(0, r.U16()).Select(_ => (int)r.U16()).ToList();
        var extra = new List<string>();
        while (r.Remaining > 0)
            extra.Add(System.Text.Encoding.ASCII.GetString(r.Span(r.U8())));
        return [.. indices.Select(i => i < 258 ? Fonts.Cff.CffStandardData.MacGlyphNames[i] : extra[i - 258])];
    }

    private static IEnumerable<GlyphPoint[]> Contours(TrueTypeGlyph glyph)
    {
        var start = 0;
        foreach (var end in glyph.EndPoints)
        {
            yield return glyph.Points[start..(end + 1)];
            start = end + 1;
        }
    }

    /// <summary>Shoelace area of the control polygon: negative for clockwise contours (y up).</summary>
    private static long SignedArea(GlyphPoint[] points)
    {
        long twice = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Length]);
            twice += (long)p.X * q.Y - (long)q.X * p.Y;
        }
        return twice / 2;
    }

    /// <summary>Renders two glyph paths at 0.25 px per unit and counts pixels whose coverage clearly differs.</summary>
    private static (int Different, int Covered) CompareCoverage(SKPath expected, SKPath actual)
    {
        var bounds = SKRect.Union(expected.TightBounds, actual.TightBounds);
        var width = (int)Math.Ceiling(bounds.Width / 4) + 4;
        var height = (int)Math.Ceiling(bounds.Height / 4) + 4;
        byte[] Render(SKPath path)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(0.25f);
            canvas.Translate(-bounds.Left + 8, -bounds.Top + 8);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            canvas.DrawPath(path, paint);
            return bitmap.Bytes;
        }
        var e = Render(expected);
        var a = Render(actual);
        var different = e.Zip(a).Count(p => Math.Abs(p.First - p.Second) > 128);
        return (different, e.Count(v => v > 128));
    }
}
