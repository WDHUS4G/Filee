// Built-in e-book conversions, no external program: EPUB, MOBI / AZW / AZW3 / PRC, FB2, HTMLZ and TXTZ are read into
// the HWPX document model and written as EPUB, HWPX (→ PDF / images through rhwp and PDFium), TXT, HTML, Markdown,
// FB2, HTMLZ or TXTZ; HTML, Markdown, TXT and DOCX become e-books too. Comics (CBZ / CBR / CB7 / CBT / CBC) become
// PDF, fixed-layout EPUB or CBZ, PDF becomes CBZ, and AZW4 (Print Replica) gives back its PDF. EPUB ↔ KEPUB (Kobo)
// adds or removes Kobo's sentence markup and keeps the rest of the book as it is.
// Formats only calibre reads or writes (LIT, LRF, PDB, MOBI / AZW3 output, ...) are in CalibreConverter.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;

namespace Filee.Engines.Ebooks;

/// <summary>E-book and comic conversions without external programs.</summary>
public sealed class EbookConverter : IConverter
{
    /// <summary>E-book formats read into the document model.</summary>
    private static readonly string[] BookReaders = ["epub", "kepub", "mobi", "azw3", "azw", "prc", "fb2", "htmlz", "txtz"];

    /// <summary>Formats written from the document model.</summary>
    private static readonly string[] BookWriters = ["epub", "hwpx", "txt", "html", "md", "fb2", "htmlz", "txtz"];

    /// <summary>Documents that become e-books (their other conversions belong to the document engines).</summary>
    private static readonly string[] DocumentReaders = ["html", "md", "txt", "docx"];

    private static readonly string[] EbookWriters = ["epub", "fb2", "htmlz", "txtz"];

    private static readonly string[] Comics = ["cbz", "cbr", "cb7", "cbt", "cbc"];

    public string Id => "ebook";
    public string DisplayName => "E-books (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        // KEPUB → EPUB is below: the book is copied without Kobo's markup instead of being written anew.
        .. BookReaders.SelectMany(from => BookWriters.Where(to => to != from && !(from == "kepub" && to == "epub"))
            .Select(to => new ConversionEdge(from, to, to == "md" ? 12 : 10))),
        // Every other format reaches KEPUB through EPUB.
        new("epub", "kepub"),
        new("kepub", "epub"),
        .. DocumentReaders.SelectMany(from => EbookWriters.Select(to => new ConversionEdge(from, to))),
        // HTML → TXT has no other built-in route; HTML → Markdown is a fallback for when Pandoc is not installed.
        new("html", "txt"),
        new("html", "md", 14),
        .. Comics.SelectMany(from => new[] { new ConversionEdge(from, "pdf"), new ConversionEdge(from, "epub") }),
        .. Comics.Where(from => from != "cbz").Select(from => new ConversionEdge(from, "cbz")),
        // Pages as pictures lose the text: other routes to EPUB (through DOCX, TXT, ...) should win when they exist.
        new("pdf", "cbz", 15),
        new("azw4", "pdf"),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("EPUB, KEPUB, MOBI / AZW3, FB2, HTMLZ, TXTZ, CBZ / CBR / CB7 / CBT / CBC, AZW4",
        $"{EngineVersions.BuiltIn} · {EngineVersions.Library("AngleSharp", typeof(AngleSharp.BrowsingContext))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Convert(step, progress, cancellationToken), cancellationToken);

    private static IReadOnlyList<string> Convert(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.05);
        var work = EbookFiles.NewFolder(step.WorkDirectory, "ebook");
        if (Comics.Contains(step.From) || step.From == "pdf")
            return Comic(step, work, progress, cancellationToken);
        if (step.From == "azw4")
        {
            var pdf = MobiReader.PrintReplicaPdf(step.InputPath);
            var target = step.Output.Allocate("pdf");
            if (target is null)
                return [];
            File.WriteAllBytes(target, pdf);
            progress?.Report(1);
            return [target];
        }
        if (step.To == "kepub" || step.From == "kepub" && step.To == "epub")
        {
            var target = step.Output.Allocate(Filee.Core.Formats.FormatRegistry.Get(step.To).PrimaryExtension);
            if (target is null)
                return [];
            if (step.To == "kepub")
                Kepub.FromEpub(step.InputPath, target, cancellationToken);
            else
                Kepub.ToEpub(step.InputPath, target, cancellationToken);
            progress?.Report(1);
            return [target];
        }

        var book = Read(step, work, cancellationToken);
        progress?.Report(0.5);
        cancellationToken.ThrowIfCancellationRequested();
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        Write(book, step, output, work);
        progress?.Report(1);
        return [output];
    }

