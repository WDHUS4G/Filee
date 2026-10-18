// DOCX (Office Open XML) → HDocument, read directly instead of through Pandoc so the layout survives:
// page size and margins per section, columns, headers and footers with page numbers, text boxes, floating
// pictures, paragraph and character formatting, tables with borders, shading and merged cells, lists,
// footnotes and endnotes, bookmarks and hyperlinks (including the links of a Word table of contents).
//
// Split over partial files: this one walks the package and the text, DocxReader.Styles resolves formatting,
// DocxReader.Tables and DocxReader.Drawing handle tables and graphic objects.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

/// <summary>Reads a .docx file into the HWPX document model.</summary>
internal sealed partial class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private readonly ZipArchive _zip;
    private readonly string _mediaFolder;
    private readonly Dictionary<string, Part?> _parts = [];
    private readonly Dictionary<string, string> _media = [];
    private readonly Dictionary<string, XElement> _footnotes = [];
    private readonly Dictionary<string, XElement> _endnotes = [];
    private readonly List<FieldState> _fields = [];
    private Part? _footnotesPart;
    private Part? _endnotesPart;
    private bool _evenAndOddHeaders;

    /// <summary>Line pitch (twips) of each section's document grid, 0 = none; see <see cref="SnapToGrid"/>.</summary>
    private readonly List<int> _linePitches = [];
    private int _sectionIndex;
    private int _textBoxDepth;

    /// <summary>The part being read; relationship ids (images, links) are resolved against it.</summary>
    private Part _part;

    private DocxReader(ZipArchive zip, string mediaFolder)
    {
        _zip = zip;
        _mediaFolder = mediaFolder;
        var mainPath = RelationshipsOf("")
            .FirstOrDefault(r => r.Value.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Value.Target ?? "word/document.xml";
        _part = LoadPart(mainPath) ?? throw new InvalidDataException("The file is not a Word document (no word/document.xml).");
    }

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder)
    {
        using var zip = ZipFile.OpenRead(path);
        return new DocxReader(zip, mediaFolder).ReadDocument();
    }

    // ───────────────────────── Package ─────────────────────────

    private sealed class Part(string path, XDocument xml, Dictionary<string, Relationship> relationships)
    {
        public string Path { get; } = path;
        public XDocument Xml { get; } = xml;
        public Dictionary<string, Relationship> Relationships { get; } = relationships;
    }

    private readonly record struct Relationship(string Type, string Target, bool External);

    private Part? LoadPart(string path)
    {
        if (_parts.TryGetValue(path, out var cached))
            return cached;
        Part? part = null;
        if (_zip.GetEntry(path) is { } entry)
        {
            using var stream = entry.Open();
            // Whitespace matters: <w:t xml:space="preserve"> </w:t> is a real space.
            part = new Part(path, XDocument.Load(stream, LoadOptions.PreserveWhitespace), RelationshipsOf(path));
        }
        _parts[path] = part;
        return part;
    }

    /// <summary>Relationships of a part ("" = the package), with targets resolved to zip paths.</summary>
    private Dictionary<string, Relationship> RelationshipsOf(string partPath)
    {
        var folder = partPath.Contains('/') ? partPath[..partPath.LastIndexOf('/')] : "";
        var relsPath = partPath.Length == 0 ? "_rels/.rels" : $"{folder}/_rels/{partPath[(partPath.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, Relationship>();
        if (_zip.GetEntry(relsPath) is not { } entry)
            return result;
        using var stream = entry.Open();
        foreach (var rel in XDocument.Load(stream).Root?.Elements(PackageRelationships + "Relationship") ?? [])
        {
            var id = (string?)rel.Attribute("Id") ?? "";
            var target = (string?)rel.Attribute("Target") ?? "";
            var external = (string?)rel.Attribute("TargetMode") == "External";
            result[id] = new Relationship((string?)rel.Attribute("Type") ?? "", external ? target : ResolvePath(folder, target), external);
        }
        return result;
    }

    private static string ResolvePath(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in Uri.UnescapeDataString(target).Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                parts.Add(segment);
            }
        }
        return string.Join('/', parts);
    }

    private Part? RelatedPart(Part from, string typeSuffix) =>
        from.Relationships.Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)) is { Target.Length: > 0 } rel
            ? LoadPart(rel.Target)
            : null;

    /// <summary>Runs <paramref name="read"/> with relationship ids resolved against <paramref name="part"/>.</summary>
    private T WithPart<T>(Part part, Func<T> read)
    {
        var previous = _part;
        _part = part;
        try
        {
            return read();
        }
        finally
        {
            _part = previous;
        }
    }

    // ───────────────────────── Document and sections ─────────────────────────

    private HDocument ReadDocument()
    {
        var main = _part;
        LoadStyles(RelatedPart(main, "/styles"));
        var theme = RelatedPart(main, "/theme");
        LoadTheme(theme);
        LoadThemeColors(theme);
        LoadNumbering(RelatedPart(main, "/numbering"));
        _evenAndOddHeaders = RelatedPart(main, "/settings")?.Xml.Root?.Element(W + "evenAndOddHeaders") is { } evenOdd && Val(evenOdd) is not ("0" or "false");
        _footnotesPart = RelatedPart(main, "/footnotes");
        _endnotesPart = RelatedPart(main, "/endnotes");
        foreach (var note in _footnotesPart?.Xml.Root?.Elements(W + "footnote") ?? [])
            _footnotes[Attr(note, "id") ?? ""] = note;
        foreach (var note in _endnotesPart?.Xml.Root?.Elements(W + "endnote") ?? [])
            _endnotes[Attr(note, "id") ?? ""] = note;

        var document = new HDocument { Title = Title() };
        var body = main.Xml.Root?.Element(W + "body") ?? throw new InvalidDataException("word/document.xml has no body.");

        // A section ends with the paragraph whose properties hold its sectPr; the last one is the body's own sectPr.
        // Paragraphs come before their sectPr, so collect the document grids first.
        foreach (var element in Flatten(body.Elements()))
        {
            if (element.Name == W + "p" && element.Element(W + "pPr")?.Element(W + "sectPr") is { } sectionEnd)
                _linePitches.Add(LinePitch(sectionEnd));
        }
        _linePitches.Add(LinePitch(body.Element(W + "sectPr")));

        var blocks = new List<HBlock>();
        var headers = new Dictionary<string, List<HBlock>>();
        var footers = new Dictionary<string, List<HBlock>>();
        foreach (var element in Flatten(body.Elements()))
        {
            if (element.Name == W + "sectPr")
                continue;
            ReadBlock(element, blocks);
            if (element.Name == W + "p" && element.Element(W + "pPr")?.Element(W + "sectPr") is { } sectPr)
            {
                AddSection(document, blocks, sectPr, headers, footers);
                blocks = [];
                _sectionIndex++;
            }
        }
        AddSection(document, blocks, body.Element(W + "sectPr"), headers, footers);
        return document;
    }

    private string? Title()
    {
        var core = _zip.GetEntry("docProps/core.xml");
        if (core is null)
            return null;
        using var stream = core.Open();
        return NullIfEmpty(XDocument.Load(stream).Descendants(Dc + "title").FirstOrDefault()?.Value);
    }

    private void AddSection(HDocument document, List<HBlock> blocks, XElement? sectPr,
        Dictionary<string, List<HBlock>> headers, Dictionary<string, List<HBlock>> footers)
    {
        var page = Page(sectPr);
        var columns = Columns(sectPr);
        var previous = document.Sections.LastOrDefault();

        // Word uses continuous section breaks to change the number of columns mid-page. 한글 does that with a
        // column definition inside the section, so keep one section when the paper does not change.
        if (previous is not null && Val(sectPr?.Element(W + "type")) == "continuous" && previous.Page == page)
        {
            if (blocks.FirstOrDefault() is not HParagraph first)
                blocks.Insert(0, first = new HParagraph());
            first.ColumnsChange = columns;
            previous.Blocks.AddRange(blocks);
            return;
        }

        var section = new HSection { Page = page, Columns = columns };
        section.Blocks.AddRange(blocks);

        // Sections without their own header or footer reference inherit the previous section's.
        foreach (var reference in sectPr?.Elements(W + "headerReference") ?? [])
            headers[Attr(reference, "type") ?? "default"] = HeaderFooterBlocks(reference);
        foreach (var reference in sectPr?.Elements(W + "footerReference") ?? [])
            footers[Attr(reference, "type") ?? "default"] = HeaderFooterBlocks(reference);

        void Add(Dictionary<string, List<HBlock>> source, List<HHeaderFooter> target)
        {
            if (_evenAndOddHeaders)
            {
                if (source.GetValueOrDefault("default") is { } odd)
                    AddHeaderFooter(target, HPageType.Odd, odd);
                if (source.GetValueOrDefault("even") is { } even)
                    AddHeaderFooter(target, HPageType.Even, even);
            }
            else if (source.GetValueOrDefault("default") is { } both)
            {
                AddHeaderFooter(target, HPageType.Both, both);
            }
        }
        Add(headers, section.Headers);
        Add(footers, section.Footers);

        // "Different first page": 한글 can only hide the header/footer on the first page, which is what an empty
        // (or missing) first-page header means in Word.
        if (sectPr?.Element(W + "titlePg") is { } titlePg && Val(titlePg) is not ("0" or "false"))
        {
            section.HideFirstHeader = IsBlank(headers.GetValueOrDefault("first"));
            section.HideFirstFooter = IsBlank(footers.GetValueOrDefault("first"));
        }
        if (int.TryParse(Attr(sectPr?.Element(W + "pgNumType"), "start"), out var start))
            section.StartPageNumber = start;

        document.Sections.Add(section);
    }

    private static void AddHeaderFooter(List<HHeaderFooter> target, HPageType pages, List<HBlock> blocks)
    {
        var item = new HHeaderFooter(pages);
        item.Blocks.AddRange(blocks);
        target.Add(item);
    }

    private List<HBlock> HeaderFooterBlocks(XElement reference)
    {
        var blocks = new List<HBlock>();
        if (Attr(reference, "id", R) is { } id && _part.Relationships.TryGetValue(id, out var rel) && LoadPart(rel.Target) is { } part)
        {
            WithPart(part, () =>
            {
                foreach (var element in Flatten(part.Xml.Root?.Elements() ?? []))
                    ReadBlock(element, blocks);
                return 0;
            });
        }
        return blocks;
    }

    private static bool IsBlank(List<HBlock>? blocks) =>
        blocks is null || blocks.All(b => b is HParagraph p && p.Inlines.All(i => i is HText t && string.IsNullOrWhiteSpace(t.Text) || i is HBookmark));

    private static HPage Page(XElement? sectPr)
    {
        var size = sectPr?.Element(W + "pgSz");
        var margin = sectPr?.Element(W + "pgMar");
        int Get(XElement? element, string name, int fallback) => Math.Abs(Twips(element, name) ?? fallback);

        var width = Get(size, "w", 11906);
        var height = Get(size, "h", 16838);
        if (Attr(size, "orient") == "landscape" && width < height)
            (width, height) = (height, width);

        // Word: top = paper edge → body, header = paper edge → header text.
        // 한글: top = paper edge → header, header = header area height (body starts at top + header).
        var top = Get(margin, "top", 1440);
        var bottom = Get(margin, "bottom", 1440);
        var headerDistance = Math.Min(Get(margin, "header", 720), top);
        var footerDistance = Math.Min(Get(margin, "footer", 720), bottom);
        return new HPage(
            Width: HwpxUnits.FromTwips(width),
            Height: HwpxUnits.FromTwips(height),
            Left: HwpxUnits.FromTwips(Get(margin, "left", 1440)),
            Right: HwpxUnits.FromTwips(Get(margin, "right", 1440)),
            Top: HwpxUnits.FromTwips(headerDistance),
            Bottom: HwpxUnits.FromTwips(footerDistance),
            Header: HwpxUnits.FromTwips(top - headerDistance),
            Footer: HwpxUnits.FromTwips(bottom - footerDistance),
            Gutter: HwpxUnits.FromTwips(Get(margin, "gutter", 0)));
    }

    /// <summary>Line pitch of a "lines" document grid (twips), or 0.</summary>
    private static int LinePitch(XElement? sectPr)
    {
        var grid = sectPr?.Element(W + "docGrid");
        return Attr(grid, "type") is "lines" or "linesAndChars" or "snapToChars" ? Twips(grid, "linePitch") ?? 0 : 0;
    }

    /// <summary>
    /// Word's document grid ("lines") puts every line on a multiple of the grid pitch: a single-spaced 10 pt line
    /// with an 18 pt grid is 18 pt high. 한글 has no such grid for body text, so the paragraph gets an "at least"
    /// line spacing of the snapped height (larger content such as pictures can still grow the line).
    /// </summary>
    private void SnapToGrid(HParagraph paragraph, ParaProps props)
    {
        var pitch = _textBoxDepth > 0 || _sectionIndex >= _linePitches.Count ? 0 : _linePitches[_sectionIndex];
        if (pitch <= 0 || props.SnapToGrid == false || props.LineRule is "exact" or "atLeast")
            return;
        var size = MaxFontSize(paragraph.Inlines) ?? paragraph.MarkFormat.Size ?? 1000;
        var cell = HwpxUnits.FromTwips(pitch);
        var natural = size * WordLineToHwpPercent / 100.0; // 1/100 pt = HWPUNIT
        var lines = Math.Max(1, Math.Ceiling(natural / cell));
        var multiple = (props.Line ?? 240) / 240.0;
        paragraph.Format = paragraph.Format with { LineSpacing = new HLineSpacing(HLineSpacingKind.AtLeast, (int)Math.Round(lines * cell * multiple)) };
    }

    private static int? MaxFontSize(IEnumerable<HInline> inlines)
    {
        int? max = null;
        foreach (var inline in inlines)
        {
            var size = inline switch
            {
                HText text => text.Format.Size,
                HLink link => MaxFontSize(link.Content),
                HField field => field.Format.Size,
                _ => null,
            };
            if (size > (max ?? 0))
                max = size;
        }
        return max;
    }

    private static HColumns Columns(XElement? sectPr)
    {
        var cols = sectPr?.Element(W + "cols");
        var count = int.TryParse(Attr(cols, "num"), out var n) ? n : cols?.Elements(W + "col").Count() ?? 1;
        if (count <= 1)
            return HColumns.Single;
        return new HColumns(Math.Min(count, 255), HwpxUnits.FromTwips(Twips(cols, "space") ?? 720), Bool(cols, "sep") == true);
    }

    // ───────────────────────── Blocks ─────────────────────────

    /// <summary>Paragraphs and tables, looking through content controls, custom XML and tracked insertions.</summary>
    private static IEnumerable<XElement> Flatten(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            var name = element.Name;
            if (name == W + "sdt")
            {
                foreach (var inner in Flatten(element.Element(W + "sdtContent")?.Elements() ?? []))
                    yield return inner;
            }
            else if (name == W + "customXml" || name == W + "ins" || name == W + "moveTo")
            {
                foreach (var inner in Flatten(element.Elements()))
                    yield return inner;
            }
            else if (name == W + "p" || name == W + "tbl" || name == W + "sectPr")
            {
                yield return element;
            }
        }
    }

    private void ReadBlocks(IEnumerable<XElement> elements, List<HBlock> output)
    {
        foreach (var element in Flatten(elements))
            ReadBlock(element, output);
    }

    private void ReadBlock(XElement element, List<HBlock> output)
    {
        if (element.Name == W + "p")
            ReadParagraph(element, output);
        else if (element.Name == W + "tbl")
            output.Add(ReadTable(element));
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>
    /// Builds the paragraph(s) for one w:p. A page or column break inside the text starts a new 한글 paragraph
    /// (breaks are paragraph attributes there), so one Word paragraph can become several.
    /// </summary>
    private sealed class ParagraphBuilder
    {
        private readonly DocxReader _reader;
        private readonly List<HBlock> _output;
        private readonly HParaFormat _format;
        private readonly HCharFormat _mark;
        private readonly int _heading;
        private readonly HListRef? _list;
        private readonly ParaProps _props;
        private readonly Stack<List<HInline>> _sinks = new();

        public ParagraphBuilder(DocxReader reader, List<HBlock> output, ParaProps props, RunProps mark, XElement? pPr)
        {
            _reader = reader;
            _props = props;
            _output = output;
            _format = reader.ParaFormat(props);
            _mark = reader.CharFormat(mark);
            _heading = reader.HeadingLevel(props, pPr);
            _list = reader.Numbering(props.NumId) is { } numbering ? new HListRef(numbering, Math.Clamp(props.Ilvl ?? 0, 0, 8), Numbered: true) : null;
            Current = NewParagraph(_list);
            Current.PageBreakBefore = props.PageBreakBefore == true;

            // A hyperlink field that started in an earlier paragraph (Word's table of contents) continues here.
            if (reader._fields.LastOrDefault(f => f.Result && f.Target is not null) is { } field)
                OpenLink(field.Target!);
        }

        public HParagraph Current { get; private set; }

        public List<HInline> Sink => _sinks.Count > 0 ? _sinks.Peek() : Current.Inlines;

        public void Add(HInline inline) => Sink.Add(inline);

        public void OpenLink(string target)
        {
            var link = new HLink(target);
            Sink.Add(link);
            _sinks.Push(link.Content);
        }

        public void CloseLink()
        {
            if (_sinks.Count > 0)
                _sinks.Pop();
        }

        /// <summary>Continues in a new paragraph that starts on a new page or column.</summary>
        public void Break(bool page)
        {
            if (Current.Inlines.Count > 0)
            {
                Emit();
                // The rest of a list item is not numbered again.
                Current = NewParagraph(_list is null ? null : _list with { Numbered = false });
                _sinks.Clear();
            }
            if (page)
                Current.PageBreakBefore = true;
            else
                Current.ColumnBreakBefore = true;
        }

        public void Finish() => Emit();

        private void Emit()
        {
            _reader.SnapToGrid(Current, _props);
            _output.Add(Current);
        }

        private HParagraph NewParagraph(HListRef? list) =>
            new() { Format = _format, MarkFormat = _mark, HeadingLevel = _heading, List = list };
    }

    private void ReadParagraph(XElement p, List<HBlock> output)
    {
        var pPr = p.Element(W + "pPr");
        var (props, mark) = ParagraphProperties(pPr);
        var builder = new ParagraphBuilder(this, output, props, mark, pPr);
        // Run formatting starts from the paragraph style, not from the paragraph mark's direct formatting.
        var styleRun = ResolveStyle(Val(pPr?.Element(W + "pStyle")) ?? _defaultParagraphStyle).Run;
        ParagraphContent(p.Elements(), builder, styleRun);
        builder.Finish();
    }

    private void ParagraphContent(IEnumerable<XElement> elements, ParagraphBuilder builder, RunProps styleRun)
    {
        foreach (var element in elements)
        {
            var name = element.Name.LocalName;
            if (element.Name.Namespace == M)
            {
                if (name is "oMath" or "oMathPara")
                    builder.Add(new HText(string.Concat(element.Descendants(M + "t").Select(t => t.Value)), CharFormat(styleRun) with { Italic = true }));
                continue;
            }
            if (element.Name.Namespace != W)
            {
                if (element.Name == Mc + "AlternateContent")
                    ParagraphContent(ChooseAlternative(element), builder, styleRun);
                continue;
            }
            switch (name)
            {
                case "r":
                    Run(element, builder, styleRun);
                    break;
                case "hyperlink":
                    {
                        var target = Attr(element, "id", R) is { } id && _part.Relationships.TryGetValue(id, out var rel) ? rel.Target
                            : Attr(element, "anchor") is { } anchor ? "#" + anchor
                            : null;
                        if (target is not null)
                            builder.OpenLink(target);
                        ParagraphContent(element.Elements(), builder, styleRun);
                        if (target is not null)
                            builder.CloseLink();
                        break;
                    }
                case "fldSimple":
                    {
                        var field = new FieldState();
                        field.Instruction.Append(Attr(element, "instr"));
                        _fields.Add(field);
                        StartFieldResult(field, builder, CharFormat(styleRun));
                        ParagraphContent(element.Elements(), builder, styleRun);
                        EndField(builder);
                        break;
                    }
                case "bookmarkStart":
                    if (Attr(element, "name") is { Length: > 0 } bookmark && bookmark != "_GoBack")
                        builder.Add(new HBookmark(bookmark));
                    break;
                case "ins" or "moveTo" or "smartTag" or "customXml" or "dir" or "bdo":
                    ParagraphContent(element.Elements(), builder, styleRun);
                    break;
                case "sdt":
                    ParagraphContent(element.Element(W + "sdtContent")?.Elements() ?? [], builder, styleRun);
                    break;
                default:
                    break; // pPr, del, moveFrom, proofErr, permStart, commentRangeStart ...: no visible content
            }
        }
    }

    private void Run(XElement r, ParagraphBuilder builder, RunProps styleRun)
    {
        var props = RunProperties(r.Element(W + "rPr"), styleRun);
        if (props.Hidden == true)
            return;
        var format = CharFormat(props);

        foreach (var child in r.Elements())
        {
            if (child.Name == Mc + "AlternateContent")
            {
                foreach (var chosen in ChooseAlternative(child))
                    RunChild(chosen, builder, format);
                continue;
            }
            RunChild(child, builder, format);
        }
    }

    private void RunChild(XElement child, ParagraphBuilder builder, HCharFormat format)
    {
        if (child.Name.Namespace != W)
            return;
        switch (child.Name.LocalName)
        {
            case "t":
                if (ShowsText())
                    builder.Add(new HText(child.Value, format));
                break;
            case "tab" or "ptab":
                if (ShowsText())
                    builder.Add(new HTab(format));
                break;
            case "br":
                if (!ShowsText())
                    break;
                switch (Attr(child, "type"))
                {
                    case "page":
                        builder.Break(page: true);
                        break;
                    case "column":
                        builder.Break(page: false);
                        break;
                    default:
                        builder.Add(new HLineBreak(format));
                        break;
                }
                break;
            case "cr":
                if (ShowsText())
                    builder.Add(new HLineBreak(format));
                break;
            case "noBreakHyphen":
                if (ShowsText())
                    builder.Add(new HText("-", format));
                break;
            case "sym":
                if (ShowsText() && int.TryParse(Attr(child, "char"), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    builder.Add(new HText(BulletText(((char)code).ToString()), format));
                break;
            case "fldChar":
                FieldChar(child, builder, format);
                break;
            case "instrText":
                if (_fields.LastOrDefault() is { Result: false } field)
                    field.Instruction.Append(child.Value);
                break;
            case "drawing":
                if (ShowsText())
                {
                    foreach (var inline in Drawing(child))
                        builder.Add(inline);
                }
                break;
            case "pict" or "object":
                if (ShowsText())
                {
                    foreach (var inline in Vml(child))
                        builder.Add(inline);
                }
                break;
            case "footnoteReference":
                if (Note(child, _footnotes, _footnotesPart, endnote: false) is { } footnote)
                    builder.Add(footnote);
                break;
            case "endnoteReference":
                if (Note(child, _endnotes, _endnotesPart, endnote: true) is { } endnote)
                    builder.Add(endnote);
                break;
            default:
                break; // footnoteRef (the number, 한글 adds its own), separator, lastRenderedPageBreak, delText ...
        }
    }

    private HNote? Note(XElement reference, Dictionary<string, XElement> notes, Part? part, bool endnote)
    {
        if (part is null || !notes.TryGetValue(Attr(reference, "id") ?? "", out var element))
            return null;
        var note = new HNote(endnote);
        WithPart(part, () =>
        {
            ReadBlocks(element.Elements(), note.Blocks);
            return 0;
        });
        // Word notes start with the reference mark and a space; 한글 writes its own number.
        if (note.Blocks.FirstOrDefault() is HParagraph first && first.Inlines.FirstOrDefault() is HText { Text: var text } leading)
        {
            var trimmed = text.TrimStart();
            first.Inlines[0] = new HText(trimmed, leading.Format);
        }
        return note;
    }

    /// <summary>mc:AlternateContent: the Choice when it is DrawingML we understand, else the (VML) Fallback.</summary>
    private static IEnumerable<XElement> ChooseAlternative(XElement alternate)
    {
        var choice = alternate.Elements(Mc + "Choice").FirstOrDefault(c => (string?)c.Attribute("Requires") is "wps" or "wpg" or "wp14" or "w14" or "a14" or "wpc");
        return (choice ?? alternate.Element(Mc + "Fallback") ?? alternate.Elements(Mc + "Choice").FirstOrDefault())?.Elements() ?? [];
    }

    // ───────────────────────── Fields ─────────────────────────

    /// <summary>A complex field (w:fldChar begin … separate … end) that may span runs and paragraphs.</summary>
    private sealed class FieldState
    {
        public readonly StringBuilder Instruction = new();
        public bool Result;
        public bool Suppress;
        public string? Target;
    }

    /// <summary>Field result text is shown unless we are inside an instruction or a field we replace (PAGE ...).</summary>
    private bool ShowsText() => _fields.All(f => f.Result && !f.Suppress);

    private void FieldChar(XElement fldChar, ParagraphBuilder builder, HCharFormat format)
    {
        switch (Attr(fldChar, "fldCharType"))
        {
            case "begin":
                _fields.Add(new FieldState());
                break;
            case "separate":
                if (_fields.LastOrDefault() is { Result: false } field)
                    StartFieldResult(field, builder, format);
                break;
            case "end":
                if (_fields.LastOrDefault() is { Result: false } withoutResult)
                    StartFieldResult(withoutResult, builder, format);
                EndField(builder);
                break;
        }
    }

    private void StartFieldResult(FieldState field, ParagraphBuilder builder, HCharFormat format)
    {
        field.Result = true;
        var words = SplitInstruction(field.Instruction.ToString());
        switch (words.FirstOrDefault()?.ToUpperInvariant())
        {
            case "HYPERLINK":
                {
                    // HYPERLINK "url" [\l "bookmark"] or HYPERLINK \l "bookmark"
                    string? url = null, anchor = null;
                    for (var i = 1; i < words.Count; i++)
                    {
                        if (words[i] == "\\l" && i + 1 < words.Count)
                            anchor = words[++i];
                        else if (!words[i].StartsWith('\\') && url is null)
                            url = words[i];
                        else if (words[i].StartsWith('\\') && i + 1 < words.Count && !words[i + 1].StartsWith('\\'))
                            i++; // other switch with an argument (\o "tooltip", \t "frame")
                    }
                    field.Target = anchor is not null ? (url ?? "") + "#" + anchor : url;
                    if (field.Target is not null)
                        builder.OpenLink(field.Target);
                    break;
                }
            case "PAGE":
                builder.Add(new HField(HFieldKind.PageNumber, format));
                field.Suppress = true;
                break;
            case "NUMPAGES" or "SECTIONPAGES":
                builder.Add(new HField(HFieldKind.TotalPages, format));
                field.Suppress = true;
                break;
        }
    }

    private void EndField(ParagraphBuilder builder)
    {
        if (_fields.Count == 0)
            return;
        var field = _fields[^1];
        _fields.RemoveAt(_fields.Count - 1);
        if (field.Target is not null)
            builder.CloseLink();
    }

    /// <summary>Splits a field instruction into words, keeping quoted arguments together.</summary>
    private static List<string> SplitInstruction(string instruction)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in instruction)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }
}
