// PDF → HDocument with PdfPig (Apache-2.0), so PDF → DOCX / HWPX / TXT needs no Word or LibreOffice.
//
// Every page's words (with their positions, fonts, sizes and colours) become lines, lines become paragraphs, and the
// paragraphs and pictures are put in reading order, also on two-column pages (PdfDocumentReader.Layout). Headings
// come from font sizes, bold / italic from the fonts, page size and margins from the page. Running headers and
// footers (also those that change with the chapter), page numbers and a logo repeated at the top or bottom of the
// pages are left out. A page without text (a scan) becomes the rendered page as a picture (PDFium via PDFtoImage), so
// the result is complete. With "Recognize text" (PdfOptions.Ocr) Tesseract reads the scans first, and their invisible
// OCR layer is the page's text (ReadWithOcrLayer).
//
// Rows laid out with tab stops (a number, a label and a date far to the right) stay one line with tabs at the PDF's
// positions, and a line that ends early although the next line's first word would have fitted (an address) keeps its
// line break.
//
// Tables drawn with lines become tables (PdfDocumentReader.Tables). Not converted: tables without rules (their cells
// come out as paragraphs in reading order), other vector drawings on text pages, annotations other than links, form
// fields.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Tokens;

// PDFtoImage marks its API for the platforms PDFium ships for; Filee only targets desktop platforms, all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>Reads a PDF into the document model (HWPX / DOCX writers, plain text).</summary>
internal sealed partial class PdfDocumentReader
{
    /// <summary>Resolution of rendered scans and of pictures PdfPig cannot decode.</summary>
    private const int RenderDpi = 150;

    /// <summary>HWPUNIT per point.</summary>
    private const double Unit = 100;

    private static readonly Regex SubsetPrefix = new("^[A-Z]{6}\\+", RegexOptions.Compiled);

