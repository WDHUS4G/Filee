// PDF → HDocument with PdfPig (Apache-2.0), so PDF → DOCX / HWPX / TXT needs no Word, LibreOffice or OCR engine.
//
// Every page's words (with their positions, fonts, sizes and colours) become lines, lines become paragraphs, and the
// paragraphs and pictures are put in reading order, also on two-column pages (PdfDocumentReader.Layout). Headings
// come from font sizes, bold / italic from the fonts, page size and margins from the page. Headers and footers that
// repeat on the pages (and bare page numbers) are left out. A page without text (a scan) becomes the rendered page as
// a picture (PDFium via PDFtoImage), so the result is complete; there is no OCR.
//
// Rows laid out with tab stops (a number, a label and a date far to the right) stay one line with tabs at the PDF's
// positions, and a line that ends early although the next line's first word would have fitted (an address) keeps its
// line break.
//
// Not converted: tables (their cells come out as paragraphs in reading order), vector drawings on text pages,
// annotations other than links, form fields.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;
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
    };

    private readonly string _path;
    private readonly string _mediaFolder;
    private readonly bool _textOnly;
    private byte[]? _pdfBytes;
    private int _pictures;

    private PdfDocumentReader(string path, string mediaFolder, bool textOnly)
    {
        _path = path;
        _mediaFolder = mediaFolder;
        _textOnly = textOnly;
    }

    /// <summary>Reads <paramref name="path"/> with pictures (extracted into <paramref name="mediaFolder"/>) and scans.</summary>
    public static HDocument Read(string path, string mediaFolder) => new PdfDocumentReader(path, mediaFolder, textOnly: false).ReadDocument();

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
    }

    /// <summary>A paragraph or a picture on a page, the unit of reading order.</summary>
    private sealed class Item
    {
        public List<TextLine> Lines { get; } = [];
        public string? PicturePath { get; init; }
        public PdfRectangle? PictureBounds { get; init; }

        public double Left => PictureBounds?.Left ?? Lines.Min(l => l.Left);
        public double Right => PictureBounds?.Right ?? Lines.Max(l => l.Right);
        public double Top => PictureBounds?.Top ?? Lines[0].Top;
        public double Bottom => PictureBounds?.Bottom ?? Lines[^1].Bottom;
        public double Width => Right - Left;
        public bool IsPicture => PicturePath is not null;

        /// <summary>Size of most of the text (by characters).</summary>
        public double Size => Lines.SelectMany(l => l.Words).GroupBy(w => Math.Round(w.Size * 2) / 2)
            .OrderByDescending(g => g.Sum(w => w.Text.Length)).Select(g => g.Key).FirstOrDefault(10);

        public string Text => string.Join(' ', Lines.Select(l => l.Text));

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

    private static InvalidOperationException PasswordProtected() =>
        new("The PDF is password-protected. Open it with its password and save a copy without one, then convert that copy.");

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

        var words = new List<TextWord>();
        foreach (var word in page.GetWords(NearestNeighbourWordExtractor.Instance))
        {
            if (word.TextOrientation != TextOrientation.Horizontal || string.IsNullOrWhiteSpace(word.Text))
                continue;
            var letters = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0)
                continue;
            // Invisible text is the OCR layer of a scan: part of the text, not of the page's look.
            if (!_textOnly && letters.All(l => l.RenderingMode == TextRenderingMode.Neither))
                continue;
            var box = Shift(word.BoundingBox, crop);
            var size = Median(letters.Select(l => l.PointSize > 0 ? l.PointSize : l.FontSize));
            var center = new PdfPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
            var link = links.FirstOrDefault(l => Inside(l.Bounds, center)).Uri;
            words.Add(new TextWord(CleanText(word.Text), box.Left, box.Right, box.Top, box.Bottom,
                letters[0].StartBaseLine.Y - crop.Bottom, Math.Max(1, size), Format(letters[0], size), link));
        }

        if (words.Count == 0 || (rotated && !_textOnly))
        {
            // A scan, a page of drawings, or a turned page: the rendered page keeps everything.
            content.Scanned = true;
            if (!_textOnly)
                content.ScanPath = RenderScan(page.Number - 1);
            return content;
        }

        var lines = Lines(words);
        var items = Paragraphs(lines);
        if (JoinTabbedRows(lines, items) is { } joined)
            items = Paragraphs(joined);
        content.Items = items;
        if (!_textOnly)
            content.Items.AddRange(Pictures(page, content, crop));
        return content;
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

    /// <summary>
    /// Leaves out paragraphs at the top or bottom edge that repeat on most pages (running heads, footers; digits are
    /// ignored so "Page 3" matches "Page 4") and bare page numbers there.
    /// </summary>
    private static void RemoveRepeatedHeadersAndFooters(List<PageContent> pages)
    {
        var textPages = pages.Where(p => !p.Scanned).ToList();
        if (textPages.Count < 2)
            return;
        bool AtEdge(Item item, PageContent page) => item.Top > page.Height * 0.9 || item.Bottom < page.Height * 0.1;
        static string Key(Item item) => Regex.Replace(item.Text, "\\d+", "#").Trim();

        var counts = textPages
            .SelectMany(p => p.Items.Where(i => !i.IsPicture && AtEdge(i, p)).Select(i => (Page: p.Index, Key: Key(i))).Distinct())
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var needed = Math.Max(2, (int)Math.Ceiling(textPages.Count * 0.5));
        foreach (var page in textPages)
        {
            page.Items.RemoveAll(i => !i.IsPicture && AtEdge(i, page) && i.Lines.Count <= 2
                                      && (counts.GetValueOrDefault(Key(i)) >= needed || PageNumber.IsMatch(i.Text)));
        }
    }

    // ───────────────────────── Building the document ─────────────────────────

    private HDocument Build(List<PageContent> pages, string? title)
    {
        var document = new HDocument { Title = title };
        var items = pages.SelectMany(p => p.Items.Where(i => !i.IsPicture)).ToList();
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
                    blocks.Add(item.IsPicture ? PictureParagraph(item, previous) : TextParagraph(item, previous, headingLevels, lineRatio));
                    previous = item;
                }
                if (blocks.Count == first)
                    blocks.Add(new HParagraph());
            }

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

        // Alignment and indents relative to the column (or text area) the paragraph is in.
        var containerWidth = Math.Max(1, item.ContainerRight - item.ContainerLeft);
        var lefts = item.Lines.Select(l => l.Left - item.ContainerLeft).ToList();
        var rights = item.Lines.Select(l => item.ContainerRight - l.Right).ToList();
        var tolerance = Math.Max(3, containerWidth * 0.03);
        HAlign align;
        if (item.Lines.All(l => Math.Abs((l.Left - item.ContainerLeft) - (item.ContainerRight - l.Right)) < tolerance) && lefts.Min() > containerWidth * 0.04)
            align = HAlign.Center;
        else if (rights.All(r => r < 3) && lefts.Min() > containerWidth * 0.15)
            align = HAlign.Right;
        else if (item.Lines.Count >= 2 && rights.Take(rights.Count - 1).All(r => r < 2.5) && item.Width > containerWidth * 0.5)
            align = HAlign.Justify;
        else
            align = HAlign.Left;

        int? left = null, firstLine = null;
        if (align is HAlign.Left or HAlign.Justify)
        {
            var body = item.Lines.Count > 1 ? lefts.Skip(1).Min() : lefts[0];
            if (body > 2)
                left = Hwp(body);
            var first = lefts[0] - Math.Max(0, body);
            if (Math.Abs(first) > 2)
                firstLine = Hwp(first);
        }

        // A tabbed row: a left tab stop where each part starts in the PDF.
        var tabs = item.Lines.SelectMany(l => l.Tabs.Select(t => Hwp(l.Words[t].Left - item.ContainerLeft))).Distinct().Order()
            .Select(position => new HTabStop(position, HTabKind.Left, "NONE")).ToList();

        var pitch = item.Lines.Count >= 2 ? Pitch(item) : size * lineRatio;
        paragraph.Format = new HParaFormat(
            Align: align,
            Left: left,
            FirstLine: firstLine,
            Tabs: tabs.Count > 0 ? tabs : null,
            Before: SpaceBefore(item, previous, lineRatio),
            After: 0,
            // "At least" the PDF's line distance: close to the original and never cuts off a larger font.
            LineSpacing: new HLineSpacing(HLineSpacingKind.AtLeast, Hwp(Math.Clamp(pitch, size, size * 3))));
        paragraph.MarkFormat = new HCharFormat(Size: (int)(Math.Round(size * 2) * 50));
        Runs(item, paragraph.Inlines);
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
        var above = previous.IsPicture ? previous.Bottom : previous.Lines[^1].Baseline - 0.25 * previous.Size;
        var top = item.IsPicture ? item.Top : item.Lines[0].Baseline + 0.95 * item.Size;
        var normal = item.IsPicture || previous.IsPicture ? 0 : (lineRatio - 1.2) * item.Size;
        var extra = above - top - normal;
        return extra > 1 ? Hwp(Math.Min(extra, 72)) : 0;
    }

    /// <summary>
    /// Words joined into runs of equal formatting; line ends become spaces (not inside CJK text or after a hyphen), or
    /// line breaks where the line ended early on purpose. Tabs of a tabbed row stay tabs.
    /// </summary>
    private static void Runs(Item item, List<HInline> output)
    {
        var pieces = new List<(string Text, HCharFormat Format, string? Link)>();
        for (var l = 0; l < item.Lines.Count; l++)
        {
            var line = item.Lines[l];
            for (var w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
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
                    else if (EndedEarly(item, l))
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
    private static bool EndedEarly(Item item, int l)
    {
        var previous = item.Lines[l - 1];
        var line = item.Lines[l];
        if (previous.Words[^1].Text.EndsWith('-'))
            return false;
        var em = line.Size;
        var end = previous.Right + 0.3 * em + (line.Words[0].Right - line.Words[0].Left);
        if (end < item.Right - 0.5 * em)
            return true;
        var column = item.ContainerRight - item.ContainerLeft;
        return end < item.ContainerRight - 0.5 * em && item.ContainerRight - item.Right > 0.25 * column;
    }

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
