// HDocument → clean XHTML with one small style sheet: the chapters of EPUB files, the page of HTMLZ, and single-page
// HTML exports. Structure is kept (headings, paragraphs with alignment and indents, lists, tables with spans,
// pictures, links and anchors, footnotes); page setup, headers and footers are left to the reading system.
//
// Chapters are split at level-1 headings and page breaks (and when a chapter grows very long), and links to
// bookmarks in other chapters point at the right file.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>How <see cref="XhtmlWriter"/> lays out its output.</summary>
internal sealed class XhtmlOptions
{
    /// <summary>Split into chapters at level-1 headings and page breaks (EPUB); false writes one page.</summary>
    public bool SplitChapters { get; init; }

    /// <summary>File name of chapter n (0-based), used for links between chapters.</summary>
    public Func<int, string> ChapterFile { get; init; } = index => $"ch{index + 1:000}.xhtml";

    /// <summary>The src of a picture file (a path in the package or a data: URI), or null to leave it out.</summary>
    public required Func<string, string?> ImageSource { get; init; }

    /// <summary>Mark footnotes and references with epub:type (EPUB 3 pop-up notes).</summary>
    public bool Epub { get; init; }
}

/// <summary>One output file of <see cref="XhtmlWriter"/>.</summary>
/// <param name="Title">Text of the chapter's first heading, if any.</param>
/// <param name="Body">Markup inside &lt;body&gt;.</param>
/// <param name="PlainStart">The first words of the chapter, a table of contents entry for chapters without headings.</param>
/// <param name="Headings">Headings for a table of contents, with their link target ("ch002.xhtml#_h7").</param>
internal sealed record XhtmlChapter(string FileName, string? Title, string Body, string PlainStart, IReadOnlyList<XhtmlHeading> Headings);

internal sealed record XhtmlHeading(int Level, string Text, string Target);

internal sealed class XhtmlWriter
{
    /// <summary>Style sheet of the pages (EPUB style.css, inlined in single-page HTML).</summary>
    public const string Css =
        "body{font-family:serif;line-height:1.6;margin:0 3%;}\n" +
        "h1,h2,h3,h4,h5,h6{line-height:1.3;margin:1.2em 0 .6em;page-break-after:avoid;}\n" +
        "p{margin:0 0 .6em;}\n" +
        "li p{margin:.2em 0;}\n" +
        "table{border-collapse:collapse;margin:.8em 0;}\n" +
        "td,th{border:1px solid #999;padding:.2em .5em;vertical-align:top;}\n" +
        "table.plain td,table.plain th{border:none;}\n" +
        "caption{font-style:italic;margin-bottom:.3em;}\n" +
        "img{max-width:100%;}\n" +
        "pre{white-space:pre-wrap;background:#f4f3f8;padding:.5em .8em;}\n" +
        "code,pre{font-family:monospace;}\n" +
        "code{background:#f1f0f5;}\n" +
        "mark{background:#fff3a3;}\n" +
        "hr{border:none;border-top:1px solid #999;width:30%;margin:1.2em auto;}\n" +
        ".page-break{page-break-before:always;break-before:page;}\n" +
        ".box{border:1px solid #999;padding:.3em .6em;display:inline-block;}\n" +
        "aside.footnote,.footnotes{font-size:.9em;}\n";

    /// <summary>Chapters are cut at the next plain paragraph once they have this many characters.</summary>
    private const int ChapterLimit = 150_000;

    private readonly XhtmlOptions _options;
    private readonly Dictionary<string, int> _bookmarkChapter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly int _bodySize;
    private int _chapter;
    private int _headingCount;
    private int _noteCount;
    private List<XhtmlHeading> _headings = [];
    private StringBuilder _notes = new();

    private XhtmlWriter(XhtmlOptions options, int bodySize)
    {
        _options = options;
        _bodySize = bodySize;
    }

