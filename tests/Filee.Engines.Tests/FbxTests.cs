// FBX (read only) into the 3D formats and pictures. The files are written here (FbxBuilder): binary 7.4 and 7.5,
// with plain and compressed arrays, and ASCII; then converted through the job queue or read directly.

using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Models3D;
using ImageMagick;
using SharpGLTF.Schema2;
using static Filee.Engines.Tests.FbxBuilder;

namespace Filee.Engines.Tests;

public class FbxTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The test cube's bounds in millimetres, Y up (see <see cref="FbxBuilder.CubeScene"/>).</summary>
    private static readonly Vector3 CubeMin = new(80, 40, -30), CubeMax = new(120, 60, 30);

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

    private string Binary(List<FbxBuilder.Node> scene, int version = 7400, bool compress = false)
    {
        var path = Path.Combine(fx.NewFolder(), "model.fbx");
        WriteBinary(path, scene, version, compress);
        return path;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static void AssertClose(Vector4 expected, Vector4 actual, float tolerance) =>
        Assert.True(Vector4.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Fbx_is_read_only_and_one_step_to_every_3d_format_and_picture()
    {
        Assert.False(FormatRegistry.Get("fbx").Writable);
        Assert.Equal(FormatCategory.Model3D, FormatRegistry.FindByExtension(".FBX")?.Category);
        var planner = fx.Catalog.CreatePlanner();
        foreach (var to in ModelConverter.Formats.Concat(["png", "jpg", "webp"]))
        {
            var route = planner.Plan("fbx", to);
            Assert.NotNull(route);
            Assert.Equal("model3d", Assert.Single(route.Steps).Converter.Id);
        }
        Assert.Null(planner.Plan("glb", "fbx"));
    }

    // ───────────────────────── Binary and ASCII ─────────────────────────

    [Theory]
    [InlineData(7400, false)]
    [InlineData(7400, true)]
    [InlineData(7500, false)]
    [InlineData(7500, true)]
    public async Task Binary_fbx_keeps_the_hierarchy_transforms_and_material(int version, bool compress)
    {
        var fbx = Binary(CubeScene(version), version, compress);

        var glb = (await RunAsync(fbx, "glb")).Outputs.Single();
        var model = ModelBuilders.Load(glb, "glb");

        Assert.Equal(12, model.TriangleCount);
        Assert.Equal(24, model.VertexCount); // a corner per face: the normals differ
        var (min, max) = model.Bounds();
        AssertClose(CubeMin, min, 1e-2f);
        AssertClose(CubeMax, max, 1e-2f);
        // 40 × 20 × 60 mm, closed and facing outwards after the turn and the scaling.
        Assert.Equal(40 * 20 * 60, ModelBuilders.SignedVolume(model), 1);
        var part = Assert.Single(model.Parts);
        Assert.NotNull(part.Normals);
        Assert.NotNull(part.TexCoords);
        var center = (min + max) / 2;
        for (var t = 0; t < part.TriangleCount; t++)
        {
            var a = part.Position(part.Indices[t * 3]);
            Assert.True(Vector3.Dot(part.Normal(part.Indices[t * 3]), a - center) > 0, $"Triangle {t} has an inward normal");
        }
        // Red from the material, opacity 0.5 from the material template.
        AssertClose(new Vector4(1, 0, 0, 0.5f), Assert.Single(model.Materials).BaseColor, 1e-4f);
        Assert.Equal("Cube", part.Name);
    }

    [Fact]
    public async Task Ascii_fbx_reads_like_binary_and_becomes_obj_with_its_material()
    {
        var folder = fx.NewFolder();
        var ascii = Path.Combine(folder, "ascii.fbx");
        WriteAscii(ascii, CubeScene());
        var model = ModelBuilders.Load(ascii, "fbx");
        var binary = ModelBuilders.Load(Binary(CubeScene()), "fbx");

        Assert.Equal(binary.TriangleCount, model.TriangleCount);
        Assert.Equal(ModelBuilders.Positions(binary), ModelBuilders.Positions(model));
        var (min, max) = model.Bounds();
        AssertClose(CubeMin, min, 1e-2f);
        AssertClose(CubeMax, max, 1e-2f);

        var job = await RunAsync(ascii, "obj");
        var obj = ModelBuilders.Load(job.Outputs.Single(o => o.EndsWith(".obj", StringComparison.Ordinal)), "obj");
        Assert.Equal(12, obj.TriangleCount);
        AssertClose(new Vector4(1, 0, 0, 0.5f), Assert.Single(obj.Materials).BaseColor, 1e-4f);
        Assert.Contains("Kd 1 0 0", File.ReadAllText(job.Outputs.Single(o => o.EndsWith(".mtl", StringComparison.Ordinal))));
    }

    // ───────────────────────── Axes and units ─────────────────────────

    [Fact]
    public async Task Z_up_fbx_in_metres_stands_up_in_gltf()
    {
        // In metres, the cube spans (8, 4, −3)–(12, 6, 3) with Z up; Y up that is (8, −3, −6)–(12, 3, −4).
        var fbx = Binary(CubeScene(upAxis: 2, unitScale: 100), compress: true);

        var glb = (await RunAsync(fbx, "glb")).Outputs.Single();
        var (min, max) = ModelRoot.Load(glb).LogicalMeshes.Single().Primitives.Single().GetVertexAccessor("POSITION").Bounds;
        AssertClose(new Vector3(8, -3, -6), new Vector3((float)min[0], (float)min[1], (float)min[2]), 1e-4f);
        AssertClose(new Vector3(12, 3, -4), new Vector3((float)max[0], (float)max[1], (float)max[2]), 1e-4f);
        // The axis change is a turn, not a mirror: still closed and outward.
        Assert.Equal(48, ModelBuilders.SignedVolume(ModelBuilders.Load(glb, "glb")) / 1e9, 1e-3); // 4 × 6 × 2 m
    }

    // ───────────────────────── Rotation order, polygons, materials ─────────────────────────

    [Fact]
    public void Rotation_order_pre_rotation_polygons_and_materials_by_polygon_are_read()
    {
        // "Turned": one triangle (0,0,0) (2,0,0) (0,1,0), rotation (90, 90, 0) in ZYX order (Y first, then X), then
        // a pre-rotation of 90° about Z: (2,0,0) → (0,0,−2) → (0,2,0) → (−2,0,0) and (0,1,0) → (0,1,0) → (0,0,1).
        // "Shapes": a triangle (material 0) and a concave pentagon (material 1, area 10 cm²).
        var turned = Model(301, "Turned", "Mesh", ("Lcl Rotation", new Vector3(90, 90, 0)), ("PreRotation", new Vector3(0, 0, 90)));
        turned.Children[1].Add(IntProperty("RotationOrder", 5));
        var objects = new FbxBuilder.Node("Objects")
        {
            Mesh(101, "Triangle", [0, 0, 0, 2, 0, 0, 0, 1, 0], [0, 1, ~2]),
            turned,
            Mesh(102, "Shapes",
                [10, 0, 0, 11, 0, 0, 10, 1, 0, /* pentagon */ 0, 0, 0, 4, 0, 0, 4, 4, 0, 2, 1, 0, 0, 4, 0],
                [0, 1, ~2, 3, 4, 5, 6, ~7],
                MaterialLayer("ByPolygon", 0, 1)),
            Model(302, "Shapes", "Mesh"),
            Material(401, "Red", new Vector3(1, 0, 0)),
            Material(402, "Blue", new Vector3(0, 0, 1)),
        };
        var connections = new FbxBuilder.Node("Connections")
        {
            Link(301, 0), Link(101, 301), Link(302, 0), Link(102, 302), Link(401, 302), Link(402, 302),
        };
        var path = Binary(Document(7400, 1, 1, objects, connections));

        var model = ModelBuilders.Load(path, "fbx");

        var triangle = model.Parts.Single(p => p.Name == "Turned");
        var bounds = new Model3D { Parts = { triangle } }.Bounds();
        AssertClose(new Vector3(-20, 0, 0), bounds.Min, 1e-3f);
        AssertClose(new Vector3(0, 0, 10), bounds.Max, 1e-3f);

        var shapes = model.Parts.Where(p => p.Name == "Shapes").ToList();
        Assert.Equal(2, shapes.Count);
        var red = shapes.Single(p => model.MaterialOf(p)?.Name == "Red");
        var blue = shapes.Single(p => model.MaterialOf(p)?.Name == "Blue");
        Assert.Equal(1, red.TriangleCount);
        Assert.Equal(3, blue.TriangleCount);
        AssertClose(new Vector4(0, 0, 1, 0.5f), model.MaterialOf(blue)!.BaseColor, 1e-4f);
        // Ear clipping keeps the notch: 4 × 4 − 6 = 10 cm² = 1000 mm².
        Assert.Equal(1000, ModelBuilders.Area(new Model3D { Parts = { blue } }), 1e-2);
    }

    // ───────────────────────── Textures and pictures ─────────────────────────

    [Fact]
    public async Task An_embedded_texture_goes_into_the_glb()
    {
        var checker = ModelBuilders.Checker();
        var fbx = Binary(CubeScene(texture: checker), compress: true);

        var glb = (await RunAsync(fbx, "glb")).Outputs.Single();
        Assert.Equal(checker, ModelBuilders.Load(glb, "glb").Materials.Single().BaseColorTexture?.Data);

        // ASCII files embed it as base64.
        var ascii = Path.Combine(fx.NewFolder(), "textured.fbx");
        WriteAscii(ascii, CubeScene(texture: checker));
        var texture = ModelBuilders.Load(ascii, "fbx").Materials.Single().BaseColorTexture;
        Assert.Equal(checker, texture?.Data);
        Assert.Equal(("png", "checker"), (texture!.Extension, texture.Name));
    }

    [Fact]
    public async Task A_texture_file_next_to_the_fbx_is_found_by_its_relative_name()
    {
        var checker = ModelBuilders.Checker();
        var fbx = Binary(CubeScene(texture: []));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(fbx)!, "checker.png"), checker);

        var glb = (await RunAsync(fbx, "glb")).Outputs.Single();
        Assert.Equal(checker, ModelBuilders.Load(glb, "glb").Materials.Single().BaseColorTexture?.Data);
    }

    [Fact]
    public async Task Fbx_renders_a_png_preview_in_the_material_colour()
    {
        var png = (await RunAsync(Binary(CubeScene()), "png")).Outputs.Single();

        using var image = new MagickImage(png);
        Assert.Equal(MagickFormat.Png, image.Format);
        var rgba = image.ToByteArray(MagickFormat.Rgba);
        var opaque = 0;
        var reddish = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128)
                continue;
            opaque++;
            if (rgba[i] > rgba[i + 1] * 2 && rgba[i] > rgba[i + 2] * 2)
                reddish++;
        }
        Assert.True(opaque > 1000, $"{opaque} opaque pixels");
        Assert.True(reddish > opaque / 2, $"{reddish} of {opaque} pixels are red");
    }

    // ───────────────────────── Bad input ─────────────────────────

    [Fact]
    public async Task Broken_fbx_files_fail_with_a_clear_message()
    {
        var folder = fx.NewFolder();

        var text = Path.Combine(folder, "text.fbx");
        File.WriteAllText(text, "hello");
        Assert.Contains("not an FBX model", await FailAsync(text, "glb"));

        var whole = File.ReadAllBytes(Binary(CubeScene(), compress: true));
        var cut = Path.Combine(folder, "cut.fbx");
        File.WriteAllBytes(cut, whole[..(whole.Length / 2)]);
        Assert.Contains("cut short or damaged", await FailAsync(cut, "glb"));

        var bad = Path.Combine(folder, "bad.fbx");
        var (positions, polygons) = Cube();
        polygons[5] = 42;
        WriteBinary(bad, Document(7400, 1, 1,
            new FbxBuilder.Node("Objects") { Mesh(100, "Cube", positions, polygons), Model(300, "Cube", "Mesh") },
            new FbxBuilder.Node("Connections") { Link(300, 0), Link(100, 300) }));
        Assert.Contains("refers to vertex 43", await FailAsync(bad, "stl"));

        var empty = Path.Combine(folder, "empty.fbx");
        WriteBinary(empty, Document(7400, 1, 1, new FbxBuilder.Node("Objects"), new FbxBuilder.Node("Connections")));
        Assert.Contains("contains no triangles", await FailAsync(empty, "stl"));
    }

    [Theory]
    [InlineData(7400, false)]
    [InlineData(7500, true)]
    public void Every_cut_or_flipped_byte_reads_or_fails_cleanly(int version, bool compress)
    {
        // A damaged file must end in a model or an InvalidDataException: no other exception, no hang.
        var whole = File.ReadAllBytes(Binary(CubeScene(version, texture: ModelBuilders.Checker()), version, compress));
        var folder = fx.NewFolder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var length = 0; length < whole.Length; length++)
            ReadsOrFailsCleanly(whole[..length], folder);
        for (var i = 23; i < whole.Length; i++)
        {
            var copy = (byte[])whole.Clone();
            copy[i] ^= (byte)(i % 3 == 0 ? 0x80 : 0xFF);
            ReadsOrFailsCleanly(copy, folder);
        }

        var ascii = Path.Combine(fx.NewFolder(), "ascii.fbx");
        WriteAscii(ascii, CubeScene());
        var text = File.ReadAllBytes(ascii);
        for (var length = 0; length < text.Length; length++)
            ReadsOrFailsCleanly(text[..length], folder);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    private static void ReadsOrFailsCleanly(byte[] data, string folder)
    {
        try
        {
            FbxFormat.Read(data, folder, CancellationToken.None).Validate();
        }
        catch (InvalidDataException) { }
    }
}
