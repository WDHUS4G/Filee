// glTF 2.0 (.gltf with embedded or separate buffers, and binary .glb) through SharpGLTF (MIT). Metres, Y up.
// Read: the default scene's nodes flattened to world coordinates, triangle primitives (strips and fans too) with
// POSITION, NORMAL, TEXCOORD_n, COLOR_0, and the base colour factor and texture of metallic-roughness or
// specular-glossiness materials. Points, lines, skins, morph targets, animations and cameras are ignored.
// Written: one node and mesh per part, metallic-roughness materials (not metallic, as OBJ / 3MF colours are meant),
// textures as PNG or JPEG. A .gltf is written as one self-contained file: the buffer is a base64 data URI.

using System.Numerics;
using System.Text.Json.Nodes;
using ImageMagick;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Filee.Engines.Models3D;

internal static class GltfFormat
{
    private const float MillimetresPerMetre = 1000;

    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ModelRoot root;
        try
        {
            root = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.Skip });
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidDataException($"A file the glTF model refers to is missing: {Path.GetFileName(ex.FileName ?? ex.Message)}", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException(Describe(ex), ex);
        }
        progress?.Report(0.4);
        if (root.ExtensionsUsed.FirstOrDefault(e => e is "KHR_draco_mesh_compression" or "EXT_meshopt_compression" or "KHR_meshopt_compression") is { } compression)
            throw new InvalidDataException(Describe(new NotSupportedException(compression)));

        try
        {
            return ReadScene(root, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or InvalidDataException))
        {
            throw new InvalidDataException($"The glTF model could not be read: {ex.Message}", ex);
        }
    }

    private static Model3D ReadScene(ModelRoot root, CancellationToken cancellationToken)
    {
        var model = new Model3D();
        var materials = new Dictionary<Material, int>();
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault();
        var nodes = scene is not null
            ? Node.Flatten(scene)
            : root.LogicalNodes.Where(n => n.VisualParent is null).SelectMany(n => Node.Flatten(n));
        var scale = Matrix4x4.CreateScale(MillimetresPerMetre);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Mesh is not { } mesh)
                continue;
            var world = node.WorldMatrix * scale;
            foreach (var primitive in mesh.Primitives)
            {
                if (primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
                    continue;
                if (ReadPrimitive(primitive, model, materials) is { } part)
                {
                    part.Name = mesh.Name ?? node.Name ?? $"Mesh {mesh.LogicalIndex + 1}";
                    part.Transform(world);
                    model.Parts.Add(part);
                }
            }
        }
        return model;
    }

    private static string Describe(Exception ex) =>
        ex.Message.Contains("KHR_draco_mesh_compression", StringComparison.Ordinal)
            ? "The glTF model is Draco-compressed (KHR_draco_mesh_compression), which Filee cannot decode."
            : ex.Message.Contains("meshopt_compression", StringComparison.Ordinal)
                ? "The glTF model is meshopt-compressed (EXT_meshopt_compression), which Filee cannot decode."
                : $"The file is not a valid glTF model: {ex.Message}";

    private static MeshPart? ReadPrimitive(MeshPrimitive primitive, Model3D model, Dictionary<Material, int> materials)
    {
        if (primitive.GetVertexAccessor("POSITION") is not { } positionAccessor)
            return null;
        var part = new MeshPart();
        var positions = positionAccessor.AsVector3Array();
        part.Positions.Capacity = positions.Count * 3;
        foreach (var p in positions)
        {
            part.Positions.Add(p.X);
            part.Positions.Add(p.Y);
            part.Positions.Add(p.Z);
        }

        if (primitive.GetVertexAccessor("NORMAL") is { } normalAccessor && normalAccessor.Count == positions.Count)
        {
            part.Normals = new List<float>(positions.Count * 3);
            foreach (var n in normalAccessor.AsVector3Array())
            {
                part.Normals.Add(n.X);
                part.Normals.Add(n.Y);
                part.Normals.Add(n.Z);
            }
        }

        var texCoordSet = 0;
        if (primitive.Material is { } source)
        {
            if (!materials.TryGetValue(source, out var index))
            {
                index = model.Materials.Count;
                model.Materials.Add(ReadMaterial(source, model.Materials.Count));
                materials[source] = index;
            }
            part.Material = index;
            texCoordSet = BaseColorChannel(source)?.TextureCoordinate ?? 0;
        }

        if ((primitive.GetVertexAccessor($"TEXCOORD_{texCoordSet}") ?? primitive.GetVertexAccessor("TEXCOORD_0")) is { } uvAccessor && uvAccessor.Count == positions.Count)
        {
            part.TexCoords = new List<float>(positions.Count * 2);
            foreach (var uv in uvAccessor.AsVector2Array())
            {
                part.TexCoords.Add(uv.X);
                part.TexCoords.Add(1 - uv.Y);
            }
        }

        if (primitive.GetVertexAccessor("COLOR_0") is { } colorAccessor && colorAccessor.Count == positions.Count)
        {
            part.Colors = new List<float>(positions.Count * 4);
            foreach (var c in colorAccessor.AsColorArray(1))
            {
                var srgb = MeshMath.LinearToSrgb(c);
                part.Colors.Add(srgb.X);
                part.Colors.Add(srgb.Y);
                part.Colors.Add(srgb.Z);
                part.Colors.Add(srgb.W);
            }
        }

        foreach (var (a, b, c) in primitive.GetTriangleIndices())
            part.AddTriangle(a, b, c);
        return part;
    }

    private static MaterialChannel? BaseColorChannel(Material material) =>
        material.FindChannel("BaseColor") ?? material.FindChannel("Diffuse");

    private static Material3D ReadMaterial(Material source, int index)
    {
        var result = new Material3D
        {
            Name = string.IsNullOrEmpty(source.Name) ? $"Material {index + 1}" : source.Name,
            DoubleSided = source.DoubleSided,
        };
        if (BaseColorChannel(source) is { } channel)
        {
            result.BaseColor = channel.Color;
            if (channel.Texture?.PrimaryImage?.Content is { IsEmpty: false } image)
            {
                var data = image.Content.ToArray();
                var name = channel.Texture.PrimaryImage.Name is { Length: > 0 } imageName ? Path.GetFileNameWithoutExtension(imageName) : $"texture{index + 1}";
                result.BaseColorTexture = new Texture3D(data, Texture3D.Sniff(data, image.FileExtension ?? "png"), name);
            }
        }
        return result;
    }

    /// <summary>Writes a GLB, or a self-contained .gltf when <paramref name="binary"/> is false.</summary>
    public static void Write(Model3D model, Stream output, bool binary, CancellationToken cancellationToken)
    {
        var root = ModelRoot.CreateModel();
        root.Asset.Generator = "Filee";
        var scene = root.UseScene("Scene");

        var images = new Dictionary<Texture3D, Image>();
        var materials = new Material[model.Materials.Count];
        for (var m = 0; m < model.Materials.Count; m++)
            materials[m] = WriteMaterial(root, model.Materials[m], images);

        foreach (var part in model.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = root.CreateMesh(part.Name.Length > 0 ? part.Name : null);
            var primitive = mesh.CreatePrimitive();
            var count = part.VertexCount;

            var positions = new Vector3[count];
            for (var v = 0; v < count; v++)
                positions[v] = part.Position(v) / MillimetresPerMetre;
            primitive.SetVertexAccessor("POSITION", Accessor(root, positions, DimensionType.VEC3, updateBounds: true));

            if (part.Normals is not null)
            {
                var normals = new Vector3[count];
                for (var v = 0; v < count; v++)
                {
                    var n = part.Normal(v);
                    normals[v] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                }
                primitive.SetVertexAccessor("NORMAL", Accessor(root, normals, DimensionType.VEC3));
            }
            if (part.TexCoords is not null)
            {
                var uvs = new Vector2[count];
                for (var v = 0; v < count; v++)
                {
                    var uv = part.TexCoord(v);
                    uvs[v] = new Vector2(uv.X, 1 - uv.Y);
                }
                primitive.SetVertexAccessor("TEXCOORD_0", Accessor(root, uvs, DimensionType.VEC2));
            }
            if (part.Colors is not null)
            {
                var colors = new Vector4[count];
                for (var v = 0; v < count; v++)
                    colors[v] = Vector4.Clamp(MeshMath.SrgbToLinear(part.Color(v)), Vector4.Zero, Vector4.One);
                primitive.SetVertexAccessor("COLOR_0", Accessor(root, colors, DimensionType.VEC4));
            }

            var indices = part.IndexSpan;
            var wide = count > ushort.MaxValue;
            var indexBytes = new byte[indices.Length * (wide ? 4 : 2)];
            for (var i = 0; i < indices.Length; i++)
            {
                if (wide)
                    BitConverter.TryWriteBytes(indexBytes.AsSpan(i * 4), (uint)indices[i]);
                else
                    BitConverter.TryWriteBytes(indexBytes.AsSpan(i * 2), (ushort)indices[i]);
            }
            var indexView = root.UseBufferView(indexBytes, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER);
            var indexAccessor = root.CreateAccessor();
            indexAccessor.SetIndexData(indexView, 0, indices.Length, wide ? IndexEncodingType.UNSIGNED_INT : IndexEncodingType.UNSIGNED_SHORT);
            primitive.SetIndexAccessor(indexAccessor);
            primitive.DrawPrimitiveType = PrimitiveType.TRIANGLES;
            if (part.Material >= 0 && part.Material < materials.Length)
                primitive.Material = materials[part.Material];

            scene.CreateNode(mesh.Name).Mesh = mesh;
        }

        var settings = new WriteSettings { MergeBuffers = true, Validation = ValidationMode.Strict };
        if (binary)
        {
            root.WriteGLB(output, settings);
            return;
        }

        // A .gltf in one file: the GLB's JSON with its binary chunk as a data URI.
        var glb = root.WriteGLB(settings);
        WriteEmbeddedGltf(glb, output);
    }

    private static Accessor Accessor<T>(ModelRoot root, T[] values, DimensionType dimensions, bool updateBounds = false) where T : unmanaged
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var view = root.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER);
        var accessor = root.CreateAccessor();
        accessor.SetVertexData(view, 0, values.Length, new AttributeFormat(dimensions, EncodingType.FLOAT, false));
        if (updateBounds)
            accessor.UpdateBounds();
        return accessor;
    }

    private static Material WriteMaterial(ModelRoot root, Material3D source, Dictionary<Texture3D, Image> images)
    {
        var material = root.CreateMaterial(source.Name.Length > 0 ? source.Name : null);
        material.InitializePBRMetallicRoughness();
        material.DoubleSided = source.DoubleSided;
        var color = Vector4.Clamp(source.BaseColor, Vector4.Zero, Vector4.One);
        if (color.W < 1)
            material.Alpha = AlphaMode.BLEND;
        var baseColor = material.FindChannel("BaseColor")!.Value;
        baseColor.Color = color;
        if (source.BaseColorTexture is { } texture)
        {
            if (!images.TryGetValue(texture, out var image))
            {
                image = root.UseImage(new MemoryImage(TextureForGltf(texture)));
                image.Name = texture.Name;
                images[texture] = image;
            }
            baseColor.SetTexture(0, image, null, TextureWrapMode.REPEAT, TextureWrapMode.REPEAT,
                TextureMipMapFilter.DEFAULT, TextureInterpolationFilter.DEFAULT);
        }
        // OBJ, 3MF and PLY colours describe plastic-like surfaces; glTF's default would make them fully metallic.
        var metallic = material.FindChannel("MetallicRoughness")!.Value;
        metallic.SetFactor("MetallicFactor", 0);
        metallic.SetFactor("RoughnessFactor", 0.8f);
        return material;
    }

    /// <summary>glTF viewers read PNG and JPEG: other pictures (BMP, TGA, TIFF, ...) are converted to PNG.</summary>
    private static byte[] TextureForGltf(Texture3D texture)
    {
        if (texture.Extension is "png" or "jpg")
            return texture.Data;
        try
        {
            using var image = new MagickImage(texture.Data);
            return image.ToByteArray(MagickFormat.Png);
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"The texture \"{texture.Name}.{texture.Extension}\" cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>Turns a GLB into a .gltf whose buffer is a base64 data URI (images stay in buffer views).</summary>
    private static void WriteEmbeddedGltf(ArraySegment<byte> glb, Stream output)
    {
        var data = glb.AsSpan();
        var jsonLength = BitConverter.ToInt32(data[12..]);
        var json = JsonNode.Parse(data.Slice(20, jsonLength))!.AsObject();
        var binStart = 20 + jsonLength;
        if (binStart + 8 <= data.Length && json["buffers"] is JsonArray { Count: > 0 } buffers)
        {
            var binLength = BitConverter.ToInt32(data[binStart..]);
            var bin = data.Slice(binStart + 8, binLength);
            var declared = buffers[0]!["byteLength"]?.GetValue<int>() ?? binLength;
            buffers[0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bin[..Math.Min(declared, binLength)]);
        }
        using var writer = new System.Text.Json.Utf8JsonWriter(output, new System.Text.Json.JsonWriterOptions { Indented = true });
        json.WriteTo(writer);
    }
}
