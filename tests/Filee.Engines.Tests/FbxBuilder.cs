// FBX files for the tests, built in code: a node tree (FbxBuilder.Node) and writers for binary FBX 7.4 (32-bit
// record offsets) and 7.5 (64-bit), with arrays plain or zlib-compressed, and for ASCII FBX 7.4. Just the nodes
// Filee's reader looks at: FBXHeaderExtension, GlobalSettings, Definitions, Objects and Connections.

using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Filee.Engines.Tests;

internal static class FbxBuilder
{
    /// <summary>A node: name, properties and children (collection initialiser).</summary>
    public sealed class Node(string name, params object[] properties) : IEnumerable<Node>
    {
        public string Name { get; } = name;
        public object[] Properties { get; } = properties;
        public List<Node> Children { get; } = [];

        public void Add(Node child) => Children.Add(child);

        public void Add(IEnumerable<Node> children) => Children.AddRange(children);

        public IEnumerator<Node> GetEnumerator() => Children.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ───────────────────────── Scenes ─────────────────────────

    /// <summary>The ids of the test scenes' objects.</summary>
    public const long GeometryId = 100, ParentId = 200, CubeId = 300, MaterialId = 400, TextureId = 500, VideoId = 600;

    /// <summary>
    /// A cube from −1 to 1 (quads, normals per corner, UVs by index) in model "Cube" (scaled 1, 2, 3, turned 90° about
    /// Z, moved 5 up) under model "Parent" (moved 10 along X), with the red material "Red" whose opacity 0.5 comes
    /// from the material template. In centimetres and Y up the cube spans (80, 40, −30)–(120, 60, 30) mm.
    /// </summary>
    public static List<Node> CubeScene(int version = 7400, int upAxis = 1, double unitScale = 1, byte[]? texture = null)
    {
        var (positions, polygons) = Cube();
        var objects = new Node("Objects")
        {
            Mesh(GeometryId, "Cube", positions, polygons,
                Normals(positions, polygons),
                new Node("LayerElementUV", 0)
                {
                    new Node("Version", 101),
                    new Node("Name", "UVMap"),
                    new Node("MappingInformationType", "ByPolygonVertex"),
                    new Node("ReferenceInformationType", "IndexToDirect"),
                    new Node("UV", new double[] { 0, 0, 1, 0, 1, 1, 0, 1 }),
                    new Node("UVIndex", Enumerable.Range(0, polygons.Length).Select(i => i % 4).ToArray()),
                },
                MaterialLayer("AllSame", 0)),
            Model(ParentId, "Parent", "Null", ("Lcl Translation", new Vector3(10, 0, 0))),
            Model(CubeId, "Cube", "Mesh",
                ("Lcl Translation", new Vector3(0, 5, 0)),
                ("Lcl Rotation", new Vector3(0, 0, 90)),
                ("Lcl Scaling", new Vector3(1, 2, 3))),
            Material(MaterialId, "Red", new Vector3(1, 0, 0)),
        };
        var connections = new Node("Connections")
        {
            Link(ParentId, 0),
            Link(CubeId, ParentId),
            Link(GeometryId, CubeId),
            Link(MaterialId, CubeId),
        };
        if (texture is not null)
        {
            objects.Add(new Node("Texture", TextureId, "Texture::Checker", "")
            {
                new Node("Type", "TextureVideoClip"),
                new Node("FileName", @"C:\Somewhere\else\checker.png"),
                new Node("RelativeFilename", "checker.png"),
            });
            objects.Add(new Node("Video", VideoId, "Video::Checker", "Clip")
            {
                new Node("Type", "Clip"),
                new Node("RelativeFilename", "checker.png"),
                new Node("Content", texture),
            });
            connections.Add(new Node("C", "OP", TextureId, MaterialId, "DiffuseColor"));
            connections.Add(Link(VideoId, TextureId));
        }
        return Document(version, upAxis, unitScale, objects, connections);
    }

    /// <summary>The header, global settings and material template around <paramref name="objects"/>.</summary>
    public static List<Node> Document(int version, int upAxis, double unitScale, Node objects, Node connections) =>
    [
        new Node("FBXHeaderExtension")
        {
            new Node("FBXHeaderVersion", 1003),
            new Node("FBXVersion", version),
            new Node("Creator", "Filee tests"),
        },
        new Node("GlobalSettings")
        {
            new Node("Version", 1000),
            new Node("Properties70")
            {
                IntProperty("UpAxis", upAxis),
                IntProperty("UpAxisSign", 1),
                // Z up as 3ds Max writes it: front is −Y.
                IntProperty("FrontAxis", upAxis == 2 ? 1 : 2),
                IntProperty("FrontAxisSign", upAxis == 2 ? -1 : 1),
                IntProperty("CoordAxis", 0),
                IntProperty("CoordAxisSign", 1),
                new Node("P", "UnitScaleFactor", "double", "Number", "", unitScale),
            },
        },
        new Node("Definitions")
        {
            new Node("Version", 100),
            new Node("ObjectType", "Material")
            {
                new Node("Count", 1),
                new Node("PropertyTemplate", "FbxSurfacePhong")
                {
                    new Node("Properties70")
                    {
                        ColorProperty("DiffuseColor", new Vector3(0.8f)),
                        new Node("P", "Opacity", "double", "Number", "", 0.5),
                    },
                },
            },
        },
        objects,
        connections,
    ];

