// The spreadsheet print studio's conversion boundary. Every preview and export is made from a disposable copy of
// the workbook, so changing a print area, page size or hidden-row option can never touch the user's original file.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;

namespace Filee.App.Services;

public sealed record SpreadsheetRenderedRegion(string PdfPath, PdfPreviewDocument Preview, string TemporaryDirectory);

public sealed class SpreadsheetPrintService(IReadOnlyList<IConverter> converters)
{
    private readonly LibreOfficeConverter? _libreOffice = converters.OfType<LibreOfficeConverter>().FirstOrDefault();
    private readonly IPdfMerger? _pdfMerger = converters.OfType<IPdfMerger>().FirstOrDefault();

    public SpreadsheetWorkbookInfo Inspect(string path) => SpreadsheetPrintInspector.Read(path);

    public EngineStatus Status => _libreOffice?.GetStatus()
        ?? EngineStatus.Unavailable("engine.reason.not_installed");

    public async Task<SpreadsheetRenderedRegion> RenderAsync(SpreadsheetWorkbookInfo workbook,
        SpreadsheetPrintRegion region, CancellationToken cancellationToken = default)
    {
        var (output, folder) = await MakePdfAsync(workbook, region, cancellationToken);
        try
        {
            var preview = await Task.Run(() => PdfPreviewDocument.Open(output), cancellationToken);
            return new SpreadsheetRenderedRegion(output, preview, folder);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    public async Task ExportAsync(SpreadsheetWorkbookInfo workbook, IReadOnlyList<SpreadsheetPrintRegion> regions,
        string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (regions.Count == 0)
            throw new ArgumentException("At least one region must be exported.", nameof(regions));
        if (_pdfMerger is null)
            throw new InvalidOperationException("The PDF merger is not available.");
        var rendered = new List<(string PdfPath, string TemporaryDirectory)>();
        var temporaryOutput = outputPath + ".filee-tmp";
        try
        {
            for (var index = 0; index < regions.Count; index++)
            {
                rendered.Add(await MakePdfAsync(workbook, regions[index], cancellationToken));
                progress?.Report(0.85 * (index + 1) / regions.Count);
            }
            TryDelete(temporaryOutput);
            await _pdfMerger.MergeAsync(rendered.Select(item => item.PdfPath).ToArray(), temporaryOutput, cancellationToken);
            File.Move(temporaryOutput, outputPath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            TryDelete(temporaryOutput);
            foreach (var item in rendered)
                TryDelete(item.TemporaryDirectory);
        }
    }

    private async Task<(string PdfPath, string TemporaryDirectory)> MakePdfAsync(SpreadsheetWorkbookInfo workbook,
        SpreadsheetPrintRegion region, CancellationToken cancellationToken)
    {
        if (_libreOffice is null)
            throw new InvalidOperationException("LibreOffice is not available.");
        var folder = CreateWorkFolder();
        try
        {
            var input = Path.Combine(folder, "sheet.xlsx");
            var output = Path.Combine(folder, "sheet.pdf");
            await Task.Run(() => SpreadsheetPrintPackage.Create(workbook.Path, input, workbook, region), cancellationToken);
            var results = await _libreOffice.ConvertAsync(new ConversionStep(
                input, "xlsx", "pdf", new Preset { TargetFormat = "pdf" },
                new ExactOutput(output), folder), null, cancellationToken);
            if (results.Count != 1 || !File.Exists(output))
                throw new InvalidOperationException("LibreOffice did not create the requested PDF.");
            return (output, folder);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    public static void Release(SpreadsheetRenderedRegion? rendered)
    {
        if (rendered is not null)
            TryDelete(rendered.TemporaryDirectory);
    }

    private static string CreateWorkFolder()
    {
        Directory.CreateDirectory(JobQueue.DefaultWorkRoot);
        string folder;
        do folder = Path.Combine(JobQueue.DefaultWorkRoot, Guid.NewGuid().ToString("N")[..10]);
        while (Directory.Exists(folder));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ExactOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
