// "Recognize text" (issue #288): Tesseract's OCR edges start a route only when the preset asks for them, and the
// route goes on from the searchable PDF.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class OcrRouteTests
{
    private static readonly ConversionEdge[] OcrEdges =
        [new("png", "pdf", Ocr: true), new("jpg", "pdf", Ocr: true), new("pdf", "pdf", Ocr: true)];

    private static string Steps(Route? route) => string.Join(", ", route!.Steps.Select(s => $"{s.Converter.Id}:{s.From}→{s.To}"));

    private static RoutePlanner Planner() => new([
        new FakeConverter("tesseract", OcrEdges),
        new FakeConverter("magick", new ConversionEdge("heic", "png"), new ConversionEdge("png", "pdf"), new ConversionEdge("jpg", "pdf")),
        new FakeConverter("docx", new ConversionEdge("pdf", "docx")),
        new FakeConverter("text", new ConversionEdge("pdf", "txt")),
    ]);

    [Fact]
    public void Ocr_edges_are_never_part_of_an_ordinary_route()
    {
        var planner = Planner();

        Assert.Equal("magick:png→pdf", Steps(planner.Plan("png", "pdf")));
        Assert.Null(planner.Plan("pdf", "pdf")); // no ordinary PDF → PDF edge here
    }

    [Fact]
    public void Asking_for_ocr_starts_with_tesseract_and_goes_on_from_its_pdf()
    {
        var planner = Planner();

        Assert.Equal("tesseract:jpg→pdf", Steps(planner.Plan("jpg", "pdf", ocr: true)));
        Assert.Equal("tesseract:pdf→pdf, docx:pdf→docx", Steps(planner.Plan("pdf", "docx", ocr: true)));
        Assert.Equal("tesseract:png→pdf, text:pdf→txt", Steps(planner.Plan("png", "txt", ocr: true)));
        // A picture Tesseract can't open becomes a PNG first.
        Assert.Equal("magick:heic→png, tesseract:png→pdf, docx:pdf→docx", Steps(planner.Plan("heic", "docx", ocr: true)));
    }

    [Fact]
    public void Without_tesseract_there_is_no_ocr_route()
    {
        var planner = new RoutePlanner([new FakeConverter("magick", new ConversionEdge("png", "pdf"))]);

        Assert.Null(planner.Plan("png", "pdf", ocr: true));
        Assert.False(planner.CanConvert("png", "pdf", ocr: true));
    }

    [Fact]
    public void Text_is_recognized_in_pdfs_and_pictures_for_document_targets()
    {
        var preset = new Preset { TargetFormat = "docx", Pdf = { Ocr = true } };

        Assert.True(preset.RecognizesTextOf("pdf"));
        Assert.True(preset.RecognizesTextOf("jpg"));
        Assert.False(preset.RecognizesTextOf("docx")); // already text
        Assert.False(new Preset { TargetFormat = "png", Pdf = { Ocr = true } }.RecognizesTextOf("pdf"));
        Assert.False(new Preset { TargetFormat = "docx" }.RecognizesTextOf("pdf"));
    }

    [Fact]
    public async Task A_job_with_ocr_runs_tesseract_before_the_document_writer()
    {
        using var dir = new TempDir();
        var scan = dir.File("scan.jpg");
        var tesseract = new FakeRecorder("tesseract", OcrEdges);
        var writer = new FakeRecorder("docx", new ConversionEdge("pdf", "docx"));
        var magick = new FakeRecorder("magick", new ConversionEdge("jpg", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([tesseract, writer, magick]));

        var job = new ConversionJob([scan], new Preset { TargetFormat = "docx", Pdf = { Ocr = true } }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([("jpg", "pdf", false)], tesseract.Steps);
        Assert.Equal([("pdf", "docx", false)], writer.Steps);
        Assert.Empty(magick.Steps);
    }

    [Fact]
    public async Task Pdfs_merged_into_one_are_recognized_first()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        var b = dir.File("b.jpg");
        var tesseract = new FakeRecorder("tesseract", OcrEdges);
        var merger = new FakeMerger();
        await using var queue = new JobQueue(new ConverterCatalog([tesseract]), merger);

        var job = new ConversionJob([a, b], new Preset { TargetFormat = "pdf", Pdf = { Ocr = true, MergeIntoSingle = true } }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([("jpg", "pdf", false), ("pdf", "pdf", false)], tesseract.Steps.OrderBy(s => s.From));
    }

    [Fact]
    public void A_version_39_library_gets_searchable_pdf_after_compress_pdf()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "searchable-pdf");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(39, presets, profiles));

        var ids = presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("compress-pdf") + 1, ids.IndexOf("searchable-pdf"));
        Assert.True(presets.Single(p => p.Id == "searchable-pdf").Pdf.Ocr);
        Assert.False(SettingsMigrations.ApplyToLibrary(40, presets, profiles));
    }
}
