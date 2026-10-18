// FictionBook 2 (FB2) → Book. FB2 is XML: description/title-info holds the metadata (title, authors, language,
// cover), bodies hold nested sections (their titles become headings by depth), binary elements hold the pictures
// as base64. Poems, epigraphs, citations, subtitles and tables are kept; note links (type="note") become real
// footnotes with the text of the notes body. Every top-level section starts a new page.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal sealed class Fb2Reader
{
    private const int Indent = 2000;
    private readonly Dictionary<string, string> _binaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _notes = new(StringComparer.Ordinal);

    private Fb2Reader()
    {
    }

    /// <summary>Reads an FB2 book's content (for the reader registry of the HWPX writer).</summary>
    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder)
    {
        var root = Load(path).Root ?? throw new InvalidDataException("The FB2 file is empty.");
        if (root.Name.LocalName != "FictionBook")
            throw new InvalidDataException("This is not a FictionBook (FB2) file.");
        var reader = new Fb2Reader();
        var folder = EbookFiles.NewFolder(workFolder, "fb2");
        reader.SaveBinaries(root, folder);

        var bodies = Children(root, "body").ToList();
        foreach (var notes in bodies.Where(b => (string?)b.Attribute("name") is "notes" or "comments"))
            foreach (var section in notes.Descendants().Where(e => e.Name.LocalName == "section" && e.Attribute("id") is not null))
                reader._notes.TryAdd((string)section.Attribute("id")!, section);

        var metadata = reader.Metadata(root);
        var sectionBlocks = new HSection();
        foreach (var body in bodies.Where(b => (string?)b.Attribute("name") is not ("notes" or "comments")))
            reader.Body(body, sectionBlocks.Blocks);
        var document = new HDocument { Title = metadata.Title };
        document.Sections.Add(sectionBlocks);
        HtmlReader.PruneBookmarks(document);
        return new Book(document, metadata);
    }

    private static XDocument Load(string path)
    {
        // FB2 files are often windows-1251; the XML declaration names the encoding.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        // Spaces between inline elements ("<strong>a</strong> <emphasis>b</emphasis>") are text.
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static XElement? Child(XElement? parent, string name) => Children(parent, name).FirstOrDefault();

    /// <summary>The xlink:href of an image or link (namespace prefixes vary: l:, xlink:, none).</summary>
    private static string? Href(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;

    private BookMetadata Metadata(XElement root)
    {
        var info = Child(Child(root, "description"), "title-info");
        var metadata = new BookMetadata
        {
            Title = Child(info, "book-title")?.Value.Trim(),
            Language = Child(info, "lang")?.Value.Trim() is { Length: > 0 } lang ? lang : null,
            Description = Child(info, "annotation")?.Value.Trim() is { Length: > 0 } annotation ? annotation : null,
            Publisher = Child(Child(Child(root, "description"), "publish-info"), "publisher")?.Value.Trim(),
        };
        foreach (var author in Children(info, "author"))
        {
            var parts = new[] { "first-name", "middle-name", "last-name" }.Select(n => Child(author, n)?.Value.Trim()).Where(p => !string.IsNullOrEmpty(p));
            var name = string.Join(" ", parts);
            if (name.Length == 0)
                name = Child(author, "nickname")?.Value.Trim() ?? "";
            if (name.Length > 0)
                metadata.Authors.Add(name);
        }
        if (Child(Child(info, "coverpage"), "image") is { } cover && Href(cover) is { } href && _binaries.TryGetValue(href.TrimStart('#'), out var file))
            metadata.CoverImage = file;
        return metadata;
    }

    private void SaveBinaries(XElement root, string folder)
    {
        foreach (var binary in Children(root, "binary"))
        {
            var id = (string?)binary.Attribute("id");
            if (string.IsNullOrEmpty(id))
                continue;
            byte[] data;
            try
            {
                data = Convert.FromBase64String(binary.Value.Trim());
            }
            catch (FormatException)
            {
                continue;
            }
            var extension = ((string?)binary.Attribute("content-type"))?.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/gif" => "gif",
                "image/jpeg" or "image/jpg" => "jpg",
                _ => data is [0x89, (byte)'P', ..] ? "png" : data is [(byte)'G', (byte)'I', (byte)'F', ..] ? "gif" : "jpg",
            };
            var file = Path.Combine(folder, $"binary{_binaries.Count + 1:0000}.{extension}");
            File.WriteAllBytes(file, data);
            _binaries[id] = file;
        }
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void Body(XElement body, List<HBlock> output)
    {
        foreach (var element in body.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "title":
                    Title(element, output, 1, center: true);
                    break;
                case "epigraph":
                    Epigraph(element, output);
                    break;
                case "image":
                    Image(element, output, 0);
                    break;
                case "section":
                    Section(element, output, 1);
                    break;
            }
        }
    }

    private void Section(XElement section, List<HBlock> output, int depth)
    {
        var start = output.Count;
        foreach (var element in section.Elements())
            Block(element, output, depth, 0);
        if (output.Count == start)
            return;
        // Every top-level section (a chapter) starts on a new page; bookmarks let links reach the section.
        if (output[start] is not HParagraph first)
            output.Insert(start, first = new HParagraph());
        first.PageBreakBefore = depth == 1 && start > 0;
        if ((string?)section.Attribute("id") is { Length: > 0 } id)
            first.Inlines.Insert(0, new HBookmark(id));
    }

    private void Block(XElement element, List<HBlock> output, int depth, int indent)
    {
        switch (element.Name.LocalName)
        {
            case "title":
                Title(element, output, depth, center: false);
                break;
            case "section":
                Section(element, output, depth + 1);
                break;
            case "p":
                output.Add(Paragraph(element, new HParaFormat(Left: indent > 0 ? indent : null), default));
                break;
            case "subtitle":
                output.Add(Paragraph(element, new HParaFormat(Align: HAlign.Center), new HCharFormat(Bold: true)));
                break;
            case "empty-line":
                output.Add(new HParagraph());
                break;
            case "epigraph":
                Epigraph(element, output);
                break;
            case "annotation" or "cite":
                foreach (var child in element.Elements())
                    Block(child, output, depth, indent + Indent);
                break;
            case "text-author":
                output.Add(Paragraph(element, new HParaFormat(Align: HAlign.Right, Left: indent > 0 ? indent : null), new HCharFormat(Italic: true)));
                break;
            case "poem":
                Poem(element, output, depth, indent + Indent);
                break;
            case "image":
                Image(element, output, indent);
                break;
            case "table":
                output.Add(Table(element));
                break;
        }
    }

    private void Title(XElement title, List<HBlock> output, int depth, bool center)
    {
        // A title may have several paragraphs; they form one heading, one line each.
        var heading = new HParagraph
        {
            HeadingLevel = Math.Clamp(depth, 1, 6),
            Format = center ? new HParaFormat(Align: HAlign.Center) : default,
        };
        foreach (var paragraph in Children(title, "p"))
        {
            if (heading.Inlines.Count > 0)
                heading.Inlines.Add(new HLineBreak(default));
            heading.Inlines.AddRange(Line(paragraph, default));
        }
        if (heading.Inlines.Count > 0)
            output.Add(heading);
    }

    private void Epigraph(XElement epigraph, List<HBlock> output)
    {
        foreach (var child in epigraph.Elements())
        {
            if (child.Name.LocalName == "p")
                output.Add(Paragraph(child, new HParaFormat(Align: HAlign.Right), new HCharFormat(Italic: true)));
            else
                Block(child, output, 0, Indent);
        }
    }

    private void Poem(XElement poem, List<HBlock> output, int depth, int indent)
    {
        foreach (var child in poem.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "stanza":
                    {
                        // One paragraph per stanza, one line per verse.
                        var stanza = new HParagraph { Format = new HParaFormat(Left: indent) };
                        foreach (var line in child.Elements())
                        {
                            if (line.Name.LocalName is "title" or "subtitle")
                            {
                                output.Add(Paragraph(line.Name.LocalName == "title" ? Child(line, "p") ?? line : line, new HParaFormat(Left: indent), new HCharFormat(Bold: true)));
                                continue;
                            }
                            if (stanza.Inlines.Count > 0)
                                stanza.Inlines.Add(new HLineBreak(default));
                            stanza.Inlines.AddRange(Line(line, default));
                        }
                        if (stanza.Inlines.Count > 0)
                            output.Add(stanza);
                        break;
                    }
                case "title":
                    Title(child, output, depth + 1, center: false);
                    break;
                default:
                    Block(child, output, depth, indent);
                    break;
            }
        }
    }

    private void Image(XElement image, List<HBlock> output, int indent)
    {
        if (Href(image) is not { } href || !_binaries.TryGetValue(href.TrimStart('#'), out var file))
            return;
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Center, Left: indent > 0 ? indent : null) };
        paragraph.Inlines.Add(new HImage(file));
        output.Add(paragraph);
    }

    private HTable Table(XElement table)
    {
        var hTable = new HTable();
        foreach (var row in Children(table, "tr"))
        {
            var cells = row.Elements().Where(c => c.Name.LocalName is "td" or "th").ToList();
            var hRow = new HRow { Header = cells.Count > 0 && cells.All(c => c.Name.LocalName == "th") };
            foreach (var cell in cells)
            {
                var hCell = new HCell
                {
                    ColSpan = Math.Max(1, (int?)cell.Attribute("colspan") ?? 1),
                    RowSpan = Math.Max(1, (int?)cell.Attribute("rowspan") ?? 1),
                };
                var align = ((string?)cell.Attribute("align"))?.ToLowerInvariant() switch
                {
                    "center" => HAlign.Center,
                    "right" => HAlign.Right,
                    _ => (HAlign?)null,
                };
                hCell.Blocks.Add(Paragraph(cell, new HParaFormat(Align: align), cell.Name.LocalName == "th" ? new HCharFormat(Bold: true) : default));
                hRow.Cells.Add(hCell);
            }
            if (hRow.Cells.Count > 0)
                hTable.Rows.Add(hRow);
        }
        hTable.ColumnCount = Math.Max(1, hTable.Rows.Select(r => r.Cells.Sum(c => c.ColSpan)).DefaultIfEmpty(1).Max());
        return hTable;
    }

    // ───────────────────────── Inlines ─────────────────────────

    private HParagraph Paragraph(XElement element, HParaFormat format, HCharFormat charFormat)
    {
        var paragraph = new HParagraph { Format = format };
        if ((string?)element.Attribute("id") is { Length: > 0 } id)
            paragraph.Inlines.Add(new HBookmark(id));
        Inlines(element, paragraph.Inlines, charFormat);
        HtmlReader.MergeRuns(paragraph.Inlines);
        TrimEdges(paragraph.Inlines);
        return paragraph;
    }

    /// <summary>The inlines of one line (a title paragraph, a verse), edges trimmed.</summary>
    private List<HInline> Line(XElement element, HCharFormat format)
    {
        var inlines = new List<HInline>();
        Inlines(element, inlines, format);
        HtmlReader.MergeRuns(inlines);
        TrimEdges(inlines);
        return inlines;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is not (' ' or '\n' or '\r' or '\t'))
                sb.Append(ch);
            else if (sb.Length == 0 || sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>Pretty-printed files indent paragraph text: spaces at the paragraph edges are dropped.</summary>
    private static void TrimEdges(List<HInline> inlines)
    {
        if (inlines.FindIndex(i => i is HText) is var first and >= 0 && inlines[first] is HText head)
            inlines[first] = new HText(head.Text.TrimStart(), head.Format);
        if (inlines.Count > 0 && inlines[^1] is HText tail)
            inlines[^1] = new HText(tail.Text.TrimEnd(), tail.Format);
        inlines.RemoveAll(i => i is HText { Text.Length: 0 });
    }

    private void Inlines(XElement element, List<HInline> output, HCharFormat format)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                var value = CollapseWhitespace(text.Value);
                if (value.Length > 0)
                    output.Add(new HText(value, format));
                continue;
            }
            if (node is not XElement child)
                continue;
            switch (child.Name.LocalName)
            {
                case "strong":
                    Inlines(child, output, format with { Bold = true });
                    break;
                case "emphasis":
                    Inlines(child, output, format with { Italic = true });
                    break;
                case "strikethrough":
                    Inlines(child, output, format with { Strike = true });
                    break;
                case "sub":
                    Inlines(child, output, format with { Subscript = true });
                    break;
                case "sup":
                    Inlines(child, output, format with { Superscript = true });
                    break;
                case "code":
                    Inlines(child, output, format with { Shade = HtmlReader.CodeShade });
                    break;
                case "image":
                    if (Href(child) is { } href && _binaries.TryGetValue(href.TrimStart('#'), out var file))
                        output.Add(new HImage(file));
                    break;
                case "a":
                    Link(child, output, format);
                    break;
                default:
                    Inlines(child, output, format); // style and unknown inline elements: their text
                    break;
            }
        }
    }

    private void Link(XElement link, List<HInline> output, HCharFormat format)
    {
        var href = Href(link) ?? "";
        if (href.StartsWith('#') && _notes.TryGetValue(href[1..], out var noteSection))
        {
            // A note reference becomes a footnote with the note's text (its title is only the number).
            var note = new HNote(endnote: false);
            foreach (var child in noteSection.Elements().Where(e => e.Name.LocalName != "title"))
                Block(child, note.Blocks, 0, 0);
            if (note.Blocks.Count > 0)
            {
                output.Add(note);
                return;
            }
        }
        if (href.Length == 0)
        {
            Inlines(link, output, format);
            return;
        }
        var hLink = new HLink(href);
        Inlines(link, hLink.Content, format);
        output.Add(hLink);
    }
}
