// Builds small .xlsx and .pptx files for tests, so the built-in spreadsheet and presentation readers can be tested
// without sample documents (see DocxBuilder for .docx).

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Filee.Engines.Tests;

/// <summary>Writes the parts of an OPC package (the zip format of DOCX, XLSX and PPTX).</summary>
internal static class OpcWriter
{
    public const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static void Save(string path, IReadOnlyDictionary<string, string> parts, IReadOnlyDictionary<string, byte[]> media)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(Declaration + xml);
        }
        foreach (var (name, bytes) in media)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    public static string Relationships(IEnumerable<(string Id, string Type, string Target)> relationships) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(relationships.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{r.Type}\" Target=\"{r.Target}\"/>")) +
        "</Relationships>";

    public static string ContentTypes(IEnumerable<(string Part, string Type)> overrides) =>
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
        string.Concat(overrides.Select(o => $"<Override PartName=\"/{o.Part}\" ContentType=\"application/vnd.openxmlformats-officedocument.{o.Type}+xml\"/>")) +
        "</Types>";

    public static string Escape(string text) => SecurityElement.Escape(text);
}

/// <summary>A workbook with shared strings, a fixed style sheet and any number of sheets.</summary>
internal sealed class XlsxBuilder
{
    private const string Namespaces = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    // Styles (cellXfs index):
    public const int Plain = 0;
    public const int Header = 1;   // bold white text on #1F4E79, thin borders, centered
    public const int Won = 2;      // #,##0"원"
    public const int Date = 3;     // built-in 14
    public const int Percent = 4;  // 0.0%
    public const int Total = 5;    // bold red, thin top border, #,##0"원"

    private const string StyleSheet =
        "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"#,##0&quot;원&quot;\"/><numFmt numFmtId=\"165\" formatCode=\"0.0%\"/></numFmts>" +
        "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"맑은 고딕\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFC00000\"/><name val=\"맑은 고딕\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"3\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color indexed=\"64\"/></left><right style=\"thin\"><color indexed=\"64\"/></right><top style=\"thin\"><color indexed=\"64\"/></top><bottom style=\"thin\"><color indexed=\"64\"/></bottom><diagonal/></border>" +
        "<border><left/><right/><top style=\"thin\"><color auto=\"1\"/></top><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\"/></cellXfs>";

    private readonly List<(string Name, string Xml, bool Hidden)> _sheets = [];
    private readonly List<string> _strings = [];

    /// <summary>A shared-string cell.</summary>
    public string Text(string reference, string text, int style = Plain)
    {
        var index = _strings.IndexOf(text);
        if (index < 0)
        {
            index = _strings.Count;
            _strings.Add(text);
        }
        return $"<c r=\"{reference}\" s=\"{style}\" t=\"s\"><v>{index}</v></c>";
    }

    public static string Number(string reference, double value, int style = Plain) =>
        $"<c r=\"{reference}\" s=\"{style}\"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>";

    /// <summary>A row element; <paramref name="cells"/> from <see cref="Text"/> and <see cref="Number"/>.</summary>
    public static string Row(int number, params string[] cells) => $"<row r=\"{number}\">{string.Concat(cells)}</row>";

    /// <param name="rows">Rows of sheetData.</param>
    /// <param name="before">Elements before sheetData (sheetViews, cols).</param>
    /// <param name="after">Elements after sheetData (mergeCells).</param>
    public XlsxBuilder Sheet(string name, string rows, string before = "", string after = "", bool hidden = false)
    {
        _sheets.Add((name, $"<worksheet {Namespaces}>{before}<sheetData>{rows}</sheetData>{after}</worksheet>", hidden));
        return this;
    }

    public string Save(string path)
    {
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = OpcWriter.Relationships([("rId1", "officeDocument", "xl/workbook.xml")]),
            ["xl/workbook.xml"] = $"<workbook {Namespaces}><sheets>" +
                                  string.Concat(_sheets.Select((s, i) => $"<sheet name=\"{OpcWriter.Escape(s.Name)}\" sheetId=\"{i + 1}\"{(s.Hidden ? " state=\"hidden\"" : "")} r:id=\"rId{i + 1}\"/>")) +
                                  "</sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships(
                _sheets.Select((_, i) => ($"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml"))
                    .Append(("rIdS", "sharedStrings", "sharedStrings.xml"))
                    .Append(("rIdT", "styles", "styles.xml"))),
            ["xl/sharedStrings.xml"] = $"<sst {Namespaces} count=\"{_strings.Count}\" uniqueCount=\"{_strings.Count}\">" +
                                       string.Concat(_strings.Select(s => $"<si><t xml:space=\"preserve\">{OpcWriter.Escape(s)}</t></si>")) + "</sst>",
            ["xl/styles.xml"] = $"<styleSheet {Namespaces}>{StyleSheet}</styleSheet>",
        };
        for (var i = 0; i < _sheets.Count; i++)
            parts[$"xl/worksheets/sheet{i + 1}.xml"] = _sheets[i].Xml;
        parts["[Content_Types].xml"] = OpcWriter.ContentTypes(
            _sheets.Select((_, i) => ($"xl/worksheets/sheet{i + 1}.xml", "spreadsheetml.worksheet"))
                .Append(("xl/workbook.xml", "spreadsheetml.sheet.main"))
                .Append(("xl/sharedStrings.xml", "spreadsheetml.sharedStrings"))
                .Append(("xl/styles.xml", "spreadsheetml.styles")));
        OpcWriter.Save(path, parts, new Dictionary<string, byte[]>());
        return path;
    }
}

