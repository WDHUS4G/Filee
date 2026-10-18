// Builds small .docx files for tests, so DOCX → HWPX features can be tested without sample documents.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

/// <summary>A minimal WordprocessingML package: body XML plus optional styles, numbering, headers, notes and media.</summary>
internal sealed class DocxBuilder
{
    public const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:w10=\"urn:schemas-microsoft-com:office:word\"";

    /// <summary>A4 portrait, 1 inch margins, header/footer 0.5 inch.</summary>
    public const string DefaultPage = "<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>";

    private readonly List<(string Id, string Type, string Target, bool External)> _relationships = [];
    private readonly Dictionary<string, string> _parts = [];
    private readonly Dictionary<string, byte[]> _media = [];

    /// <summary>Content of w:body (paragraphs and tables), without the final sectPr.</summary>
    public StringBuilder Body { get; } = new();

    /// <summary>Children of the body's final w:sectPr.</summary>
    public string SectionProperties { get; set; } = DefaultPage;

    public string Styles { get; set; } = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="맑은 고딕" w:eastAsia="맑은 고딕" w:hAnsi="맑은 고딕"/><w:sz w:val="20"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
        <w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>
        <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:tblPr><w:tblBorders>
          <w:top w:val="single" w:sz="4" w:color="000000"/><w:left w:val="single" w:sz="4" w:color="000000"/><w:bottom w:val="single" w:sz="4" w:color="000000"/>
          <w:right w:val="single" w:sz="4" w:color="000000"/><w:insideH w:val="single" w:sz="4" w:color="000000"/><w:insideV w:val="single" w:sz="4" w:color="000000"/>
        </w:tblBorders></w:tblPr></w:style>
        """;

    public string? Numbering { get; set; }
    public string? Settings { get; set; }

    public DocxBuilder Paragraph(string xml)
    {
        Body.Append(xml);
        return this;
    }

    /// <summary>Adds a PNG and returns its relationship id.</summary>
    public string Image(byte[] png)
    {
        var name = $"media/image{_media.Count + 1}.png";
        _media["word/" + name] = png;
        return Relationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", name);
    }

    public string Hyperlink(string url) =>
        Relationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", url, external: true);

    /// <summary>Adds a header part (content of w:hdr) and returns its relationship id.</summary>
    public string Header(string paragraphs) => Part("header", $"<w:hdr {Namespaces}>{paragraphs}</w:hdr>");

    public string Footer(string paragraphs) => Part("footer", $"<w:ftr {Namespaces}>{paragraphs}</w:ftr>");

    /// <summary>Footnotes (w:footnote elements with ids ≥ 1; the separators are added).</summary>
    public void Footnotes(string notes) =>
        Part("footnotes", $"<w:footnotes {Namespaces}><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>{notes}</w:footnotes>");

    /// <summary>Endnotes (w:endnote elements with ids ≥ 1; the separators are added).</summary>
    public void Endnotes(string notes) =>
        Part("endnotes", $"<w:endnotes {Namespaces}><w:endnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>{notes}</w:endnotes>");

    public string Save(string path)
    {
        if (Numbering is not null)
            Part("numbering", $"<w:numbering {Namespaces}>{Numbering}</w:numbering>");
        if (Settings is not null)
            Part("settings", $"<w:settings {Namespaces}>{Settings}</w:settings>");
        Part("styles", $"<w:styles {Namespaces}>{Styles}</w:styles>");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        var overrides = string.Concat(_parts.Keys.Select(p => $"<Override PartName=\"/word/{p}\" ContentType=\"{ContentType(p)}\"/>"));
        Write("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            overrides + "</Types>");
        Write("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Write("word/_rels/document.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(_relationships.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{System.Security.SecurityElement.Escape(r.Target)}\"{(r.External ? " TargetMode=\"External\"" : "")}/>")) +
            "</Relationships>");
        Write("word/document.xml",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Namespaces}><w:body>{Body}<w:sectPr>{SectionProperties}</w:sectPr></w:body></w:document>");
        foreach (var (name, xml) in _parts)
            Write("word/" + name, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
        foreach (var (name, bytes) in _media)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private string Part(string kind, string xml)
    {
        var name = kind is "header" or "footer" ? $"{kind}{_parts.Keys.Count(k => k.StartsWith(kind, StringComparison.Ordinal)) + 1}.xml" : $"{kind}.xml";
        _parts[name] = xml;
        return Relationship($"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{kind}", name);
    }

    private string Relationship(string type, string target, bool external = false)
    {
        var id = $"rId{_relationships.Count + 10}";
        _relationships.Add((id, type, target, external));
        return id;
    }

    private static string ContentType(string part) => part switch
    {
        "styles.xml" => "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml",
        "numbering.xml" => "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml",
        "settings.xml" => "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml",
        "footnotes.xml" => "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml",
        "endnotes.xml" => "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml",
        _ when part.StartsWith("header", StringComparison.Ordinal) => "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml",
        _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml",
    };

    // ───────── snippets ─────────

    public static string P(string runs, string pPr = "") => $"<w:p><w:pPr>{pPr}</w:pPr>{runs}</w:p>";

    public static string R(string text, string rPr = "") => $"<w:r><w:rPr>{rPr}</w:rPr><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r>";

    /// <summary>A complex field (PAGE, NUMPAGES ...) with a cached result.</summary>
    public static string Field(string instruction, string result) =>
        $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> {instruction} </w:instrText></w:r>" +
        $"<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>{R(result)}<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    /// <summary>A floating DrawingML object (wp:anchor) around <paramref name="graphic"/> (a:graphicData content).</summary>
    public static string Anchor(string graphicUri, string graphic, long cx, long cy, string horizontal, string vertical, string wrap = "<wp:wrapSquare wrapText=\"bothSides\"/>") =>
        $"<w:r><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        $"<wp:simplePos x=\"0\" y=\"0\"/>{horizontal}{vertical}<wp:extent cx=\"{cx}\" cy=\"{cy}\"/>{wrap}<wp:docPr id=\"1\" name=\"obj\"/>" +
        $"<a:graphic><a:graphicData uri=\"{graphicUri}\">{graphic}</a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";

    public static string Picture(string relationshipId, long cx, long cy) =>
        $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"1\" name=\"p\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"{relationshipId}\"/></pic:blipFill>" +
        $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"/></pic:spPr></pic:pic>";

    public static string InlinePicture(string relationshipId, long cx, long cy) =>
        $"<w:r><w:drawing><wp:inline><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"2\" name=\"i\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
        Picture(relationshipId, cx, cy) + "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    public const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
}
