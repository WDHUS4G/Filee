// Writes a PowerPoint presentation (PPTX, PresentationML / ECMA-376) whose slides each show one picture, fitted and
// centred, with optional speaker notes: the package PDF → PPTX needs. Written by hand like the DOCX writer (the Open XML
// SDK is only used by the tests, which validate the output with it), as small as PowerPoint, LibreOffice Impress and
// Keynote accept: presentation, one slide master with one blank layout, a theme, presentation / view / table style
// properties, the slides with their pictures, a notes master and notes slides when there is text, and the document
// properties.
//
// Slides and pictures are written into the ZIP as they come, so a long PDF never has all its pages in memory; the
// parts that list them (presentation, content types) follow at the end.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Pdf;

/// <summary>A PPTX of picture slides, written slide by slide.</summary>
internal sealed class PictureDeckWriter : IDisposable
{
    /// <summary>EMU (English Metric Units) per point: 914400 per inch.</summary>
    public const double EmuPerPoint = 12700;

    /// <summary>Smallest slide side PowerPoint allows (1 inch, ST_SlideSizeCoordinate).</summary>
    public const long MinSlideSide = 914400;

    /// <summary>Largest slide side PowerPoint allows (56 inches, ST_SlideSizeCoordinate).</summary>
    public const long MaxSlideSide = 51206400;

    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string PresentationTypes = "application/vnd.openxmlformats-officedocument.presentationml.";

    /// <summary>Namespaces of every PresentationML part.</summary>
    private const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    /// <summary>The group every shape tree starts with.</summary>
    private const string TreeStart =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private const string ColorMap =
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    /// <summary>Notes pages are portrait Letter-like, as in PowerPoint (7.5 × 10 inches).</summary>
    private const long NotesWidth = 6858000;
    private const long NotesHeight = 9144000;

    private readonly string _path;
    private readonly string _temp;
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly List<bool> _slideHasNotes = [];
    private bool _saved;

