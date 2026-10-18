// E-book formats Filee does not read or write itself, with calibre's ebook-convert (GPL-3.0, a separate program the
// user downloads in Settings → Engines): LIT, LRF, CHM, PDB, PML, RB, SNB, TCR and OEB → EPUB, and EPUB → MOBI,
// AZW3, LIT, LRF, PDB, PML, RB, SNB and TCR. EPUB is the hub: the built-in engine handles every other step, and
// calibre's edges cost more so built-in routes always win where they exist.
//
// Each run gets its own calibre configuration, cache and temp folders inside the job's work directory, so Filee
// never reads or changes the settings of a calibre the user may have installed.

using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Ebooks;

/// <summary>Conversions through calibre's ebook-convert.</summary>
public sealed partial class CalibreConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Formats only calibre reads.</summary>
    internal static readonly string[] Inputs = ["lit", "lrf", "chm", "pdb", "pml", "rb", "snb", "tcr", "oeb"];

    /// <summary>Formats only calibre writes.</summary>
    internal static readonly string[] Outputs = ["mobi", "azw3", "lit", "lrf", "pdb", "pml", "rb", "snb", "tcr"];

    private string? _executable;

    public string Id => "calibre";
    public string DisplayName => "Calibre";

    /// <summary>ebook-convert is a large Python process; two at a time keep the PC responsive.</summary>
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. Inputs.Select(from => new ConversionEdge(from, "epub", 20)),
        .. Outputs.Select(to => new ConversionEdge("epub", to, 20)),
    ];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("calibre") ? EngineVersions.Component("calibre") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = _executable ?? Locate() ?? throw new InvalidOperationException("Calibre is not installed.");
        var work = EbookFiles.NewFolder(step.WorkDirectory, "calibre");

        // ebook-convert picks formats by file extension: give it names it knows. An .opf (OEB) stays where it is,
        // next to the files it lists; a zipped .oeb is read by calibre's ZIP input.
        var input = step.InputPath;
        if (!input.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
        {
            input = Path.Combine(work, "input." + (step.From == "oeb" ? "zip" : step.From));
            File.Copy(step.InputPath, input);
        }
        var temp = Path.Combine(work, "output." + step.To);

        var environment = new Dictionary<string, string>
        {
            ["CALIBRE_CONFIG_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(work, "config")).FullName,
            ["CALIBRE_CACHE_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(work, "cache")).FullName,
            ["CALIBRE_TEMP_DIR"] = Directory.CreateDirectory(Path.Combine(work, "temp")).FullName,
            ["CALIBRE_OVERRIDE_LANG"] = "en", // English messages in the error details, whatever the Windows language
            ["PYTHONIOENCODING"] = "utf-8",
        };
        progress?.Report(0.02);
        var result = await ProcessRunner.RunAsync(executable, [input, temp], Timeout, cancellationToken,
            workingDirectory: work, environment: environment, onOutput: line => ReportProgress(line, progress));
        if (result.ExitCode != 0 || !File.Exists(temp))
            throw new InvalidOperationException($"Calibre failed (exit {result.ExitCode}). {LastLines(result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput)}");

        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        File.Move(temp, output, overwrite: true);
        progress?.Report(1);
        return [output];
    }

    /// <summary>ebook-convert prints "34% Running transforms on e-book..." while it works.</summary>
    private static void ReportProgress(string line, IProgress<double>? progress)
    {
        if (progress is not null && Percent().Match(line) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            progress.Report(Math.Clamp(percent, 0, 100) / 100.0 * 0.95);
    }

    /// <summary>The end of calibre's output, where its error message (or Python traceback) is.</summary>
    private static string LastLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%\s")]
    private static partial Regex Percent();

    /// <summary>
    /// The user's own calibre when they chose one in Settings → Engines, else Filee's (engines/calibre, downloaded on
    /// demand), else a calibre already on the PC.
    /// </summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("calibre", "ebook-convert.exe") ?? LocateBundled()
        ?? EngineEnvironment.SystemProgram("calibre", "ebook-convert.exe");

    private static string? LocateBundled() =>
        EngineEnvironment.BundledProgram("calibre", "ebook-convert.exe");
}
