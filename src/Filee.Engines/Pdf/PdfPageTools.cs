// Lossless PDF page tools used by the interactive editor: PDFium supplies previews, while PDFsharp changes page
// boxes or copies pages without rasterising their text and drawings.

using Filee.Core.Platform;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

#pragma warning disable CA1416 // PDFium ships for every desktop platform Filee targets.

namespace Filee.Engines.Pdf;

public sealed record PdfPagePreview(byte[] Png, int Width, int Height);

/// <summary>
/// One cached PDF snapshot for previews. The bytes and page geometry are read once, so an editor with hundreds of
/// pages does not reopen and inspect the whole file for every thumbnail.
/// </summary>
public sealed class PdfPreviewSource
{
    private readonly byte[] _pdf;
    private readonly IReadOnlyList<(double Width, double Height)> _sizes;

    private PdfPreviewSource(string path, byte[] pdf, IReadOnlyList<(double Width, double Height)> sizes)
    {
        Path = path;
        _pdf = pdf;
        _sizes = sizes;
    }

    public string Path { get; }
    public int PageCount => _sizes.Count;

    public static PdfPreviewSource Open(string path)
    {
        var pdf = File.ReadAllBytes(path);
        try
        {
            var sizes = Conversion.GetPageSizes(pdf, null)
                .Select(size => ((double)size.Width, (double)size.Height))
                .ToList();
            return new PdfPreviewSource(path, pdf, sizes);
        }
        catch (PdfPasswordProtectedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }
    }

    /// <summary>Renders one page, bounded so a poster-sized page cannot make an enormous bitmap.</summary>
    public PdfPagePreview Preview(int pageIndex = 0, int maxDimension = 2560)
    {
        if (pageIndex < 0 || pageIndex >= _sizes.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var size = _sizes[pageIndex];
        var longest = Math.Max(size.Width, size.Height);
        var dpi = longest <= 0 ? 96 : Math.Clamp((int)Math.Floor(maxDimension * 72 / longest), 36, 150);
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
        try
        {
            using var bitmap = Conversion.ToImages(_pdf, [pageIndex], null, options).Single();
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return new PdfPagePreview(encoded.ToArray(), bitmap.Width, bitmap.Height);
        }
        catch (PdfPasswordProtectedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }
    }
}

/// <summary>One page in an edited document. <see cref="Rotation"/> is an additional clockwise turn in degrees.</summary>
public sealed record PdfPageReference(string Path, int PageIndex, int Rotation = 0);

public static class PdfPageTools
{
    public static int PageCount(string path)
    {
        using var document = PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    /// <summary>Copies pages in the supplied order, optionally from several PDFs, and applies their lossless turns.</summary>
    public static void Save(IReadOnlyList<PdfPageReference> pages, string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("A PDF must contain at least one page.");

        // Keeping every page of one document in its original order only changes /Rotate. Modify a copy so outlines,
        // AcroForm fields, page labels and document information are not discarded by a page-by-page rebuild.
        var originalPath = pages[0].Path;
        if (pages.All(page => FileSystemPaths.Comparer.Equals(page.Path, originalPath))
            && pages.Select(page => page.PageIndex).SequenceEqual(Enumerable.Range(0, pages.Count)))
        {
            bool keepsEveryPage;
            using (var source = PdfSharpConverter.Open(originalPath, PdfDocumentOpenMode.Import))
                keepsEveryPage = source.PageCount == pages.Count;
            if (keepsEveryPage)
            {
                ModifyCopy(originalPath, outputPath, document =>
                {
                    for (var index = 0; index < pages.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pages[index].Rotation != 0)
                            document.Pages[index].Rotate = NormaliseRotation(document.Pages[index].Rotate + pages[index].Rotation);
                    }
                });
                return;
            }
        }

        var sources = new Dictionary<string, PdfDocument>(FileSystemPaths.Comparer);
        try
        {
            using var output = new PdfDocument();
            foreach (var item in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(item.Path, out var source))
                {
                    source = PdfSharpConverter.Open(item.Path, PdfDocumentOpenMode.Import);
                    sources.Add(item.Path, source);
                }
                if (item.PageIndex < 0 || item.PageIndex >= source.PageCount)
                    throw new InvalidDataException($"Page {item.PageIndex + 1} no longer exists in {Path.GetFileName(item.Path)}.");
                var page = output.AddPage(source.Pages[item.PageIndex]);
                if (item.Rotation != 0)
                    page.Rotate = NormaliseRotation(page.Rotate + item.Rotation);
            }
            output.Save(outputPath);
        }
        finally
        {
            foreach (var source in sources.Values)
                source.Dispose();
        }
    }

    /// <summary>Renders one page for the crop UI, bounded so a poster-sized page cannot make an enormous bitmap.</summary>
    public static PdfPagePreview Preview(string path, int pageIndex = 0, int maxDimension = 2560)
    {
        return PdfPreviewSource.Open(path).Preview(pageIndex, maxDimension);
    }

    /// <summary>
    /// Applies one crop rectangle, in the rendered page's normalised top-left coordinates, to every page. Only the
    /// page crop boxes change; text, vectors, links and pictures remain PDF objects.
    /// </summary>
    public static void CropAll(string inputPath, string outputPath, double x, double y, double width, double height,
        CancellationToken cancellationToken = default)
    {
        (x, y, width, height) = Clamp(x, y, width, height);
        ModifyCopy(inputPath, outputPath, document =>
        {
            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var box = page.EffectiveCropBoxReadOnly;
                var (left, bottom, right, top) = MapFromDisplayed(page.Rotate, x, y, width, height);
                page.CropBox = new PdfRectangle(
                    new XPoint(box.X1 + left * box.Width, box.Y1 + bottom * box.Height),
                    new XPoint(box.X1 + right * box.Width, box.Y1 + top * box.Height));
            }
        });
    }

    /// <summary>Turns every page while preserving the document catalogue, forms, labels, outlines and information.</summary>
    internal static void RotateAll(string inputPath, string outputPath, int degrees,
        CancellationToken cancellationToken = default) =>
        ModifyCopy(inputPath, outputPath, document =>
        {
            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page.Rotate = NormaliseRotation(page.Rotate + degrees);
            }
        });

    private static void ModifyCopy(string inputPath, string outputPath, Action<PdfDocument> change)
    {
        if (!FileSystemPaths.Comparer.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath)))
            PdfSharpConverter.CopyWritable(inputPath, outputPath);
        using var document = PdfSharpConverter.OpenForChanges(outputPath);
        change(document);
        document.Save(outputPath);
    }

    private static int NormaliseRotation(int rotation) => ((rotation % 360) + 360) % 360;

    private static (double X, double Y, double Width, double Height) Clamp(double x, double y, double width, double height)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        width = Math.Clamp(width, 0.0001, 1 - x);
        height = Math.Clamp(height, 0.0001, 1 - y);
        return (x, y, width, height);
    }

    /// <summary>Displayed top-left coordinates to the unrotated PDF box (left, bottom, right, top).</summary>
    private static (double Left, double Bottom, double Right, double Top) MapFromDisplayed(
        int rotation, double x, double y, double width, double height)
    {
        var u0 = x;
        var u1 = x + width;
        var v0 = y;
        var v1 = y + height;
        return ((rotation % 360 + 360) % 360) switch
        {
            90 => (v0, u0, v1, u1),
            180 => (1 - u1, v0, 1 - u0, v1),
            270 => (1 - v1, 1 - u1, 1 - v0, 1 - u0),
            _ => (u0, 1 - v1, u1, 1 - v0),
        };
    }
}
