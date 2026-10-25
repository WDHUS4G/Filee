// Structured data between JSON, JSON Lines, YAML, TOML and XML (and CSV for tables), plus vCard contacts and iCalendar ↔ CSV,
// in-process. Every format is read into one tree (DataTree) and written from it, so structure and types carry over as
// far as the target can hold them; see the format files for what each one loses (TOML has no null, XML no types,
// CSV only tables). Output is UTF-8 without BOM (CSV: with BOM, like the spreadsheet engine), indented by two spaces.
// Spreadsheets reach JSON and the others through CSV (XLSX → CSV → JSON), contacts and calendars through CSV as well. HAR
// network logs are read as JSON, and become a table of their requests (HarData).

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Data;

/// <summary>JSON, JSON Lines, YAML, TOML, XML and CSV into each other; vCard and iCalendar ↔ CSV; HAR → all of them.</summary>
public sealed class DataConverter : IConverter
{
    private static readonly string[] Trees = ["json", "jsonl", "yaml", "toml", "xml"];

    public string Id => "data";
    public string DisplayName => "Data (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. Trees.SelectMany(from => Trees.Where(to => to != from).Select(to => new ConversionEdge(from, to))),
        .. Trees.Select(from => new ConversionEdge(from, "csv")),
        .. Trees.Select(to => new ConversionEdge("csv", to)),
        new("vcf", "csv"),
        new("csv", "vcf"),
        .. Trees.Append("csv").Select(to => new ConversionEdge("har", to)),
        new("ics", "csv"),
        new("csv", "ics"),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other; vCard and iCalendar ↔ CSV",
            $"{EngineVersions.BuiltIn} · {EngineVersions.Library("YamlDotNet", typeof(YamlDotNet.RepresentationModel.YamlStream))}"
            + $" · {EngineVersions.Library("Tomlyn", typeof(Tomlyn.TomlSerializer))}");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            if (step.From == "vcf")
            {
                var contacts = VCard.Read(File.ReadAllBytes(step.InputPath));
                if (contacts.Count == 0)
                    throw new InvalidDataException("The file has no vCard contacts.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("csv") is not { } csv)
                    return [];
                CsvFormat.WriteRows(VCard.ToRows(contacts), csv);
                progress?.Report(1);
                return [csv];
            }
            if (step.To == "vcf")
            {
                var contacts = VCard.FromRows(CsvData.ReadRows(step.InputPath));
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("vcf") is not { } vcf)
                    return [];
                File.WriteAllText(vcf, VCard.Write(contacts), Utf8);
                progress?.Report(1);
                return [vcf];
            }
            if (step.From == "ics")
            {
                var components = ICalendar.Read(File.ReadAllBytes(step.InputPath));
                if (components.Count == 0)
                    throw new InvalidDataException("The file has no iCalendar events or to-dos.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("csv") is not { } csv)
                    return [];
                CsvFormat.WriteRows(ICalendar.ToRows(components), csv);
                progress?.Report(1);
                return [csv];
            }
            if (step.To == "ics")
            {
                var components = ICalendar.FromRows(CsvData.ReadRows(step.InputPath));
                if (components.Count == 0)
                    throw new InvalidDataException("The table has no iCalendar events or to-dos.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("ics") is not { } ics)
                    return [];
                File.WriteAllText(ics, ICalendar.Write(components), Utf8);
                progress?.Report(1);
                return [ics];
            }

            var tree = Read(step.InputPath, step.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);
            // Built before a path is taken, so a tree the target can't hold leaves no empty file behind.
            var text = step.To switch
            {
                "csv" => null,
                "json" => JsonData.Write(tree),
                "jsonl" => JsonData.WriteLines(tree),
                "yaml" => YamlData.Write(tree),
                "toml" => TomlData.Write(tree),
                "xml" => XmlData.Write(tree),
                _ => throw new NotSupportedException($"Data → {step.To} is not supported."),
            };
            var rows = step.To != "csv" ? null : step.From == "har" ? HarData.Table(tree) : CsvData.Table(tree);
            if (step.Output.Allocate(step.To) is not { } output)
                return [];
            if (rows is not null)
                CsvFormat.WriteRows(rows, output);
            else
                File.WriteAllText(output, text, Utf8);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>Reads a data file of a format id this engine accepts into the common tree.</summary>
    internal static object? Read(string path, string format) => format switch
    {
        "xml" => XmlData.Read(path),
        "csv" => CsvData.Read(path),
        _ => ReadText(HwpxConverter.DecodeText(File.ReadAllBytes(path)), format),
    };

    private static object? ReadText(string text, string format) => format switch
    {
        "json" or "har" => JsonData.Read(text),
        "jsonl" => JsonData.ReadLines(text),
        "yaml" => YamlData.Read(text),
        "toml" => TomlData.Read(text),
        _ => throw new NotSupportedException($"{format} → data is not supported."),
    };
}
