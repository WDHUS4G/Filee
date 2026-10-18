// Builds PDFs from images, splits / extracts PDF pages, and merges PDFs (PDFsharp, MIT).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>Image → PDF, PDF → PDF (split / page range) and PDF merging.</summary>
public sealed class PdfSharpConverter : IConverter, IPdfMerger, IImageTransformer
{
    private const double PointsPerInch = 72.0;
    private const double MillimetresPerInch = 25.4;

    public string Id => "pdfsharp";
    public string DisplayName => "PDFsharp";
    public int MaxParallelism => 0;

    /// <summary>Images into a PDF are turned first; a PDF to a PDF (split, extract) is copied page by page, as is.</summary>
    public bool Transforms(string from, string to) => from != "pdf";

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. ImageEncoder.Sources.Select(source => new ConversionEdge(source, "pdf")),
        new("pdf", "pdf"),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.Library("PDFsharp", typeof(PdfDocument)));

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => step.From == "pdf"
            ? SplitOrExtract(step, progress, cancellationToken)
            : ImageToPdf(step, progress, cancellationToken), cancellationToken);

    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var output = new PdfDocument();
            foreach (var input in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
                foreach (var page in source.Pages)
                    output.AddPage(page);
            }
            output.Save(outputPath);
        }, cancellationToken);

    /// <summary>Places every frame of an image (TIFF pages, ...) on its own PDF page.</summary>
    private static IReadOnlyList<string> ImageToPdf(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var options = step.Preset.Image;
        var pdfOptions = step.Preset.Pdf;
        using var images = ImageReader.Read(step.InputPath, step.From, step.Preset);
        var frames = ImageEncoder.SelectFrames(images, step.From, "pdf");

        using var document = new PdfDocument();
        for (var i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var frame = frames[i];
            var dpi = frame.Density.Units == DensityUnit.PixelsPerInch && frame.Density.X > 0 ? frame.Density.X
                : frame.Density.Units == DensityUnit.PixelsPerCentimeter && frame.Density.X > 0 ? frame.Density.X * 2.54
                : 96.0;

            // "pdf" as target: resize / grayscale / metadata apply, no format-specific encoding settings.
            ImageEncoder.ApplyOptions(frame, options, "pdf");
            using var stream = EncodeForPdf(frame, step.From, options);
            using var ximage = XImage.FromStream(stream);

            var imageWidthPt = frame.Width / dpi * PointsPerInch;
            var imageHeightPt = frame.Height / dpi * PointsPerInch;
            var page = document.AddPage();
            var (pageWidth, pageHeight) = PageSize(pdfOptions.PageSize, imageWidthPt > imageHeightPt);
            if (pdfOptions.PageSize == PdfPageSize.FitImage)
                (pageWidth, pageHeight) = (imageWidthPt, imageHeightPt);
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);

            // Fit the image inside the page margins, keeping its aspect ratio, centred.
            var margin = pdfOptions.PageSize == PdfPageSize.FitImage ? 0 : pdfOptions.MarginMm / MillimetresPerInch * PointsPerInch;
            var boxWidth = pageWidth - 2 * margin;
            var boxHeight = pageHeight - 2 * margin;
            var scale = Math.Min(boxWidth / imageWidthPt, boxHeight / imageHeightPt);
            var drawWidth = imageWidthPt * scale;
            var drawHeight = imageHeightPt * scale;

            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawImage(ximage, margin + (boxWidth - drawWidth) / 2, margin + (boxHeight - drawHeight) / 2, drawWidth, drawHeight);

            progress?.Report((i + 1.0) / frames.Count);
        }

        var path = step.Output.Allocate("pdf");
        if (path is null)
            return [];
        document.Save(path);
        return [path];
    }

    /// <summary>PDF → PDF: split into single pages, or extract the configured page range.</summary>
    private static IReadOnlyList<string> SplitOrExtract(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        using var source = PdfReader.Open(step.InputPath, PdfDocumentOpenMode.Import);
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, source.PageCount);
        var written = new List<string>();

        if (step.Preset.Pdf.SplitPages)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var single = new PdfDocument();
                single.AddPage(source.Pages[pages[i]]);
                var path = step.Output.Allocate("pdf", $"_p{pages[i] + 1}");
                if (path is null)
                    continue;
                single.Save(path);
                written.Add(path);
                progress?.Report((i + 1.0) / pages.Count);
            }
            return written;
        }

        using var extract = new PdfDocument();
        foreach (var index in pages)
            extract.AddPage(source.Pages[index]);
        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        extract.Save(output);
        return [output];
    }

    /// <summary>
    /// Encodes a frame for embedding. JPEG sources stay JPEG (no generational loss beyond one re-encode at high quality);
    /// everything else is embedded losslessly as PNG.
    /// </summary>
    private static MemoryStream EncodeForPdf(IMagickImage<byte> frame, string sourceFormat, ImageOptions options)
    {
        var stream = new MemoryStream();
        if (sourceFormat == "jpg" && !frame.HasAlpha)
        {
            frame.Quality = (uint)Math.Clamp(Math.Max(options.Quality, 85), 1, 100);
            frame.Write(stream, MagickFormat.Jpeg);
        }
        else
        {
            // No palette PNGs: PDFsharp's decoder rejects them with transparency, and ImageMagick writes them for
            // pictures with few colours (charts, metafiles, pixel art). Grayscale stays grayscale.
            var format = frame.HasAlpha ? MagickFormat.Png32
                : frame.ColorType is ColorType.Grayscale or ColorType.Bilevel ? MagickFormat.Png
                : MagickFormat.Png24;
            frame.Write(stream, format);
        }
        stream.Position = 0;
        return stream;
    }

    private static (double Width, double Height) PageSize(PdfPageSize size, bool landscape)
    {
        var (w, h) = size switch
        {
            PdfPageSize.Letter => (612.0, 792.0),
            _ => (595.28, 841.89), // A4
        };
        return landscape ? (h, w) : (w, h);
    }
}
