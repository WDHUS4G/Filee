// TEXT, ATTRIB and MTEXT for the CAD scene: justification, rotation, width factor, oblique angle, mirroring, and
// MTEXT formatting codes with word wrap. Text is measured with the typeface it is drawn with (see CadFonts).

using ACadSharp.Entities;
using ACadSharp.Tables;
using SkiaSharp;

namespace Filee.Engines.Cad;

internal sealed partial class CadSceneBuilder
{
    /// <summary>AutoCAD's MTEXT line pitch: 5/3 of the text height at line spacing factor 1.</summary>
    private const double LinePitch = 5.0 / 3.0;

    /// <summary>Top of the text box above the baseline for extents, in em (covers accents and CJK glyphs).</summary>
    private const double AscentEm = 0.95;

    private void DrawText(TextEntity text, string? value, Layer layer, DrawContext context)
    {
        var content = CadTextCodes.DecodeText(value);
        if (content.Trim().Length == 0)
            return;

        var style = text.Style;
        var height = text.Height > 0 ? text.Height : style?.Height > 0 ? style.Height : 2.5;
        var typeface = TypefaceOf(style, null, false, false);
        var (capHeight, descent) = CadFonts.Metrics(typeface);
        var em = height / capHeight;
        var widthFactor = text.WidthFactor > 0 ? text.WidthFactor : 1;
        var runs = CadFonts.Layout(content, typeface);
        var width = runs.Sum(r => r.Advance) * em * widthFactor;

        // Justified text is positioned by its second alignment point; "aligned" and "fit" stretch between both.
        var horizontal = text.HorizontalAlignment;
        var vertical = text.VerticalAlignment;
        var justified = horizontal != TextHorizontalAlignment.Left || vertical != TextVerticalAlignmentType.Baseline;
        Vec3 anchor = justified ? text.AlignmentPoint : text.InsertPoint;
        var rotation = text.Rotation;
        if (horizontal is TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit && width > 0)
        {
            Vec3 first = text.InsertPoint;
            var span = (Vec3)text.AlignmentPoint - first;
            var length = Math.Sqrt(span.X * span.X + span.Y * span.Y);
            if (length > 1e-12)
            {
                anchor = first;
                rotation = Math.Atan2(span.Y, span.X);
                if (horizontal == TextHorizontalAlignment.Aligned)
                {
                    em *= length / width;
                    height *= length / width;
                }
                else
                {
                    widthFactor *= length / width;
                }
                width = length;
            }
        }

        var x = horizontal switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => -width / 2,
            TextHorizontalAlignment.Right => -width,
            _ => 0,
        };
        var y = horizontal == TextHorizontalAlignment.Middle ? -height / 2 : vertical switch
        {
            TextVerticalAlignmentType.Bottom => descent * em,
            TextVerticalAlignmentType.Middle => -height / 2,
            TextVerticalAlignmentType.Top => -height,
            _ => 0.0,
        };

        // Mirrored text (MIRRTEXT, "backward" / "upside down" styles) flips the glyph axes around the anchor.
        var flipX = text.Mirror.HasFlag(TextMirrorFlag.Backward) ? -1 : 1;
        var flipY = text.Mirror.HasFlag(TextMirrorFlag.UpsideDown) ? -1 : 1;
        var frame = context.Transform
            .Multiply(Affine3.Ocs(text.Normal))
            .Multiply(Affine3.Translation(anchor))
            .Multiply(Affine3.RotationZ(rotation))
            .Multiply(Affine3.Scale(flipX, flipY, 1));

