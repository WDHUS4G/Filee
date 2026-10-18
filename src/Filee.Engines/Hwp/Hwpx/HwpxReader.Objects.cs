// Objects inside runs: pictures (inline or floating), rectangles / ellipses / lines, text boxes (drawing objects with
// hp:drawText), groups (hp:container), equations and other objects that only carry text. Objects the model cannot
// hold (OLE, charts, video, form controls, free-form shapes without text) are skipped and counted.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    /// <summary>Drawing objects that may appear in a run or a group.</summary>
    private static readonly HashSet<string> ShapeNames = ["pic", "rect", "ellipse", "line", "arc", "polygon", "curve", "connectLine", "container"];

    /// <summary>Size and position of an object: <c>Anchor</c> is null for objects laid out like a character.</summary>
    private readonly record struct Placement(int Width, int Height, HAnchor? Anchor);

    private void DrawingObject(XElement element, ParagraphBuilder builder, HCharFormat format)
    {
        var name = element.Name.LocalName;
        if (ShapeNames.Contains(name))
        {
            foreach (var inline in Shapes(element, PlacementOf(element)))
                builder.Add(inline);
            return;
        }
        switch (name)
        {
            case "equation":
                // The equation script (한글's equation language, e.g. "x over y") is the closest text form.
                if (Child(element, "script")?.Value.Trim() is { Length: > 0 } script)
                    builder.AddText(script, format);
                break;
            case "textart":
                if ((Attr(element, "text") ?? Child(element, "text")?.Value) is { Length: > 0 } art)
                    builder.AddText(art, format);
                break;
            case "compose":
                // Overlapping characters (글자 겹치기): the characters one after the other.
                if ((Attr(element, "composeText") ?? Child(element, "composeText")?.Value) is { Length: > 0 } composed)
                    builder.AddText(composed, format);
                break;
            case "dutmal":
                // Ruby text (덧말): the main text; the small text above it has no place in the model.
                if ((Attr(element, "mainText") ?? Child(element, "mainText")?.Value) is { Length: > 0 } main)
                    builder.AddText(main, format);
                break;
            default:
                Skip(element); // ole, chart, video, form controls, ...
                break;
        }
    }

    /// <summary>The model objects for one drawing object; groups give one object per member.</summary>
    private IEnumerable<HInline> Shapes(XElement element, Placement placement)
    {
        switch (element.Name.LocalName)
        {
            case "pic":
                if (Picture(element, placement) is { } picture)
                    yield return picture;
                else
                    Skip("pic");
                break;
            case "rect":
                yield return Box(element, HShapeKind.Rectangle, placement);
                break;
            case "ellipse":
                yield return Box(element, HShapeKind.Ellipse, placement);
                break;
            case "line":
                {
                    var start = Child(element, "startPt");
                    var end = Child(element, "endPt");
                    var points = start is not null && end is not null;
                    yield return new HShape(HShapeKind.Line)
                    {
                        // A horizontal or vertical line still has a size of one unit across in the file.
                        Width = points && Int(start, "x") == Int(end, "x") ? 0 : placement.Width,
                        Height = points && Int(start, "y") == Int(end, "y") ? 0 : placement.Height,
                        Anchor = placement.Anchor,
                        Line = LineShape(Child(element, "lineShape")),
                        // A line runs corner to corner of its box; the end points say which corners.
                        FlipHorizontal = Int(start, "x") > Int(end, "x"),
                        FlipVertical = Int(start, "y") > Int(end, "y"),
                    };
                    break;
                }
            case "container":
                foreach (var member in Group(element, placement))
                    yield return member;
                break;
            default:
                // Arcs, polygons, curves and connectors: only their text survives, in a box without outline.
                if (Child(element, "drawText") is not null)
                {
                    var box = (HTextBox)Box(element, HShapeKind.Rectangle, placement);
                    box.Line = HBorder.None;
                    box.Fill = null;
                    box.Gradient = null;
                    yield return box;
                }
                else
                {
                    Skip(element);
                }
                break;
        }
    }

    private HImage? Picture(XElement pic, Placement placement)
    {
        var image = pic.Descendants().FirstOrDefault(e => e.Name.LocalName == "img" && e.Attribute("binaryItemIDRef") is not null);
        return ExtractBinary(Attr(image, "binaryItemIDRef")) is { } path
            ? new HImage(path)
            {
                Width = placement.Width > 0 ? placement.Width : null,
                Height = placement.Height > 0 ? placement.Height : null,
                Anchor = placement.Anchor,
            }
            : null;
    }

    /// <summary>A rectangle or ellipse: a text box when it has text (hp:drawText), else a plain shape.</summary>
    private HInline Box(XElement element, HShapeKind kind, Placement placement)
    {
        var line = LineShape(Child(element, "lineShape"));
        var (fill, gradient) = FillBrush(Child(element, "fillBrush"));
        var ratio = kind == HShapeKind.Rectangle ? Int(element, "ratio") : 0;
        if (Child(element, "drawText") is not { } drawText)
        {
            return new HShape(kind)
            {
                Width = placement.Width,
                Height = placement.Height,
                Anchor = placement.Anchor,
                Fill = fill,
                Gradient = gradient,
                Line = line,
                CornerRatio = ratio,
            };
        }

        var subList = Child(drawText, "subList");
        var margin = Child(drawText, "textMargin");
        var box = new HTextBox
        {
            Width = placement.Width,
            Height = placement.Height,
            Anchor = placement.Anchor,
            Fill = fill,
            Gradient = gradient,
            Line = line,
            Shape = kind,
            CornerRatio = ratio,
            VerticalAlign = VerticalAlign(Attr(subList, "vertAlign")),
        };
        if (margin is not null)
            box.Padding = new HInsets(Int(margin, "left"), Int(margin, "right"), Int(margin, "top"), Int(margin, "bottom"));
        box.Blocks.AddRange(ReadSubList(subList));
        return box;
    }

    /// <summary>
    /// Members of a group (묶음 개체) become separate objects at their place inside the group. An inline group floats
    /// over its paragraph instead, like the DOCX reader does with inline groups.
    /// </summary>
    private IEnumerable<HInline> Group(XElement container, Placement placement)
    {
        var anchor = placement.Anchor ?? new HAnchor("COLUMN", "LEFT", 0, "PARA", "TOP", 0, "IN_FRONT_OF_TEXT", true);
        foreach (var member in Elements(container))
        {
            if (!ShapeNames.Contains(member.Name.LocalName))
                continue; // the group's own size, position and rendering properties
            var offset = Child(member, "offset");
            var (x, y) = (Int(offset, "x"), Int(offset, "y"));
            var (width, height) = MemberSize(member);
            var memberAnchor = anchor with
            {
                HorizontalOffset = anchor.HorizontalOffset + AlignedOffset(anchor.HorizontalAlign, x, width, placement.Width),
                VerticalOffset = anchor.VerticalOffset + AlignedOffset(anchor.VerticalAlign, y, height, placement.Height),
                AllowOverlap = true,
            };
            foreach (var inline in Shapes(member, new Placement(width, height, memberAnchor)))
                yield return inline;
        }
    }

    /// <summary>Keeps a member's distance from the group's centre or far edge when the group is aligned, not placed.</summary>
    private static int AlignedOffset(string align, int position, int size, int groupSize) => align switch
    {
        "CENTER" => position + size / 2 - groupSize / 2,
        "RIGHT" or "BOTTOM" => position + size - groupSize,
        _ => position,
    };

    /// <summary>Size of a group member: its current size, else its original size scaled by its rendering matrix.</summary>
    private static (int Width, int Height) MemberSize(XElement member)
    {
        var current = Child(member, "curSz");
        if (Int(current, "width") > 0 && Int(current, "height") > 0)
            return (Int(current, "width"), Int(current, "height"));
        var original = Child(member, "orgSz");
        var scale = Child(Child(member, "renderingInfo"), "scaMatrix");
        return ((int)Math.Round(Int(original, "width") * Double(scale, "e1", 1)), (int)Math.Round(Int(original, "height") * Double(scale, "e5", 1)));
    }

    private static Placement PlacementOf(XElement element)
    {
        var size = Child(element, "sz");
        var (width, height) = (Int(size, "width"), Int(size, "height"));
        if (width <= 0 || height <= 0)
            (width, height) = MemberSize(element);
        return new Placement(width, height, Anchor(element));
    }

    /// <summary>Position of a floating object (hp:pos); null when it is laid out like a character (treatAsChar).</summary>
    private static HAnchor? Anchor(XElement element)
    {
        var pos = Child(element, "pos");
        if (pos is null || Flag(pos, "treatAsChar"))
            return null;
        return new HAnchor(
            HorizontalRelativeTo: Attr(pos, "horzRelTo") ?? "COLUMN",
            HorizontalAlign: Attr(pos, "horzAlign") ?? "LEFT",
            HorizontalOffset: Int(pos, "horzOffset"),
            VerticalRelativeTo: Attr(pos, "vertRelTo") ?? "PARA",
            VerticalAlign: Attr(pos, "vertAlign") ?? "TOP",
            VerticalOffset: Int(pos, "vertOffset"),
            Wrap: Attr(element, "textWrap") switch
            {
                "SQUARE" or "TIGHT" or "THROUGH" => "SQUARE",
                "BEHIND_TEXT" => "BEHIND_TEXT",
                "IN_FRONT_OF_TEXT" => "IN_FRONT_OF_TEXT",
                _ => "TOP_AND_BOTTOM",
            },
            AllowOverlap: Flag(pos, "allowOverlap"));
    }

    /// <summary>Outline of a drawing object; its width is in HWPUNIT (a width of 0 is 한글's thinnest line).</summary>
    private static HBorder LineShape(XElement? lineShape)
    {
        var style = LineStyle(Attr(lineShape, "style"));
        if (lineShape is null || style == HBorderStyle.None)
            return HBorder.None;
        var width = Int(lineShape, "width");
        return new HBorder(style, width > 0 ? width / HwpxUnits.PerMm : 0.1, Color(Attr(lineShape, "color")) ?? "#000000");
    }

    /// <summary>A solid colour (hc:winBrush) or linear gradient (hc:gradation); picture fills are not kept.</summary>
    private static (string? Fill, HGradient? Gradient) FillBrush(XElement? fillBrush)
    {
        if (Child(fillBrush, "gradation") is { } gradation)
        {
            var colors = Children(gradation, "color").Select(c => Color(Attr(c, "value"))).OfType<string>().ToList();
            if (colors.Count >= 2)
                return (null, new HGradient(colors, Int(gradation, "angle")));
            if (colors.Count == 1)
                return (colors[0], null);
        }
        return (Color(Attr(Child(fillBrush, "winBrush"), "faceColor")), null);
    }

    private static double Double(XElement? element, string name, double fallback) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value != 0 ? value : fallback;
}