    /// <param name="path">The PPTX to write; it appears only once <see cref="Save"/> has finished.</param>
    /// <param name="slideWidth">Slide width in EMU, within <see cref="MinSlideSide"/>..<see cref="MaxSlideSide"/>.</param>
    /// <param name="slideHeight">Slide height in EMU.</param>
    public PictureDeckWriter(string path, long slideWidth, long slideHeight)
    {
        _path = path;
        SlideWidth = Math.Clamp(slideWidth, MinSlideSide, MaxSlideSide);
        SlideHeight = Math.Clamp(slideHeight, MinSlideSide, MaxSlideSide);
        _temp = path + ".tmp";
        _file = File.Create(_temp);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create);
    }

    public long SlideWidth { get; }
    public long SlideHeight { get; }

    /// <summary>
    /// Slide size for a page of <paramref name="width"/> × <paramref name="height"/> points: the page's own size, so an
    /// A4 page gives an A4 portrait slide and a 16:9 page a 16:9 slide. Pages larger than PowerPoint's 56 inches are
    /// scaled down and pages smaller than 1 inch scaled up, keeping the shape as far as those limits allow.
    /// </summary>
    public static (long Width, long Height) SlideSize(double width, double height)
    {
        double cx = Math.Max(1, width) * EmuPerPoint;
        double cy = Math.Max(1, height) * EmuPerPoint;
        var shrink = Math.Min(1, MaxSlideSide / Math.Max(cx, cy));
        cx *= shrink;
        cy *= shrink;
        var grow = Math.Max(1, MinSlideSide / Math.Min(cx, cy));
        cx *= grow;
        cy *= grow;
        return (Math.Clamp((long)Math.Round(cx), MinSlideSide, MaxSlideSide), Math.Clamp((long)Math.Round(cy), MinSlideSide, MaxSlideSide));
    }

    /// <summary>
    /// Where a picture of <paramref name="width"/> × <paramref name="height"/> (any unit) goes on the slide: as large as
    /// fits without cropping, centred (EMU).
    /// </summary>
    public (long X, long Y, long Width, long Height) Fit(double width, double height)
    {
        var scale = Math.Min(SlideWidth / Math.Max(width, 1e-6), SlideHeight / Math.Max(height, 1e-6));
        var cx = Math.Clamp((long)Math.Round(width * scale), 1, SlideWidth);
        var cy = Math.Clamp((long)Math.Round(height * scale), 1, SlideHeight);
        return ((SlideWidth - cx) / 2, (SlideHeight - cy) / 2, cx, cy);
    }

    /// <summary>
    /// Adds a slide showing <paramref name="jpeg"/>, fitted and centred like <see cref="Fit"/> for a picture of
    /// <paramref name="width"/> × <paramref name="height"/>, with <paramref name="notes"/> (lines separated by CR LF or
    /// LF) as its speaker notes when there are any.
    /// </summary>
    public void AddSlide(ReadOnlySpan<byte> jpeg, double width, double height, string name, string? notes)
    {
        var number = _slideHasNotes.Count + 1;
        var hasNotes = !string.IsNullOrWhiteSpace(notes);
        _slideHasNotes.Add(hasNotes);

        using (var media = _zip.CreateEntry($"ppt/media/image{number}.jpeg", CompressionLevel.NoCompression).Open())
            media.Write(jpeg);

        var (x, y, cx, cy) = Fit(width, height);
        var slide = new StringBuilder($"<p:sld {Namespaces}><p:cSld>").Append(TreeStart)
            .Append("<p:pic><p:nvPicPr>")
            .Append($"<p:cNvPr id=\"2\" name=\"{Escape(name)}\" descr=\"{Escape(name)}\"/>")
            .Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
            .Append("<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
            .Append($"<p:spPr><a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(cx)}\" cy=\"{N(cy)}\"/></a:xfrm>")
            .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>")
            .Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        Write($"ppt/slides/slide{number}.xml", slide.ToString());
        List<(string, string, string)> relationships =
        [
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", "image", $"../media/image{number}.jpeg"),
        ];
        if (hasNotes)
            relationships.Add(("rId3", "notesSlide", $"../notesSlides/notesSlide{number}.xml"));
        Write($"ppt/slides/_rels/slide{number}.xml.rels", Rels([.. relationships]));

        if (!hasNotes)
            return;
        var body = new StringBuilder();
        foreach (var line in notes!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            body.Append(line.Length == 0 ? "<a:p/>" : $"<a:p><a:r><a:t>{Escape(line)}</a:t></a:r></a:p>");
        Write($"ppt/notesSlides/notesSlide{number}.xml",
            $"<p:notes {Namespaces}><p:cSld>{TreeStart}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>" +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
        Write($"ppt/notesSlides/_rels/notesSlide{number}.xml.rels", Rels(
            ("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"),
            ("rId2", "slide", $"../slides/slide{number}.xml")));
    }

    /// <summary>Writes the parts that list the slides and moves the finished file into place.</summary>
    public void Save()
    {
        if (_slideHasNotes.Count == 0)
            throw new InvalidOperationException("A presentation needs at least one slide.");
        var notes = _slideHasNotes.Contains(true);
        var slides = _slideHasNotes.Count;

        // Presentation: master, notes master, slides, sizes. Relationship ids: 1-5 fixed parts, 6 the notes master,
        // slides from 7.
        var presentation = new StringBuilder($"<p:presentation {Namespaces} saveSubsetFonts=\"1\">")
            .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
        if (notes)
            presentation.Append("<p:notesMasterIdLst><p:notesMasterId r:id=\"rId6\"/></p:notesMasterIdLst>");
        presentation.Append("<p:sldIdLst>");
        for (var i = 0; i < slides; i++)
            presentation.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{7 + i}\"/>");
        presentation.Append("</p:sldIdLst>")
            .Append($"<p:sldSz cx=\"{N(SlideWidth)}\" cy=\"{N(SlideHeight)}\"/>")
            .Append($"<p:notesSz cx=\"{N(NotesWidth)}\" cy=\"{N(NotesHeight)}\"/>")
            .Append("</p:presentation>");
        Write("ppt/presentation.xml", presentation.ToString());

        var relationships = new List<(string, string, string)>
        {
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"),
            ("rId2", "theme", "theme/theme1.xml"),
            ("rId3", "presProps", "presProps.xml"),
            ("rId4", "viewProps", "viewProps.xml"),
            ("rId5", "tableStyles", "tableStyles.xml"),
        };
        if (notes)
            relationships.Add(("rId6", "notesMaster", "notesMasters/notesMaster1.xml"));
        for (var i = 0; i < slides; i++)
            relationships.Add(($"rId{7 + i}", "slide", $"slides/slide{i + 1}.xml"));
        Write("ppt/_rels/presentation.xml.rels", Rels([.. relationships]));

        Write("ppt/slideMasters/slideMaster1.xml", SlideMaster());
        Write("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", "theme", "../theme/theme1.xml")));
        Write("ppt/slideLayouts/slideLayout1.xml",
            $"<p:sldLayout {Namespaces} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\">{TreeStart}</p:spTree></p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Write("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")));
        Write("ppt/theme/theme1.xml", Theme());
        if (notes)
        {
            Write("ppt/notesMasters/notesMaster1.xml", NotesMaster());
            // A master of its own needs a theme of its own (PowerPoint's notes masters use theme2 as well).
            Write("ppt/notesMasters/_rels/notesMaster1.xml.rels", Rels(("rId1", "theme", "../theme/theme2.xml")));
            Write("ppt/theme/theme2.xml", Theme());
        }
        Write("ppt/presProps.xml", $"<p:presentationPr {Namespaces}/>");
        Write("ppt/viewProps.xml",
            $"<p:viewPr {Namespaces}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr>" +
            "<p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        Write("ppt/tableStyles.xml",
            "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>");

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Write("docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created>" +
            $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>");
        Write("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            $"<Application>Filee</Application><Slides>{N(slides)}</Slides><Notes>{N(_slideHasNotes.Count(n => n))}</Notes></Properties>");

        Write("_rels/.rels", Rels(
            ("rId1", "officeDocument", "ppt/presentation.xml"),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
            ("rId3", "extended-properties", "docProps/app.xml")));
        Write("[Content_Types].xml", ContentTypes(slides, notes));

        _zip.Dispose();
        _file.Dispose();
        File.Move(_temp, _path, overwrite: true);
        _saved = true;
    }

    public void Dispose()
    {
        if (_saved)
            return;
        _zip.Dispose();
        _file.Dispose();
        try { File.Delete(_temp); } catch (IOException) { }
    }

    private string ContentTypes(int slides, bool notes)
    {
        var sb = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
        void Override(string part, string type) => sb.Append($"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>");
        Override("ppt/presentation.xml", PresentationTypes + "presentation.main+xml");
        Override("ppt/slideMasters/slideMaster1.xml", PresentationTypes + "slideMaster+xml");
        Override("ppt/slideLayouts/slideLayout1.xml", PresentationTypes + "slideLayout+xml");
        for (var i = 1; i <= slides; i++)
            Override($"ppt/slides/slide{i}.xml", PresentationTypes + "slide+xml");
        Override("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
        if (notes)
        {
            Override("ppt/notesMasters/notesMaster1.xml", PresentationTypes + "notesMaster+xml");
            Override("ppt/theme/theme2.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
            for (var i = 1; i <= slides; i++)
            {
                if (_slideHasNotes[i - 1])
                    Override($"ppt/notesSlides/notesSlide{i}.xml", PresentationTypes + "notesSlide+xml");
            }
        }
        Override("ppt/presProps.xml", PresentationTypes + "presProps+xml");
        Override("ppt/viewProps.xml", PresentationTypes + "viewProps+xml");
        Override("ppt/tableStyles.xml", PresentationTypes + "tableStyles+xml");
        Override("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        return sb.Append("</Types>").ToString();
    }

    /// <summary>A white background, no placeholders and default text styles: the slides only hold their picture.</summary>
    private static string SlideMaster()
    {
        static string Level(string size, string font, string align = "") =>
            $"<a:lvl1pPr{align}><a:defRPr sz=\"{size}\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
            $"<a:latin typeface=\"+{font}-lt\"/><a:ea typeface=\"+{font}-ea\"/><a:cs typeface=\"+{font}-cs\"/></a:defRPr></a:lvl1pPr>";

        return $"<p:sldMaster {Namespaces}><p:cSld>" +
               "<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
               $"{TreeStart}</p:spTree></p:cSld>{ColorMap}" +
               "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
               $"<p:txStyles><p:titleStyle>{Level("4400", "mj", " algn=\"ctr\"")}</p:titleStyle>" +
               $"<p:bodyStyle>{Level("2800", "mn")}</p:bodyStyle><p:otherStyle>{Level("1800", "mn")}</p:otherStyle></p:txStyles>" +
               "</p:sldMaster>";
    }

    /// <summary>The notes page: the slide's picture in the upper part (shaped like the slide), the notes below.</summary>
    private string NotesMaster()
    {
        // The slide image fills a 6 × 4 inch box at the top, centred; the notes take the lower half of the page.
        const long boxLeft = 685800, boxTop = 457200, boxWidth = 5486400, boxHeight = 3657600;
        var scale = Math.Min((double)boxWidth / SlideWidth, (double)boxHeight / SlideHeight);
        var cx = (long)Math.Round(SlideWidth * scale);
        var cy = (long)Math.Round(SlideHeight * scale);
        var x = boxLeft + (boxWidth - cx) / 2;
        var y = boxTop + (boxHeight - cy) / 2;
        return $"<p:notesMaster {Namespaces}><p:cSld>{TreeStart}" +
               "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
               "<p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>" +
               $"<p:spPr><a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(cx)}\" cy=\"{N(cy)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
               "<a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>" +
               "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
               "<p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"1\"/></p:nvPr></p:nvSpPr>" +
               "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"4343400\"/><a:ext cx=\"5486400\" cy=\"4114800\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
               "<p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
               $"</p:spTree></p:cSld>{ColorMap}" +
               "<p:notesStyle><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
               "<a:defRPr sz=\"1200\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
               "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:notesStyle>" +
               "</p:notesMaster>";
    }

    /// <summary>Office's default colours and fonts; three plain entries in each style list, the least the schema takes.</summary>
    private static string Theme()
    {
        static string Color(string name, string value) => $"<a:{name}><a:srgbClr val=\"{value}\"/></a:{name}>";
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var lines = string.Concat(new[] { "6350", "12700", "19050" }.Select(w => $"<a:ln w=\"{w}\">{fill}</a:ln>"));
        return "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office Theme\"><a:themeElements>" +
               "<a:clrScheme name=\"Office\">" +
               "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
               Color("dk2", "44546A") + Color("lt2", "E7E6E6") + Color("accent1", "4472C4") + Color("accent2", "ED7D31") +
               Color("accent3", "A5A5A5") + Color("accent4", "FFC000") + Color("accent5", "5B9BD5") + Color("accent6", "70AD47") +
               Color("hlink", "0563C1") + Color("folHlink", "954F72") + "</a:clrScheme>" +
               "<a:fontScheme name=\"Office\">" +
               "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
               "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
               "<a:fmtScheme name=\"Office\">" +
               $"<a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst>" +
               $"<a:lnStyleLst>{lines}</a:lnStyleLst>" +
               "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
               $"<a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme>" +
               "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    /// <summary>A relationships part; a relationship type without a scheme is one of the officeDocument types.</summary>
    private static string Rels(params (string Id, string Type, string Target)[] relationships)
    {
        var sb = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (id, type, target) in relationships)
        {
            var fullType = type.Contains("://", StringComparison.Ordinal) ? type : Relationships + type;
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{fullType}\" Target=\"{target}\"/>");
        }
        return sb.Append("</Relationships>").ToString();
    }

    private void Write(string name, string xml)
    {
        using var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(Declaration);
        writer.Write(xml);
    }

    private static string Escape(string text) => HwpxWriter.Escape(text);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
