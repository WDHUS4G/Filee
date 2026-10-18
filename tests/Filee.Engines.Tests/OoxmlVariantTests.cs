// Office Open XML variants (macro-enabled files, templates, slide shows) without Office: the package is copied with
// the main part's content type rewritten, and macros are dropped when the target may not hold them. The built-in
// readers accept the variants directly (DOCM → HWPX, XLSM → CSV, PPSX → DOCX ...).

using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class OoxmlVariantTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string VbaRelationship = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>
    /// Turns a macro-free package into the macro-enabled variant <paramref name="format"/>: main content type, a VBA
    /// project (with its data part, as Word keeps it) related from the main part, and for Excel an Excel 4.0 macro sheet.
    /// </summary>
    private static string AddMacros(string package, string format, string folder)
    {
        var path = Path.ChangeExtension(package, "." + format);
        File.Copy(package, path, overwrite: true);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var main = XDocument.Load(zip.GetEntry("_rels/.rels")!.Open()).Root!.Elements(Rels + "Relationship")
            .Single(r => ((string)r.Attribute("Type")!).EndsWith("/officeDocument", StringComparison.Ordinal)).Attribute("Target")!.Value.TrimStart('/');
        var mainFolder = main[..main.LastIndexOf('/')];
        var relsPath = $"{mainFolder}/_rels/{Path.GetFileName(main)}.rels";

        var types = Load(zip, "[Content_Types].xml");
        types.Root!.Elements(Types + "Override").Single(o => (string)o.Attribute("PartName")! == "/" + main)
            .SetAttributeValue("ContentType", OoxmlConverter.Variants[format].ContentType);
        types.Root.AddFirst(new XElement(Types + "Default", new XAttribute("Extension", "bin"), new XAttribute("ContentType", "application/vnd.ms-office.vbaProject")));
        types.Root.Add(new XElement(Types + "Override", new XAttribute("PartName", $"/{mainFolder}/vbaData.xml"), new XAttribute("ContentType", "application/vnd.ms-word.vbaData+xml")));

        var rels = zip.GetEntry(relsPath) is { } existing ? Load(zip, relsPath) : new XDocument(new XElement(Rels + "Relationships"));
        rels.Root!.Add(new XElement(Rels + "Relationship", new XAttribute("Id", "rIdVba"), new XAttribute("Type", VbaRelationship), new XAttribute("Target", "vbaProject.bin")));
        Write(zip, $"{mainFolder}/vbaProject.bin", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        Write(zip, $"{mainFolder}/_rels/vbaProject.bin.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/office/2006/relationships/wordVbaData\" Target=\"vbaData.xml\"/></Relationships>"u8.ToArray());
        Write(zip, $"{mainFolder}/vbaData.xml", "<wne:vbaSuppData xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"/>"u8.ToArray());

        if (format == "xlsm")
        {
            // An Excel 4.0 macro sheet, listed in the workbook like a sheet.
            types.Root.Add(new XElement(Types + "Override", new XAttribute("PartName", "/xl/macrosheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.ms-excel.macrosheet+xml")));
            rels.Root.Add(new XElement(Rels + "Relationship", new XAttribute("Id", "rIdMacro"), new XAttribute("Type", "http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet"), new XAttribute("Target", "macrosheets/sheet1.xml")));
            Write(zip, "xl/macrosheets/sheet1.xml", "<xm:macrosheet xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\"/>"u8.ToArray());
            var workbook = Load(zip, "xl/workbook.xml");
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            workbook.Root!.Element(s + "sheets")!.Add(new XElement(s + "sheet", new XAttribute("name", "Macro1"), new XAttribute("sheetId", 99), new XAttribute(r + "id", "rIdMacro")));
            Save(zip, "xl/workbook.xml", workbook);
        }
        Save(zip, "[Content_Types].xml", types);
        Save(zip, relsPath, rels);
        return path;
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static void Save(ZipArchive zip, string name, XDocument xml)
    {
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        xml.Save(stream);
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    /// <summary>No macro part, no content type or relationship pointing to one.</summary>
    private static void AssertNoMacros(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.DoesNotContain(zip.Entries, e => e.Name is "vbaProject.bin" or "vbaData.xml" or "vbaProject.bin.rels" || e.FullName.Contains("macrosheets", StringComparison.Ordinal));
        var types = Load(zip, "[Content_Types].xml").ToString();
        Assert.DoesNotContain("vbaProject", types);
        Assert.DoesNotContain("macrosheet", types);
        foreach (var rels in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.DoesNotContain(Load(zip, rels.FullName).Root!.Elements(Rels + "Relationship"), r => ((string)r.Attribute("Type")!).StartsWith("http://schemas.microsoft.com/office/2006/relationships/", StringComparison.Ordinal) && ((string)r.Attribute("Type")!).Contains("Macro", StringComparison.OrdinalIgnoreCase) || (string)r.Attribute("Type")! == VbaRelationship);
    }

    private static string MainContentType(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var main = XDocument.Load(zip.GetEntry("_rels/.rels")!.Open()).Root!.Elements(Rels + "Relationship")
            .Single(r => ((string)r.Attribute("Type")!).EndsWith("/officeDocument", StringComparison.Ordinal)).Attribute("Target")!.Value.TrimStart('/');
        return (string)Load(zip, "[Content_Types].xml").Root!.Elements(Types + "Override").Single(o => (string)o.Attribute("PartName")! == "/" + main).Attribute("ContentType")!;
    }

    [Fact]
    public void Variants_are_converted_by_the_ooxml_engine_and_read_directly()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var (from, to) in new[] { ("docm", "docx"), ("dotx", "docx"), ("docx", "dotm"), ("xlsm", "xlsx"), ("xlsx", "xltx"), ("pptm", "pptx"), ("potx", "pptx"), ("ppsx", "pptx"), ("pptx", "ppsx") })
            Assert.Equal("ooxml", Assert.Single(planner.Plan(from, to)!.Steps).Converter.Id);
        // Families never mix.
        Assert.DoesNotContain(fx.Converters.Single(c => c.Id == "ooxml").Edges, e => OoxmlConverter.Variants[e.From].Family != OoxmlConverter.Variants[e.To].Family);
        foreach (var from in new[] { "docm", "dotx", "dotm", "xlsm", "xltx", "pptm", "potx", "ppsx" })
            Assert.Equal("hwpx-writer", Assert.Single(planner.Plan(from, "hwpx")!.Steps).Converter.Id);
        Assert.Equal("spreadsheet", Assert.Single(planner.Plan("xlsm", "csv")!.Steps).Converter.Id);
        Assert.Equal("spreadsheet", Assert.Single(planner.Plan("xltx", "csv")!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Docm_to_docx_drops_the_macros_and_keeps_the_document()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder().Paragraph(P(R("매크로가 있던 문서"))).Save(Path.Combine(dir, "보고.docx"));
        var docm = AddMacros(docx, "docm", dir);
        File.Delete(docx);

        var converted = await ConvertAsync(docm, "docx");

        Assert.Equal(".docx", Path.GetExtension(converted));
        Assert.Equal(OoxmlConverter.Variants["docx"].ContentType, MainContentType(converted));
        AssertNoMacros(converted);
        using (var document = WordprocessingDocument.Open(converted, false))
            Assert.Equal(WordprocessingDocumentType.Document, document.DocumentType);
        Assert.Equal("매크로가 있던 문서", DocxAssert.TextOf((Hwp.Hwpx.HParagraph)DocxAssert.ReadBack(converted).Sections[0].Blocks[0]));

        // Macro-enabled to macro-enabled keeps them; the readers take the variant directly.
        var dotm = await ConvertAsync(docm, "dotm");
        Assert.Equal(OoxmlConverter.Variants["dotm"].ContentType, MainContentType(dotm));
        using (var zip = ZipFile.OpenRead(dotm))
            Assert.NotNull(zip.GetEntry("word/vbaProject.bin"));
        HwpxAssert.ValidPackage(await ConvertAsync(docm, "hwpx"));
    }

    [Fact]
    public async Task Templates_and_documents_turn_into_each_other()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder().Paragraph(P(R("서식 파일"))).Save(Path.Combine(dir, "서식.docx"));

        var dotx = await ConvertAsync(docx, "dotx");
        using (var template = WordprocessingDocument.Open(dotx, false))
            Assert.Equal(WordprocessingDocumentType.Template, template.DocumentType);
        File.Delete(docx);
        var back = await ConvertAsync(dotx, "docx");
        Assert.Equal(OoxmlConverter.Variants["docx"].ContentType, MainContentType(back));
        DocxAssert.ValidPackage(back);
    }

    [Fact]
    public async Task Xlsm_to_xlsx_removes_vba_and_macro_sheets()
    {
        var dir = fx.NewFolder();
        var builder = new XlsxBuilder();
        builder.Sheet("매출", XlsxBuilder.Row(1, builder.Text("A1", "지점"), XlsxBuilder.Number("B1", 42)));
        var xlsx = builder.Save(Path.Combine(dir, "매출.xlsx"));
        var xlsm = AddMacros(xlsx, "xlsm", dir);
        File.Delete(xlsx);

        var converted = await ConvertAsync(xlsm, "xlsx");

        AssertNoMacros(converted);
        using (var workbook = SpreadsheetDocument.Open(converted, false))
        {
            Assert.Equal(SpreadsheetDocumentType.Workbook, workbook.DocumentType);
            Assert.Equal(["매출"], workbook.WorkbookPart!.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(s => s.Name!.Value));
        }
        Assert.Equal("42", XlsxReader.Read(converted).Sheets.Single().Get(0, 1)!.Text);

        // XLSM → CSV directly.
        var csv = await ConvertAsync(xlsm, "csv");
        Assert.Equal("지점,42", (await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken)).Trim('﻿', '\r', '\n'));

        var xltx = await ConvertAsync(converted, "xltx");
        using (var template = SpreadsheetDocument.Open(xltx, false))
            Assert.Equal(SpreadsheetDocumentType.Template, template.DocumentType);
    }

    [Fact]
    public async Task Presentation_variants_become_pptx()
    {
        var dir = fx.NewFolder();
        var pptx = new PptxBuilder();
        pptx.Slide(pptx.Title("슬라이드 쇼"));
        var source = pptx.Save(Path.Combine(dir, "발표.pptx"));
        var pptm = AddMacros(source, "pptm", dir);
        var ppsx = await ConvertAsync(source, "ppsx");
        File.Delete(source);

        var fromShow = await ConvertAsync(ppsx, "pptx");
        using (var presentation = PresentationDocument.Open(fromShow, false))
            Assert.Equal(PresentationDocumentType.Presentation, presentation.DocumentType);
        using (var show = PresentationDocument.Open(ppsx, false))
            Assert.Equal(PresentationDocumentType.Slideshow, show.DocumentType);

        File.Delete(fromShow);
        var fromMacros = await ConvertAsync(pptm, "pptx");
        AssertNoMacros(fromMacros);
        using (var presentation = PresentationDocument.Open(fromMacros, false))
        {
            Assert.Equal(PresentationDocumentType.Presentation, presentation.DocumentType);
            var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(presentation, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(errors, e => e.Description.Contains("vba", StringComparison.OrdinalIgnoreCase));
        }

        var docx = await ConvertAsync(ppsx, "docx");
        DocxAssert.ValidPackage(docx);
    }
}
