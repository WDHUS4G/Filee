// Small PDF page previews shared by interactive document tools. Rendering stays in Engines because PDFtoImage and
// its native PDFium runtime already live here; the Avalonia app receives ordinary PNG bytes.

using PDFtoImage;
using SkiaSharp;

// PDFtoImage supports every desktop platform Filee targets.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

public sealed record RenderedPdfPage(int PageNumber, int Width, int Height, byte[] Png);

/// <summary>
/// A reusable PDF preview source. The file is read and its page geometry is discovered once; callers request only
/// the thumbnail or large page they currently need instead of eagerly rasterising an entire document.
/// </summary>
public sealed class PdfPreviewDocument
{
    private readonly byte[] _bytes;
    private readonly (double Width, double Height)[] _sizes;

    private PdfPreviewDocument(byte[] bytes, (double Width, double Height)[] sizes)
    {
        _bytes = bytes;
        _sizes = sizes;
    }

    public int PageCount => _sizes.Length;

    public static PdfPreviewDocument Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var sizes = Conversion.GetPageSizes(bytes)
            .Select(size => ((double)size.Width, (double)size.Height)).ToArray();
        return new PdfPreviewDocument(bytes, sizes);
    }

    public RenderedPdfPage RenderPage(int pageNumber, int maxLongEdge,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        cancellationToken.ThrowIfCancellationRequested();
        var size = _sizes[pageNumber - 1];
        var dpi = (int)Math.Ceiling(Math.Clamp(maxLongEdge * 72d / Math.Max(size.Width, size.Height), 24d, 180d));
        using var page = Conversion.ToImage(_bytes, pageNumber - 1, options: new RenderOptions(
            Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
        cancellationToken.ThrowIfCancellationRequested();
        using var shown = Downscale(page, maxLongEdge);
        using var png = shown.Encode(SKEncodedImageFormat.Png, 88);
        return new RenderedPdfPage(pageNumber, shown.Width, shown.Height, png.ToArray());
    }

    /// <summary>Renders the thumbnail strip in one PDFium document load.</summary>
    public IReadOnlyList<RenderedPdfPage> RenderThumbnails(int maxLongEdge,
        CancellationToken cancellationToken = default)
    {
        if (PageCount == 0)
            return [];
        var largestPage = _sizes.Max(size => Math.Max(size.Width, size.Height));
        var dpi = (int)Math.Ceiling(Math.Clamp(maxLongEdge * 72d / largestPage, 24d, 180d));
        var result = new List<RenderedPdfPage>(PageCount);
        var number = 1;
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true,
            BackgroundColor: SKColors.White);
        foreach (var page in Conversion.ToImages(_bytes, Enumerable.Range(0, PageCount), null, options))
        {
            using (page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var shown = Downscale(page, maxLongEdge);
                using var png = shown.Encode(SKEncodedImageFormat.Png, 82);
                result.Add(new RenderedPdfPage(number++, shown.Width, shown.Height, png.ToArray()));
            }
        }
        return result;
    }

    private static SKBitmap Downscale(SKBitmap source, int maxLongEdge)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxLongEdge)
            return source.Copy();
        var factor = (double)maxLongEdge / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));
        return source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("The PDF preview could not be resized.");
    }
}