    /// <summary>Converts a document into one page or a list of chapters.</summary>
    public static IReadOnlyList<XhtmlChapter> Write(HDocument document, XhtmlOptions options)
    {
        var chapters = Split(document, options.SplitChapters);
        var writer = new XhtmlWriter(options, BodySize(document));
        for (var i = 0; i < chapters.Count; i++)
            foreach (var inline in HDocumentWalker.Inlines(chapters[i]))
                if (inline is HBookmark bookmark)
                    writer._bookmarkChapter.TryAdd(bookmark.Name, i);

        var result = new List<XhtmlChapter>();
        for (var i = 0; i < chapters.Count; i++)
        {
            writer._chapter = i;
            writer._headings = [];
            writer._notes = new StringBuilder();
            var body = new StringBuilder();
            writer.Blocks(chapters[i], body);
            if (writer._notes.Length > 0)
                body.Append(options.Epub ? "<section class=\"footnotes\" epub:type=\"footnotes\">" : "<section class=\"footnotes\"><hr/>")
                    .Append(writer._notes).Append("</section>\n");
            var plain = HDocumentWalker.Text(HDocumentWalker.Inlines(chapters[i]).Take(200));
            result.Add(new XhtmlChapter(options.ChapterFile(i), writer._headings.FirstOrDefault()?.Text, body.ToString(),
                plain.Length > 60 ? plain[..60] : plain, writer._headings));
        }
        return result;
    }

    /// <summary>A complete page around a body written by <see cref="Write"/>.</summary>
    /// <param name="styleSheet">Linked style sheet (EPUB, HTMLZ); null embeds <see cref="Css"/>.</param>
    /// <param name="epub">An EPUB content document: XML declaration and the epub namespace (else plain HTML5).</param>
    /// <param name="head">Extra markup for &lt;head&gt; (e.g. a viewport for fixed-layout pages).</param>
    public static string Page(string title, string body, string language, string? styleSheet, bool epub, string head = "")
    {
        var sb = new StringBuilder();
        if (epub)
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<!DOCTYPE html>\n");
        sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\"");
        if (epub)
            sb.Append(" xmlns:epub=\"http://www.idpf.org/2007/ops\"");
        sb.Append($" lang=\"{Escape(language)}\" xml:lang=\"{Escape(language)}\">\n<head>\n<meta charset=\"utf-8\"/>\n");
        sb.Append($"<title>{Escape(title)}</title>\n");
        sb.Append(head);
        sb.Append(styleSheet is null
            ? $"<style>\n{Css}</style>\n"
            : $"<link rel=\"stylesheet\" type=\"text/css\" href=\"{Escape(styleSheet)}\"/>\n");
        sb.Append("</head>\n<body>\n").Append(body).Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>XML-escaped text without characters XML 1.0 forbids.</summary>
    internal static string Escape(string text) => HwpxWriter.Escape(text);

    // ───────────────────────── Chapters ─────────────────────────

    /// <summary>Blocks per chapter: a new one at a section start, a page break or a level-1 heading.</summary>
    private static List<List<HBlock>> Split(HDocument document, bool split)
    {
        var chapters = new List<List<HBlock>> { new() };
        var length = 0;
        foreach (var section in document.Sections)
        {
            var sectionStart = true;
            foreach (var block in section.Blocks)
            {
                var current = chapters[^1];
                if (split && HasContent(current))
                {
                    var paragraph = block as HParagraph;
                    var cut = sectionStart
                              || paragraph is { PageBreakBefore: true } or { HeadingLevel: 1 }
                              || (length > ChapterLimit && paragraph is { List: null, HeadingLevel: 0 });
                    if (cut)
                    {
                        chapters.Add(current = []);
                        length = 0;
                    }
                }
                sectionStart = false;
                current.Add(block);
                length += HDocumentWalker.Inlines([block]).OfType<HText>().Sum(t => t.Text.Length);
            }
        }
        return chapters;
    }

    private static bool HasContent(List<HBlock> blocks) =>
        blocks.Any(b => b is HTable || HDocumentWalker.Inlines([b]).Any(i => i is HImage || i is HText { Text: var text } && !string.IsNullOrWhiteSpace(text)));

