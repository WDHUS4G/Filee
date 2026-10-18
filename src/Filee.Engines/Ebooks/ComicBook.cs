// Comic book archives: CBZ (ZIP), CBR (RAR), CB7 (7-Zip), CBT (TAR) and CBC (a ZIP of CBZ files) are pictures in
// an archive, read with SharpCompress (MIT). Pages are sorted by name the way people number them ("page2" before
// "page10"). Comics become PDF (one page per picture, sized like the picture, JPEGs embedded as they are), EPUB
// (fixed layout) or CBZ; PDF becomes CBZ by rendering its pages with PDFium.

using System.IO.Compression;
using Filee.Core.Conversion;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFtoImage;
using SharpCompress.Archives;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for (Windows, macOS, Linux, mobile); Filee only targets
// desktop platforms that are all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Ebooks;

internal static class ComicBook
{
    private static readonly HashSet<string> PictureExtensions =
        [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl", ".tif", ".tiff", ".heic", ".heif"];

    /// <summary>Extracts the pages of a comic archive into <paramref name="folder"/>, in reading order.</summary>
    public static List<string> ExtractPages(string path, string format, string folder, CancellationToken cancellationToken)
    {
        if (format == "cbc")
        {
            // A collection: the CBZ files inside, in the order of comics.txt ("file.cbz:Title" per line) or by name.
            var comics = ExtractArchive(path, EbookFiles.NewFolder(folder, "cbc"), f => Path.GetExtension(f).Equals(".cbz", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("comics.txt", StringComparison.OrdinalIgnoreCase), cancellationToken);
            var list = comics.FirstOrDefault(c => Path.GetFileName(c).Equals("comics.txt", StringComparison.OrdinalIgnoreCase));
            var books = comics.Where(c => c != list).ToList();
            if (list is not null)
            {
                var order = File.ReadAllLines(list).Select(l => l.Split(':')[0].Trim()).Where(l => l.Length > 0).ToList();
                books = [.. books.OrderBy(b => order.FindIndex(o => b.EndsWith(o.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue)];
            }
            return [.. books.SelectMany(book => ExtractPages(book, "cbz", EbookFiles.NewFolder(folder, "cbz"), cancellationToken))];
        }
        var pages = ExtractArchive(path, folder, f => PictureExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()), cancellationToken);
        if (pages.Count == 0)
            throw new InvalidDataException("The comic contains no pictures.");
        return pages;
    }

    /// <summary>Extracts the matching entries (skipping macOS metadata) and returns them sorted naturally by path.</summary>
    private static List<string> ExtractArchive(string path, string folder, Func<string, bool> wanted, CancellationToken cancellationToken)
    {
        var files = new List<(string Key, string File)>();
        IArchive archive;
        try
        {
            archive = ArchiveFactory.Open(path, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException or SharpCompress.Common.ArchiveException)
        {
            throw new InvalidDataException("The comic archive cannot be opened (damaged, or not the format its extension says).", ex);
        }
        using (archive)
        {
            if (archive.Entries.Any(e => e.IsEncrypted))
                throw new InvalidOperationException("The comic archive is password-protected.");
            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                // Sequential extraction: random access would decompress a solid block again for every entry.
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    if (Wanted(reader.Entry) is { } target)
                    {
                        using var output = File.Create(target);
                        reader.WriteEntryTo(output);
                    }
                }
            }
            else
            {
                foreach (var entry in archive.Entries)
                {
                    if (Wanted(entry) is not { } target)
                        continue;
                    using var input = entry.OpenEntryStream();
                    using var output = File.Create(target);
                    input.CopyTo(output);
                }
            }
        }
        return [.. files.OrderBy(f => f.Key, NaturalComparer.Instance).Select(f => f.File)];

        // The file to extract an entry to, or null for folders, macOS metadata and unwanted files.
        string? Wanted(SharpCompress.Common.IEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = entry.Key ?? "";
            var name = Path.GetFileName(key.Replace('\\', '/'));
            if (entry.IsDirectory || key.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.') || !wanted(name))
                return null;
            if (entry.IsEncrypted)
                throw new InvalidOperationException("The comic archive is password-protected.");
            var target = Path.Combine(folder, $"{files.Count:00000}{Path.GetExtension(name).ToLowerInvariant()}");
            files.Add((key, target));
            return target;
        }
    }

    /// <summary>One PDF page per picture, each as large as its picture (at its DPI, else 96 dpi).</summary>
    public static void ToPdf(IReadOnlyList<string> pages, string output, string workFolder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var converted = EbookFiles.NewFolder(workFolder, "pdf-pages");
        using var document = new PdfDocument();
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // JPEG and PNG go in as they are (PDFsharp embeds JPEG data without re-encoding); others become one of them.
            if (EbookFiles.CommonImage(pages[i], converted, gifAndSvg: false) is not ({ } file, _))
                continue;
            var info = new MagickImageInfo(file);
            var dpi = info.Density is { X: >= 36 } density ? density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X : 96;
            using var image = XImage.FromFile(file);
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(info.Width / dpi * 72);
            page.Height = XUnit.FromPoint(info.Height / dpi * 72);
            using (var graphics = XGraphics.FromPdfPage(page))
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            progress?.Report((i + 1.0) / pages.Count);
        }
        if (document.PageCount == 0)
            throw new InvalidDataException("The comic contains no readable pictures.");
        document.Save(output);
    }

    /// <summary>Writes pages into a CBZ (stored: pictures do not compress further), numbered in order.</summary>
    public static void ToCbz(IReadOnlyList<string> pages, string output, CancellationToken cancellationToken)
    {
        var temp = output + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var digits = Math.Max(3, pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0') + Path.GetExtension(pages[i]).ToLowerInvariant();
                zip.CreateEntryFromFile(pages[i], name, CompressionLevel.NoCompression);
            }
        }
        File.Move(temp, output, overwrite: true);
    }

    /// <summary>
    /// Renders the PDF's pages (the preset's page range) as JPEG files at the preset's DPI and quality. PDFtoImage
    /// serializes its calls into PDFium (not thread-safe), so this may run next to PdfiumConverter.
    /// </summary>
    public static List<string> RenderPdf(string pdf, ConversionStep step, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(pdf);
        var pageNumbers = PageRange.Parse(step.Preset.Pdf.PageRange, Conversion.GetPageCount(bytes, null));
        var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 600);
        var quality = Math.Clamp(step.Preset.Image.Quality, 1, 100);
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
        var pages = new List<string>();
        foreach (var bitmap in Conversion.ToImages(bytes, pageNumbers, null, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (bitmap)
            using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality))
            {
                var file = Path.Combine(folder, $"{pages.Count + 1:00000}.jpg");
                File.WriteAllBytes(file, data.ToArray());
                pages.Add(file);
            }
            progress?.Report(0.9 * pages.Count / Math.Max(1, pageNumbers.Count));
        }
        return pages;
    }
}

/// <summary>Compares names the way people number pages: digit runs by value ("p2" &lt; "p10"), case-insensitively.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.CompareOrdinal(x, y);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;
                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byValue = numberX.Length != numberY.Length ? numberX.Length.CompareTo(numberY.Length) : string.CompareOrdinal(numberX, numberY);
                if (byValue != 0)
                    return byValue;
                continue;
            }
            var byChar = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (byChar != 0)
                return byChar;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
