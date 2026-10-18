// Structured data between JSON, JSON Lines, YAML, TOML and XML (and CSV for tables), plus vCard contacts ↔ CSV,
// in-process. Every format is read into one tree (DataTree) and written from it, so structure and types carry over as
// far as the target can hold them; see the format files for what each one loses (TOML has no null, XML no types,
// CSV only tables). Output is UTF-8 without BOM (CSV: with BOM, like the spreadsheet engine), indented by two spaces.
// Spreadsheets reach JSON and the others through CSV (XLSX → CSV → JSON), contacts through CSV as well.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Data;

/// <summary>JSON, JSON Lines, YAML, TOML, XML and CSV into each other; vCard ↔ CSV.</summary>
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
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other; vCard ↔ CSV",
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
            var rows = step.To == "csv" ? CsvData.Table(tree) : null;
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
        "json" => JsonData.Read(text),
        "jsonl" => JsonData.ReadLines(text),
        "yaml" => YamlData.Read(text),
        "toml" => TomlData.Read(text),
        _ => throw new NotSupportedException($"{format} → data is not supported."),
    };
}
