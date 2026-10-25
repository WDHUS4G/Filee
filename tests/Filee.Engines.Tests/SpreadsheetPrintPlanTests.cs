using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.XlsxBuilder;

namespace Filee.Engines.Tests;

public class SpreadsheetPrintPlanTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Inspector_reads_real_content_and_the_workbooks_print_settings()
    {
        var folder = Directory.CreateTempSubdirectory("filee-print-plan-").FullName;
        try
        {
            var builder = new XlsxBuilder();
            builder.Sheet("Hidden", Row(1, builder.Text("A1", "x")), hidden: true);
            builder.Sheet("4 course",
                Row(1, builder.Text("A1", "name"), builder.Text("EI1", "last")) +
                "<row r=\"72\" hidden=\"1\"><c r=\"A72\" s=\"1\"/></row>",
                before: "<cols><col min=\"3\" max=\"4\" hidden=\"1\"/></cols>",
                after: "<pageMargins left=\"0.25\" right=\"0.25\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>" +
                       "<pageSetup paperSize=\"9\" orientation=\"landscape\" scale=\"10\" pageOrder=\"overThenDown\"/>");
            var path = builder.Save(Path.Combine(folder, "courses.xlsx"));
            AddDefinedNames(path,
                "<definedName name=\"_xlnm.Print_Area\" localSheetId=\"1\">'4 course'!$A$1:$EI$154</definedName>" +
                "<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"1\">'4 course'!$1:$2</definedName>");

            var workbook = SpreadsheetPrintInspector.Read(path);

            Assert.Equal(2, workbook.Sheets.Count);
            var sheet = workbook.Sheets[1];
            Assert.Equal("A1:EI1", sheet.UsedRange); // the styled empty A72 does not enlarge real content
            Assert.Equal("A1:EI154", sheet.PrintArea);
            Assert.Equal("1:2", sheet.RepeatRows);
            Assert.Equal(139, sheet.UsedColumns);
            Assert.Equal(1, sheet.HiddenRows);
            Assert.Equal(2, sheet.HiddenColumns);
            Assert.Equal(SpreadsheetPaper.A4, sheet.PageSetup.Paper);
            Assert.Equal(SpreadsheetPageOrientation.Landscape, sheet.PageSetup.Orientation);
            Assert.Equal(10, sheet.PageSetup.ScalePercent);
            Assert.Equal(SpreadsheetPageOrder.OverThenDown, sheet.PageSetup.Order);
            Assert.True(sheet.HasSuspiciousScale);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Temporary_copy_changes_only_the_chosen_regions_print_layout()
    {
        var folder = Directory.CreateTempSubdirectory("filee-print-copy-").FullName;
        try
        {
            var builder = new XlsxBuilder();
            builder.Sheet("First", Row(1, builder.Text("A1", "leave me")),
                before: "<sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews>");
            builder.Sheet("Plan's page", Row(1, builder.Text("A1", "show me")) +
                "<row r=\"2\" hidden=\"1\"><c r=\"A2\" t=\"inlineStr\"><is><t>hidden</t></is></c></row>",
                before: "<sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews>",
                after: "<rowBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"2\" max=\"16383\" man=\"1\"/></rowBreaks>");
            var source = builder.Save(Path.Combine(folder, "source.xlsx"));
            AddWorkbookView(source);
            var original = File.ReadAllBytes(source);
            var book = SpreadsheetPrintInspector.Read(source);
            var selected = book.Sheets[1];
            var plan = SpreadsheetPrintRegion.FromSource(selected) with
            {
                Range = "A1:C20",
                Paper = SpreadsheetPaper.A3,
                Orientation = SpreadsheetPageOrientation.Landscape,
                ScaleMode = SpreadsheetScaleMode.FitToPages,
                FitWidth = 2,
                FitHeight = 0,
                Order = SpreadsheetPageOrder.DownThenOver,
                MarginLeftMm = 8,
                MarginRightMm = 8,
                MarginTopMm = 10,
                MarginBottomMm = 10,
                IncludeHidden = true,
                KeepManualBreaks = false,
                WritePrintArea = true,
            };
            var output = Path.Combine(folder, "temporary.xlsx");

            SpreadsheetPrintPackage.Create(source, output, book, plan);

            Assert.Equal(original, File.ReadAllBytes(source));
            using var zip = ZipFile.OpenRead(output);
            var workbook = Xml(zip, "xl/workbook.xml").Root!;
            var sheets = workbook.Element(S + "sheets")!.Elements(S + "sheet").ToList();
            Assert.Equal(2, sheets.Count);
            Assert.Equal("hidden", (string?)sheets[0].Attribute("state"));
            Assert.Equal("Plan's page", (string?)sheets[1].Attribute("name"));
            Assert.Null(sheets[1].Attribute("state"));
            var view = workbook.Element(S + "bookViews")!.Element(S + "workbookView")!;
            Assert.Equal("1", (string?)view.Attribute("activeTab"));
            Assert.Equal("1", (string?)view.Attribute("firstSheet"));
            var area = workbook.Element(S + "definedNames")!.Elements(S + "definedName").Single();
            Assert.Equal("'Plan''s page'!$A$1:$C$20", area.Value);
            Assert.Equal("1", (string?)area.Attribute("localSheetId"));

            var first = Xml(zip, "xl/worksheets/sheet1.xml").Root!;
            Assert.All(first.Element(S + "sheetViews")?.Elements(S + "sheetView") ?? [],
                sheetView => Assert.Null(sheetView.Attribute("tabSelected")));

            var sheet = Xml(zip, "xl/worksheets/sheet2.xml").Root!;
            Assert.Equal("1", (string?)sheet.Element(S + "sheetViews")!.Element(S + "sheetView")!.Attribute("tabSelected"));
            Assert.Null(sheet.Element(S + "sheetData")!.Elements(S + "row").Last().Attribute("hidden"));
            Assert.Null(sheet.Element(S + "rowBreaks"));
            Assert.Equal("8", (string?)sheet.Element(S + "pageSetup")!.Attribute("paperSize"));
            Assert.Equal("landscape", (string?)sheet.Element(S + "pageSetup")!.Attribute("orientation"));
            Assert.Equal("2", (string?)sheet.Element(S + "pageSetup")!.Attribute("fitToWidth"));
            Assert.Equal("0", (string?)sheet.Element(S + "pageSetup")!.Attribute("fitToHeight"));
            Assert.Equal("downThenOver", (string?)sheet.Element(S + "pageSetup")!.Attribute("pageOrder"));
            Assert.Equal("1", (string?)sheet.Element(S + "sheetPr")!.Element(S + "pageSetUpPr")!.Attribute("fitToPage"));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("A1", true)]
    [InlineData("$A$1:$S$52", true)]
    [InlineData("A1:S52,T1:Z80", true)]
    [InlineData("A:H", true)]
    [InlineData("1:40", true)]
    [InlineData("A0:S52", false)]
    [InlineData("XFE1", false)]
    [InlineData("A1048577", false)]
    [InlineData("not a range", false)]
    public void Print_ranges_are_validated(string range, bool valid) =>
        Assert.Equal(valid, SpreadsheetPrintInspector.IsValidRange(range));

    [Fact]
    public void Temporary_copy_keeps_cross_sheet_formulas_and_named_ranges()
    {
        var folder = Directory.CreateTempSubdirectory("filee-print-formula-").FullName;
        try
        {
            var builder = new XlsxBuilder();
            builder.Sheet("Inputs", Row(1, XlsxBuilder.Number("A1", 21)),
                before: "<sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews>");
            builder.Sheet("Report", "<row r=\"1\"><c r=\"A1\"><f>Inputs!A1*3</f></c>" +
                "<c r=\"B1\"><f>Rate*10</f></c></row>",
                before: "<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            var source = builder.Save(Path.Combine(folder, "formula.xlsx"));
            AddWorkbookView(source);
            AddDefinedNames(source,
                "<definedName name=\"Rate\">Inputs!$A$1</definedName>" +
                "<definedName name=\"LocalReport\" localSheetId=\"1\">Report!$A$1</definedName>");
            var book = SpreadsheetPrintInspector.Read(source);
            var output = Path.Combine(folder, "temporary.xlsx");

            SpreadsheetPrintPackage.Create(source, output, book, SpreadsheetPrintRegion.FromSource(book.Sheets[1]));

            using var zip = ZipFile.OpenRead(output);
            var workbook = Xml(zip, "xl/workbook.xml").Root!;
            var sheets = workbook.Element(S + "sheets")!.Elements(S + "sheet").ToList();
            Assert.Equal(2, sheets.Count);
            Assert.Equal("hidden", (string?)sheets[0].Attribute("state"));
            Assert.Null(sheets[1].Attribute("state"));
            var names = workbook.Element(S + "definedNames")!.Elements(S + "definedName").ToList();
            Assert.Contains(names, name => (string?)name.Attribute("name") == "Rate" && name.Value == "Inputs!$A$1");
            Assert.Contains(names, name => (string?)name.Attribute("name") == "LocalReport"
                && (string?)name.Attribute("localSheetId") == "1");
            Assert.Equal(["Inputs!A1*3", "Rate*10"],
                Xml(zip, "xl/worksheets/sheet2.xml").Descendants(S + "f").Select(formula => formula.Value));
            Assert.Null(workbook.Element(S + "definedNames")!.Elements(S + "definedName")
                .FirstOrDefault(name => (string?)name.Attribute("name") == "_xlnm.Print_Area"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Inspector_handles_a_comma_in_the_sheet_name()
    {
        var folder = Directory.CreateTempSubdirectory("filee-print-comma-").FullName;
        try
        {
            var builder = new XlsxBuilder();
            var source = builder.Sheet("Sales, West", Row(1, builder.Text("A1", "x")))
                .Save(Path.Combine(folder, "comma.xlsx"));
            AddDefinedNames(source,
                "<definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">'Sales, West'!$A:$H,'Sales, West'!$1:$40</definedName>");

            var sheet = Assert.Single(SpreadsheetPrintInspector.Read(source).Sheets);

            Assert.Equal("A:H,1:40", sheet.PrintArea);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task LibreOffice_round_trip_recalculates_a_cross_sheet_named_formula()
    {
        var folder = Directory.CreateTempSubdirectory("filee-print-lo-").FullName;
        try
        {
            var libreOffice = new LibreOfficeConverter(new EngineEnvironment(Path.Combine(folder, "data")));
            Assert.SkipUnless(libreOffice.GetStatus().IsAvailable, "LibreOffice not found");
            var builder = new XlsxBuilder();
            builder.Sheet("Inputs", Row(1, XlsxBuilder.Number("A1", 21)));
            builder.Sheet("Report", "<row r=\"1\"><c r=\"A1\"><f>Inputs!A1*3</f></c>" +
                "<c r=\"B1\"><f>Rate*10</f></c></row>");
            var source = builder.Save(Path.Combine(folder, "formula.xlsx"));
            AddWorkbookView(source);
            AddDefinedNames(source, "<definedName name=\"Rate\">Inputs!$A$1</definedName>");
            var book = SpreadsheetPrintInspector.Read(source);
            var temporary = Path.Combine(folder, "temporary.xlsx");
            SpreadsheetPrintPackage.Create(source, temporary, book,
                SpreadsheetPrintRegion.FromSource(book.Sheets[1]) with { Range = "A1:B1", WritePrintArea = true });
            var pdf = Path.Combine(folder, "formula.pdf");

            var results = await libreOffice.ConvertAsync(new ConversionStep(temporary, "xlsx", "pdf",
                new Preset { TargetFormat = "pdf" }, new ExactOutput(pdf), folder), null,
                TestContext.Current.CancellationToken);

            Assert.Single(results);
            Assert.True(File.Exists(pdf));
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var text = document.GetPage(1).Text;
            Assert.Contains("63", text);
            Assert.Contains("210", text);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Pdf_preview_preserves_the_page_shape()
    {
        var folder = Directory.CreateTempSubdirectory("filee-sheet-preview-").FullName;
        try
        {
            var path = Path.Combine(folder, "mixed.pdf");
            using (var pdf = new PdfSharp.Pdf.PdfDocument())
            {
                var portrait = pdf.AddPage();
                portrait.Width = PdfSharp.Drawing.XUnit.FromMillimeter(210);
                portrait.Height = PdfSharp.Drawing.XUnit.FromMillimeter(297);
                var landscape = pdf.AddPage();
                landscape.Width = PdfSharp.Drawing.XUnit.FromMillimeter(297);
                landscape.Height = PdfSharp.Drawing.XUnit.FromMillimeter(210);
                pdf.Save(path);
            }

            var preview = Filee.Engines.Pdf.PdfPreviewDocument.Open(path);
            var pages = preview.RenderThumbnails(400, TestContext.Current.CancellationToken);

            Assert.Equal(2, pages.Count);
            Assert.True(pages[0].Height > pages[0].Width);
            Assert.True(pages[1].Width > pages[1].Height);
            Assert.All(pages, page => Assert.Equal(400, Math.Max(page.Width, page.Height)));
            Assert.All(pages, page => Assert.NotEmpty(page.Png));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static XDocument Xml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    private static void AddDefinedNames(string path, string names)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/workbook.xml")!;
        XDocument document;
        using (var stream = entry.Open())
            document = XDocument.Load(stream);
        document.Root!.Element(S + "sheets")!.AddAfterSelf(XElement.Parse($"<definedNames xmlns=\"{S}\">{names}</definedNames>"));
        entry.Delete();
        using var output = zip.CreateEntry("xl/workbook.xml").Open();
        document.Save(output);
    }

    private static void AddWorkbookView(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/workbook.xml")!;
        XDocument document;
        using (var stream = entry.Open())
            document = XDocument.Load(stream);
        document.Root!.Element(S + "sheets")!.AddBeforeSelf(
            new XElement(S + "bookViews", new XElement(S + "workbookView",
                new XAttribute("activeTab", 0), new XAttribute("firstSheet", 0))));
        entry.Delete();
        using var output = zip.CreateEntry("xl/workbook.xml").Open();
        document.Save(output);
    }

    private sealed class ExactOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
