// The in-memory 3D model every reader produces and every writer takes (see docs/ENGINES.md, "3D models").
//
// Conventions, chosen once so every reader and writer agrees:
// - positions are millimetres, Y up (glTF's axes). Unitless files (STL, OBJ, PLY) are read as millimetres; STL, 3MF
//   and PLY are Z up like Blender treats them, OBJ and glTF Y up;
// - triangles only (polygons are triangulated on reading), counter-clockwise front faces;
// - UVs have their origin at the bottom left (OBJ, PLY); glTF's top-left origin is flipped on the way in and out;
// - vertex colours are sRGB 0–1 (as PLY, 3MF and OBJ store them), material colours linear (as glTF stores them);
// - node hierarchies are flattened: every mesh instance becomes a part in world coordinates.

using System.Numerics;
using System.Runtime.InteropServices;

namespace Filee.Engines.Models3D;

/// <summary>A whole model: parts (meshes) and the materials they use.</summary>
internal sealed class Model3D
{
    public List<MeshPart> Parts { get; } = [];
    public List<Material3D> Materials { get; } = [];

    public int TriangleCount => Parts.Sum(p => p.TriangleCount);
    public int VertexCount => Parts.Sum(p => p.VertexCount);

    /// <summary>The material of <paramref name="part"/>, or null.</summary>
    public Material3D? MaterialOf(MeshPart part) =>
        part.Material >= 0 && part.Material < Materials.Count ? Materials[part.Material] : null;

