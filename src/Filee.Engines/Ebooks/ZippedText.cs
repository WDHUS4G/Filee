// HTMLZ and TXTZ, calibre's zipped single-file formats: HTMLZ is index.html + style.css + images + metadata.opf,
// TXTZ is index.txt (Markdown) + images + metadata.opf. Both are read and written here.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class ZippedText
{
    /// <summary>Reads an HTMLZ book (for the reader registry of the HWPX writer).</summary>
    public static HDocument ReadHtmlz(string path, string workFolder) => ReadHtmlzBook(path, workFolder).Document;

    /// <summary>Reads a TXTZ book (for the reader registry of the HWPX writer).</summary>
    public static HDocument ReadTxtz(string path, string workFolder) => ReadTxtzBook(path, workFolder).Document;

    public static Book ReadHtmlzBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var folder = Extract(path, workFolder, "htmlz");
        var page = MainFile(folder, "index.html", ".html", ".htm", ".xhtml")
                   ?? throw new InvalidDataException("The HTMLZ file contains no HTML page.");
        var document = ChapterReader.Read([page], Path.Combine(folder, "~media"), cancellationToken);
        var metadata = Metadata(folder);
        document.Title = metadata.Title ?? document.Title;
        return new Book(document, metadata);
    }

    public static Book ReadTxtzBook(string path, string workFolder)
    {
        var folder = Extract(path, workFolder, "txtz");
        var text = MainFile(folder, "index.txt", ".txt", ".md", ".markdown", ".text")
                   ?? throw new InvalidDataException("The TXTZ file contains no text file.");
        // calibre writes TXTZ text as plain text, Markdown or Textile; Markdown reads all of them sensibly.
        var document = MarkdownReader.Read(HwpxConverter.DecodeText(File.ReadAllBytes(text)), Path.GetDirectoryName(text)!);
        var metadata = Metadata(folder);
        document.Title = metadata.Title ?? document.Title;
        return new Book(document, metadata);
    }

    public static void WriteHtmlz(Book book, string outputPath, string sourcePath, string workFolder)
    {
        var title = book.DisplayTitle(sourcePath);
        var language = book.DisplayLanguage();
        var images = new ImageSet(EbookFiles.NewFolder(workFolder, "htmlz-images"));
        var page = XhtmlWriter.Write(book.Document, new XhtmlOptions { ImageSource = images.Add }).Single();
        Save(outputPath, [
            ("index.html", XhtmlWriter.Page(title, page.Body, language, "style.css", epub: false)),
            ("style.css", XhtmlWriter.Css),
            ("metadata.opf", OpfPackage.MetadataOnly(book.Metadata, title, language)),
        ], images);
    }

    public static void WriteTxtz(Book book, string outputPath, string sourcePath, string workFolder)
    {
        var title = book.DisplayTitle(sourcePath);
        var images = new ImageSet(EbookFiles.NewFolder(workFolder, "txtz-images"));
        var text = MarkdownWriter.Write(book.Document, images.Add);
        Save(outputPath, [
            ("index.txt", text),
            ("metadata.opf", OpfPackage.MetadataOnly(book.Metadata, title, book.DisplayLanguage())),
        ], images);
    }

    private static string Extract(string path, string workFolder, string prefix)
    {
        var folder = EbookFiles.NewFolder(workFolder, prefix);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            EbookFiles.Extract(zip, folder);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"This is not a valid {prefix.ToUpperInvariant()} file (it is not a ZIP archive).", ex);
        }
        return folder;
    }

    /// <summary>The preferred file name at the top level, else the first file with one of the extensions (shallowest first).</summary>
    private static string? MainFile(string folder, string preferred, params string[] extensions)
    {
        var top = Path.Combine(folder, preferred);
        if (File.Exists(top))
            return top;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static BookMetadata Metadata(string folder)
    {
        var opf = Directory.EnumerateFiles(folder, "*.opf", SearchOption.AllDirectories).FirstOrDefault();
        if (opf is null)
            return new BookMetadata();
        try
        {
            return OpfPackage.Load(opf).Metadata;
        }
        catch (System.Xml.XmlException)
        {
            return new BookMetadata(); // broken metadata: the content still converts
        }
    }

    private static void Save(string outputPath, IEnumerable<(string Name, string Text)> texts, ImageSet images)
    {
        var temp = outputPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in texts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            foreach (var (name, file) in images.Files)
                zip.CreateEntryFromFile(file, name, CompressionLevel.NoCompression);
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    /// <summary>Pictures of a zipped book: images/imgNNNN.ext, converted to JPEG / PNG / GIF where needed.</summary>
    private sealed class ImageSet(string convertFolder)
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Name, string File)> Files { get; } = [];

        public string? Add(string path)
        {
            if (_names.TryGetValue(path, out var known))
                return known;
            if (EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is not ({ } file, _))
                return null;
            var name = $"images/img{Files.Count + 1:0000}{Path.GetExtension(file).ToLowerInvariant()}";
            Files.Add((name, file));
            _names[path] = name;
            return name;
        }
    }
}
