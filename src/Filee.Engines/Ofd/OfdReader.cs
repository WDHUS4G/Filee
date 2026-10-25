// OFD (GB/T 33190-2016, the Chinese standard for fixed-layout documents: e-invoices, contracts, official
// documents) → HDocument with no external engine, so OFD → DOCX / HWPX / TXT works out of the box.
//
// An OFD file is a ZIP package of XML: OFD.xml names the document (Document.xml), which lists the pages, the
// template pages they are drawn on and the resource files (fonts, pictures, drawing parameters). Like a PDF a page
// holds positioned text, pictures and vector paths, in millimetres from the top-left corner. Text objects are read
// with their transformation (CTM), character advances (DeltaX, also the "g" shorthand) and vertical runs (DeltaY);
// the layout (OfdReader.Layout) groups them into lines and paragraphs, and grids drawn with lines become tables
// (OfdReader.Tables) — the frame of an e-invoice. Pictures are unpacked and kept in reading order, or float at their
// place when text sits beside or under them (QR codes, seals).
//
// Three writer variants seen in the wild are handled: the standard's own structure, WPS (page size only in
// Document.xml, a Res.xml next to the pages, bold as Weight=700) and tax invoices (template pages with the labels,
// DocumentRes, the colour in a layer's DrawParam, item names wrapped onto a second line, the cipher text as one
// TextCode of four lines, a sales list with rules only between its columns). Annotations add the text and pictures
// of their appearance; seals stored in signatures (Signs/) float where their stamp is (OfdReader.Seals).
//
// Not converted: signature checks, embedded fonts (the font name is kept), composite objects, JBIG2 pictures.
// Encrypted documents (GM/T 0099 Encryptions.xml) are refused; nothing is decrypted. The package is read
// defensively (OfdPackage).

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Ofd;

/// <summary>Reads an OFD file into the document model (DOCX / HWPX writers, plain text).</summary>
internal sealed partial class OfdReader
{
    /// <summary>Documents with more pages are refused.</summary>
    private const int MaxPages = 10_000;

    /// <summary>Text, picture and path objects of a whole document; more are refused.</summary>
    private const int MaxObjects = 2_000_000;

    /// <summary>Text runs on one page (a dense page drawn character by character has a few thousand); more are refused.</summary>
    private const int MaxFragmentsPerPage = 50_000;

    /// <summary>Pictures unpacked per document; further ones are left out.</summary>
    private const int MaxPictures = 2_000;

    /// <summary>Largest picture decoded (pixels), against images that unpack to gigabytes.</summary>
    private const long MaxPicturePixels = 100_000_000;

    /// <summary>Nesting of layers and page blocks followed.</summary>
    private const int MaxDepth = 32;

    /// <summary>Default font size (mm) when a text object names none.</summary>
    private const double DefaultSize = 3.5;

    private readonly OfdPackage _package;
    private readonly string _mediaFolder;
    private readonly Dictionary<string, string?> _extracted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (XElement Root, Resources Res)?> _templates = new();
    private int _objects;

    private OfdReader(OfdPackage package, string mediaFolder)
    {
        _package = package;
        _mediaFolder = mediaFolder;
    }

    public static HDocument Read(string path, string mediaFolder)
    {
        using var package = new OfdPackage(path);
        return new OfdReader(package, mediaFolder).Read();
    }

