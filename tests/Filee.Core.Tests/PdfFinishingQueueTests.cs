// Watermark, page numbers and password are applied by JobQueue to every PDF a job writes, whichever engine made it,
// and once to a merged PDF.

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class PdfFinishingQueueTests
{
    /// <summary>Converts like <see cref="FakeConverter"/> and records the PDFs it was asked to finish.</summary>
    private sealed class FakeFinisher(params ConversionEdge[] edges) : IConverter, IPdfFinisher
    {
        private readonly FakeConverter _writer = new("pdfsharp", edges);

        public List<string> Finished { get; } = [];
        public string Id => "pdfsharp";
        public string DisplayName => "PDFsharp";
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            _writer.ConvertAsync(step, progress, cancellationToken);

        public Task FinishAsync(string path, PdfOptions options, CancellationToken cancellationToken)
        {
            lock (Finished)
                Finished.Add(Path.GetFileName(path));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Every_pdf_of_a_job_is_finished_whichever_engine_wrote_it()
    {
        using var dir = new TempDir();
        var finisher = new FakeFinisher(new ConversionEdge("jpg", "pdf"));
        var office = new FakeConverter("rhwp", new ConversionEdge("docx", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([finisher, office]));

        var job = new ConversionJob([dir.File("scan.jpg"), dir.File("report.docx")],
            new Preset { TargetFormat = "pdf", Pdf = { PageNumbers = true } }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["report.pdf", "scan.pdf"], finisher.Finished.Order());
    }

    [Fact]
    public async Task Nothing_is_finished_without_the_options_or_for_other_targets()
    {
        using var dir = new TempDir();
        var finisher = new FakeFinisher(new ConversionEdge("jpg", "pdf"), new ConversionEdge("jpg", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([finisher]));

        await queue.RunAsync(new ConversionJob([dir.File("a.jpg")], new Preset { TargetFormat = "pdf" }, "Test"));
        await queue.RunAsync(new ConversionJob([dir.File("b.jpg")], new Preset { TargetFormat = "png", Pdf = { Watermark = "DRAFT" } }, "Test"));

        Assert.Empty(finisher.Finished);
    }

    [Fact]
    public async Task A_merged_pdf_is_finished_once_so_its_pages_are_numbered_through()
    {
        using var dir = new TempDir();
        var finisher = new FakeFinisher(new ConversionEdge("jpg", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([finisher]), new FakeMerger());

        var job = new ConversionJob([dir.File("1.jpg"), dir.File("2.jpg")],
            new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, PageNumbers = true, Watermark = "DRAFT" } }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([Path.GetFileName(job.Outputs.Single())], finisher.Finished);
    }
}
