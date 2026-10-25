// Symbols of Wingdings, Wingdings 2, Wingdings 3, Webdings and Symbol (#350): Word's Insert → Symbol stores a check
// box as a code of the font (w:sym w:font="Wingdings 2" w:char="F052"); the built-in Word → HWPX/PDF route drew every
// such symbol as a black dot "●" and Wingdings' empty box as "◆". They now arrive as the Unicode characters they show.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class SymbolFontTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Sym(string font, string code) => $"<w:r><w:sym w:font=\"{font}\" w:char=\"{code}\"/></w:r>";

    /// <summary>The check boxes and marks people insert, with the text each one must become.</summary>
    private static readonly (string Font, string Code, string Text)[] Symbols =
    [
        ("Wingdings 2", "F052", "☑"), ("Wingdings 2", "F0A3", "☐"), ("Wingdings 2", "F054", "☒"),
        ("Wingdings 2", "F050", "✓"), ("Wingdings 2", "F04F", "✗"),
        ("Wingdings", "F0FE", "☑"), ("Wingdings", "F0A8", "☐"), ("Wingdings", "F0FD", "☒"), ("Wingdings", "F0FC", "✓"),
        ("Wingdings", "F06F", "□"), ("Wingdings", "F0E0", "→"), ("Wingdings", "F046", "☞"), ("Wingdings", "F028", "☎"),
        ("Wingdings", "F02A", "✉"), ("Wingdings", "F081", "①"),
        ("Wingdings 3", "F075", "▶"), ("Webdings", "F061", "✔"),
        ("Symbol", "F061", "α"), ("Symbol", "F057", "Ω"), ("Symbol", "F0B3", "≥"), ("Symbol", "F0B7", "•"),
    ];

    private async Task<string> ConvertAsync(DocxBuilder docx, string target = "hwpx")
    {
        var source = docx.Save(Path.Combine(fx.NewFolder(), "symbols.docx"));
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => f.ErrorDetail)));
        return job.Outputs.Single();
    }

    private static DocxBuilder SymbolDocument()
    {
        var docx = new DocxBuilder();
        foreach (var (font, code, _) in Symbols)
            docx.Paragraph(P(R($"{font} {code}: ") + Sym(font, code)));
        return docx;
    }

    [Fact]
    public async Task Inserted_symbols_become_the_characters_they_show()
    {
        var hwpx = await ConvertAsync(SymbolDocument());
        ValidPackage(hwpx, 0);
        var section = Xml(hwpx);
        foreach (var (font, code, text) in Symbols)
            Assert.EndsWith(text, ParagraphWith(section, $"{font} {code}: ").Value.Trim());
        Assert.DoesNotContain("●", section.Value);
        Assert.DoesNotContain("◆", section.Value);
    }

    [Fact]
    public async Task An_unknown_symbol_keeps_the_old_placeholder()
    {
        var hwpx = await ConvertAsync(new DocxBuilder().Paragraph(P(R("bomb: ") + Sym("Wingdings", "F04D"))));
        Assert.EndsWith("●", ParagraphWith(Xml(hwpx), "bomb: ").Value.Trim());
    }

    [Fact]
    public async Task Text_typed_in_a_symbol_font_is_drawn_in_the_text_font()
    {
        // "þ" in Wingdings is ☑, "R" in Wingdings 2 too, "a" in Symbol α.
        var docx = new DocxBuilder().Paragraph(P(
            R("þ", "<w:rFonts w:ascii=\"Wingdings\" w:hAnsi=\"Wingdings\"/>") +
            R("R", "<w:rFonts w:ascii=\"Wingdings 2\" w:hAnsi=\"Wingdings 2\"/>") +
            R("a", "<w:rFonts w:ascii=\"Symbol\" w:hAnsi=\"Symbol\"/>") +
            R(" done")));
        var hwpx = await ConvertAsync(docx);
        Assert.Contains("☑☑α done", Xml(hwpx).Value);
        // The characters carry the paragraph's text font: no run asks for a symbol font any more.
        Assert.DoesNotContain("Wingdings", Xml(hwpx, "Contents/header.xml").ToString());
        Assert.DoesNotContain("Symbol\"", Xml(hwpx, "Contents/header.xml").ToString());
    }

    [Theory]
    [InlineData("Wingdings", "", "☐")] // Word's check-list bullet, ◆ before #350
    [InlineData("Wingdings", "", "☑")]
    [InlineData("Wingdings", "", "▪")]
    [InlineData("Wingdings", "", "➢")]
    [InlineData("Symbol", "", "♦")]
    [InlineData("Symbol", "", "•")]
    [InlineData("Wingdings 2", "", "☐")]
    [InlineData(null, "", "◆")] // no font: the usual Symbol bullets as before
    public async Task List_bullets_follow_their_symbol_font(string? font, string bullet, string expected)
    {
        var rPr = font is null ? "" : $"<w:rPr><w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:hint=\"default\"/></w:rPr>";
        var docx = new DocxBuilder
        {
            Numbering = $"<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullet}\"/>{rPr}</w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num>",
        };
        docx.Paragraph(P(R("item"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        var head = Xml(await ConvertAsync(docx), "Contents/header.xml");
        Assert.Equal(expected, head.Descendants(Hh + "numbering").Last().Elements(Hh + "paraHead").First().Value);
    }

    [Fact]
    public async Task Slide_bullets_and_text_in_symbol_fonts_become_their_characters()
    {
        var pptx = new PptxBuilder();
        pptx.Slide(
            "<p:sp><p:nvSpPr><p:cNvPr id=\"20\" name=\"List\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"838200\"/><a:ext cx=\"6000000\" cy=\"3000000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
            "<p:txBody><a:bodyPr/><a:lstStyle/>" +
            "<a:p><a:pPr marL=\"228600\" indent=\"-228600\"><a:buFont typeface=\"Wingdings\" pitchFamily=\"2\" charset=\"2\"/><a:buChar char=\"§\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>square item</a:t></a:r></a:p>" +
            "<a:p><a:pPr marL=\"228600\" indent=\"-228600\"><a:buFont typeface=\"Wingdings\" pitchFamily=\"2\" charset=\"2\"/><a:buChar char=\"ü\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>checked item</a:t></a:r></a:p>" +
            "<a:p><a:r><a:rPr lang=\"en-US\"><a:latin typeface=\"Wingdings 2\"/></a:rPr><a:t>R</a:t></a:r><a:r><a:rPr lang=\"en-US\"/><a:t> agreed</a:t></a:r></a:p>" +
            "</p:txBody></p:sp>");
        var source = pptx.Save(Path.Combine(fx.NewFolder(), "symbols.pptx"));
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "hwpx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => f.ErrorDetail)));
        var text = Xml(job.Outputs.Single()).Value;
        Assert.Contains("▪ square item", text);
        Assert.Contains("✓ checked item", text);
        Assert.Contains("☑ agreed", text);
    }

    [Fact]
    public async Task Rhwp_draws_the_check_boxes_in_the_pdf()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");

        var pdf = await ConvertAsync(SymbolDocument(), "pdf");
        using var document = PdfDocument.Open(pdf);
        var letters = document.GetPages().SelectMany(p => p.Letters).ToList();
        var text = string.Concat(letters.Select(l => l.Value));
        foreach (var symbol in new[] { "☑", "☐", "☒", "✓", "✗", "α", "≥" })
            Assert.Contains(symbol, text);
        Assert.DoesNotContain("●", text);
        // Drawn with a real font, not as an empty box: every check box letter has a glyph with some width.
        Assert.All(letters.Where(l => l.Value is "☑" or "☐" or "☒"), l => Assert.True(l.Width > 0, $"{l.Value} in {l.FontName}"));
    }

    [Fact]
    public void Tables_map_both_code_forms_and_ignore_other_fonts()
    {
        Assert.Equal("☑", SymbolFonts.Map("Wingdings 2", 0xF052));
        Assert.Equal("☑", SymbolFonts.Map("wingdings2", 0x52));
        Assert.Equal("☐", SymbolFonts.Map("WINGDINGS", ""));
        Assert.Null(SymbolFonts.Map("Wingdings", 0xF04D)); // a bomb: no Unicode character of the plane we draw
        Assert.Null(SymbolFonts.Map("Calibri", 0xF052));
        Assert.Null(SymbolFonts.Map(null, 0xF052));
        Assert.Null(SymbolFonts.Map("Wingdings", "þM"));
        Assert.True(SymbolFonts.IsSymbolFont("Webdings"));
        Assert.False(SymbolFonts.IsSymbolFont("Segoe UI Symbol"));
    }
}
