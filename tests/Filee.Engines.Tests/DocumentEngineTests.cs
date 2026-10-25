// Tests for engines that are downloaded or bundled separately (LibreOffice, rhwp).
// They skip automatically when the engine is not available, so CI stays green without them.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class DocumentEngineTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The engine with this id; skips the test when it is not registered.</summary>
    private IConverter Engine(string id)
    {
        var engine = fx.Converters.SingleOrDefault(c => c.Id == id);
        Assert.SkipUnless(engine is not null, $"{id} not registered");
        return engine!;
    }

    private void SkipUnlessAvailable(string id)
    {
        var status = fx.Catalog.StatusOf(Engine(id));
        Assert.SkipUnless(status.IsAvailable, $"{id} not available ({status.ReasonKey})");
    }

    [Theory]
    [InlineData("docx", "pdf", false, "pdf:writer_pdf_Export")]
    [InlineData("xlsx", "pdf", false, "pdf:calc_pdf_Export")]
    [InlineData("docx", "pdf", true, "pdf:writer_pdf_Export:{\"SelectPdfVersion\":{\"type\":\"long\",\"value\":\"2\"}}")]
    [InlineData("odt", "docx", false, "docx:MS Word 2007 XML")]
    [InlineData("xlsx", "html", false, "html:HTML (StarCalc)")]
    public void LibreOffice_filter_names(string from, string to, bool pdfA, string expected) =>
        Assert.Equal(expected, LibreOfficeConverter.FilterFor(from, to, pdfA));

    [Fact]
    public async Task LibreOffice_txt_to_docx_and_pdf()
    {
        SkipUnlessAvailable("libreoffice");
        var dir = fx.NewFolder();
        var txt = Path.Combine(dir, "note.txt");
        await File.WriteAllTextAsync(txt, "Filee 변환 테스트\n文件转换测试\nHello", TestContext.Current.CancellationToken);

        var docx = await fx.ConvertAsync([txt], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, docx.State);

        var pdf = await fx.ConvertAsync([docx.Outputs.Single()], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, pdf.State);
        Assert.True(new FileInfo(pdf.Outputs.Single()).Length > 1000);
    }

    [Fact]
    public async Task Hwpx_to_pdf()
    {
        Assert.SkipUnless(fx.Converters.Any(c => c.Id is "rhwp" or "libreoffice" && fx.Catalog.StatusOf(c).IsAvailable), "no HWPX renderer");
        var hwpx = await fx.MakeHwpxAsync(fx.NewFolder());

        var job = await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.True(new FileInfo(job.Outputs.Single()).Length > 1000);
    }

    [Fact]
    public async Task Hwpx_to_text()
    {
        SkipUnlessAvailable("unhwp");
        var hwpx = await fx.MakeHwpxAsync(fx.NewFolder());

        var job = await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "txt" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Contains("두 번째 문단입니다", await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hwpx_to_markdown_and_html_keeps_the_pictures()
    {
        SkipUnlessAvailable("unhwp");
        var dir = fx.NewFolder();
        EngineFixture.MakeImage(dir, "chart.png", ImageMagick.MagickFormat.Png);
        var source = Path.Combine(dir, "memo.md");
        await File.WriteAllTextAsync(source, "# 회의 메모\n\n그림 앞 문단.\n\n![](chart.png)\n\n그림 뒤 문단.\n", TestContext.Current.CancellationToken);
        var hwpx = (await fx.ConvertAsync([source], new Preset { TargetFormat = "hwpx" })).Outputs.Single();
        Assert.Equal("unhwp", Assert.Single(fx.Catalog.CreatePlanner().Plan("hwpx", "md")!.Steps).Converter.Id);

        // Unhwp links "assets/image1.png" without writing it: the picture goes into "memo_files" next to the result.
        var md = (await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "md" })).Outputs.Single();
        var markdown = await File.ReadAllTextAsync(md, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("assets/", markdown);
        var link = System.Text.RegularExpressions.Regex.Match(markdown, @"!\[[^\]]*\]\(<([^>]+)>\)").Groups[1].Value;
        Assert.StartsWith("memo_files/", link, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, Uri.UnescapeDataString(link))));
        Assert.Contains("그림 뒤 문단", markdown);

        // HTML is one page: the picture is embedded.
        var html = await File.ReadAllTextAsync((await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "html" })).Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("assets/", html);

        // Text only: no picture, no folder.
        var folders = Directory.GetDirectories(dir).Length;
        var textOnly = await File.ReadAllTextAsync((await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "md", Document = { LeaveOutPictures = true } })).Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("![", textOnly);
        Assert.Contains("그림 앞 문단.\n\n그림 뒤 문단", textOnly.ReplaceLineEndings("\n"));
        Assert.Equal(folders, Directory.GetDirectories(dir).Length);
    }

    [Fact]
    public async Task Rhwp_converts_between_hwpx_and_hwp()
    {
        SkipUnlessAvailable("rhwp");
        var hwpx = await fx.MakeHwpxAsync(fx.NewFolder());

        var hwp = await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "hwp" });
        Assert.True(hwp.State == JobState.Completed, string.Join("; ", hwp.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        // HWP 5.0 is an OLE compound file.
        var header = new byte[8];
        await using (var stream = File.OpenRead(hwp.Outputs.Single()))
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        Assert.Equal([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], header);

        var back = await fx.ConvertAsync([hwp.Outputs.Single()], new Preset { TargetFormat = "hwpx" });
        Assert.Equal(JobState.Completed, back.State);
        Assert.Equal("rhwp", Assert.Single(fx.Catalog.CreatePlanner().Plan("hwp", "hwpx")!.Steps).Converter.Id);
        Assert.Contains("두 번째 문단입니다", HwpxAssert.ReadBackText(back.Outputs.Single()));
    }

    [Fact]
    public async Task Rhwp_time_limit_grows_with_the_documents_text()
    {
        var dir = fx.NewFolder();
        var hwpx = await fx.MakeHwpxAsync(dir);
        using (var zip = ZipFile.OpenRead(hwpx))
            Assert.Equal(zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal)).Sum(e => e.Length), RhwpConverter.TextSize(hwpx));
        var hwp = Path.Combine(dir, "deflated.hwp");
        await File.WriteAllBytesAsync(hwp, new byte[1000], TestContext.Current.CancellationToken);
        Assert.Equal(3000, RhwpConverter.TextSize(hwp)); // not a ZIP: an HWP's text is deflated to about a third

        Assert.Equal(TimeSpan.FromMinutes(2), RhwpConverter.TimeLimit(0));
        Assert.Equal(TimeSpan.FromMinutes(32), RhwpConverter.TimeLimit(1024 * 1024));
        Assert.Equal(TimeSpan.FromMinutes(60), RhwpConverter.TimeLimit(100 * 1024 * 1024)); // a hung rhwp is still stopped
    }

    [Fact]
    public async Task Rhwp_progress_follows_the_characters_it_draws()
    {
        // A table cell takes hundreds of bytes of XML for a few characters, and rhwp's time goes by characters: a
        // 65-page workbook (12.8 MB of XML, 180 000 characters) was estimated at over three hours.
        var dir = fx.NewFolder();
        var csv = Path.Combine(dir, "명단.csv");
        await File.WriteAllTextAsync(csv, "이름,금액\n홍길동,\"1,000\"\nKim Lee,  7 \n", TestContext.Current.CancellationToken);
        var hwpx = Assert.Single((await fx.ConvertAsync([csv], new Preset { TargetFormat = "hwpx" })).Outputs);

        Assert.Equal(2 + 2 + 3 + 5 + 6 + 1, RhwpConverter.Characters(hwpx)); // spaces aren't drawn
        var hwp = Path.Combine(dir, "deflated.hwp");
        await File.WriteAllBytesAsync(hwp, new byte[1000], TestContext.Current.CancellationToken);
        Assert.Null(RhwpConverter.Characters(hwp));

        Assert.Equal(TimeSpan.FromMinutes(10.5), RhwpConverter.Typical(180_000, 12 * 1048576, direct: false));
        Assert.Equal(TimeSpan.FromSeconds(36), RhwpConverter.Typical(180_000, 12 * 1048576, direct: true));
        Assert.Equal(TimeSpan.FromMinutes(15), RhwpConverter.Typical(null, 1048576, direct: false)); // an HWP: by its size
        Assert.Equal(TimeSpan.FromSeconds(1.5), RhwpConverter.Typical(10, 1000, direct: false));
    }

    [Theory]
    [InlineData("매출.xlsx", true)]
    [InlineData("data.CSV", true)]
    [InlineData("old.xls", true)]
    [InlineData("calc.ods", true)]
    [InlineData("report.docx", false)]
    [InlineData("form.hwpx", false)]
    [InlineData("form.hwp", false)]
    public void Rhwp_draws_documents_written_from_spreadsheets_with_its_direct_backend(string source, bool direct)
    {
        Assert.Equal(direct, RhwpConverter.UsesDirectBackend(source));
        Assert.All(SheetDocument.SourceFormats, f => Assert.Contains(f, DocumentReaders.BuiltInFormats));
    }

    [Fact]
    public async Task Csv_to_pdf_with_rhwp()
    {
        SkipUnlessAvailable("rhwp");
        var dir = fx.NewFolder();
        var csv = Path.Combine(dir, "재고.csv");
        await File.WriteAllLinesAsync(csv, ["품명,수량,단가", .. Enumerable.Range(1, 120).Select(i => $"부품 {i},{i},\"{i * 1000:N0}\"")], TestContext.Current.CancellationToken);
        Assert.Equal(["hwpx-writer", "rhwp"], fx.Catalog.CreatePlanner(["rhwp"]).Plan("csv", "pdf")!.Steps.Select(s => s.Converter.Id));

        var job = await fx.ConvertAsync([csv], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.True(PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(job.Outputs.Single(), TestContext.Current.CancellationToken)) >= 2);
    }

    [Fact]
    public async Task LibreOffice_with_H2Orestart_reads_hwpx()
    {
        SkipUnlessAvailable("libreoffice");
        var libreOffice = Engine("libreoffice");
        Assert.SkipUnless(libreOffice.Edges.Any(e => e.From == "hwpx"), "H2Orestart extension not installed");
        var dir = fx.NewFolder();
        var hwpx = await fx.MakeHwpxAsync(dir);
        var output = Path.Combine(dir, "from-hwpx.docx");

        // Call the engine directly: the planner may prefer another HWPX reader.
        var written = await libreOffice.ConvertAsync(
            new ConversionStep(hwpx, "hwpx", "docx", new Preset(), new SinglePath(output), dir), null, TestContext.Current.CancellationToken);

        Assert.Equal(output, written.Single());
        Assert.True(new FileInfo(output).Length > 2000);
    }

    private sealed class SinglePath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