    private static Book Read(ConversionStep step, string work, CancellationToken cancellationToken)
    {
        var input = step.InputPath;
        var media = Path.Combine(work, "media");
        switch (step.From)
        {
            case "epub" or "kepub":
                return EpubReader.ReadBook(input, work, cancellationToken);
            case "mobi" or "azw3" or "azw" or "prc":
                return MobiReader.ReadBook(input, work, cancellationToken);
            case "fb2":
                return Fb2Reader.ReadBook(input, work);
            case "htmlz":
                return ZippedText.ReadHtmlzBook(input, work, cancellationToken);
            case "txtz":
                return ZippedText.ReadTxtzBook(input, work);
            case "html":
                {
                    var document = HtmlReader.Read(input, media);
                    return new Book(document, new BookMetadata { Title = document.Title });
                }
            case "md":
                {
                    var document = MarkdownReader.Read(HwpxConverter.DecodeText(File.ReadAllBytes(input)), Path.GetDirectoryName(Path.GetFullPath(input))!);
                    return new Book(document, new BookMetadata { Title = document.Title });
                }
            case "txt":
                return new Book(PlainText.ToDocument(HwpxConverter.DecodeText(File.ReadAllBytes(input))), new BookMetadata());
            case "docx":
                {
                    var document = DocxReader.Read(input, media);
                    return new Book(document, new BookMetadata { Title = document.Title });
                }
            default:
                throw new NotSupportedException($"{step.From} → {step.To}");
        }
    }

    private static void Write(Book book, ConversionStep step, string output, string work)
    {
        switch (step.To)
        {
            case "epub":
                EpubWriter.Write(book, output, step.InputPath, work);
                break;
            case "hwpx":
                book.Document.Title = book.DisplayTitle(step.InputPath);
                HwpxWriter.Write(book.Document, output);
                break;
            case "txt":
                File.WriteAllText(output, PlainTextWriter.Write(book.Document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            case "html":
                {
                    // One self-contained page: pictures are embedded as data: URIs.
                    var images = EbookFiles.NewFolder(work, "html-images");
                    var page = XhtmlWriter.Write(book.Document, new XhtmlOptions { ImageSource = path => DataUri(path, images) }).Single();
                    File.WriteAllText(output, XhtmlWriter.Page(book.DisplayTitle(step.InputPath), page.Body, book.DisplayLanguage(), null, epub: false), new UTF8Encoding(false));
                    break;
                }
            case "md":
                {
                    Func<string, string?> pictures = step.Preset.Document.LeaveOutPictures ? _ => null : new MarkdownPictures(output, step.Output, work).Add;
                    File.WriteAllText(output, MarkdownWriter.Write(book.Document, pictures), new UTF8Encoding(false));
                    break;
                }
            case "fb2":
                Fb2Writer.Write(book, output, step.InputPath, work);
                break;
            case "htmlz":
                ZippedText.WriteHtmlz(book, output, step.InputPath, work);
                break;
            case "txtz":
                ZippedText.WriteTxtz(book, output, step.InputPath, work);
                break;
            default:
                throw new NotSupportedException($"{step.From} → {step.To}");
        }
    }

    private static IReadOnlyList<string> Comic(ConversionStep step, string work, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pages = step.From == "pdf"
            ? ComicBook.RenderPdf(step.InputPath, step, work, progress, cancellationToken)
            : ComicBook.ExtractPages(step.InputPath, step.From, work, cancellationToken);
        progress?.Report(0.4);
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        switch (step.To)
        {
            case "pdf":
                ComicBook.ToPdf(pages, output, work, progress, cancellationToken);
                break;
            case "epub":
                EpubWriter.WriteFixedLayout(Filee.Core.Formats.FormatRegistry.NameWithoutExtension(step.InputPath), pages, output, work, cancellationToken);
                break;
            case "cbz":
                ComicBook.ToCbz(pages, output, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"{step.From} → {step.To}");
        }
        progress?.Report(1);
        return [output];
    }

    private static string? DataUri(string path, string convertFolder) =>
        EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is ({ } file, { } mediaType)
            ? $"data:{mediaType};base64,{System.Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
