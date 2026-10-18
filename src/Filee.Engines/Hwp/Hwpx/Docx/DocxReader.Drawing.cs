// DOCX graphic objects: DrawingML pictures, text boxes (wps), groups and drawing canvases, plus legacy VML
// (w:pict / w:object) used by older documents and as the fallback of mc:AlternateContent.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    private static readonly XNamespace Wpc = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";
    private static readonly XNamespace W10 = "urn:schemas-microsoft-com:office:word";

    /// <summary>Default DrawingML text box insets: 0.1" left/right, 0.05" top/bottom.</summary>
    private static readonly HInsets DefaultTextBoxPadding = new(720, 720, 360, 360);

    private readonly Dictionary<string, string> _themeColors = [];

    private IEnumerable<HInline> Drawing(XElement drawing)
    {
        foreach (var container in drawing.Elements())
        {
            var anchored = container.Name == Wp + "anchor";
            if (!anchored && container.Name != Wp + "inline")
                continue;
            var extent = container.Element(Wp + "extent");
            var width = HwpxUnits.FromEmu(Long(extent, "cx") ?? 0);
            var height = HwpxUnits.FromEmu(Long(extent, "cy") ?? 0);
            var anchor = anchored ? Anchor(container) : null;
            var data = container.Element(A + "graphic")?.Element(A + "graphicData");
            foreach (var inline in GraphicObjects(data?.Elements() ?? [], width, height, anchor, (0, 0, 1, 1)))
                yield return inline;
        }
    }

    /// <summary>
    /// Pictures and text boxes of a graphic frame. Members of groups and canvases become separate objects placed at
    /// their position inside the group (<paramref name="groupTransform"/>: offset and scale of the child coordinates).
    /// </summary>
    private IEnumerable<HInline> GraphicObjects(IEnumerable<XElement> objects, int width, int height, HAnchor? anchor,
        (double X, double Y, double ScaleX, double ScaleY) groupTransform)
    {
        foreach (var obj in objects)
        {
            if (obj.Name == Pic + "pic")
            {
                if (Picture(obj, width, height, anchor) is { } picture)
                    yield return picture;
            }
            else if (obj.Name == Wps + "wsp")
            {
                if (Shape(obj, width, height, anchor) is { } shape)
                    yield return shape;
            }
            else if (obj.Name == Wpg + "wgp" || obj.Name == Wpg + "grpSp" || obj.Name == Wpc + "wpc")
            {
                foreach (var inline in Group(obj, width, height, anchor, groupTransform))
                    yield return inline;
            }
        }
    }

    private IEnumerable<HInline> Group(XElement group, int width, int height, HAnchor? anchor,
        (double X, double Y, double ScaleX, double ScaleY) parent)
    {
        var xfrm = group.Element(Wpg + "grpSpPr")?.Element(A + "xfrm") ?? group.Element(A + "grpSpPr")?.Element(A + "xfrm");
        var chOff = xfrm?.Element(A + "chOff");
        var chExt = xfrm?.Element(A + "chExt");
        // Child coordinates → HWPUNIT relative to the group's top-left corner.
        var scaleX = (Long(chExt, "cx") ?? 0) > 0 ? width / (double)Long(chExt, "cx")! : 1 / 127.0;
        var scaleY = (Long(chExt, "cy") ?? 0) > 0 ? height / (double)Long(chExt, "cy")! : 1 / 127.0;
        var originX = Long(chOff, "x") ?? 0;
        var originY = Long(chOff, "y") ?? 0;

        foreach (var child in group.Elements())
        {
            var childXfrm = child.Descendants(A + "xfrm").FirstOrDefault();
            var off = childXfrm?.Element(A + "off");
            var childExt = childXfrm?.Element(A + "ext");
            var x = (int)(((Long(off, "x") ?? 0) - originX) * scaleX);
            var y = (int)(((Long(off, "y") ?? 0) - originY) * scaleY);
            var childWidth = (int)((Long(childExt, "cx") ?? 0) * scaleX);
            var childHeight = (int)((Long(childExt, "cy") ?? 0) * scaleY);

            // An inline group becomes objects floating over the paragraph at the same place.
            var childAnchor = anchor is null
                ? new HAnchor("COLUMN", "LEFT", x, "PARA", "TOP", y, "IN_FRONT_OF_TEXT", true)
                : anchor with
                {
                    HorizontalOffset = anchor.HorizontalOffset + AlignedOffset(anchor.HorizontalAlign, x, childWidth, width),
                    VerticalOffset = anchor.VerticalOffset + AlignedOffset(anchor.VerticalAlign, y, childHeight, height),
                    AllowOverlap = true,
                };
            foreach (var inline in GraphicObjects([child], childWidth, childHeight, childAnchor, parent))
                yield return inline;
        }
    }

    /// <summary>
    /// Offset of a group member when the group is aligned (centered, right-aligned ...) rather than placed at an
    /// offset: the member keeps its distance from the group's center or far edge.
    /// </summary>
    private static int AlignedOffset(string align, int position, int size, int groupSize) => align switch
    {
        "CENTER" => position + size / 2 - groupSize / 2,
        "RIGHT" or "BOTTOM" => position + size - groupSize,
        _ => position,
    };

    private HImage? Picture(XElement pic, int width, int height, HAnchor? anchor)
    {
        var blip = pic.Descendants(A + "blip").FirstOrDefault();
        var path = ExtractImage(Attr(blip, "embed", R));
        return path is null ? null : new HImage(path) { Width = width > 0 ? width : null, Height = height > 0 ? height : null, Anchor = anchor };
    }

    /// <summary>A text box, or a rectangle / ellipse / line without text. Other geometries are skipped.</summary>
    private HInline? Shape(XElement wsp, int width, int height, HAnchor? anchor)
    {
        var spPr = wsp.Element(Wps + "spPr");
        var style = wsp.Element(Wps + "style");
        var (fill, gradient) = FillOf(spPr, style);
        var geometry = Attr(spPr?.Element(A + "prstGeom"), "prst");
        if (wsp.Element(Wps + "txbx")?.Element(W + "txbxContent") is { } content)
        {
            if (geometry is "rect" or "roundRect" or "flowChartProcess" or "flowChartAlternateProcess" or "ellipse" or "flowChartConnector")
            {
                var framed = TextBox(wsp, content, width, height, anchor, fill ?? gradient?.Colors[0]);
                if (geometry is "ellipse" or "flowChartConnector")
                    framed.Shape = HShapeKind.Ellipse;
                else if (geometry is "roundRect" or "flowChartAlternateProcess")
                    framed.CornerRatio = 20;
                return framed;
            }
            // Text on a free-form or decorative shape: keep the text, drop the outline 한글 cannot draw.
            if (string.IsNullOrWhiteSpace(content.Value))
                return null;
            var box = TextBox(wsp, content, width, height, anchor, fill: null);
            box.Line = HBorder.None;
            return box;
        }

        HShapeKind? kind = geometry switch
        {
            "rect" or "roundRect" or "snip1Rect" or "snip2SameRect" or "round1Rect" or "round2SameRect"
                or "flowChartProcess" or "flowChartAlternateProcess" or "frame" => HShapeKind.Rectangle,
            "ellipse" or "flowChartConnector" => HShapeKind.Ellipse,
            "line" or "straightConnector1" => HShapeKind.Line,
            _ => null, // free-form and other preset shapes have no 한글 equivalent
        };
        if (kind is null)
            return null;
        var xfrm = spPr?.Element(A + "xfrm");
        return new HShape(kind.Value)
        {
            Width = width,
            Height = height,
            Anchor = anchor,
            Fill = kind == HShapeKind.Line ? null : fill,
            Gradient = kind == HShapeKind.Line ? null : gradient,
            Line = Line(spPr?.Element(A + "ln"), style),
            CornerRatio = geometry is "roundRect" or "round1Rect" or "round2SameRect" or "flowChartAlternateProcess" ? 20 : 0,
            FlipHorizontal = Attr(xfrm, "flipH") is "1" or "true",
            FlipVertical = Attr(xfrm, "flipV") is "1" or "true",
        };
    }

    private HTextBox TextBox(XElement wsp, XElement content, int width, int height, HAnchor? anchor, string? fill)
    {
        var spPr = wsp.Element(Wps + "spPr");
        var bodyPr = wsp.Element(Wps + "bodyPr");
        var box = new TextBoxBuilder(width, height, anchor)
        {
            Fill = fill,
            Line = Line(spPr?.Element(A + "ln"), wsp.Element(Wps + "style")),
            Padding = new HInsets(
                Inset(bodyPr, "lIns", DefaultTextBoxPadding.Left), Inset(bodyPr, "rIns", DefaultTextBoxPadding.Right),
                Inset(bodyPr, "tIns", DefaultTextBoxPadding.Top), Inset(bodyPr, "bIns", DefaultTextBoxPadding.Bottom)),
            VerticalAlign = Attr(bodyPr, "anchor") switch
            {
                "ctr" => HVerticalAlign.Center,
                "b" => HVerticalAlign.Bottom,
                _ => HVerticalAlign.Top,
            },
        }.Build();
        ReadTextBoxContent(content, box.Blocks);
        return box;
    }

    /// <summary>Text box paragraphs; Word's document grid does not apply inside text boxes.</summary>
    private void ReadTextBoxContent(XElement content, List<HBlock> output)
    {
        _textBoxDepth++;
        try
        {
            ReadBlocks(content.Elements(), output);
        }
        finally
        {
            _textBoxDepth--;
        }
    }

    /// <summary>Collects text box settings before the HTextBox (whose Blocks list is read-only) is created.</summary>
    private sealed class TextBoxBuilder(int width, int height, HAnchor? anchor)
    {
        public string? Fill { get; init; }
        public HBorder Line { get; init; } = HBorder.Thin;
        public HInsets Padding { get; init; } = DefaultTextBoxPadding;
        public HVerticalAlign VerticalAlign { get; init; }

        public HTextBox Build() => new()
        {
            Width = Math.Max(width, 1000),
            Height = Math.Max(height, 600),
            Anchor = anchor,
            Fill = Fill,
            Line = Line,
            Padding = Padding,
            VerticalAlign = VerticalAlign,
        };
    }

    private static int Inset(XElement? bodyPr, string name, int fallback) =>
        Long(bodyPr, name) is { } emu ? HwpxUnits.FromEmu(emu) : fallback;

    /// <summary>Solid colour or linear gradient of a shape; without its own fill the shape style decides.</summary>
    private (string? Solid, HGradient? Gradient) FillOf(XElement? spPr, XElement? style)
    {
        if (spPr?.Element(A + "noFill") is not null)
            return (null, null);
        if (spPr?.Element(A + "solidFill") is { } solid)
            return (Color(solid), null);
        if (spPr?.Element(A + "gradFill") is { } gradient)
        {
            var colors = gradient.Element(A + "gsLst")?.Elements(A + "gs")
                .OrderBy(gs => Long(gs, "pos") ?? 0)
                .Select(Color)
                .OfType<string>()
                .ToList() ?? [];
            var angle = (int)((Long(gradient.Element(A + "lin"), "ang") ?? 0) / 60000);
            return colors.Count >= 2 ? (null, new HGradient(colors, angle)) : (colors.FirstOrDefault(), null);
        }
        var fillRef = style?.Element(A + "fillRef");
        return fillRef is not null && Attr(fillRef, "idx") is not (null or "0") ? (Color(fillRef), null) : (null, null);
    }

    private HBorder Line(XElement? ln, XElement? style)
    {
        if (ln is null)
        {
            // No explicit outline: the shape style decides (lnRef idx 0 = no line).
            var lnRef = style?.Element(A + "lnRef");
            return lnRef is not null && Attr(lnRef, "idx") is not (null or "0")
                ? HBorder.Thin with { Color = Color(lnRef) ?? "#000000" }
                : HBorder.None;
        }
        if (ln.Element(A + "noFill") is not null)
            return HBorder.None;
        var widthMm = Long(ln, "w") is { } emu ? emu / 36000.0 : 0.12;
        var dash = Attr(ln.Element(A + "prstDash"), "val");
        var borderStyle = dash switch
        {
            null or "solid" => HBorderStyle.Solid,
            "dot" or "sysDot" => HBorderStyle.Dot,
            "dashDot" or "sysDashDot" or "lgDashDot" => HBorderStyle.DashDot,
            _ => HBorderStyle.Dash,
        };
        return new HBorder(borderStyle, widthMm, Color(ln.Element(A + "solidFill")) ?? "#000000");
    }

    /// <summary>Colour of a fill element: srgbClr, sysClr or a theme colour (luminance modifiers are ignored).</summary>
    private string? Color(XElement? fill)
    {
        if (fill is null)
            return null;
        if (fill.Element(A + "srgbClr") is { } rgb)
            return "#" + (Val(rgb) ?? "000000").ToUpperInvariant();
        if (fill.Element(A + "sysClr") is { } sys)
            return "#" + (Attr(sys, "lastClr") ?? "000000").ToUpperInvariant();
        if (fill.Element(A + "schemeClr") is { } scheme)
        {
            var name = Val(scheme) switch
            {
                "bg1" => "lt1",
                "tx1" => "dk1",
                "bg2" => "lt2",
                "tx2" => "dk2",
                var other => other ?? "dk1",
            };
            return _themeColors.GetValueOrDefault(name, name.StartsWith("lt", StringComparison.Ordinal) ? "#FFFFFF" : "#000000");
        }
        return null;
    }

    private void LoadThemeColors(Part? theme)
    {
        foreach (var color in theme?.Xml.Descendants(A + "clrScheme").FirstOrDefault()?.Elements() ?? [])
        {
            var value = Val(color.Element(A + "srgbClr")) ?? Attr(color.Element(A + "sysClr"), "lastClr");
            if (value is { Length: 6 })
                _themeColors[color.Name.LocalName] = "#" + value.ToUpperInvariant();
        }
    }

    /// <summary>Position of a floating DrawingML object (wp:anchor).</summary>
    private static HAnchor Anchor(XElement anchor)
    {
        var horizontal = anchor.Element(Wp + "positionH");
        var vertical = anchor.Element(Wp + "positionV");
        var wrap = anchor.Elements().FirstOrDefault(e => e.Name.Namespace == Wp && e.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal))?.Name.LocalName;
        var behind = Attr(anchor, "behindDoc") is "1" or "true";
        return new HAnchor(
            HorizontalRelativeTo: Attr(horizontal, "relativeFrom") switch
            {
                "page" or "leftMargin" or "rightMargin" or "insideMargin" or "outsideMargin" => "PAPER",
                "margin" => "PAGE",
                "character" => "PARA",
                _ => "COLUMN",
            },
            HorizontalAlign: horizontal?.Element(Wp + "align")?.Value switch
            {
                "center" => "CENTER",
                "right" => "RIGHT",
                "inside" => "INSIDE",
                "outside" => "OUTSIDE",
                _ => "LEFT",
            },
            HorizontalOffset: OffsetOf(horizontal),
            VerticalRelativeTo: Attr(vertical, "relativeFrom") switch
            {
                "page" or "topMargin" or "bottomMargin" or "insideMargin" or "outsideMargin" => "PAPER",
                "margin" => "PAGE",
                _ => "PARA",
            },
            VerticalAlign: vertical?.Element(Wp + "align")?.Value switch
            {
                "center" => "CENTER",
                "bottom" => "BOTTOM",
                "inside" => "INSIDE",
                "outside" => "OUTSIDE",
                _ => "TOP",
            },
            VerticalOffset: OffsetOf(vertical),
            Wrap: wrap switch
            {
                "wrapSquare" or "wrapTight" or "wrapThrough" => "SQUARE",
                "wrapTopAndBottom" => "TOP_AND_BOTTOM",
                _ => behind ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
            },
            AllowOverlap: Attr(anchor, "allowOverlap") is not ("0" or "false"));
    }

    private static int OffsetOf(XElement? position) =>
        long.TryParse(position?.Element(Wp + "posOffset")?.Value, out var emu) ? HwpxUnits.FromEmu(emu) : 0;

    private string? ExtractImage(string? relationshipId)
    {
        if (relationshipId is null || !_part.Relationships.TryGetValue(relationshipId, out var rel) || rel.External)
            return null;
        if (_media.TryGetValue(rel.Target, out var existing))
            return existing;
        if (_zip.GetEntry(rel.Target) is not { } entry)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        var path = Path.Combine(_mediaFolder, $"docx-media-{_media.Count + 1}{Path.GetExtension(rel.Target)}");
        using (var source = entry.Open())
        using (var target = File.Create(path))
            source.CopyTo(target);
        _media[rel.Target] = path;
        return path;
    }

    // ───────────────────────── VML (legacy) ─────────────────────────

    private IEnumerable<HInline> Vml(XElement container)
    {
        foreach (var shape in container.Elements())
        {
            if (shape.Name.Namespace != V)
                continue;
            if (shape.Name.LocalName == "group")
            {
                foreach (var inline in Vml(shape))
                    yield return inline;
                continue;
            }
            if (shape.Name.LocalName is "shapetype")
                continue;

            var style = VmlStyle((string?)shape.Attribute("style"));
            var width = HwpxUnits.ParseLength(style.GetValueOrDefault("width")) ?? 0;
            var height = HwpxUnits.ParseLength(style.GetValueOrDefault("height")) ?? 0;
            var anchor = style.GetValueOrDefault("position") == "absolute" ? VmlAnchor(shape, style) : null;

            if (shape.Element(V + "imagedata") is { } imageData && ExtractImage(Attr(imageData, "id", R)) is { } path)
            {
                yield return new HImage(path) { Width = width > 0 ? width : null, Height = height > 0 ? height : null, Anchor = anchor };
            }
            else if (shape.Element(V + "textbox")?.Element(W + "txbxContent") is { } content)
            {
                var box = new TextBoxBuilder(width, height, anchor)
                {
                    Fill = VmlFill(shape),
                    Line = VmlLine(shape),
                }.Build();
                ReadTextBoxContent(content, box.Blocks);
                yield return box;
            }
            else if (shape.Name.LocalName is "rect" or "roundrect" or "oval" or "line")
            {
                var kind = shape.Name.LocalName switch
                {
                    "oval" => HShapeKind.Ellipse,
                    "line" => HShapeKind.Line,
                    _ => HShapeKind.Rectangle,
                };
                if (kind == HShapeKind.Line)
                {
                    // v:line has from="x,y" to="x,y" instead of a size.
                    var from = VmlPoint((string?)shape.Attribute("from"));
                    var to = VmlPoint((string?)shape.Attribute("to"));
                    width = Math.Abs(to.X - from.X);
                    height = Math.Abs(to.Y - from.Y);
                    var lineAnchor = anchor ?? VmlAnchor(shape, style);
                    anchor = lineAnchor with
                    {
                        HorizontalOffset = lineAnchor.HorizontalOffset + Math.Min(from.X, to.X),
                        VerticalOffset = lineAnchor.VerticalOffset + Math.Min(from.Y, to.Y),
                    };
                }
                yield return new HShape(kind)
                {
                    Width = width,
                    Height = height,
                    Anchor = anchor,
                    Fill = kind == HShapeKind.Line ? null : VmlFill(shape),
                    Line = VmlLine(shape),
                    CornerRatio = shape.Name.LocalName == "roundrect" ? 20 : 0,
                };
            }
        }
    }

    private static string? VmlFill(XElement shape) =>
        (string?)shape.Attribute("filled") is "f" or "false" ? null : VmlColor((string?)shape.Attribute("fillcolor")) ?? "#FFFFFF";

    private static HBorder VmlLine(XElement shape) =>
        (string?)shape.Attribute("stroked") is "f" or "false"
            ? HBorder.None
            : HBorder.Thin with
            {
                Color = VmlColor((string?)shape.Attribute("strokecolor")) ?? "#000000",
                WidthMm = HwpxUnits.ParseLength((string?)shape.Attribute("strokeweight")) is { } weight ? weight / HwpxUnits.PerMm : 0.12,
            };

    /// <summary>"x,y" of a VML line end; bare numbers are points.</summary>
    private static (int X, int Y) VmlPoint(string? point)
    {
        var parts = (point ?? "0,0").Split(',');
        int Coordinate(int index)
        {
            var text = parts.ElementAtOrDefault(index)?.Trim() ?? "0";
            if (text.Length == 0)
                text = "0";
            return HwpxUnits.ParseLength(char.IsDigit(text[^1]) ? text + "pt" : text) ?? 0;
        }
        return (Coordinate(0), Coordinate(1));
    }

    private static Dictionary<string, string> VmlStyle(string? style) =>
        (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim());

    private static HAnchor VmlAnchor(XElement shape, Dictionary<string, string> style)
    {
        var wrap = (string?)shape.Element(W10 + "wrap")?.Attribute("type");
        return new HAnchor(
            HorizontalRelativeTo: style.GetValueOrDefault("mso-position-horizontal-relative") switch
            {
                "page" => "PAPER",
                "margin" => "PAGE",
                "char" => "PARA",
                _ => "COLUMN",
            },
            HorizontalAlign: style.GetValueOrDefault("mso-position-horizontal") switch
            {
                "center" => "CENTER",
                "right" => "RIGHT",
                _ => "LEFT",
            },
            HorizontalOffset: HwpxUnits.ParseLength(style.GetValueOrDefault("margin-left")) ?? 0,
            VerticalRelativeTo: style.GetValueOrDefault("mso-position-vertical-relative") switch
            {
                "page" => "PAPER",
                "margin" => "PAGE",
                _ => "PARA",
            },
            VerticalAlign: style.GetValueOrDefault("mso-position-vertical") switch
            {
                "center" => "CENTER",
                "bottom" => "BOTTOM",
                _ => "TOP",
            },
            VerticalOffset: HwpxUnits.ParseLength(style.GetValueOrDefault("margin-top")) ?? 0,
            Wrap: wrap switch
            {
                "square" or "tight" or "through" => "SQUARE",
                "topAndBottom" => "TOP_AND_BOTTOM",
                _ => style.TryGetValue("z-index", out var z) && z.StartsWith('-') ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
            },
            AllowOverlap: true);
    }

    /// <summary>VML colours: "#RRGGBB", "#RGB" or "#RRGGBB [index]".</summary>
    private static string? VmlColor(string? value)
    {
        var color = value?.Split(' ')[0].Trim();
        if (color is null || !color.StartsWith('#'))
            return null;
        if (color.Length == 4)
            color = $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}";
        return color.Length == 7 && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, null, out _) ? color.ToUpperInvariant() : null;
    }
}
