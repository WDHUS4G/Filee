// Autodesk FBX, read only: binary (6.x and 7.x) and ASCII files through Filee's own parser (FbxParser), no FBX SDK.
// Read: mesh geometry with polygons triangulated, the first normal, UV, colour and material layer (by polygon
// vertex, by control point, by polygon or all the same; direct or indexed); the model hierarchy from the
// connections, each model's translation, rotation (any Euler order, pre- and post-rotation, pivots and offsets) and
// scaling, and the geometric transform of its mesh; material diffuse colour and opacity; the diffuse texture,
// embedded (Video Content) or a file next to the model. GlobalSettings' axes and UnitScaleFactor (centimetres per
// unit) turn the scene Y up and into millimetres. Animation, skins, blend shapes, NURBS, cameras and lights are
// skipped: the model is read in its rest pose.

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Filee.Engines.Models3D;

internal static class FbxFormat
{
    private const float MillimetresPerCentimetre = 10;

    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = Read(File.ReadAllBytes(path), Path.GetDirectoryName(Path.GetFullPath(path))!, cancellationToken);
        progress?.Report(1);
        return model;
    }

    /// <summary>Reads the FBX file <paramref name="data"/>; texture files are looked for in <paramref name="directory"/>.</summary>
    internal static Model3D Read(byte[] data, string directory, CancellationToken cancellationToken)
    {
        FbxNode document;
        try
        {
            document = FbxParser.Parse(data, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            throw new InvalidDataException(FbxParser.Damaged, ex);
        }
        try
        {
            return new Scene(document, directory).Build(cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or InvalidDataException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException($"The FBX model could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>The objects of the file, their connections and the global settings.</summary>
    private sealed class Scene
    {
        private readonly string _directory;
        private readonly FbxNode? _settings;
        private readonly List<(string Key, FbxNode Node)> _ordered = [];
        private readonly Dictionary<string, FbxNode> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FbxNode> _templates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FbxNode>> _geometries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _materials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Texture, bool Exact)> _textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _videos = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Matrix4x4> _world = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materialIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture3D?> _loaded = new(StringComparer.Ordinal);

        public Scene(FbxNode document, string directory)
        {
            _directory = directory;
            _settings = document.Child("GlobalSettings");
            foreach (var node in document.Child("Objects")?.Children ?? [])
            {
                if (node.Properties.Count > 0 && KeyOf(node.Properties[0]) is { } key && _objects.TryAdd(key, node))
                    _ordered.Add((key, node));
            }
            // Property templates: what an object of the type has where it does not say (the FBX SDK writes only
            // what differs from them).
            foreach (var type in document.Child("Definitions")?.ChildrenNamed("ObjectType") ?? [])
            {
                if (type.Text(0) is { } name && type.Child("PropertyTemplate") is { } template)
                    _templates.TryAdd(name, template);
            }
            foreach (var link in document.Child("Connections")?.Children ?? [])
            {
                if (link.Name is not ("C" or "Connect") || link.Properties.Count < 3
                    || KeyOf(link.Properties[1]) is not { } childKey || KeyOf(link.Properties[2]) is not { } parentKey
                    || !_objects.TryGetValue(childKey, out var child) || !_objects.TryGetValue(parentKey, out var parent))
                    continue;
                var property = link.Text(3);
                switch (child.Name, parent.Name)
                {
                    case ("Geometry", "Model"):
                        Add(_geometries, parentKey, child);
                        break;
                    case ("Model", "Model"):
                        _parents.TryAdd(childKey, parentKey);
                        break;
                    case ("Material", "Model"):
                        Add(_materials, parentKey, childKey);
                        break;
                    case ("Texture", "Material") when IsDiffuse(property):
                        // "DiffuseColor" wins over the other colour channels a material may have a texture on.
                        var exact = property is null or "DiffuseColor";
                        if (!_textures.TryGetValue(parentKey, out var existing) || (exact && !existing.Exact))
                            _textures[parentKey] = (childKey, exact);
                        break;
                    case ("Video", "Texture"):
                        _videos.TryAdd(parentKey, childKey);
                        break;
                }
            }
        }

        private static void Add<T>(Dictionary<string, List<T>> lists, string key, T value)
        {
            if (!lists.TryGetValue(key, out var list))
                lists[key] = list = [];
            list.Add(value);
        }

        private static bool IsDiffuse(string? property) =>
            property is null
            || property.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
            || property.Contains("BaseColor", StringComparison.OrdinalIgnoreCase)
            || property.Contains("base_color", StringComparison.OrdinalIgnoreCase);

        /// <summary>An object's id (FBX 7) or its full name (FBX 6, "Model::Cube").</summary>
        private static string? KeyOf(object value) => value switch
        {
            long id => id.ToString(CultureInfo.InvariantCulture),
            string name => name,
            _ => null,
        };

        public Model3D Build(CancellationToken cancellationToken)
        {
            var model = new Model3D();
            var scene = AxisConversion() * Matrix4x4.CreateScale(UnitScale() * MillimetresPerCentimetre);
            foreach (var (key, node) in _ordered)
            {
                if (node.Name != "Model")
                    continue;
                // FBX 6 keeps the mesh in the model itself.
                var geometries = _geometries.GetValueOrDefault(key)
                    ?? (node.Child("PolygonVertexIndex") is not null ? new List<FbxNode> { node } : null);
                if (geometries is null)
                    continue;
                var transform = Geometric(node) * World(key, 0) * scene;
                var slots = _materials.GetValueOrDefault(key)?.Select(m => MaterialIndex(model, m)).ToList() ?? [];
                foreach (var geometry in geometries)
                    AddMesh(model, geometry, NameOf(node), transform, slots, cancellationToken);
            }
            return model;
        }

        // ───────────────────────── Transforms ─────────────────────────

        /// <summary>The file's axes to Y up (right X, up Y, front Z), right-handed as FBX always is.</summary>
        private Matrix4x4 AxisConversion()
        {
            var up = (int)(Number(_settings, "UpAxis") ?? 1);
            var coord = (int)(Number(_settings, "CoordAxis") ?? 0);
            if (up is < 0 or > 2)
                up = 1;
            if (coord is < 0 or > 2 || coord == up)
                coord = up == 0 ? 1 : 0;
            var front = 3 - up - coord;
            var matrix = Matrix4x4.Identity;
            matrix[0, 0] = matrix[1, 1] = matrix[2, 2] = 0;
            matrix[coord, 0] = Number(_settings, "CoordAxisSign") < 0 ? -1 : 1;
            matrix[up, 1] = Number(_settings, "UpAxisSign") < 0 ? -1 : 1;
            matrix[front, 2] = 1;
            // The front axis follows from the other two: its sign in the file is not always consistent.
            if (matrix.GetDeterminant() < 0)
                matrix[front, 2] = -1;
            return matrix;
        }

        /// <summary>Centimetres per file unit (1 when missing or nonsense).</summary>
        private float UnitScale()
        {
            var scale = Number(_settings, "UnitScaleFactor") ?? 1;
            return scale > 0 && double.IsFinite(scale) ? (float)scale : 1;
        }

        private Matrix4x4 World(string key, int depth)
        {
            if (_world.TryGetValue(key, out var world))
                return world;
            world = Local(_objects[key]);
            // A cycle in the connections stops at a depth no real hierarchy has.
            if (depth < 512 && _parents.TryGetValue(key, out var parent))
                world *= World(parent, depth + 1);
            _world[key] = world;
            return world;
        }

        /// <summary>
        /// The FBX SDK's local transform, T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹ for column
        /// vectors, written in reverse for System.Numerics' row vectors.
        /// </summary>
        private Matrix4x4 Local(FbxNode node)
        {
            var translation = Vector(node, "Lcl Translation") ?? Vector3.Zero;
            var rotation = Vector(node, "Lcl Rotation") ?? Vector3.Zero;
            var scaling = Vector(node, "Lcl Scaling") ?? Vector3.One;
            var preRotation = Vector(node, "PreRotation") ?? Vector3.Zero;
            var postRotation = Vector(node, "PostRotation") ?? Vector3.Zero;
            var rotationOffset = Vector(node, "RotationOffset") ?? Vector3.Zero;
            var rotationPivot = Vector(node, "RotationPivot") ?? Vector3.Zero;
            var scalingOffset = Vector(node, "ScalingOffset") ?? Vector3.Zero;
            var scalingPivot = Vector(node, "ScalingPivot") ?? Vector3.Zero;
            var order = (int)(Number(node, "RotationOrder") ?? 0);
            Matrix4x4.Invert(Euler(postRotation, 0), out var inversePost);
            return Matrix4x4.CreateTranslation(-scalingPivot)
                * Matrix4x4.CreateScale(scaling)
                * Matrix4x4.CreateTranslation(scalingPivot + scalingOffset - rotationPivot)
                * inversePost
                * Euler(rotation, order)
                * Euler(preRotation, 0)
                * Matrix4x4.CreateTranslation(rotationPivot + rotationOffset + translation);
        }

        /// <summary>The mesh's own offset from its model, which children do not inherit.</summary>
        private Matrix4x4 Geometric(FbxNode node) =>
            Matrix4x4.CreateScale(Vector(node, "GeometricScaling") ?? Vector3.One)
            * Euler(Vector(node, "GeometricRotation") ?? Vector3.Zero, 0)
            * Matrix4x4.CreateTranslation(Vector(node, "GeometricTranslation") ?? Vector3.Zero);

        /// <summary>Euler angles in degrees; <paramref name="order"/> is FBX's EFbxRotationOrder (0 XYZ: X first).</summary>
        private static Matrix4x4 Euler(Vector3 degrees, int order)
        {
            if (degrees == Vector3.Zero)
                return Matrix4x4.Identity;
            var x = Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180);
            var y = Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180);
            var z = Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180);
            return order switch
            {
                1 => x * z * y, // XZY
                2 => y * z * x, // YZX
                3 => y * x * z, // YXZ
                4 => z * x * y, // ZXY
                5 => z * y * x, // ZYX
                _ => x * y * z, // XYZ (and spheric XYZ)
            };
        }

        // ───────────────────────── Properties ─────────────────────────

        /// <summary>The property <paramref name="name"/> of an object (Properties70 or FBX 6's Properties60), else of its type's template.</summary>
        private FbxNode? Property(FbxNode? node, string name)
        {
            if (node is null)
                return null;
            return Find(node) ?? (_templates.TryGetValue(node.Name, out var template) ? Find(template) : null);

            FbxNode? Find(FbxNode owner)
            {
                foreach (var list in owner.Children)
                {
                    if (list.Name is not ("Properties70" or "Properties60"))
                        continue;
                    foreach (var property in list.Children)
                    {
                        if (property.Name is "P" or "Property" && property.Text(0) == name)
                            return property;
                    }
                }
                return null;
            }
        }

        /// <summary>The numbers of a property (after its name, type and flags).</summary>
        private List<double> Numbers(FbxNode? node, string name)
        {
            var values = new List<double>();
            if (Property(node, name) is { } property)
            {
                foreach (var value in property.Properties.Skip(1))
                {
                    if (AsNumber(value) is { } number)
                        values.Add(number);
                }
            }
            return values;
        }

        private double? Number(FbxNode? node, string name) => Numbers(node, name) is [var value, ..] ? value : null;

        private Vector3? Vector(FbxNode? node, string name)
        {
            var values = Numbers(node, name);
            if (values.Count < 3)
                return null;
            var vector = new Vector3((float)values[0], (float)values[1], (float)values[2]);
            return float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z) ? vector : null;
        }

        private static double? AsNumber(object value) => value switch
        {
            long l => l,
            double d => d,
            bool b => b ? 1 : 0,
            _ => null,
        };

        /// <summary>"Cube" of "Model::Cube" (ASCII) or "Cube\0\u0001Model" (binary).</summary>
        private static string NameOf(FbxNode node)
        {
            var name = node.Properties.OfType<string>().FirstOrDefault() ?? "";
            var binary = name.IndexOf("\0\u0001", StringComparison.Ordinal);
            if (binary >= 0)
                return name[..binary];
            var ascii = name.IndexOf("::", StringComparison.Ordinal);
            return ascii >= 0 ? name[(ascii + 2)..] : name;
        }

        // ───────────────────────── Materials ─────────────────────────

        private int MaterialIndex(Model3D model, string key)
        {
            if (_materialIndex.TryGetValue(key, out var index))
                return index;
            var node = _objects[key];
            var color = Vector(node, "DiffuseColor") ?? Vector(node, "Diffuse") ?? new Vector3(0.8f);
            var opacity = Math.Clamp((float)(Number(node, "Opacity") ?? 1), 0, 1);
            var material = new Material3D
            {
                Name = NameOf(node),
                BaseColor = new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), opacity),
                BaseColorTexture = _textures.TryGetValue(key, out var texture) ? Texture(texture.Texture) : null,
            };
            index = model.Materials.Count;
            model.Materials.Add(material);
            _materialIndex[key] = index;
            return index;
        }

        /// <summary>The picture of a texture: embedded in its video, else the file it names next to the model.</summary>
        private Texture3D? Texture(string key)
        {
            if (_loaded.TryGetValue(key, out var loaded))
                return loaded;
            var texture = _objects[key];
            var video = _videos.TryGetValue(key, out var videoKey) ? _objects[videoKey] : null;
            string[] references =
            [
                .. new[] { texture, video }.OfType<FbxNode>()
                    .SelectMany(n => n.Children.Where(c => c.Name is "RelativeFilename" or "FileName" or "Filename"))
                    .Select(n => n.Text(0)?.Replace('\\', '/')).OfType<string>().Where(s => s.Length > 0).Distinct(),
            ];
            var name = references.Select(Path.GetFileNameWithoutExtension).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? NameOf(texture);
            var extension = references.Select(Path.GetExtension).FirstOrDefault(e => !string.IsNullOrEmpty(e)) ?? "";

            Texture3D? result = null;
            if (video?.Child("Content") is { } content && Embedded(content) is { Length: > 0 } data)
                result = new Texture3D(data, Texture3D.Sniff(data, extension), name);
            else
            {
                foreach (var reference in references)
                {
                    if (ObjFormat.Resolve(_directory, reference) is { } file)
                    {
                        var bytes = File.ReadAllBytes(file);
                        result = new Texture3D(bytes, Texture3D.Sniff(bytes, Path.GetExtension(file)), name);
                        break;
                    }
                }
            }
            _loaded[key] = result;
            return result;
        }

        /// <summary>Video Content: raw bytes (binary) or base64 strings (ASCII).</summary>
        private static byte[]? Embedded(FbxNode content)
        {
            if (content.Properties is [byte[] bytes])
                return bytes;
            var base64 = string.Concat(content.Properties.OfType<string>());
            if (base64.Length == 0)
                return null;
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // ───────────────────────── Meshes ─────────────────────────

        private static void AddMesh(Model3D model, FbxNode geometry, string name, Matrix4x4 transform, List<int> slots, CancellationToken cancellationToken)
        {
            var positions = Doubles(geometry.Child("Vertices"));
            var polygons = Ints(geometry.Child("PolygonVertexIndex"));
            if (positions is null || polygons is null)
                return;
            var controlPoints = positions.Length / 3;
            var normals = Layer.Of(geometry, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            var uvs = Layer.Of(geometry, "LayerElementUV", "UV", "UVIndex", 2);
            var colors = Layer.Of(geometry, "LayerElementColor", "Colors", "ColorIndex", 4);
            // The material layer's values are the indices themselves (into the model's materials).
            var materials = Layer.Of(geometry, "LayerElementMaterial", "Materials", null, 1);

            var parts = new Dictionary<int, PartBuilder>();
            var corners = new List<int>();
            for (int i = 0, start = 0, polygon = 0; i < polygons.Length; i++)
            {
                if ((i & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                // A negative index ends a polygon (it is the last corner's index, bit-inverted); so does the end.
                if (polygons[i] >= 0 && i + 1 < polygons.Length)
                    continue;

                var slot = materials is null ? 0 : (int)(materials.Get1(i, 0, polygon) ?? -1);
                var material = (uint)slot < (uint)slots.Count ? slots[slot] : -1;
                if (!parts.TryGetValue(material, out var builder))
                {
                    parts[material] = builder = new PartBuilder(new MeshPart
                    {
                        Name = name,
                        Material = material,
                        Normals = normals is null ? null : [],
                        TexCoords = uvs is null ? null : [],
                        Colors = colors is null ? null : [],
                    });
                }

                corners.Clear();
                for (var k = start; k <= i; k++)
                {
                    var controlPoint = polygons[k] < 0 ? ~polygons[k] : polygons[k];
                    if (controlPoint >= controlPoints)
                        throw new InvalidDataException($"A polygon of the FBX model refers to vertex {controlPoint + 1}, but the mesh has only {controlPoints} vertices.");
                    corners.Add(builder.Vertex(positions, controlPoint,
                        normals?.Get3(k, controlPoint, polygon), uvs?.Get2(k, controlPoint, polygon), colors?.Get4(k, controlPoint, polygon)));
                }
                Triangulator.AddPolygon(builder.Part, CollectionsMarshal.AsSpan(corners));
                start = i + 1;
                polygon++;
            }

            foreach (var builder in parts.Values)
            {
                var part = builder.Part;
                if (builder.MissingNormal)
                    part.ComputeNormals();
                part.Transform(transform);
                model.Parts.Add(part);
            }
        }

        /// <summary>An array property as doubles, or the numbers of an FBX 6 ASCII node written inline.</summary>
        private static double[]? Doubles(FbxNode? node) => node?.Properties switch
        {
            null => null,
            [double[] d] => d,
            [float[] f] => [.. f.Select(v => (double)v)],
            [int[] i] => [.. i.Select(v => (double)v)],
            [long[] l] => [.. l.Select(v => (double)v)],
            var values => [.. values.Select(AsNumber).OfType<double>()],
        };

        private static int[]? Ints(FbxNode? node) => node?.Properties switch
        {
            null => null,
            [int[] i] => i,
            [long[] l] => [.. l.Select(v => (int)Math.Clamp(v, int.MinValue, int.MaxValue))],
            _ => Doubles(node) is { } d ? [.. d.Select(v => (int)Math.Clamp(v, int.MinValue, int.MaxValue))] : null,
        };

        /// <summary>One layer element: which value each polygon corner takes.</summary>
        private sealed class Layer(double[] values, int[]? index, Layer.Mapping mapping, int width)
        {
            public enum Mapping { ByPolygonVertex, ByControlPoint, ByPolygon, AllSame }

            /// <summary>The first usable element <paramref name="element"/> of the geometry (layer 0), or null.</summary>
            public static Layer? Of(FbxNode geometry, string element, string data, string? indexName, int width)
            {
                foreach (var node in geometry.ChildrenNamed(element))
                {
                    if (Doubles(node.Child(data)) is not { Length: > 0 } values)
                        continue;
                    Mapping? mapping = node.Child("MappingInformationType")?.Text(0) switch
                    {
                        "ByPolygonVertex" => Mapping.ByPolygonVertex,
                        "ByVertice" or "ByVertex" or "ByControlPoint" => Mapping.ByControlPoint,
                        "ByPolygon" => Mapping.ByPolygon,
                        "AllSame" => Mapping.AllSame,
                        _ => null, // by edge: no corner value
                    };
                    if (mapping is null)
                        continue;
                    var indexed = node.Child("ReferenceInformationType")?.Text(0) is "IndexToDirect" or "Index";
                    var index = indexed && indexName is not null ? Ints(node.Child(indexName)) : null;
                    if (indexed && indexName is not null && index is null)
                        continue;
                    return new Layer(values, index, mapping.Value, width);
                }
                return null;
            }

            public Vector3? Get3(int corner, int controlPoint, int polygon) =>
                Offset(corner, controlPoint, polygon) is var i and >= 0 ? new((float)values[i], (float)values[i + 1], (float)values[i + 2]) : null;

            public Vector2? Get2(int corner, int controlPoint, int polygon) =>
                Offset(corner, controlPoint, polygon) is var i and >= 0 ? new((float)values[i], (float)values[i + 1]) : null;

            public Vector4? Get4(int corner, int controlPoint, int polygon) =>
                Offset(corner, controlPoint, polygon) is var i and >= 0 ? new((float)values[i], (float)values[i + 1], (float)values[i + 2], (float)values[i + 3]) : null;

            public double? Get1(int corner, int controlPoint, int polygon) =>
                Offset(corner, controlPoint, polygon) is var i and >= 0 ? values[i] : null;

            /// <summary>Where the value of a corner starts in the values, or -1 when the file has none for it.</summary>
            private int Offset(int corner, int controlPoint, int polygon)
            {
                var i = mapping switch
                {
                    Mapping.ByPolygonVertex => corner,
                    Mapping.ByControlPoint => controlPoint,
                    Mapping.ByPolygon => polygon,
                    _ => 0,
                };
                if (index is not null)
                    i = (uint)i < (uint)index.Length ? index[i] : -1;
                return i >= 0 && (long)(i + 1) * width <= values.Length ? i * width : -1;
            }
        }

        /// <summary>Collects the vertices of one part, one per distinct corner.</summary>
        private sealed class PartBuilder(MeshPart part)
        {
            private readonly Dictionary<(int, Vector3, Vector2, Vector4), int> _vertices = [];

            public MeshPart Part { get; } = part;

            /// <summary>A corner without a normal although the mesh has normals: the part's normals are computed.</summary>
            public bool MissingNormal { get; private set; }

            public int Vertex(double[] positions, int controlPoint, Vector3? normal, Vector2? uv, Vector4? color)
            {
                if (Part.Normals is not null && (normal is null || normal == Vector3.Zero))
                    MissingNormal = true;
                var key = (controlPoint, normal ?? default, uv ?? default, color ?? default);
                if (_vertices.TryGetValue(key, out var existing))
                    return existing;
                var position = new Vector3((float)positions[controlPoint * 3], (float)positions[controlPoint * 3 + 1], (float)positions[controlPoint * 3 + 2]);
                var vertex = Part.AddVertex(position, normal, uv, color);
                _vertices[key] = vertex;
                return vertex;
            }
        }
    }
}
