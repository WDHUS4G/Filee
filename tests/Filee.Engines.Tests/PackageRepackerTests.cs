// Issue #141: LibreOffice refuses some ZIP containers that other readers open (Tencent Docs exports), saying only
// "source file could not be loaded". It gets a second try with the same parts repacked by PackageRepacker; the
// built-in readers take such files as they are.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.QuirkyZip.Quirks;

namespace Filee.Engines.Tests;

public class PackageRepackerTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Every quirk at once, the parts in reverse order ([Content_Types].xml last).</summary>
    private const QuirkyZip.Quirks Everything = DirectoryEntries | DataDescriptors | Stored | Gaps | DuplicateName | Reversed;

    private string Docx(string folder, string text, string name = "report.docx")
    {
        var docx = new DocxBuilder();
        var picture = docx.Image(Png());
        docx.Paragraph(DocxBuilder.P(DocxBuilder.R(text))).Paragraph(DocxBuilder.P(DocxBuilder.InlinePicture(picture, 914400, 914400)));
        return docx.Save(Path.Combine(folder, name));
    }

    private static byte[] Png()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Teal, 16, 16);
        return image.ToByteArray(ImageMagick.MagickFormat.Png);
    }

    private static string Odt(string path, string text)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var stream = zip.CreateEntry(name, level).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        Write("mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        Write("META-INF/manifest.xml",
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
        Write("content.xml",
            "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">" +
            $"<office:body><office:text><text:p>{text}</text:p></office:text></office:body></office:document-content>");
        return path;
    }

    private static string Text(HDocument document) =>
        string.Concat(DocxAssert.Paragraphs(document.Sections.SelectMany(s => s.Blocks)).Select(DocxAssert.TextOf));

    [Fact]
    public void Repacking_keeps_every_part_in_a_plain_container()
    {
        var folder = fx.NewFolder();
        var plain = Docx(folder, "Tencent office");
        var quirky = QuirkyZip.Rewrite(plain, Path.Combine(folder, "quirky.docx"), Everything);
        var before = SHA256.HashData(File.ReadAllBytes(quirky));

        var repacked = Path.Combine(fx.NewFolder(), "quirky.docx");
        PackageRepacker.Repack(quirky, repacked);

        // The same parts with the same content (the second copy of a name dropped)...
        var expected = QuirkyZip.Parts(plain).ToDictionary(p => p.Name, p => p.Data);
        var parts = QuirkyZip.Parts(repacked);
        Assert.Equal(expected.Keys.Order(), parts.Select(p => p.Name).Order());
        Assert.All(parts, p => Assert.Equal(expected[p.Name], p.Data));
        // ... in a plain container: [Content_Types].xml first, no folders, deflated, sizes in the local headers, no
        // data descriptors, extra fields or gaps.
        var entries = QuirkyZip.LocalEntries(repacked);
        Assert.Equal("[Content_Types].xml", entries[0].Name);
        Assert.Equal(parts.Count, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.False(e.Name.EndsWith('/'), e.Name);
            Assert.Equal(0, e.Flags & 0x0008);
            Assert.Equal(8, e.Method);
            Assert.Equal(0, e.ExtraLength);
            Assert.NotEqual(0u, e.Crc);
        });
        // The user's file is untouched.
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(quirky)));
    }

    [Fact]
    public void An_opendocument_file_gets_its_mimetype_first_and_uncompressed()
    {
        var folder = fx.NewFolder();
        var quirky = QuirkyZip.Rewrite(Odt(Path.Combine(folder, "plain.odt"), "Hello"), Path.Combine(folder, "letter.odt"), DataDescriptors | Reversed);
        Assert.NotEqual("mimetype", QuirkyZip.Parts(quirky)[0].Name);

        var repacked = Path.Combine(fx.NewFolder(), "letter.odt");
        PackageRepacker.Repack(quirky, repacked);

        var mimetype = QuirkyZip.LocalEntries(repacked)[0];
        Assert.Equal("mimetype", mimetype.Name);
        Assert.Equal(0, mimetype.Method);
        Assert.Equal(0, mimetype.ExtraLength);
        Assert.Equal("application/vnd.oasis.opendocument.text"u8.ToArray(), QuirkyZip.Parts(repacked)[0].Data);
    }

    [Fact]
    public void Only_office_packages_are_repacked()
    {
        var folder = fx.NewFolder();
        Assert.True(PackageRepacker.IsPackage(Docx(folder, "x")));
        Assert.True(PackageRepacker.IsPackage(Odt(Path.Combine(folder, "a.odt"), "x")));

        var zip = Path.Combine(folder, "notes.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("notes.txt");
        Assert.False(PackageRepacker.IsPackage(zip));
        var text = Path.Combine(folder, "fake.docx");
        File.WriteAllText(text, "not a zip");
        Assert.False(PackageRepacker.IsPackage(text));
    }

    [Fact]
    public void Built_in_readers_take_quirky_containers()
    {
        // DOCX / XLSX / PPTX / ODS → PDF, HWPX, CSV ... read the parts through System.IO.Compression, which goes by the
        // central directory: folders, data descriptors and gaps don't matter.
        const QuirkyZip.Quirks quirks = DirectoryEntries | DataDescriptors | Stored | Gaps | Reversed;
        var folder = fx.NewFolder();

        var docx = QuirkyZip.Rewrite(Docx(folder, "Hello Tencent"), Path.Combine(folder, "quirky.docx"), quirks);
        Assert.Contains("Hello Tencent", Text(DocxReader.Read(docx, Path.Combine(folder, "docx-media"))));
        var dotx = Path.Combine(folder, "quirky.dotx");
        OoxmlConverter.Rewrite(docx, dotx, "dotx");
        Assert.Contains("Hello Tencent", Text(DocxReader.Read(dotx, Path.Combine(folder, "dotx-media"))));

        var xlsx = new XlsxBuilder();
        xlsx.Sheet("Data", XlsxBuilder.Row(1, xlsx.Text("A1", "Tencent cell"), XlsxBuilder.Number("B1", 42)));
        var quirkyXlsx = QuirkyZip.Rewrite(xlsx.Save(Path.Combine(folder, "plain.xlsx")), Path.Combine(folder, "quirky.xlsx"), quirks);
        Assert.Equal("Tencent cell", XlsxReader.Read(quirkyXlsx).Sheets[0].Get(0, 0)?.Text);

        var pptx = new PptxBuilder();
        pptx.Slide(pptx.Title("Tencent slide"));
        var quirkyPptx = QuirkyZip.Rewrite(pptx.Save(Path.Combine(folder, "plain.pptx")), Path.Combine(folder, "quirky.pptx"), quirks);
        Assert.Contains("Tencent slide", Text(PptxReader.Read(quirkyPptx, Path.Combine(folder, "pptx-media"))));

        var book = new Workbook();
        book.Sheets.Add(new Worksheet("Data"));
        book.Sheets[0].Set(0, 0, new SheetCell("Tencent ods", CellStyle.Default));
        OdsWriter.Write(book, Path.Combine(folder, "plain.ods"));
        var ods = QuirkyZip.Rewrite(Path.Combine(folder, "plain.ods"), Path.Combine(folder, "quirky.ods"), quirks);
        Assert.Equal("Tencent ods", OdsReader.Read(ods).Sheets[0].Get(0, 0)?.Text);
    }

    [Theory]
    [InlineData(DirectoryEntries | DataDescriptors)]
    [InlineData(Stored | DataDescriptors)]
    [InlineData(Gaps)]
    [InlineData(DuplicateName)]
    public async Task LibreOffice_converts_a_container_it_refuses_from_a_repacked_copy(QuirkyZip.Quirks quirks)
    {
        // LibreOffice 26.2 fails to load each of these ("source file could not be loaded"), the repacked copy converts.
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        Assert.SkipUnless(fx.Catalog.StatusOf(libreOffice).IsAvailable, "LibreOffice not installed");
        var folder = fx.NewFolder();
        var docx = QuirkyZip.Rewrite(Docx(folder, "Hello Tencent", "plain.docx"), Path.Combine(folder, "腾讯文档.docx"), quirks);
        var before = SHA256.HashData(File.ReadAllBytes(docx));

        var (odt, repacked) = await ConvertAsync(libreOffice, docx, "docx", "odt");

        Assert.True(repacked, "LibreOffice loaded the file the first time");
        using (var zip = ZipFile.OpenRead(odt))
            Assert.Contains("Hello Tencent", new StreamReader(zip.GetEntry("content.xml")!.Open()).ReadToEnd());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(docx)));
    }

    [Fact]
    public async Task LibreOffice_converts_a_refused_opendocument_file_from_a_repacked_copy()
    {
        var libreOffice = fx.Converters.Single(c => c.Id == "libreoffice");
        Assert.SkipUnless(fx.Catalog.StatusOf(libreOffice).IsAvailable, "LibreOffice not installed");
        var folder = fx.NewFolder();
        var plain = Odt(Path.Combine(folder, "plain.odt"), "Hello OpenDocument");
        var odt = QuirkyZip.Rewrite(plain, Path.Combine(folder, "letter.odt"), Stored | DataDescriptors);

        var (docx, repacked) = await ConvertAsync(libreOffice, odt, "odt", "docx");

        Assert.True(repacked, "LibreOffice loaded the file the first time");
        Assert.Contains("Hello OpenDocument", Text(DocxAssert.ReadBack(docx)));
        // A file LibreOffice loads is converted as it is.
        Assert.False((await ConvertAsync(libreOffice, plain, "odt", "docx")).Repacked);
    }

    /// <returns>The output, and whether a repacked copy was needed (the work folder holds one).</returns>
    private static async Task<(string Output, bool Repacked)> ConvertAsync(IConverter libreOffice, string input, string from, string to)
    {
        var output = Path.Combine(Path.GetDirectoryName(input)!, "out", Path.GetFileNameWithoutExtension(input) + "." + to);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var work = Path.Combine(Path.GetDirectoryName(input)!, "work-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        await HwpxAssert.LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var written = await libreOffice.ConvertAsync(new ConversionStep(input, from, to, new Preset(), new HwpxAssert.FixedPath(output), work),
                null, TestContext.Current.CancellationToken);
            return (written.Single(), Directory.EnumerateDirectories(work, "lo-repack-*").Any());
        }
        finally
        {
            HwpxAssert.LibreOfficeGate.Release();
        }
    }
}