    private HDocument Read()
    {
        var ofd = _package.LoadXml("OFD.xml")?.Root;
        if (ofd is null || ofd.Name.LocalName != "OFD")
            throw new InvalidDataException("The file is not an OFD document (it has no OFD.xml).");
        RefuseEncrypted(ofd);

        // A package may hold several documents; the first is read.
        var docPath = OfdPackage.Resolve("", Child(Child(ofd, "DocBody"), "DocRoot")?.Value)
                      ?? throw new InvalidDataException("The OFD file names no document (DocRoot).");
        var document = _package.LoadXml(docPath)?.Root ?? throw new InvalidDataException($"The OFD package has no {docPath}.");
        var docDir = OfdPackage.DirName(docPath);
        var common = Child(document, "CommonData");
        var defaultArea = PageArea(Child(common, "PageArea")) ?? (210, 297);

        // Fonts, pictures and drawing parameters of the whole document; WPS keeps them in a Res.xml it does not name.
        var docRes = new Resources(null);
        foreach (var res in Children(common, "PublicRes").Concat(Children(common, "DocumentRes")))
            LoadRes(docRes, OfdPackage.Resolve(docDir, res.Value), docDir);
        LoadRes(docRes, OfdPackage.Resolve(docDir, "Res.xml"), docDir);

        var templates = new Dictionary<string, string>();
        foreach (var template in Children(common, "TemplatePage"))
        {
            if (Attr(template, "ID") is { } id && PagePath(docDir, Attr(template, "BaseLoc")) is { } path)
                templates.TryAdd(id, path);
        }

        var annotations = AnnotationFiles(document, docDir);
        var pages = new List<PageContent>();
        foreach (var page in Children(Child(document, "Pages"), "Page"))
        {
            if (pages.Count == MaxPages)
                throw new InvalidDataException($"The OFD document has more than {MaxPages:N0} pages; Filee does not read it.");
            var path = PagePath(docDir, Attr(page, "BaseLoc"));
            if (_package.LoadXml(path)?.Root is not { } content)
                continue;
            var id = Attr(page, "ID");
            var read = ReadPage(content, path!, docDir, docRes, templates, defaultArea,
                id is not null && annotations.TryGetValue(id, out var files) ? files : []);
            read.Id = id;
            pages.Add(read);
        }
        AddSeals(ofd, pages);
        return Build(pages);
    }

    /// <summary>The annotation files of each page (by page ID) that the document's Annotations.xml lists.</summary>
    private Dictionary<string, List<string>> AnnotationFiles(XElement document, string docDir)
    {
        var result = new Dictionary<string, List<string>>();
        var listPath = OfdPackage.Resolve(docDir, Child(document, "Annotations")?.Value);
        if (_package.LoadXml(listPath)?.Root is not { } list)
            return result;
        foreach (var page in Children(list, "Page").Take(MaxPages))
        {
            if (Attr(page, "PageID") is not { } id || OfdPackage.Resolve(OfdPackage.DirName(listPath!), Child(page, "FileLoc")?.Value) is not { } file)
                continue;
            if (!result.TryGetValue(id, out var files))
                result[id] = files = [];
            if (files.Count < 8)
                files.Add(file);
        }
        return result;
    }

    /// <summary>
    /// Encrypted OFD (GM/T 0099: an Encryptions.xml entry file, encrypted parts) is refused, never decrypted.
    /// </summary>
    private void RefuseEncrypted(XElement ofd)
    {
        if (_package.Names.Any(n => n.Equals("Encryptions.xml", StringComparison.OrdinalIgnoreCase)
                                    || n.EndsWith("/Encryptions.xml", StringComparison.OrdinalIgnoreCase))
            || ofd.Descendants().Any(e => e.Name.LocalName is "Encryptions" or "Encryption" or "EncryptInfo"))
        {
            throw new InvalidDataException("The OFD document is encrypted. Filee does not convert encrypted (DRM-protected) documents.");
        }
    }

    /// <summary>A page or template location: the content file itself, or (some writers) its folder.</summary>
    private static string? PagePath(string docDir, string? baseLoc) =>
        OfdPackage.Resolve(docDir, baseLoc) is not { } path ? null
        : path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? path
        : path + "/Content.xml";

    // ───────────────────────── Pages ─────────────────────────

    /// <summary>What a page draws, in millimetres from its top-left corner.</summary>
    private sealed class PageContent
    {
        /// <summary>The page's ID in Document.xml (signatures and annotations name pages by it).</summary>
        public string? Id { get; set; }

        public double Width { get; set; }
        public double Height { get; set; }
        public List<Fragment> Fragments { get; } = [];
        public List<Picture> Pictures { get; } = [];
        public List<Rule> Rules { get; } = [];
        public List<Shade> Shades { get; } = [];

        /// <summary>Straight path segments seen; a page with very many is a drawing, not a form.</summary>
        public int Segments { get; set; }
    }

    /// <summary>A run of text on one baseline (or one vertical column of characters).</summary>
    private sealed class Fragment(string text, HCharFormat format, double size, bool template)
    {
        public string Text { get; set; } = text;
        public HCharFormat Format { get; } = format;

        /// <summary>Font size in millimetres.</summary>
        public double Size { get; } = size;

        /// <summary>Drawn by a template page (labels of a form), not by the page itself.</summary>
        public bool Template { get; } = template;

