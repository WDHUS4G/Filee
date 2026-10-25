// DWG / DXF conversion without AutoCAD: ACadSharp (MIT) reads DWG R13–2018+ and DXF (ASCII and binary) and writes
// DXF and DWG; the drawing is rendered with SkiaSharp to PDF (vector), SVG and raster images.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Filee.Engines.Cad;

/// <summary>
/// DWG ↔ DXF and DWG / DXF → PDF, SVG, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF.
/// </summary>
/// <remarks>
/// Rendering draws model space as seen from the top (the active layout when model space is empty), fitted to the
/// page: PDF on an A3 sheet (A4 / Letter when the preset says so), SVG and images with the drawing's aspect ratio
/// on a white background. A PDF of a drawing with paper-space layouts has one page per layout instead, with model
/// space shown through each layout's viewports. See docs/ENGINES.md for what is and is not drawn.
/// </remarks>
public sealed class CadConverter(ILogger? logger = null) : IConverter, IImageTransformer
{
    private static readonly string[] Sources = ["dwg", "dxf"];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Id => "cad";
    public string DisplayName => "CAD (built-in)";
    /// <summary>
    /// A loaded drawing takes several times its file size in memory (a 55 MB DXF peaks around 800 MB), so only
    /// a few are converted at once.
    /// </summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Only when rendering a drawing to an image (not to PDF, SVG or between DWG and DXF).</summary>
    public bool Transforms(string from, string to) => ImageEncoder.Writable.Contains(to);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("dwg", "dxf"),
        new("dxf", "dwg"),
        // Into another version of the same format (#489).
        new("dwg", "dwg"),
        new("dxf", "dxf"),
        .. from source in Sources
           from target in (string[])["pdf", "svg", .. ImageEncoder.Writable]
           select ImageEncoder.Edge(source, target),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("ACadSharp: DWG R13–2018+, DXF", $"{EngineVersions.BuiltIn} · {EngineVersions.Library("ACadSharp", typeof(ACadSharp.CadDocument))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.05);
            var document = CadFile.Read(step.InputPath, step.From, message => _logger.LogDebug("{File}: {Message}", step.InputPath, message));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.4);

            if (step.To is "dxf" or "dwg")
            {
                if (step.Output.Allocate(step.To) is not { } path)
                    return [];
                if (CadFile.Write(document, path, step.To, step.Preset.Cad.Version, message => _logger.LogDebug("{File}: {Message}", path, message)) > 0)
                    step.Notice?.Invoke("notice.cad_newer_left_out");
                progress?.Report(1);
                return [path];
            }

            var scene = CadSceneBuilder.Build(document, cancellationToken);
            Report(step.InputPath, scene);
            // A PDF follows the layouts like a plot of every sheet; images and SVG stay one picture of model space.
            var sheets = step.To == "pdf" ? CadSceneBuilder.BuildLayouts(document, cancellationToken) : [];
            if (sheets.Count > 0)
                _logger.LogInformation("{File}: {Count} layout(s), one PDF page each", step.InputPath, sheets.Count);
            if (scene.Items.Count == 0 && sheets.Count == 0)
                throw new InvalidDataException(scene.Skipped.Count > 0
                    ? $"The drawing only contains objects Filee cannot draw ({string.Join(", ", scene.Skipped.Keys)})."
                    : "The drawing has nothing visible to draw: model space and the layout are empty, or every entity is on a layer that is off, frozen or not plotted.");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7);

            var output = step.Output.Allocate(step.To);
            if (output is null)
                return [];
            switch (step.To)
            {
                case "pdf":
                    using (var stream = File.Create(output))
                    {
                        // With empty model space the scene is the active layout's paper space, which must not show
                        // through the viewports.
                        if (sheets.Count > 0)
                            CadRenderer.WritePdf(sheets, scene.Space == "model" ? scene : new CadScene(), step.Preset.Pdf,
                                Path.GetFileNameWithoutExtension(step.InputPath), stream);
                        else
                            CadRenderer.WritePdf(scene, step.Preset.Pdf, Path.GetFileNameWithoutExtension(step.InputPath), stream);
                    }
                    break;
                case "svg":
                    using (var stream = File.Create(output))
                        CadRenderer.WriteSvg(scene, stream);
                    break;
                default:
                    WriteImage(scene, step, output);
                    break;
            }
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>
    /// Renders at the preset's render resolution (PDF → image uses the same setting; 150 dpi by default, for an
    /// A3-sized page that is about 2500 px on the long edge), then applies the image options like other image
    /// conversions do (resize, grayscale, quality, DPI).
    /// </summary>
    private static void WriteImage(CadScene scene, ConversionStep step, string output)
    {
        var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
        using var bitmap = CadRenderer.RenderBitmap(scene, dpi);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var image = new MagickImage(data.ToArray());
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
        ImageEncoder.WriteImage(image, output, step.To);
    }

    private void Report(string input, CadScene scene)
    {
        if (scene.Space != "model")
            _logger.LogInformation("{File}: model space is empty, drew layout {Layout}", input, scene.Space);
        if (scene.Skipped.Count > 0)
            _logger.LogInformation("{File}: skipped {Count} unsupported entities ({Types})", input,
                scene.Skipped.Values.Sum(), string.Join(", ", scene.Skipped.Select(p => $"{p.Key} ×{p.Value}")));
        if (scene.Truncated)
            _logger.LogWarning("{File}: the drawing exceeds the rendering limits and was cut short", input);
    }
}
