// Writes one-page PDFs glyph by glyph, for the PDF reader's complex-script tests: each glyph is placed on its own at a
// given position and width, in the order given (the content-stream order), with the text its ToUnicode entry gives it,
// and runs of glyphs can carry an /ActualText. That is how shaping engines leave Indic and Arabic text in a PDF (marks
// with no width on top of their letter, vowel signs before their consonant, right-to-left glyphs drawn left to right,
// placeholder glyphs), which the PdfPig writer behind PdfBuilder can't produce. The font is a Type 3 font whose glyphs
// draw nothing: only the positions, widths and ToUnicode entries matter.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Tests;

/// <summary>A one-page A4 PDF of individually placed glyphs; <c>y</c> is the baseline in points from the top.</summary>
internal sealed class GlyphPdfBuilder
{
    private const double Height = 842;
    private readonly StringBuilder _content = new();
    private readonly Dictionary<string, int> _codes = [];
    private readonly Dictionary<int, double> _widths = [];

    /// <summary>
    /// One glyph whose ToUnicode text is <paramref name="text"/>, its origin at <paramref name="x"/>, advancing by
    /// <paramref name="width"/> points (0 for a mark drawn on the glyph before it).
    /// </summary>
    public GlyphPdfBuilder Glyph(string text, double x, double y, double width, double size = 12)
    {
        if (!_codes.TryGetValue(text + "|" + Number(width / size), out var code))
        {
            code = 0x21 + _codes.Count;
            if (code > 0xFE)
                throw new InvalidOperationException("Too many different glyphs for one test font.");
            _codes[text + "|" + Number(width / size)] = code;
            _widths[code] = width / size * 1000;
        }
        _content.Append(CultureInfo.InvariantCulture, $"BT /F1 {Number(size)} Tf 1 0 0 1 {Number(x)} {Number(Height - y)} Tm <{code:X2}> Tj ET\n");
        return this;
    }

    /// <summary>Glyphs one after the other from <paramref name="x"/>, each <paramref name="advance"/> wide (a word of plain letters).</summary>
    public GlyphPdfBuilder Run(string text, double x, double y, double advance, double size = 12)
    {
        foreach (var letter in text.EnumerateRunes())
        {
            Glyph(letter.ToString(), x, y, advance, size);
            x += advance;
        }
        return this;
    }

    /// <summary>The glyphs added in <paramref name="glyphs"/> stand for <paramref name="text"/> (/Span with /ActualText).</summary>
    public GlyphPdfBuilder ActualText(string text, Action<GlyphPdfBuilder> glyphs)
    {
        _content.Append("/Span <</ActualText <FEFF").Append(Utf16Hex(text)).Append(">>> BDC\n");
        glyphs(this);
        _content.Append("EMC\n");
        return this;
    }

    public string Save(string path)
    {
        var codes = _codes.OrderBy(c => c.Value).ToList();
        var first = codes.Count == 0 ? 0x21 : codes[0].Value;
        var last = codes.Count == 0 ? 0x21 : codes[^1].Value;
        var widths = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(c => Number(_widths.GetValueOrDefault(c))));
        var names = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(c => $"/g{c}"));
        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
            .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
            .Append("1 begincodespacerange\n<00> <FF>\nendcodespacerange\n")
            .Append(CultureInfo.InvariantCulture, $"{codes.Count} beginbfchar\n");
        foreach (var (key, code) in codes)
            cmap.Append(CultureInfo.InvariantCulture, $"<{code:X2}> <{Utf16Hex(key[..key.LastIndexOf('|')])}>\n");
        cmap.Append("endbfchar\nendcmap\nCMapName currentdict /CIDInit /ResourceSet defineresource pop\nend\nend\n");

        var content = _content.ToString();
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            $"<< /Type /Font /Subtype /Type3 /FontBBox [0 -200 1000 800] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 7 0 R " +
            $"/Encoding << /Type /Encoding /Differences [{first} {names}] >> /FirstChar {first} /LastChar {last} /Widths [{widths}] " +
            "/ToUnicode 6 0 R /Resources << >> >>",
            Stream(content),
            Stream(cmap.ToString()),
            $"<< {string.Join(' ', Enumerable.Range(first, last - first + 1).Select(c => $"/g{c} 8 0 R"))} >>",
            Stream("0 0 d0\n"),
        ];

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;

        static string Stream(string data) => $"<< /Length {Encoding.ASCII.GetByteCount(data)} >>\nstream\n{data}endstream";
    }

    private static string Utf16Hex(string text) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