        var color = ResolveColor(text, layer, context);
        EmitLine(runs, frame, x, y, em, widthFactor, text.ObliqueAngle, color);
    }

    private void DrawMText(MText mtext, Layer layer, DrawContext context)
    {
        var style = mtext.Style;
        var height = mtext.Height > 0 ? mtext.Height : style?.Height > 0 ? style.Height : 2.5;
        DrawMTextCore(mtext.Value, mtext.InsertPoint, mtext.AlignmentPoint, mtext.Normal, height, mtext.RectangleWidth,
            mtext.AttachmentPoint, mtext.LineSpacing, style, ResolveColor(mtext, layer, context), context);
    }

    /// <summary>One placed piece of an MTEXT line.</summary>
    private readonly record struct Piece(string Text, MTextFormat Format, double Width);

    /// <summary>
    /// Lays out MTEXT: paragraphs are word-wrapped at <paramref name="wrapWidth"/> (CJK text breaks between
    /// characters), lines are stacked with AutoCAD's line pitch and the block is placed by its attachment point.
    /// </summary>
    private void DrawMTextCore(string? value, Vec3 insert, Vec3 direction, Vec3 normal, double height, double wrapWidth,
        AttachmentPointType attachment, double lineSpacing, TextStyle? style, SKColor color, DrawContext context)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var styleFlags = style?.TrueType ?? FontFlags.Regular;
        var initial = new MTextFormat(
            height,
            ColorIndex: null,
            TrueColor: null,
            FontFamily: null,
            Bold: styleFlags.HasFlag(FontFlags.Bold),
            Italic: styleFlags.HasFlag(FontFlags.Italic),
            WidthFactor: style?.Width > 0 ? style.Width : 1,
            ObliqueDegrees: style is null ? 0 : style.ObliqueAngle * 180 / Math.PI);
        var paragraphs = CadTextCodes.ParseMText(value, initial);

        var limit = wrapWidth > 0 ? wrapWidth : double.PositiveInfinity;
        var lines = new List<List<Piece>>();
        foreach (var paragraph in paragraphs)
            WrapParagraph(paragraph, limit, style, lines);
        if (lines.All(line => line.All(p => p.Text.Trim().Length == 0)))
            return;

        // Baselines from the top: the first line's caps touch the top, then one pitch per line.
        var baselines = new double[lines.Count];
        var y = 0.0;
        var spacing = lineSpacing > 0 ? lineSpacing : 1;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineHeight = lines[i].Count == 0 ? height : lines[i].Max(p => p.Format.Height);
            y -= i == 0 ? lineHeight : lineHeight * LinePitch * spacing;
            baselines[i] = y;
        }
        var totalHeight = -y;
        var shiftY = attachment switch
        {
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => totalHeight / 2,
            AttachmentPointType.BottomLeft or AttachmentPointType.BottomCenter or AttachmentPointType.BottomRight => totalHeight,
            _ => 0,
        };

        // MTEXT's direction vector is in WCS; a zero vector means the OCS X axis.
        var ocs = Affine3.Ocs(normal);
        var xAxis = direction.Length > 1e-12 ? direction.Normalized() : ocs.X;
        var zAxis = ocs.Z;
        var yAxis = Vec3.Cross(zAxis, xAxis).Normalized();
        var frame = context.Transform.Multiply(new Affine3(xAxis, yAxis, zAxis, insert));

        for (var i = 0; i < lines.Count; i++)
        {
            var line = TrimEnd(lines[i]);
            var lineWidth = line.Sum(p => p.Width);
            var x = attachment switch
            {
                AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => -lineWidth / 2,
                AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => -lineWidth,
                _ => 0,
            };
            foreach (var piece in line)
            {
                var format = piece.Format;
                var typeface = TypefaceOf(style, format.FontFamily, format.Bold, format.Italic);
                var em = format.Height / CadFonts.Metrics(typeface).CapHeight;
                var pieceColor = format.TrueColor is { } rgb
                    ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                    : format.ColorIndex is { } index and not 256
                        ? index == 0 ? context.BlockColor : ResolveColor(new ACadSharp.Color(index), Layer.Default, context)
                        : color;
                EmitLine(CadFonts.Layout(piece.Text, typeface), frame, x, baselines[i] + shiftY, em, format.WidthFactor,
                    format.ObliqueDegrees * Math.PI / 180, pieceColor);
                x += piece.Width;
            }
        }
    }

    /// <summary>Greedy word wrap of one paragraph into <paramref name="lines"/>.</summary>
    private static void WrapParagraph(List<MTextRun> paragraph, double limit, TextStyle? style, List<List<Piece>> lines)
    {
        var line = new List<Piece>();
        var width = 0.0;
        foreach (var run in paragraph)
        {
            var typeface = TypefaceOf(style, run.Format.FontFamily, run.Format.Bold, run.Format.Italic);
            var em = run.Format.Height / CadFonts.Metrics(typeface).CapHeight * run.Format.WidthFactor;
            foreach (var word in Words(run.Text))
            {
                var wordWidth = CadFonts.Layout(word, typeface).Sum(r => r.Advance) * em;
                var inkWidth = CadFonts.Layout(word.TrimEnd(), typeface).Sum(r => r.Advance) * em;
                if (line.Count > 0 && width + inkWidth > limit)
                {
                    lines.Add(line);
                    line = [];
                    width = 0;
                    if (word.Trim().Length == 0)
                        continue; // spaces at a line break disappear
                }
                line.Add(new Piece(word, run.Format, wordWidth));
                width += wordWidth;
            }
        }
        lines.Add(Merge(line));
    }

    /// <summary>Splits text into wrap units: words with their trailing spaces, and single CJK characters.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (IsCjk(ch))
            {
                if (i > start)
                    yield return text[start..i];
                var end = i + 1;
                while (end < text.Length && text[end] == ' ')
                    end++;
                yield return text[i..end];
                start = end;
                i = end - 1;
            }
            else if (ch == ' ' && (i + 1 >= text.Length || text[i + 1] != ' '))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    private static bool IsCjk(char ch) =>
        ch is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯';

    /// <summary>Joins neighbouring pieces with the same format so each becomes one text run.</summary>
    private static List<Piece> Merge(List<Piece> line)
    {
        var merged = new List<Piece>();
        foreach (var piece in line)
        {
            if (merged.Count > 0 && merged[^1].Format == piece.Format)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + piece.Text, Width = merged[^1].Width + piece.Width };
            else
                merged.Add(piece);
        }
        return merged;
    }

    /// <summary>Drops trailing spaces so right and centre alignment use the visible width.</summary>
    private static List<Piece> TrimEnd(List<Piece> line)
    {
        var result = Merge(line);
        if (result.Count > 0)
        {
            var last = result[^1];
            var trimmed = last.Text.TrimEnd();
            if (trimmed.Length != last.Text.Length)
            {
                var ratio = last.Text.Length == 0 ? 0 : (double)trimmed.Length / last.Text.Length;
                result[^1] = last with { Text = trimmed, Width = last.Width * ratio };
            }
        }
        return result;
    }

    /// <summary>
    /// Emits the runs of one text line with its baseline starting at (<paramref name="x"/>, <paramref name="y"/>)
    /// in <paramref name="frame"/>'s XY plane.
    /// </summary>
    private void EmitLine(List<GlyphRun> runs, Affine3 frame, double x, double y, double em, double widthFactor, double oblique, SKColor color)
    {
        // Oblique angles are stored in [0, 2π): 345° leans left like -15°. Steeper than ±85° is not allowed.
        var shear = Math.Tan(Math.Clamp(Math.IEEERemainder(oblique, 2 * Math.PI), -1.48, 1.48));
        var xAxis = frame.Vector(em * widthFactor, 0);
        var yAxis = frame.Vector(-em * shear, -em);
        foreach (var run in runs)
        {
            var advance = run.Advance * em * widthFactor;
            if (run.Text.Trim().Length > 0 && Reserve())
            {
                _scene.Items.Add(new TextPrimitive
                {
                    Text = run.Text,
                    Typeface = run.Typeface,
                    Origin = frame.Point(x, y),
                    XAxis = xAxis,
                    YAxis = yAxis,
                    Color = color,
                });
                foreach (var rise in (ReadOnlySpan<double>)[-0.2 * em, AscentEm * em])
                {
                    _scene.Extents.Add(frame.Point(x + rise * shear, y + rise));
                    _scene.Extents.Add(frame.Point(x + advance + rise * shear, y + rise));
                }
            }
            x += advance;
        }
    }

    /// <summary>The typeface of an MTEXT font code, else of the text style.</summary>
    private static SKTypeface TypefaceOf(TextStyle? style, string? family, bool bold, bool italic)
    {
        if (family is not null)
            return CadFonts.Resolve(family, bold, italic);
        var flags = style?.TrueType ?? FontFlags.Regular;
        return CadFonts.Resolve(style?.Filename, bold || flags.HasFlag(FontFlags.Bold), italic || flags.HasFlag(FontFlags.Italic));
    }
}
