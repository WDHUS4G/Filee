// Writes DOCX (WordprocessingML, ECMA-376) from the HDocument model — the counterpart of DocxReader, written by hand
// like HwpxWriter, so anything the readers understand (Markdown, TXT, spreadsheets, slides, PDF, ...) becomes an
// editable Word document without Word, LibreOffice or Pandoc.
//
// Split over partial files:
//  * DocxWriter.cs         package: parts, relationships, content types, styles, settings, numbering, notes;
//  * DocxWriter.Body.cs    sections (page setup, columns, headers/footers), paragraphs, runs and tables;
//  * DocxWriter.Objects.cs pictures (inline and floating), text boxes and shapes (wps), links, bookmarks, fields.
//
// Child elements are written in schema order (Word refuses files where they are not), see the notes in each file.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Docx;

/// <summary>Converts an <see cref="HDocument"/> into a .docx package.</summary>
internal sealed partial class DocxWriter
{
    private const string RelationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string ContentTypeBase = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    /// <summary>Namespaces of every WordprocessingML part (declared on the root, as Word does).</summary>
    private const string Namespaces =
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    /// <summary>Default body font size (1/100 pt), the size of the Normal style.</summary>
    private const int DefaultSize = 1000;

    /// <summary>Picture types Word shows as they are; others are converted to PNG.</summary>
    private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["emf"] = "image/x-emf",
        ["wmf"] = "image/x-wmf",
    };

    private readonly List<Part> _parts = [];
    private readonly Dictionary<string, (string ZipPath, string Extension)> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _footnotes = [];
    private readonly List<string> _endnotes = [];
    private readonly Dictionary<HNumbering, int> _numberings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _bookmarks = [];
    private readonly HashSet<string> _bookmarkNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Part _document = new("word/document.xml", ContentTypeBase + "document.main+xml");
    private Part? _footnotesPart;
    private Part? _endnotesPart;
    private Part _part;
    private int _nextDrawingId = 1;
    private int _nextBookmarkId;
    private bool _evenAndOddHeaders;

    private DocxWriter() => _part = _document;

    // ───────────────────────── Entry point ─────────────────────────

    /// <summary>Writes <paramref name="document"/> as a .docx file (replaced atomically).</summary>
    public static void Write(HDocument document, string outputPath)
    {
        var writer = new DocxWriter();
        var body = writer.Body(document);
        writer.WritePackage(document, body, outputPath);
    }

    // ───────────────────────── Parts and relationships ─────────────────────────

    /// <summary>A part of the package with its relationships (pictures and links are related per part).</summary>
    private sealed class Part(string path, string contentType)
    {
        public string Path { get; } = path;
        public string ContentType { get; } = contentType;
        public string Xml { get; set; } = "";
        public List<(string Id, string Type, string Target, bool External)> Relationships { get; } = [];

        /// <summary>Id of a relationship from this part (reused when the same target is related twice).</summary>
        public string Relate(string type, string target, bool external = false)
        {
            foreach (var rel in Relationships)
            {
                if (rel.Type == type && rel.Target == target && rel.External == external)
                    return rel.Id;
            }
            var id = "rId" + (Relationships.Count + 1).ToString(CultureInfo.InvariantCulture);
            Relationships.Add((id, type, target, external));
            return id;
        }
    }

    /// <summary>Runs <paramref name="write"/> with pictures and links related to <paramref name="part"/>.</summary>
    private T WithPart<T>(Part part, Func<T> write)
    {
        var previous = _part;
        _part = part;
        try
        {
            return write();
        }
        finally
        {
            _part = previous;
        }
    }

    /// <summary>Creates a header or footer part (content already written) and relates it to the document.</summary>
    private string HeaderFooterPart(string kind, Func<string> content)
    {
        var index = _parts.Count(p => p.Path.StartsWith($"word/{kind}", StringComparison.Ordinal)) + 1;
        var part = new Part($"word/{kind}{index}.xml", ContentTypeBase + kind + "+xml");
        var root = kind == "header" ? "w:hdr" : "w:ftr";
        part.Xml = $"<{root} {Namespaces}>{WithPart(part, content)}</{root}>";
        _parts.Add(part);
        return _document.Relate(RelationshipBase + kind, $"{kind}{index}.xml");
    }

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage(HDocument document, string body, string outputPath)
    {
        _document.Xml = $"<w:document {Namespaces}><w:body>{body}</w:body></w:document>";
        _document.Relate(RelationshipBase + "styles", "styles.xml");
        _document.Relate(RelationshipBase + "settings", "settings.xml");
        if (_numberings.Count > 0)
            _document.Relate(RelationshipBase + "numbering", "numbering.xml");
        if (_footnotes.Count > 0)
            _document.Relate(RelationshipBase + "footnotes", "footnotes.xml");
        if (_endnotes.Count > 0)
            _document.Relate(RelationshipBase + "endnotes", "endnotes.xml");

        var parts = new List<Part> { _document };
        parts.AddRange(_parts);
        parts.Add(new Part("word/styles.xml", ContentTypeBase + "styles+xml") { Xml = Styles() });
        parts.Add(new Part("word/settings.xml", ContentTypeBase + "settings+xml") { Xml = Settings() });
        if (_numberings.Count > 0)
            parts.Add(new Part("word/numbering.xml", ContentTypeBase + "numbering+xml") { Xml = Numbering() });
        if (_footnotesPart is not null)
        {
            _footnotesPart.Xml = Notes("footnote", _footnotes);
            parts.Add(_footnotesPart);
        }
        if (_endnotesPart is not null)
        {
            _endnotesPart.Xml = Notes("endnote", _endnotes);
            parts.Add(_endnotesPart);
        }
        parts.Add(new Part("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml") { Xml = CoreProperties(document.Title) });
        parts.Add(new Part("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml")
        {
            Xml = "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Filee</Application></Properties>",
        });

        var temp = outputPath + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteText(zip, "[Content_Types].xml", ContentTypes(parts));
            WriteText(zip, "_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{RelationshipBase}officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                $"<Relationship Id=\"rId3\" Type=\"{RelationshipBase}extended-properties\" Target=\"docProps/app.xml\"/>" +
                "</Relationships>");
            foreach (var part in parts)
            {
                WriteText(zip, part.Path, part.Xml);
                if (part.Relationships.Count > 0)
                {
                    var folder = part.Path[..part.Path.LastIndexOf('/')];
                    var name = part.Path[(part.Path.LastIndexOf('/') + 1)..];
                    WriteText(zip, $"{folder}/_rels/{name}.rels",
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        string.Concat(part.Relationships.Select(r =>
                            $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{Escape(r.Target)}\"{(r.External ? " TargetMode=\"External\"" : "")}/>")) +
                        "</Relationships>");
                }
            }
            foreach (var (source, (zipPath, extension)) in _media)
            {
                using var target = zip.CreateEntry(zipPath, CompressionLevel.Optimal).Open();
                if (string.Equals(Path.GetExtension(source).TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)
                    || (extension == "jpg" && Path.GetExtension(source).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
                {
                    using var input = File.OpenRead(source);
                    input.CopyTo(target);
                }
                else
                {
                    // WEBP, TIFF, SVG, AVIF ... → PNG, which every Word version shows.
                    using var image = new MagickImage(source);
                    image.Write(target, MagickFormat.Png);
                }
            }
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    private string ContentTypes(List<Part> parts)
    {
        var sb = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        foreach (var extension in _media.Values.Select(m => m.Extension).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.Append($"<Default Extension=\"{extension}\" ContentType=\"{PictureTypes[extension]}\"/>");
        foreach (var part in parts)
            sb.Append($"<Override PartName=\"/{part.Path}\" ContentType=\"{part.ContentType}\"/>");
        return sb.Append("</Types>").ToString();
    }

    private static string CoreProperties(string? title)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
               "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
               "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
               (string.IsNullOrWhiteSpace(title) ? "" : $"<dc:title>{Escape(title)}</dc:title>") +
               $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created>" +
               $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified>" +
               "</cp:coreProperties>";
    }

    // ───────────────────────── Styles and settings ─────────────────────────

    /// <summary>
    /// Heading sizes (half-points) of "heading 1".."heading 9". The names are Word's built-in ones, so the navigation
    /// pane, the table of contents and DocxReader (outline levels) recognise them.
    /// </summary>
    private static readonly int[] HeadingSizes = [32, 28, 26, 24, 22, 20, 20, 20, 20];

    private static string Styles()
    {
        var sb = new StringBuilder($"<w:styles {Namespaces}>");
        // Normal matches the HWPX writer's body text: 10 pt, 160 % of the font size ≈ 1.23 Word lines, no spacing.
        sb.Append("<w:docDefaults><w:rPrDefault><w:rPr>")
          .Append("<w:rFonts w:ascii=\"Calibri\" w:eastAsia=\"맑은 고딕\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/>")
          .Append("<w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/></w:rPr></w:rPrDefault>")
          .Append("<w:pPrDefault><w:pPr><w:widowControl/><w:spacing w:after=\"0\" w:line=\"295\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>");
        sb.Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>");
        sb.Append("<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>");
        sb.Append("<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>")
          .Append("<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>");
        sb.Append("<w:style w:type=\"numbering\" w:default=\"1\" w:styleId=\"NoList\"><w:name w:val=\"No List\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>");
        for (var level = 1; level <= 9; level++)
        {
            var size = HeadingSizes[level - 1];
            sb.Append($"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>")
              .Append($"<w:uiPriority w:val=\"9\"/>{(level > 1 ? "<w:unhideWhenUsed/>" : "")}<w:qFormat/>")
              .Append($"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{(level <= 2 ? 240 : 160)}\" w:after=\"80\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>")
              .Append($"<w:rPr><w:b/><w:bCs/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>");
        }
        sb.Append("<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>");
        foreach (var kind in new[] { "Footnote", "Endnote" })
        {
            sb.Append($"<w:style w:type=\"paragraph\" w:styleId=\"{kind}Text\"><w:name w:val=\"{kind.ToLowerInvariant()} text\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>")
              .Append("<w:pPr><w:spacing w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/></w:rPr></w:style>");
            sb.Append($"<w:style w:type=\"character\" w:styleId=\"{kind}Reference\"><w:name w:val=\"{kind.ToLowerInvariant()} reference\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>")
              .Append("<w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr></w:style>");
        }
        foreach (var kind in new[] { "Header", "Footer" })
        {
            sb.Append($"<w:style w:type=\"paragraph\" w:styleId=\"{kind}\"><w:name w:val=\"{kind.ToLowerInvariant()}\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>")
              .Append("<w:pPr><w:tabs><w:tab w:val=\"center\" w:pos=\"4513\"/><w:tab w:val=\"right\" w:pos=\"9026\"/></w:tabs><w:spacing w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>");
        }
        sb.Append("<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:uiPriority w:val=\"39\"/>")
          .Append("<w:pPr><w:spacing w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:tblPr><w:tblBorders>")
          .Append(string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(e => $"<w:{e} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>")))
          .Append("</w:tblBorders></w:tblPr></w:style>");
        return sb.Append("</w:styles>").ToString();
    }

    /// <summary>settings.xml; children follow the CT_Settings sequence.</summary>
    private string Settings()
    {
        var sb = new StringBuilder($"<w:settings {Namespaces}>");
        sb.Append("<w:zoom w:percent=\"100\"/>");
        sb.Append("<w:defaultTabStop w:val=\"800\"/>");
        if (_evenAndOddHeaders)
            sb.Append("<w:evenAndOddHeaders/>");
        sb.Append("<w:characterSpacingControl w:val=\"doNotCompress\"/>");
        if (_footnotes.Count > 0)
            sb.Append("<w:footnotePr><w:footnote w:id=\"-1\"/><w:footnote w:id=\"0\"/></w:footnotePr>");
        if (_endnotes.Count > 0)
            sb.Append("<w:endnotePr><w:endnote w:id=\"-1\"/><w:endnote w:id=\"0\"/></w:endnotePr>");
        // Word 2013+ layout; without it Word opens the file in compatibility mode.
        sb.Append("<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>");
        return sb.Append("</w:settings>").ToString();
    }

    // ───────────────────────── Numbering ─────────────────────────

    /// <summary>numId of a list (one abstractNum + num per list definition, created on first use).</summary>
    private int NumberingId(HNumbering numbering)
    {
        if (!_numberings.TryGetValue(numbering, out var id))
            _numberings[numbering] = id = _numberings.Count + 1;
        return id;
    }

    /// <summary>Indent of list level <paramref name="level"/> (twips): 400 per level with a 400 hanging number.</summary>
    private static int ListIndent(int level) => (level + 1) * 400;

    private string Numbering()
    {
        var sb = new StringBuilder($"<w:numbering {Namespaces}>");
        foreach (var (numbering, id) in _numberings)
        {
            sb.Append($"<w:abstractNum w:abstractNumId=\"{id}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (var level = 0; level < 9; level++)
            {
                var definition = level < numbering.Levels.Count ? numbering.Levels[level] : new HNumberingLevel("DIGIT", $"^{level + 1}.", 1, Bullet: false);
                var (format, text) = definition.Bullet
                    ? ("bullet", definition.Text.Length > 0 ? definition.Text : "●")
                    : (NumberFormat(definition.Format), NumberText(definition.Text));
                sb.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"{Math.Max(0, definition.Start)}\"/><w:numFmt w:val=\"{format}\"/>")
                  .Append($"<w:lvlText w:val=\"{Escape(text)}\"/><w:lvlJc w:val=\"left\"/>")
                  .Append($"<w:pPr><w:ind w:left=\"{ListIndent(level)}\" w:hanging=\"400\"/></w:pPr></w:lvl>");
            }
            sb.Append("</w:abstractNum>");
        }
        foreach (var id in _numberings.Values)
            sb.Append($"<w:num w:numId=\"{id}\"><w:abstractNumId w:val=\"{id}\"/></w:num>");
        return sb.Append("</w:numbering>").ToString();
    }

    /// <summary>OWPML number format → Word numFmt (the inverse of DocxReader.NumberFormat).</summary>
    private static string NumberFormat(string owpml) => owpml switch
    {
        "ROMAN_CAPITAL" => "upperRoman",
        "ROMAN_SMALL" => "lowerRoman",
        "LATIN_CAPITAL" => "upperLetter",
        "LATIN_SMALL" => "lowerLetter",
        "HANGUL_SYLLABLE" => "ganada",
        "HANGUL_JAMO" => "chosung",
        "CIRCLED_DIGIT" => "decimalEnclosedCircle",
        "HANGUL_PHONETIC" => "koreanDigital",
        "IDEOGRAPH" => "ideographDigital",
        _ => "decimal",
    };

    /// <summary>"^1.^2." → "%1.%2." (Word's level placeholders).</summary>
    private static string NumberText(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i + 1 < chars.Length; i++)
        {
            if (chars[i] == '^' && char.IsDigit(chars[i + 1]))
                chars[i] = '%';
        }
        return new string(chars);
    }

    // ───────────────────────── Notes ─────────────────────────

    /// <summary>footnotes.xml / endnotes.xml: the two separators Word expects (ids -1 and 0), then the notes from id 1.</summary>
    private static string Notes(string kind, List<string> notes)
    {
        var sb = new StringBuilder($"<w:{kind}s {Namespaces}>");
        sb.Append($"<w:{kind} w:type=\"separator\" w:id=\"-1\"><w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:r><w:separator/></w:r></w:p></w:{kind}>");
        sb.Append($"<w:{kind} w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:r><w:continuationSeparator/></w:r></w:p></w:{kind}>");
        for (var i = 0; i < notes.Count; i++)
            sb.Append($"<w:{kind} w:id=\"{i + 1}\">{notes[i]}</w:{kind}>");
        return sb.Append($"</w:{kind}s>").ToString();
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>HWPUNIT (1/7200 inch) → twips (1/1440 inch).</summary>
    private static int Twips(int hwpUnit) => (int)Math.Round(hwpUnit / 5.0);

    /// <summary>HWPUNIT → EMU (1/914400 inch).</summary>
    private static long Emu(int hwpUnit) => hwpUnit * 127L;

    /// <summary>"#RRGGBB" → "RRGGBB", or null when it is not a colour.</summary>
    private static string? Hex(string? color) =>
        color is { Length: 7 } && color[0] == '#' && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? color[1..].ToUpperInvariant()
            : null;

    private static string Escape(string text) => HwpxWriter.Escape(text);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteText(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(Declaration);
        writer.Write(xml);
    }
}
