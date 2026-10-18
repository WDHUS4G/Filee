// Book → FictionBook 2 (FB2): headings become nested sections with titles, paragraphs keep bold / italic /
// strikethrough / sub / sup / code and links, pictures become base64 binaries (JPEG or PNG), tables stay tables,
// footnotes go to a "notes" body. FB2 has no lists or line breaks: list markers are written out and a line break
// starts a new paragraph.

using System.Text;
using System.Xml;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal sealed class Fb2Writer
{
    private const string Fb2 = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private const string XLink = "http://www.w3.org/1999/xlink";

    private readonly string _imageFolder;
    private readonly Dictionary<string, (string Id, string File, string MediaType)> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<List<HBlock>> _notes = [];
    private readonly ListNumbers _numbers = new();

    private Fb2Writer(string imageFolder) => _imageFolder = imageFolder;

    /// <summary>A section of the output: its title and content (paragraphs, then sub-sections).</summary>
    private sealed class Section(HParagraph? title)
    {
        public HParagraph? Title { get; } = title;
        public int Level { get; init; }
        public List<HBlock> Blocks { get; } = [];
        public List<Section> Children { get; } = [];
    }

    public static void Write(Book book, string outputPath, string sourcePath, string workFolder)
    {
        var writer = new Fb2Writer(EbookFiles.NewFolder(workFolder, "fb2-images"));
        var root = writer.Tree(book.Document);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = " " };
        var temp = outputPath + ".tmp";
        using (var xml = XmlWriter.Create(temp, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("FictionBook", Fb2);
            xml.WriteAttributeString("xmlns", "l", null, XLink);
            writer.Description(xml, book, sourcePath);

            xml.WriteStartElement("body", Fb2);
            foreach (var section in root.Children)
                writer.WriteSection(xml, section);
            if (root.Children.Count == 0)
            {
                xml.WriteStartElement("section", Fb2);
                xml.WriteElementString("empty-line", Fb2, null);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();

            if (writer._notes.Count > 0)
            {
                xml.WriteStartElement("body", Fb2);
                xml.WriteAttributeString("name", "notes");
                for (var i = 0; i < writer._notes.Count; i++)
                {
                    xml.WriteStartElement("section", Fb2);
                    xml.WriteAttributeString("id", $"n{i + 1}");
                    xml.WriteStartElement("title", Fb2);
                    xml.WriteElementString("p", Fb2, $"{i + 1}");
                    xml.WriteEndElement();
                    writer.Blocks(xml, writer._notes[i]);
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }

            foreach (var (id, file, mediaType) in writer._binaries.Values)
            {
                xml.WriteStartElement("binary", Fb2);
                xml.WriteAttributeString("id", id);
                xml.WriteAttributeString("content-type", mediaType);
                xml.WriteString(Convert.ToBase64String(File.ReadAllBytes(file), Base64FormattingOptions.InsertLineBreaks));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    /// <summary>Nests the blocks into sections by heading level (a level-2 heading opens a section in the level-1 one).</summary>
    private Section Tree(HDocument document)
    {
        var root = new Section(null) { Level = 0 };
        var stack = new List<Section> { root };
        foreach (var block in document.Sections.SelectMany(s => s.Blocks))
        {
            if (block is HParagraph { HeadingLevel: > 0 } heading)
            {
                while (stack[^1].Level >= heading.HeadingLevel)
                    stack.RemoveAt(stack.Count - 1);
                var section = new Section(heading) { Level = heading.HeadingLevel };
                stack[^1].Children.Add(section);
                stack.Add(section);
                continue;
            }
            // Content before the first heading (or between a heading and its first sub-heading) of the root goes
            // into an untitled section.
            if (stack.Count == 1)
            {
                var untitled = new Section(null) { Level = 1 };
                root.Children.Add(untitled);
                stack.Add(untitled);
            }
            stack[^1].Blocks.Add(block);
        }
        return root;
    }

    private void Description(XmlWriter xml, Book book, string sourcePath)
    {
        xml.WriteStartElement("description", Fb2);
        xml.WriteStartElement("title-info", Fb2);
        xml.WriteElementString("genre", Fb2, "antique"); // required; calibre uses the same neutral default
        var authors = book.Metadata.Authors.Count > 0 ? book.Metadata.Authors : [""];
        foreach (var author in authors)
        {
            xml.WriteStartElement("author", Fb2);
            var parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                xml.WriteElementString("first-name", Fb2, string.Join(" ", parts[..^1]));
                xml.WriteElementString("last-name", Fb2, parts[^1]);
            }
            else
            {
                xml.WriteElementString("nickname", Fb2, parts.Length == 1 ? parts[0] : "Unknown");
            }
            xml.WriteEndElement();
        }
        xml.WriteElementString("book-title", Fb2, book.DisplayTitle(sourcePath));
        if (book.Metadata.Description is { } description)
        {
            xml.WriteStartElement("annotation", Fb2);
            xml.WriteElementString("p", Fb2, Clean(description));
            xml.WriteEndElement();
        }
        xml.WriteElementString("lang", Fb2, book.DisplayLanguage().Split('-')[0]);
        if (book.Metadata.CoverImage is { } cover && Binary(cover) is { } coverId)
        {
            xml.WriteStartElement("coverpage", Fb2);
            ImageElement(xml, coverId);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();

        xml.WriteStartElement("document-info", Fb2);
        xml.WriteStartElement("author", Fb2);
        xml.WriteElementString("nickname", Fb2, "Filee");
        xml.WriteEndElement();
        xml.WriteElementString("program-used", Fb2, "Filee");
        xml.WriteStartElement("date", Fb2);
        xml.WriteAttributeString("value", DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        xml.WriteString(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        xml.WriteEndElement();
        xml.WriteElementString("id", Fb2, Guid.NewGuid().ToString());
        xml.WriteElementString("version", Fb2, "1.0");
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private void WriteSection(XmlWriter xml, Section section)
    {
        xml.WriteStartElement("section", Fb2);
        if (section.Title is { } title && Bookmark(title.Inlines) is { } id)
            xml.WriteAttributeString("id", id);
        if (section.Title is not null)
        {
            xml.WriteStartElement("title", Fb2);
            Paragraphs(xml, section.Title.Inlines.Where(i => i is not HBookmark).ToList(), "p"); // the id is on the section
            xml.WriteEndElement();
        }
        // FB2 sections hold either content or sub-sections: leading content moves into an untitled section.
        if (section.Children.Count > 0 && section.Blocks.Count > 0)
        {
            xml.WriteStartElement("section", Fb2);
            Blocks(xml, section.Blocks);
            xml.WriteEndElement();
        }
        else if (section.Blocks.Count > 0)
        {
            Blocks(xml, section.Blocks);
        }
        foreach (var child in section.Children)
            WriteSection(xml, child);
        if (section.Blocks.Count == 0 && section.Children.Count == 0)
            xml.WriteElementString("empty-line", Fb2, null);
        xml.WriteEndElement();
    }

    private void Blocks(XmlWriter xml, List<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    Paragraph(xml, paragraph);
                    break;
                case HTable table:
                    Table(xml, table);
                    break;
            }
        }
    }

    private void Paragraph(XmlWriter xml, HParagraph paragraph)
    {
        var visible = HDocumentWalker.InlinesOf(paragraph.Inlines).Any(i => i is HImage or HNote || i is HText { Text: var t } && t.Trim().Length > 0);
        if (!visible)
        {
            if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }))
                xml.WriteElementString("subtitle", Fb2, "* * *");
            else
                xml.WriteElementString("empty-line", Fb2, null);
            return;
        }
        // A picture on its own line is a block image.
        if (paragraph.Inlines.Where(i => i is not HBookmark).ToList() is [HImage only] && Binary(only.Path) is { } imageId)
        {
            ImageElement(xml, imageId);
            return;
        }
        var inlines = paragraph.Inlines;
        if (paragraph.List is { } list)
        {
            var marker = list.Numbered ? _numbers.Next(list) + " " : "";
            inlines = [new HText(new string(' ', list.Level * 4) + marker, default), .. inlines];
        }
        var element = paragraph.Format.Align == HAlign.Center && paragraph.Inlines.OfType<HText>().All(t => t.Format.Bold is true) ? "subtitle" : "p";
        Paragraphs(xml, inlines, element);
    }

    /// <summary>Writes inlines as one element per line (FB2 paragraphs have no line breaks).</summary>
    private void Paragraphs(XmlWriter xml, List<HInline> inlines, string element)
    {
        var lines = new List<List<HInline>> { new() };
        foreach (var inline in inlines)
        {
            if (inline is HLineBreak)
                lines.Add([]);
            else
                lines[^1].Add(inline);
        }
        foreach (var line in lines)
        {
            xml.WriteStartElement(element, Fb2);
            if (Bookmark(line) is { } id)
                xml.WriteAttributeString("id", id);
            Inlines(xml, line);
            xml.WriteEndElement();
        }
    }

    private static string? Bookmark(IEnumerable<HInline> inlines) =>
        inlines.OfType<HBookmark>().Select(b => Id(b.Name)).FirstOrDefault();

    private static string Id(string name)
    {
        var id = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return id.Length > 0 && char.IsAsciiLetter(id[0]) ? id : "id-" + id;
    }

    private void Inlines(XmlWriter xml, List<HInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Run(xml, text.Text, text.Format);
                    break;
                case HTab:
                    xml.WriteString("    ");
                    break;
                case HLink link:
                    {
                        var target = link.Target.StartsWith('#') ? "#" + Id(link.Target[1..]) : link.Target;
                        xml.WriteStartElement("a", Fb2);
                        xml.WriteAttributeString("href", XLink, target);
                        Inlines(xml, link.Content.Where(i => i is not HLineBreak).ToList());
                        xml.WriteEndElement();
                        break;
                    }
                case HImage image:
                    if (Binary(image.Path) is { } id)
                        ImageElement(xml, id);
                    break;
                case HNote note:
                    _notes.Add(note.Blocks);
                    xml.WriteStartElement("a", Fb2);
                    xml.WriteAttributeString("href", XLink, $"#n{_notes.Count}");
                    xml.WriteAttributeString("type", "note");
                    xml.WriteString($"[{_notes.Count}]");
                    xml.WriteEndElement();
                    break;
                case HTextBox box:
                    Inlines(xml, box.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines.Prepend(new HText(" ", default))).Where(i => i is not HLineBreak).ToList());
                    break;
            }
        }
    }

    private static void Run(XmlWriter xml, string text, HCharFormat format)
    {
        var open = 0;
        void Open(string name)
        {
            xml.WriteStartElement(name, Fb2);
            open++;
        }
        if (format.Shade == HtmlReader.CodeShade)
            Open("code");
        if (format.Bold is true)
            Open("strong");
        if (format.Italic is true)
            Open("emphasis");
        if (format.Strike is true)
            Open("strikethrough");
        if (format.Superscript is true)
            Open("sup");
        else if (format.Subscript is true)
            Open("sub");
        xml.WriteString(Clean(text));
        for (; open > 0; open--)
            xml.WriteEndElement();
    }

    private void Table(XmlWriter xml, HTable table)
    {
        xml.WriteStartElement("table", Fb2);
        foreach (var row in table.Rows)
        {
            xml.WriteStartElement("tr", Fb2);
            foreach (var cell in row.Cells)
            {
                xml.WriteStartElement(row.Header ? "th" : "td", Fb2);
                if (cell.ColSpan > 1)
                    xml.WriteAttributeString("colspan", $"{cell.ColSpan}");
                if (cell.RowSpan > 1)
                    xml.WriteAttributeString("rowspan", $"{cell.RowSpan}");
                var paragraphs = cell.Blocks.OfType<HParagraph>().ToList();
                for (var i = 0; i < paragraphs.Count; i++)
                {
                    if (i > 0)
                        xml.WriteString(" ");
                    Inlines(xml, paragraphs[i].Inlines.Where(x => x is not HLineBreak and not HImage).ToList());
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static void ImageElement(XmlWriter xml, string id)
    {
        xml.WriteStartElement("image", Fb2);
        xml.WriteAttributeString("href", XLink, "#" + id);
        xml.WriteEndElement();
    }

    /// <summary>The binary id of a picture (JPEG or PNG, converted when needed), or null when it cannot be read.</summary>
    private string? Binary(string path)
    {
        if (_binaries.TryGetValue(path, out var known))
            return known.Id;
        if (EbookFiles.CommonImage(path, _imageFolder, gifAndSvg: false) is not ({ } file, { } mediaType))
            return null;
        var id = $"img{_binaries.Count + 1}.{(mediaType == "image/png" ? "png" : "jpg")}";
        _binaries[path] = (id, file, mediaType);
        return id;
    }

    /// <summary>Removes characters XML 1.0 does not allow (e.g. vertical tabs from Word).</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                sb.Append(c).Append(text[++i]);
            else if (!char.IsSurrogate(c) && (c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)))
                sb.Append(c);
        }
        return sb.ToString();
    }
}
