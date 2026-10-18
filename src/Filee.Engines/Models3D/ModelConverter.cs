// 3D models in managed code only (SharpGLTF for glTF, Filee's own readers and writers for the rest): GLB, glTF, STL,
// OBJ, 3MF and PLY into each other through one in-memory model (Model3D), and an isometric preview picture.
// See docs/ENGINES.md, "3D models", for units, axes and what each format keeps.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Models3D;

/// <summary>Converts 3D models between GLB, glTF, STL, OBJ, 3MF and PLY, and renders them to pictures.</summary>
public sealed class ModelConverter(ILogger? logger = null) : IConverter, IImageTransformer
{
    /// <summary>The 3D formats, each readable and writable.</summary>
    public static readonly string[] Formats = ["glb", "gltf", "stl", "obj", "3mf", "ply"];

    /// <summary>Long edge of the preview picture before the preset's resize.</summary>
    internal const int PreviewSize = 1024;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Id => "model3d";
    public string DisplayName => "3D models (built-in)";

    /// <summary>The rendered picture goes through the image options, rotation and flip included.</summary>
    public bool Transforms(string from, string to) => ImageEncoder.Writable.Contains(to);

    /// <summary>A large mesh takes a few hundred MB while it is converted, so only a few run at once.</summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in Formats from target in Formats where source != target select new ConversionEdge(source, target),
        .. from source in Formats from target in ImageEncoder.Writable select ImageEncoder.Edge(source, target),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("GLB, glTF, STL, OBJ, 3MF, PLY",
        $"{EngineVersions.BuiltIn} · {EngineVersions.Library("SharpGLTF", typeof(SharpGLTF.Schema2.ModelRoot))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var reading = progress is null ? null : new SubProgress(progress, 0, 0.6);
            var model = Read(step.InputPath, step.From, reading, cancellationToken);
            _logger.LogDebug("{File}: {Parts} parts, {Vertices} vertices, {Triangles} triangles, {Materials} materials",
                step.InputPath, model.Parts.Count, model.VertexCount, model.TriangleCount, model.Materials.Count);
            progress?.Report(0.6);

            var outputs = new List<string>();
            try
            {
                Write(model, step.To, step.Output, step.Preset.Image, outputs, cancellationToken);
            }
            catch
            {
                foreach (var output in outputs)
                    TryDelete(output);
                throw;
            }
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    /// <summary>Reads a model of format <paramref name="format"/> and checks it.</summary>
    internal static Model3D Read(string path, string format, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = format switch
        {
            "stl" => StlFormat.Read(path, progress, cancellationToken),
            "obj" => ObjFormat.Read(path, progress, cancellationToken),
            "ply" => PlyFormat.Read(path, progress, cancellationToken),
            "3mf" => ThreeMfFormat.Read(path, progress, cancellationToken),
            "glb" or "gltf" => GltfFormat.Read(path, progress, cancellationToken),
            _ => throw new NotSupportedException($"Unknown 3D format '{format}'."),
        };
        model.Validate();
        return model;
    }

    /// <summary>Writes <paramref name="model"/> as <paramref name="format"/>; every file written is added to <paramref name="outputs"/>.</summary>
    internal static void Write(Model3D model, string format, IOutputAllocator output, ImageOptions imageOptions, List<string> outputs, CancellationToken cancellationToken)
    {
        if (format == "obj")
        {
            WriteObj(model, output, outputs, cancellationToken);
            return;
        }
        if (output.Allocate(format) is not { } path)
            return;
        outputs.Add(path);
        if (!Formats.Contains(format))
        {
            WriteImage(model, format, imageOptions, path, cancellationToken);
            return;
        }
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        switch (format)
        {
            case "stl":
                StlFormat.Write(model, stream, cancellationToken);
                break;
            case "ply":
                PlyFormat.Write(model, stream, cancellationToken);
                break;
            case "3mf":
                ThreeMfFormat.Write(model, stream, cancellationToken);
                break;
            case "glb":
            case "gltf":
                GltfFormat.Write(model, stream, binary: format == "glb", cancellationToken);
                break;
        }
    }

    /// <summary>The .obj, the .mtl next to it (when the model has materials) and the textures next to that.</summary>
    private static void WriteObj(Model3D model, IOutputAllocator output, List<string> outputs, CancellationToken cancellationToken)
    {
        if (output.Allocate("obj") is not { } obj)
            return;
        outputs.Add(obj);
        string? mtl = null;
        var textures = new Dictionary<Texture3D, string>();
        if (model.Materials.Count > 0)
        {
            mtl = output.Allocate("mtl");
            if (mtl is not null)
            {
                outputs.Add(mtl);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var texture in model.Materials.Select(m => m.BaseColorTexture).OfType<Texture3D>().Distinct())
                {
                    var name = SafeName(texture.Name);
                    for (var i = 2; !names.Add(name); i++)
                        name = $"{SafeName(texture.Name)}_{i}";
                    if (output.Allocate(texture.Extension.Length > 0 ? texture.Extension : "png", "_" + name) is not { } file)
                        continue;
                    outputs.Add(file);
                    File.WriteAllBytes(file, texture.Data);
                    textures[texture] = file;
                }
            }
        }
        ObjFormat.Write(model, obj, mtl, textures, cancellationToken);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Where(c => !invalid.Contains(c) && !char.IsWhiteSpace(c))]);
        return clean.Length == 0 ? "texture" : clean.Length > 40 ? clean[..40] : clean;
    }

    /// <summary>An isometric preview, then the preset's image options (resize, background, quality) as for other pictures.</summary>
    private static void WriteImage(Model3D model, string format, ImageOptions options, string path, CancellationToken cancellationToken)
    {
        var png = MeshRenderer.RenderPng(model, PreviewSize, cancellationToken);
        using var image = new MagickImage(png);
        ImageEncoder.ApplyOptions(image, options, format);
        image.Write(path, ImageEncoder.ToMagickFormat(format));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Maps 0–1 progress of one phase onto a slice of the step's progress, synchronously.</summary>
    private sealed class SubProgress(IProgress<double> parent, double start, double length) : IProgress<double>
    {
        public void Report(double value) => parent.Report(start + length * value);
    }
}
