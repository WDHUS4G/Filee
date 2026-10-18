// Shared checks for DOCX files written by Filee: the Open XML SDK schema validator (the element order Word enforces),
// package structure, and an independent reader (LibreOffice) that must convert the file without error.

using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Tests;

internal static class DocxAssert
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>
    /// The package is what Word expects: schema-valid parts (element order included), every part with a content type,
    /// every relationship target present, every r:id resolvable in its part, unique drawing ids.
    /// </summary>
    public static void ValidPackage(string docx)
    {
        using (var document = WordprocessingDocument.Open(docx, false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
                .ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        using var zip = ZipFile.OpenRead(docx);
        var types = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open()).Root!;
        var overrides = types.Elements(Types + "Override").Select(o => ((string)o.Attribute("PartName")!).TrimStart('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaults = types.Elements(Types + "Default").Select(d => (string)d.Attribute("Extension")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(e => e.FullName != "[Content_Types].xml"))
            Assert.True(overrides.Contains(entry.FullName) || defaults.Contains(Path.GetExtension(entry.FullName).TrimStart('.')), $"{entry.FullName} has no content type");
        Assert.All(overrides, part => Assert.NotNull(zip.GetEntry(part)));

        foreach (var rels in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = rels.FullName[..rels.FullName.IndexOf("_rels/", StringComparison.Ordinal)];
            var owner = folder + Path.GetFileName(rels.FullName)[..^".rels".Length];
            var relationships = XDocument.Load(rels.Open()).Root!.Elements(Rels + "Relationship").ToList();
            foreach (var rel in relationships.Where(r => (string?)r.Attribute("TargetMode") != "External"))
                Assert.NotNull(zip.GetEntry(folder + (string)rel.Attribute("Target")!));
            if (owner.Length > 0 && zip.GetEntry(owner) is { } part && owner.EndsWith(".xml", StringComparison.Ordinal))
            {
                var ids = relationships.Select(r => (string)r.Attribute("Id")!).ToHashSet();
                var used = XDocument.Load(part.Open()).Descendants().SelectMany(e => e.Attributes().Where(a => a.Name.Namespace == R)).Select(a => a.Value);
                Assert.All(used, id => Assert.Contains(id, ids));
            }
        }

        var drawingIds = zip.Entries.Where(e => e.FullName.StartsWith("word/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .SelectMany(e => XDocument.Load(e.Open()).Descendants(Wp + "docPr").Select(d => (string)d.Attribute("id")!))
            .ToList();
        Assert.Equal(drawingIds.Count, drawingIds.Distinct().Count());
    }

    /// <summary>word/document.xml.</summary>
    public static XElement Body(string docx, string part = "word/document.xml")
    {
        using var zip = ZipFile.OpenRead(docx);
        return XDocument.Load(zip.GetEntry(part)!.Open(), LoadOptions.PreserveWhitespace).Root!;
    }

    /// <summary>The file read back with Filee's own DOCX reader.</summary>
    public static HDocument ReadBack(string docx) => DocxReader.Read(docx, Path.Combine(Path.GetDirectoryName(docx)!, "readback-media"));

    /// <summary>Plain text of a paragraph (links included).</summary>
    public static string TextOf(HParagraph paragraph) =>
        string.Concat(paragraph.Inlines.SelectMany(i => i is HLink link ? link.Content : [i]).OfType<HText>().Select(t => t.Text));

    /// <summary>All paragraphs of the body, in tables and text boxes too.</summary>
    public static IEnumerable<HParagraph> Paragraphs(IEnumerable<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    yield return paragraph;
                    foreach (var box in paragraph.Inlines.OfType<HTextBox>())
                    {
                        foreach (var inner in Paragraphs(box.Blocks))
                            yield return inner;
                    }
                    break;
                case HTable table:
                    foreach (var inner in Paragraphs(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks)))
                        yield return inner;
                    break;
            }
        }
    }

    /// <summary>
    /// Converts the file to PDF with LibreOffice (an independent DOCX reader) when it is installed.
    /// </summary>
    /// <returns>Number of PDF pages, or null when LibreOffice is not available.</returns>
    public static async Task<int?> ConvertibleByLibreOfficeAsync(EngineFixture fx, string docx)
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        if (!fx.Catalog.StatusOf(libreOffice).IsAvailable || !OperatingSystem.IsWindows())
            return null;
        var pdf = Path.ChangeExtension(docx, ".lo.pdf");
        await HwpxAssert.LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var written = await libreOffice.ConvertAsync(new ConversionStep(docx, "docx", "pdf", new Preset(), new HwpxAssert.FixedPath(pdf), Path.GetDirectoryName(docx)!),
                null, TestContext.Current.CancellationToken);
            Assert.True(new FileInfo(written.Single()).Length > 1000);
        }
        finally
        {
            HwpxAssert.LibreOfficeGate.Release();
        }
        return PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken));
    }
}
