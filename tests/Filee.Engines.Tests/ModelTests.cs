// 3D models: GLB, glTF, STL, OBJ, 3MF and PLY into each other and to pictures. Meshes are generated (ModelBuilders)
// or written as text / bytes here; every format pair is converted through the job queue and read back.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Models3D;
using ImageMagick;
using SharpGLTF.Schema2;

namespace Filee.Engines.Tests;

public class ModelTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] Formats = ModelConverter.Formats;
    private static readonly string[] WithNormals = ["obj", "ply", "glb", "gltf"];
    private static readonly string[] WithUvs = ["obj", "ply", "glb", "gltf"];
    private static readonly string[] WithVertexColors = ["obj", "ply", "3mf", "glb", "gltf"];
    private static readonly string[] WithMaterials = ["obj", "3mf", "glb", "gltf"];
    private static readonly string[] WithTextures = ["obj", "glb", "gltf"];

    private async Task<ConversionJob> RunAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job;
    }

    private async Task<string> FailAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.Equal(JobState.Failed, job.State);
        return job.Files.Single().ErrorDetail ?? "";
    }

    public static TheoryData<string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var kind in new[] { "cube", "sphere", "fan" })
            foreach (var from in Formats)
                foreach (var to in Formats.Where(t => t != from))
                    data.Add(kind, from, to);
        return data;
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Every_pair_and_every_picture_is_one_step_of_the_3d_engine()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var from in Formats)
        {
            foreach (var to in Formats.Where(t => t != from).Concat(["png", "jpg", "webp"]))
            {
                var route = planner.Plan(from, to);
                Assert.NotNull(route);
                Assert.Equal("model3d", Assert.Single(route.Steps).Converter.Id);
            }
        }
    }

    // ───────────────────────── Every format pair ─────────────────────────

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Converting_keeps_geometry_and_what_both_formats_store(string kind, string from, string to)
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Make(kind), folder, kind, from);
        var expected = ModelBuilders.Load(source, from);

        var job = await RunAsync(source, to);
        var output = job.Outputs.First(o => o.EndsWith("." + to, StringComparison.OrdinalIgnoreCase));
        var actual = ModelBuilders.Load(output, to);

        Assert.Equal(expected.TriangleCount, actual.TriangleCount);
        var (expectedMin, expectedMax) = expected.Bounds();
        var (actualMin, actualMax) = actual.Bounds();
        AssertClose(expectedMin, actualMin, 1e-3f);
        AssertClose(expectedMax, actualMax, 1e-3f);
        Assert.Equal(ModelBuilders.SignedVolume(expected), ModelBuilders.SignedVolume(actual), 0.05);
        Assert.Equal(ModelBuilders.Area(expected), ModelBuilders.Area(actual), 0.05);
        Assert.Equal(ModelBuilders.Positions(expected), ModelBuilders.Positions(actual));

        if (WithNormals.Contains(to) && expected.Parts.Any(p => p.Normals is not null))
            AssertAttributesMatch(expected, actual, p => p.Normals is not null, (p, v) => new Vector4(p.Normal(v), 0), 1e-3f);
        if (WithUvs.Contains(to) && expected.Parts.Any(p => p.TexCoords is not null))
            AssertAttributesMatch(expected, actual, p => p.TexCoords is not null, (p, v) => new Vector4(p.TexCoord(v), 0, 0), 1e-4f);
        if (WithVertexColors.Contains(to) && expected.Parts.Any(p => p.Colors is not null))
            AssertAttributesMatch(expected, actual, p => p.Colors is not null, (p, v) => p.Color(v), 2.5f / 255);

        if (WithMaterials.Contains(to) && expected.Materials.Count > 0)
        {
            var color = Assert.Single(actual.Materials).BaseColor;
            AssertClose(expected.Materials[0].BaseColor, color, to == "3mf" ? 0.01f : 1e-4f);
            if (WithTextures.Contains(to) && expected.Materials[0].BaseColorTexture is { } texture)
                Assert.Equal(texture.Data, actual.Materials[0].BaseColorTexture?.Data);
        }
    }

    /// <summary>Every vertex of <paramref name="actual"/> has a value that some vertex at the same place had before.</summary>
    private static void AssertAttributesMatch(Model3D expected, Model3D actual, Func<MeshPart, bool> has, Func<MeshPart, int, Vector4> value, float tolerance)
    {
        var known = new Dictionary<(int, int, int), List<Vector4>>();
        foreach (var part in expected.Parts.Where(has))
        {
            for (var v = 0; v < part.VertexCount; v++)
            {
                var key = ModelBuilders.Key(part.Position(v));
                if (!known.TryGetValue(key, out var list))
                    known[key] = list = [];
                list.Add(value(part, v));
            }
        }
        Assert.Contains(actual.Parts, p => has(p));
        foreach (var part in actual.Parts.Where(has))
        {
            for (var v = 0; v < part.VertexCount; v++)
            {
                var found = value(part, v);
                var candidates = known[ModelBuilders.Key(part.Position(v))];
                Assert.True(candidates.Any(c => Vector4.Distance(c, found) <= tolerance * 2),
                    $"Vertex {v} at {part.Position(v)} has {found}; expected one of {string.Join(", ", candidates)}");
            }
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static void AssertClose(Vector4 expected, Vector4 actual, float tolerance) =>
        Assert.True(Vector4.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");

    // ───────────────────────── Units and axes ─────────────────────────

    [Fact]
    public async Task Stl_millimetres_and_z_up_become_gltf_metres_and_y_up()
    {
        // A box 10 × 20 mm wide and 50 mm tall along Z, as a 3D printer sees it.
        var folder = fx.NewFolder();
        var stl = Path.Combine(folder, "tower.stl");
        WriteBinaryStl(stl, Box(new Vector3(0, 0, 0), new Vector3(10, 20, 50)));

        var glb = (await RunAsync(stl, "glb")).Outputs.Single();
        var root = ModelRoot.Load(glb);
        var position = root.LogicalMeshes.Single().Primitives.Single().GetVertexAccessor("POSITION");
        var (min, max) = position.Bounds;
        Assert.Equal(0.010, max[0] - min[0], 1e-6);
        Assert.Equal(0.050, max[1] - min[1], 1e-6); // up
        Assert.Equal(0.020, max[2] - min[2], 1e-6);

        // And back: the same box, standing on Z again.
        var raw = ReadBinaryStl((await RunAsync(glb, "stl")).Outputs.Single());
        Assert.Equal(new Vector3(10, 20, 50), raw.Max - raw.Min);
        Assert.Equal(12, raw.Facets.Count);
    }

    [Fact]
    public async Task Gltf_from_3mf_in_inches_is_scaled_to_metres()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "inch.3mf");
        Write3mf(path, """
            <model unit="inch" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
             <resources>
              <object id="1" type="model"><mesh>
               <vertices><vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="0" y="1" z="0"/><vertex x="0" y="0" z="1"/></vertices>
               <triangles><triangle v1="0" v2="2" v3="1"/><triangle v1="0" v2="1" v3="3"/><triangle v1="0" v2="3" v3="2"/><triangle v1="1" v2="2" v3="3"/></triangles>
              </mesh></object>
             </resources>
             <build><item objectid="1"/></build>
            </model>
            """);
        var glb = (await RunAsync(path, "glb")).Outputs.Single();
        var (min, max) = ModelRoot.Load(glb).LogicalMeshes.Single().Primitives.Single().GetVertexAccessor("POSITION").Bounds;
        Assert.Equal(0.0254, max[0] - min[0], 1e-6);
        Assert.Equal(0.0254, max[1] - min[1], 1e-6);
    }

    [Fact]
    public async Task Stl_facet_normals_point_outward()
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Cube(), folder, "cube", "glb");
        var stl = ReadBinaryStl((await RunAsync(source, "stl")).Outputs.Single());
        var center = (stl.Min + stl.Max) / 2;
        Assert.Equal(12, stl.Facets.Count);
        foreach (var (normal, a, b, c) in stl.Facets)
        {
            Assert.Equal(1, normal.Length(), 1e-4);
            Assert.True(Vector3.Dot(normal, (a + b + c) / 3 - center) > 0);
        }
    }

    // ───────────────────────── Outputs with several files ─────────────────────────

    [Fact]
    public async Task Obj_output_writes_the_material_library_and_texture_next_to_it()
    {
        var folder = fx.NewFolder();
        var glb = ModelBuilders.Save(ModelBuilders.Cube(), folder, "kubus", "glb");
        var job = await RunAsync(glb, "obj");

        var obj = job.Outputs.Single(o => o.EndsWith(".obj", StringComparison.Ordinal));
        var mtl = job.Outputs.Single(o => o.EndsWith(".mtl", StringComparison.Ordinal));
        var png = job.Outputs.Single(o => o.EndsWith(".png", StringComparison.Ordinal));
        Assert.All(job.Outputs, o => Assert.Equal(Path.GetDirectoryName(obj), Path.GetDirectoryName(o)));
        Assert.Contains($"mtllib {Path.GetFileName(mtl)}", File.ReadAllText(obj));
        Assert.Contains($"map_Kd {Path.GetFileName(png)}", File.ReadAllText(mtl));
        Assert.Equal(ModelBuilders.Checker(), File.ReadAllBytes(png));
    }

    [Fact]
    public async Task Gltf_output_is_one_file_with_the_buffer_inside()
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Cube(), folder, "cube", "obj");
        var gltf = (await RunAsync(source, "gltf")).Outputs.Single();
        var json = JsonNode.Parse(File.ReadAllText(gltf))!;
        Assert.StartsWith("data:application/octet-stream;base64,", (string)json["buffers"]![0]!["uri"]!);
        Assert.Empty(Directory.GetFiles(folder, "*.bin"));
        var root = ModelRoot.Load(gltf);
        Assert.Equal(12, root.LogicalMeshes.Single().Primitives.Single().GetTriangleIndices().Count());
        Assert.NotNull(root.LogicalMaterials.Single().FindChannel("BaseColor")!.Value.Texture);
    }

    [Fact]
    public async Task Gltf_with_a_separate_buffer_and_texture_is_read()
    {
        var folder = fx.NewFolder();
        var root = ModelRoot.CreateModel();
        var material = root.CreateMaterial("Painted");
        material.InitializePBRMetallicRoughness();
        material.FindChannel("BaseColor")!.Value.SetTexture(0, root.UseImage(new SharpGLTF.Memory.MemoryImage(ModelBuilders.Checker())), null,
            TextureWrapMode.REPEAT, TextureWrapMode.REPEAT, TextureMipMapFilter.DEFAULT, TextureInterpolationFilter.DEFAULT);
        var mesh = root.CreateMesh("Quad");
        var primitive = mesh.CreatePrimitive();
        primitive.SetVertexAccessor("POSITION", Accessor(root, [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0)]));
        primitive.SetVertexAccessor("TEXCOORD_0", Accessor(root, [new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0)]));
        var indices = root.CreateAccessor();
        indices.SetIndexData(root.UseBufferView(new byte[] { 0, 0, 1, 0, 2, 0, 0, 0, 2, 0, 3, 0 }, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER), 0, 6, IndexEncodingType.UNSIGNED_SHORT);
        primitive.SetIndexAccessor(indices);
        primitive.Material = material;
        // The node scales by 2 and moves up by 1 m: both end up in the vertices.
        var node = root.UseScene("Scene").CreateNode("Quad");
        node.Mesh = mesh;
        node.LocalMatrix = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, 1, 0);
        var gltf = Path.Combine(folder, "quad.gltf");
        root.SaveGLTF(gltf, new WriteSettings { ImageWriting = ResourceWriteMode.SatelliteFile });
        Assert.NotEmpty(Directory.GetFiles(folder, "*.bin"));

        var model = ModelBuilders.Load((await RunAsync(gltf, "ply")).Outputs.Single(), "ply");
        var (min, max) = model.Bounds();
        AssertClose(new Vector3(0, 1000, 0), min, 1e-2f);
        AssertClose(new Vector3(2000, 3000, 0), max, 1e-2f);
        Assert.Equal(2, model.TriangleCount);
        // glTF's UV origin is the top left, PLY's the bottom left.
        Assert.Equal(new Vector2(0, 0), model.Parts[0].TexCoord(Enumerable.Range(0, 4).Single(v => model.Parts[0].Position(v) is { X: < 1, Y: < 1001 })));

        var objJob = await RunAsync(gltf, "obj");
        Assert.Equal(3, objJob.Outputs.Count()); // .obj, .mtl and the texture
    }

    private static Accessor Accessor<T>(ModelRoot root, T[] values) where T : unmanaged
    {
        var view = root.UseBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), 0, null, 0, BufferMode.ARRAY_BUFFER);
        var accessor = root.CreateAccessor();
        accessor.SetVertexData(view, 0, values.Length, new SharpGLTF.Memory.AttributeFormat(typeof(T) == typeof(Vector3) ? DimensionType.VEC3 : DimensionType.VEC2));
        accessor.UpdateBounds();
        return accessor;
    }

    // ───────────────────────── Real-world files ─────────────────────────

    [Fact]
    public async Task Ascii_stl_with_two_solids_becomes_two_meshes()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "two.stl");
        File.WriteAllText(path, """
            solid first
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 1 0
                endloop
              endfacet
            endsolid first
            solid second
              facet normal 0 0 1
                outer loop
                  vertex 5 0 0
                  vertex 6.5e0 0 0
                  vertex 5 1 0
                endloop
              endfacet
            endsolid second
            """);
        var model = ModelBuilders.Load(path, "stl");
        Assert.Equal(["first", "second"], model.Parts.Select(p => p.Name));
        var glb = ModelRoot.Load((await RunAsync(path, "glb")).Outputs.Single());
        Assert.Equal(2, glb.LogicalMeshes.Count);
    }

    [Fact]
    public void Binary_stl_with_a_solid_header_and_extra_bytes_is_read_as_binary()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "solid.stl");
        WriteBinaryStl(path, Box(Vector3.Zero, new Vector3(1, 2, 3)));
        var data = File.ReadAllBytes(path);
        "solid exported by a CAD program"u8.CopyTo(data);
        File.WriteAllBytes(path, [.. data, .. "trailing"u8]);
        var model = ModelBuilders.Load(path, "stl");
        Assert.Equal(12, model.TriangleCount);
        Assert.Equal(8, model.VertexCount);
    }

    [Fact]
    public async Task Obj_with_polygons_negative_indices_colours_and_a_material_library()
    {
        var folder = fx.NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "wood grain.png"), ModelBuilders.Checker());
        File.WriteAllText(Path.Combine(folder, "parts.mtl"), """
            newmtl wood
            Kd 0.5 0.25 0.125
            map_Kd -s 1 1 1 -bm 0.5 wood grain.png
            """);
        // A U-shaped octagon (concave: a fan from its first corner would cover the notch) and a quad with
        // negative indices and vertex colours.
        File.WriteAllText(Path.Combine(folder, "parts.obj"), """
            mtllib parts.mtl
            o U
            v 2 2 0 1 0 0
            v 2 1 0 1 0 0
            v 1 1 0 1 0 0
            v 1 2 0 1 0 0
            v 0 2 0 1 0 0
            v 0 0 0 1 0 0
            v 3 0 0 1 0 0
            v 3 2 0 1 0 0
            usemtl wood
            f 1 2 3 4 5 6 7 8
            o Quad
            v 10 0 0 0 0 255
            v 11 0 0 0 0 255
            v 11 1 0 0 0 255
            v 10 1 0 0 0 255
            vt 0 0
            vt 1 1
            f -4/-2 -3/-2 -2/-1 -1/-1
            """);
        var model = ModelBuilders.Load(Path.Combine(folder, "parts.obj"), "obj");
        var u = model.Parts.Single(p => p.Name == "U");
        Assert.Equal(6, u.TriangleCount);
        Assert.Equal(5, ModelBuilders.Area(new Model3D { Parts = { u } }), 1e-4);
        Assert.Equal("wood", model.MaterialOf(u)!.Name);
        Assert.Equal(new Vector4(0.5f, 0.25f, 0.125f, 1), model.MaterialOf(u)!.BaseColor);
        Assert.Equal(ModelBuilders.Checker(), model.MaterialOf(u)!.BaseColorTexture!.Data);
        var quad = model.Parts.Single(p => p.Name == "Quad");
        Assert.Equal(2, quad.TriangleCount);
        Assert.Equal(new Vector4(0, 0, 1, 1), quad.Color(0)); // 0–255 values are scaled

        var glb = ModelRoot.Load((await RunAsync(Path.Combine(folder, "parts.obj"), "glb")).Outputs.Single());
        Assert.Equal(8, glb.LogicalMeshes.Sum(m => m.Primitives.Sum(p => p.GetTriangleIndices().Count())));
        Assert.NotNull(glb.LogicalMaterials.Single(m => m.Name == "wood").FindChannel("BaseColor")!.Value.Texture);
    }

    [Fact]
    public void Ply_ascii_with_quads_and_colours_and_big_endian_binary_are_read()
    {
        var folder = fx.NewFolder();
        var ascii = Path.Combine(folder, "ascii.ply");
        File.WriteAllText(ascii, """
            ply
            format ascii 1.0
            comment a quad and a triangle
            element vertex 5
            property float x
            property float y
            property float z
            property uchar red
            property uchar green
            property uchar blue
            element face 2
            property list uchar int vertex_indices
            end_header
            0 0 0 255 0 0
            1 0 0 0 255 0
            1 1 0 0 0 255
            0 1 0 255 255 255
            2 0 0 0 0 0
            4 0 1 2 3
            3 1 4 2

            """);
        var model = ModelBuilders.Load(ascii, "ply");
        Assert.Equal(3, model.TriangleCount);
        Assert.Equal(new Vector4(0, 1, 0, 1), model.Parts[0].Color(1));
        // Z up → Y up: the quad lies in the XZ plane.
        Assert.Equal(new Vector3(1, 0, -1), model.Parts[0].Position(2));

        var binary = Path.Combine(folder, "big.ply");
        using (var stream = File.Create(binary))
        {
            stream.Write(Encoding.ASCII.GetBytes("ply\nformat binary_big_endian 1.0\nelement vertex 3\nproperty double x\nproperty double y\nproperty double z\nproperty ushort red\nproperty ushort green\nproperty ushort blue\nelement edge 1\nproperty int vertex1\nproperty int vertex2\nelement face 1\nproperty list uchar uint vertex_indices\nend_header\n"));
            Span<byte> buffer = stackalloc byte[8];
            foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (4.0, 0.0, 0.0), (0.0, 3.0, 0.0) })
            {
                foreach (var value in new[] { x, y, z })
                {
                    BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
                    stream.Write(buffer);
                }
                foreach (var channel in new ushort[] { 65535, 0, 32768 })
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer, channel);
                    stream.Write(buffer[..2]);
                }
            }
            BinaryPrimitives.WriteInt32BigEndian(buffer, 0);
            stream.Write(buffer[..4]);
            BinaryPrimitives.WriteInt32BigEndian(buffer, 1);
            stream.Write(buffer[..4]);
            stream.WriteByte(3);
            foreach (var index in new uint[] { 0, 1, 2 })
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, index);
                stream.Write(buffer[..4]);
            }
        }
        var big = ModelBuilders.Load(binary, "ply");
        Assert.Equal(1, big.TriangleCount);
        Assert.Equal(6, ModelBuilders.Area(big), 1e-5);
        Assert.Equal(1, big.Parts[0].Color(0).X, 1e-4);
        Assert.Equal(0.5, big.Parts[0].Color(0).Z, 1e-3);
    }

    [Fact]
    public void Three_mf_components_transforms_materials_and_colour_groups_are_read()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "assembly.3mf");
        Write3mf(path, """
            <model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02" xmlns:m="http://schemas.microsoft.com/3dmanufacturing/material/2015/02">
             <resources>
              <basematerials id="1"><base name="Orange" displaycolor="#FF8000"/><base name="Blue" displaycolor="#0000FFFF"/></basematerials>
              <m:colorgroup id="2"><m:color color="#FF0000"/><m:color color="#00FF00"/></m:colorgroup>
              <object id="3" type="model" pid="1" pindex="0"><mesh>
               <vertices><vertex x="0" y="0" z="0"/><vertex x="10" y="0" z="0"/><vertex x="0" y="10" z="0"/><vertex x="10" y="10" z="0"/></vertices>
               <triangles><triangle v1="0" v2="1" v3="2"/><triangle v1="1" v2="3" v3="2" pid="1" p1="1"/></triangles>
              </mesh></object>
              <object id="4" type="model"><mesh>
               <vertices><vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="0" y="1" z="0"/></vertices>
               <triangles><triangle v1="0" v2="1" v3="2" pid="2" p1="0" p2="1"/></triangles>
              </mesh></object>
              <object id="5" type="support"><mesh>
               <vertices><vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="0" y="1" z="0"/></vertices>
               <triangles><triangle v1="0" v2="1" v3="2"/></triangles>
              </mesh></object>
              <object id="6" type="model"><components>
               <component objectid="3"/>
               <component objectid="4" transform="1 0 0 0 1 0 0 0 1 100 0 0"/>
               <component objectid="5"/>
              </components></object>
             </resources>
             <build><item objectid="6" transform="1 0 0 0 1 0 0 0 1 0 0 5"/></build>
            </model>
            """);
        var model = ModelBuilders.Load(path, "3mf");
        Assert.Equal(3, model.TriangleCount); // the support is left out
        Assert.Equal(["Orange", "Blue"], model.Materials.Select(m => m.Name));
        AssertClose(new Vector4(1, MeshMath.SrgbToLinear(128 / 255f), 0, 1), model.Materials[0].BaseColor, 1e-4f);
        var colored = model.Parts.Single(p => p.Colors is not null);
        var corner = Enumerable.Range(0, colored.VertexCount).Single(v => colored.Position(v).X > 100.5f);
        Assert.Equal(new Vector4(0, 1, 0, 1), colored.Color(corner)); // p2 → second colour
        // Z up → Y up, and the item's 5 mm lift becomes Y.
        Assert.All(model.Parts, p => Assert.Equal(5, p.Position(0).Y, 1e-4));
    }

    [Fact]
    public async Task Ply_output_turns_material_colours_into_vertex_colours()
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Cube(), folder, "cube", "3mf");
        var model = ModelBuilders.Load((await RunAsync(source, "ply")).Outputs.Single(), "ply");
        var expected = MeshMath.LinearToSrgb(ModelBuilders.CubeColor);
        Assert.All(Enumerable.Range(0, model.VertexCount), v => AssertClose(expected, model.Parts[0].Color(v), 1.5f / 255));
    }

    [Fact]
    public async Task Missing_normals_are_computed_where_the_output_has_them()
    {
        // A sphere with normals and a fan without, in one OBJ (the fan's faces have no "vn"): every vertex of the
        // PLY gets a unit normal, the fan's pointing up.
        var model = ModelBuilders.Sphere();
        var fan = ModelBuilders.Fan().Parts[0];
        fan.Colors = null;
        model.Parts.Add(fan);
        var folder = fx.NewFolder();
        var obj = ModelBuilders.Save(model, folder, "mixed", "obj");
        var ply = ModelBuilders.Load((await RunAsync(obj, "ply")).Outputs.Single(), "ply");
        var part = ply.Parts.Single();
        Assert.NotNull(part.Normals);
        for (var v = 0; v < part.VertexCount; v++)
            Assert.Equal(1, part.Normal(v).Length(), 1e-3);
        var fanVertices = Enumerable.Range(0, part.VertexCount).Where(v => part.Position(v).Y == 0 && part.Position(v).Length() > 9.9f && part.Normal(v).Y > 0.99f);
        Assert.Equal(6, fanVertices.Count());

        // A face without normals among faces with them: the part's normals are computed from the faces.
        var path = Path.Combine(folder, "partial.obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 0 -1\nv 1 0 -1\nvn 0 1 0\nf 1//1 2//1 3//1\nf 2 4 3\n");
        var partial = ModelBuilders.Load(path, "obj").Parts.Single();
        Assert.All(Enumerable.Range(0, partial.VertexCount), v => Assert.Equal(1, partial.Normal(v).Y, 1e-4));
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Fact]
    public async Task A_rotate_preset_turns_the_rendered_picture_once()
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Fan(), folder, "fan", "stl");
        var plain = (await RunAsync(source, "png")).Outputs.Single();
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png", Image = { Rotation = ImageRotation.Left90 } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

        using var expected = new MagickImage(plain);
        expected.Rotate(-90);
        using var turned = new MagickImage(job.Outputs.Single());
        using var unturned = new MagickImage(plain);
        Assert.True(unturned.Compare(expected, ErrorMetric.RootMeanSquared) > 0.01); // the fan looks different turned
        Assert.True(turned.Compare(expected, ErrorMetric.RootMeanSquared) < 0.01);
    }

    [Fact]
    public async Task A_model_renders_to_a_shaded_picture_with_a_transparent_background()
    {
        var folder = fx.NewFolder();
        var source = ModelBuilders.Save(ModelBuilders.Sphere(), folder, "ball", "ply");
        var png = (await RunAsync(source, "png")).Outputs.Single();
        using var image = new MagickImage(png);
        Assert.Equal(1024u, image.Width);
        Assert.Equal(1024u, image.Height);
        using var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(2, 2)!.GetChannel(3));
        var center = pixels.GetPixel(512, 512)!;
        Assert.Equal(255, center.GetChannel(3));
        // The sphere's colours come through (not a grey default).
        var colors = Enumerable.Range(0, 16).Select(i => pixels.GetPixel(300 + i * 25, 512)!).Where(p => p.GetChannel(3) == 255)
            .Select(p => (p.GetChannel(0), p.GetChannel(1), p.GetChannel(2))).Distinct().Count();
        Assert.True(colors > 4);

        var jpg = (await RunAsync(source, "jpg")).Outputs.Single();
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(jpg).Format);
    }

    // ───────────────────────── Large files ─────────────────────────

    [Fact]
    public async Task A_million_triangle_stl_converts_in_seconds()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "terrain.stl");
        const int grid = 708; // 708 × 708 × 2 = 1,002,528 triangles
        using (var stream = new BufferedStream(File.Create(path), 1 << 20))
        {
            var header = new byte[84];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), grid * grid * 2);
            stream.Write(header);
            var record = new byte[50];
            Vector3 P(int x, int y) => new(x, y, MathF.Sin(x * 0.05f) * MathF.Cos(y * 0.05f) * 5);
            for (var y = 0; y < grid; y++)
            {
                for (var x = 0; x < grid; x++)
                {
                    WriteFacet(stream, record, P(x, y), P(x + 1, y), P(x + 1, y + 1));
                    WriteFacet(stream, record, P(x, y), P(x + 1, y + 1), P(x, y + 1));
                }
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var glb = (await RunAsync(path, "glb")).Outputs.Single();
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        var primitive = ModelRoot.Load(glb).LogicalMeshes.Single().Primitives.Single();
        Assert.Equal(grid * grid * 2, primitive.GetIndexAccessor().Count / 3);
        Assert.Equal((grid + 1) * (grid + 1), primitive.GetVertexAccessor("POSITION").Count); // merged by position
    }

    private static void WriteFacet(Stream stream, byte[] record, Vector3 a, Vector3 b, Vector3 c)
    {
        var i = 12;
        foreach (var v in new[] { a, b, c })
        {
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(i), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(i + 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(i + 8), v.Z);
            i += 12;
        }
        stream.Write(record);
    }

    // ───────────────────────── Bad input ─────────────────────────

    [Fact]
    public async Task Broken_files_fail_with_a_clear_message()
    {
        var folder = fx.NewFolder();

        var truncated = Path.Combine(folder, "cut.stl");
        var header = new byte[84 + 50];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 10);
        File.WriteAllBytes(truncated, header);
        Assert.Contains("cut short", await FailAsync(truncated, "glb"));

        var badIndex = Path.Combine(folder, "bad.obj");
        File.WriteAllText(badIndex, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 7\n");
        Assert.Contains("vertex 7, which does not exist", await FailAsync(badIndex, "stl"));

        var noFaces = Path.Combine(folder, "points.obj");
        File.WriteAllText(noFaces, "v 0 0 0\nv 1 0 0\n");
        Assert.Contains("contains no triangles", await FailAsync(noFaces, "stl"));

        var notPly = Path.Combine(folder, "text.ply");
        File.WriteAllText(notPly, "hello");
        Assert.Contains("not a PLY file", await FailAsync(notPly, "stl"));

        var not3mf = Path.Combine(folder, "text.3mf");
        File.WriteAllText(not3mf, "hello");
        Assert.Contains("not a 3MF package", await FailAsync(not3mf, "stl"));

        var notGltf = Path.Combine(folder, "text.gltf");
        File.WriteAllText(notGltf, "{ \"asset\": ");
        Assert.Contains("glTF", await FailAsync(notGltf, "stl"));

        var draco = Path.Combine(folder, "draco.gltf");
        File.WriteAllText(draco, """
            {"asset":{"version":"2.0"},"extensionsUsed":["KHR_draco_mesh_compression"],"extensionsRequired":["KHR_draco_mesh_compression"],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"extensions":{"KHR_draco_mesh_compression":{"bufferView":0,"attributes":{"POSITION":0}}}}]}],
             "accessors":[{"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
             "bufferViews":[{"buffer":0,"byteLength":4}],"buffers":[{"byteLength":4,"uri":"data:application/octet-stream;base64,AAAAAA=="}],
             "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
            """);
        Assert.Contains("Draco-compressed", await FailAsync(draco, "stl"));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void Write3mf(string path, string model)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("_rels/.rels").Open()))
            writer.Write("""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>""");
        using (var writer = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open()))
            writer.Write(model);
    }

    /// <summary>The 12 triangles of an axis-aligned box, outward facing.</summary>
    private static List<(Vector3, Vector3, Vector3)> Box(Vector3 min, Vector3 max)
    {
        Vector3 C(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[][] quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        return [.. quads.SelectMany(q => new[] { (C(q[0]), C(q[1]), C(q[2])), (C(q[0]), C(q[2]), C(q[3])) })];
    }

    private static void WriteBinaryStl(string path, List<(Vector3, Vector3, Vector3)> triangles)
    {
        using var stream = File.Create(path);
        var header = new byte[84];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), (uint)triangles.Count);
        stream.Write(header);
        var record = new byte[50];
        foreach (var (a, b, c) in triangles)
            WriteFacet(stream, record, a, b, c);
    }

    private static (List<(Vector3 Normal, Vector3 A, Vector3 B, Vector3 C)> Facets, Vector3 Min, Vector3 Max) ReadBinaryStl(string path)
    {
        var data = File.ReadAllBytes(path);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(80));
        var facets = new List<(Vector3, Vector3, Vector3, Vector3)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < count; i++)
        {
            var v = new Vector3[4];
            for (var k = 0; k < 4; k++)
            {
                var o = 84 + i * 50 + k * 12;
                v[k] = new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                if (k > 0)
                {
                    min = Vector3.Min(min, v[k]);
                    max = Vector3.Max(max, v[k]);
                }
            }
            facets.Add((v[0], v[1], v[2], v[3]));
        }
        return (facets, min, max);
    }
}
