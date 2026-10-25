// Spreadsheets between XLSX, XLS, ODS, CSV and TSV without Excel or LibreOffice (XLS / XLT and the XLSM / XLTX
// variants are read, not written).
// XLSX / XLS / ODS / CSV / TSV → PDF, HWPX and HTML go through the HWPX writer (sheets become 한글 tables), see
// HwpxConverter and SheetDocument.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>Converts between spreadsheet formats in-process.</summary>
public sealed class SpreadsheetConverter : IConverter
{
    private static readonly string[] Sources = ["xlsx", "xlsm", "xltx", "xls", "xlt", "ods", "csv", "tsv"];
    private static readonly string[] Targets = ["xlsx", "ods", "csv", "tsv"];

    public string Id => "spreadsheet";
    public string DisplayName => "Spreadsheets (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        // XLSM / XLTX → XLSX is left to the ooxml engine, which keeps the whole package instead of rewriting the sheets.
        [.. Sources.SelectMany(from => Targets.Where(to => to != from && !(to == "xlsx" && from is "xlsm" or "xltx"))
            .Select(to => new ConversionEdge(from, to)))];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV",
            $"{EngineVersions.BuiltIn} · {EngineVersions.Library("ExcelDataReader", typeof(ExcelDataReader.ExcelReaderFactory))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            var book = Read(step.InputPath, step.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);

            var outputs = Write(book, step);
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    /// <summary>Writes a workbook as the step's XLSX, ODS, CSV or TSV (also PdfTablesConverter's output).</summary>
    internal static List<string> Write(Workbook book, ConversionStep step)
    {
        var outputs = new List<string>();
        switch (step.To)
        {
            case "xlsx" or "ods":
                if (step.Output.Allocate(step.To) is { } path)
                {
                    if (step.To == "xlsx")
                        XlsxWriter.Write(book, path);
                    else
                        OdsWriter.Write(book, path);
                    outputs.Add(path);
                }
                break;
            default:
                // CSV and TSV hold one sheet: a workbook with several sheets becomes one file per sheet.
                var sheets = book.Sheets.Where(s => s.UsedRange() is not null).ToList();
                if (sheets.Count == 0 && book.Sheets.Count > 0)
                    sheets = [book.Sheets[0]];
                foreach (var sheet in sheets)
                {
                    var file = step.Output.Allocate(step.To, sheets.Count > 1 ? "_" + SafeName(sheet.Name) : null);
                    if (file is null)
                        continue;
                    CsvFormat.Write(sheet, file, step.To == "tsv" ? CsvFormat.Tab : ',');
                    outputs.Add(file);
                }
                break;
        }
        return outputs;
    }

    /// <summary>Reads a spreadsheet of a format id this engine accepts.</summary>
    internal static Workbook Read(string path, string format) => format switch
    {
        "xls" or "xlt" => XlsReader.Read(path),
        "ods" => OdsReader.Read(path),
        "csv" => CsvFormat.Read(path),
        "tsv" => CsvFormat.ReadTsv(path),
        _ => XlsxReader.Read(path),
    };

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(ch => invalid.Contains(ch) ? '_' : ch)]).Trim();
        return clean.Length == 0 ? "Sheet" : clean;
    }
}
