// Objects inside runs: pictures (inline or floating), text boxes, footnotes/endnotes, hyperlinks, page numbers.
// Child order follows files saved by 한글: object-specific children first, then sz / pos / outMargin.

using System.Text;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxWriter
{
    /// <summary>The fieldid 한글 gives every hyperlink field (control id "%hlk").</summary>
    private const string HyperlinkFieldId = "627600491";

    private const string IdentityRendering =
        "<hp:renderingInfo><hc:transMatrix e1=\"1\" e2=\"0\" e3=\"0\" e4=\"0\" e5=\"1\" e6=\"0\"/><hc:scaMatrix e1=\"1\" e2=\"0\" e3=\"0\" e4=\"0\" e5=\"1\" e6=\"0\"/><hc:rotMatrix e1=\"1\" e2=\"0\" e3=\"0\" e4=\"0\" e5=\"1\" e6=\"0\"/></hp:renderingInfo>";

    private int _footnotes;
    private int _endnotes;
    private int _zOrder;

    // ───────────────────────── Pictures ─────────────────────────

    private string? Picture(HImage image)
    {
        if (!File.Exists(image.Path))
            return null; // missing image: skip rather than produce a broken reference

        var (width, height) = (image.Width, image.Height);
        if (width is null || height is null)
        {
            try
            {
                var info = new MagickImageInfo(image.Path);
                // Pixels at 96 dpi, like Word and browsers show images without a physical size.
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
                // Unknown format: fall through to a square placeholder size.
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

        var extension = Path.GetExtension(image.Path).TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" or "jpe" => "jpg",
            var e => e,
        };
        var existing = _images.FindIndex(i => i.SourcePath == image.Path);
        var binaryId = existing >= 0 ? _images[existing].Id : $"image{_images.Count + 1}";
        if (existing < 0)
            _images.Add((binaryId, image.Path, extension));

        var id = NextId();
        var wrap = image.Anchor?.Wrap ?? "TOP_AND_BOTTOM";
        return $"<hp:pic id=\"{id}\" zOrder=\"{_zOrder++}\" numberingType=\"{(image.Anchor is null ? "NONE" : "PICTURE")}\" textWrap=\"{wrap}\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"{id}\" reverse=\"0\">" +
               "<hp:offset x=\"0\" y=\"0\"/>" +
               $"<hp:orgSz width=\"{w}\" height=\"{h}\"/><hp:curSz width=\"{w}\" height=\"{h}\"/>" +
               "<hp:flip horizontal=\"0\" vertical=\"0\"/>" +
               $"<hp:rotationInfo angle=\"0\" centerX=\"{w / 2}\" centerY=\"{h / 2}\" rotateimage=\"1\"/>" +
               IdentityRendering +
               $"<hc:img binaryItemIDRef=\"{binaryId}\" bright=\"0\" contrast=\"0\" effect=\"REAL_PIC\" alpha=\"0\"/>" +
               $"<hp:imgRect><hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"{w}\" y=\"0\"/><hc:pt2 x=\"{w}\" y=\"{h}\"/><hc:pt3 x=\"0\" y=\"{h}\"/></hp:imgRect>" +
               "<hp:imgClip left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:inMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>" +
               "<hp:imgDim dimwidth=\"0\" dimheight=\"0\"/><hp:effects/>" +
               $"<hp:sz width=\"{w}\" widthRelTo=\"ABSOLUTE\" height=\"{h}\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" +
               Position(image.Anchor) +
               "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:shapeComment/>" +
               "</hp:pic>";
    }

    /// <summary>hp:pos: inline objects are laid out like a character; floating ones use the anchor.</summary>
    private string Position(HAnchor? anchor)
    {
        if (anchor is null)
            return "<hp:pos treatAsChar=\"1\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"1\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/>";
        anchor = NonNegative(anchor);
        var flowWithText = anchor.VerticalRelativeTo == "PARA" ? 1 : 0;
        return $"<hp:pos treatAsChar=\"0\" affectLSpacing=\"0\" flowWithText=\"{flowWithText}\" allowOverlap=\"{(anchor.AllowOverlap ? 1 : 0)}\" holdAnchorAndSO=\"0\" " +
               $"vertRelTo=\"{anchor.VerticalRelativeTo}\" horzRelTo=\"{anchor.HorizontalRelativeTo}\" vertAlign=\"{anchor.VerticalAlign}\" horzAlign=\"{anchor.HorizontalAlign}\" " +
               $"vertOffset=\"{anchor.VerticalOffset}\" horzOffset=\"{anchor.HorizontalOffset}\"/>";
    }

    /// <summary>
    /// Object offsets are unsigned in the 한글 format (HWPUNIT), and readers fail on negative values. An object
    /// reaching into the margin is re-anchored to the paper edge, which keeps it in place; one above its paragraph
    /// (which has no paper-relative equivalent) moves down to the paragraph.
    /// </summary>
    private HAnchor NonNegative(HAnchor anchor)
    {
        if (anchor.HorizontalOffset < 0)
        {
            anchor = anchor.HorizontalRelativeTo is "PAGE" or "COLUMN" && anchor.HorizontalAlign == "LEFT"
                ? anchor with { HorizontalRelativeTo = "PAPER", HorizontalOffset = Math.Max(0, _pageLeft + anchor.HorizontalOffset) }
                : anchor with { HorizontalOffset = 0 };
        }
        if (anchor.VerticalOffset < 0)
        {
            anchor = anchor.VerticalRelativeTo == "PAGE" && anchor.VerticalAlign == "TOP"
                ? anchor with { VerticalRelativeTo = "PAPER", VerticalOffset = Math.Max(0, _pageTop + anchor.VerticalOffset) }
                : anchor with { VerticalOffset = 0 };
        }
        return anchor;
    }

    // ───────────────────────── Text boxes ─────────────────────────

    private string TextBox(HTextBox box)
    {
        var id = NextId();
        var (w, h) = (box.Width, box.Height);
        var verticalAlign = box.VerticalAlign switch
        {
            HVerticalAlign.Center => "CENTER",
            HVerticalAlign.Bottom => "BOTTOM",
            _ => "TOP",
        };

        // Readers lay out (and align) the text within textWidth, so it is the box minus its margins.
        var outerWidth = _width;
        _width = Math.Max(1000, w - box.Padding.Left - box.Padding.Right);
        var content = SubList(box.Blocks, verticalAlign, _width, Math.Max(0, h - box.Padding.Top - box.Padding.Bottom));
        _width = outerWidth;

        var ellipse = box.Shape == HShapeKind.Ellipse;
        var (element, extra) = ellipse
            ? ("ellipse", " intervalDirty=\"0\" hasArcPr=\"0\" arcType=\"NORMAL\"")
            : ("rect", $" ratio=\"{box.CornerRatio}\"");
        var sb = new StringBuilder();
        sb.Append($"<hp:{element} id=\"{id}\" zOrder=\"{_zOrder++}\" numberingType=\"NONE\" textWrap=\"{box.Anchor?.Wrap ?? "TOP_AND_BOTTOM"}\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"{id}\"{extra}>");
        sb.Append("<hp:offset x=\"0\" y=\"0\"/>");
        sb.Append($"<hp:orgSz width=\"{w}\" height=\"{h}\"/><hp:curSz width=\"0\" height=\"0\"/>");
        sb.Append("<hp:flip horizontal=\"0\" vertical=\"0\"/>");
        sb.Append($"<hp:rotationInfo angle=\"0\" centerX=\"{w / 2}\" centerY=\"{h / 2}\" rotateimage=\"1\"/>");
        sb.Append(IdentityRendering);
        sb.Append(LineShape(box.Line));
        sb.Append(FillBrush(box.Fill, box.Gradient));
        sb.Append("<hp:shadow type=\"NONE\" color=\"#B2B2B2\" offsetX=\"0\" offsetY=\"0\" alpha=\"0\"/>");
        sb.Append($"<hp:drawText lastWidth=\"{w}\" name=\"\" editable=\"0\">");
        sb.Append(content);
        sb.Append($"<hp:textMargin left=\"{box.Padding.Left}\" right=\"{box.Padding.Right}\" top=\"{box.Padding.Top}\" bottom=\"{box.Padding.Bottom}\"/>");
        sb.Append("</hp:drawText>");
        sb.Append(ellipse ? EllipseGeometry(w, h) : RectangleGeometry(w, h));
        sb.Append($"<hp:sz width=\"{w}\" widthRelTo=\"ABSOLUTE\" height=\"{h}\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>");
        sb.Append(Position(box.Anchor));
        sb.Append("<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>");
        sb.Append($"</hp:{element}>");
        return sb.ToString();
    }

    private static string RectangleGeometry(int w, int h) =>
        $"<hc:pt0 x=\"0\" y=\"0\"/><hc:pt1 x=\"{w}\" y=\"0\"/><hc:pt2 x=\"{w}\" y=\"{h}\"/><hc:pt3 x=\"0\" y=\"{h}\"/>";

    private static string EllipseGeometry(int w, int h) =>
        $"<hc:center x=\"{w / 2}\" y=\"{h / 2}\"/><hc:ax1 x=\"{w}\" y=\"{h / 2}\"/><hc:ax2 x=\"{w / 2}\" y=\"0\"/>" +
        "<hc:start1 x=\"0\" y=\"0\"/><hc:end1 x=\"0\" y=\"0\"/><hc:start2 x=\"0\" y=\"0\"/><hc:end2 x=\"0\" y=\"0\"/>";

    // ───────────────────────── Shapes ─────────────────────────

    private string Shape(HShape shape)
    {
        var id = NextId();
        var w = Math.Max(1, shape.Width);
        var h = Math.Max(1, shape.Height);
        var (element, extra) = shape.Kind switch
        {
            HShapeKind.Ellipse => ("ellipse", " intervalDirty=\"0\" hasArcPr=\"0\" arcType=\"NORMAL\""),
            HShapeKind.Line => ("line", " isReverseHV=\"0\""),
            _ => ("rect", $" ratio=\"{shape.CornerRatio}\""),
        };

        var sb = new StringBuilder();
        sb.Append($"<hp:{element} id=\"{id}\" zOrder=\"{_zOrder++}\" numberingType=\"NONE\" textWrap=\"{shape.Anchor?.Wrap ?? "TOP_AND_BOTTOM"}\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" href=\"\" groupLevel=\"0\" instid=\"{id}\"{extra}>");
        sb.Append("<hp:offset x=\"0\" y=\"0\"/>");
        sb.Append($"<hp:orgSz width=\"{w}\" height=\"{h}\"/><hp:curSz width=\"0\" height=\"0\"/>");
        sb.Append("<hp:flip horizontal=\"0\" vertical=\"0\"/>");
        sb.Append($"<hp:rotationInfo angle=\"0\" centerX=\"{w / 2}\" centerY=\"{h / 2}\" rotateimage=\"1\"/>");
        sb.Append(IdentityRendering);
        sb.Append(LineShape(shape.Line));
        if (shape.Kind != HShapeKind.Line)
            sb.Append(FillBrush(shape.Fill, shape.Gradient));
        sb.Append("<hp:shadow type=\"NONE\" color=\"#B2B2B2\" offsetX=\"0\" offsetY=\"0\" alpha=\"0\"/>");
        switch (shape.Kind)
        {
            case HShapeKind.Line:
                {
                    // A line runs corner to corner of its box; flips choose which corners.
                    var (x1, x2) = shape.FlipHorizontal ? (shape.Width, 0) : (0, shape.Width);
                    var (y1, y2) = shape.FlipVertical ? (shape.Height, 0) : (0, shape.Height);
                    sb.Append($"<hc:startPt x=\"{x1}\" y=\"{y1}\"/><hc:endPt x=\"{x2}\" y=\"{y2}\"/>");
                    break;
                }
            case HShapeKind.Ellipse:
                sb.Append(EllipseGeometry(w, h));
                break;
            default:
                sb.Append(RectangleGeometry(w, h));
                break;
        }
        sb.Append($"<hp:sz width=\"{w}\" widthRelTo=\"ABSOLUTE\" height=\"{h}\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>");
        sb.Append(Position(shape.Anchor));
        sb.Append("<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/>");
        sb.Append($"</hp:{element}>");
        return sb.ToString();
    }

    /// <summary>Outline of a drawing object; the width is in HWPUNIT here (33 ≈ 0.12 mm), not a named width.</summary>
    private static string LineShape(HBorder line)
    {
        var none = line.Style == HBorderStyle.None;
        var width = none ? 0 : Math.Max(1, (int)Math.Round(line.WidthMm * HwpxUnits.PerMm));
        return $"<hp:lineShape color=\"{line.Color}\" width=\"{width}\" style=\"{(none ? "NONE" : LineType(line.Style))}\" endCap=\"FLAT\" headStyle=\"NORMAL\" tailStyle=\"NORMAL\" headfill=\"1\" tailfill=\"1\" headSz=\"MEDIUM_MEDIUM\" tailSz=\"MEDIUM_MEDIUM\" outlineStyle=\"NORMAL\" alpha=\"0\"/>";
    }

    /// <summary>hc:fillBrush holds exactly one of winBrush (solid) or gradation; no brush = transparent.</summary>
    private static string FillBrush(string? fill, HGradient? gradient)
    {
        if (gradient is { Colors.Count: >= 2 })
        {
            var colors = string.Concat(gradient.Colors.Select(c => $"<hc:color value=\"{c}\"/>"));
            return $"<hc:fillBrush><hc:gradation type=\"LINEAR\" angle=\"{gradient.Angle}\" centerX=\"50\" centerY=\"50\" step=\"255\" colorNum=\"{gradient.Colors.Count}\" stepCenter=\"50\" alpha=\"0\">{colors}</hc:gradation></hc:fillBrush>";
        }
        return fill is null ? "" : $"<hc:fillBrush><hc:winBrush faceColor=\"{fill}\" hatchColor=\"#000000\" alpha=\"0\"/></hc:fillBrush>";
    }

    // ───────────────────────── Notes ─────────────────────────

    private string Note(HNote note)
    {
        var kind = note.Endnote ? "endNote" : "footNote";
        var number = note.Endnote ? ++_endnotes : ++_footnotes;
        var previousBase = _base;
        _base = _footnoteStyle ?? _base;

        // The number is an autoNum control at the start of the note's first paragraph (as 한글 writes it).
        var numberRun = $"<hp:run charPrIDRef=\"{_base.CharPrId}\">" +
                        AutoNumber(note.Endnote ? "ENDNOTE" : "FOOTNOTE", number, ")") +
                        "<hp:t> </hp:t></hp:run>";
        var content = SubList(note.Blocks, "TOP", leadingRun: numberRun);
        _base = previousBase;
        return $"<hp:ctrl><hp:{kind} number=\"{number}\" suffixChar=\"41\" instId=\"{NextId()}\">{content}</hp:{kind}></hp:ctrl>";
    }

    /// <summary>Automatic number: PAGE, TOTAL_PAGE, FOOTNOTE or ENDNOTE (<paramref name="number"/> is a display cache).</summary>
    private static string AutoNumber(string type, int number, string suffix) =>
        $"<hp:ctrl><hp:autoNum num=\"{number}\" numType=\"{type}\">" +
        $"<hp:autoNumFormat type=\"DIGIT\" userChar=\"\" prefixChar=\"\" suffixChar=\"{Escape(suffix)}\" supscript=\"0\"/>" +
        "</hp:autoNum></hp:ctrl>";

    // ───────────────────────── Hyperlinks ─────────────────────────

    /// <summary>
    /// A HYPERLINK field as 한글 stores it: the target in "Command" with ':', '?', ';' and '#' escaped, followed by
    /// a kind tail (";1;0;0;" web, ";2;0;0" mail, "?name;0;0;0;" bookmark in this document).
    /// </summary>
    private static string FieldBegin(int id, string target)
    {
        static string Escaped(string text) =>
            string.Concat(text.Select(c => c is ':' or '?' or ';' or '#' ? "\\" + c : c.ToString()));

        string command, category;
        string? path = null;
        if (target.StartsWith('#'))
        {
            command = "?" + Escaped(target[1..]) + ";0;0;0;";
            category = "HWPHYPERLINK_TYPE_HWP";
        }
        else if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            (command, path, category) = (target + ";2;0;0", target, "HWPHYPERLINK_TYPE_EMAIL");
        }
        else
        {
            (command, path, category) = (Escaped(target) + ";1;0;0;", target, "HWPHYPERLINK_TYPE_URL");
        }

        var parameters = new StringBuilder();
        parameters.Append("<hp:integerParam name=\"Prop\">0</hp:integerParam>");
        parameters.Append($"<hp:stringParam name=\"Command\">{Escape(command)}</hp:stringParam>");
        if (path is not null)
            parameters.Append($"<hp:stringParam name=\"Path\">{Escape(path)}</hp:stringParam>");
        parameters.Append($"<hp:stringParam name=\"Category\">{category}</hp:stringParam>");
        parameters.Append("<hp:stringParam name=\"TargetType\">HWPHYPERLINK_TARGET_BOOKMARK</hp:stringParam>");
        parameters.Append("<hp:stringParam name=\"DocOpenType\">HWPHYPERLINK_JUMP_CURRENTTAB</hp:stringParam>");

        return $"<hp:ctrl><hp:fieldBegin id=\"{id}\" type=\"HYPERLINK\" name=\"\" editable=\"0\" dirty=\"1\" zorder=\"-1\" fieldid=\"{HyperlinkFieldId}\">" +
               $"<hp:parameters cnt=\"{(path is null ? 5 : 6)}\" name=\"\">{parameters}</hp:parameters></hp:fieldBegin></hp:ctrl>";
    }
}
