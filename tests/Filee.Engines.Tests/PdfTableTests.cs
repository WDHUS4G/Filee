// Ruled tables in PDF → DOCX / HWPX: grids drawn with lines (or thin filled bars) become real tables with the PDF's
// column widths, merged cells, borders and shading, in reading order between the paragraphs around them. Boxes,
// underlines and empty grids stay what they were. The PDFs are drawn here with the standard 14 fonts.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using static Filee.Engines.Tests.DocxAssert;

namespace Filee.Engines.Tests;

public class PdfTableTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>Text in a cell: 4 pt from its left edge, on a baseline 14 pt below the row's top.</summary>
    private static void Cell(PdfBuilder pdf, string text, double left, double top, bool bold = false) =>
        pdf.Line(text, 10, pdf.Font(bold), left + 4, top + 14);

    /// <summary>
    /// A paragraph, a 3 × 3 table with every rule drawn as a stroked line (a bold header row), and a paragraph after
    /// it. Columns are 150, 150 and 195 pt wide, rows 20 pt high.
    /// </summary>
    private static string SimpleTable(string folder)
    {
        var pdf = new PdfBuilder().NewPage();
        var body = pdf.Font();
        pdf.Paragraph("The files converted this week are listed in the table below, with their format and size.", 11, body, 50, 80, 495, 14);
        double[] xs = [50, 200, 350, 545];
        double[] ys = [110, 130, 150, 170];
        foreach (var y in ys)
            pdf.Rule(xs[0], y, xs[^1], y);
        foreach (var x in xs)
            pdf.Rule(x, ys[0], x, ys[^1]);
        string[][] cells = [["Name", "Format", "Size"], ["Report", "PDF", "120 KB"], ["Photo", "JPEG", "2.4 MB"]];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                Cell(pdf, cells[r][c], xs[c], ys[r], bold: r == 0);
        }
        pdf.Paragraph("Everything was converted on this computer; nothing was uploaded anywhere.", 11, body, 50, 200, 495, 14);
        return pdf.Save(Path.Combine(folder, "table.pdf"));
    }

    private static List<HBlock> Blocks(HDocument document) =>
        [.. document.Sections.SelectMany(s => s.Blocks).Where(b => b is HTable || (b is HParagraph p && TextOf(p).Trim().Length > 0))];

    private static string CellText(HCell cell) => string.Join("\n", Paragraphs(cell.Blocks).Select(TextOf).Select(t => t.Trim()).Where(t => t.Length > 0));

    private static string[][] Texts(HTable table) => [.. table.Rows.Select(r => r.Cells.Select(CellText).ToArray())];

    [Fact]
    public async Task Ruled_table_becomes_a_word_table_between_its_paragraphs()
    {
        var docx = await ConvertAsync(SimpleTable(fx.NewFolder()), "docx");

        ValidPackage(docx);
        var blocks = Blocks(ReadBack(docx));
        Assert.Equal(3, blocks.Count);
        Assert.StartsWith("The files converted", TextOf(Assert.IsType<HParagraph>(blocks[0])).Trim());
        Assert.StartsWith("Everything was converted", TextOf(Assert.IsType<HParagraph>(blocks[2])).Trim());

        var table = Assert.IsType<HTable>(blocks[1]);
        Assert.Equal([["Name", "Format", "Size"], ["Report", "PDF", "120 KB"], ["Photo", "JPEG", "2.4 MB"]], Texts(table));
        // Column widths from the rules (points × 100), rows at least as high as in the PDF.
        Assert.Equal([15000, 15000, 19500], table.ColumnWidths!.Select(w => (int)Math.Round(w / 100.0) * 100));
        Assert.All(table.Rows, r => Assert.InRange(r.Height ?? 0, 1900, 2100));
        // Every cell has its four borders; the header keeps its bold font.
        Assert.All(table.Rows.SelectMany(r => r.Cells), c =>
        {
            var borders = c.Borders ?? table.Borders ?? HBorders.Grid;
            Assert.All(new[] { borders.Left, borders.Right, borders.Top, borders.Bottom }, b => Assert.Equal(HBorderStyle.Solid, b.Style));
        });
        var header = Paragraphs(table.Rows[0].Cells[0].Blocks).Single().Inlines.OfType<HText>().First();
        Assert.Equal((true, "Arial", 1000), (header.Format.Bold, header.Format.Font, header.Format.Size));

        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(1, pages);
    }

    [Fact]
    public async Task Ruled_table_reaches_hwpx_and_text_keeps_the_cells()
    {
        var path = SimpleTable(fx.NewFolder());

        var hwpx = await ConvertAsync(path, "hwpx");
        HwpxAssert.ValidPackage(hwpx);
        var model = HwpxReader.Read(hwpx, Path.Combine(Path.GetDirectoryName(hwpx)!, "readback-media"));
        var table = Assert.Single(model.Sections.SelectMany(s => s.Blocks).OfType<HTable>());
        Assert.Equal([["Name", "Format", "Size"], ["Report", "PDF", "120 KB"], ["Photo", "JPEG", "2.4 MB"]], Texts(table));
        HwpxAssert.RenderWithRhwp(fx, hwpx, "pdf-table");

        // PDF → TXT has no tables: the cells stay text in reading order.
        var text = await File.ReadAllTextAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken);
        Assert.True(text.IndexOf("Name", StringComparison.Ordinal) < text.IndexOf("2.4 MB", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task Missing_inner_rules_become_merged_cells()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        double[] xs = [50, 180, 310, 440];
        double[] ys = [100, 120, 140, 160];
        // Outer frame, the header's bottom rule, and the inner rules with the gaps that merge cells:
        // "Region and city" spans the first two columns, "North" the last two rows.
        pdf.Rule(50, 100, 440, 100).Rule(50, 160, 440, 160).Rule(50, 100, 50, 160).Rule(440, 100, 440, 160);
        pdf.Rule(50, 120, 440, 120);
        pdf.Rule(180, 140, 440, 140);
        pdf.Rule(180, 120, 180, 160);
        pdf.Rule(310, 100, 310, 160);
        Cell(pdf, "Region and city", xs[0], ys[0], bold: true);
        Cell(pdf, "Total", xs[2], ys[0], bold: true);
        Cell(pdf, "North", xs[0], ys[1]);
        Cell(pdf, "Seoul", xs[1], ys[1]);
        Cell(pdf, "10", xs[2], ys[1]);
        Cell(pdf, "Busan", xs[1], ys[2]);
        Cell(pdf, "20", xs[2], ys[2]);
        var docx = await ConvertAsync(pdf.Save(Path.Combine(dir, "merged.pdf")), "docx");

        ValidPackage(docx);
        var table = Assert.Single(ReadBack(docx).Sections.SelectMany(s => s.Blocks).OfType<HTable>());
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal([["Region and city", "Total"], ["North", "Seoul", "10"], ["Busan", "20"]], Texts(table));
        Assert.Equal([(2, 1), (1, 1)], table.Rows[0].Cells.Select(c => (c.ColSpan, c.RowSpan)));
        Assert.Equal([(1, 2), (1, 1), (1, 1)], table.Rows[1].Cells.Select(c => (c.ColSpan, c.RowSpan)));
        Assert.Equal([1, 2], table.Rows[2].Cells.Select(c => c.Column));
    }

    [Fact]
    public async Task Rules_drawn_as_bars_keep_their_weight_and_shading()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Line("Results", 14, pdf.Font(bold: true), 60, 80);
        // A shaded header row, a 2 pt frame and 0.5 pt inner rules, all filled rectangles.
        pdf.Bar(60, 100, 400, 22, (217, 217, 217));
        pdf.Bar(60, 99, 400, 2).Bar(60, 165, 400, 2).Bar(59, 100, 2, 66).Bar(459, 100, 2, 66);
        pdf.Bar(60, 121.75, 400, 0.5).Bar(60, 143.75, 400, 0.5);
        pdf.Bar(199.75, 100, 0.5, 66).Bar(329.75, 100, 0.5, 66);
        string[][] cells = [["Test", "Before", "After"], ["Speed", "12 s", "4 s"], ["Memory", "800 MB", "300 MB"]];
        double[] xs = [60, 200, 330];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
                Cell(pdf, cells[r][c], xs[c], 100 + 22 * r, bold: r == 0);
        }
        var docx = await ConvertAsync(pdf.Save(Path.Combine(dir, "bars.pdf")), "docx");

        ValidPackage(docx);
        var table = Assert.Single(ReadBack(docx).Sections.SelectMany(s => s.Blocks).OfType<HTable>());
        Assert.Equal(cells, Texts(table));
        var corner = table.Rows[0].Cells[0];
        var inner = table.Rows[1].Cells[1];
        Assert.True(corner.Borders!.Value.Top.WidthMm > 2 * inner.Borders!.Value.Top.WidthMm, $"{corner.Borders.Value.Top.WidthMm} vs {inner.Borders.Value.Top.WidthMm}");
        Assert.InRange(corner.Borders.Value.Left.WidthMm, 0.5, 1);
        Assert.Equal("#D9D9D9", corner.Fill);
        Assert.Null(inner.Fill);
    }

    [Fact]
    public async Task Boxes_underlines_and_empty_grids_stay_text()
    {
        var dir = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        var body = pdf.Font();
        // A note in a box with a rule under its title.
        pdf.Rule(50, 70, 545, 70).Rule(50, 140, 545, 140).Rule(50, 70, 50, 140).Rule(545, 70, 545, 140);
        pdf.Line("Note", 11, pdf.Font(bold: true), 60, 88);
        pdf.Rule(50, 95, 545, 95);
        pdf.Paragraph("A box drawn around a note is no table, even with a rule under its title line.", 11, body, 60, 112, 470, 14);
        // Underlined words and a separator.
        pdf.Line("Important words", 11, body, 50, 170);
        pdf.Rule(50, 172, 50 + pdf.Width("Important words", 11, body), 172);
        pdf.Rule(50, 190, 545, 190, 1);
        // An empty chart grid with its labels outside.
        foreach (var y in new double[] { 220, 260, 300 })
            pdf.Rule(100, y, 400, y);
        foreach (var x in new double[] { 100, 250, 400 })
            pdf.Rule(x, 220, x, 300);
        pdf.Line("Chart axis label", 9, body, 200, 315);
        var docx = await ConvertAsync(pdf.Save(Path.Combine(dir, "boxes.pdf")), "docx");

        ValidPackage(docx);
        var blocks = ReadBack(docx).Sections.SelectMany(s => s.Blocks).ToList();
        Assert.Empty(blocks.OfType<HTable>());
        var texts = blocks.OfType<HParagraph>().Select(p => TextOf(p).Trim()).Where(t => t.Length > 0).ToList();
        Assert.Equal(["Note", "A box drawn around a note is no table, even with a rule under its title line.", "Important words", "Chart axis label"], texts);
    }
}
