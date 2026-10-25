// Draws an Svg.Skia drawing with its text converted to glyph outlines, for PDF output. SkiaSharp's PDF backend is
// built without a font subsetter and embeds every font file whole: one line of Korean text adds the 13 MB Malgun
// Gothic font to the PDF. Outlines keep the text's exact shape at a few kilobytes (it is no longer selectable).
// Text is shaped with HarfBuzz (GlyphOutlines).

using ShimSkiaSharp;
using Svg.Skia;
using SKCanvas = SkiaSharp.SKCanvas;
using SKFont = SkiaSharp.SKFont;
using SKPath = SkiaSharp.SKPath;
using SKTextAlign = SkiaSharp.SKTextAlign;
using SKTypeface = SkiaSharp.SKTypeface;

namespace Filee.Engines.Vector;

/// <summary>Plays back an Svg.Skia picture model, drawing text commands as paths.</summary>
internal sealed class OutlinedTextPlayback(SkiaModel model) : IDisposable
{
    private readonly GlyphOutlines _glyphs = new();

    /// <summary>Draws every command of <paramref name="picture"/> onto <paramref name="canvas"/>.</summary>
    public void Draw(ShimSkiaSharp.SKPicture picture, SKCanvas canvas)
    {
        if (picture.Commands is null)
            return;
        foreach (var command in picture.Commands)
            Draw(command, canvas);
    }

    private void Draw(CanvasCommand command, SKCanvas canvas)
    {
        if (command is DrawPictureCanvasCommand { Picture: { } nested })
        {
            Draw(nested, canvas);
            return;
        }
        // Everything else, and text that can't be shaped (text on a path, fonts without outlines), is drawn by
        // Svg.Skia itself.
        using var outline = TextOutline(command, out var shimPaint);
        if (outline is null)
        {
            model.Draw(command, canvas);
            return;
        }
        // The outlines with the text's paint (fill or stroke, colour, gradient, opacity).
        using var paint = shimPaint is null ? null : model.ToSKPaint(shimPaint);
        if (paint is not null && !outline.IsEmpty)
            canvas.DrawPath(outline, paint);
    }

    /// <summary>
    /// The glyph outlines of a text command, in the command's coordinates, with the text's paint; <c>null</c> when the
    /// command is no text or its text can't be shaped. Empty text gives an empty path.
    /// </summary>
    public SKPath? TextOutline(CanvasCommand command, out SKPaint? paint)
    {
        paint = command switch
        {
            DrawTextCanvasCommand text => text.Paint,
            DrawTextBlobCanvasCommand blob => blob.Paint,
            DrawPositionedTextRunCanvasCommand run => run.Paint,
            _ => null,
        };
        if (paint is null)
            return null;
        var path = new SKPath();
        var shaped = command switch
        {
            DrawTextCanvasCommand text => OutlineText(text, path),
            DrawTextBlobCanvasCommand blob => OutlineTextBlob(blob, path),
            DrawPositionedTextRunCanvasCommand run => OutlinePositionedRun(run, path),
            _ => false,
        };
        if (shaped)
            return path;
        path.Dispose();
        return null;
    }

    private bool OutlineText(DrawTextCanvasCommand command, SKPath path)
    {
        if (string.IsNullOrEmpty(command.Text))
            return true;
        using var font = command.Font is { } shimFont ? model.ToSKFont(shimFont) : model.ToSKFont(command.Paint!);
        var align = model.ToSKTextAlign(command.TextAlign ?? command.Paint!.TextAlign);
        return font?.Typeface is not null && _glyphs.AppendShaped(path, command.Text, font, command.X, command.Y, align);
    }

    private bool OutlineTextBlob(DrawTextBlobCanvasCommand command, SKPath path)
    {
        if (command.TextBlob?.Points is not { } points)
            return true;
        using var font = command.TextBlob.Font is { } shimFont ? model.ToSKFont(shimFont) : model.ToSKFont(command.Paint!);
        if (font is null)
            return false;
        var glyphs = command.TextBlob.Glyphs is { Length: > 0 } ids ? ids
            : command.TextBlob.Text is { } text ? font.GetGlyphs(text)
            : [];
        if (glyphs.Length != points.Length)
            return false;
        for (var i = 0; i < glyphs.Length; i++)
            GlyphOutlines.AppendGlyph(path, font, glyphs[i], command.X + points[i].X, command.Y + points[i].Y);
        return true;
    }

    private bool OutlinePositionedRun(DrawPositionedTextRunCanvasCommand command, SKPath path)
    {
        if (command.Fragments is not { Count: > 0 })
            return true;
        using var font = command.Font is { } shimFont ? model.ToSKFont(shimFont) : model.ToSKFont(command.Paint!);
        if (font?.Typeface is null)
            return false;
        var align = model.ToSKTextAlign(command.TextAlign ?? command.Paint!.TextAlign);
        foreach (var fragment in command.Fragments)
        {
            using var part = new SKPath();
            if (!_glyphs.AppendShaped(part, fragment.Text, font, fragment.Point.X, fragment.Point.Y, align))
                continue;
            // Per-character rotation (SVG "rotate") and horizontal scaling (textLength), as Svg.Skia applies them.
            var transform = SkiaSharp.SKMatrix.Identity;
            if (fragment.RotationDegrees != 0f)
                transform = SkiaSharp.SKMatrix.CreateRotationDegrees(fragment.RotationDegrees, fragment.Point.X, fragment.Point.Y);
            if (fragment.ScaleX != 1f)
                transform = transform.PreConcat(SkiaSharp.SKMatrix.CreateScale(fragment.ScaleX, 1f, fragment.ScaleOriginX, fragment.Point.Y));
            part.Transform(transform);
            path.AddPath(part);
        }
        return true;
    }

    public void Dispose() => _glyphs.Dispose();
}
