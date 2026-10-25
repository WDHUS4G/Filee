// What Filee fixes in an HWPX copy before rhwp renders it (issue #65: an exam paper written in 한글 2022; issue #49:
// a table floating in a table cell of an HWP file).

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public class RhwpInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    private static readonly XNamespace Hs = "http://www.hancom.co.kr/hwpml/2011/section";

    // The form of #49: in a table cell taller than its text, "Title", a paragraph holding only a table that floats 500
    // below the paragraph's top (three rows of 2000, outer margins 141) and "After". Paragraphs are 2800 apart.
    private const int Step = 2800, Offset = 500, RowHeight = 2000, Rows = 3, Margin = 141, OuterHeight = 30_000;
    private const int AnchorTop = Step;
    private const int UnderTable = AnchorTop + Offset + Margin + Rows * RowHeight + Margin; // where 한글 stores the line

    [Theory]
    [InlineData("f ^{2} = f CIRCf", "f ^{2} = f CIRC f")]
    [InlineData("n(A SMALLINTERB)", "n(A SMALLINTER B)")]
    [InlineData("A SUBSETB", "A SUBSET B")]
    [InlineData("1 lek le28", "1 le k le 28")]
    [InlineData("rootx + rootxy", "root x + root xy")]
    [InlineData("P CUP Q!=U", "P CUP Q NEQ U")]
    [InlineData("4 +- root 6", "4  PLUSMINUS  root 6")]
    public void Commands_glued_to_the_next_letter_are_spaced(string script, string expected)
    {
        Assert.Equal(expected, RhwpInput.NormaliseEquation(script));
    }

    [Theory]
    [InlineData("left ( x right ) leq 3")]
    [InlineData("index line inside lemma")]  // words, not commands
    [InlineData("\"CIRCf != x\" + y")]       // quoted text is literal
    [InlineData("x SUBSETEQ y")]
    public void Commands_and_words_that_are_whole_stay(string script)
    {
        Assert.Equal(script, RhwpInput.NormaliseEquation(script));
    }

    [Fact]
    public void Shapes_sized_relative_to_the_column_get_their_laid_out_size()
    {
        // A "보기" box inside a text box: 9920 means 99.2 % of the column, which rhwp read as 9920 HWPUNIT.
        XNamespace hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
        var section = new XDocument(new XElement(hp + "sec",
            new XElement(hp + "rect",
                new XElement(hp + "sz", new XAttribute("width", 9920), new XAttribute("widthRelTo", "COLUMN"), new XAttribute("height", 3206), new XAttribute("heightRelTo", "ABSOLUTE")),
                new XElement(hp + "curSz", new XAttribute("width", 23053), new XAttribute("height", 24841))),
            new XElement(hp + "pic",
                new XElement(hp + "sz", new XAttribute("width", 5000), new XAttribute("widthRelTo", "ABSOLUTE"), new XAttribute("height", 4000), new XAttribute("heightRelTo", "ABSOLUTE")),
                new XElement(hp + "curSz", new XAttribute("width", 6000), new XAttribute("height", 4800)))));

        Assert.True(RhwpInput.FixShapeSizes(section));

        var sizes = section.Descendants(hp + "sz").ToList();
        Assert.Equal(("23053", "24841", "ABSOLUTE", "ABSOLUTE"),
            ((string)sizes[0].Attribute("width")!, (string)sizes[0].Attribute("height")!, (string)sizes[0].Attribute("widthRelTo")!, (string)sizes[0].Attribute("heightRelTo")!));
        Assert.Equal("5000", (string)sizes[1].Attribute("width")!); // absolute sizes are left alone
        Assert.False(RhwpInput.FixShapeSizes(section));             // nothing left to fix
    }

    [Fact]
    public void A_table_floating_in_a_cell_is_drawn_from_the_top_of_its_paragraph()
    {
        var section = new XDocument(new XElement(Hs + "sec", Paragraph(0, Text("Before"), OuterTable(Anchor()))));

        Assert.True(RhwpInput.AnchorTablesInCells(section));

        Assert.Equal([0, AnchorTop, UnderTable + Step], CellLines(section));
        Assert.Equal(0, LineOf(section, "Before")); // the paragraph holding the outer table keeps its line
        Assert.False(RhwpInput.AnchorTablesInCells(section)); // nothing left to fix
    }

    [Fact]
    public void Paragraphs_with_text_tables_in_line_and_tables_outside_cells_keep_their_lines()
    {
        XDocument Cell(XElement anchor) => new(new XElement(Hs + "sec", Paragraph(0, OuterTable(anchor))));

        var withText = Cell(Anchor(text: "next to the table"));        // rhwp would draw the text over the table
        var inLine = Cell(Anchor(treatAsChar: true));                   // a table in the line is laid out as text
        var notUnder = Cell(Anchor(line: AnchorTop + Offset));          // the line is not under the table
        var outside = new XDocument(new XElement(Hs + "sec", Paragraph(0, Text("Title")), Anchor(), Paragraph(UnderTable + Step, Text("After"))));

        Assert.All(new[] { withText, inLine, notUnder, outside }, section => Assert.False(RhwpInput.AnchorTablesInCells(section)));
        Assert.Equal(UnderTable, LineOf(outside, "InnerA", anchor: true));
    }

    [Fact]
    public async Task Only_an_hwpx_rhwp_converted_from_hwp_gets_its_tables_moved()
    {
        var dir = fx.NewFolder();
        var fromHwp = await MakeFormAsync(dir, fromHwp: true);
        var saved = await MakeFormAsync(dir, fromHwp: false);
        static InstalledFont? AllFonts(string face) => new(true, _ => true);

        // A Hancom-saved HWPX is laid out right by rhwp: nothing to change, no copy.
        Assert.Null(RhwpInput.Prepare(saved, dir, AllFonts));

        var copy = RhwpInput.Prepare(fromHwp, dir, AllFonts);
        Assert.NotNull(copy);
        Assert.Equal(AnchorTop, LineOf(Section(copy), "InnerA", anchor: true));
        Assert.Equal(UnderTable, LineOf(Section(fromHwp), "InnerA", anchor: true)); // the original is untouched
        using var zip = ZipFile.OpenRead(copy);
        Assert.NotNull(zip.GetEntry(RhwpInput.Hwp5OriginMarker)); // rhwp still reads the rest like the HWP it came from
    }

    [Fact]
    public async Task The_table_floating_in_a_cell_stays_above_the_next_paragraph_in_the_pdf()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var dir = fx.NewFolder();
        var hwpx = await MakeFormAsync(dir, fromHwp: true);
        var pdf = Path.Combine(dir, "form.pdf");

        await rhwp.ConvertAsync(new ConversionStep(hwpx, "hwpx", "pdf", new Preset(), new HwpxAssert.FixedPath(pdf), dir), null, TestContext.Current.CancellationToken);

        using var document = PdfDocument.Open(pdf);
        var words = document.GetPage(1).GetWords().ToDictionary(w => w.Text, w => w.BoundingBox);
        // PDF coordinates grow upwards. Before the fix the table started where "After" is, its last row below it.
        Assert.True(words["Title"].Bottom > words["InnerA"].Top, "the table starts below the title");
        Assert.True(words["InnerC"].Bottom > words["After"].Top, "the table ends above the next paragraph");
    }

    [Fact]
    public void Cambria_math_draws_equations_only_when_no_symbol_font_is_installed()
    {
        static InstalledFont Font() => new(true, _ => true);
        Assert.Equal(["--equation-font", "Cambria Math"], RhwpInput.PdfOptions(name => name == "Cambria Math" ? Font() : null));
        Assert.Empty(RhwpInput.PdfOptions(name => name is "Cambria Math" or "Arial Unicode MS" ? Font() : null));
        Assert.Empty(RhwpInput.PdfOptions(_ => null));
    }

    [Fact]
    public void Installed_generic_fonts_are_passed_to_rhwp_as_pdf_fallbacks()
    {
        static InstalledFont? Lookup(string name) => name is "Noto Serif" or "Noto Sans" or "Noto Sans Mono"
            ? new InstalledFont(true, _ => true)
            : null;

        Assert.Equal([
            "--fallback-serif", "Noto Serif",
            "--fallback-sans", "Noto Sans",
            "--fallback-mono", "Noto Sans Mono",
        ], RhwpInput.PdfOptions(Lookup));
    }

    /// <summary>
    /// The #49 form in a package of Filee's HWPX writer: "Before", then the tall outer table. With
    /// <paramref name="fromHwp"/> it carries the marker rhwp's export-hwpx writes.
    /// </summary>
    private async Task<string> MakeFormAsync(string dir, bool fromHwp)
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir, fromHwp ? "from-hwp" : "saved")).FullName;
        var hwpx = await fx.MakeHwpxAsync(folder);
        using var zip = ZipFile.Open(hwpx, ZipArchiveMode.Update);
        var entry = zip.GetEntry("Contents/section0.xml")!;
        XDocument section;
        using (var stream = entry.Open())
            section = XDocument.Load(stream);
        var setup = section.Root!.Element(Hp + "p")!.Elements(Hp + "run").First(r => r.Element(Hp + "secPr") is not null);
        section.Root.ReplaceNodes(
            new XElement(Hp + "p", ParagraphAttributes(), setup, Run(Text("Before")), Lines(0)),
            new XElement(Hp + "p", ParagraphAttributes(), Run(OuterTable(Anchor())), Lines(Step, OuterHeight + 2 * Margin)));
        entry.Delete();
        using (var stream = zip.CreateEntry("Contents/section0.xml").Open())
            section.Save(stream, SaveOptions.DisableFormatting);
        if (fromHwp)
            using (var writer = new StreamWriter(zip.CreateEntry(RhwpInput.Hwp5OriginMarker).Open()))
                writer.Write("2:paragraph-utf16");
        return hwpx;
    }

    /// <summary>A one-cell table in the line, as tall as a form's cell: "Title", <paramref name="anchor"/>, "After".</summary>
    private static XElement OuterTable(XElement anchor) =>
        Table(40_000, OuterHeight, treatAsChar: true, 0,
            [[Paragraph(0, Text("Title")), anchor, Paragraph(UnderTable + Step, Text("After"))]]);

    /// <summary>A paragraph holding the floating table, with the line where 한글 stores it unless given.</summary>
    private static XElement Anchor(string? text = null, bool treatAsChar = false, int line = UnderTable)
    {
        var rows = new[] { "InnerA", "InnerB", "InnerC" }.Select(t => new[] { Paragraph(0, Text(t)) }).ToArray();
        return Paragraph(line, Table(30_000, RowHeight, treatAsChar, Offset, rows), text is null ? null : Text(text));
    }

    /// <summary>A one-column table, its rows <paramref name="rowHeight"/> high, placed relative to its paragraph.</summary>
    private static XElement Table(int width, int rowHeight, bool treatAsChar, int vertOffset, XElement[][] rows) =>
        new(Hp + "tbl",
            new XAttribute("id", 7), new XAttribute("zOrder", 0), new XAttribute("numberingType", "TABLE"),
            new XAttribute("textWrap", "TOP_AND_BOTTOM"), new XAttribute("textFlow", "BOTH_SIDES"), new XAttribute("lock", 0),
            new XAttribute("dropcapstyle", "None"), new XAttribute("pageBreak", "CELL"), new XAttribute("repeatHeader", 1),
            new XAttribute("rowCnt", rows.Length), new XAttribute("colCnt", 1), new XAttribute("cellSpacing", 0),
            new XAttribute("borderFillIDRef", 2), new XAttribute("noAdjust", 0),
            new XElement(Hp + "sz", new XAttribute("width", width), new XAttribute("widthRelTo", "ABSOLUTE"),
                new XAttribute("height", rows.Length * rowHeight), new XAttribute("heightRelTo", "ABSOLUTE"), new XAttribute("protect", 0)),
            new XElement(Hp + "pos", new XAttribute("treatAsChar", treatAsChar ? 1 : 0), new XAttribute("affectLSpacing", 0),
                new XAttribute("flowWithText", 1), new XAttribute("allowOverlap", 0), new XAttribute("holdAnchorAndSO", 0),
                new XAttribute("vertRelTo", "PARA"), new XAttribute("horzRelTo", "COLUMN"), new XAttribute("vertAlign", "TOP"),
                new XAttribute("horzAlign", "LEFT"), new XAttribute("vertOffset", vertOffset), new XAttribute("horzOffset", 0)),
            Margins("outMargin"), Margins("inMargin"),
            rows.Select((paragraphs, row) => new XElement(Hp + "tr", new XElement(Hp + "tc",
                new XAttribute("name", ""), new XAttribute("header", 0), new XAttribute("hasMargin", 0), new XAttribute("protect", 0),
                new XAttribute("editable", 0), new XAttribute("dirty", 0), new XAttribute("borderFillIDRef", 2),
                new XElement(Hp + "subList", new XAttribute("id", ""), new XAttribute("textDirection", "HORIZONTAL"),
                    new XAttribute("lineWrap", "BREAK"), new XAttribute("vertAlign", "TOP"), new XAttribute("linkListIDRef", 0),
                    new XAttribute("linkListNextIDRef", 0), new XAttribute("textWidth", 0), new XAttribute("textHeight", 0),
                    new XAttribute("hasTextRef", 0), new XAttribute("hasNumRef", 0), paragraphs),
                new XElement(Hp + "cellAddr", new XAttribute("colAddr", 0), new XAttribute("rowAddr", row)),
                new XElement(Hp + "cellSpan", new XAttribute("colSpan", 1), new XAttribute("rowSpan", 1)),
                new XElement(Hp + "cellSz", new XAttribute("width", width), new XAttribute("height", rowHeight)),
                Margins("cellMargin")))));

    private static XElement Margins(string name) =>
        new(Hp + name, new XAttribute("left", Margin), new XAttribute("right", Margin), new XAttribute("top", Margin), new XAttribute("bottom", Margin));

    private static XElement Paragraph(int line, params object?[] content) => new(Hp + "p", ParagraphAttributes(), Run(content), Lines(line));

    private static XElement Run(params object?[] content) => new(Hp + "run", new XAttribute("charPrIDRef", 0), content);

    private static object[] ParagraphAttributes() =>
    [
        new XAttribute("id", 0), new XAttribute("paraPrIDRef", 0), new XAttribute("styleIDRef", 0),
        new XAttribute("pageBreak", 0), new XAttribute("columnBreak", 0), new XAttribute("merged", 0),
    ];

    private static XElement Text(string text) => new(Hp + "t", text);

    /// <summary>One stored line: 1000 high (charPr 0 is 10 pt) with 800 below it (180 % line spacing).</summary>
    private static XElement Lines(int vertpos, int height = 1000) =>
        new(Hp + "linesegarray", new XElement(Hp + "lineseg",
            new XAttribute("textpos", 0), new XAttribute("vertpos", vertpos), new XAttribute("vertsize", height),
            new XAttribute("textheight", height), new XAttribute("baseline", height * 85 / 100), new XAttribute("spacing", 800),
            new XAttribute("horzpos", 0), new XAttribute("horzsize", 45_130), new XAttribute("flags", 393_216)));

    /// <summary>Stored line of the paragraph with <paramref name="text"/>, or of the paragraph anchoring its table.</summary>
    private static int LineOf(XDocument section, string text, bool anchor = false)
    {
        var paragraph = section.Descendants(Hp + "p").Last(p => p.Elements(Hp + "run").Elements(Hp + "t").Any(t => t.Value == text));
        if (anchor)
            paragraph = paragraph.Ancestors(Hp + "p").First();
        return (int)paragraph.Element(Hp + "linesegarray")!.Element(Hp + "lineseg")!.Attribute("vertpos")!;
    }

    /// <summary>Stored lines of the paragraphs in the outer table's cell.</summary>
    private static List<int> CellLines(XDocument section) =>
        section.Descendants(Hp + "subList").First().Elements(Hp + "p")
            .Select(p => (int)p.Element(Hp + "linesegarray")!.Element(Hp + "lineseg")!.Attribute("vertpos")!).ToList();

    private static XDocument Section(string hwpx)
    {
        using var zip = ZipFile.OpenRead(hwpx);
        using var stream = zip.GetEntry("Contents/section0.xml")!.Open();
        return XDocument.Load(stream);
    }
}
