// The last touch on a PDF a preset writes (PdfOptions.FinishesPdf): a watermark across every page, page numbers at
// the bottom, and a password it opens with. Text is drawn as glyph outlines (GlyphOutlines, HarfBuzz), so any
// language works with the fonts on the PC and no font is embedded; the password encrypts with AES-256.
// Runs after whichever engine made the PDF (JobQueue), once for a merged PDF.

using Filee.Core.Presets;
using Filee.Engines.Vector;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Filee.Engines.Pdf;

internal static class PdfFinishing
{
    private const double PointsPerMm = 72 / 25.4;

    /// <summary>The most UTF-8 bytes of a password readers use (AES-256, revision 6): longer ones can't be opened.</summary>
    public const int MaxPasswordBytes = 127;

    /// <summary>Rewrites <paramref name="path"/> with the watermark, page numbers and password <paramref name="options"/> ask for.</summary>
    public static void Finish(string path, PdfOptions options, CancellationToken ct)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(options.Password) > MaxPasswordBytes)
            throw new ArgumentException($"The PDF password is longer than {MaxPasswordBytes} bytes, so PDF readers couldn't open the file with it.");
        var finished = path + ".finished";
        try
        {
            using (var document = PdfSharpConverter.OpenForChanges(path))
            using (var glyphs = new GlyphOutlines())
            {
                var watermark = options.Watermark.Trim();
                for (var i = 0; i < document.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Stamp(document.Pages[i], glyphs, watermark, options.PageNumbers ? $"{i + 1} / {document.PageCount}" : null);
                }
                if (options.Password.Length > 0)
                {
                    document.SecuritySettings.UserPassword = options.Password;
                    document.SecuritySettings.OwnerPassword = options.Password;
                    document.SecurityHandler.SetEncryptionToV5();
                }
                document.Save(finished);
            }
            File.Move(finished, path, overwrite: true);
        }
        finally
        {
            // Nothing is left next to the user's file when saving fails.
            File.Delete(finished);
        }
    }

    /// <summary>
    /// Draws the stamps on one page as it is shown: inside its crop box and the right way up for its /Rotate. The page
    /// is drawn on unturned (PDFsharp's own handling of turned pages puts the stamps elsewhere) and turned back.
    /// </summary>
    private static void Stamp(PdfPage page, GlyphOutlines glyphs, string watermark, string? number)
    {
        if (watermark.Length == 0 && number is null)
            return;
        var media = page.MediaBox;
        var crop = page.Elements.ContainsKey("/CropBox") ? page.CropBox : media;
        // The visible part in drawing units: x from the media box's left edge, y down from its top edge.
        var left = Math.Max(crop.X1, media.X1) - media.X1;
        var top = media.Y2 - Math.Min(crop.Y2, media.Y2);
        var width = Math.Min(crop.X2, media.X2) - Math.Max(crop.X1, media.X1);
        var height = Math.Min(crop.Y2, media.Y2) - Math.Max(crop.Y1, media.Y1);
        if (width <= 0 || height <= 0)
            return;
        var rotate = page.Rotate;
        var orientation = page.Orientation;
        page.Rotate = 0;
        // PDFsharp swaps width and height of a page whose orientation doesn't match its media box: drawn on unturned, it
        // must match, or a landscape page is drawn on as if it were portrait.
        page.Orientation = media.Width > media.Height ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
        try
        {
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            graphics.TranslateTransform(left, top);
            // Shown turned clockwise by /Rotate: map the shown page (x right, y down) onto the unturned one.
            var (shownWidth, shownHeight) = (((rotate % 360) + 360) % 360) switch
            {
                90 => Turn(graphics, new XMatrix(0, -1, 1, 0, 0, height), height, width),
                180 => Turn(graphics, new XMatrix(-1, 0, 0, -1, width, height), width, height),
                270 => Turn(graphics, new XMatrix(0, 1, -1, 0, width, 0), height, width),
                _ => (width, height),
            };
            if (watermark.Length > 0)
                DrawWatermark(graphics, glyphs, watermark, shownWidth, shownHeight);
            if (number is not null)
                DrawPageNumber(graphics, glyphs, number, shownWidth, shownHeight);
        }
        finally
        {
            page.Orientation = orientation;
            page.Rotate = rotate;
        }

        static (double, double) Turn(XGraphics graphics, XMatrix matrix, double shownWidth, double shownHeight)
        {
            graphics.MultiplyTransform(matrix);
            return (shownWidth, shownHeight);
        }
    }

    /// <summary>Large, faint grey text along the page's diagonal, over the content.</summary>
    private static void DrawWatermark(XGraphics graphics, GlyphOutlines glyphs, string text, double width, double height)
    {
        using var outline = Outline(glyphs, text, 100, bold: true);
        if (outline.IsEmpty)
            return;
        var bounds = outline.TightBounds;
        var diagonal = Math.Sqrt(width * width + height * height);
        var scale = 0.7 * diagonal / bounds.Width;
        // Short text isn't blown up to cover the page: at most 1/4 of the short side high.
        scale = Math.Min(scale, Math.Min(width, height) / 4 / bounds.Height);
        var state = graphics.Save();
        graphics.TranslateTransform(width / 2, height / 2);
        graphics.RotateTransform(-Math.Atan2(height, width) * 180 / Math.PI);
        graphics.ScaleTransform(scale);
        graphics.TranslateTransform(-bounds.MidX, -bounds.MidY);
        graphics.DrawPath(new XSolidBrush(XColor.FromArgb(56, 128, 128, 128)), ToXPath(outline));
        graphics.Restore(state);
    }

    /// <summary>"3 / 10" centred 10 mm above the bottom edge, 9 pt dark grey.</summary>
    private static void DrawPageNumber(XGraphics graphics, GlyphOutlines glyphs, string text, double width, double height)
    {
        using var outline = Outline(glyphs, text, 9, bold: false);
        if (outline.IsEmpty)
            return;
        var bounds = outline.TightBounds;
        var state = graphics.Save();
        graphics.TranslateTransform(width / 2 - bounds.MidX, height - 10 * PointsPerMm);
        graphics.DrawPath(new XSolidBrush(XColor.FromArgb(96, 96, 96)), ToXPath(outline));
        graphics.Restore(state);
    }

    /// <summary>
    /// The text's glyph outlines at <paramref name="size"/> points, baseline at y = 0 (y down). The font is one the PC
    /// has for the text's first non-Latin character, else Arial or the system's default face.
    /// </summary>
    internal static SKPath Outline(GlyphOutlines glyphs, string text, float size, bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        var other = text.EnumerateRunes().FirstOrDefault(r => r.Value > 0x24F);
        using var typeface = (other.Value > 0 ? SKFontManager.Default.MatchCharacter(null, style, null, other.Value) : null)
                             ?? SKFontManager.Default.MatchFamily("Arial", style)
                             ?? SKTypeface.CreateDefault();
        using var font = new SKFont(typeface, size);
        var path = new SKPath { FillType = SKPathFillType.Winding };
        glyphs.AppendShaped(path, text, font, 0, 0, SKTextAlign.Left);
        return path;
    }

    /// <summary>A Skia path as a PDFsharp path; quadratic curves become cubic ones.</summary>
    internal static XGraphicsPath ToXPath(SKPath path)
    {
        var result = new XGraphicsPath { FillMode = XFillMode.Winding };
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    result.StartFigure();
                    break;
                case SKPathVerb.Line:
                    result.AddLine(X(points[0]), X(points[1]));
                    break;
                case SKPathVerb.Quad or SKPathVerb.Conic:
                    var (p0, p1, p2) = (points[0], points[1], points[2]);
                    result.AddBezier(X(p0), X(p0 + Third(p1 - p0)), X(p2 + Third(p1 - p2)), X(p2));
                    break;
                case SKPathVerb.Cubic:
                    result.AddBezier(X(points[0]), X(points[1]), X(points[2]), X(points[3]));
                    break;
                case SKPathVerb.Close:
                    result.CloseFigure();
                    break;
            }
        }
        return result;

        static XPoint X(SKPoint p) => new(p.X, p.Y);
        static SKPoint Third(SKPoint v) => new(v.X * 2 / 3, v.Y * 2 / 3);
    }
}