/// <summary>
/// A 16:9 presentation with one master (title / body placeholders, centred title style, bullet body style and a
/// decorative bar), one layout and a theme; slides are added as p:spTree content.
/// </summary>
internal sealed class PptxBuilder
{
    public const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    public const long Width = 12192000;
    public const long Height = 6858000;

    /// <summary>Colour of the master's decorative bar (theme accent2).</summary>
    public const string BarColor = "#ED7D31";

    private const string TreeStart = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    private const string Master =
        "<p:cSld><p:spTree>" + TreeStart +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"365125\"/><a:ext cx=\"10515600\" cy=\"1325563\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr anchor=\"ctr\"/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Body\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"838200\" y=\"1825625\"/><a:ext cx=\"10515600\" cy=\"4351338\"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"Bar\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        "<p:spPr><a:xfrm><a:off x=\"0\" y=\"6705600\"/><a:ext cx=\"12192000\" cy=\"152400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        "<a:solidFill><a:schemeClr val=\"accent2\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>" +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
        "<p:txStyles>" +
        "<p:titleStyle><a:lvl1pPr algn=\"ctr\"><a:buNone/><a:defRPr sz=\"4400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle><a:lvl1pPr marL=\"228600\" indent=\"-228600\"><a:buChar char=\"•\"/><a:defRPr sz=\"2800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:defRPr></a:lvl1pPr>" +
        "<a:lvl2pPr marL=\"685800\" indent=\"-228600\"><a:buChar char=\"–\"/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:defRPr></a:lvl2pPr></p:bodyStyle>" +
        "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:defRPr></a:lvl1pPr></p:otherStyle>" +
        "</p:txStyles>";

    private const string Layout =
        "<p:cSld name=\"Title and Content\"><p:spTree>" + TreeStart +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>";

    private const string Theme =
        "<a:themeElements><a:clrScheme name=\"Office\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/><a:font script=\"Hang\" typeface=\"맑은 고딕\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/><a:font script=\"Hang\" typeface=\"맑은 고딕\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Office\"><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme></a:themeElements>";

    private readonly List<string> _slides = [];
    private readonly List<byte[]> _images = [];
    private int _nextId = 10;

    /// <summary>Adds a PNG; every slide can refer to it by the returned relationship id.</summary>
    public string Image(byte[] png)
    {
        _images.Add(png);
        return $"rIdImg{_images.Count}";
    }

    /// <summary>
    /// Adds a slide from p:spTree children; <paramref name="background"/> is an optional hex colour,
    /// <paramref name="backgroundImage"/> an optional picture (relationship id from <see cref="Image"/>) as the background.
    /// </summary>
    public PptxBuilder Slide(string shapes, string? background = null, string? backgroundImage = null)
    {
        var bg = backgroundImage is not null
            ? $"<p:bg><p:bgPr><a:blipFill dpi=\"0\" rotWithShape=\"1\"><a:blip r:embed=\"{backgroundImage}\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill><a:effectLst/></p:bgPr></p:bg>"
            : background is null ? "" : $"<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"{background.TrimStart('#')}\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>";
        _slides.Add($"<p:sld {Namespaces}><p:cSld>{bg}<p:spTree>{TreeStart}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return this;
    }

    /// <summary>The title placeholder (position and style come from the layout and master).</summary>
    public string Title(string text) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{_nextId++}\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(text)}</p:txBody></p:sp>";

