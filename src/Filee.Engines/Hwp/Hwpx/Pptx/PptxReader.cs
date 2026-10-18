// PPTX (PresentationML) → HDocument without PowerPoint or LibreOffice: one page per slide, sized like the slide, with
// every object floating at its place on the page: text boxes (placeholders inherit position and text style from the
// layout and master), shapes with solid or gradient fills and outlines, pictures, tables, lines, groups and the slide
// background. PPTX → PDF / PNG then goes through the HWPX writer and rhwp.
//
// Split over partial files: this one walks slides and objects, PptxReader.Text resolves text and its inherited styles.
// Not converted: charts, SmartArt, rotation, effects (shadows, glow), picture cropping, animations, notes, hidden slides.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

internal sealed partial class PptxReader
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = OoxmlColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Picture formats the HWPX writer and 한글 readers display.</summary>
    private static readonly HashSet<string> PictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" };

    private readonly OpcPackage _package;
    private readonly string _mediaFolder;
    private XElement? _defaultTextStyle;

    private PptxReader(OpcPackage package, string mediaFolder)
    {
        _package = package;
        _mediaFolder = mediaFolder;
    }

    /// <summary>Reads <paramref name="path"/>; pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder)
    {
        using var package = new OpcPackage(path);
        return new PptxReader(package, mediaFolder).ReadPresentation();
    }

    /// <summary>EMU (914400 per inch) → HWPUNIT (7200 per inch).</summary>
    private static int Hwp(long emu) => (int)Math.Round(emu / 127.0);

    private static long Emu(XAttribute? attribute) =>
        long.TryParse((string?)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // ───────────────────────── Presentation & slides ─────────────────────────

    private HDocument ReadPresentation()
    {
        var presentationPath = _package.MainPartPath ?? "ppt/presentation.xml";
        var presentation = _package.Part(presentationPath)?.Root
                           ?? throw new InvalidDataException("The file is not a PowerPoint presentation (no ppt/presentation.xml).");
        _defaultTextStyle = presentation.Element(P + "defaultTextStyle");
        var size = presentation.Element(P + "sldSz");
        var width = Hwp(Emu(size?.Attribute("cx")) is > 0 and var cx ? cx : 12192000);
        var height = Hwp(Emu(size?.Attribute("cy")) is > 0 and var cy ? cy : 6858000);

        var section = new HSection { Page = new HPage(width, height, 0, 0, 0, 0, 0, 0, 0) };
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            var slidePath = _package.Target(presentationPath, (string?)id.Attribute(R + "id"));
            if (slidePath is null || _package.Part(slidePath)?.Root is not { } slide || (string?)slide.Attribute("show") is "0" or "false")
                continue;
            var page = new HParagraph { PageBreakBefore = section.Blocks.Count > 0 };
            ReadSlide(slidePath, slide, width, height, page.Inlines);
            section.Blocks.Add(page);
        }
        if (section.Blocks.Count == 0)
            section.Blocks.Add(new HParagraph());

        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    /// <summary>The parts one slide draws from, with the colours and placeholders they define.</summary>
    /// <summary>Latin and East Asian fonts of the theme (what "+mj-lt", "+mn-ea" ... refer to).</summary>
    private sealed record ThemeFonts(string? MajorLatin, string? MinorLatin, string? MajorEastAsian, string? MinorEastAsian)
    {
        public static ThemeFonts From(XDocument? theme)
        {
            var scheme = theme?.Descendants(A + "fontScheme").FirstOrDefault();
            string? Latin(string kind) => NonEmpty((string?)scheme?.Element(A + kind)?.Element(A + "latin")?.Attribute("typeface"));
            // Korean templates name the Hangul font in <a:font script="Hang">, often with an empty <a:ea>.
            string? EastAsian(string kind) =>
                NonEmpty((string?)scheme?.Element(A + kind)?.Element(A + "ea")?.Attribute("typeface"))
                ?? NonEmpty((string?)scheme?.Element(A + kind)?.Elements(A + "font").FirstOrDefault(f => (string?)f.Attribute("script") == "Hang")?.Attribute("typeface"));
            return new ThemeFonts(Latin("majorFont"), Latin("minorFont"), EastAsian("majorFont"), EastAsian("minorFont"));
        }

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed class SlideContext(string slidePath, XElement slide, XElement? layout, string? layoutPath, XElement? master, string? masterPath, OoxmlColors colors, ThemeFonts fonts)
    {
        public ThemeFonts Fonts { get; } = fonts;
        public string SlidePath { get; } = slidePath;
        public XElement Slide { get; } = slide;
        public XElement? Layout { get; } = layout;
        public string? LayoutPath { get; } = layoutPath;
        public XElement? Master { get; } = master;
        public string? MasterPath { get; } = masterPath;
        public OoxmlColors Colors { get; } = colors;
    }

    private void ReadSlide(string slidePath, XElement slide, int width, int height, List<HInline> output)
    {
        var layoutPath = _package.TargetOfType(slidePath, "/slideLayout");
        var layout = layoutPath is null ? null : _package.Part(layoutPath)?.Root;
        var masterPath = layoutPath is null ? null : _package.TargetOfType(layoutPath, "/slideMaster");
        var master = masterPath is null ? null : _package.Part(masterPath)?.Root;
        var themePath = masterPath is null ? null : _package.TargetOfType(masterPath, "/theme");
        var theme = themePath is null ? null : _package.Part(themePath);
        // The master's colour map says which theme colours "text" and "background" are (dark templates swap them).
        var colors = OoxmlColors.FromTheme(theme)
            .WithMap(master?.Element(P + "clrMap")?.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));
        var context = new SlideContext(slidePath, slide, layout, layoutPath, master, masterPath, colors, ThemeFonts.From(theme));

        if (Background(context, width, height) is { } background)
            output.Add(background);

        // Decorations of the master and the layout (logos, bars) lie under the slide's own objects.
        var showMaster = (string?)slide.Attribute("showMasterSp") is not ("0" or "false")
                         && (string?)layout?.Attribute("showMasterSp") is not ("0" or "false");
        if (showMaster && master is not null && masterPath is not null)
            Objects(context, masterPath, Tree(master), Transform.Identity, output, decorationsOnly: true);
        if (layout is not null && layoutPath is not null)
            Objects(context, layoutPath, Tree(layout), Transform.Identity, output, decorationsOnly: true);
        Objects(context, slidePath, Tree(slide), Transform.Identity, output, decorationsOnly: false);
    }

    private static XElement? Tree(XElement part) => part.Element(P + "cSld")?.Element(P + "spTree");

    private static HAnchor Anchor(int x, int y) =>
        new("PAPER", "LEFT", Math.Max(0, x), "PAPER", "TOP", Math.Max(0, y), "IN_FRONT_OF_TEXT", AllowOverlap: true);

    /// <summary>
    /// The first background of slide, layout or master, as a full-page rectangle, or as a full-page picture for a
    /// picture background (chalkboards, photos); null for plain white.
    /// </summary>
    private HInline? Background(SlideContext context, int width, int height)
    {
        foreach (var (part, partPath) in new[] { (context.Slide, context.SlidePath), (context.Layout, context.LayoutPath), (context.Master, context.MasterPath) })
        {
            var bg = part?.Element(P + "cSld")?.Element(P + "bg");
            if (bg is null)
                continue;
            var properties = bg.Element(P + "bgPr");
            if (properties?.Element(A + "blipFill") is { } blipFill && partPath is not null)
                return PictureBackground(partPath, blipFill, width, height);
            string? fill = null;
            HGradient? gradient = null;
            if (properties is not null)
                (fill, gradient) = Fill(properties, context.Colors, null);
            else if (bg.Element(P + "bgRef") is { } reference)
                fill = context.Colors.Drawing(reference);
            if (gradient is null && (fill is null || fill.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)))
                return null;
            return new HShape(HShapeKind.Rectangle)
            {
                Fill = fill,
                Gradient = gradient,
                Line = HBorder.None,
                Width = width,
                Height = height,
                Anchor = Anchor(0, 0),
            };
        }
        return null;
    }

    /// <summary>A picture background stretched over the slide (a tiled one too); null when the picture can't be used.</summary>
    private HImage? PictureBackground(string partPath, XElement blipFill, int width, int height)
    {
        var embed = (string?)blipFill.Element(A + "blip")?.Attribute(R + "embed");
        var target = _package.Target(partPath, embed);
        if (target is null || !PictureTypes.Contains(Path.GetExtension(target)) || _package.Extract(target, _mediaFolder) is not { } file)
            return null;
        return new HImage(file) { Width = width, Height = height, Anchor = Anchor(0, 0) };
    }

    // ───────────────────────── Objects ─────────────────────────

    /// <summary>Maps child coordinates of a group to slide coordinates (EMU).</summary>
    private readonly record struct Transform(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
    {
        public static readonly Transform Identity = new(1, 1, 0, 0);
        public (long X, long Y, long W, long H) Apply(long x, long y, long w, long h) =>
            ((long)(OffsetX + x * ScaleX), (long)(OffsetY + y * ScaleY), (long)(w * ScaleX), (long)(h * ScaleY));
    }

    private void Objects(SlideContext context, string partPath, XElement? tree, Transform transform, List<HInline> output, bool decorationsOnly)
    {
        foreach (var element in tree?.Elements() ?? [])
        {
            // Placeholders of the master / layout are only templates for the slide's own placeholders.
            if (decorationsOnly && Placeholder(element) is not null)
                continue;
            switch (element.Name.LocalName)
            {
                case "sp":
                    Shape(context, partPath, element, transform, output);
                    break;
                case "cxnSp":
                    Connector(context, element, transform, output);
                    break;
                case "pic":
                    Picture(context, partPath, element, transform, output);
                    break;
                case "graphicFrame":
                    Table(context, partPath, element, transform, output);
                    break;
                case "grpSp":
                    Group(context, partPath, element, transform, output, decorationsOnly);
                    break;
                case "AlternateContent":
                    // Newer object kinds come with a fallback that older readers understand.
                    var fallback = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback");
                    if (fallback is not null)
                        Objects(context, partPath, fallback, transform, output, decorationsOnly);
                    break;
            }
        }
    }

    private void Group(SlideContext context, string partPath, XElement group, Transform transform, List<HInline> output, bool decorationsOnly)
    {
        var xfrm = group.Element(P + "grpSpPr")?.Element(A + "xfrm");
        var inner = transform;
        if (xfrm is not null)
        {
            var (x, y, w, h) = (Emu(xfrm.Element(A + "off")?.Attribute("x")), Emu(xfrm.Element(A + "off")?.Attribute("y")),
                Emu(xfrm.Element(A + "ext")?.Attribute("cx")), Emu(xfrm.Element(A + "ext")?.Attribute("cy")));
            var (cx, cy, cw, ch) = (Emu(xfrm.Element(A + "chOff")?.Attribute("x")), Emu(xfrm.Element(A + "chOff")?.Attribute("y")),
                Emu(xfrm.Element(A + "chExt")?.Attribute("cx")), Emu(xfrm.Element(A + "chExt")?.Attribute("cy")));
            var sx = cw > 0 ? (double)w / cw : 1;
            var sy = ch > 0 ? (double)h / ch : 1;
            // child → group space → parent space
            inner = new Transform(transform.ScaleX * sx, transform.ScaleY * sy,
                transform.OffsetX + (x - cx * sx) * transform.ScaleX, transform.OffsetY + (y - cy * sy) * transform.ScaleY);
        }
        Objects(context, partPath, group, inner, output, decorationsOnly);
    }

    /// <summary>The p:ph element of a shape, picture or frame, if it is a placeholder.</summary>
    private static XElement? Placeholder(XElement element) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?
            .Element(P + "nvPr")?.Element(P + "ph");

    /// <summary>
    /// The layout's and master's version of placeholder <paramref name="ph"/>: matched by index, else by type
    /// (ctrTitle ↔ title, subTitle / obj / untyped ↔ body).
    /// </summary>
    private static (XElement? Layout, XElement? Master) Inherited(SlideContext context, XElement? ph)
    {
        if (ph is null)
            return (null, null);
        var type = Normalize((string?)ph.Attribute("type"));
        var index = (string?)ph.Attribute("idx");

        XElement? Find(XElement? part, bool byIndex)
        {
            var candidates = Tree(part ?? new XElement("none"))?.Descendants().Where(e => e.Name.LocalName is "sp" or "pic" && Placeholder(e) is not null).ToList() ?? [];
            if (byIndex && index is not null && candidates.FirstOrDefault(c => (string?)Placeholder(c)!.Attribute("idx") == index) is { } byIdx)
                return byIdx;
            return candidates.FirstOrDefault(c => Normalize((string?)Placeholder(c)!.Attribute("type")) == type);
        }

        var layout = Find(context.Layout, byIndex: true);
        var master = Find(context.Master, byIndex: false);
        return (layout, master);
    }

    private static string Normalize(string? type) => type switch
    {
        "ctrTitle" or "title" => "title",
        null or "" or "obj" or "subTitle" or "body" => "body",
        _ => type,
    };

    /// <summary>a:xfrm of the object, else of the placeholder it inherits from.</summary>
    private static XElement? Geometry(XElement element, XElement? layoutPh, XElement? masterPh)
    {
        static XElement? Xfrm(XElement? e) => e?.Element(P + "spPr")?.Element(A + "xfrm") ?? e?.Element(P + "xfrm");
        return Xfrm(element) ?? Xfrm(layoutPh) ?? Xfrm(masterPh);
    }

    private static (long X, long Y, long W, long H)? Bounds(XElement? xfrm, Transform transform)
    {
        if (xfrm is null)
            return null;
        var off = xfrm.Element(A + "off");
        var ext = xfrm.Element(A + "ext");
        return transform.Apply(Emu(off?.Attribute("x")), Emu(off?.Attribute("y")), Emu(ext?.Attribute("cx")), Emu(ext?.Attribute("cy")));
    }

    private void Shape(SlideContext context, string partPath, XElement shape, Transform transform, List<HInline> output)
    {
        var ph = Placeholder(shape);
        var (layoutPh, masterPh) = partPath == context.SlidePath ? Inherited(context, ph) : (null, null);
        if (Bounds(Geometry(shape, layoutPh, masterPh), transform) is not { } bounds || bounds.W <= 0 && bounds.H <= 0)
            return;

        var properties = shape.Element(P + "spPr");
        var style = shape.Element(P + "style");
        var (fill, gradient) = Fill(properties, context.Colors, style?.Element(A + "fillRef"));
        var line = Line(properties, context.Colors, style?.Element(A + "lnRef"));
        var geometry = (string?)properties?.Element(A + "prstGeom")?.Attribute("prst") ?? "rect";
        var (x, y, w, h) = (Hwp(bounds.X), Hwp(bounds.Y), Math.Max(1, Hwp(bounds.W)), Math.Max(1, Hwp(bounds.H)));

        var textColor = context.Colors.Drawing(style?.Element(A + "fontRef"));
        var blocks = TextBody(context, partPath, shape.Element(P + "txBody"), ph, layoutPh, masterPh, textColor);
        if (geometry is "line" or "straightConnector1")
        {
            AddLine(shape, bounds, line, output);
            return;
        }
        var kind = geometry == "ellipse" ? HShapeKind.Ellipse : HShapeKind.Rectangle;
        var corners = geometry == "roundRect" ? CornerRatio(properties) : 0;
        if (blocks.Count > 0)
        {
            var body = BodyProperties(shape, layoutPh, masterPh);
            var padding = body.Padding;
            if (kind == HShapeKind.Ellipse)
            {
                // PowerPoint puts the text of an ellipse into the rectangle inscribed in it.
                var (dx, dy) = ((int)(w * 0.146), (int)(h * 0.146));
                padding = new HInsets(padding.Left + dx, padding.Right + dx, padding.Top + dy, padding.Bottom + dy);
            }
            output.Add(new HTextBox
            {
                Width = w,
                Height = h,
                Anchor = Anchor(x, y),
                Fill = fill,
                Gradient = gradient,
                Line = line,
                Shape = kind,
                CornerRatio = corners,
                Padding = padding,
                VerticalAlign = body.Anchor,
            }.With(blocks));
            return;
        }
        if (fill is null && gradient is null && line.Style == HBorderStyle.None)
            return; // an empty placeholder or an invisible box
        output.Add(new HShape(kind)
        {
            Width = w,
            Height = h,
            Anchor = Anchor(x, y),
            Fill = fill,
            Gradient = gradient,
            Line = line,
            CornerRatio = corners,
        });
    }

    /// <summary>
    /// Corner radius of a rounded rectangle as a percentage of the shorter side: the "adj" guide in 1/1000 %,
    /// 16 667 (a sixth) by default.
    /// </summary>
    private static int CornerRatio(XElement? properties)
    {
        var guide = properties?.Element(A + "prstGeom")?.Element(A + "avLst")?.Elements(A + "gd")
            .FirstOrDefault(g => (string?)g.Attribute("name") == "adj");
        var value = ((string?)guide?.Attribute("fmla"))?.Split(' ') is ["val", var number] && int.TryParse(number, out var v) ? v : 16667;
        return Math.Clamp((int)Math.Round(value / 1000.0), 0, 50);
    }

    private void Connector(SlideContext context, XElement connector, Transform transform, List<HInline> output)
    {
        var properties = connector.Element(P + "spPr");
        if (Bounds(properties?.Element(A + "xfrm"), transform) is not { } bounds)
            return;
        var line = Line(properties, context.Colors, connector.Element(P + "style")?.Element(A + "lnRef"));
        AddLine(connector, bounds, line, output);
    }

    private static void AddLine(XElement element, (long X, long Y, long W, long H) bounds, HBorder line, List<HInline> output)
    {
        if (line.Style == HBorderStyle.None)
            return;
        var xfrm = element.Element(P + "spPr")?.Element(A + "xfrm");
        output.Add(new HShape(HShapeKind.Line)
        {
            Width = Math.Max(1, Hwp(bounds.W)),
            Height = Math.Max(1, Hwp(bounds.H)),
            Anchor = Anchor(Hwp(bounds.X), Hwp(bounds.Y)),
            Line = line,
            FlipHorizontal = (string?)xfrm?.Attribute("flipH") is "1" or "true",
            FlipVertical = (string?)xfrm?.Attribute("flipV") is "1" or "true",
        });
    }

    private void Picture(SlideContext context, string partPath, XElement picture, Transform transform, List<HInline> output)
    {
        var (layoutPh, masterPh) = partPath == context.SlidePath ? Inherited(context, Placeholder(picture)) : (null, null);
        if (Bounds(Geometry(picture, layoutPh, masterPh), transform) is not { } bounds)
            return;
        var embed = (string?)picture.Element(P + "blipFill")?.Element(A + "blip")?.Attribute(R + "embed");
        var target = _package.Target(partPath, embed);
        if (target is null || !PictureTypes.Contains(Path.GetExtension(target)) || _package.Extract(target, _mediaFolder) is not { } file)
            return; // vector formats (EMF, WMF, SVG without a bitmap fallback) are left out
        output.Add(new HImage(file)
        {
            Width = Math.Max(1, Hwp(bounds.W)),
            Height = Math.Max(1, Hwp(bounds.H)),
            Anchor = Anchor(Hwp(bounds.X), Hwp(bounds.Y)),
        });
    }

    // ───────────────────────── Fills and lines ─────────────────────────

    /// <summary>
    /// Solid colour or gradient of a shape's properties; a style reference (p:style/a:fillRef) applies when the
    /// properties say nothing, as for shapes drawn with PowerPoint's default style.
    /// </summary>
    private static (string? Fill, HGradient? Gradient) Fill(XElement? properties, OoxmlColors colors, XElement? styleRef)
    {
        if (properties?.Element(A + "noFill") is not null)
            return (null, null);
        if (properties?.Element(A + "solidFill") is { } solid)
            return (colors.Drawing(solid), null);
        if (properties?.Element(A + "gradFill") is { } gradFill)
        {
            var stops = gradFill.Element(A + "gsLst")?.Elements(A + "gs")
                .OrderBy(gs => Emu(gs.Attribute("pos")))
                .Select(gs => colors.Drawing(gs))
                .OfType<string>()
                .ToList() ?? [];
            var angle = (int)(Emu(gradFill.Element(A + "lin")?.Attribute("ang")) / 60000);
            return stops.Count >= 2 ? (stops[0], new HGradient(stops, angle)) : (stops.FirstOrDefault(), null);
        }
        if (properties?.Element(A + "blipFill") is not null || properties?.Element(A + "pattFill") is { })
            return (null, null);
        return styleRef is not null && Emu(styleRef.Attribute("idx")) > 0 ? (colors.Drawing(styleRef), null) : (null, null);
    }

    private static HBorder Line(XElement? properties, OoxmlColors colors, XElement? styleRef)
    {
        var ln = properties?.Element(A + "ln");
        if (ln?.Element(A + "noFill") is not null)
            return HBorder.None;
        var width = Emu(ln?.Attribute("w")) is > 0 and var w ? w : 12700; // 1 pt
        var color = ln?.Element(A + "solidFill") is { } solid ? colors.Drawing(solid)
            : styleRef is not null && Emu(styleRef.Attribute("idx")) > 0 ? colors.Drawing(styleRef)
            : null;
        if (color is null)
            return HBorder.None;
        var dash = (string?)ln?.Element(A + "prstDash")?.Attribute("val");
        var kind = dash switch
        {
            "dot" or "sysDot" => HBorderStyle.Dot,
            "dash" or "sysDash" or "lgDash" => HBorderStyle.Dash,
            "dashDot" or "lgDashDot" or "sysDashDot" => HBorderStyle.DashDot,
            _ => HBorderStyle.Solid,
        };
        return new HBorder(kind, width / 36000.0, color);
    }
}

internal static class HTextBoxExtensions
{
    /// <summary>Adds blocks to a text box built with an object initializer.</summary>
    public static HTextBox With(this HTextBox box, IEnumerable<HBlock> blocks)
    {
        box.Blocks.AddRange(blocks);
        return box;
    }
}
