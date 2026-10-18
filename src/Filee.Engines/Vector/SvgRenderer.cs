// Loads SVG/SVGZ with Svg.Skia (MIT) and draws it with SkiaSharp: onto PDF pages as real vector graphics, or onto
// a bitmap for raster targets.

using System.IO.Compression;
using Filee.Core.Presets;
using ImageMagick;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace Filee.Engines.Vector;

/// <summary>SVG → PDF and SVG → bitmap.</summary>
internal static class SvgRenderer
{
    /// <summary>CSS pixels per inch: SVG user units are CSS pixels.</summary>
    public const float CssDpi = 96f;

    /// <summary>Small drawings (icons) are rendered at least this large on the long edge.</summary>
    public const int MinimumLongEdge = 1024;

    /// <summary>Upper bounds for rendered bitmaps, so a huge drawing doesn't exhaust memory.</summary>
    private const int MaximumLongEdge = 16384;
    private const long MaximumPixels = 100_000_000;

    static SvgRenderer()
    {
        // An SVG may reference images, other SVGs and XML entities by URL. Files next to the drawing are fine;
        // network access is not: converting a file must not fetch anything (tracking pixels, slow servers).
        SvgDocument.ResolveExternalImages = ExternalType.Local;
        SvgDocument.ResolveExternalElements = ExternalType.Local;
        SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
    }

    /// <summary>A loaded drawing. Dispose it after drawing.</summary>
    public sealed class Drawing(SKSvg svg, SKPicture picture, SKRect bounds) : IDisposable
    {
        /// <summary>The loaded document (its model is replayed for PDF output).</summary>
        public SKSvg Svg { get; } = svg;

        /// <summary>The drawing as a Skia picture, in CSS pixels.</summary>
        public SKPicture Picture { get; } = picture;

        /// <summary>The SVG viewport (width/height or viewBox) in CSS pixels.</summary>
        public SKRect Bounds { get; } = bounds;

        public void Dispose() => Svg.Dispose();
    }

    /// <summary>Loads an SVG or SVGZ (gzip) file.</summary>
    /// <exception cref="InvalidDataException">The file is not a drawable SVG.</exception>
    public static Drawing Load(string path)
    {
        var bytes = ReadSvgBytes(path);
        var svg = new SKSvg();
        svg.Settings.EnableJavaScript = false;
        svg.Settings.EnableExternalJavaScript = false;
        SKPicture? picture;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            picture = svg.Load(stream, null, new Uri(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            svg.Dispose();
            throw new InvalidDataException("The SVG file could not be read: " + ex.Message, ex);
        }
        var bounds = picture?.CullRect ?? SKRect.Empty;
        if (picture is null || bounds.Width <= 0 || bounds.Height <= 0 || float.IsInfinity(bounds.Width) || float.IsInfinity(bounds.Height))
        {
            svg.Dispose();
            throw new InvalidDataException("The SVG file has no drawable content or no size.");
        }
        return new Drawing(svg, picture, bounds);
    }

    /// <summary>The file's SVG text as bytes: SVGZ (or a gzipped .svg) is decompressed.</summary>
    public static byte[] ReadSvgBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!IsGzip(bytes))
            return bytes;
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }

    /// <summary>True when the data starts with the gzip magic number.</summary>
    public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    /// <summary>
    /// Writes a one-page PDF the size of the drawing. Paths, gradients and text stay vector (text as glyph outlines,
    /// see <see cref="OutlinedTextPlayback"/>); only effects PDF can't express (filters such as blur) are rasterized at
    /// 300 dpi.
    /// </summary>
    public static void WritePdf(Drawing drawing, string output, string title, bool pdfA)
    {
        const float pointsPerPixel = 72f / CssDpi;
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = title;
        metadata.Creator = "Filee";
        metadata.Producer = "Filee (Skia PDF)";
        metadata.Creation = DateTime.Now;
        metadata.Modified = DateTime.Now;
        metadata.RasterDpi = 300;
        metadata.EncodingQuality = 95;
        metadata.PdfA = pdfA;

        using (var stream = File.Create(output))
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = document.BeginPage(drawing.Bounds.Width * pointsPerPixel, drawing.Bounds.Height * pointsPerPixel);
            canvas.Scale(pointsPerPixel);
            canvas.Translate(-drawing.Bounds.Left, -drawing.Bounds.Top);
            if (drawing.Svg.Model is { } model)
            {
                using var playback = new OutlinedTextPlayback(drawing.Svg.SkiaModel);
                playback.Draw(model, canvas);
            }
            else
            {
                canvas.DrawPicture(drawing.Picture);
            }
            document.EndPage();
            document.Close();
        }
        if (new FileInfo(output).Length == 0)
            throw new InvalidOperationException("Skia could not write the PDF.");
    }

    /// <summary>
    /// Scale from CSS pixels to output pixels. Vector drawings are rendered at the size they end up at: at the preset's
    /// render DPI, at least <see cref="MinimumLongEdge"/> on the long edge, or exactly at the preset's resize target.
    /// </summary>
    public static float RasterScale(SKRect bounds, ImageOptions image, PdfOptions pdf)
    {
        var longEdge = Math.Max(bounds.Width, bounds.Height);
        var dpi = Math.Clamp(pdf.RenderDpi, 36, 1200);
        var scale = Math.Max(dpi / CssDpi, MinimumLongEdge / longEdge);
        switch (image.Resize)
        {
            case ResizeMode.LongEdge when image.LongEdge > 0:
                scale = image.LongEdge / longEdge;
                break;
            case ResizeMode.Percent when image.ResizePercent is > 0 and not 100:
                scale *= image.ResizePercent / 100f;
                break;
        }
        var limit = Math.Min(MaximumLongEdge / longEdge, MathF.Sqrt(MaximumPixels / (bounds.Width * bounds.Height)));
        return Math.Max(Math.Min(scale, limit), 1e-3f);
    }

    /// <summary>Renders the drawing on a transparent bitmap and hands it to ImageMagick for encoding.</summary>
    public static MagickImage Rasterize(Drawing drawing, float scale)
    {
        var width = Math.Max(1, (int)Math.Ceiling(drawing.Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(drawing.Bounds.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-drawing.Bounds.Left, -drawing.Bounds.Top);
            canvas.DrawPicture(drawing.Picture);
        }

        // Skia draws premultiplied; ImageMagick expects straight alpha.
        using var straight = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var source = bitmap.PeekPixels())
        using (var target = straight.PeekPixels())
        {
            if (!source.ReadPixels(target))
                throw new InvalidOperationException("Could not read the rendered SVG.");
        }
        var pixels = straight.GetPixelSpan().ToArray();

        var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        var dpi = CssDpi * scale;
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        return image;
    }
}
