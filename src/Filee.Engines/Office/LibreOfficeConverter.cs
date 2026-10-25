// Office documents via LibreOffice in headless mode (MPL-2.0): the optional fallback for formats the built-in engines
// don't handle (DOC, XLS, PPT, OpenDocument, ...) and the only reader of rarer ones: Works / WPS, WordPerfect, Lotus
// Word Pro, AbiWord, Apple Pages / Numbers / Keynote, StarOffice, WPS Office, ClarisWorks / AppleWorks, MacWrite, T602,
// Pocket Word, flat OpenDocument, Publisher, CorelDRAW, Visio, CGM. It also writes the OpenDocument templates, FODT,
// XLT and EMF / WMF, and turns SVG into EMF / WMF / ODG and a PDF page into SVG.
// Uses a selected installation, Filee's downloaded copy, or the system installation.
//
// Gotchas handled here:
//  * Each concurrent soffice process needs its own user profile (-env:UserInstallation), otherwise
//    conversions silently fail with exit code 1.
//  * soffice writes <outdir>/<input name>.<ext>; we convert into a private folder and move the result.
//  * Always use a timeout: a document with a modal dialog would otherwise hang forever.
//  * HWP/HWPX import needs the H2Orestart extension (GPL-3.0), pre-installed into the bundled copy.
//  * The first conversion with a new profile takes 2-3× longer (LibreOffice builds its caches), so WarmUpAsync
//    converts a tiny file once per worker profile in the background. --terminate_after_init alone doesn't help.
//  * soffice reports no progress: ProgressEstimate keeps the progress ring moving while it runs.
//  * Its ZIP reader refuses some containers other readers open (Tencent Docs exports, issue #141), saying only "source
//    file could not be loaded": an Office Open XML / OpenDocument file is tried once more as a repacked copy.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Office;

/// <summary>Word processor, spreadsheet, presentation and drawing conversions (and HWP / HWPX) through LibreOffice.</summary>
public sealed class LibreOfficeConverter : IConverter
{
    private const int Workers = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Usual time of one conversion with a prepared profile (drives the progress estimate).</summary>
    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(4);

    // Read and written: FODT (flat XML) and the OpenDocument templates (OTT, OTS, OTP), XLT (Excel 97 template).
    private static readonly string[] Writer = ["docx", "doc", "odt", "rtf", "fodt", "ott"];
    private static readonly string[] Calc = ["xlsx", "xls", "ods", "csv", "xlt", "ots"];
    private static readonly string[] Impress = ["pptx", "ppt", "odp", "otp"];

    // Formats LibreOffice only reads, by the application that opens them (checked against the filter registry of
    // LibreOffice 26.2: the DocumentService of each type's preferred import filter). ClarisWorks / AppleWorks files can
    // also be spreadsheets or drawings; they are taken as word processor files, the common case.
    private static readonly string[] WriterImport = ["dot", "wps", "wpd", "lwp", "abw", "pages", "sdw", "cwk", "mcw", "602", "psw"];
    private static readonly string[] CalcImport = ["et", "numbers", "sdc", "fods"];

    /// <summary>"sda" also covers .sdd: StarDraw files (.sda) open in Draw, StarImpress files (.sdd) in Impress.</summary>
    private static readonly string[] ImpressImport = ["key", "dps", "sda", "fodp"];

    /// <summary>CGM opens in Impress but is a drawing: exported like one (PDF, ODG, SVG, PNG, EMF, WMF).</summary>
    private static readonly string[] ImpressGraphics = ["cgm"];
    private static readonly string[] Draw = ["pub", "cdr", "vsd", "odg", "fodg", "emf", "wmf"];

    /// <summary>What a drawing is exported as. SVG, PNG, EMF and WMF hold the first page.</summary>
    private static readonly string[] DrawTargets = ["pdf", "odg", "svg", "png", "emf", "wmf"];

    /// <summary>
    /// Opened in Draw although another engine reads them better: SVG (only → EMF / WMF / ODG; the vector engine draws it
    /// to PDF and images) and PDF (only → SVG / ODG; one page for SVG, see <see cref="FirstPage"/>).
    /// </summary>
    private static readonly string[] DrawOnlyFor = ["svg", "pdf"];

    /// <summary>Outputs for which LibreOffice lays out document text rather than preserving it for a later editor.</summary>
    private static readonly HashSet<string> RenderedTargets = ["pdf", "png", "svg", "emf", "wmf"];

    private readonly EngineEnvironment _env;
    private readonly ILogger _logger;

