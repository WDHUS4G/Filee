// PostScript and EPS with Ghostscript (AGPL-3.0, a separate program downloaded on demand into engines/ghostscript).
// EPS/PS → PDF with pdfwrite; PDF → EPS/PS with eps2write/ps2write; EPS/PS → PNG with pngalpha, which keeps a
// transparent background (through PDF the page would be drawn on white paper). Everything else (EPS → JPG,
// SVG → EPS, …) is planned through PDF or PNG by the route planner. "Compress PDF" may also run a PDF through pdfwrite
// (PdfSharpConverter calls CompressPdfAsync; there is no PDF → PDF edge here, so routes don't change).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Vector;

/// <summary>EPS, PS and PostScript-based AI ↔ PDF through Ghostscript.</summary>
public sealed class GhostscriptConverter : IConverter, IImageTransformer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Environment variables Ghostscript would otherwise read from the user's system (extra options, resource and
    /// font search paths). Filee's copy has its resources compiled in and must behave the same on every PC.
    /// </summary>
    private static readonly Dictionary<string, string> CleanEnvironment = new()
    {
        ["GS_OPTIONS"] = "",
        ["GS_LIB"] = "",
        ["GS_FONTPATH"] = "",
    };

    private string? _executable;

    public string Id => "ghostscript";
    public string DisplayName => "Ghostscript";
    public int MaxParallelism => 0;

    /// <summary>Only when rendering EPS / PostScript to PNG.</summary>
    public bool Transforms(string from, string to) => to == "png";

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("eps", "pdf"),
        new("ps", "pdf"),
        new("pdf", "eps"),
        new("pdf", "ps"),
        new("eps", "png"),
        new("ps", "png"),
    ];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("ghostscript") ? EngineVersions.Component("ghostscript") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var gs = _executable ?? Locate() ?? throw new InvalidOperationException("Ghostscript is not installed.");
        progress?.Report(0.05);
        IReadOnlyList<string> written = step.To switch
        {
            "pdf" => await ToPdfAsync(gs, step, cancellationToken, progress),
            "ps" => await ToPostScriptAsync(gs, step, cancellationToken, progress),
            "eps" => await ToEpsAsync(gs, step, cancellationToken, progress),
            "png" => await ToPngAsync(gs, step, cancellationToken, progress),
            _ => throw new NotSupportedException($"Ghostscript cannot write '{step.To}'."),
        };
        progress?.Report(1);
        return written;
    }

    /// <summary>Filee's own Ghostscript (engines/ghostscript, downloaded on demand), never one installed on the system.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("ghostscript", "gswin64c.exe") ?? LocateBundled()
        ?? EngineEnvironment.SystemProgram("ghostscript", "gswin64c.exe");

    private static string? LocateBundled() =>
        EngineEnvironment.BundledProgram("ghostscript", "gswin64c.exe");

    /// <summary>
    /// Converts PostScript (PS, EPS, PostScript-based Illustrator files) to PDF. EPS pages are cropped to the
    /// bounding box. Used by <see cref="VectorConverter"/> for AI files saved without PDF compatibility.
    /// </summary>
    internal static async Task ConvertToPdfAsync(string gs, string input, string output, bool crop, string workDirectory,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var scratch = NewScratch(workDirectory);
        var target = Path.Combine(scratch, "out.pdf");
        List<string> args = ["-sDEVICE=pdfwrite", "-dAutoRotatePages=/None"];
        if (crop)
            args.Add("-dEPSCrop");
        await RunAsync(gs, args, target, input, cancellationToken, progress);
        File.Move(target, output, overwrite: true);
    }

    /// <summary>
    /// Rewrites a PDF smaller with pdfwrite's settings for the level (/screen, /ebook, /printer: pictures at 72, 150 and
    /// 300 dpi, Filee's levels too). Used by <see cref="Pdf.PdfSharpConverter"/> for "Compress PDF" when the preset asks
    /// for Ghostscript; the caller keeps the result only when it is smaller.
    /// </summary>
    internal static async Task CompressPdfAsync(string gs, string input, string output, PdfCompression level, string workDirectory,
        CancellationToken cancellationToken)
    {
        var settings = level switch
        {
            PdfCompression.Screen => "/screen",
            PdfCompression.Print => "/printer",
            _ => "/ebook",
        };
        var target = Path.Combine(NewScratch(workDirectory), "out.pdf");
        List<string> args = ["-sDEVICE=pdfwrite", "-dPDFSETTINGS=" + settings, "-dCompatibilityLevel=1.6", "-dAutoRotatePages=/None"];
        await RunAsync(gs, args, target, input, cancellationToken, progress: null);
        File.Move(target, output, overwrite: true);
    }

    private static async Task<IReadOnlyList<string>> ToPdfAsync(string gs, ConversionStep step, CancellationToken ct, IProgress<double>? progress)
    {
        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        await ConvertToPdfAsync(gs, step.InputPath, output, crop: step.From == "eps", step.WorkDirectory, ct, progress);
        return [output];
    }

    private static async Task<IReadOnlyList<string>> ToPostScriptAsync(string gs, ConversionStep step, CancellationToken ct, IProgress<double>? progress)
    {
        var output = step.Output.Allocate("ps");
        if (output is null)
            return [];
        var target = Path.Combine(NewScratch(step.WorkDirectory), "out.ps");
        List<string> args = ["-sDEVICE=ps2write"];
        if (SelectedPages(step) is { } pages)
            args.Add("-sPageList=" + string.Join(',', pages.Select(p => p + 1)));
        await RunAsync(gs, args, target, step.InputPath, ct, progress);
        File.Move(target, output, overwrite: true);
        return [output];
    }

    /// <summary>EPS holds one page: a multi-page PDF becomes one EPS per page (<c>_p{n}</c> like PDF → PNG).</summary>
    private static async Task<IReadOnlyList<string>> ToEpsAsync(string gs, ConversionStep step, CancellationToken ct, IProgress<double>? progress)
    {
        var scratch = NewScratch(step.WorkDirectory);
        var pages = SelectedPages(step);
        List<string> args = ["-sDEVICE=eps2write"];
        if (pages is not null)
            args.Add("-sPageList=" + string.Join(',', pages.Select(p => p + 1)));
        // %d makes Ghostscript write one numbered file per page (1, 2, 3 … in output order).
        await RunAsync(gs, args, Path.Combine(scratch, "page-%d.eps"), step.InputPath, ct, progress);

        var files = Directory.GetFiles(scratch, "page-*.eps")
            .Select(f => (Path: f, Index: int.Parse(Path.GetFileNameWithoutExtension(f)["page-".Length..], System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(f => f.Index)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("Ghostscript wrote no EPS file.");

        var written = new List<string>();
        foreach (var (file, index) in files)
        {
            var pageNumber = pages is not null && index - 1 < pages.Count ? pages[index - 1] + 1 : index;
            var path = step.Output.Allocate("eps", files.Count > 1 ? $"_p{pageNumber}" : null);
            if (path is null)
                continue;
            File.Move(file, path, overwrite: true);
            written.Add(path);
        }
        return written;
    }

    /// <summary>
    /// EPS/PS → PNG with a transparent background where the drawing leaves the page empty, at the preset's render
    /// DPI, EPS cropped to its bounding box. A multi-page PS gives one PNG per page (<c>_p{n}</c>). The preset's image
    /// options (resize, grayscale, DPI, metadata) apply as for any other PNG.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ToPngAsync(string gs, ConversionStep step, CancellationToken ct, IProgress<double>? progress)
    {
        var scratch = NewScratch(step.WorkDirectory);
        var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
        List<string> args = ["-sDEVICE=pngalpha", $"-r{dpi}", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4"];
        if (step.From == "eps")
            args.Add("-dEPSCrop");
        await RunAsync(gs, args, Path.Combine(scratch, "page-%d.png"), step.InputPath, ct, progress);

        var files = Directory.GetFiles(scratch, "page-*.png")
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)["page-".Length..], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("Ghostscript wrote no PNG file.");

        var (format, extension) = ImageEncoder.OutputFormat("png", step.Preset.Image);
        var written = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var path = step.Output.Allocate(extension, files.Count > 1 ? $"_p{i + 1}" : null);
            if (path is null)
                continue;
            using var image = new MagickImage(files[i]);
            ImageEncoder.ApplyOptions(image, step.Preset.Image, "png");
            image.Write(path, format);
            written.Add(path);
        }
        return written;
    }

    /// <summary>The preset's page range as zero-based pages, or null for every page (also when the PDF can't be read).</summary>
    private static IReadOnlyList<int>? SelectedPages(ConversionStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Preset.Pdf.PageRange))
            return null;
        try
        {
            using var document = PdfReader.Open(step.InputPath, PdfDocumentOpenMode.Import);
            return PageRange.Parse(step.Preset.Pdf.PageRange, document.PageCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // PDFsharp can't open every PDF Ghostscript can: convert all pages rather than fail
        }
    }

    /// <summary>
    /// Runs Ghostscript in safe mode. Outputs go to an ASCII-named scratch folder first: in -sOutputFile a '%' is a
    /// page-number placeholder, so arbitrary user paths can't be passed there.
    /// </summary>
    private static async Task RunAsync(string gs, List<string> deviceArgs, string outputFile, string input, CancellationToken ct, IProgress<double>? progress)
    {
        List<string> args =
        [
            "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET",
            .. deviceArgs,
            "-sOutputFile=" + outputFile,
            "-f", Path.GetFullPath(input),
        ];
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(2)))
            result = await ProcessRunner.RunAsync(gs, args, Timeout, ct, environment: CleanEnvironment);

        var produced = outputFile.Contains('%', StringComparison.Ordinal)
            ? Directory.EnumerateFiles(Path.GetDirectoryName(outputFile)!).Any()
            : File.Exists(outputFile) && new FileInfo(outputFile).Length > 0;
        if (result.ExitCode != 0 || !produced)
        {
            var detail = LastLines(result.StandardError + "\n" + result.StandardOutput);
            throw new InvalidOperationException($"Ghostscript failed (exit {result.ExitCode}). {detail}".Trim());
        }
    }

    private static string NewScratch(string workDirectory)
    {
        var folder = Path.Combine(workDirectory, "gs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The end of Ghostscript's messages: the actual error is at the bottom.</summary>
    private static string LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(6));
    }
}