        /// <summary>A further line of one TextCode (a block the writer broke itself): it starts a line of its own.</summary>
        public bool Continued { get; init; }

        public double X { get; set; }
        public double Baseline { get; set; }
        public double Width { get; set; }

        /// <summary>Vertical text: distance from the first character's baseline to the last one's.</summary>
        public double Run { get; set; }

        public bool Vertical => Run > 0;
        public double Left => X;
        public double Right => X + Width;
        public double Top => Baseline - 0.88 * Size;
        public double Bottom => Baseline + Run + 0.12 * Size;
    }

    /// <summary>A picture and its box: a part of the package, or a file already made (a seal drawn from its signature).</summary>
    private sealed record Picture(string? Part, string? File, double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    private PageContent ReadPage(XElement content, string path, string docDir, Resources docRes,
        Dictionary<string, string> templates, (double W, double H) defaultArea, List<string> annotationFiles)
    {
        var page = new PageContent();
        var area = PageArea(Child(content, "Area"));

        // Template pages first (the form behind the values): their labels read together with the page's text.
        foreach (var id in Children(content, "Template").Select(t => Attr(t, "TemplateID")).OfType<string>().Distinct().Take(8))
        {
            if (!templates.TryGetValue(id, out var templatePath) || Template(templatePath, docDir, docRes) is not { } template)
                continue;
            area ??= PageArea(Child(template.Root, "Area"));
            Collect(Child(template.Root, "Content"), template.Res, page, null, true, 0);
        }

        var res = PageResources(content, path, docDir, docRes);
        Collect(Child(content, "Content"), res, page, null, false, 0);

        // Annotations drawn on the page (a stamp, a note): the text and pictures of their appearance, placed from its
        // boundary. Their paths (highlights, underlines) are no table rules.
        foreach (var file in annotationFiles)
        {
            foreach (var annot in Children(_package.LoadXml(file)?.Root, "Annot").Take(1000))
            {
                if (Attr(annot, "Visible") == "false")
                    continue;
                foreach (var appearance in Children(annot, "Appearance"))
                {
                    var box = Box(Attr(appearance, "Boundary")) ?? default;
                    Collect(appearance, res, page, null, false, 1, (box.X, box.Y), paths: false);
                }
            }
        }

        // Labels are merged once the page's values are there: a value between two characters (共 4 页) keeps them apart.
        if (page.Fragments.Count(f => f.Template) <= 5000)
            MergeTemplateLabels(page.Fragments);
        (page.Width, page.Height) = area ?? defaultArea;
        return page;
    }

    private (XElement Root, Resources Res)? Template(string path, string docDir, Resources docRes)
    {
        if (!_templates.TryGetValue(path, out var template))
        {
            template = _package.LoadXml(path)?.Root is { } root ? (root, PageResources(root, path, docDir, docRes)) : null;
            _templates[path] = template;
        }
        return template;
    }

    /// <summary>The resource files a page names (PageRes), and a Res.xml next to it; the document's behind them.</summary>
    private Resources PageResources(XElement content, string path, string docDir, Resources docRes)
    {
        var pageDir = OfdPackage.DirName(path);
        var res = new Resources(docRes);
        foreach (var pageRes in Children(content, "PageRes"))
            LoadRes(res, OfdPackage.Resolve(pageDir, pageRes.Value) is { } name && _package.Has(name) ? name : OfdPackage.Resolve(docDir, pageRes.Value), docDir);
        LoadRes(res, OfdPackage.Resolve(pageDir, "Res.xml"), docDir);
        return res;
    }

    /// <summary>Page area "x y width height" (PhysicalBox, else ApplicationBox) in millimetres.</summary>
    private static (double W, double H)? PageArea(XElement? area)
    {
        foreach (var name in new[] { "PhysicalBox", "ApplicationBox" })
        {
            if (Box(Child(area, name)?.Value) is { Width: >= 1 and <= 5000, Height: >= 1 and <= 5000 } box)
                return (box.Width, box.Height);
        }
        return null;
    }

    /// <summary>
    /// The drawing objects of a content tree (layers, page blocks), with the layer's drawing parameters. Objects of an
    /// annotation's appearance are placed from its <paramref name="origin"/> and draw no rules (<paramref name="paths"/>).
    /// </summary>
    private void Collect(XElement? container, Resources res, PageContent page, string? layerDraw, bool template, int depth,
        (double X, double Y) origin = default, bool paths = true)
    {
        if (container is null || depth > MaxDepth)
            return;
        foreach (var element in container.Elements())
        {
            if (Attr(element, "Visible") == "false")
                continue;
            switch (element.Name.LocalName)
            {
                case "Layer":
                    Collect(element, res, page, Attr(element, "DrawParam") ?? layerDraw, template, depth + 1, origin, paths);
                    break;
                case "PageBlock":
                    Collect(element, res, page, layerDraw, template, depth + 1, origin, paths);
                    break;
                case "TextObject":
                    Count();
                    TextObject(element, res, page, layerDraw, template, origin);
                    break;
                case "ImageObject":
                    Count();
                    ImageObject(element, res, page, origin);
                    break;
                case "PathObject" when paths:
                    Count();
                    PathObject(element, res, page, layerDraw);
                    break;
            }
        }
    }

    private void Count()
    {
        if (++_objects > MaxObjects)
            throw new InvalidDataException($"The OFD document has more than {MaxObjects:N0} drawing objects; Filee does not read it.");
    }

    // ───────────────────────── Text ─────────────────────────

    private static void TextObject(XElement element, Resources res, PageContent page, string? layerDraw, bool template, (double X, double Y) origin)
    {
        var box = Box(Attr(element, "Boundary")) ?? default;
        var ctm = Matrix.Parse(Attr(element, "CTM"));
        var size = Number(Attr(element, "Size")) is { } s and > 0 and < 1000 ? s : DefaultSize;
        var font = Attr(element, "Font") is { } fontId ? res.Font(fontId) : null;
        var bold = Number(Attr(element, "Weight")) >= 600 || font?.Bold == true;
        var italic = Attr(element, "Italic") == "true" || font?.Italic == true;

        // Colour: the object's own (an attribute in some writers, the standard's FillColor element), its drawing
        // parameter, the layer's. Text drawn as outline only takes the stroke colour.
        var own = res.Draw(Attr(element, "DrawParam"));
        var layer = res.Draw(layerDraw);
        var color = Color(Attr(element, "FillColor")) ?? ColorOf(Child(element, "FillColor")) ?? own.Fill ?? layer.Fill;
        if (Attr(element, "Fill") == "false" && Attr(element, "Stroke") == "true")
            color = ColorOf(Child(element, "StrokeColor")) ?? own.Stroke ?? layer.Stroke ?? color;

        var scaleX = ctm.ScaleX;
        var fontSize = Math.Clamp(size * ctm.ScaleY, 0.1, 1000);
        var family = FontFamily(font?.Name);
        var format = new HCharFormat(
            Bold: bold ? true : null,
            Italic: italic ? true : null,
            Size: (int)(Math.Round(fontSize * 72 / 25.4 * 2) * 50),
            Font: family,
            EastAsianFont: family,
            Color: color);

        // A run of text (trimmed) drawn from (x, y) in the object's space: along the baseline, or down a column (run).
        void Add(string text, double x, double y, double width, double run, bool continued = false)
        {
            var (pageX, pageY) = ctm.Apply(x, y);
            if (page.Fragments.Count == MaxFragmentsPerPage)
                throw new InvalidDataException($"An OFD page has more than {MaxFragmentsPerPage:N0} pieces of text; Filee does not read it.");
            page.Fragments.Add(new Fragment(text, format, fontSize, template)
            {
                X = origin.X + box.X + pageX,
                Baseline = origin.Y + box.Y + pageY,
                Width = width * scaleX,
                Run = run * ctm.ScaleY,
                Continued = continued,
            });
        }

        double penX = 0, penY = 0;
        foreach (var code in Children(element, "TextCode"))
        {
            if (Glyphs(code, size, ref penX, ref penY) is not { } glyphs)
                continue;
            var (text, xs, ys, vertical) = glyphs;
            if (vertical)
            {
                var first = text.TakeWhile(char.IsWhiteSpace).Count();
                var last = text.Length - 1 - text.Reverse().TakeWhile(char.IsWhiteSpace).Count();
                if (first <= last)
                    Add(text[first..(last + 1)], xs[first], ys[first], size, ys[last] - ys[first]);
                continue;
            }

            // A step back or to another baseline starts a new line: some writers put a whole block (the cipher text of
            // an invoice's 密码区, four lines of 27 characters) into one TextCode.
            var lineStart = 0;
            for (var i = 1; i <= text.Length; i++)
            {
                if (i < text.Length && Math.Abs(ys[i] - ys[i - 1]) < 0.01 && xs[i] >= xs[i - 1] - 0.01)
                    continue;
                var segment = text[lineStart..i];
                var first = lineStart + segment.TakeWhile(char.IsWhiteSpace).Count();
                var last = i - 1 - segment.Reverse().TakeWhile(char.IsWhiteSpace).Count();
                if (first <= last)
                    Add(text[first..(last + 1)], xs[first], ys[first], xs[last] - xs[first] + Advance(text[last], size), 0, lineStart > 0);
                lineStart = i;
            }
        }
    }

    /// <summary>
    /// The characters of a TextCode and where each is drawn in its object's space; vertical when only DeltaY moves
    /// them (a column). A TextCode without X or Y goes on where the one before it ended (the pen). A DeltaX list
    /// shorter than the text goes on with its last value (writers give one value for a run of equal advances) while
    /// the characters are as wide as the last one it covers; without DeltaX the advance is estimated.
    /// </summary>
    private static (string Text, double[] Xs, double[] Ys, bool Vertical)? Glyphs(XElement code, double size, ref double penX, ref double penY)
    {
        var text = string.Concat(code.Value.Select(c => char.IsControl(c) ? ' ' : c));
        var x = Number(Attr(code, "X")) ?? penX;
        var y = Number(Attr(code, "Y")) ?? penY;
        if (text.Length == 0)
            return null;
        var dx = Deltas(Attr(code, "DeltaX"), text.Length - 1);
        var dy = Deltas(Attr(code, "DeltaY"), text.Length - 1);
        var vertical = dy is not null && dy.Any(d => d != 0) && (dx is null || dx.All(d => d == 0));

        var xs = new double[text.Length];
        var ys = new double[text.Length];
        (xs[0], ys[0]) = (x, y);
        for (var i = 1; i < text.Length; i++)
        {
            if (vertical)
            {
                xs[i] = x;
                ys[i] = ys[i - 1] + (i - 1 < dy!.Length ? dy[i - 1] : size);
                continue;
            }
            var step = dx is null ? Advance(text[i - 1], size)
                : i - 1 < dx.Length ? dx[i - 1]
                : dx[^1] > 0 && IsWide(text[i - 1]) == IsWide(text[dx.Length - 1]) ? dx[^1]
                : Advance(text[i - 1], size);
            xs[i] = xs[i - 1] + step;
            ys[i] = ys[i - 1] + (dy is not null && i - 1 < dy.Length ? dy[i - 1] : 0);
        }
        (penX, penY) = vertical ? (x, ys[^1] + size) : (xs[^1] + Advance(text[^1], size), ys[^1]);
        return (text, xs, ys, vertical);
    }

    /// <summary>
    /// DeltaX / DeltaY: the distances between the characters, with "g n d" standing for n times d. At most
    /// <paramref name="count"/> values are taken (a huge repeat count cannot allocate more than the text needs).
    /// </summary>
    private static double[]? Deltas(string? value, int count)
    {
        if (value is null || count <= 0)
            return null;
        var result = new List<double>();
        var tokens = value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length && result.Count < count; i++)
        {
            if (tokens[i] == "g" && i + 2 < tokens.Length
                && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeat)
                && Number(tokens[i + 2]) is { } repeated)
            {
                for (var k = 0; k < repeat && result.Count < count; k++)
                    result.Add(repeated);
                i += 2;
            }
            else if (Number(tokens[i]) is { } delta)
            {
                result.Add(delta);
            }
        }
        return result.Count > 0 ? [.. result] : null;
    }

    /// <summary>Advance of a character without DeltaX: full width for CJK, half for the rest (as Chinese fonts draw Latin).</summary>
    private static double Advance(char c, double size) => IsWide(c) ? size : size * 0.5;

    private static bool IsWide(char c) => c >= 0x2E80 && c is < '｡' or > 'ￜ';

    /// <summary>
    /// Single characters of a template stacked in one column (购买方信息, 备注 — the block titles of an invoice) or spread
    /// over one line (合　计) become one fragment, so they read as a word. Template content only: the page's own single
    /// characters are values in table cells that must stay apart, and a pair with anything between them (共 4 页, the
    /// page's number between the template's characters) stays apart too.
    /// </summary>
    private static void MergeTemplateLabels(List<Fragment> fragments)
    {
        var singles = fragments.Where(f => f.Template && f.Text.Length == 1 && IsWide(f.Text[0]) && !f.Vertical).ToList();
        var merged = new HashSet<Fragment>();

        // Stacked: same X, one under the other, at most three lines apart.
        foreach (var column in singles.GroupBy(f => Math.Round(f.X * 2) / 2))
        {
            var stack = new List<Fragment>();
            foreach (var fragment in column.OrderBy(f => f.Baseline).Append(null))
            {
                if (fragment is not null && stack.Count is > 0 and < 8 && Math.Abs(fragment.Size - stack[0].Size) < 0.1
                    && fragment.Baseline - stack[^1].Baseline > 0 && fragment.Baseline - stack[^1].Baseline < 3 * fragment.Size)
                {
                    stack.Add(fragment);
                    continue;
                }
                if (stack.Count >= 2)
                {
                    stack[0].Text = string.Concat(stack.Select(f => f.Text));
                    stack[0].Run = stack[^1].Baseline - stack[0].Baseline;
                    merged.UnionWith(stack.Skip(1));
                }
                stack = fragment is null ? [] : [fragment];
            }
        }

        // Spread: a pair of single characters on one baseline with nothing between them.
        var row = singles.Where(f => !merged.Contains(f) && !f.Vertical).OrderBy(f => f.Baseline).ThenBy(f => f.X).ToList();
        for (var i = 1; i < row.Count; i++)
        {
            var (left, right) = (row[i - 1], row[i]);
            var gap = right.Left - left.Right;
            if (left.Text.Length != 1 || Math.Abs(left.Baseline - right.Baseline) > 0.3 || Math.Abs(left.Size - right.Size) > 0.1
                || gap < -0.1 || gap > 8 * right.Size
                || fragments.Any(f => f != left && f != right && Math.Abs(f.Baseline - left.Baseline) < 0.5 * f.Size
                                      && (f.Left + f.Right) / 2 > left.Right - 0.1 && (f.Left + f.Right) / 2 < right.Left + 0.1))
            {
                continue;
            }
            left.Text += right.Text;
            left.Width = right.Right - left.Left;
            merged.Add(right);
            row[i] = left;
        }
        fragments.RemoveAll(merged.Contains);
    }

    /// <summary>English family names of common Chinese fonts, which Word and LibreOffice know on any system.</summary>
    private static readonly Dictionary<string, string> ChineseFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["宋体"] = "SimSun",
        ["新宋体"] = "NSimSun",
        ["黑体"] = "SimHei",
        ["楷体"] = "KaiTi",
        ["楷体_GB2312"] = "KaiTi_GB2312",
        ["仿宋"] = "FangSong",
        ["仿宋_GB2312"] = "FangSong_GB2312",
        ["微软雅黑"] = "Microsoft YaHei",
        ["等线"] = "DengXian",
        ["隶书"] = "LiSu",
        ["幼圆"] = "YouYuan",
        ["华文宋体"] = "STSong",
        ["华文中宋"] = "STZhongsong",
        ["华文楷体"] = "STKaiti",
        ["华文仿宋"] = "STFangsong",
        ["华文细黑"] = "STXihei",
        ["方正小标宋简体"] = "FZXiaoBiaoSong-B05S",
    };

    /// <summary>The family to write: subset prefixes ("ABCDEF+") and style suffixes (",Bold") off, Chinese names in English.</summary>
    private static string? FontFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
            name = name[7..];
        name = name.Split(',')[0].Trim();
        return name.Length == 0 ? null : ChineseFonts.GetValueOrDefault(name, name);
    }

    // ───────────────────────── Pictures ─────────────────────────

    private static void ImageObject(XElement element, Resources res, PageContent page, (double X, double Y) origin)
    {
        if (Attr(element, "ResourceID") is not { } id || res.Media(id) is not { } part)
            return;
        if (Box(Attr(element, "Boundary")) is not { } box)
            return;
        // The picture fills the unit square, which the CTM scales to its size within the boundary.
        var ctm = Matrix.Parse(Attr(element, "CTM"));
        var (left, top, width, height) = ctm != Matrix.Identity ? ctm.Bounds(box.X, box.Y) : box;
        if (width >= 0.5 && height >= 0.5 && width < 10_000 && height < 10_000)
            page.Pictures.Add(new Picture(part, null, origin.X + left, origin.Y + top, width, height));
    }

    /// <summary>A picture unpacked into the media folder (once per part), as a format the writers embed; null when
    /// it cannot be decoded (JBIG2, damaged) or is too large.</summary>
    private string? Extract(string part)
    {
        if (_extracted.TryGetValue(part, out var known))
            return known;
        string? file = null;
        if (_extracted.Count < MaxPictures && _package.Read(part, OfdPackage.MaxPictureBytes) is { } bytes)
            file = Save(bytes);
        _extracted[part] = file;
        return file;
    }

    private string? Save(byte[] bytes)
    {
        // The part name is never used for the file: the format comes from the bytes.
        var kind = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => "png",
            [0xFF, 0xD8, 0xFF, ..] => "jpg",
            [0x47, 0x49, 0x46, 0x38, ..] => "gif",
            [0x42, 0x4D, ..] => "bmp",
            [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => "tiff",
            [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, ..] => "jp2",
            [0xFF, 0x4F, 0xFF, 0x51, ..] => "j2k",
            _ => null,
        };
        if (kind is null)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        var name = Path.Combine(_mediaFolder, $"ofd-{Guid.NewGuid():N}");
        if (kind is "png" or "jpg" or "gif" or "bmp")
        {
            File.WriteAllBytes($"{name}.{kind}", bytes);
            return $"{name}.{kind}";
        }
        try
        {
            var settings = new MagickReadSettings
            {
                Format = kind switch { "tiff" => MagickFormat.Tiff, "jp2" => MagickFormat.Jp2, _ => MagickFormat.J2k },
            };
            var info = new MagickImageInfo(bytes, settings);
            if ((long)info.Width * info.Height > MaxPicturePixels)
                return null;
            using var image = new MagickImage(bytes, settings);
            image.Write($"{name}.png", MagickFormat.Png);
            return $"{name}.png";
        }
        catch (MagickException)
        {
            return null; // a picture Filee cannot decode is left out; the text still converts
        }
    }

    // ───────────────────────── Resources ─────────────────────────

    private sealed record FontInfo(string? Name, bool Bold, bool Italic);

    private sealed record DrawParam(string? Relative, string? Fill, string? Stroke, double? LineWidth);

    /// <summary>Resources by ID; a page's own come first, then the document's (<see cref="Parent"/>).</summary>
    private sealed class Resources(Resources? parent)
    {
        public Resources? Parent { get; } = parent;
        public Dictionary<string, FontInfo> Fonts { get; } = [];
        public Dictionary<string, string> MediaParts { get; } = [];
        public Dictionary<string, DrawParam> DrawParams { get; } = [];

        public FontInfo? Font(string id) => Fonts.GetValueOrDefault(id) ?? Parent?.Font(id);

        public string? Media(string id) => MediaParts.GetValueOrDefault(id) ?? Parent?.Media(id);

        /// <summary>Fill and stroke colour and line width of a drawing parameter, completed from the ones it is relative to.</summary>
        public (string? Fill, string? Stroke, double? LineWidth) Draw(string? id)
        {
            string? fill = null, stroke = null;
            double? width = null;
            for (var hops = 0; id is not null && hops < 8 && Find(id) is { } param; hops++)
            {
                fill ??= param.Fill;
                stroke ??= param.Stroke;
                width ??= param.LineWidth;
                id = param.Relative;
            }
            return (fill, stroke, width);
        }

        private DrawParam? Find(string id) => DrawParams.GetValueOrDefault(id) ?? Parent?.Find(id);
    }

    /// <summary>Reads a resource file into <paramref name="into"/> (IDs already there are kept).</summary>
    private void LoadRes(Resources into, string? name, string docDir)
    {
        if (_package.LoadXml(name)?.Root is not { } root)
            return;
        var resDir = OfdPackage.DirName(name!);
        // Media files are relative to the folder the Res element names (BaseLoc); writers differ, so the file's own
        // folder, its Res subfolder, the document and the package root are tried too.
        var baseDir = OfdPackage.Resolve(resDir, Attr(root, "BaseLoc")) ?? resDir;
        foreach (var element in root.Descendants())
        {
            if (Attr(element, "ID") is not { } id)
                continue;
            switch (element.Name.LocalName)
            {
                case "Font":
                    into.Fonts.TryAdd(id, new FontInfo(Attr(element, "FontName") ?? Attr(element, "FamilyName"),
                        Attr(element, "Bold") == "true", Attr(element, "Italic") == "true"));
                    break;
                case "MultiMedia" when Attr(element, "Type") is null || Attr(element, "Type")!.Equals("Image", StringComparison.OrdinalIgnoreCase):
                    var file = Attr(element, "MediaFile") ?? Child(element, "MediaFile")?.Value;
                    var part = new[] { baseDir, resDir, resDir.Length == 0 ? "Res" : resDir + "/Res", docDir, "" }
                        .Select(dir => OfdPackage.Resolve(dir, file)).FirstOrDefault(_package.Has);
                    if (part is not null)
                        into.MediaParts.TryAdd(id, part);
                    break;
                case "DrawParam":
                    into.DrawParams.TryAdd(id, new DrawParam(Attr(element, "Relative"),
                        Color(Attr(element, "FillColor")) ?? ColorOf(Child(element, "FillColor")),
                        Color(Attr(element, "StrokeColor")) ?? ColorOf(Child(element, "StrokeColor")),
                        Number(Attr(element, "LineWidth"))));
                    break;
            }
        }
    }

    // ───────────────────────── Small helpers ─────────────────────────

    /// <summary>A transformation "a b c d e f": (x, y) → (a·x + c·y + e, b·x + d·y + f).</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public static Matrix Parse(string? value)
        {
            var numbers = Numbers(value);
            if (numbers is not [var a, var b, var c, var d, var e, var f] || Math.Abs(a * d - b * c) < 1e-9)
                return Identity;
            return new Matrix(a, b, c, d, e, f);
        }

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        public double ScaleX => Math.Sqrt(A * A + B * B);
        public double ScaleY => Math.Sqrt(C * C + D * D);

        /// <summary>Box of the unit square after the transformation, moved by (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public (double Left, double Top, double Width, double Height) Bounds(double x, double y)
        {
            var corners = new[] { Apply(0, 0), Apply(1, 0), Apply(0, 1), Apply(1, 1) };
            var (left, top) = (corners.Min(p => p.X), corners.Min(p => p.Y));
            return (x + left, y + top, corners.Max(p => p.X) - left, corners.Max(p => p.Y) - top);
        }
    }

    /// <summary>A box "x y width height" in millimetres; null when missing or not four numbers.</summary>
    private static (double X, double Y, double Width, double Height)? Box(string? value) =>
        Numbers(value) is [var x, var y, var w, var h] && w >= 0 && h >= 0 ? (x, y, w, h) : null;

    private static double[]? Numbers(string? value)
    {
        if (value is null)
            return null;
        var parts = value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 16)
            return null;
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (Number(parts[i]) is not { } number)
                return null;
            numbers[i] = number;
        }
        return numbers;
    }

    /// <summary>A number written the XML way (invariant culture); null when it is none, not finite or absurdly large.</summary>
    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(number) <= 1e6 ? number : null;

    /// <summary>A colour element's Value ("R G B", grey, CMYK, channels in decimal or "#hex").</summary>
    private static string? ColorOf(XElement? element) => Color(Attr(element, "Value"));

    private static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is [var single] && single.StartsWith('#') && single.Length >= 7
            && int.TryParse(single[^6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return $"#{rgb:X6}"; // #RRGGBB or #AARRGGBB
        var channels = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var ok = parts[i].StartsWith('#')
                ? int.TryParse(parts[i][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out channels[i])
                : int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i]);
            if (!ok)
                return null;
            channels[i] = Math.Clamp(channels[i], 0, 255);
        }
        return channels switch
        {
            [var gray] => $"#{gray:X2}{gray:X2}{gray:X2}",
            [var r, var g, var b] => $"#{r:X2}{g:X2}{b:X2}",
            [var c, var m, var y, var k] => $"#{Cmyk(c, k):X2}{Cmyk(m, k):X2}{Cmyk(y, k):X2}",
            _ => null,
        };

        static int Cmyk(int channel, int black) => (int)Math.Round((255 - channel) * (255 - black) / 255.0);
    }

    private static string? Attr(XElement? element, string name) =>
        element?.Attribute(name)?.Value ?? element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? element, string name) =>
        element?.Elements().Where(e => e.Name.LocalName == name) ?? [];
}