    /// <summary>PostScript names of common fonts → the family names Word and 한글 know.</summary>
    private static readonly Dictionary<string, string> KnownFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial",
        ["Arial"] = "Arial",
        ["ArialMT"] = "Arial",
        ["ArialNarrow"] = "Arial Narrow",
        ["Times"] = "Times New Roman",
        ["TimesNewRoman"] = "Times New Roman",
        ["TimesNewRomanPS"] = "Times New Roman",
        ["TimesNewRomanPSMT"] = "Times New Roman",
        ["Courier"] = "Courier New",
        ["CourierNew"] = "Courier New",
        ["CourierNewPS"] = "Courier New",
        ["CourierNewPSMT"] = "Courier New",
        ["MalgunGothic"] = "Malgun Gothic",
        ["SegoeUI"] = "Segoe UI",
        ["TrebuchetMS"] = "Trebuchet MS",
        ["CenturyGothic"] = "Century Gothic",
        ["BookAntiqua"] = "Book Antiqua",
        ["ComicSansMS"] = "Comic Sans MS",
        ["LucidaConsole"] = "Lucida Console",
        ["PalatinoLinotype"] = "Palatino Linotype",
        ["MicrosoftSansSerif"] = "Microsoft Sans Serif",
        // Fonts for scripts that need shaping, as Word and LibreOffice name them in PDFs.
        ["ArialUnicodeMS"] = "Arial Unicode MS",
        ["NirmalaUI"] = "Nirmala UI",
        ["NirmalaText"] = "Nirmala Text",
        ["LeelawadeeUI"] = "Leelawadee UI",
        ["KhmerUI"] = "Khmer UI",
        ["LaoUI"] = "Lao UI",
        ["MyanmarText"] = "Myanmar Text",
        ["IskoolaPota"] = "Iskoola Pota",
        ["TraditionalArabic"] = "Traditional Arabic",
        ["SimplifiedArabic"] = "Simplified Arabic",
        ["SimplifiedArabicFixed"] = "Simplified Arabic Fixed",
        ["ArabicTypesetting"] = "Arabic Typesetting",
        ["SakkalMajalla"] = "Sakkal Majalla",
        ["UrduTypesetting"] = "Urdu Typesetting",
        ["AngsanaNew"] = "Angsana New",
        ["AngsanaUPC"] = "AngsanaUPC",
        ["BrowalliaNew"] = "Browallia New",
        ["CordiaNew"] = "Cordia New",
    };

    /// <summary>
    /// Families whose PostScript names are the family name without spaces ("NotoSansGujarati", "LiberationSerif",
    /// "DejaVuSans"): split again before each capital ("Noto Sans Gujarati").
    /// </summary>
    private static readonly Regex SpacedFamily = new("^(Noto|Liberation|DejaVu)[A-Z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Where a space goes in such a name: before a capital that follows a small letter or a digit, and between "UI" and a word.</summary>
    private static readonly Regex WordStart = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly string _mediaFolder;
    private readonly bool _textOnly;
    private readonly bool _ocrLayer;
    private byte[]? _pdfBytes;
    private int _pictures;

    private PdfDocumentReader(string path, string mediaFolder, bool textOnly, bool ocrLayer = false)
    {
        _path = path;
        _mediaFolder = mediaFolder;
        _textOnly = textOnly;
        _ocrLayer = ocrLayer;
    }

    /// <summary>Reads <paramref name="path"/> with pictures (extracted into <paramref name="mediaFolder"/>) and scans.</summary>
    public static HDocument Read(string path, string mediaFolder) => new PdfDocumentReader(path, mediaFolder, textOnly: false).ReadDocument();

    /// <summary>
    /// Like <see cref="Read"/>, but the invisible text of a scan (its OCR layer, PdfOptions.Ocr) is the page's text:
    /// the words become paragraphs and the scan under them is left out.
    /// </summary>
    public static HDocument ReadWithOcrLayer(string path, string mediaFolder) =>
        new PdfDocumentReader(path, mediaFolder, textOnly: false, ocrLayer: true).ReadDocument();

    /// <summary>
    /// Reads only the text, including invisible text (the OCR layer of scanned PDFs); pictures and scans are left out.
    /// </summary>
    public static HDocument ReadText(string path) => new PdfDocumentReader(path, "", textOnly: true).ReadDocument();

    // ───────────────────────── Model of a page ─────────────────────────

    /// <summary>A word with its box (points, y upwards from the bottom of the visible page) and formatting.</summary>
    private sealed record TextWord(string Text, double Left, double Right, double Top, double Bottom, double Baseline, double Size, HCharFormat Format, string? Link);

    /// <summary>A text line (or the part of it in one column).</summary>
    private sealed class TextLine(List<TextWord> words, int row = 0)
    {
        public List<TextWord> Words { get; } = words;

        /// <summary>The row of the page the line is on: the parts of a line split at wide gaps share it.</summary>
        public int Row { get; } = row;

        /// <summary>Indexes of the words a tab leads to (the start of each part after the first of a tabbed row).</summary>
        public List<int> Tabs { get; } = [];

        public double Left => Words[0].Left;
        public double Right => Words[^1].Right;
        public double Top => Words.Max(w => w.Top);
        public double Bottom => Words.Min(w => w.Bottom);
        public double Baseline => Words[0].Baseline;
        public double Size => Words.Max(w => w.Size);
        public string Text => string.Join(' ', Words.Select(w => w.Text));

        private bool? _rightToLeft;

        /// <summary>Mostly Arabic or Hebrew: read from right to left, so its first word is the rightmost one.</summary>
        public bool RightToLeft => _rightToLeft ??= ComplexScripts.Direction(string.Concat(Words.Select(w => w.Text))) < 0;

        /// <summary>The word the line starts with in reading order.</summary>
        public TextWord First => RightToLeft ? Words[^1] : Words[0];

        /// <summary>The word the line ends with in reading order.</summary>
        public TextWord Last => RightToLeft ? Words[0] : Words[^1];
    }

    /// <summary>A paragraph, a picture or a ruled table on a page, the unit of reading order.</summary>
    private sealed class Item
    {
        public List<TextLine> Lines { get; } = [];
        public string? PicturePath { get; init; }
        public PdfRectangle? PictureBounds { get; init; }
        public RuledTable? Table { get; init; }

        private PdfRectangle? Box => PictureBounds ?? Table?.Bounds;

        public double Left => Box?.Left ?? Lines.Min(l => l.Left);
        public double Right => Box?.Right ?? Lines.Max(l => l.Right);
        public double Top => Box?.Top ?? Lines[0].Top;
        public double Bottom => Box?.Bottom ?? Lines[^1].Bottom;
        public double Width => Right - Left;
        public bool IsPicture => PicturePath is not null;
        public bool IsTable => Table is not null;

        /// <summary>Size of most of the text (by characters).</summary>
        public double Size => Lines.SelectMany(l => l.Words).GroupBy(w => Math.Round(w.Size * 2) / 2)
            .OrderByDescending(g => g.Sum(w => w.Text.Length)).Select(g => g.Key).FirstOrDefault(10);

        public string Text => Table?.Text ?? string.Join(' ', Lines.Select(l => l.Text));

        /// <summary>A paragraph of mostly right-to-left lines.</summary>
        public bool RightToLeft => !IsPicture && !IsTable && Lines.Count(l => l.RightToLeft) * 2 > Lines.Count;

        /// <summary>Left and right edges of the column (or the text area) the item sits in, set by the reading order.</summary>
        public double ContainerLeft { get; set; }
        public double ContainerRight { get; set; }
    }

    private sealed class PageContent(int index, double width, double height)
    {
        public int Index { get; } = index;
        public double Width { get; } = width;
        public double Height { get; } = height;
        public List<Item> Items { get; set; } = [];
        public bool Scanned { get; set; }
        public string? ScanPath { get; set; }
    }

    // ───────────────────────── Document ─────────────────────────

    private HDocument ReadDocument()
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(_path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PasswordProtected();
        }

        using (pdf)
        {
            var pages = new List<PageContent>();
            try
            {
                foreach (var page in pdf.GetPages())
                    pages.Add(ReadPage(page));
            }
            catch (PdfDocumentEncryptedException)
            {
                throw PasswordProtected();
            }

            RemoveRepeatedHeadersAndFooters(pages);
            foreach (var page in pages.Where(p => !p.Scanned))
                page.Items = ReadingOrder(page.Items);
            var title = NullIfBlank(pdf.Information.Title);
            return Build(pages, title);
        }
    }

    internal static InvalidOperationException PasswordProtected() =>
        new("The PDF is password-protected. Open it with its password and save a copy without one, then convert that copy.");

    /// <summary>
    /// The text of each page in <paramref name="pages"/> (zero-based), read like <see cref="ReadText"/> (reading order,
    /// invisible OCR text included) but page by page, with running heads and page numbers kept: "" for a page without
    /// text. Paragraphs are separated by an empty line, lines end with CR LF.
    /// </summary>
    public static IReadOnlyList<string> PageTexts(string path, IReadOnlyList<int> pages, CancellationToken cancellationToken = default)
    {
        var reader = new PdfDocumentReader(path, "", textOnly: true);
        try
        {
            using var pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var texts = new List<string>(pages.Count);
            foreach (var index in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = reader.ReadPage(pdf.GetPage(index + 1));
                if (page.Scanned)
                {
                    texts.Add("");
                    continue;
                }
                page.Items = ReadingOrder(page.Items);
                texts.Add(PlainText(reader.Build([page], null)).TrimEnd());
            }
            return texts;
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PasswordProtected();
        }
    }

    private PageContent ReadPage(Page page)
    {
        var crop = page.CropBox.Bounds;
        // A page turned by 90° is shown (and rendered) with width and height swapped.
        var rotated = page.Rotation.Value % 180 != 0;
        var content = new PageContent(page.Number - 1, rotated ? crop.Height : crop.Width, rotated ? crop.Width : crop.Height);

        var links = page.GetHyperlinks()
            .Where(h => h.Uri is { } uri && (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            .Select(h => (Bounds: Shift(h.Bounds, crop), Uri: h.Uri!))
            .ToList();

        // Text that needs shaping (Arabic, Indic, ...) is read in content-stream order (PdfDocumentReader.ComplexText).
        var pageLetters = ContentLetters(page);
        var pageWords = HasComplexText(pageLetters)
            ? ComplexWords(pageLetters)
            : NearestNeighbourWordExtractor.Instance.GetWords(pageLetters.Where(l => l.Value.Length > 0).ToList())
                .Where(w => w.TextOrientation == TextOrientation.Horizontal && w.Letters.Count > 0)
                .Select(w => new PageWord(w.Text, w.BoundingBox, w.Letters, w.Letters[0]));

        var words = new List<TextWord>();
        foreach (var word in pageWords)
        {
            if (string.IsNullOrWhiteSpace(word.Text))
                continue;
            var letters = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0)
                continue;
            // Invisible text is the OCR layer of a scan: part of the text, not of the page's look (unless OCR was asked for).
            var invisible = letters.All(l => l.RenderingMode == TextRenderingMode.Neither);
            if (!_textOnly && !_ocrLayer && invisible)
                continue;
            var lead = string.IsNullOrWhiteSpace(word.Lead.Value) ? letters[0] : word.Lead;
            var box = Shift(word.Box, crop);
            var size = Median(letters.Select(LetterSize));
            var center = new PdfPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
            var link = links.FirstOrDefault(l => Inside(l.Bounds, center)).Uri;
            var format = Format(lead, size);
            // An OCR layer's font only places the words (Tesseract's GlyphLessFont): the document's own font is used.
            if (invisible)
                format = format with { Font = null, Bold = false, Italic = false };
            words.Add(new TextWord(CleanText(word.Text), box.Left, box.Right, box.Top, box.Bottom,
                lead.StartBaseLine.Y - crop.Bottom, Math.Max(1, size), format, link));
        }

        if (words.Count == 0 || (rotated && !_textOnly))
        {
            // A scan, a page of drawings, or a turned page: the rendered page keeps everything.
            content.Scanned = true;
            if (!_textOnly)
                content.ScanPath = RenderScan(page.Number - 1);
            return content;
        }

        // Plain text (TXT, page texts for slides) keeps table cells as paragraphs in reading order.
        var tables = _textOnly ? [] : RuledTables(page.Paths, crop, words);
        content.Items = words.Count > 0 ? TextItems(words) : [];
        content.Items.AddRange(tables.Select(t => new Item { Table = t }));
        if (!_textOnly)
            content.Items.AddRange(Pictures(page, content, crop));
        return content;
    }

    /// <summary>Words → lines → paragraphs, with tabbed rows joined.</summary>
    private static List<Item> TextItems(List<TextWord> words)
    {
        var lines = Lines(words);
        var items = Paragraphs(lines);
        if (JoinTabbedRows(lines, items) is { } joined)
            items = Paragraphs(joined);
        return items;
    }

    private static PdfRectangle Shift(PdfRectangle box, PdfRectangle crop) =>
        new(box.Left - crop.Left, box.Bottom - crop.Bottom, box.Right - crop.Left, box.Top - crop.Bottom);

    private static bool Inside(PdfRectangle box, PdfPoint point) =>
        point.X >= box.Left && point.X <= box.Right && point.Y >= box.Bottom && point.Y <= box.Top;

    // ───────────────────────── Text formatting ─────────────────────────

    private static HCharFormat Format(Letter letter, double size)
    {
        var name = letter.FontName ?? letter.FontDetails?.Name ?? "";
        var bold = letter.FontDetails?.IsBold == true || letter.RenderingMode == TextRenderingMode.FillThenStroke
                   || Regex.IsMatch(name, "Bold|Black|Heavy|Semibold|SemiBold|Demi", RegexOptions.CultureInvariant);
        var italic = letter.FontDetails?.IsItalic == true || Regex.IsMatch(name, "Italic|Oblique", RegexOptions.CultureInvariant);
        return new HCharFormat(
            Bold: bold,
            Italic: italic,
            Size: (int)(Math.Round(size * 2) * 50),
            Font: FontFamily(name),
            Color: Color(letter.Color));
    }

    /// <summary>"ABCDEF+TimesNewRomanPS-BoldMT" → "Times New Roman"; null for names that are no family (F1, T3Font_0).</summary>
    internal static string? FontFamily(string postScriptName)
    {
        var name = SubsetPrefix.Replace(postScriptName, "");
        var cut = name.IndexOfAny(['-', ',']);
        if (cut > 0)
            name = name[..cut];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                if (KnownFonts.TryGetValue(name, out var mapped))
                    return mapped;
                name = name[..^suffix.Length];
            }
        }
        if (KnownFonts.TryGetValue(name, out var known))
            return known;
        if (SpacedFamily.IsMatch(name))
        {
            // "NotoSansCJKkr" → "Noto Sans CJK KR"
            var cjk = Regex.Match(name, "CJK(kr|jp|sc|tc|hk)$", RegexOptions.CultureInvariant);
            var spaced = WordStart.Replace(cjk.Success ? name[..cjk.Index] : name, " ").Replace("Deja Vu", "DejaVu", StringComparison.Ordinal);
            return cjk.Success ? $"{spaced} CJK {cjk.Groups[1].Value.ToUpperInvariant()}" : spaced;
        }
        return name.Length < 3 || Regex.IsMatch(name, "^(F|T3Font_?|Font)\\d+$", RegexOptions.CultureInvariant) ? null : name;
    }

    /// <summary>
    /// "#RRGGBB" of a text colour, black included: heading styles of the HWPX template are coloured, the PDF's text
    /// was not.
    /// </summary>
    private static string? Color(IColor? color)
    {
        if (color is null)
            return null;
        try
        {
            var (r, g, b) = color.ToRGBValues();
            return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return null; // pattern and other colours without an RGB value
        }

        static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }

    /// <summary>Latin ligatures (ﬁ, ﬂ, ...) as letters, so the text can be searched and edited.</summary>
    private static string CleanText(string text)
    {
        if (!text.Any(c => c is >= 'ﬀ' and <= 'ﬆ'))
            return text;
        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
            sb.Append(ch is >= 'ﬀ' and <= 'ﬆ' ? ch.ToString().Normalize(NormalizationForm.FormKC) : ch.ToString());
        return sb.ToString();
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>Pictures of a text page. A picture covering the page behind text is the scan under an OCR layer: skipped.</summary>
    private IEnumerable<Item> Pictures(Page page, PageContent content, PdfRectangle crop)
    {
        var result = new List<Item>();
        SKBitmap? rendered = null;
        try
        {
            foreach (var image in page.GetImages().Take(200))
            {
                var bounds = Shift(image.BoundingBox, crop);
                bounds = new PdfRectangle(Math.Max(0, bounds.Left), Math.Max(0, bounds.Bottom), Math.Min(content.Width, bounds.Right), Math.Min(content.Height, bounds.Top));
                if (bounds.Width < 6 || bounds.Height < 6 || image.IsImageMask)
                    continue;
                if (bounds.Width * bounds.Height > 0.85 * content.Width * content.Height)
                    continue;

                string? path = null;
                if (IsPlainJpeg(image))
                {
                    path = NewMediaPath("jpg");
                    File.WriteAllBytes(path, image.RawMemory.ToArray());
                }
                else if (image.TryGetPng(out var png))
                {
                    path = NewMediaPath("png");
                    File.WriteAllBytes(path, png);
                }
                else
                {
                    // JPEG 2000, CCITT, unusual colour spaces ...: cut the picture out of the rendered page.
                    rendered ??= Render(content.Index);
                    path = Crop(rendered, bounds, content);
                }
                if (path is not null)
                    result.Add(new Item { PicturePath = path, PictureBounds = bounds });
            }
        }
        finally
        {
            rendered?.Dispose();
        }
        return result;
    }

    /// <summary>A DCT (JPEG) stream in grey or RGB without a decode array: the bytes are a JPEG file as they are.</summary>
    private static bool IsPlainJpeg(IPdfImage image)
    {
        var dictionary = image.ImageDictionary;
        if (!dictionary.TryGet(NameToken.Filter, out var filter))
            return false;
        var name = filter switch
        {
            NameToken single => single.Data,
            ArrayToken { Data.Count: 1 } array when array.Data[0] is NameToken only => only.Data,
            _ => null,
        };
        if (name is not ("DCTDecode" or "DCT") || dictionary.ContainsKey(NameToken.Decode))
            return false;
        var components = image.ColorSpaceDetails?.NumberOfColorComponents ?? 3;
        return components is 1 or 3;
    }

    private string NewMediaPath(string extension)
    {
        Directory.CreateDirectory(_mediaFolder);
        return Path.Combine(_mediaFolder, $"pdf-{++_pictures}.{extension}");
    }

    private SKBitmap Render(int pageIndex)
    {
        _pdfBytes ??= File.ReadAllBytes(_path);
        return Conversion.ToImage(_pdfBytes, pageIndex, null,
            new RenderOptions(Dpi: RenderDpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
    }

    /// <summary>The whole page as a JPEG, the format of scans.</summary>
    private string RenderScan(int pageIndex)
    {
        using var bitmap = Render(pageIndex);
        var path = NewMediaPath("jpg");
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string? Crop(SKBitmap page, PdfRectangle bounds, PageContent content)
    {
        var scaleX = page.Width / content.Width;
        var scaleY = page.Height / content.Height;
        var rect = SKRectI.Round(new SKRect(
            (float)(bounds.Left * scaleX), (float)((content.Height - bounds.Top) * scaleY),
            (float)(bounds.Right * scaleX), (float)((content.Height - bounds.Bottom) * scaleY)));
        rect.Intersect(new SKRectI(0, 0, page.Width, page.Height));
        if (rect.Width < 2 || rect.Height < 2)
            return null;
        using var part = new SKBitmap();
        if (!page.ExtractSubset(part, rect))
            return null;
        var path = NewMediaPath("png");
        using var image = SKImage.FromBitmap(part);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    // ───────────────────────── Headers and footers ─────────────────────────

    private static readonly Regex PageNumber = new(
        "^[\\s\\-–—(\\[]*(page|p\\.|페이지|第)?\\s*\\d+\\s*(/|of|페이지|쪽|页)?\\s*\\d*[\\s\\-–—)\\]]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Page numbers in roman numerals ("iv", "- XII -"), as front matter is often numbered.</summary>
    private static readonly Regex RomanPageNumber = new(
        "^[\\s\\-–—(\\[]*(?=[ivxlcdm])m{0,3}(cm|cd|d?c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3})[\\s\\-–—)\\]]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Leaves out what sits at the top or bottom edge of the pages only because it is a page (running heads, footers,
    /// page numbers), so text exports read on without it:
    /// <list type="bullet">
    /// <item>paragraphs that repeat on most pages (digits are ignored, so "Page 3" matches "Page 4");</item>
    /// <item>running heads that change with the chapter: the same text at the same height on three pages or more,
    /// no larger than the body text (a heading that starts several pages is larger, and stays);</item>
    /// <item>bare page numbers ("12", "- 12 -", "Page 3 of 10", "iv");</item>
    /// <item>the same picture (a logo) at the same place on most pages.</item>
    /// </list>
    /// </summary>
    private static void RemoveRepeatedHeadersAndFooters(List<PageContent> pages)
    {
        var textPages = pages.Where(p => !p.Scanned).ToList();
        if (textPages.Count < 2)
            return;
        bool AtEdge(Item item, PageContent page) => item.Top > page.Height * 0.9 || item.Bottom < page.Height * 0.1;
        static string Key(Item item) => Regex.Replace(item.Text, "\\d+", "#").Trim();
        static (string Key, long Top) Place(Item item) => (Key(item), (long)Math.Round(item.Top / 2));

        var edgeText = textPages.SelectMany(p => p.Items.Where(i => !i.IsPicture && AtEdge(i, p)).Select(i => (Page: p.Index, Item: i))).ToList();
        var counts = edgeText.Select(x => (x.Page, Key: Key(x.Item))).Distinct()
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var placeCounts = edgeText.Select(x => (x.Page, Place: Place(x.Item))).Distinct()
            .GroupBy(x => x.Place)
            .ToDictionary(g => g.Key, g => g.Count());
        var bodySize = BodySize([.. textPages.SelectMany(p => p.Items.Where(i => !i.IsPicture))]);
        var needed = Math.Max(2, (int)Math.Ceiling(textPages.Count * 0.5));

        bool RunningText(Item item) =>
            item.Lines.Count <= 2
            && (counts.GetValueOrDefault(Key(item)) >= needed
                || (placeCounts.GetValueOrDefault(Place(item)) >= 3 && item.Size <= bodySize + 0.5)
                || PageNumber.IsMatch(item.Text) || RomanPageNumber.IsMatch(item.Text));

        var pictureKeys = textPages.SelectMany(p => p.Items.Where(i => i.IsPicture && AtEdge(i, p))).ToDictionary(i => i, PictureKey);
        var pictureCounts = textPages
            .SelectMany(p => p.Items.Where(pictureKeys.ContainsKey).Select(i => (Page: p.Index, Key: pictureKeys[i])).Distinct())
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var page in textPages)
        {
            page.Items.RemoveAll(i => AtEdge(i, page) && (i.IsPicture
                ? pictureCounts.GetValueOrDefault(pictureKeys[i]) >= needed
                : RunningText(i)));
        }
    }

    /// <summary>A picture's place (to 2 points) and content: the same logo on every page has the same key.</summary>
    private static string PictureKey(Item item)
    {
        var bounds = item.PictureBounds!.Value;
        var bytes = File.ReadAllBytes(item.PicturePath!);
        return FormattableString.Invariant($"{Math.Round(bounds.Left / 2)},{Math.Round(bounds.Top / 2)},{Math.Round(bounds.Width / 2)},{Math.Round(bounds.Height / 2)}:")
               + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    // ───────────────────────── Building the document ─────────────────────────

    private HDocument Build(List<PageContent> pages, string? title)
    {
        var document = new HDocument { Title = title };
        var items = pages.SelectMany(p => p.Items.Where(i => !i.IsPicture && !i.IsTable)).ToList();
        var bodySize = BodySize(items);
        var headingLevels = HeadingLevels(items, bodySize);
        var (left, right, top, bottom) = Margins(pages);
        var lineRatio = TypicalLineRatio(items);

        HSection? section = null;
        foreach (var page in pages)
        {
            var hpage = new HPage(Hwp(page.Width), Hwp(page.Height), Hwp(left), Hwp(right), Hwp(top), Hwp(bottom), 0, 0, 0);
            var newSection = section is null || section.Page != hpage;
            if (newSection)
            {
                section = new HSection { Page = hpage };
                document.Sections.Add(section);
            }
            var blocks = section!.Blocks;
            var first = blocks.Count;

            if (page.Scanned)
            {
                if (page.ScanPath is { } scan)
                {
                    // Floating behind the (empty) paragraph at the paper's corner: the page image never pushes text.
                    var picture = new HImage(scan)
                    {
                        Width = hpage.Width,
                        Height = hpage.Height,
                        Anchor = new HAnchor("PAPER", "LEFT", 0, "PAPER", "TOP", 0, "BEHIND_TEXT", AllowOverlap: true),
                    };
                    var paragraph = new HParagraph { MarkFormat = new HCharFormat(Size: 100) };
                    paragraph.Inlines.Add(picture);
                    blocks.Add(paragraph);
                }
                else
                {
                    blocks.Add(new HParagraph());
                }
            }
            else
            {
                Item? previous = null;
                foreach (var item in page.Items)
                {
                    blocks.Add(item.IsPicture ? PictureParagraph(item, previous)
                        : item.IsTable ? TableBlock(item, lineRatio)
                        : TextParagraph(item, previous, headingLevels, lineRatio));
                    previous = item;
                }
                if (blocks.Count == first)
                    blocks.Add(new HParagraph());
            }

            // A table cannot start a page in every format: a tiny empty paragraph before it carries the page break.
            if (!newSection && blocks[first] is HTable)
                blocks.Insert(first, new HParagraph { MarkFormat = new HCharFormat(Size: 100) });
            if (!newSection && blocks[first] is HParagraph startOfPage)
                startOfPage.PageBreakBefore = true;
        }
        if (document.Sections.Count == 0)
        {
            var empty = new HSection();
            empty.Blocks.Add(new HParagraph());
            document.Sections.Add(empty);
        }
        return document;
    }

    private static int Hwp(double points) => (int)Math.Round(points * Unit);

    /// <summary>The size of most body text (by characters, rounded to half points).</summary>
    private static double BodySize(List<Item> items) =>
        items.SelectMany(i => i.Lines).SelectMany(l => l.Words).GroupBy(w => Math.Round(w.Size * 2) / 2)
            .OrderByDescending(g => g.Sum(w => w.Text.Length)).Select(g => g.Key).FirstOrDefault(10);

    /// <summary>Heading level by size: short paragraphs clearly larger than the body text, the largest size first.</summary>
    private static Dictionary<double, int> HeadingLevels(List<Item> items, double bodySize)
    {
        var sizes = items.Where(i => IsHeadingCandidate(i, bodySize)).Select(i => Math.Round(i.Size)).Distinct().OrderByDescending(s => s).ToList();
        var levels = new Dictionary<double, int>();
        for (var i = 0; i < sizes.Count; i++)
            levels[sizes[i]] = Math.Min(6, i + 1);
        return levels;
    }

    private static bool IsHeadingCandidate(Item item, double bodySize) =>
        !item.IsPicture && item.Lines.Count <= 3 && item.Text.Length <= 200 && item.Size >= bodySize * 1.15 && item.Size >= bodySize + 1;

    /// <summary>Margins shared by the text pages: the smallest distances from the paper edges to any text or picture.</summary>
    private static (double Left, double Right, double Top, double Bottom) Margins(List<PageContent> pages)
    {
        const double Min = 18, Max = 108, Default = 72;
        double? left = null, right = null, top = null, bottom = null;
        foreach (var page in pages.Where(p => !p.Scanned && p.Items.Count > 0))
        {
            left = Math.Min(left ?? double.MaxValue, page.Items.Min(i => i.Left));
            right = Math.Min(right ?? double.MaxValue, page.Width - page.Items.Max(i => i.Right));
            top = Math.Min(top ?? double.MaxValue, page.Height - page.Items.Max(i => i.Top));
            bottom = Math.Min(bottom ?? double.MaxValue, page.Items.Min(i => i.Bottom));
        }
        static double Clamp(double? value) => Math.Clamp(value ?? Default, Min, Max);
        return (Clamp(left), Clamp(right), Clamp(top), Clamp(bottom));
    }

    /// <summary>Distance between baselines relative to the font size in multi-line paragraphs (1.2 when unknown).</summary>
    private static double TypicalLineRatio(List<Item> items)
    {
        var ratios = items.Where(i => i.Lines.Count >= 2).Select(i => Pitch(i) / i.Size).Where(r => r is > 0.8 and < 3).ToList();
        return ratios.Count == 0 ? 1.2 : Median(ratios);
    }

    private static double Pitch(Item item)
    {
        var gaps = new List<double>();
        for (var i = 1; i < item.Lines.Count; i++)
            gaps.Add(item.Lines[i - 1].Baseline - item.Lines[i].Baseline);
        return gaps.Count == 0 ? item.Size * 1.2 : Median(gaps);
    }

    private static HParagraph PictureParagraph(Item item, Item? previous)
    {
        var bounds = item.PictureBounds!.Value;
        var paragraph = new HParagraph();
        var containerCenter = (item.ContainerLeft + item.ContainerRight) / 2;
        var centered = Math.Abs((bounds.Left + bounds.Right) / 2 - containerCenter) < Math.Max(6, (item.ContainerRight - item.ContainerLeft) * 0.05)
                       && bounds.Width < (item.ContainerRight - item.ContainerLeft) * 0.9;
        paragraph.Format = new HParaFormat(Align: centered ? HAlign.Center : HAlign.Left, Before: SpaceBefore(item, previous), After: 0,
            // Word's single line spacing (130 % in the model): a smaller multiple would cut off the top of the picture.
            LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 130));
        paragraph.Inlines.Add(new HImage(item.PicturePath!) { Width = Hwp(bounds.Width), Height = Hwp(bounds.Height) });
        return paragraph;
    }

    private static HParagraph TextParagraph(Item item, Item? previous, Dictionary<double, int> headingLevels, double lineRatio)
    {
        var size = item.Size;
        var paragraph = new HParagraph();
        if (headingLevels.TryGetValue(Math.Round(size), out var level) && item.Lines.Count <= 3 && item.Text.Length <= 200)
            paragraph.HeadingLevel = level;

        // Alignment and indents relative to the column (or text area) the paragraph is in. A right-to-left paragraph
        // starts at the right: its alignment and indents are measured from that side (HParaFormat.RightToLeft).
        var rightToLeft = item.RightToLeft;
        var containerWidth = Math.Max(1, item.ContainerRight - item.ContainerLeft);
        var lefts = item.Lines.Select(l => l.Left - item.ContainerLeft).ToList();
        var rights = item.Lines.Select(l => item.ContainerRight - l.Right).ToList();
        var (starts, ends) = rightToLeft ? (rights, lefts) : (lefts, rights);
        var tolerance = Math.Max(3, containerWidth * 0.03);
        HAlign align;
        if (item.Lines.All(l => Math.Abs((l.Left - item.ContainerLeft) - (item.ContainerRight - l.Right)) < tolerance) && lefts.Min() > containerWidth * 0.04)
            align = HAlign.Center;
        else if (ends.All(r => r < 3) && starts.Min() > containerWidth * 0.15)
            align = HAlign.Right;
        else if (item.Lines.Count >= 2 && ends.Take(ends.Count - 1).All(r => r < 2.5) && item.Width > containerWidth * 0.5)
            align = HAlign.Justify;
        else
            align = HAlign.Left;

        int? start = null, firstLine = null;
        if (align is HAlign.Left or HAlign.Justify)
        {
            var body = item.Lines.Count > 1 ? starts.Skip(1).Min() : starts[0];
            if (body > 2)
                start = Hwp(body);
            var first = starts[0] - Math.Max(0, body);
            if (Math.Abs(first) > 2)
                firstLine = Hwp(first);
        }

        // A tabbed row: a left tab stop where each part starts in the PDF.
        var tabs = rightToLeft ? [] : item.Lines.SelectMany(l => l.Tabs.Select(t => Hwp(l.Words[t].Left - item.ContainerLeft))).Distinct().Order()
            .Select(position => new HTabStop(position, HTabKind.Left, "NONE")).ToList();

        var pitch = item.Lines.Count >= 2 ? Pitch(item) : size * lineRatio;
        paragraph.Format = new HParaFormat(
            Align: align,
            Left: start,
            FirstLine: firstLine,
            Tabs: tabs.Count > 0 ? tabs : null,
            Before: SpaceBefore(item, previous, lineRatio),
            After: 0,
            // "At least" the PDF's line distance: close to the original and never cuts off a larger font.
            LineSpacing: new HLineSpacing(HLineSpacingKind.AtLeast, Hwp(Math.Clamp(pitch, size, size * 3))),
            RightToLeft: rightToLeft ? true : null);
        paragraph.MarkFormat = new HCharFormat(Size: (int)(Math.Round(size * 2) * 50));
        Runs(item, paragraph.Inlines, rightToLeft);
        return paragraph;
    }

    /// <summary>
    /// Extra space above an item: its distance to the item right above it (in the same column) minus what a line
    /// (or a picture's own height) takes anyway. Items at the top of a column get none.
    /// </summary>
    private static int SpaceBefore(Item item, Item? previous, double lineRatio = 1.2)
    {
        if (previous is null || previous.Bottom <= item.Top - 1 || previous.Right <= item.Left || previous.Left >= item.Right)
            return 0;
        // Edges of the line boxes: a text line reaches about 0.25 em below its baseline and 0.95 em above it.
        var above = previous.Lines.Count == 0 ? previous.Bottom : previous.Lines[^1].Baseline - 0.25 * previous.Size;
        var top = item.Lines.Count == 0 ? item.Top : item.Lines[0].Baseline + 0.95 * item.Size;
        var normal = item.Lines.Count == 0 || previous.Lines.Count == 0 ? 0 : (lineRatio - 1.2) * item.Size;
        var extra = above - top - normal;
        return extra > 1 ? Hwp(Math.Min(extra, 72)) : 0;
    }

    /// <summary>
    /// Words joined into runs of equal formatting; line ends become spaces (not inside CJK text or after a hyphen), or
    /// line breaks where the line ended early on purpose. Tabs of a tabbed row stay tabs.
    /// </summary>
    private static void Runs(Item item, List<HInline> output, bool rightToLeft)
    {
        var pieces = new List<(string Text, HCharFormat Format, string? Link)>();
        for (var l = 0; l < item.Lines.Count; l++)
        {
            var line = item.Lines[l];
            // Words in reading order: a tabbed row keeps its parts where they are.
            var words = line.Tabs.Count > 0 ? line.Words : InReadingOrder(line.Words, rightToLeft);
            for (var w = 0; w < words.Count; w++)
            {
                var word = words[w];
                var text = word.Text;
                if (pieces.Count > 0)
                {
                    var (previousText, previousFormat, previousLink) = pieces[^1];
                    if (w > 0 && line.Tabs.Contains(w))
                    {
                        pieces.Add(("\t", word.Format, null));
                    }
                    else if (w > 0)
                    {
                        pieces[^1] = (previousText + " ", previousFormat, previousLink);
                    }
                    else if (previousText.EndsWith('-') && previousText.Length > 1 && char.IsLetter(previousText[^2]) && text.Length > 0 && char.IsLower(text[0]))
                    {
                        pieces[^1] = (previousText[..^1], previousFormat, previousLink); // hyphenated at the line end
                    }
                    else if (EndedEarly(item, l, rightToLeft))
                    {
                        pieces.Add(("\n", word.Format, null));
                    }
                    else if (!(IsCjk(previousText[^1]) || IsCjk(text[0])))
                    {
                        pieces[^1] = (previousText + " ", previousFormat, previousLink);
                    }
                }
                pieces.Add((text, word.Format, word.Link));
            }
        }

        HLink? link = null;
        foreach (var group in Merge(pieces))
        {
            if (group.Text is "\t" or "\n")
            {
                link = null;
                output.Add(group.Text == "\t" ? new HTab(group.Format) : new HLineBreak(group.Format));
                continue;
            }
            var text = new HText(group.Text, group.Format);
            if (group.Link is null)
            {
                link = null;
                output.Add(text);
            }
            else
            {
                if (link is null || link.Target != group.Link)
                {
                    link = new HLink(group.Link);
                    output.Add(link);
                }
                link.Content.Add(text);
            }
        }
    }

    /// <summary>
    /// Whether line <paramref name="l"/> - 1 of the item ended although the first word of line <paramref name="l"/>
    /// would have fitted after it: a line break the author made (an address, a poem). Wrapped text never ends like
    /// that within its own width; past it only when the paragraph is clearly narrower than its column (an indented
    /// block of short lines), not a quote with a right indent.
    /// </summary>
    private static bool EndedEarly(Item item, int l, bool rightToLeft)
    {
        var previous = item.Lines[l - 1];
        var line = item.Lines[l];
        if (previous.Last.Text.EndsWith('-'))
            return false;
        var em = line.Size;
        var column = item.ContainerRight - item.ContainerLeft;
        var firstWord = line.First.Right - line.First.Left;
        if (rightToLeft)
        {
            // Mirrored: right-to-left lines end at the left.
            var start = previous.Left - 0.3 * em - firstWord;
            if (start > item.Left + 0.5 * em)
                return true;
            return start > item.ContainerLeft + 0.5 * em && item.Left - item.ContainerLeft > 0.25 * column;
        }
        var end = previous.Right + 0.3 * em + firstWord;
        if (end < item.Right - 0.5 * em)
            return true;
        return end < item.ContainerRight - 0.5 * em && item.ContainerRight - item.Right > 0.25 * column;
    }

    /// <summary>
    /// A line's words (left to right) in reading order: from right to left in a right-to-left paragraph, with numbers
    /// and Latin words in it kept left to right, and Arabic or Hebrew words in a left-to-right line turned around.
    /// </summary>
    private static List<TextWord> InReadingOrder(List<TextWord> words, bool rightToLeft)
    {
        if (!rightToLeft && !words.Any(w => ComplexScripts.Direction(w.Text) < 0))
            return words;
        var ordered = rightToLeft
            ? LogicalOrder(Enumerable.Reverse(words).ToList(), w => ComplexScripts.Direction(w.Text), rightToLeft: true)
            : LogicalOrder(words, w => ComplexScripts.Direction(w.Text), rightToLeft: false);
        // A full stop or comma after a word of the other direction is drawn on that word's far side ("2024." at the end
        // of an Arabic sentence shows as ".2024"): it comes first in the word's letters and goes back to the end.
        var other = rightToLeft ? 1 : -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var text = ordered[i].Text;
            var lead = 0;
            while (lead < text.Length - 1 && SentencePunctuation.Contains(text[lead]))
                lead++;
            if (lead > 0 && ComplexScripts.Direction(text) == other)
                ordered[i] = ordered[i] with { Text = text[lead..] + text[..lead] };
        }
        return ordered;
    }

    private const string SentencePunctuation = ".,:;!?،؛؟";

    private static IEnumerable<(string Text, HCharFormat Format, string? Link)> Merge(List<(string Text, HCharFormat Format, string? Link)> pieces)
    {
        var sb = new StringBuilder();
        HCharFormat format = default;
        string? link = null;
        var open = false;
        foreach (var piece in pieces)
        {
            if (open && (piece.Format != format || piece.Link != link || piece.Text is "\t" or "\n" || sb.ToString() is "\t" or "\n"))
            {
                yield return (sb.ToString(), format, link);
                sb.Clear();
            }
            sb.Append(piece.Text);
            (format, link, open) = (piece.Format, piece.Link, true);
        }
        if (open)
            yield return (sb.ToString() is "\t" or "\n" ? sb.ToString() : sb.ToString().TrimEnd(), format, link);
    }

    /// <summary>Chinese and Japanese text has no spaces between words (Korean does).</summary>
    private static bool IsCjk(char ch) => ch is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '｠' or >= '　' and <= '〿';

    // ───────────────────────── Helpers ─────────────────────────

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Plain text of a document read by <see cref="ReadText"/>: paragraphs separated by an empty line.</summary>
    public static string PlainText(HDocument document)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>())
        {
            var text = string.Concat(paragraph.Inlines.SelectMany(i => i is HLink link ? link.Content : [i]).Select(i => i switch
            {
                HText t => t.Text,
                HTab => "\t",
                HLineBreak => "\r\n",
                _ => "",
            })).Trim();
            if (text.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append("\r\n\r\n");
            sb.Append(text);
        }
        return sb.Length == 0 ? "" : sb.Append("\r\n").ToString();
    }
}
