// The invisible text of a searchable PDF written by Filee itself (Apple Vision, VisionConverter): each page is the
// picture as it looks, with the recognized words on top in text render mode 3 (neither filled nor stroked), placed and
// stretched over the words of the picture so they can be searched, selected and copied. PdfDocumentReader and
// TesseractConverter.PagesWithText read it like Tesseract's own layer.
//
// The words are written in a font without glyph shapes made here (GlyphlessFont): every UTF-16 unit is one character
// code (Identity-H), drawn as an empty glyph half an em wide, and the ToUnicode map gives each code back as itself,
// so any script works without a real font in the file.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Ocr;

/// <summary>
/// One recognized line. Positions are fractions of the picture (0–1) from its bottom-left corner, as Apple Vision
/// reports them.
/// </summary>
/// <param name="Words">The line's words, left to right.</param>
internal sealed record OcrLine(double Left, double Bottom, double Width, double Height, IReadOnlyList<OcrWord> Words)
{
    /// <summary>
    /// Splits <paramref name="text"/> into words at white space and spreads them over the line by their length: for
    /// a line whose words have no boxes of their own.
    /// </summary>
    public static OcrLine Estimate(string text, double left, double bottom, double width, double height)
    {
        var words = new List<OcrWord>();
        var ranges = WordRanges(text);
        var letters = ranges.Sum(r => r.Length);
        if (letters == 0)
            return new OcrLine(left, bottom, width, height, words);
        // A space between two words counts as one letter of width.
        var unit = width / (letters + ranges.Count - 1);
        var x = left;
        foreach (var (start, length) in ranges)
        {
            words.Add(new OcrWord(text.Substring(start, length), x, unit * length));
            x += unit * (length + 1);
        }
        return new OcrLine(left, bottom, width, height, words);
    }

    /// <summary>Where each word of <paramref name="text"/> starts and how long it is, in UTF-16 units.</summary>
    public static List<(int Start, int Length)> WordRanges(string text)
    {
        var ranges = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || char.IsWhiteSpace(text[i]);
            if (!space && start < 0)
                start = i;
            else if (space && start >= 0)
            {
                ranges.Add((start, i - start));
                start = -1;
            }
        }
        return ranges;
    }
}

/// <summary>One recognized word: its text and where it is across the line (fractions of the picture's width).</summary>
internal sealed record OcrWord(string Text, double Left, double Width);

/// <summary>Writes searchable PDFs: pictures as pages, each with its recognized text as an invisible layer.</summary>
internal sealed class OcrPdfWriter : IDisposable
{
    /// <summary>The font's resource name on every page.</summary>
    internal const string FontResource = "/FOcr";

    /// <summary>Width of every character code, in thousandths of the font size.</summary>
    private const int CharacterWidth = 500;

    private readonly PdfDocument _document = new();
    private PdfDictionary? _font;

    public int PageCount => _document.PageCount;

