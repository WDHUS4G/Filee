// Text recognition (OCR) with Apple Vision on macOS: the same "Recognize text" step as TesseractConverter (scans and
// pictures → searchable PDF, read on into DOCX, TXT, Markdown and HWPX), with macOS's own recognizer, so a Mac needs no
// download for the languages Vision reads. It has the same OCR edges as Tesseract and is registered before it, so on
// a Mac it is the one the route planner takes; a language Vision doesn't read (VisionLanguages) goes to Tesseract when
// that is installed, and is refused with a hint to install it otherwise. On Windows and Linux it is unavailable.
//
// PDFs go page by page like Tesseract's (TesseractConverter.RecognizePdfAsync: pages with text are copied, scans are
// rendered at 300 dpi); each recognized page is the picture with the words as an invisible text layer (OcrPdfWriter).
// A JPEG picture goes into the PDF as it is; other pictures (and a JPEG turned by its EXIF orientation) as PNG, every
// page of a TIFF.

using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Ocr;

/// <summary>Scans and pictures → searchable PDF through Apple Vision (macOS).</summary>
public sealed class VisionConverter : IConverter
{
    private readonly ITextRecognizer? _recognizer;
    private readonly IConverter _tesseract;

    public VisionConverter()
        : this(OperatingSystem.IsMacOS() ? new AppleVision() : null, new TesseractConverter())
    {
    }

    /// <param name="tesseract">Reads the languages Vision doesn't, when it is installed.</param>
    internal VisionConverter(ITextRecognizer? recognizer, IConverter tesseract)
    {
        _recognizer = recognizer;
        _tesseract = tesseract;
    }

    /// <summary>Vision uses the Neural Engine or the GPU: a few pages at a time keep it busy.</summary>
    internal static int Runs => Math.Clamp(Environment.ProcessorCount, 1, 2);

    public string Id => "vision";
    public string DisplayName => "Apple Vision";

    /// <summary>One file at a time; its pages are recognized in parallel.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. TesseractConverter.ImageSources.Select(f => new ConversionEdge(f, "pdf", Ocr: true)), new("pdf", "pdf", Ocr: true)];

    public EngineStatus GetStatus()
    {
        if (_recognizer is null)
            return EngineStatus.Unavailable("engine.reason.unsupported_platform");
        if (!_recognizer.IsAvailable)
            return EngineStatus.Unavailable("engine.reason.not_installed", "Vision.framework");
        return EngineStatus.Available(string.Join(", ", _recognizer.Languages), $"macOS {Environment.OSVersion.Version}");
    }

    /// <summary>
    /// True when Apple Vision reads text in <paramref name="code"/> (a Tesseract code, PdfOptions.OcrLanguage) on
    /// this computer, so that language needs no download.
    /// </summary>
    public bool Reads(string code) =>
        _recognizer is { IsAvailable: true } recognizer && VisionLanguages.For(code, recognizer.Languages) is not null;

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var recognizer = _recognizer is { IsAvailable: true } available ? available
            : throw new PlatformNotSupportedException("Apple Vision text recognition runs on macOS only.");
        var language = OcrLanguages.Find(step.Preset.Pdf.OcrLanguage) ?? OcrLanguages.Find(OcrLanguages.Default)!;
        if (VisionLanguages.For(language.Code, recognizer.Languages) is not { } languages)
        {
            if (_tesseract.GetStatus().IsAvailable)
                return await _tesseract.ConvertAsync(step, progress, cancellationToken);
            throw new ConversionRefusedException("error.ocr_needs_tesseract", language.NativeName);
        }

        var scratch = Path.Combine(step.WorkDirectory, "vision-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        progress?.Report(0.05);
        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        if (step.From == "pdf")
        {
            await TesseractConverter.RecognizePdfAsync(step, scratch, output, Runs, (picture, page, token) => Task.Run(() =>
            {
                var path = Path.Combine(scratch, picture);
                var (width, height) = PixelSize(path);
                using var writer = new OcrPdfWriter();
                writer.AddPage(path, Points(width, TesseractConverter.Dpi), Points(height, TesseractConverter.Dpi),
                    Recognize(recognizer, path, languages, token));
                writer.Save(Path.Combine(scratch, page + ".pdf"));
            }, token), progress, cancellationToken);
        }
        else
        {
            await Task.Run(() =>
            {
                using var writer = new OcrPdfWriter();
                foreach (var page in Pictures(step.InputPath, step.From, scratch))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.AddPage(page.Path, Points(page.Width, page.Dpi), Points(page.Height, page.Dpi),
                        Recognize(recognizer, page.Path, languages, cancellationToken));
                }
                writer.Save(output);
            }, cancellationToken);
        }
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// Reads one picture. When Vision fails with a second language (English) next to the one asked for, it reads again
    /// with that one alone.
    /// </summary>
    internal static IReadOnlyList<OcrLine> Recognize(ITextRecognizer recognizer, string picture, IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        try
        {
            return recognizer.Recognize(picture, languages, cancellationToken);
        }
        catch (InvalidOperationException) when (languages.Count > 1 && !cancellationToken.IsCancellationRequested)
        {
            return recognizer.Recognize(picture, [languages[0]], cancellationToken);
        }
    }

    /// <summary>A picture's pages: where each is, its size in pixels and its resolution.</summary>
    internal sealed record Picture(string Path, uint Width, uint Height, double Dpi);

    /// <summary>
    /// The pages of a picture as files PDFsharp embeds: a JPEG as it is (unless EXIF turns it), anything else as PNG
    /// in <paramref name="scratch"/>, turned upright; every page of a TIFF, the first frame of anything else.
    /// </summary>
    internal static IEnumerable<Picture> Pictures(string path, string format, string scratch)
    {
        if (format == "jpg")
        {
            using var info = new MagickImage();
            info.Ping(path);
            if (info.Orientation is OrientationType.Undefined or OrientationType.TopLeft)
            {
                yield return new Picture(path, info.Width, info.Height, Dpi(info));
                yield break;
            }
        }
        using var frames = new MagickImageCollection();
        frames.Read(path, new MagickReadSettings { FrameIndex = format == "tiff" ? null : 0u });
        var index = 0;
        foreach (var frame in frames)
        {
            var dpi = Dpi(frame);
            frame.AutoOrient();
            var png = Path.Combine(scratch, $"picture-{index++}.png");
            frame.Write(png, MagickFormat.Png);
            yield return new Picture(png, frame.Width, frame.Height, dpi);
        }
    }

    /// <summary>
    /// The resolution the picture says it has, or 300 dpi when it says none (a scanner's resolution; like
    /// Tesseract, which would otherwise assume 70 dpi and make a huge page).
    /// </summary>
    internal static double Dpi(IMagickImage image)
    {
        var density = image.Density;
        if (density.X <= 1)
            return TesseractConverter.Dpi;
        return density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
    }

    private static double Points(uint pixels, double dpi) => pixels * 72.0 / dpi;

    private static (uint Width, uint Height) PixelSize(string path)
    {
        using var info = new MagickImage();
        info.Ping(path);
        return (info.Width, info.Height);
    }
}
