// Objects inside runs: pictures (DrawingML, inline or floating), text boxes and simple shapes (Word 2010 "wps"
// shapes inside mc:AlternateContent, as Word writes them), hyperlinks and bookmarks.
//
// Child order follows CT_Inline / CT_Anchor (extent, effectExtent, wrap, docPr, cNvGraphicFramePr, graphic) and
// CT_WordprocessingShape (cNvSpPr, spPr, txbx, bodyPr). Every drawing gets a document-wide unique docPr id.

using System.Globalization;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxWriter
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    /// <summary>Z order of floating objects (Word counts up from this value).</summary>
    private int _relativeHeight = 251659264;
    private int _textBoxDepth;

    // ───────────────────────── Pictures ─────────────────────────

    private string Picture(HImage image)
    {
        if (!File.Exists(image.Path) || MediaTarget(image.Path) is not { } target)
            return ""; // missing or unreadable picture: skip rather than write a broken reference

        var (width, height) = (image.Width, image.Height);
        if (width is null || height is null)
        {
            try
            {
                // Pixels at 96 dpi, like Word and browsers show pictures without a physical size.
                var info = new MagickImageInfo(image.Path);
                double pxW = info.Width, pxH = info.Height;
                if (pxW > 0 && pxH > 0)
                {
                    if (width is null && height is null)
                        (width, height) = ((int)(pxW * HwpxUnits.PerPixel), (int)(pxH * HwpxUnits.PerPixel));
                    else if (width is not null)
                        height = (int)(width.Value * pxH / pxW);
                    else
                        width = (int)(height!.Value * pxW / pxH);
                }
            }
            catch (MagickException)
            {
                // Unknown size: a square placeholder size below.
            }
        }
        var w = Math.Max(1, width ?? 8504);
        var h = Math.Max(1, height ?? w);
        if (image.Anchor is null && w > _width)
        {
            // Inline pictures never overflow the text width.
            h = (int)((long)h * _width / w);
            w = _width;
        }

        var relationship = _part.Relate(RelationshipBase + "image", target);
        var id = _nextDrawingId++;
        var name = $"Picture {id}";
        var graphic =
            $"<a:graphic><a:graphicData uri=\"{PictureUri}\"><pic:pic>" +
            $"<pic:nvPicPr><pic:cNvPr id=\"{Number(id)}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
            $"<pic:blipFill><a:blip r:embed=\"{relationship}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
            $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Number(Emu(w))}\" cy=\"{Number(Emu(h))}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
            "</pic:pic></a:graphicData></a:graphic>";
        return $"<w:r><w:drawing>{Frame(image.Anchor, w, h, id, name, graphic, "<a:graphicFrameLocks noChangeAspect=\"1\"/>")}</w:drawing></w:r>";
    }

    /// <summary>
    /// Zip path (relative to word/) of a picture, added to the package once. Formats Word may not show are converted
    /// to PNG when the package is written; null when the file is no picture Magick can read.
    /// </summary>
    private string? MediaTarget(string path)
    {
        if (_media.TryGetValue(path, out var known))
            return known.ZipPath["word/".Length..];
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension == "jpeg")
            extension = "jpg";
        if (!PictureTypes.ContainsKey(extension))
        {
            try
            {
                _ = new MagickImageInfo(path);
            }
            catch (MagickException)
            {
                return null;
            }
            extension = "png";
        }
        var zipPath = $"word/media/image{_media.Count + 1}.{extension}";
        _media[path] = (zipPath, extension);
        return zipPath["word/".Length..];
    }

    /// <summary>wp:inline for objects in the text, wp:anchor for floating ones.</summary>
    private string Frame(HAnchor? anchor, int width, int height, int id, string name, string graphic, string locks = "")
    {
        var extent = $"<wp:extent cx=\"{Number(Emu(width))}\" cy=\"{Number(Emu(height))}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>";
        var docPr = $"<wp:docPr id=\"{Number(id)}\" name=\"{name}\"/><wp:cNvGraphicFramePr>{locks}</wp:cNvGraphicFramePr>";
        if (anchor is null)
            return $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">{extent}{docPr}{graphic}</wp:inline>";

        var behind = anchor.Wrap == "BEHIND_TEXT";
        var wrap = anchor.Wrap switch
        {
            "SQUARE" => "<wp:wrapSquare wrapText=\"bothSides\"/>",
            "TOP_AND_BOTTOM" => "<wp:wrapTopAndBottom/>",
            _ => "<wp:wrapNone/>",
        };
        var horizontal = Position("positionH", anchor.HorizontalRelativeTo switch
        {
            "PAPER" => "page",
            "PAGE" => "margin",
            "PARA" => "character",
            _ => "column",
        }, anchor.HorizontalAlign, anchor.HorizontalOffset, "LEFT");
        var vertical = Position("positionV", anchor.VerticalRelativeTo switch
        {
            "PAPER" => "page",
            "PAGE" => "margin",
            _ => "paragraph",
        }, anchor.VerticalAlign, anchor.VerticalOffset, "TOP");
        return $"<wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"{Number(_relativeHeight++)}\" " +
               $"behindDoc=\"{(behind ? 1 : 0)}\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"{(anchor.AllowOverlap ? 1 : 0)}\">" +
               $"<wp:simplePos x=\"0\" y=\"0\"/>{horizontal}{vertical}{extent}{wrap}{docPr}{graphic}</wp:anchor>";
    }

    /// <summary>An aligned position (left, center, ...) without an offset, or an offset from the reference.</summary>
    private static string Position(string element, string relativeFrom, string align, int offset, string start)
    {
        var content = offset == 0 && align != start && align is "CENTER" or "RIGHT" or "BOTTOM" or "INSIDE" or "OUTSIDE"
            ? $"<wp:align>{align.ToLowerInvariant()}</wp:align>"
            : $"<wp:posOffset>{Number(Emu(offset))}</wp:posOffset>";
        return $"<wp:{element} relativeFrom=\"{relativeFrom}\">{content}</wp:{element}>";
    }

    // ───────────────────────── Text boxes and shapes ─────────────────────────

    private string TextBox(HTextBox box, RunContext context)
    {
        if (_textBoxDepth > 0)
        {
            // Word has no text boxes inside text boxes: keep the text in place.
            var text = new StringBuilder();
            foreach (var paragraph in box.Blocks.OfType<HParagraph>())
                Inlines(paragraph.Inlines.Where(i => i is HText or HLineBreak or HTab), text, context);
            return text.ToString();
        }

        var w = Math.Max(1, box.Width);
        var h = Math.Max(1, box.Height);
        var outerWidth = _width;
        _width = Math.Max(1000, w - box.Padding.Left - box.Padding.Right);
        _textBoxDepth++;
        string content;
        try
        {
            content = Blocks(box.Blocks, requireParagraph: true);
        }
        finally
        {
            _textBoxDepth--;
            _width = outerWidth;
        }

        var geometry = box.Shape == HShapeKind.Ellipse ? "ellipse" : box.CornerRatio > 0 ? "roundRect" : "rect";
        var anchor = box.VerticalAlign switch
        {
            HVerticalAlign.Center => "ctr",
            HVerticalAlign.Bottom => "b",
            _ => "t",
        };
        var shape =
            "<wps:wsp><wps:cNvSpPr txBox=\"1\"/>" +
            ShapeProperties(w, h, geometry, box.CornerRatio, box.Fill, box.Gradient, box.Line, flipH: false, flipV: false) +
            $"<wps:txbx><w:txbxContent>{content}</w:txbxContent></wps:txbx>" +
            $"<wps:bodyPr rot=\"0\" vert=\"horz\" wrap=\"square\" lIns=\"{Number(Emu(box.Padding.Left))}\" tIns=\"{Number(Emu(box.Padding.Top))}\" " +
            $"rIns=\"{Number(Emu(box.Padding.Right))}\" bIns=\"{Number(Emu(box.Padding.Bottom))}\" anchor=\"{anchor}\" anchorCtr=\"0\"><a:noAutofit/></wps:bodyPr>" +
            "</wps:wsp>";
        return DrawingRun(box.Anchor, w, h, "Text Box", shape);
    }

    private string Shape(HShape shape)
    {
        var w = Math.Max(1, shape.Width);
        var h = Math.Max(1, shape.Height);
        var geometry = shape.Kind switch
        {
            HShapeKind.Ellipse => "ellipse",
            HShapeKind.Line => "line",
            _ => shape.CornerRatio > 0 ? "roundRect" : "rect",
        };
        var line = shape.Kind == HShapeKind.Line;
        var xml =
            "<wps:wsp><wps:cNvSpPr/>" +
            ShapeProperties(w, h, geometry, shape.CornerRatio, line ? null : shape.Fill, line ? null : shape.Gradient, shape.Line, shape.FlipHorizontal, shape.FlipVertical) +
            "<wps:bodyPr/></wps:wsp>";
        return DrawingRun(shape.Anchor, w, h, "Shape", xml);
    }

    /// <summary>A wps shape in a run, inside mc:AlternateContent like Word writes it (Word 2007 would need VML).</summary>
    private string DrawingRun(HAnchor? anchor, int width, int height, string kind, string shape)
    {
        var id = _nextDrawingId++;
        var graphic = $"<a:graphic><a:graphicData uri=\"{ShapeUri}\">{shape}</a:graphicData></a:graphic>";
        return $"<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>{Frame(anchor, width, height, id, $"{kind} {id}", graphic)}</w:drawing></mc:Choice></mc:AlternateContent></w:r>";
    }

    /// <summary>wps:spPr: xfrm, preset geometry, fill (solid, linear gradient or none) and outline.</summary>
    private static string ShapeProperties(int width, int height, string geometry, int cornerRatio, string? fill, HGradient? gradient, HBorder line, bool flipH, bool flipV)
    {
        var sb = new StringBuilder("<wps:spPr>");
        sb.Append($"<a:xfrm{(flipH ? " flipH=\"1\"" : "")}{(flipV ? " flipV=\"1\"" : "")}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Number(Emu(width))}\" cy=\"{Number(Emu(height))}\"/></a:xfrm>");
        // 한글's corner ratio (50 = half circle) and DrawingML's "adj" (50000 = half the shorter side) scale alike.
        sb.Append(geometry == "roundRect"
            ? $"<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val {Number(Math.Clamp(cornerRatio, 0, 50) * 1000)}\"/></a:avLst></a:prstGeom>"
            : $"<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom>");
        if (gradient is { Colors.Count: >= 2 })
        {
            sb.Append("<a:gradFill rotWithShape=\"1\"><a:gsLst>");
            for (var i = 0; i < gradient.Colors.Count; i++)
            {
                var position = (int)Math.Round(100000.0 * i / (gradient.Colors.Count - 1));
                sb.Append($"<a:gs pos=\"{Number(position)}\"><a:srgbClr val=\"{Hex(gradient.Colors[i]) ?? "FFFFFF"}\"/></a:gs>");
            }
            sb.Append($"</a:gsLst><a:lin ang=\"{Number(((gradient.Angle % 360) + 360) % 360 * 60000)}\" scaled=\"0\"/></a:gradFill>");
        }
        else if (Hex(fill) is { } solid)
        {
            sb.Append($"<a:solidFill><a:srgbClr val=\"{solid}\"/></a:solidFill>");
        }
        else if (geometry != "line")
        {
            sb.Append("<a:noFill/>");
        }

        if (line.Style == HBorderStyle.None)
        {
            sb.Append("<a:ln><a:noFill/></a:ln>");
        }
        else
        {
            var dash = line.Style switch
            {
                HBorderStyle.Dash => "<a:prstDash val=\"dash\"/>",
                HBorderStyle.Dot => "<a:prstDash val=\"sysDot\"/>",
                HBorderStyle.DashDot => "<a:prstDash val=\"dashDot\"/>",
                _ => "",
            };
            var widthEmu = Math.Max(3175, (long)Math.Round(line.WidthMm * 36000));
            sb.Append($"<a:ln w=\"{Number(widthEmu)}\"{(line.Style == HBorderStyle.Double ? " cmpd=\"dbl\"" : "")}><a:solidFill><a:srgbClr val=\"{Hex(line.Color) ?? "000000"}\"/></a:solidFill>{dash}</a:ln>");
        }
        return sb.Append("</wps:spPr>").ToString();
    }

    // ───────────────────────── Links and bookmarks ─────────────────────────

    private void Hyperlink(HLink link, StringBuilder sb, RunContext context)
    {
        var content = new StringBuilder();
        Inlines(link.Content, content, context with { Link = true });
        if (content.Length == 0)
            return;
        if (link.Target.StartsWith('#'))
        {
            sb.Append($"<w:hyperlink w:anchor=\"{Escape(BookmarkName(link.Target[1..]))}\" w:history=\"1\">").Append(content).Append("</w:hyperlink>");
        }
        else if (link.Target.Length > 0)
        {
            var id = _part.Relate(RelationshipBase + "hyperlink", SafeUri(link.Target), external: true);
            sb.Append($"<w:hyperlink r:id=\"{id}\" w:history=\"1\">").Append(content).Append("</w:hyperlink>");
        }
        else
        {
            sb.Append(content);
        }
    }

    /// <summary>A relationship target must be a URI: absolute ones are normalised, spaces in others escaped.</summary>
    private static string SafeUri(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
            return uri.AbsoluteUri;
        return target.Replace("\\", "/", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal);
    }

    /// <summary>
    /// Word bookmark names start with a letter, hold letters, digits and underscores and are at most 40 characters.
    /// Names are mapped once, so a link to "#name" finds the bookmark written for "name".
    /// </summary>
    private string BookmarkName(string name)
    {
        if (_bookmarks.TryGetValue(name, out var known))
            return known;
        var clean = new StringBuilder();
        foreach (var ch in name)
            clean.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (clean.Length == 0 || !char.IsLetter(clean[0]))
            clean.Insert(0, 'b');
        var baseName = clean.Length > 36 ? clean.ToString(0, 36) : clean.ToString();
        var result = baseName;
        for (var i = 2; !_bookmarkNames.Add(result); i++)
            result = baseName + i.ToString(CultureInfo.InvariantCulture);
        _bookmarks[name] = result;
        return result;
    }
}
