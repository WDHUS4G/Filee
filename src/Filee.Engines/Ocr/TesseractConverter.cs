// Text recognition (OCR) with Tesseract (Apache-2.0, a separate program downloaded on demand into engines/tesseract;
// on Linux and macOS the one installed with the system's packages). "Recognize text" (PdfOptions.Ocr) turns a scan or
// a picture into a searchable PDF: the page as it looks, with the recognized words as an invisible text layer on top.
// The route planner puts this step first and goes on from the PDF, so DOCX, TXT, Markdown and HWPX get the text
// (PdfDocumentReader reads the layer when the preset asks for OCR).
//
// A PDF keeps its pages that already have text (born-digital pages are never turned into pictures); only pages
// without any are rendered at 300 dpi and recognized, page by page, at most four at a time. The language data comes from OcrLanguages,
// downloaded the first time a language is used, and is copied into the job's folder so Tesseract reads only it.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

// PDFtoImage marks its API for the platforms PDFium ships for; Filee only targets desktop platforms, all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Ocr;

/// <summary>Scans and pictures → searchable PDF through Tesseract.</summary>
public sealed class TesseractConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Scans are rendered at this resolution, which Tesseract reads best (and the page keeps its size).</summary>
    internal const int Dpi = 300;

    /// <summary>
    /// Tesseract runs side by side at most: enough to keep a desktop busy, few enough for memory and disk on a long scan
    /// (each run holds one 300 dpi page). A PC with fewer cores runs fewer.
    /// </summary>
    internal static int Runs => Math.Clamp(System.Environment.ProcessorCount, 1, 4);

    /// <summary>Pictures Tesseract opens itself (Leptonica); others become a PNG first (RoutePlanner).</summary>
    internal static readonly IReadOnlyList<string> ImageSources = ["png", "jpg", "tiff", "bmp", "webp", "gif"];

    /// <summary>
    /// One thread per Tesseract: Filee runs several pages at a time, and Tesseract's own threads would only compete
    /// (its documentation recommends this). TESSDATA_PREFIX of the system is not used, the job's folder is.
    /// </summary>
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["OMP_THREAD_LIMIT"] = "1",
        ["TESSDATA_PREFIX"] = "",
    };

    private string? _executable;

    public string Id => "tesseract";
    public string DisplayName => "Tesseract OCR";

    /// <summary>One file at a time; its pages are recognized in parallel.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. ImageSources.Select(f => new ConversionEdge(f, "pdf", Ocr: true)), new("pdf", "pdf", Ocr: true)];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("tesseract") ? EngineVersions.Component("tesseract") : null);
    }

    /// <summary>The user's chosen copy, Filee's download, then one installed on the system.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("tesseract", "tesseract.exe") ?? EngineEnvironment.BundledProgram("tesseract", "tesseract.exe")
        ?? EngineEnvironment.SystemProgram("tesseract", "tesseract.exe");

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tesseract = _executable ?? Locate() ?? throw new InvalidOperationException("Tesseract is not installed.");
        var language = OcrLanguages.Find(step.Preset.Pdf.OcrLanguage) ?? OcrLanguages.Find(OcrLanguages.Default)!;
        string data;
        try
        {
            data = await OcrLanguages.EnsureAsync(language.Code, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ConversionRefusedException("error.ocr_language_download", $"{language.NativeName}: {ex.Message}");
        }
        var scratch = Path.Combine(step.WorkDirectory, "ocr-" + Guid.NewGuid().ToString("N")[..8]);
        var tessdata = Path.Combine(scratch, "tessdata");
        Directory.CreateDirectory(tessdata);
        File.Copy(data, Path.Combine(tessdata, Path.GetFileName(data)));
        var ocr = new Run(tesseract, scratch, language.Code);
        progress?.Report(0.05);

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        if (step.From == "pdf")
        {
            await RecognizePdfAsync(step, scratch, output, Runs, (picture, page, token) => ocr.ToPdfAsync(picture, page, Dpi, token),
                progress, cancellationToken);
        }
        else
        {
            // A copy with a plain name: the picture's own folder and name may not be readable for Tesseract (Run).
            var picture = "picture" + Path.GetExtension(step.InputPath).ToLowerInvariant();
            File.Copy(step.InputPath, Path.Combine(scratch, picture));
            await ocr.ToPdfAsync(picture, "page", HasResolution(step.InputPath) ? null : Dpi, cancellationToken);
            File.Move(Path.Combine(scratch, "page.pdf"), output, overwrite: true);
        }
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// The pages without text become recognized scans, the others are copied. A PDF whose pages all have text is
    /// copied as it is, with a note. Also Apple Vision's way (<see cref="VisionConverter"/>).
    /// </summary>
    /// <param name="parallel">How many pages are recognized at the same time.</param>
    /// <param name="recognize">
    /// Reads the picture of a page (<c>p{n}.png</c> in <paramref name="scratch"/>, at <see cref="Dpi"/>) and writes it
    /// as a searchable one-page PDF (<c>p{n}.pdf</c> there); it gets both names relative to <paramref name="scratch"/>.
    /// </param>
    internal static async Task RecognizePdfAsync(ConversionStep step, string scratch, string output, int parallel,
        Func<string, string, CancellationToken, Task> recognize, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(step.InputPath, cancellationToken);
        var hasText = PagesWithText(step.InputPath);
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, hasText.Count);
        var scans = pages.Where(p => !hasText[p]).ToList();
        if (scans.Count == 0 && pages.Count == hasText.Count)
        {
            step.Notice?.Invoke("notice.ocr_has_text");
            File.Copy(step.InputPath, output, overwrite: true);
            return;
        }

        // Page by page: PDFium draws a page only when one of the Tesseract runs is free, and a page's picture is deleted
        // as soon as its PDF exists, so a 300-page scan never has more than Runs pictures in the work folder.
        var options = new RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
        using var free = new SemaphoreSlim(parallel);
        var runs = new List<Task>();
        var done = 0;
        try
        {
            foreach (var page in scans)
            {
                await free.WaitAsync(cancellationToken);
                if (runs.Any(r => r.IsFaulted))
                {
                    free.Release();
                    break; // a page failed: stop rendering, the error comes out below
                }
                var picture = Path.Combine(scratch, $"p{page}.png");
                try
                {
                    using var bitmap = Conversion.ToImage(bytes, page, null, options);
                    await using var file = File.Create(picture);
                    bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
                }
                catch
                {
                    free.Release();
                    throw;
                }
                runs.Add(Task.Run(async () =>
                {
                    try
                    {
                        await recognize($"p{page}.png", $"p{page}", cancellationToken);
                        File.Delete(picture);
                        progress?.Report(0.05 + 0.9 * Interlocked.Increment(ref done) / scans.Count);
                    }
                    finally
                    {
                        free.Release();
                    }
                }, cancellationToken));
            }
        }
        finally
        {
            // Rendering stopped (a failed page, a cancel): the running pages end before the work folder can go.
            await Task.WhenAll(runs).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        await Task.WhenAll(runs);

        // A new document: the pages and the document information come along (not when the preset removes metadata),
        // bookmarks and form fields don't.
        using var source = PdfSharpConverter.Open(step.InputPath, PdfDocumentOpenMode.Import);
        using var result = new PdfDocument();
        if (!step.Preset.Pdf.RemoveMetadata)
        {
            result.Info.Title = source.Info.Title;
            result.Info.Author = source.Info.Author;
            result.Info.Subject = source.Info.Subject;
            result.Info.Keywords = source.Info.Keywords;
        }
        var recognized = new List<PdfDocument>();
        try
        {
            foreach (var page in pages)
            {
                if (hasText[page])
                {
                    result.AddPage(source.Pages[page]);
                    continue;
                }
                var scan = PdfReader.Open(Path.Combine(scratch, $"p{page}.pdf"), PdfDocumentOpenMode.Import);
                recognized.Add(scan);
                result.AddPage(scan.Pages[0]);
            }
            result.Save(output);
        }
        finally
        {
            recognized.ForEach(d => d.Dispose());
        }
    }

    /// <summary>
    /// For each page: true when it has text of its own. An OCR layer (invisible text) always counts. A scan with a
    /// stamp does not: a picture over most of the page whose only text is a little visible text (a page number or a
    /// Bates number the scanner software added, under <see cref="StampCoverage"/> of the page).
    /// </summary>
    internal static List<bool> PagesWithText(string path)
    {
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            return [.. pdf.GetPages().Select(HasText)];
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }
    }

    /// <summary>The share of a page that visible text may cover on a scan and still be only a stamp.</summary>
    internal const double StampCoverage = 0.01;

    private static bool HasText(UglyToad.PdfPig.Content.Page page)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0)
            return false;
        if (letters.Any(l => l.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip))
            return true;
        var area = page.Width * page.Height;
        if (area <= 0 || letters.Sum(l => l.GlyphRectangle.Area) >= StampCoverage * area)
            return true;
        // A page drawn with little text and no picture under it (a cover, a short note) is not a scan.
        return !page.GetImages().Any(image => image.Bounds.Area >= area / 2);
    }

    /// <summary>
    /// True when the picture says how large it is meant to be. Without that Tesseract guesses 70 dpi (a page of a
    /// metre and more); 300 dpi is what scanners write.
    /// </summary>
    private static bool HasResolution(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            return info.Density is { X: > 1 };
        }
        catch (MagickException)
        {
            return false;
        }
    }

    /// <summary>
    /// One language, one Tesseract, working in <paramref name="Folder"/> (the job's scratch folder, with the language in
    /// its <c>tessdata</c>). Tesseract only ever sees plain relative names there: some Windows builds of Tesseract and
    /// Leptonica open files through the ANSI code page and fail on a user name or folder in Korean or Cyrillic.
    /// </summary>
    private sealed record Run(string Executable, string Folder, string Language)
    {
        /// <summary>Reads <paramref name="input"/> and writes <c>{outputBase}.pdf</c>, both names in <see cref="Folder"/>.</summary>
        public async Task ToPdfAsync(string input, string outputBase, int? dpi, CancellationToken cancellationToken)
        {
            // tessedit_create_pdf instead of the "pdf" config file, which lives in Tesseract's own tessdata folder.
            List<string> args = [input, outputBase, "--tessdata-dir", "tessdata", "-l", Language, "-c", "tessedit_create_pdf=1"];
            if (dpi is { } value)
                args.AddRange(["--dpi", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            var result = await ProcessRunner.RunAsync(Executable, args, Timeout, cancellationToken, workingDirectory: Folder, environment: Environment);
            if (result.ExitCode != 0 || !File.Exists(Path.Combine(Folder, outputBase + ".pdf")))
                throw new InvalidOperationException($"Tesseract failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        }
    }
}
