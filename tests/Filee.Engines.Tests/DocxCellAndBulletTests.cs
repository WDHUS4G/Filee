// DOCX → HWPX fidelity regressions: per-cell padding overrides table defaults, and list glyphs retain their shape.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxCellAndBulletTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<string> ConvertAsync(DocxBuilder docx)
    {
        var source = docx.Save(Path.Combine(fx.NewFolder(), "source.docx"));
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "hwpx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => f.ErrorDetail)));
        var output = job.Outputs.Single();
        ValidPackage(output, 0);
        return output;
    }

    private static (int Left, int Right, int Top, int Bottom) Insets(XElement element) =>
        ((int)element.Attribute("left")!, (int)element.Attribute("right")!, (int)element.Attribute("top")!, (int)element.Attribute("bottom")!);

    private static string Cell(string text, string properties = "") =>
        $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(R(text))}</w:tc>";

    [Fact]
    public async Task Cell_margins_override_only_the_specified_sides_and_preserve_zero()
    {
        var docx = new DocxBuilder().Paragraph("""
            <w:tbl><w:tblPr><w:tblCellMar>
              <w:left w:w="120" w:type="dxa"/><w:right w:w="140" w:type="dxa"/>
              <w:top w:w="40" w:type="dxa"/><w:bottom w:w="60" w:type="dxa"/>
            </w:tblCellMar></w:tblPr><w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid><w:tr>
            """ + Cell("custom", "<w:tcMar><w:start w:w=\"180\" w:type=\"dxa\"/><w:top w:w=\"85\" w:type=\"dxa\"/></w:tcMar>") +
            Cell("zero", "<w:tcMar><w:end w:w=\"60\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/></w:tcMar>") +
            Cell("inherited") + "</w:tr></w:tbl>" + P(""));

        var table = Xml(await ConvertAsync(docx)).Descendants(Hp + "tbl").Single();
        var cells = table.Descendants(Hp + "tc").ToList();
        Assert.Equal((600, 700, 200, 300), Insets(table.Element(Hp + "inMargin")!));
        Assert.Equal((900, 700, 425, 300), Insets(cells[0].Element(Hp + "cellMargin")!));
        Assert.Equal((600, 300, 200, 0), Insets(cells[1].Element(Hp + "cellMargin")!));
        Assert.Equal((600, 700, 200, 300), Insets(cells[2].Element(Hp + "cellMargin")!));
        Assert.Equal(["1", "1", "0"], cells.Select(c => (string?)c.Attribute("hasMargin")));
    }

    [Fact]
    public async Task Cell_style_and_header_margins_are_inherited_before_direct_overrides()
    {
        var docx = new DocxBuilder();
        docx.Styles += """
            <w:style w:type="table" w:styleId="Padded"><w:name w:val="Padded"/>
              <w:tblPr><w:tblCellMar><w:left w:w="100"/><w:right w:w="120"/><w:top w:w="20"/><w:bottom w:w="40"/></w:tblCellMar></w:tblPr>
              <w:tcPr><w:tcMar><w:top w:w="60"/></w:tcMar></w:tcPr>
              <w:tblStylePr w:type="firstRow"><w:tcPr><w:tcMar><w:bottom w:w="80"/></w:tcMar></w:tcPr></w:tblStylePr>
            </w:style>
            """;
        docx.Paragraph("<w:tbl><w:tblPr><w:tblStyle w:val=\"Padded\"/><w:tblLook w:firstRow=\"1\"/></w:tblPr>" +
            "<w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr>" +
            Cell("header", "<w:tcMar><w:top w:type=\"nil\"/></w:tcMar>") + "</w:tr><w:tr>" + Cell("body") + "</w:tr></w:tbl>" + P(""));

        var cells = Xml(await ConvertAsync(docx)).Descendants(Hp + "tc").ToList();
        Assert.Equal((500, 600, 0, 400), Insets(cells[0].Element(Hp + "cellMargin")!));
        Assert.Equal((500, 600, 300, 200), Insets(cells[1].Element(Hp + "cellMargin")!));
        Assert.All(cells, c => Assert.Equal("1", (string?)c.Attribute("hasMargin")));
    }

    [Fact]
    public async Task Cached_row_height_includes_cell_padding()
    {
        var docx = new DocxBuilder().Paragraph("<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr>" +
            Cell("text", "<w:tcMar><w:top w:w=\"1440\"/><w:bottom w:w=\"1440\"/></w:tcMar>") + "</w:tr></w:tbl>" + P(""));
        var cell = Xml(await ConvertAsync(docx)).Descendants(Hp + "tc").Single();
        Assert.True((int)cell.Element(Hp + "cellSz")!.Attribute("height")! >= 15700,
            "One 10 pt line plus two 72 pt cell margins must fit in the cached row height.");
    }

    [Fact]
    public async Task Header_cell_margins_inherit_each_side_from_the_base_table_style()
    {
        var docx = new DocxBuilder();
        docx.Styles += """
            <w:style w:type="table" w:styleId="Base"><w:name w:val="Base"/>
              <w:tblPr><w:tblCellMar><w:left w:w="100"/><w:right w:w="120"/><w:top w:w="20"/><w:bottom w:w="40"/></w:tblCellMar></w:tblPr>
              <w:tblStylePr w:type="firstRow"><w:tcPr><w:tcMar><w:top w:w="60"/></w:tcMar></w:tcPr></w:tblStylePr>
            </w:style>
            <w:style w:type="table" w:styleId="Derived"><w:name w:val="Derived"/><w:basedOn w:val="Base"/>
              <w:tblStylePr w:type="firstRow"><w:tcPr><w:tcMar><w:bottom w:w="80"/></w:tcMar></w:tcPr></w:tblStylePr>
            </w:style>
            """;
        docx.Paragraph("<w:tbl><w:tblPr><w:tblStyle w:val=\"Derived\"/><w:tblLook w:firstRow=\"1\"/></w:tblPr>" +
            "<w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr>" + Cell("header") + "</w:tr></w:tbl>" + P(""));

        var cell = Xml(await ConvertAsync(docx)).Descendants(Hp + "tc").Single();
        Assert.Equal((500, 600, 300, 400), Insets(cell.Element(Hp + "cellMargin")!));
    }

    [Theory]
    [InlineData("•", "•")]
    [InlineData("▪", "▪")]
    [InlineData("●", "●")]
    [InlineData("–", "–")]
    [InlineData("\uF0B7", "•")]
    [InlineData("\uF09F", "•")]
    [InlineData("\uF0A7", "▪")]
    [InlineData("\uF06E", "■")]
    public async Task List_markers_preserve_unicode_and_normalize_known_symbol_glyphs(string input, string expected)
    {
        var docx = new DocxBuilder
        {
            Numbering = $"<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{input}\"/></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num>",
        };
        docx.Paragraph(P(R("item"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        var head = Xml(await ConvertAsync(docx), "Contents/header.xml");
        var numbering = head.Descendants(Hh + "numbering").Last();
        Assert.Equal(expected, numbering.Elements(Hh + "paraHead").First().Value);
    }

    [Fact]
    public async Task Nested_table_uses_the_outer_cells_content_width()
    {
        var inner = "<w:tbl><w:tblPr/><w:tr>" + Cell("inner") + "</w:tr></w:tbl>";
        var docx = new DocxBuilder().Paragraph("<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"4000\"/></w:tblGrid><w:tr>" +
            "<w:tc><w:tcPr><w:tcMar><w:left w:w=\"600\"/><w:right w:w=\"400\"/></w:tcMar></w:tcPr>" + inner + P("") +
            "</w:tc></w:tr></w:tbl>" + P(""));

        var outer = Xml(await ConvertAsync(docx)).Descendants(Hp + "tbl").First();
        var nested = outer.Descendants(Hp + "tbl").Single();
        // 4000 twips of cell width minus 600 + 400 twips of cell padding, at 5 HWPUNIT per twip.
        Assert.Equal(15000, (int)nested.Element(Hp + "sz")!.Attribute("width")!);
    }
}
