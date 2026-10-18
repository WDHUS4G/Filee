// HWP / HWPX → PDF, HWP → HWPX and HWPX → HWP with the rhwp command line tool (MIT, https://github.com/edwardkim/rhwp).
// Bundled under engines/rhwp. Works without Hancom Office, so there is no automation approval dialog.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp;

/// <summary>Renders HWP and HWPX documents to PDF and converts between the two 한글 formats.</summary>
public sealed class RhwpConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private string? _exe;

    public string Id => "rhwp";
    public string DisplayName => "rhwp";
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "pdf"),
        new("hwpx", "pdf"),
        // Lossless within the 한글 document model; anything → HWP goes through the HWPX writer first.
        new("hwp", "hwpx"),
        new("hwpx", "hwp"),
    ];

    public EngineStatus GetStatus()
    {
        _exe = Locate();
        return _exe is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_exe, EngineVersions.Component("rhwp"));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _exe ?? Locate() ?? throw new InvalidOperationException("rhwp was not found.");
        var temp = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.{step.To}");
        var input = step.To == "pdf" ? await PrepareAsync(exe, step, cancellationToken) : step.InputPath;
        string[] arguments = step.To switch
        {
            "pdf" => ["export-pdf", input, "-o", temp, .. RhwpInput.PdfOptions()],
            "hwpx" => ["export-hwpx", step.InputPath, temp],
            "hwp" => ["convert", step.InputPath, temp],
            _ => throw new NotSupportedException(step.To),
        };

        progress?.Report(0.1);
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(1.5)))
            result = await ProcessRunner.RunAsync(exe, arguments, Timeout, cancellationToken);
        if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            throw new InvalidOperationException($"rhwp failed (exit {result.ExitCode}). {result.StandardError.Trim()}");

        var target = step.Output.Allocate(step.To);
        if (target is null)
            return [];
        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        return [target];
    }

    /// <summary>
    /// The document rhwp renders: a copy with the fixes of <see cref="RhwpInput"/> (fonts, shape sizes, equation
    /// scripts, tables floating in cells) when it needs any, else the original. An HWP is read through an HWPX copy for
    /// that, and used as it is when nothing needs fixing or the HWPX copy fails.
    /// </summary>
    private static async Task<string> PrepareAsync(string exe, ConversionStep step, CancellationToken cancellationToken)
    {
        var hwpx = step.InputPath;
        if (step.From == "hwp")
        {
            hwpx = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.hwpx");
            await ProcessRunner.RunAsync(exe, ["export-hwpx", step.InputPath, hwpx], Timeout, cancellationToken);
            if (!File.Exists(hwpx) || new FileInfo(hwpx).Length == 0)
                return step.InputPath;
        }
        return RhwpInput.Prepare(hwpx, step.WorkDirectory) ?? step.InputPath;
    }

    /// <summary>Filee's own copy (engines/rhwp), never one found elsewhere on the system.</summary>
    private static string? Locate() =>
        EngineEnvironment.FindBundled("rhwp") is { } bundled
            ? EngineEnvironment.FirstExisting(Path.Combine(bundled, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp"))
            : null;
}