    /// <summary>
    /// Adds <paramref name="picture"/> (a JPEG or PNG) as a page of <paramref name="width"/> × <paramref name="height"/>
    /// points with <paramref name="lines"/> on it as invisible text.
    /// </summary>
    public void AddPage(string picture, double width, double height, IReadOnlyList<OcrLine> lines)
    {
        var page = _document.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var image = XImage.FromFile(picture))
            gfx.DrawImage(image, 0, 0, width, height);
        var text = TextLayer(lines, width, height);
        if (text.Length == 0)
            return;
        var resources = page.Resources;
        if (resources.Elements.GetDictionary("/Font") is not { } fonts)
        {
            fonts = new PdfDictionary(_document);
            resources.Elements["/Font"] = fonts;
        }
        fonts.Elements.SetReference(FontResource, _font ??= CreateFont());
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(text));
    }

    public void Save(string path) => _document.Save(path);

    public void Dispose() => _document.Dispose();

    /// <summary>
    /// The content stream of the invisible text: one font size per line (its height), the baseline at the line's
    /// bottom, and each word stretched (Tz) from where it starts to where the next one starts, its trailing space
    /// included, so text extraction finds the words and the spaces between them. Empty when there is nothing to write.
    /// </summary>
    internal static string TextLayer(IReadOnlyList<OcrLine> lines, double pageWidth, double pageHeight)
    {
        var content = new StringBuilder();
        foreach (var line in lines)
        {
            var size = line.Height * pageHeight;
            if (size <= 0)
                continue;
            var baseline = line.Bottom * pageHeight;
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                if (string.IsNullOrWhiteSpace(word.Text))
                    continue;
                var x = word.Left * pageWidth;
                var last = i == line.Words.Count - 1;
                var text = last ? word.Text : word.Text + " ";
                var width = word.Width * pageWidth;
                if (!last && (line.Words[i + 1].Left - word.Left) * pageWidth is var toNext && toNext > width)
                    width = toNext;
                if (width <= 0)
                    continue;
                var scale = 100 * width / (text.Length * size * CharacterWidth / 1000);
                content.Append(CultureInfo.InvariantCulture, $"{FontResource} {N(size)} Tf {N(scale)} Tz 1 0 0 1 {N(x)} {N(baseline)} Tm <");
                foreach (var unit in text)
                    content.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
                content.Append("> Tj\n");
            }
        }
        return content.Length == 0 ? "" : "q\nBT\n3 Tr\n" + content + "ET\nQ\n";
    }

    private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The Type 0 font with the glyphless TrueType font in it, once per document.</summary>
    private PdfDictionary CreateFont()
    {
        var file = Indirect(GlyphlessFont.Bytes, compress: true);
        file.Elements.SetInteger("/Length1", GlyphlessFont.Bytes.Length);

        var descriptor = Indirect();
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/" + GlyphlessFont.Name);
        descriptor.Elements.SetInteger("/Flags", 5); // fixed pitch, symbolic
        descriptor.Elements["/FontBBox"] = Numbers(0, 0, CharacterWidth, 1000);
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", 1000);
        descriptor.Elements.SetInteger("/Descent", -1);
        descriptor.Elements.SetInteger("/CapHeight", 1000);
        descriptor.Elements.SetInteger("/StemV", 80);
        descriptor.Elements.SetReference("/FontFile2", file);

        // Every character code shows glyph 1, the empty one.
        var map = new byte[2 * 65536];
        for (var i = 1; i < map.Length; i += 2)
            map[i] = 1;
        var cidToGid = Indirect(map, compress: true);

        var info = new PdfDictionary(_document);
        info.Elements["/Registry"] = new PdfString("Adobe");
        info.Elements["/Ordering"] = new PdfString("Identity");
        info.Elements.SetInteger("/Supplement", 0);

        var cidFont = Indirect();
        cidFont.Elements.SetName("/Type", "/Font");
        cidFont.Elements.SetName("/Subtype", "/CIDFontType2");
        cidFont.Elements.SetName("/BaseFont", "/" + GlyphlessFont.Name);
        cidFont.Elements["/CIDSystemInfo"] = info;
        cidFont.Elements.SetReference("/FontDescriptor", descriptor);
        cidFont.Elements.SetInteger("/DW", CharacterWidth);
        cidFont.Elements.SetReference("/CIDToGIDMap", cidToGid);

        var toUnicode = Indirect(Encoding.ASCII.GetBytes(ToUnicode), compress: false);

        var font = Indirect();
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type0");
        font.Elements.SetName("/BaseFont", "/" + GlyphlessFont.Name);
        font.Elements.SetName("/Encoding", "/Identity-H");
        var descendants = new PdfArray(_document);
        descendants.Elements.Add(cidFont.Reference!);
        font.Elements["/DescendantFonts"] = descendants;
        font.Elements.SetReference("/ToUnicode", toUnicode);
        return font;
    }

    /// <summary>Each two-byte code is the UTF-16 unit it shows (a character outside the BMP is two codes).</summary>
    private const string ToUnicode = """
        /CIDInit /ProcSet findresource begin
        12 dict begin
        begincmap
        /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
        /CMapName /Adobe-Identity-UCS def
        /CMapType 2 def
        1 begincodespacerange
        <0000> <FFFF>
        endcodespacerange
        1 beginbfrange
        <0000> <FFFF> <0000>
        endbfrange
        endcmap
        CMapName currentdict /CMap defineresource pop
        end
        end
        """;

    private PdfDictionary Indirect(byte[]? stream = null, bool compress = false)
    {
        var dictionary = new PdfDictionary(_document);
        _document.Internals.AddObject(dictionary);
        if (stream is not null)
        {
            if (compress)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    zlib.Write(stream);
                dictionary.CreateStream(buffer.ToArray());
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
            else
            {
                dictionary.CreateStream(stream);
            }
        }
        return dictionary;
    }

    private PdfArray Numbers(params int[] values)
    {
        var array = new PdfArray(_document);
        foreach (var value in values)
            array.Elements.Add(new PdfInteger(value));
        return array;
    }
}