    /// <summary>The most common text size, written as 1em (other sizes become relative to it).</summary>
    private static int BodySize(HDocument document)
    {
        var sizes = document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).OfType<HText>()
            .Where(t => t.Format.Size is not null)
            .GroupBy(t => t.Format.Size!.Value)
            .Select(g => (Size: g.Key, Characters: g.Sum(t => t.Text.Length)))
            .OrderByDescending(g => g.Characters)
            .FirstOrDefault();
        return sizes.Size > 0 ? sizes.Size : 1000;
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void Blocks(List<HBlock> blocks, StringBuilder sb)
    {
        var i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i] is HParagraph { List: not null })
                i = List(blocks, i, sb);
            else if (IsCodeLine(blocks[i]))
                i = Code(blocks, i, sb);
            else
                Block(blocks[i++], sb);
        }
    }

    private void Block(HBlock block, StringBuilder sb)
    {
        switch (block)
        {
            case HParagraph paragraph:
                Paragraph(paragraph, sb);
                break;
            case HTable table:
                Table(table, sb);
                break;
        }
    }

    private void Paragraph(HParagraph paragraph, StringBuilder sb)
    {
        var breakClass = paragraph.PageBreakBefore && !_options.SplitChapters ? " class=\"page-break\"" : "";
        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && !HasVisible(paragraph.Inlines))
        {
            sb.Append(Anchors(paragraph.Inlines)).Append("<hr/>\n");
            return;
        }
        var content = Inlines(paragraph.Inlines, paragraph.HeadingLevel > 0);
        if (!HasVisible(paragraph.Inlines))
        {
            // Empty paragraphs are spacing in word processors; reading systems space paragraphs themselves.
            if (content.Length > 0)
                sb.Append("<div").Append(breakClass).Append('>').Append(content).Append("</div>\n");
            return;
        }

        var style = ParagraphStyle(paragraph.Format);
        if (paragraph.HeadingLevel > 0)
        {
            var level = Math.Clamp(paragraph.HeadingLevel, 1, 6);
            var id = $"_h{++_headingCount}";
            _usedIds.Add(id);
            _headings.Add(new XhtmlHeading(paragraph.HeadingLevel, HDocumentWalker.Text(paragraph.Inlines), $"{_options.ChapterFile(_chapter)}#{id}"));
            sb.Append($"<h{level} id=\"{id}\"").Append(breakClass).Append(style).Append('>').Append(content).Append($"</h{level}>\n");
            return;
        }
        sb.Append("<p").Append(breakClass).Append(style).Append('>').Append(content).Append("</p>\n");
    }

    private static string ParagraphStyle(HParaFormat format)
    {
        var styles = new List<string>();
        switch (format.Align)
        {
            case HAlign.Center:
                styles.Add("text-align:center");
                break;
            case HAlign.Right:
                styles.Add("text-align:right");
                break;
            case HAlign.Justify or HAlign.Distribute:
                styles.Add("text-align:justify");
                break;
        }
        // HWPUNIT → em of a 10 pt body text (1000 HWPUNIT).
        if (format.Left is > 0 and var left)
            styles.Add($"margin-left:{Em(left)}");
        if (format.FirstLine is { } first && first != 0)
            styles.Add($"text-indent:{Em(first)}");
        return styles.Count == 0 ? "" : $" style=\"{string.Join(';', styles)}\"";
    }

    private static string Em(int hwpUnits) => (hwpUnits / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + "em";

    /// <summary>A paragraph that is all code (shaded like the readers mark code blocks).</summary>
    private static bool IsCodeLine(HBlock block) =>
        block is HParagraph { List: null, HeadingLevel: 0 } paragraph
        && paragraph.Inlines.Count > 0
        && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    private static int Code(List<HBlock> blocks, int start, StringBuilder sb)
    {
        var lines = new List<string>();
        var i = start;
        while (i < blocks.Count && IsCodeLine(blocks[i]))
            lines.Add(string.Concat(((HParagraph)blocks[i++]).Inlines.OfType<HText>().Select(t => t.Text)));
        sb.Append("<pre><code>").Append(Escape(string.Join("\n", lines))).Append("</code></pre>\n");
        return i;
    }

    private sealed class OpenList(HNumbering numbering, int level, bool ordered)
    {
        public HNumbering Numbering { get; } = numbering;
        public int Level { get; } = level;
        public bool Ordered { get; } = ordered;
    }

    /// <summary>Consecutive list paragraphs as nested ul / ol; returns the index after the list.</summary>
    private int List(List<HBlock> blocks, int start, StringBuilder sb)
    {
        var stack = new List<OpenList>();
        var i = start;
        for (; i < blocks.Count && blocks[i] is HParagraph { List: { } item } paragraph; i++)
        {
            while (stack.Count > 0 && stack[^1].Level > item.Level)
                Close(stack, sb);
            if (!item.Numbered)
            {
                if (stack.Count == 0)
                    Paragraph(paragraph, sb);
                else
                    sb.Append("<p").Append(ParagraphStyle(paragraph.Format with { Left = null })).Append('>').Append(Inlines(paragraph.Inlines, false)).Append("</p>");
                continue;
            }
            if (stack.Count > 0 && stack[^1].Level == item.Level && !ReferenceEquals(stack[^1].Numbering, item.Numbering))
                Close(stack, sb);
            if (stack.Count > 0 && stack[^1].Level == item.Level)
            {
                sb.Append("</li>\n");
            }
            else
            {
                var level = item.Numbering.Levels.Count == 0 ? null : item.Numbering.Levels[Math.Min(item.Level, item.Numbering.Levels.Count - 1)];
                var ordered = level is { Bullet: false };
                stack.Add(new OpenList(item.Numbering, item.Level, ordered));
                if (!ordered)
                {
                    sb.Append("<ul>\n");
                }
                else
                {
                    var type = level!.Format switch
                    {
                        "LATIN_SMALL" => " type=\"a\"",
                        "LATIN_CAPITAL" => " type=\"A\"",
                        "ROMAN_SMALL" => " type=\"i\"",
                        "ROMAN_CAPITAL" => " type=\"I\"",
                        _ => "",
                    };
                    sb.Append("<ol").Append(type).Append(level.Start != 1 ? $" start=\"{level.Start}\"" : "").Append(">\n");
                }
            }
            sb.Append("<li").Append(ParagraphStyle(paragraph.Format with { Left = null, FirstLine = null })).Append('>')
              .Append(Inlines(paragraph.Inlines, false));
        }
        while (stack.Count > 0)
            Close(stack, sb);
        return i;
    }

    private static void Close(List<OpenList> stack, StringBuilder sb)
    {
        sb.Append("</li>\n").Append(stack[^1].Ordered ? "</ol>\n" : "</ul>\n");
        stack.RemoveAt(stack.Count - 1);
    }

    private void Table(HTable table, StringBuilder sb)
    {
        sb.Append(table.Borders == HBorders.Empty ? "<table class=\"plain\"" : "<table");
        if (table.Indent > 0)
            sb.Append($" style=\"margin-left:{Em(table.Indent)}\"");
        sb.Append(">\n");
        if (table.Caption.Count > 0)
            sb.Append("<caption>").Append(string.Join(" ", table.Caption.OfType<HParagraph>().Select(p => Inlines(p.Inlines, false)))).Append("</caption>\n");
        var header = table.Rows.TakeWhile(r => r.Header).Count();
        if (header > 0 && header < table.Rows.Count)
            sb.Append("<thead>\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            if (r == header && header > 0 && header < table.Rows.Count)
                sb.Append("</thead>\n<tbody>\n");
            var row = table.Rows[r];
            sb.Append("<tr>");
            foreach (var cell in row.Cells)
            {
                var tag = row.Header ? "th" : "td";
                sb.Append('<').Append(tag);
                if (cell.RowSpan > 1)
                    sb.Append($" rowspan=\"{cell.RowSpan}\"");
                if (cell.ColSpan > 1)
                    sb.Append($" colspan=\"{cell.ColSpan}\"");
                var styles = new List<string>();
                if (cell.Fill is { } fill)
                    styles.Add($"background-color:{fill}");
                if (cell.VerticalAlign != HVerticalAlign.Top)
                    styles.Add(cell.VerticalAlign == HVerticalAlign.Center ? "vertical-align:middle" : "vertical-align:bottom");
                if (styles.Count > 0)
                    sb.Append($" style=\"{string.Join(';', styles)}\"");
                sb.Append('>');
                if (cell.Blocks is [HParagraph { List: null, HeadingLevel: 0 } only])
                    sb.Append(Inlines(only.Inlines, false)); // a single paragraph needs no <p>
                else
                    Blocks(cell.Blocks, sb);
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>\n");
        }
        if (header > 0 && header < table.Rows.Count)
            sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    // ───────────────────────── Inlines ─────────────────────────

    private static bool HasVisible(List<HInline> inlines) => HDocumentWalker.InlinesOf(inlines).Any(i => i switch
    {
        HText text => !string.IsNullOrWhiteSpace(text.Text) || text.Text.Contains(' '),
        HImage or HNote or HTextBox or HTab => true,
        _ => false,
    });

    private string Anchors(IEnumerable<HInline> inlines) =>
        string.Concat(inlines.OfType<HBookmark>().Select(b => Id(b.Name)).OfType<string>().Select(id => $"<a id=\"{id}\"></a>"));

    private string Inlines(List<HInline> inlines, bool heading)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Run(text.Text, text.Format, heading, sb);
                    break;
                case HLineBreak:
                    sb.Append("<br/>");
                    break;
                case HTab:
                    sb.Append(' ');
                    break;
                case HBookmark bookmark:
                    if (Id(bookmark.Name) is { } id)
                        sb.Append($"<a id=\"{id}\"></a>");
                    break;
                case HLink link:
                    Link(link, heading, sb);
                    break;
                case HImage image:
                    Image(image, sb);
                    break;
                case HNote note:
                    Note(note, sb);
                    break;
                case HTextBox box:
                    {
                        var parts = box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines, false)).Where(p => p.Length > 0).ToList();
                        if (parts.Count > 0)
                            sb.Append("<span class=\"box\">").Append(string.Join("<br/>", parts)).Append("</span>");
                        break;
                    }
            }
        }
        return sb.ToString();
    }

    private void Run(string text, HCharFormat format, bool heading, StringBuilder sb)
    {
        if (text.Length == 0)
            return;
        var close = new Stack<string>();
        void Open(string tag, string attributes = "")
        {
            sb.Append('<').Append(tag).Append(attributes).Append('>');
            close.Push($"</{tag}>");
        }

        var styles = new List<string>();
        if (format.Color is { } color && color != "#000000")
            styles.Add($"color:{color}");
        if (format.Shade is { } shade && shade is not (HtmlReader.CodeShade or HtmlReader.MarkShade))
            styles.Add($"background-color:{shade}");
        if (format.Size is { } size && Math.Abs(size - _bodySize) > _bodySize / 20 && format.Superscript is not true && format.Subscript is not true)
            styles.Add($"font-size:{(size / (double)_bodySize).ToString("0.##", CultureInfo.InvariantCulture)}em");
        if (heading && format.Bold is false)
            styles.Add("font-weight:normal");
        if (styles.Count > 0)
            Open("span", $" style=\"{string.Join(';', styles)}\"");
        if (format.Shade == HtmlReader.CodeShade)
            Open("code");
        if (format.Shade == HtmlReader.MarkShade)
            Open("mark");
        if (format.Bold is true && !heading)
            Open("b");
        if (format.Italic is true)
            Open("i");
        if (format.Underline is true)
            Open("u");
        if (format.Strike is true)
            Open("s");
        if (format.Superscript is true)
            Open("sup");
        else if (format.Subscript is true)
            Open("sub");
        sb.Append(Escape(text));
        while (close.Count > 0)
            sb.Append(close.Pop());
    }

    private void Link(HLink link, bool heading, StringBuilder sb)
    {
        var content = Inlines(link.Content, heading);
        var href = Href(link.Target);
        if (href is null)
            sb.Append(content);
        else
            sb.Append($"<a href=\"{Escape(href)}\">").Append(content).Append("</a>");
    }

    /// <summary>The href of a link: bookmarks point into the right chapter; only absolute URIs leave the book.</summary>
    private string? Href(string target)
    {
        if (target.StartsWith('#'))
        {
            var name = target[1..];
            if (!_bookmarkChapter.TryGetValue(name, out var chapter) || Id(name) is not { } id)
                return null; // no such bookmark: keep the text only
            return !_options.SplitChapters || chapter == _chapter ? "#" + id : $"{_options.ChapterFile(chapter)}#{id}";
        }
        var colon = target.IndexOf(':');
        return colon > 1 && target[..colon].All(char.IsAsciiLetter) ? target : null;
    }

    /// <summary>A valid, unique XML id for a bookmark name (the same name always gets the same id).</summary>
    private string? Id(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (_ids.TryGetValue(name, out var known))
            return known;
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        if (!char.IsAsciiLetter(sb[0]) && sb[0] != '_')
            sb.Insert(0, "id-");
        var id = sb.ToString();
        for (var n = 2; !_usedIds.Add(id); n++)
            id = $"{sb}-{n}";
        _ids[name] = id;
        return id;
    }

    private void Image(HImage image, StringBuilder sb)
    {
        if (_options.ImageSource(image.Path) is not { } source)
            return;
        sb.Append($"<img src=\"{Escape(source)}\" alt=\"\"");
        if (image.Width is > 0 and var width)
            sb.Append($" style=\"width:{Math.Max(1, (int)Math.Round(width / HwpxUnits.PerPixel))}px\"");
        sb.Append("/>");
    }

    private void Note(HNote note, StringBuilder sb)
    {
        var number = ++_noteCount;
        var epub = _options.Epub ? " epub:type=\"noteref\"" : "";
        sb.Append($"<a class=\"noteref\" href=\"#_fn{number}\" id=\"_fnref{number}\"{epub}><sup>{number}</sup></a>");

        var content = new StringBuilder();
        Blocks(note.Blocks, content);
        var type = _options.Epub ? " epub:type=\"footnote\"" : "";
        _notes.Append($"<aside class=\"footnote\" id=\"_fn{number}\"{type}><p><a href=\"#_fnref{number}\">{number}.</a></p>\n")
              .Append(content).Append("</aside>\n");
    }
}
