// Built-in vector graphics: SVG/SVGZ → PDF (vector) and → raster images with Svg.Skia and SkiaSharp, SVG ↔ SVGZ,
// SVG → DXF (ACadSharp), raster images → SVG (tracing) and Illustrator files → PDF. Other vector formats reach PNG,
// JPG, … through PDF and PDFium.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Vector.Tracing;

namespace Filee.Engines.Vector;

/// <summary>SVG, SVGZ and AI conversions and raster image tracing that need no external program.</summary>
public sealed class VectorConverter : IConverter, IImageTransformer
{
    /// <summary>
    /// Cost of tracing a raster image into SVG. Tracing redraws the picture, so it must never be a step on the way to
    /// another format (PNG → SVG → PDF instead of PNG → PDF): its cost is higher than any route from an image to a
    /// format SVG leads to, and the planner only takes it when SVG (or DXF, DWG through SVG) is the target. Its edges
    /// are for raster sources only: a PDF, EPS or other vector file that can be rendered to PNG is never traced.
    /// </summary>
    public const int TraceCost = 40;

    public string Id => "vector";
    public string DisplayName => "Vector graphics (built-in)";
    public int MaxParallelism => 0;

    /// <summary>Only when rendering to an image (not to PDF or between SVG and SVGZ).</summary>
    public bool Transforms(string from, string to) => ImageEncoder.Writable.Contains(to);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("svg", "pdf"),
        new("svgz", "pdf"),
        new("svg", "svgz"),
        new("svgz", "svg"),
        .. ImageEncoder.Writable.SelectMany(target => new[] { ImageEncoder.Edge("svg", target), ImageEncoder.Edge("svgz", target) }),
        new("svg", "dxf"),
        new("svgz", "dxf"),
        // Illustrator files are PDF inside (or PostScript, see ConvertIllustratorAsync).
        new("ai", "pdf"),
        // Raster images are traced (other raster formats through PNG, e.g. ICNS; never vector files or documents).
        .. ImageEncoder.Readable.Select(source => new ConversionEdge(source, "svg", TraceCost, ImageSourcesOnly: true)),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: $"{EngineVersions.BuiltIn} · {EngineVersions.Library("Svg.Skia", typeof(Svg.Skia.SKSvg))}");

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var written = (step.From, step.To) switch
        {
            ("ai", "pdf") => await ConvertIllustratorAsync(step, progress, cancellationToken),
            ("svg", "svgz") => await Task.Run(() => Compress(step), cancellationToken),
            ("svgz", "svg") => await Task.Run(() => Decompress(step), cancellationToken),
            (_, "svg") => await Task.Run(() => RasterTracing.Run(step, progress, cancellationToken), cancellationToken),
            (_, "dxf") => await Task.Run(() => ToDxf(step, cancellationToken), cancellationToken),
            (_, "pdf") => await Task.Run(() => ToPdf(step, cancellationToken), cancellationToken),
            _ => await Task.Run(() => ToRaster(step, progress, cancellationToken), cancellationToken),
        };
        progress?.Report(1);
        return written;
    }

    private static IReadOnlyList<string> ToPdf(ConversionStep step, CancellationToken ct)
    {
        using var drawing = SvgRenderer.Load(step.InputPath);
        ct.ThrowIfCancellationRequested();
        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        SvgRenderer.WritePdf(drawing, output, FormatRegistry.NameWithoutExtension(step.InputPath), step.Preset.Document.PdfA);
        return [output];
    }

    private static IReadOnlyList<string> ToDxf(ConversionStep step, CancellationToken ct)
    {
        using var drawing = SvgRenderer.Load(step.InputPath);
        ct.ThrowIfCancellationRequested();
        var output = step.Output.Allocate("dxf");
        if (output is null)
            return [];
        SvgDxfWriter.Write(drawing, output, ct);
        return [output];
    }

    private static IReadOnlyList<string> ToRaster(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        using var drawing = SvgRenderer.Load(step.InputPath);
        progress?.Report(0.2);
        var scale = SvgRenderer.RasterScale(drawing.Bounds, step.Preset.Image, step.Preset.Pdf);
        using var image = SvgRenderer.Rasterize(drawing, scale);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        // The drawing was rendered at its final size, so the preset's resize must not scale the bitmap again.
        var options = step.Preset.Image.Clone();
        options.Resize = Core.Presets.ResizeMode.None;
        ImageEncoder.ApplyOptions(image, options, step.To);
        return ImageEncoder.Write([image], step.To, step.Output, options);
    }

    private static IReadOnlyList<string> Compress(ConversionStep step)
    {
        var bytes = File.ReadAllBytes(step.InputPath);
        if (!SvgRenderer.IsGzip(bytes))
            RequireSvgText(bytes);
        var output = step.Output.Allocate("svgz");
        if (output is null)
            return [];
        if (SvgRenderer.IsGzip(bytes))
        {
            File.WriteAllBytes(output, bytes); // a gzipped file named .svg: already SVGZ
            return [output];
        }
        using (var file = File.Create(output))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(bytes);
        return [output];
    }

    private static IReadOnlyList<string> Decompress(ConversionStep step)
    {
        var bytes = SvgRenderer.ReadSvgBytes(step.InputPath);
        RequireSvgText(bytes);
        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        File.WriteAllBytes(output, bytes);
        return [output];
    }

    /// <summary>Guards SVG ↔ SVGZ, which never parse the drawing, against renamed non-SVG files.</summary>
    private static void RequireSvgText(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (!head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not an SVG drawing (no <svg> element).");
    }

    // ───────────────────────── Illustrator ─────────────────────────

    /// <summary>
    /// Illustrator 9 and later save a complete PDF inside the .ai file ("Create PDF compatible file", on by
    /// default), so the file is copied as a PDF after checking its header. Files saved without it (and Illustrator 8
    /// and older) are PostScript, which needs Ghostscript.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ConvertIllustratorAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var kind = IllustratorKind(step.InputPath);
        if (kind == AiKind.Unknown)
            throw new InvalidDataException("The file is not an Adobe Illustrator document (neither PDF nor PostScript inside).");

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        if (kind == AiKind.Pdf)
        {
            File.Copy(step.InputPath, output, overwrite: true);
            return [output];
        }

        var gs = GhostscriptConverter.Locate()
                 ?? throw new InvalidOperationException(
                     "This Illustrator file was saved without PDF compatibility, so it is PostScript. " +
                     "Install Ghostscript in Settings → Engines to convert it, or save it in Illustrator with \"Create PDF compatible file\".");
        await GhostscriptConverter.ConvertToPdfAsync(gs, step.InputPath, output, crop: true, step.WorkDirectory, ct, progress);
        return [output];
    }

    internal enum AiKind
    {
        Unknown,
        Pdf,
        PostScript,
    }

    /// <summary>
    /// Looks at the header: PDF-compatible files start with "%PDF-" (a PDF reader accepts it within the first 1 KB);
    /// PostScript-based ones with "%!PS-Adobe" or the binary header of a DOS EPS file (C5 D0 D3 C6).
    /// </summary>
    internal static AiKind IllustratorKind(string path)
    {
        var buffer = new byte[1024];
        int read;
        using (var file = File.OpenRead(path))
            read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var head = buffer.AsSpan(0, read);
        if (head.IndexOf("%PDF-"u8) >= 0)
            return AiKind.Pdf;
        if (head.StartsWith("%!PS"u8) || head.StartsWith((ReadOnlySpan<byte>)[0xC5, 0xD0, 0xD3, 0xC6]))
            return AiKind.PostScript;
        return AiKind.Unknown;
    }
}