    /// <summary>Axis-aligned bounds of every position, or (0, 0) for an empty model.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in Parts)
        {
            var p = part.PositionSpan;
            for (var i = 0; i + 2 < p.Length; i += 3)
            {
                var v = new Vector3(p[i], p[i + 1], p[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>Drops parts without triangles and checks every index, so writers can trust the data.</summary>
    public void Validate()
    {
        Parts.RemoveAll(p => p.TriangleCount == 0);
        foreach (var part in Parts)
            part.Validate();
        if (Parts.Count == 0)
            throw new InvalidDataException("The file contains no triangles (only points, lines or nothing at all).");
    }
}

/// <summary>One mesh with per-vertex attributes and a triangle list.</summary>
internal sealed class MeshPart
{
    public string Name { get; set; } = "";

    /// <summary>Index into <see cref="Model3D.Materials"/>, or -1.</summary>
    public int Material { get; set; } = -1;

    /// <summary>x, y, z per vertex (millimetres, Y up).</summary>
    public List<float> Positions { get; } = [];

    /// <summary>x, y, z per vertex, or null.</summary>
    public List<float>? Normals { get; set; }

    /// <summary>u, v per vertex (origin bottom left), or null.</summary>
    public List<float>? TexCoords { get; set; }

    /// <summary>r, g, b, a per vertex (sRGB, 0–1), or null.</summary>
    public List<float>? Colors { get; set; }

    /// <summary>Three vertex indices per triangle.</summary>
    public List<int> Indices { get; } = [];

    public int VertexCount => Positions.Count / 3;
    public int TriangleCount => Indices.Count / 3;

    public Span<float> PositionSpan => CollectionsMarshal.AsSpan(Positions);
    public Span<int> IndexSpan => CollectionsMarshal.AsSpan(Indices);

    public Vector3 Position(int vertex) => new(Positions[vertex * 3], Positions[vertex * 3 + 1], Positions[vertex * 3 + 2]);

    public Vector3 Normal(int vertex) => Normals is null ? Vector3.Zero : new(Normals[vertex * 3], Normals[vertex * 3 + 1], Normals[vertex * 3 + 2]);

    public Vector2 TexCoord(int vertex) => TexCoords is null ? Vector2.Zero : new(TexCoords[vertex * 2], TexCoords[vertex * 2 + 1]);

    public Vector4 Color(int vertex) => Colors is null ? Vector4.One : new(Colors[vertex * 4], Colors[vertex * 4 + 1], Colors[vertex * 4 + 2], Colors[vertex * 4 + 3]);

    /// <summary>Appends a vertex; attributes the part does not have are ignored.</summary>
    public int AddVertex(Vector3 position, Vector3? normal = null, Vector2? uv = null, Vector4? color = null)
    {
        var index = VertexCount;
        Positions.Add(position.X);
        Positions.Add(position.Y);
        Positions.Add(position.Z);
        if (Normals is not null)
        {
            var n = normal ?? Vector3.Zero;
            Normals.Add(n.X);
            Normals.Add(n.Y);
            Normals.Add(n.Z);
        }
        if (TexCoords is not null)
        {
            var t = uv ?? Vector2.Zero;
            TexCoords.Add(t.X);
            TexCoords.Add(t.Y);
        }
        if (Colors is not null)
        {
            var c = color ?? Vector4.One;
            Colors.Add(c.X);
            Colors.Add(c.Y);
            Colors.Add(c.Z);
            Colors.Add(c.W);
        }
        return index;
    }

    public void AddTriangle(int a, int b, int c)
    {
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);
    }

    /// <summary>The geometric normal of triangle <paramref name="triangle"/> (from its winding), or zero when degenerate.</summary>
    public Vector3 FaceNormal(int triangle)
    {
        var a = Position(Indices[triangle * 3]);
        var b = Position(Indices[triangle * 3 + 1]);
        var c = Position(Indices[triangle * 3 + 2]);
        var n = Vector3.Cross(b - a, c - a);
        var length = n.Length();
        return length > 0 && float.IsFinite(length) ? n / length : Vector3.Zero;
    }

    /// <summary>
    /// Smooth vertex normals: the area-weighted sum of the faces around each vertex (vertices that are split at hard
    /// edges stay hard).
    /// </summary>
    public void ComputeNormals()
    {
        var normals = new float[Positions.Count];
        var indices = IndexSpan;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = Position(indices[t]);
            var n = Vector3.Cross(Position(indices[t + 1]) - a, Position(indices[t + 2]) - a);
            for (var k = 0; k < 3; k++)
            {
                var v = indices[t + k] * 3;
                normals[v] += n.X;
                normals[v + 1] += n.Y;
                normals[v + 2] += n.Z;
            }
        }
        for (var v = 0; v < normals.Length; v += 3)
        {
            var n = Vector3.Normalize(new Vector3(normals[v], normals[v + 1], normals[v + 2]));
            if (!float.IsFinite(n.X))
                n = Vector3.UnitY;
            normals[v] = n.X;
            normals[v + 1] = n.Y;
            normals[v + 2] = n.Z;
        }
        Normals = [.. normals];
    }

    /// <summary>Applies a transform to positions and normals; a mirroring transform also flips the winding.</summary>
    public void Transform(Matrix4x4 matrix)
    {
        if (matrix.IsIdentity)
            return;
        var p = PositionSpan;
        for (var i = 0; i + 2 < p.Length; i += 3)
        {
            var v = Vector3.Transform(new Vector3(p[i], p[i + 1], p[i + 2]), matrix);
            p[i] = v.X;
            p[i + 1] = v.Y;
            p[i + 2] = v.Z;
        }
        if (Normals is not null)
        {
            var normalMatrix = Matrix4x4.Invert(matrix, out var inverse) ? Matrix4x4.Transpose(inverse) : matrix;
            var n = CollectionsMarshal.AsSpan(Normals);
            for (var i = 0; i + 2 < n.Length; i += 3)
            {
                var v = Vector3.TransformNormal(new Vector3(n[i], n[i + 1], n[i + 2]), normalMatrix);
                var length = v.Length();
                if (length > 0)
                    v /= length;
                n[i] = v.X;
                n[i + 1] = v.Y;
                n[i + 2] = v.Z;
            }
        }
        if (matrix.GetDeterminant() < 0)
        {
            var indices = IndexSpan;
            for (var t = 0; t + 2 < indices.Length; t += 3)
                (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        }
    }

    /// <summary>Checks that every index points at a vertex and every attribute list has the vertex count.</summary>
    public void Validate()
    {
        if (Indices.Count % 3 != 0)
            throw new InvalidDataException("A mesh has an incomplete triangle.");
        var count = VertexCount;
        foreach (var index in IndexSpan)
        {
            if ((uint)index >= (uint)count)
                throw new InvalidDataException($"A triangle refers to vertex {index + 1}, but the mesh has only {count} vertices.");
        }
        if (Normals is not null && Normals.Count != count * 3)
            Normals = null;
        if (TexCoords is not null && TexCoords.Count != count * 2)
            TexCoords = null;
        if (Colors is not null && Colors.Count != count * 4)
            Colors = null;
    }
}

/// <summary>A simple material: base colour (linear RGBA, as glTF) and an optional base colour texture.</summary>
internal sealed class Material3D
{
    public string Name { get; set; } = "";
    public Vector4 BaseColor { get; set; } = Vector4.One;
    public Texture3D? BaseColorTexture { get; set; }
    public bool DoubleSided { get; set; }
}

/// <summary>An encoded picture (PNG, JPEG, ...) used as a texture.</summary>
/// <param name="Data">The file's bytes.</param>
/// <param name="Extension">Lower-case extension without the dot ("png", "jpg").</param>
/// <param name="Name">Original file name without extension, used for written texture files.</param>
internal sealed record Texture3D(byte[] Data, string Extension, string Name)
{
    /// <summary>Recognises PNG, JPEG, WebP, BMP, GIF and TGA-less pictures by their signature, else the given extension.</summary>
    public static string Sniff(ReadOnlySpan<byte> data, string fallback)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            return "png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "jpg";
        if (data.Length > 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        if (data.StartsWith("BM"u8))
            return "bmp";
        if (data.StartsWith("GIF8"u8))
            return "gif";
        var ext = fallback.TrimStart('.').ToLowerInvariant();
        return ext is "jpeg" ? "jpg" : ext;
    }
}

/// <summary>Colour space and axis helpers shared by the readers and writers.</summary>
internal static class MeshMath
{
    /// <summary>Z up (STL, 3MF, PLY) → Y up: (x, y, z) → (x, z, −y).</summary>
    public static readonly Matrix4x4 ZUpToYUp = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    /// <summary>Y up → Z up: (x, y, z) → (x, −z, y).</summary>
    public static Vector3 ToZUp(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>Z up → Y up: (x, y, z) → (x, z, −y).</summary>
    public static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    public static Vector4 SrgbToLinear(Vector4 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z), c.W);

    public static Vector4 LinearToSrgb(Vector4 c) => new(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z), c.W);

    /// <summary>0–1 → 0–255, rounded and clamped.</summary>
    public static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    /// <summary>The 0–1 colour as #RRGGBB, or #RRGGBBAA when not opaque (3MF).</summary>
    public static string ToHex(Vector4 srgb)
    {
        var hex = $"#{ToByte(srgb.X):X2}{ToByte(srgb.Y):X2}{ToByte(srgb.Z):X2}";
        return ToByte(srgb.W) == 255 ? hex : hex + ToByte(srgb.W).ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Parses #RGB, #RRGGBB or #RRGGBBAA (3MF colours), or null.</summary>
    public static Vector4? ParseHex(string? text)
    {
        if (text is null || !text.StartsWith('#'))
            return null;
        var hex = text[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value))
            return null;
        if (hex.Length == 6)
            value = (value << 8) | 0xFF;
        return new Vector4((value >> 24) / 255f, ((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
    }
}
