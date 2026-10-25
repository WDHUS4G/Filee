// A Chinese Word document (issue #237): legacy WordArt, indents in characters, numbering turned off with numId 0,
// CJK punctuation in the East Asian font, and the punctuation widths rhwp guesses for Chinese fonts.

using System.Xml.Linq;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxChineseLayoutTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string WordArt = """
        <w:r><w:rPr><w:b/><w:color w:val="FF0000"/><w:sz w:val="32"/><w:u w:val="single"/></w:rPr><w:pict>
          <v:shape id="_x0000_i1025" type="#_x0000_t136" style="height:28.8pt;width:420pt;" fillcolor="#FF0000" filled="t" stroked="t">
            <v:textpath on="t" fitshape="t" fitpath="t" trim="t" string="111111111111111111111" style="font-family:宋体;font-size:28pt;font-weight:bold;"/>
            <w10:wrap type="none"/>
          </v:shape>
        </w:pict></w:r>
        """;

    private const string ChineseStyles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Times New Roman" w:hAnsi="Times New Roman" w:eastAsia="宋体"/></w:rPr></w:rPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/>
          <w:pPr><w:ind w:firstLine="420"/></w:pPr><w:rPr><w:sz w:val="21"/></w:rPr></w:style>
        """;

    private HDocument Read(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
    }

    private static List<HParagraph> Paragraphs(HDocument document) => document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();

    [Fact]
    public void Legacy_WordArt_keeps_its_text_in_its_font_colour_and_about_its_size()
    {
        var document = Read(new DocxBuilder().Paragraph(P(WordArt, "<w:jc w:val=\"center\"/>")));

        var text = Assert.IsType<HText>(Assert.Single(Paragraphs(document)[0].Inlines));
        Assert.Equal("111111111111111111111", text.Text);
        Assert.Equal(("宋体", "宋体", "#FF0000", true, true), (text.Format.Font, text.Format.EastAsianFont, text.Format.Color, text.Format.Bold, text.Format.Underline));
        // Stretched over 420 × 28.8 pt: 21 half-width digits make about 40 pt.
        Assert.InRange(text.Format.Size!.Value, 3800, 4100);
    }

    [Fact]
    public void Floating_WordArt_becomes_a_borderless_text_box_where_it_was()
    {
        var floating = WordArt.Replace("style=\"height:28.8pt;width:420pt;\"",
            "style=\"position:absolute;margin-left:10pt;margin-top:20pt;height:36pt;width:200pt;mso-position-horizontal-relative:margin;z-index:2\"", StringComparison.Ordinal);
        var document = Read(new DocxBuilder().Paragraph(P(floating)));

        var box = Assert.IsType<HTextBox>(Assert.Single(Paragraphs(document)[0].Inlines));
        Assert.Equal(HBorder.None, box.Line);
        Assert.Equal((1000, 2000), (box.Anchor!.HorizontalOffset, box.Anchor.VerticalOffset));
        var paragraph = Assert.IsType<HParagraph>(Assert.Single(box.Blocks));
        Assert.Equal("111111111111111111111", Assert.IsType<HText>(Assert.Single(paragraph.Inlines)).Text);
    }

    [Fact]
    public void Indents_in_characters_use_the_paragraph_font_size()
    {
        var docx = new DocxBuilder { Styles = ChineseStyles };
        docx.Paragraph(P(R("左缩进"), "<w:ind w:leftChars=\"200\"/><w:rPr><w:sz w:val=\"21\"/></w:rPr>"))
            .Paragraph(P(R("首行"), "<w:ind w:firstLineChars=\"200\"/><w:rPr><w:sz w:val=\"24\"/></w:rPr>"))
            .Paragraph(P(R("悬挂"), "<w:ind w:left=\"840\" w:hangingChars=\"100\"/>"))
            .Paragraph(P(R("厘米"), "<w:ind w:left=\"720\" w:leftChars=\"0\" w:firstLine=\"0\" w:firstLineChars=\"0\"/>"));

        var formats = Paragraphs(Read(docx)).Select(p => p.Format).ToList();
        // 2 characters of 10.5 pt = 21 pt; the style's first-line indent (420 twips) stays where only the left changes.
        Assert.Equal((HwpxUnits.FromTwips(420), HwpxUnits.FromTwips(420)), (formats[0].Left, formats[0].FirstLine));
        Assert.Equal((0, HwpxUnits.FromTwips(480)), (formats[1].Left, formats[1].FirstLine));
        Assert.Equal((HwpxUnits.FromTwips(840), -HwpxUnits.FromTwips(210)), (formats[2].Left, formats[2].FirstLine));
        // leftChars="0" means the twips value counts.
        Assert.Equal((HwpxUnits.FromTwips(720), 0), (formats[3].Left, formats[3].FirstLine));
    }

    [Fact]
    public void Numbering_turned_off_drops_the_inherited_indents()
    {
        var docx = new DocxBuilder { Styles = ChineseStyles };
        docx.Paragraph(P(R("模型部署"), "<w:numPr><w:numId w:val=\"0\"/></w:numPr><w:ind w:leftChars=\"200\"/>"))
            .Paragraph(P(R("正文"), "<w:numPr><w:numId w:val=\"0\"/></w:numPr>"))
            .Paragraph(P(R("普通")));

        var formats = Paragraphs(Read(docx)).Select(p => (p.Format.Left, p.Format.FirstLine)).ToList();
        Assert.Equal((HwpxUnits.FromTwips(420), 0), formats[0]);
        Assert.Equal((0, 0), formats[1]);
        Assert.Equal((0, HwpxUnits.FromTwips(420)), formats[2]); // without numPr the style's indent stays
    }

    [Fact]
    public void Cjk_punctuation_is_written_in_the_East_Asian_font()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder { Styles = ChineseStyles }.Paragraph(P(R("“在线教育平台”、《标题》")));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);

        var header = Xml(hwpx, "Contents/header.xml");
        string Face(XElement fontRef, string slot) =>
            (string)header.Descendants(Hh + "fontface").Single(f => (string?)f.Attribute("lang") == slot.ToUpperInvariant())
                .Elements(Hh + "font").Single(f => (string?)f.Attribute("id") == (string?)fontRef.Attribute(slot))
                .Attribute("face")!;
        var run = RunWith(Xml(hwpx), "在线");
        var fontRef = HeadItem(hwpx, "charPr", (string)run.Attribute("charPrIDRef")!).Element(Hh + "fontRef")!;
        Assert.Equal(("宋体", "宋体", "Times New Roman"), (Face(fontRef, "symbol"), Face(fontRef, "hanja"), Face(fontRef, "latin")));
    }

    /// <summary>A Chinese font as rhwp's PDF draws it: full-width punctuation, no Hangul.</summary>
    private static InstalledFont? ChineseComputer(string face) => face switch
    {
        "宋体" => new InstalledFont(true, c => c != '가', c => c < 0x2000 ? 0.5 : 1.0),
        "Times New Roman" => new InstalledFont(true, c => c < 0x3000, c => 0.5),
        _ => null,
    };

    [Fact]
    public void Punctuation_rhwp_guesses_narrow_gets_spacing_up_to_its_width()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder { Styles = ChineseStyles }.Paragraph(P(R("“在线”、‘课’……Web。")));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);

        var copy = RhwpInput.Prepare(hwpx, dir, ChineseComputer);

        Assert.NotNull(copy);
        ValidPackage(copy);
        var paragraph = ParagraphWith(Xml(copy), "在线");
        var runs = paragraph.Elements(Hp + "run").Where(r => r.Element(Hp + "t") is not null).ToList();
        Assert.Equal("“在线”、‘课’……Web。", string.Concat(runs.Select(r => r.Element(Hp + "t")!.Value)));

        int Spacing(string text)
        {
            var run = runs.Single(r => r.Element(Hp + "t")!.Value == text);
            var spacing = HeadItem(copy, "charPr", (string)run.Attribute("charPrIDRef")!).Element(Hh + "spacing")!;
            return (int)spacing.Attribute("hangul")!;
        }
        // rhwp: “ ” … half a character, ‘ ’ 0.3, 、 。 half; the font: all full width.
        Assert.Equal(100, Spacing("“"));
        Assert.Equal(100, Spacing("”、")); // same shape: one run
        Assert.Equal(233, Spacing("‘"));
        Assert.Equal(100, Spacing("……"));
        Assert.Equal(100, Spacing("。"));
        Assert.Equal(0, Spacing("在线"));
        Assert.Equal(0, Spacing("Web"));
    }

    [Fact]
    public void Punctuation_in_Korean_and_Latin_fonts_is_left_to_rhwp()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder().Paragraph(P(R("“인용”、《제목》")));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);

        // A Korean font: rhwp has its widths.
        Assert.Null(RhwpInput.Prepare(hwpx, dir, _ => new InstalledFont(true, _ => true, _ => 1.0)));
    }
}