    /// <summary>The 8 corners and 6 outward quads of the cube from −1 to 1 (the last corner of each quad bit-inverted).</summary>
    public static (double[] Positions, int[] Polygons) Cube()
    {
        var positions = new List<double>();
        for (var i = 0; i < 8; i++)
            positions.AddRange([(i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1]);
        int[][] quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        return ([.. positions], [.. quads.SelectMany(q => new[] { q[0], q[1], q[2], ~q[3] })]);
    }

    public static Node Mesh(long id, string name, double[] positions, int[] polygons, params Node[] layers)
    {
        var node = new Node("Geometry", id, $"Geometry::{name}", "Mesh")
        {
            new Node("Vertices", positions),
            new Node("PolygonVertexIndex", polygons),
            new Node("GeometryVersion", 124),
        };
        node.Add(layers);
        return node;
    }

    /// <summary>Face normals per polygon corner (by polygon vertex, direct).</summary>
    public static Node Normals(double[] positions, int[] polygons)
    {
        var normals = new List<double>();
        var start = 0;
        for (var i = 0; i < polygons.Length; i++)
        {
            if (polygons[i] >= 0)
                continue;
            Vector3 P(int k)
            {
                var v = polygons[k] < 0 ? ~polygons[k] : polygons[k];
                return new Vector3((float)positions[v * 3], (float)positions[v * 3 + 1], (float)positions[v * 3 + 2]);
            }
            var n = Vector3.Normalize(Vector3.Cross(P(start + 1) - P(start), P(start + 2) - P(start)));
            for (var k = start; k <= i; k++)
                normals.AddRange([n.X, n.Y, n.Z]);
            start = i + 1;
        }
        return new Node("LayerElementNormal", 0)
        {
            new Node("Version", 101),
            new Node("MappingInformationType", "ByPolygonVertex"),
            new Node("ReferenceInformationType", "Direct"),
            new Node("Normals", normals.ToArray()),
        };
    }

    public static Node MaterialLayer(string mapping, params int[] materials) => new("LayerElementMaterial", 0)
    {
        new Node("Version", 101),
        new Node("MappingInformationType", mapping),
        new Node("ReferenceInformationType", "IndexToDirect"),
        new Node("Materials", materials),
    };

    public static Node Model(long id, string name, string type, params (string Name, Vector3 Value)[] properties)
    {
        var list = new Node("Properties70");
        foreach (var (property, value) in properties)
            list.Add(new Node("P", property, property, "", "A", (double)value.X, (double)value.Y, (double)value.Z));
        return new Node("Model", id, $"Model::{name}", type) { new Node("Version", 232), list };
    }

    public static Node Material(long id, string name, Vector3 color) => new("Material", id, $"Material::{name}", "")
    {
        new Node("ShadingModel", "phong"),
        new Node("Properties70") { ColorProperty("DiffuseColor", color) },
    };

    public static Node Link(long child, long parent) => new("C", "OO", child, parent);

    public static Node IntProperty(string name, int value) => new("P", name, "int", "Integer", "", value);

    public static Node ColorProperty(string name, Vector3 color) =>
        new("P", name, "Color", "", "A", (double)color.X, (double)color.Y, (double)color.Z);

    // ───────────────────────── Binary ─────────────────────────

    /// <summary>Binary FBX: 64-bit record offsets from version 7500; arrays zlib-compressed when <paramref name="compress"/>.</summary>
    public static void WriteBinary(string path, IEnumerable<Node> nodes, int version = 7400, bool compress = false)
    {
        var wide = version >= 7500;
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("Kaydara FBX Binary  \0"u8);
        writer.Write((byte)0x1A);
        writer.Write((byte)0);
        writer.Write((uint)version);
        foreach (var node in nodes)
            WriteNode(writer, node, wide, compress);
        writer.Write(new byte[wide ? 25 : 13]);
        writer.Write(new byte[16]); // stands for the footer
        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static void WriteNode(BinaryWriter writer, Node node, bool wide, bool compress)
    {
        var stream = writer.BaseStream;
        var start = stream.Position;
        writer.Write(new byte[wide ? 24 : 12]);
        var name = Encoding.ASCII.GetBytes(node.Name);
        writer.Write((byte)name.Length);
        writer.Write(name);
        var propertiesStart = stream.Position;
        foreach (var property in node.Properties)
            WriteProperty(writer, property, compress);
        var propertiesLength = stream.Position - propertiesStart;
        if (node.Children.Count > 0)
        {
            foreach (var child in node.Children)
                WriteNode(writer, child, wide, compress);
            writer.Write(new byte[wide ? 25 : 13]);
        }
        var end = stream.Position;
        stream.Position = start;
        if (wide)
        {
            writer.Write((ulong)end);
            writer.Write((ulong)node.Properties.Length);
            writer.Write((ulong)propertiesLength);
        }
        else
        {
            writer.Write((uint)end);
            writer.Write((uint)node.Properties.Length);
            writer.Write((uint)propertiesLength);
        }
        stream.Position = end;
    }

    private static void WriteProperty(BinaryWriter writer, object property, bool compress)
    {
        switch (property)
        {
            case int i:
                writer.Write((byte)'I');
                writer.Write(i);
                break;
            case long l:
                writer.Write((byte)'L');
                writer.Write(l);
                break;
            case double d:
                writer.Write((byte)'D');
                writer.Write(d);
                break;
            case string s:
                var bytes = Encoding.UTF8.GetBytes(BinaryName(s));
                writer.Write((byte)'S');
                writer.Write((uint)bytes.Length);
                writer.Write(bytes);
                break;
            case byte[] raw:
                writer.Write((byte)'R');
                writer.Write((uint)raw.Length);
                writer.Write(raw);
                break;
            case double[] doubles:
                WriteArray(writer, 'd', doubles.Length, MemoryMarshal.AsBytes(doubles.AsSpan()), compress);
                break;
            case int[] ints:
                WriteArray(writer, 'i', ints.Length, MemoryMarshal.AsBytes(ints.AsSpan()), compress);
                break;
            default:
                throw new ArgumentException($"No FBX property type for {property.GetType()}.");
        }
    }

    private static void WriteArray(BinaryWriter writer, char type, int length, ReadOnlySpan<byte> data, bool compress)
    {
        writer.Write((byte)type);
        writer.Write((uint)length);
        writer.Write(compress ? 1u : 0u);
        if (!compress)
        {
            writer.Write((uint)data.Length);
            writer.Write(data);
            return;
        }
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        writer.Write((uint)packed.Length);
        writer.Write(packed.ToArray());
    }

    /// <summary>"Model::Cube" as binary FBX writes object names: "Cube\0\u0001Model".</summary>
    private static string BinaryName(string text)
    {
        var separator = text.IndexOf("::", StringComparison.Ordinal);
        return separator > 0 && text[..separator] is "Model" or "Geometry" or "Material" or "Texture" or "Video"
            ? $"{text[(separator + 2)..]}\0\u0001{text[..separator]}"
            : text;
    }

    // ───────────────────────── ASCII ─────────────────────────

    public static void WriteAscii(string path, IEnumerable<Node> nodes)
    {
        var text = new StringBuilder("; FBX 7.4.0 project file\n; Written by the Filee tests\n\n");
        foreach (var node in nodes)
            WriteAscii(text, node, 0);
        File.WriteAllText(path, text.ToString());
    }

    private static void WriteAscii(StringBuilder text, Node node, int depth)
    {
        var indent = new string('\t', depth);
        text.Append(indent).Append(node.Name).Append(':');
        if (node.Properties is [Array array] && array is not byte[])
        {
            var values = array.Cast<object>().Select(Format).ToList();
            text.Append(" *").Append(values.Count).Append(" {\n")
                .Append(indent).Append("\ta: ");
            // Long arrays are wrapped after a comma, as the FBX SDK does.
            for (var i = 0; i < values.Count; i++)
                text.Append(i == 0 ? "" : i % 8 == 0 ? ",\n" + indent + "\t" : ",").Append(values[i]);
            text.Append('\n').Append(indent).Append("} \n");
            return;
        }
        // Embedded files start with an empty value: "Content: , "base64...""
        text.Append(node.Properties is [byte[], ..] ? " , " : " ").AppendJoin(", ", node.Properties.Select(Format));
        if (node.Children.Count > 0)
        {
            text.Append(" {\n");
            foreach (var child in node.Children)
                WriteAscii(text, child, depth + 1);
            text.Append(indent).Append('}');
        }
        text.Append('\n');
    }

    private static string Format(object value) => value switch
    {
        string s => $"\"{s}\"",
        byte[] bytes => $"\"{Convert.ToBase64String(bytes)}\"",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
