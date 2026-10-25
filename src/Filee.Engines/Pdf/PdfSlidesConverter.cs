// PDF → PPTX: one slide per page showing the page as a picture, so the slides look exactly like the PDF (they are not
// editable). Pages are rendered with PDFium (via PDFtoImage) at the preset's resolution and written by
// PictureDeckWriter; the text of each page (PdfPig, reading order, like PDF → TXT) goes into the slide's speaker notes,
// so it can still be searched and copied.
//
// - Slide size: the first page's own size, so A4 portrait gives portrait slides and a 16:9 PDF 16:9 slides. Every
//   page, also one of another size or orientation, is fitted onto the slide without cropping and centred on white.
// - Resolution: the preset's render DPI, at least 150 (sharp on a projector) and at most 600; a page over 25
//   megapixels at that resolution (a poster) is rendered smaller, so memory and file size stay reasonable.
// - Pictures are JPEG at the preset's quality (90 by default). Pages are rendered on white as every PDF viewer shows
//   them, so they have no transparency that would need PNG, and JPEG keeps a 100-page deck a manageable size.
// - The edge is DirectOnly: only a PDF becomes picture slides, and a picture deck is never a step towards another
//   format (ODP, PPSX, ...).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for; Filee only targets desktop platforms, all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>PDF → PPTX with a picture of each page on its own slide.</summary>
public sealed class PdfSlidesConverter : IConverter
{
    internal const int MinDpi = 150;
    internal const int MaxDpi = 600;

    /// <summary>A page larger than this many pixels at the preset's resolution is rendered at a lower one.</summary>
    internal const double MaxPagePixels = 25_000_000;

    public string Id => "pdf-slides";
    public string DisplayName => "PDF slides (built-in)";

    /// <summary>PDFium renders one page at a time anyway; one deck at a time keeps memory predictable.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("pdf", "pptx", DirectOnly: true)];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("PDF → PPTX", $"{EngineVersions.BuiltIn} · {EngineVersions.Library("PDFtoImage", typeof(Conversion))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var pdf = File.ReadAllBytes(step.InputPath);
            int pageCount;
            IList<System.Drawing.SizeF> sizes;
            try
            {
                pageCount = Conversion.GetPageCount(pdf, null);
                sizes = Conversion.GetPageSizes(pdf, null);
            }
            catch (PdfPasswordProtectedException)
            {
                throw PdfDocumentReader.PasswordProtected();
            }
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, pageCount);
            if (pages.Count == 0)
                throw new InvalidOperationException("The PDF has no pages.");

            var output = step.Output.Allocate("pptx");
            if (output is null)
                return [];

            var notes = PageTexts(step.InputPath, pages, cancellationToken);
            progress?.Report(0.1);
            var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, MinDpi, MaxDpi);
            var quality = Math.Clamp(step.Preset.Image.Quality, 1, 100);

            PictureDeckWriter? deck = null;
            try
            {
                var done = 0;
                // Pages that render at the same resolution go through PDFium together (one document load).
                foreach (var run in Runs(pages, page => PageDpi(sizes[page].Width, sizes[page].Height, dpi)))
                {
                    var options = new RenderOptions(Dpi: run.Dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
                    var index = 0;
                    foreach (var bitmap in Conversion.ToImages(pdf, run.Pages, null, options))
                    {
                        using (bitmap)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var page = run.Pages[index++];
                            var (width, height) = Shown(sizes[page], bitmap);
                            if (deck is null)
                            {
                                var (slideWidth, slideHeight) = PictureDeckWriter.SlideSize(width, height);
                                deck = new PictureDeckWriter(output, slideWidth, slideHeight);
                            }
                            using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
                            deck.AddSlide(jpeg.AsSpan(), width, height, $"Page {page + 1}", notes?[done]);
                        }
                        done++;
                        progress?.Report(0.1 + 0.85 * done / pages.Count);
                    }
                }
                (deck ?? throw new InvalidOperationException("PDFium rendered no page of the PDF.")).Save();
            }
            finally
            {
                deck?.Dispose();
            }
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>
    /// Resolution for a page of <paramref name="width"/> × <paramref name="height"/> points: <paramref name="dpi"/>, or
    /// less when the page would exceed <see cref="MaxPagePixels"/>.
    /// </summary>
    internal static int PageDpi(double width, double height, int dpi)
    {
        var pixels = width / 72 * dpi * (height / 72 * dpi);
        if (pixels <= MaxPagePixels || pixels <= 0)
            return dpi;
        return Math.Max(1, (int)Math.Floor(dpi * Math.Sqrt(MaxPagePixels / pixels)));
    }

    /// <summary>Consecutive pages with the same resolution.</summary>
    private static List<(int Dpi, List<int> Pages)> Runs(IReadOnlyList<int> pages, Func<int, int> dpiOf)
    {
        var runs = new List<(int Dpi, List<int> Pages)>();
        foreach (var page in pages)
        {
            var dpi = dpiOf(page);
            if (runs.Count == 0 || runs[^1].Dpi != dpi)
                runs.Add((dpi, []));
            runs[^1].Pages.Add(page);
        }
        return runs;
    }

    /// <summary>
    /// The page's size in points as it is shown: PDFium turns pages that are stored rotated, so the rendering tells
    /// whether width and height swap.
    /// </summary>
    private static (double Width, double Height) Shown(System.Drawing.SizeF size, SKBitmap bitmap)
    {
        double width = size.Width, height = size.Height;
        var landscapePage = width > height;
        var landscapePicture = bitmap.Width > bitmap.Height;
        return width != height && bitmap.Width != bitmap.Height && landscapePage != landscapePicture ? (height, width) : (width, height);
    }

    /// <summary>
    /// Text of every page for the speaker notes, or null when PdfPig can't read the file: the pictures (PDFium) are the
    /// conversion, the notes an extra that must not make it fail.
    /// </summary>
    private static IReadOnlyList<string>? PageTexts(string path, IReadOnlyList<int> pages, CancellationToken cancellationToken)
    {
        try
        {
            return PdfDocumentReader.PageTexts(path, pages, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