    // A worker profile must never be used by two soffice processes at once: conversions and the warm-up take a
    // profile number from _freeProfiles (guarded by itself) after _capacity lets them in.
    private readonly SemaphoreSlim _capacity = new(Workers, Workers);
    private readonly Stack<int> _freeProfiles = new(Enumerable.Range(0, Workers).Reverse());
    private string? _soffice;
    private bool _hasHwpFilter;

    /// <param name="logger">The app's log; notes when a file only loaded after its container was repacked.</param>
    public LibreOfficeConverter(EngineEnvironment env, ILogger? logger = null)
    {
        _env = env;
        _logger = logger ?? NullLogger.Instance;
        Locate();
        Edges = BuildEdges(_hasHwpFilter, EngineEnvironment.LibreOfficeForSlides, EngineEnvironment.LibreOfficeForHwp,
            EngineEnvironment.LibreOfficeForDocuments, EngineEnvironment.LibreOfficeForSheets);
    }

    public string Id => "libreoffice";
    public string DisplayName => "LibreOffice";
    public int MaxParallelism => Workers;
    public IReadOnlyList<ConversionEdge> Edges { get; private set; }

    public EngineStatus GetStatus()
    {
        Locate();
        Edges = BuildEdges(_hasHwpFilter, EngineEnvironment.LibreOfficeForSlides, EngineEnvironment.LibreOfficeForHwp,
            EngineEnvironment.LibreOfficeForDocuments, EngineEnvironment.LibreOfficeForSheets);
        return _soffice is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : !EngineEnvironment.UsesFileesCopy("libreoffice")
                ? EngineStatus.Available(_soffice, _hasHwpFilter ? "H2Orestart" : null)
                : EngineStatus.Available(_soffice, EngineVersions.Component("libreoffice")
                                                   + (_hasHwpFilter ? $" · H2Orestart {EngineVersions.Component("h2orestart")}" : ""));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var soffice = _soffice ?? throw new InvalidOperationException("LibreOffice was not found.");
        if (RenderedTargets.Contains(step.To) && DocumentFonts.Missing(step.OriginalSourcePath ?? step.InputPath) is [_, ..] missing)
            step.MissingFonts?.Invoke(missing);
        var slot = await TakeProfileAsync(cancellationToken);
        try
        {
            progress?.Report(0.1);
            var input = step.From == "pdf" && step.To == "svg" ? FirstPage(step) : step.InputPath;
            var filter = FilterFor(step.From, step.To, step.Preset.Document.PdfA, FormatRegistry.ExtensionOf(step.InputPath));
            var (produced, result) = await ConvertOnceAsync(soffice, slot, filter, step.WorkDirectory, input, progress, cancellationToken);
            if (produced is null && Repack(step.InputPath, step.WorkDirectory) is { } repacked)
            {
                _logger.LogInformation("{File}: LibreOffice could not load it, trying again with the ZIP container repacked", step.InputPath);
                (produced, _) = await ConvertOnceAsync(soffice, slot, filter, step.WorkDirectory, repacked, progress, cancellationToken);
                _logger.LogInformation("{File}: the repacked copy {Result}", step.InputPath, produced is null ? "failed too" : "converted");
            }
            // The first attempt's error, which is about the user's own file.
            if (produced is null)
                throw new InvalidOperationException(
                    $"LibreOffice produced no output (exit {result.ExitCode}). {result.StandardError.Trim()}");

            // Calc sometimes turns stale Excel print tiles into completely empty PDF pages anywhere in the output.
            // Only remove pages with no painting operation at all; pages containing text, borders, charts, pictures
            // or annotations stay. PDF/A is never rewritten: doing so would invalidate LibreOffice's XMP metadata.
            try
            {
                var removed = RemoveEmptySpreadsheetPages(step, produced);
                if (removed > 0)
                {
                    _logger.LogInformation("{File}: removed {Count} empty spreadsheet PDF pages", step.InputPath, removed);
                    step.Notice?.Invoke("notice.empty_spreadsheet_pages_removed");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Post-processing is an improvement, not a reason to discard LibreOffice's otherwise valid PDF.
                _logger.LogInformation("{File}: could not check empty spreadsheet PDF pages: {Message}", step.InputPath, e.Message);
            }

            var target = step.Output.Allocate(step.To);
            if (target is null)
                return [];
            File.Move(produced, target, overwrite: true);
            progress?.Report(1);
            return [target];
        }
        finally
        {
            ReturnProfile(slot);
        }
    }

    /// <summary>
    /// Removes empty Calc PDF pages when that post-processing is safe. PDF/A stays byte-for-byte as LibreOffice wrote
    /// it because PDFsharp changes the Info dictionary without updating the archival XMP metadata.
    /// </summary>
    internal static int RemoveEmptySpreadsheetPages(ConversionStep step, string path) =>
        step.To == "pdf" && !step.Preset.Document.PdfA
        && FamilyOf(step.From, FormatRegistry.ExtensionOf(step.InputPath)) == "calc"
            ? PdfBlankPages.Remove(path) : 0;

    /// <summary>Runs LibreOffice into a fresh folder; returns the file it wrote (null if none) and how it ended.</summary>
    private async Task<(string? Produced, ProcessResult Result)> ConvertOnceAsync(string soffice, int slot, string filter, string workDirectory,
        string input, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var outDir = Path.Combine(workDirectory, "lo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TypicalDuration))
            result = await RunAsync(soffice, slot, filter, outDir, input, cancellationToken);
        return (Directory.EnumerateFiles(outDir).FirstOrDefault(f => new FileInfo(f).Length > 0), result);
    }

    /// <summary>
    /// A copy of an Office Open XML or OpenDocument file with its ZIP container rewritten by <see cref="PackageRepacker"/>,
    /// under the same file name (LibreOffice also goes by the name). Null for other files and unreadable containers.
    /// </summary>
    private string? Repack(string input, string workDirectory)
    {
        if (!PackageRepacker.IsPackage(input))
            return null;
        try
        {
            var folder = Path.Combine(workDirectory, "lo-repack-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(input));
            PackageRepacker.Repack(input, copy);
            return copy;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("{File}: could not repack the ZIP container: {Message}", input, e.Message);
            return null;
        }
    }

    /// <summary>
    /// The first page of the preset's page range as a PDF of its own: Draw's SVG export writes every page of a PDF on
    /// top of each other, so it gets one page (like the other drawings, whose SVG holds the first page).
    /// </summary>
    private static string FirstPage(ConversionStep step)
    {
        using var source = PdfReader.Open(step.InputPath, PdfDocumentOpenMode.Import);
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, source.PageCount);
        if (pages.Count == 0)
            throw new InvalidOperationException("The PDF has no pages in the selected range.");
        using var single = new PdfDocument();
        single.AddPage(source.Pages[pages[0]]);
        var folder = Path.Combine(step.WorkDirectory, "lo-page-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FormatRegistry.NameWithoutExtension(step.InputPath) + ".pdf");
        single.Save(path);
        return path;
    }

    /// <summary>
    /// Prepares the worker profiles that were never used, by converting a tiny file with each of them, so the user's
    /// first conversion doesn't pay for LibreOffice's first start. Quick no-op when LibreOffice is missing or the
    /// profiles are ready. A conversion that starts meanwhile gets the other profile or waits for this one.
    /// </summary>
    /// <returns>Number of profiles prepared.</returns>
    public async Task<int> WarmUpAsync(CancellationToken cancellationToken = default)
    {
        Locate();
        if (_soffice is not { } soffice || Enumerable.Range(0, Workers).All(IsPrepared))
            return 0;

        var prepared = 0;
        int? held = null;
        try
        {
            for (var i = 0; i < Workers; i++)
            {
                // Taken while the previous one is still held, so every round gets a different profile.
                var slot = await TakeProfileAsync(cancellationToken);
                if (held is { } previous)
                    ReturnProfile(previous);
                held = slot;
                if (IsPrepared(slot))
                    continue;

                var folder = Path.Combine(Path.GetTempPath(), "filee-warmup-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(folder);
                    var input = Path.Combine(folder, "warmup.txt");
                    await File.WriteAllTextAsync(input, "Filee", cancellationToken);
                    await RunAsync(soffice, slot, FilterFor("txt", "pdf", pdfA: false), folder, input, cancellationToken);
                    prepared++;
                }
                finally
                {
                    try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            if (held is { } last)
                ReturnProfile(last);
        }
        return prepared;
    }

    private string ProfileDirectory(int slot) => Path.Combine(_env.DataDirectory, "libreoffice-profiles", $"worker-{slot}");

    /// <summary>LibreOffice has started with this profile before (it creates the "user" folder).</summary>
    private bool IsPrepared(int slot) => Directory.Exists(Path.Combine(ProfileDirectory(slot), "user"));

    private async Task<int> TakeProfileAsync(CancellationToken cancellationToken)
    {
        await _capacity.WaitAsync(cancellationToken);
        lock (_freeProfiles)
            return _freeProfiles.Pop();
    }

    private void ReturnProfile(int slot)
    {
        lock (_freeProfiles)
            _freeProfiles.Push(slot);
        _capacity.Release();
    }

    private async Task<ProcessResult> RunAsync(string soffice, int slot, string filter, string outDir, string input, CancellationToken cancellationToken)
    {
        var profileDir = ProfileDirectory(slot);
        Directory.CreateDirectory(profileDir);
        var args = new List<string>
        {
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
            "-env:UserInstallation=" + new Uri(profileDir).AbsoluteUri,
        };
        // The HWP import filter (H2Orestart) is written in Java: point LibreOffice at the bundled JRE.
        if (EngineEnvironment.FindBundled("jre") is { } jre)
        {
            if (Directory.Exists(Path.Combine(jre, "Contents", "Home")))
                jre = Path.Combine(jre, "Contents", "Home");
            args.Add("-env:UNO_JAVA_JFW_JREHOME=" + new Uri(jre + Path.DirectorySeparatorChar).AbsoluteUri);
        }
        args.AddRange(["--convert-to", filter, "--outdir", outDir, input]);
        return await ProcessRunner.RunAsync(soffice, args, Timeout, cancellationToken);
    }

    /// <summary>Maps a target format to a LibreOffice <c>--convert-to</c> filter specification.</summary>
    /// <param name="extension">Extension of the input file, for format ids that cover files of two applications.</param>
    internal static string FilterFor(string from, string to, bool pdfA, string? extension = null)
    {
        var family = FamilyOf(from, extension);
        return to switch
        {
            // PDF/A-2b via the JSON filter options syntax (LibreOffice 7.4+).
            "pdf" when pdfA => $"pdf:{family}_pdf_Export:{{\"SelectPdfVersion\":{{\"type\":\"long\",\"value\":\"2\"}}}}",
            "pdf" => $"pdf:{family}_pdf_Export",
            "docx" => "docx:MS Word 2007 XML",
            "doc" => "doc:MS Word 97",
            "odt" => "odt",
            "fodt" => "fodt:OpenDocument Text Flat XML",
            "ott" => "ott:writer8_template",
            "rtf" => "rtf:Rich Text Format",
            "txt" => "txt:Text (encoded):UTF8",
            "html" when family == "calc" => "html:HTML (StarCalc)",
            "html" => "html:HTML (StarWriter)",
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "xls" => "xls:MS Excel 97",
            "xlt" => "xlt:MS Excel 97 Vorlage/Template",
            "ods" => "ods",
            "ots" => "ots:calc8_template",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "ppt" => "ppt:MS PowerPoint 97",
            "odp" => "odp",
            "otp" => "otp:impress8_template",
            "odg" when family == "impress" => "odg:impress8_draw",
            "odg" => "odg:draw8",
            "svg" => $"svg:{family}_svg_Export",
            "png" => $"png:{family}_png_Export",
            "emf" => $"emf:{family}_emf_Export",
            "wmf" => $"wmf:{family}_wmf_Export",
            _ => to,
        };
    }

    /// <summary>The LibreOffice application that opens a format: writer, calc, impress or draw.</summary>
    private static string FamilyOf(string from, string? extension)
    {
        if (from == "sda")
            return string.Equals(extension, "sda", StringComparison.OrdinalIgnoreCase) ? "draw" : "impress";
        if (Calc.Contains(from) || CalcImport.Contains(from))
            return "calc";
        if (Impress.Contains(from) || ImpressImport.Contains(from) || ImpressGraphics.Contains(from))
            return "impress";
        return Draw.Contains(from) || DrawOnlyFor.Contains(from) ? "draw" : "writer";
    }

    /// <param name="hwp">H2Orestart is installed (HWP / HWPX import).</param>
    /// <param name="slides">Presentations → PDF should go through LibreOffice (<see cref="EngineEnvironment.LibreOfficeForSlides"/>).</param>
    /// <param name="hwpToPdf">HWP / HWPX → PDF should go through LibreOffice (<see cref="EngineEnvironment.LibreOfficeForHwp"/>).</param>
    /// <param name="documents">Word documents → PDF should go through LibreOffice (<see cref="EngineEnvironment.LibreOfficeForDocuments"/>).</param>
    /// <param name="sheets">Spreadsheets → PDF should go through LibreOffice (<see cref="EngineEnvironment.LibreOfficeForSheets"/>).</param>
    internal static List<ConversionEdge> BuildEdges(bool hwp, bool slides, bool hwpToPdf, bool documents = false, bool sheets = false)
    {
        var edges = new List<ConversionEdge>();
        void Add(IEnumerable<string> sources, IEnumerable<string> targets, int cost)
        {
            foreach (var s in sources)
                foreach (var t in targets)
                    if (s != t)
                        // An image that reached a drawing or a PDF would come back as a picture inside the SVG:
                        // images are traced instead.
                        edges.Add(new ConversionEdge(s, t, cost, NotForImageSources: t == "svg"));
        }

        // LibreOffice is the fallback: its costs make the built-in engines win wherever they can do the job
        // (DOCX / XLSX / PPTX / TXT → PDF through the HWPX writer and rhwp, XLSX ↔ CSV, ...), so it is only used for
        // what nothing else reads or writes (DOC, XLS, PPT, OpenDocument, DOCX output, ...).
        const int Fallback = 25;
        // Chosen in Settings → Engines: cheap enough to beat the built-in route even at the bottom of the engine list
        // (its priority adds at most 9; PPTX → HWPX → PDF costs about 32, rhwp's HWP → PDF about 13).
        const int Preferred = 0;
        // Word and spreadsheet sources the built-in route also reads: cheap enough to win for the file itself (10 + 9
        // against about 31 through the HWPX writer and rhwp), not as a hop for other formats, which the DOCX writer can
        // turn into DOCX (Excel → DOCX → LibreOffice would cost 17 + 19).
        const int Chosen = 10;
        Add(Writer, [.. Writer, "txt", "html"], Fallback);
        Add(Writer, ["pdf"], documents ? Chosen : Fallback);
        Add(Calc, [.. Calc, "html"], Fallback);
        Add(Calc, ["pdf"], sheets ? Chosen : Fallback);
        Add(Impress, Impress, Fallback);
        Add(Impress, ["pdf"], slides ? Preferred : Fallback);
        Add(["txt", "html"], ["docx", "odt", "pdf"], Fallback + 3);
        // Formats only LibreOffice reads, to PDF and the editable formats of their kind.
        Add(WriterImport, [.. Writer, "pdf", "txt", "html"], Fallback);
        Add(CalcImport, [.. Calc, "pdf", "html"], Fallback);
        Add(ImpressImport, [.. Impress, "pdf"], Fallback);
        Add([.. ImpressGraphics, .. Draw], DrawTargets, Fallback);
        Add(["svg"], ["odg", "emf", "wmf"], Fallback);
        Add(["pdf"], ["svg", "odg"], Fallback);
        if (hwp)
        {
            Add(["hwp", "hwpx"], ["docx", "odt", "rtf"], Fallback + 6);
            Add(["hwp", "hwpx"], ["pdf"], hwpToPdf ? Preferred : Fallback + 6);
        }
        return edges;
    }

    /// <summary>
    /// The user's own LibreOffice when they chose one in Settings → Engines, else Filee's copy (engines/libreoffice),
    /// else a LibreOffice installed on the PC. Each conversion uses its own profile, so an open LibreOffice is untouched.
    /// </summary>
    private void Locate()
    {
        _soffice = EngineEnvironment.OwnProgram("libreoffice", "soffice.exe")
            ?? WithRuntime(EngineEnvironment.BundledProgram("libreoffice", "soffice.exe"))
            ?? EngineEnvironment.SystemProgram("libreoffice", "soffice.exe");
        _hasHwpFilter = _soffice is not null && HasH2Orestart(_soffice);
    }

    /// <summary>
    /// Filee's copy as earlier versions installed it has LibreOffice's C++ runtime only in System64, where soffice.exe
    /// does not look (see <see cref="AppLocalRuntime.CopyLibreOfficeRuntime"/>): copied next to it once, so LibreOffice
    /// also starts on PCs without the Visual C++ Redistributable.
    /// </summary>
    private string? WithRuntime(string? soffice)
    {
        if (soffice is null || !OperatingSystem.IsWindows())
            return soffice;
        try
        {
            var copied = AppLocalRuntime.CopyLibreOfficeRuntime(Directory.GetParent(Path.GetDirectoryName(soffice)!)!.FullName);
            if (copied > 0)
                _logger.LogInformation("Copied {Count} C++ runtime files next to {Program}", copied, soffice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy the C++ runtime next to {Program}", soffice);
        }
        return soffice;
    }

    /// <summary>Detects the H2Orestart extension in the installation (shared) extension folders.</summary>
    private static bool HasH2Orestart(string soffice)
    {
        try
        {
            soffice = File.ResolveLinkTarget(soffice, returnFinalTarget: true)?.FullName ?? soffice;
            var root = Directory.GetParent(Path.GetDirectoryName(soffice)!)!.FullName;
            foreach (var sub in new[] { "share/uno_packages", "share/extensions", "Resources/uno_packages", "Resources/extensions" })
            {
                var dir = Path.Combine(root, sub);
                if (Directory.Exists(dir) &&
                    Directory.EnumerateFileSystemEntries(dir, "*H2Orestart*", SearchOption.AllDirectories).Any())
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