/// <summary>
/// A TrueType font with two empty glyphs, 500 of 1000 units wide: what the invisible text of an OCR layer is set in.
/// Built in code, so there is no font file to ship.
/// </summary>
internal static class GlyphlessFont
{
    public const string Name = "FileeGlyphless";

    public static readonly byte[] Bytes = Build();

    private static byte[] Build()
    {
        // Tables in the order of their tags (the directory must be sorted), as 16-bit words (a 32-bit field is two).
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            // Version 0, one subtable (Windows, Unicode BMP) at 12: format 4, one segment that maps only 0xFFFF.
            ["cmap"] = Words(0, 1, 3, 1, 0, 12, 4, 24, 0, 2, 2, 0, 0, 0xFFFF, 0, 0xFFFF, 1, 0),
            ["glyf"] = [],
            // Version 1.0, revision 1.0, checksum adjustment (set below), magic number, flags, 1000 units per em,
            // created and modified (none), bounding box 0 0 500 1000, style, smallest size, direction, short loca,
            // glyph format.
            ["head"] = Words(1, 0, 1, 0, 0, 0, 0x5F0F, 0x3CF5, 3, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 500, 1000, 0, 3, 2, 0, 0),
            // Version 1.0, ascender 1000, descender and gap 0, widest 500, bearings and extent 0, upright caret,
            // reserved, metric format, two metrics.
            ["hhea"] = Words(1, 0, 1000, 0, 0, 500, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 2),
            ["hmtx"] = Words(500, 0, 500, 0),
            ["loca"] = Words(0, 0, 0),
            // Version 1.0, two glyphs, no points or contours, two zones.
            ["maxp"] = Words(1, 0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0),
            ["name"] = Words(0, 0, 6),
            // Version 3.0 (no glyph names), upright, no underline, fixed pitch.
            ["post"] = Words(3, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        var count = tables.Count;
        var power = 1;
        var log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }
        var font = new BigEndian();
        font.U32(0x00010000);
        font.U16(count);
        font.U16(power * 16);
        font.U16(log);
        font.U16(count * 16 - power * 16);
        var offset = 12 + 16 * count;
        var headOffset = 0;
        foreach (var (tag, data) in tables)
        {
            font.Bytes(Encoding.ASCII.GetBytes(tag));
            font.U32(Checksum(data));
            font.U32((uint)offset);
            font.U32((uint)data.Length);
            if (tag == "head")
                headOffset = offset;
            offset += Padded(data.Length);
        }
        foreach (var data in tables.Values)
        {
            font.Bytes(data);
            font.Bytes(new byte[Padded(data.Length) - data.Length]);
        }
        var bytes = font.ToArray();
        var adjustment = unchecked(0xB1B0AFBA - Checksum(bytes));
        bytes[headOffset + 8] = (byte)(adjustment >> 24);
        bytes[headOffset + 9] = (byte)(adjustment >> 16);
        bytes[headOffset + 10] = (byte)(adjustment >> 8);
        bytes[headOffset + 11] = (byte)adjustment;
        return bytes;
    }

    private static int Padded(int length) => (length + 3) & ~3;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint value = 0;
            for (var j = 0; j < 4; j++)
                value = (value << 8) | (i + j < data.Length ? data[i + j] : 0u);
            sum = unchecked(sum + value);
        }
        return sum;
    }

    private static byte[] Words(params int[] values)
    {
        var writer = new BigEndian();
        foreach (var value in values)
            writer.U16(value);
        return writer.ToArray();
    }

    private sealed class BigEndian
    {
        private readonly MemoryStream _stream = new();

        public void U16(int value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void U32(uint value)
        {
            U16((int)(value >> 16));
            U16((int)(value & 0xFFFF));
        }

        public void Bytes(byte[] value) => _stream.Write(value);

        public byte[] ToArray() => _stream.ToArray();
    }
}
