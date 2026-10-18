// Generated 3D test meshes (no sample files): a textured cube with face normals and UVs, a sphere with smooth
// normals and vertex colours, and a flat fan of coloured triangles. All in Filee's model convention: millimetres,
// Y up, counter-clockwise front faces.

using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Models3D;
using SkiaSharp;

namespace Filee.Engines.Tests;

internal static class ModelBuilders
{
    public static readonly Vector4 CubeColor = new(0.8f, 0.2f, 0.1f, 1);

    /// <summary>A 20 mm cube from (0, 0, 0) to (20, 20, 20): 24 vertices with face normals and UVs, a textured material.</summary>
    public static Model3D Cube()
    {
        var model = new Model3D();
        model.Materials.Add(new Material3D { Name = "Brick red", BaseColor = CubeColor, BaseColorTexture = new Texture3D(Checker(), "png", "checker") });
        var part = new MeshPart { Name = "Cube", Material = 0, Normals = [], TexCoords = [] };
        var center = new Vector3(10);
        foreach (var n in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var u = MathF.Abs(n.Y) < 0.9f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n)) : Vector3.Normalize(Vector3.Cross(n, Vector3.UnitX));
            var v = Vector3.Cross(n, u);
            var face = center + n * 10;
            var first = part.AddVertex(face + (-u - v) * 10, n, new Vector2(0, 0));
            part.AddVertex(face + (u - v) * 10, n, new Vector2(1, 0));
            part.AddVertex(face + (u + v) * 10, n, new Vector2(1, 1));
            part.AddVertex(face + (-u + v) * 10, n, new Vector2(0, 1));
            part.AddTriangle(first, first + 1, first + 2);
            part.AddTriangle(first, first + 2, first + 3);
        }
        model.Parts.Add(part);
        return model;
    }

    /// <summary>A sphere of radius 10 mm around the origin with smooth normals and colours from the normal direction.</summary>
    public static Model3D Sphere(int stacks = 12, int slices = 16)
    {
        var part = new MeshPart { Name = "Sphere", Normals = [], Colors = [] };
        int Add(Vector3 n) => part.AddVertex(n * 10, n, color: SphereColor(n));
        var top = Add(Vector3.UnitY);
        var rings = new int[stacks - 1, slices];
        for (var i = 1; i < stacks; i++)
        {
            var phi = MathF.PI * i / stacks;
            for (var j = 0; j < slices; j++)
            {
                var theta = 2 * MathF.PI * j / slices;
                rings[i - 1, j] = Add(new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), -MathF.Sin(phi) * MathF.Sin(theta)));
            }
        }
        var bottom = Add(-Vector3.UnitY);
        for (var j = 0; j < slices; j++)
        {
            var next = (j + 1) % slices;
            part.AddTriangle(top, rings[0, j], rings[0, next]);
            for (var i = 0; i < stacks - 2; i++)
            {
                part.AddTriangle(rings[i, j], rings[i + 1, j], rings[i + 1, next]);
                part.AddTriangle(rings[i, j], rings[i + 1, next], rings[i, next]);
            }
            part.AddTriangle(bottom, rings[stacks - 2, next], rings[stacks - 2, j]);
        }
        var model = new Model3D();
        model.Parts.Add(part);
        return model;
    }

    /// <summary>The colour the sphere has where its normal is <paramref name="n"/> (sRGB).</summary>
    public static Vector4 SphereColor(Vector3 n) => new((n.X + 1) / 2, (n.Y + 1) / 2, (n.Z + 1) / 2, 1);

    /// <summary>Six triangles around a white centre, flat in the XZ plane facing up, with pure colours on the rim.</summary>
    public static Model3D Fan()
    {
        var part = new MeshPart { Name = "Fan", Colors = [] };
        var center = part.AddVertex(Vector3.Zero, color: Vector4.One);
        Vector4[] colors = [new(1, 0, 0, 1), new(1, 1, 0, 1), new(0, 1, 0, 1), new(0, 1, 1, 1), new(0, 0, 1, 1), new(1, 0, 1, 1)];
        for (var k = 0; k < 6; k++)
        {
            var angle = MathF.PI / 3 * k;
            part.AddVertex(new Vector3(MathF.Cos(angle), 0, -MathF.Sin(angle)) * 10, color: colors[k]);
        }
        for (var k = 0; k < 6; k++)
            part.AddTriangle(center, 1 + k, 1 + (k + 1) % 6);
        var model = new Model3D();
        model.Parts.Add(part);
        return model;
    }

    public static Model3D Make(string kind) => kind switch
    {
        "cube" => Cube(),
        "sphere" => Sphere(),
        "fan" => Fan(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A 4 × 4 PNG checkerboard.</summary>
    public static byte[] Checker()
    {
        using var bitmap = new SKBitmap(4, 4);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? new SKColor(250, 240, 230) : new SKColor(40, 60, 200));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Writes <paramref name="model"/> as <paramref name="format"/> into <paramref name="folder"/>; returns the main file.</summary>
    public static string Save(Model3D model, string folder, string name, string format)
    {
        var outputs = new List<string>();
        ModelConverter.Write(model, format, new FolderAllocator(folder, name), new ImageOptions(), outputs, CancellationToken.None);
        return outputs[0];
    }

    public static Model3D Load(string path, string format) => ModelConverter.Read(path, format, null, CancellationToken.None);

    /// <summary>Signed volume (positive for a closed mesh with outward faces): changes sign when the winding flips.</summary>
    public static double SignedVolume(Model3D model)
    {
        double volume = 0;
        foreach (var part in model.Parts)
        {
            for (var t = 0; t < part.TriangleCount; t++)
            {
                var a = part.Position(part.Indices[t * 3]);
                var b = part.Position(part.Indices[t * 3 + 1]);
                var c = part.Position(part.Indices[t * 3 + 2]);
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
        }
        return volume;
    }

    public static double Area(Model3D model)
    {
        double area = 0;
        foreach (var part in model.Parts)
        {
            for (var t = 0; t < part.TriangleCount; t++)
            {
                var a = part.Position(part.Indices[t * 3]);
                var b = part.Position(part.Indices[t * 3 + 1]);
                var c = part.Position(part.Indices[t * 3 + 2]);
                area += Vector3.Cross(b - a, c - a).Length() / 2;
            }
        }
        return area;
    }

    /// <summary>Rounds a position to 1/100 mm so positions from different files compare equal.</summary>
    public static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 100), (int)MathF.Round(p.Y * 100), (int)MathF.Round(p.Z * 100));

    public static HashSet<(int, int, int)> Positions(Model3D model) =>
        [.. model.Parts.SelectMany(p => Enumerable.Range(0, p.VertexCount).Select(v => Key(p.Position(v))))];

    /// <summary>Hands out files named <c>name{suffix}.ext</c> in one folder.</summary>
    public sealed class FolderAllocator(string folder, string name) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"{name}{suffix}.{extension}");
    }
}