    /// <summary>The content placeholder with one paragraph per item (level 0 or 1).</summary>
    public string Body(params (string Text, int Level)[] items) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{_nextId++}\" name=\"Content\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{string.Concat(items.Select(i => Paragraph(i.Text, i.Level > 0 ? $"<a:pPr lvl=\"{i.Level}\"/>" : "")))}</p:txBody></p:sp>";

    /// <summary>An auto shape (rect, roundRect, ellipse, ...) with a solid fill and optional centred white text.</summary>
    public string Shape(string geometry, long x, long y, long cx, long cy, string fill, string? text = null) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{_nextId++}\" name=\"Shape\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, cx, cy)}<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"{fill.TrimStart('#')}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>" +
        (text is null ? "" : $"<p:txBody><a:bodyPr anchor=\"ctr\"/><a:lstStyle/>{Paragraph(text, "<a:pPr algn=\"ctr\"/>", "<a:rPr lang=\"ko-KR\" sz=\"2000\" b=\"1\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:rPr>")}</p:txBody>") +
        "</p:sp>";

    public string Picture(string relationshipId, long x, long y, long cx, long cy) =>
        $"<p:pic><p:nvPicPr><p:cNvPr id=\"{_nextId++}\" name=\"Picture\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>" +
        $"<p:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
        $"<p:spPr>{Xfrm(x, y, cx, cy)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

    /// <summary>A table with PowerPoint's default style: header row and banded rows.</summary>
    public string Table(long x, long y, long columnWidth, long rowHeight, params string[][] rows) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{_nextId++}\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{columnWidth * rows[0].Length}\" cy=\"{rowHeight * rows.Length}\"/></p:xfrm>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl>" +
        "<a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}</a:tableStyleId></a:tblPr>" +
        $"<a:tblGrid>{string.Concat(rows[0].Select(_ => $"<a:gridCol w=\"{columnWidth}\"/>"))}</a:tblGrid>" +
        string.Concat(rows.Select(row => $"<a:tr h=\"{rowHeight}\">" +
            string.Concat(row.Select(cell => $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(cell)}</a:txBody><a:tcPr/></a:tc>")) + "</a:tr>")) +
        "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";

    /// <summary>A group whose child coordinates are scaled by <paramref name="scale"/> into the slide.</summary>
    public string Group(long x, long y, long cx, long cy, double scale, string children) =>
        $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"{_nextId++}\" name=\"Group\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        $"<p:grpSpPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{(long)(cx / scale)}\" cy=\"{(long)(cy / scale)}\"/></a:xfrm></p:grpSpPr>" +
        children + "</p:grpSp>";

    private static string Xfrm(long x, long y, long cx, long cy) => $"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>";

    private static string Paragraph(string text, string pPr = "", string rPr = "<a:rPr lang=\"ko-KR\"/>") =>
        $"<a:p>{pPr}<a:r>{rPr}<a:t>{OpcWriter.Escape(text)}</a:t></a:r></a:p>";

    public string Save(string path)
    {
        var presentationRels = new List<(string, string, string)> { ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "theme", "theme/theme1.xml") };
        presentationRels.AddRange(_slides.Select((_, i) => ($"rId{i + 10}", "slide", $"slides/slide{i + 1}.xml")));
        var slideRels = new List<(string, string, string)> { ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml") };
        slideRels.AddRange(_images.Select((_, i) => ($"rIdImg{i + 1}", "image", $"../media/image{i + 1}.png")));

        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = OpcWriter.Relationships([("rId1", "officeDocument", "ppt/presentation.xml")]),
            ["ppt/presentation.xml"] = $"<p:presentation {Namespaces}>" +
                                       "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
                                       $"<p:sldIdLst>{string.Concat(_slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 10}\"/>"))}</p:sldIdLst>" +
                                       $"<p:sldSz cx=\"{Width}\" cy=\"{Height}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = OpcWriter.Relationships(presentationRels),
            ["ppt/slideMasters/slideMaster1.xml"] = $"<p:sldMaster {Namespaces}>{Master}</p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = OpcWriter.Relationships([("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml")]),
            ["ppt/slideLayouts/slideLayout1.xml"] = $"<p:sldLayout {Namespaces}>{Layout}</p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = OpcWriter.Relationships([("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")]),
            ["ppt/theme/theme1.xml"] = $"<a:theme {Namespaces} name=\"Office\">{Theme}</a:theme>",
        };
        for (var i = 0; i < _slides.Count; i++)
        {
            parts[$"ppt/slides/slide{i + 1}.xml"] = _slides[i];
            parts[$"ppt/slides/_rels/slide{i + 1}.xml.rels"] = OpcWriter.Relationships(slideRels);
        }
        parts["[Content_Types].xml"] = OpcWriter.ContentTypes(
            _slides.Select((_, i) => ($"ppt/slides/slide{i + 1}.xml", "presentationml.slide"))
                .Append(("ppt/presentation.xml", "presentationml.presentation.main"))
                .Append(("ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster"))
                .Append(("ppt/slideLayouts/slideLayout1.xml", "presentationml.slideLayout"))
                .Append(("ppt/theme/theme1.xml", "theme")));
        OpcWriter.Save(path, parts, _images.Select((png, i) => ($"ppt/media/image{i + 1}.png", png)).ToDictionary(p => p.Item1, p => p.png));
        return path;
    }
}
