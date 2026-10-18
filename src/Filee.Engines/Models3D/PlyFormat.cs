// Stanford PLY: ASCII, binary little endian and binary big endian. Unitless (read as millimetres), Z up like Blender
// treats it. Read: vertex x/y/z, normals, texture coordinates (s/t, u/v, texture_u/texture_v), colours (red/green/
// blue/alpha in any type), faces as index lists (polygons are triangulated) with optional per-corner texture
// coordinates and face colours, and a "comment TextureFile" picture next to the file; other elements are skipped.
// Written: binary little endian, one mesh; PLY has no materials, so material colours become vertex colours.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Models3D;

internal static class PlyFormat
{
    private enum Encoding3 { Ascii, LittleEndian, BigEndian }

    private enum PlyType { Int8, UInt8, Int16, UInt16, Int32, UInt32, Float32, Float64 }

    private sealed record Property(string Name, PlyType Type, PlyType? CountType);

    private sealed class Element(string name, long count)
    {
        public string Name { get; } = name;
        public long Count { get; } = count;
        public List<Property> Properties { get; } = [];
    }

    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var (encoding, elements, textureFile) = ReadHeader(stream);
        IValueReader values = encoding == Encoding3.Ascii
            ? new AsciiReader(stream)
            : new BinaryReader(stream, encoding == Encoding3.BigEndian);

        var part = new MeshPart { Name = Path.GetFileNameWithoutExtension(path) };
        var model = new Model3D { Parts = { part } };
        // Vertices as read; faces with their own texture coordinates or colours get split copies.
        var vertexCount = 0;
        var split = new Dictionary<(int, Vector2, Vector4), int>();
        var claimed = new HashSet<int>();
        var total = Math.Max(1, elements.Sum(e => e.Count));
        long done = 0;
        Span<int> corners = stackalloc int[256];
        Span<double> color = stackalloc double[4];
        var cornerValues = new List<double>();
        var cornerUvs = new List<double>();

        foreach (var element in elements)
        {
            if (element.Name == "vertex")
            {
                vertexCount = (int)Math.Min(element.Count, int.MaxValue / 4);
                ReadVertices(element, values, part, progress, done, total, cancellationToken);
            }
            else if (element.Name == "face")
            {
                var indexProperty = element.Properties.FindIndex(p => p.CountType is not null && p.Name is "vertex_indices" or "vertex_index");
                if (indexProperty < 0)
                    throw new InvalidDataException("The PLY file's faces have no vertex_indices list.");
                var uvProperty = element.Properties.FindIndex(p => p.CountType is not null && p.Name == "texcoord");
                var colorRoles = element.Properties.Select(p => ColorChannel(p.Name)).ToArray();
                var hasFaceColor = colorRoles.Any(r => r >= 0);
                for (long f = 0; f < element.Count; f++)
                {
                    if ((f & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report((double)(done + f) / total);
                    }
                    cornerValues.Clear();
                    cornerUvs.Clear();
                    color[0] = color[1] = color[2] = color[3] = 1;
                    for (var p = 0; p < element.Properties.Count; p++)
                    {
                        var property = element.Properties[p];
                        if (property.CountType is { } countType)
                        {
                            var count = (long)values.Read(countType);
                            if (count < 0 || count > 1 << 20)
                                throw new InvalidDataException("The PLY file has a face with an invalid number of corners.");
                            var target = p == indexProperty ? cornerValues : p == uvProperty ? cornerUvs : null;
                            for (var i = 0; i < count; i++)
                            {
                                var value = values.Read(property.Type);
                                target?.Add(value);
                            }
                        }
                        else
                        {
                            var value = values.Read(property.Type);
                            if (colorRoles[p] >= 0)
                                color[colorRoles[p]] = ColorValue(value, property.Type);
                        }
                    }

                    var n = Math.Min(cornerValues.Count, corners.Length);
                    var wedgeUvs = cornerUvs.Count >= n * 2 && n > 0;
                    for (var i = 0; i < n; i++)
                    {
                        var index = (long)cornerValues[i];
                        if (index < 0 || index >= vertexCount)
                            throw new InvalidDataException($"A face of the PLY file refers to vertex {index}, but there are only {vertexCount} vertices.");
                        corners[i] = (int)index;
                        if (!wedgeUvs && !hasFaceColor)
                            continue;
                        var uv = wedgeUvs ? new Vector2((float)cornerUvs[i * 2], (float)cornerUvs[i * 2 + 1]) : part.TexCoord((int)index);
                        var rgba = hasFaceColor ? new Vector4((float)color[0], (float)color[1], (float)color[2], (float)color[3]) : part.Color((int)index);
                        corners[i] = Split(part, split, claimed, (int)index, uv, rgba, wedgeUvs, hasFaceColor);
                    }
                    Triangulator.AddPolygon(part, corners[..n]);
                }
            }
            else
            {
                // Edges, materials, ...: read past them.
                for (long i = 0; i < element.Count; i++)
                {
                    foreach (var property in element.Properties)
                    {
                        if (property.CountType is { } countType)
                        {
                            var count = (long)values.Read(countType);
                            for (var k = 0; k < count; k++)
                                values.Read(property.Type);
                        }
                        else
                        {
                            values.Read(property.Type);
                        }
                    }
                }
            }
            done += element.Count;
        }

        if (textureFile is not null && ObjFormat.Resolve(Path.GetDirectoryName(Path.GetFullPath(path))!, textureFile) is { } texture)
        {
            var data = File.ReadAllBytes(texture);
            model.Materials.Add(new Material3D
            {
                Name = Path.GetFileNameWithoutExtension(texture),
                BaseColorTexture = new Texture3D(data, Texture3D.Sniff(data, Path.GetExtension(texture)), Path.GetFileNameWithoutExtension(texture)),
            });
            part.Material = 0;
        }
        part.Transform(MeshMath.ZUpToYUp);
        return model;
    }

    /// <summary>A copy of <paramref name="vertex"/> with the corner's own texture coordinate and/or colour.</summary>
    private static int Split(MeshPart part, Dictionary<(int, Vector2, Vector4), int> split, HashSet<int> claimed, int vertex, Vector2 uv, Vector4 color, bool setUv, bool setColor)
    {
        if (setUv && part.TexCoords is null)
            part.TexCoords = [.. new float[part.VertexCount * 2]];
        if (setColor && part.Colors is null)
            part.Colors = [.. Enumerable.Repeat(1f, part.VertexCount * 4)];
        if (claimed.Add(vertex))
        {
            // The first face to use a vertex gives it its texture coordinate and colour.
            if (part.TexCoords is not null)
            {
                part.TexCoords[vertex * 2] = uv.X;
                part.TexCoords[vertex * 2 + 1] = uv.Y;
            }
            if (part.Colors is not null)
            {
                for (var k = 0; k < 4; k++)
                    part.Colors[vertex * 4 + k] = color[k];
            }
            return vertex;
        }
        if (part.TexCoord(vertex) == uv && part.Color(vertex) == color)
            return vertex;
        if (split.TryGetValue((vertex, uv, color), out var copy))
            return copy;
        copy = part.AddVertex(part.Position(vertex), part.Normals is null ? null : part.Normal(vertex), uv, color);
        split[(vertex, uv, color)] = copy;
        return copy;
    }

    private static void ReadVertices(Element element, IValueReader values, MeshPart part, IProgress<double>? progress, long done, long total, CancellationToken cancellationToken)
    {
        var names = element.Properties.Select(p => p.Name).ToList();
        int[] position = [names.IndexOf("x"), names.IndexOf("y"), names.IndexOf("z")];
        if (position.Any(i => i < 0))
            throw new InvalidDataException("The PLY file's vertices have no x, y and z.");
        int[] normal = [names.IndexOf("nx"), names.IndexOf("ny"), names.IndexOf("nz")];
        int[] uv = [IndexOfAny(names, "s", "u", "texture_u", "texture_s"), IndexOfAny(names, "t", "v", "texture_v", "texture_t")];
        var colorRoles = element.Properties.Select(p => ColorChannel(p.Name)).ToArray();
        var hasNormals = normal.All(i => i >= 0);
        var hasUvs = uv.All(i => i >= 0);
        var hasColors = colorRoles.Any(r => r >= 0);
        if (hasNormals)
            part.Normals = [];
        if (hasUvs)
            part.TexCoords = [];
        if (hasColors)
            part.Colors = [];

        var count = (int)Math.Min(element.Count, int.MaxValue / 4);
        part.Positions.Capacity = count * 3;
        var row = new double[element.Properties.Count];
        for (var v = 0; v < count; v++)
        {
            if ((v & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)(done + v) / total);
            }
            Vector4 color = Vector4.One;
            for (var p = 0; p < row.Length; p++)
            {
                var property = element.Properties[p];
                if (property.CountType is { } countType)
                {
                    var n = (long)values.Read(countType);
                    for (var k = 0; k < n; k++)
                        values.Read(property.Type);
                    continue;
                }
                row[p] = values.Read(property.Type);
                if (colorRoles[p] >= 0)
                    color[colorRoles[p]] = (float)ColorValue(row[p], property.Type);
            }
            part.AddVertex(
                new Vector3((float)row[position[0]], (float)row[position[1]], (float)row[position[2]]),
                hasNormals ? new Vector3((float)row[normal[0]], (float)row[normal[1]], (float)row[normal[2]]) : null,
                hasUvs ? new Vector2((float)row[uv[0]], (float)row[uv[1]]) : null,
                color);
        }
    }

    private static int IndexOfAny(List<string> names, params string[] candidates) =>
        candidates.Select(c => names.IndexOf(c)).FirstOrDefault(i => i >= 0, -1);

    /// <summary>0–3 for red, green, blue, alpha (also "diffuse_red" ...), else -1.</summary>
    private static int ColorChannel(string name) => name switch
    {
        "red" or "diffuse_red" or "r" => 0,
        "green" or "diffuse_green" or "g" => 1,
        "blue" or "diffuse_blue" or "b" => 2,
        "alpha" or "diffuse_alpha" or "a" => 3,
        _ => -1,
    };

    /// <summary>A colour channel as 0–1: integers are scaled by their range, floats are taken as they are.</summary>
    private static double ColorValue(double value, PlyType type) => type switch
    {
        PlyType.UInt8 or PlyType.Int8 => value / 255,
        PlyType.UInt16 or PlyType.Int16 => value / 65535,
        PlyType.UInt32 or PlyType.Int32 => value > 255 ? value / uint.MaxValue : value / 255,
        _ => Math.Clamp(value > 1 ? value / 255 : value, 0, 1),
    };

    private static (Encoding3, List<Element>, string?) ReadHeader(Stream stream)
    {
        var magic = ReadHeaderLine(stream);
        if (magic != "ply")
            throw new InvalidDataException("The file is not a PLY file (it does not start with \"ply\").");
        Encoding3? encoding = null;
        string? texture = null;
        var elements = new List<Element>();
        while (true)
        {
            var line = ReadHeaderLine(stream) ?? throw new InvalidDataException("The PLY header has no end_header line.");
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            switch (words[0])
            {
                case "end_header":
                    return (encoding ?? throw new InvalidDataException("The PLY header has no format line."), elements, texture);
                case "format" when words.Length >= 2:
                    encoding = words[1] switch
                    {
                        "ascii" => Encoding3.Ascii,
                        "binary_little_endian" => Encoding3.LittleEndian,
                        "binary_big_endian" => Encoding3.BigEndian,
                        _ => throw new InvalidDataException($"Unknown PLY format \"{words[1]}\"."),
                    };
                    break;
                case "comment" when words.Length >= 3 && words[1].Equals("TextureFile", StringComparison.OrdinalIgnoreCase):
                    texture = line[(line.IndexOf(words[1], StringComparison.Ordinal) + words[1].Length)..].Trim();
                    break;
                case "element" when words.Length >= 3:
                    if (!long.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
                        throw new InvalidDataException($"The PLY element \"{words[1]}\" has an invalid count.");
                    elements.Add(new Element(words[1], count));
                    break;
                case "property" when elements.Count > 0:
                    var element = elements[^1];
                    if (words.Length >= 5 && words[1] == "list")
                        element.Properties.Add(new Property(words[4], Type(words[3]), Type(words[2])));
                    else if (words.Length >= 3)
                        element.Properties.Add(new Property(words[2], Type(words[1]), null));
                    else
                        throw new InvalidDataException($"Invalid PLY property line: \"{line}\".");
                    break;
            }
        }
    }

    private static PlyType Type(string name) => name switch
    {
        "char" or "int8" => PlyType.Int8,
        "uchar" or "uint8" => PlyType.UInt8,
        "short" or "int16" => PlyType.Int16,
        "ushort" or "uint16" => PlyType.UInt16,
        "int" or "int32" => PlyType.Int32,
        "uint" or "uint32" => PlyType.UInt32,
        "float" or "float32" => PlyType.Float32,
        "double" or "float64" => PlyType.Float64,
        _ => throw new InvalidDataException($"Unknown PLY property type \"{name}\"."),
    };

    /// <summary>Reads one header line byte by byte, so the stream stops exactly where the body starts.</summary>
    private static string? ReadHeaderLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return bytes.Count == 0 ? null : System.Text.Encoding.ASCII.GetString([.. bytes]);
            if (b == '\n')
                return System.Text.Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            if (bytes.Count > 4096)
                throw new InvalidDataException("The file is not a PLY file (its header has an endless line).");
            bytes.Add((byte)b);
        }
    }

    private interface IValueReader
    {
        double Read(PlyType type);
    }

    private sealed class BinaryReader(Stream stream, bool bigEndian) : IValueReader
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _position;
        private int _length;

        public double Read(PlyType type)
        {
            var size = type switch
            {
                PlyType.Int8 or PlyType.UInt8 => 1,
                PlyType.Int16 or PlyType.UInt16 => 2,
                PlyType.Float64 => 8,
                _ => 4,
            };
            if (_length - _position < size)
                Fill(size);
            var data = _buffer.AsSpan(_position, size);
            _position += size;
            return type switch
            {
                PlyType.Int8 => (sbyte)data[0],
                PlyType.UInt8 => data[0],
                PlyType.Int16 => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(data) : BinaryPrimitives.ReadInt16LittleEndian(data),
                PlyType.UInt16 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data),
                PlyType.Int32 => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(data) : BinaryPrimitives.ReadInt32LittleEndian(data),
                PlyType.UInt32 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data),
                PlyType.Float32 => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(data) : BinaryPrimitives.ReadSingleLittleEndian(data),
                _ => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(data) : BinaryPrimitives.ReadDoubleLittleEndian(data),
            };
        }

        private void Fill(int size)
        {
            var left = _length - _position;
            Array.Copy(_buffer, _position, _buffer, 0, left);
            _position = 0;
            _length = left + stream.ReadAtLeast(_buffer.AsSpan(left), size - left, throwOnEndOfStream: false);
            if (_length < size)
                throw new InvalidDataException("The PLY file is cut short: it ends before all vertices and faces were read.");
        }
    }

    private sealed class AsciiReader(Stream stream) : IValueReader
    {
        private readonly StreamReader _reader = new(stream, System.Text.Encoding.ASCII, false, 1 << 16);
        private string _line = "";
        private int _position;

        public double Read(PlyType type)
        {
            while (true)
            {
                while (_position < _line.Length && char.IsWhiteSpace(_line[_position]))
                    _position++;
                if (_position < _line.Length)
                    break;
                _line = _reader.ReadLine() ?? throw new InvalidDataException("The PLY file is cut short: it ends before all vertices and faces were read.");
                _position = 0;
            }
            var start = _position;
            while (_position < _line.Length && !char.IsWhiteSpace(_line[_position]))
                _position++;
            var token = _line.AsSpan(start, _position - start);
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new InvalidDataException($"The PLY file has an invalid number: \"{token}\".");
        }
    }

    /// <summary>Writes one binary little-endian mesh (Z up, millimetres).</summary>
    public static void Write(Model3D model, Stream output, CancellationToken cancellationToken)
    {
        var hasNormals = model.Parts.Any(p => p.Normals is not null);
        var hasUvs = model.Parts.Any(p => p.TexCoords is not null);
        var hasColors = model.Parts.Any(p => p.Colors is not null || (model.MaterialOf(p) is { } m && m.BaseColor != Vector4.One));
        foreach (var part in model.Parts.Where(p => hasNormals && p.Normals is null))
            part.ComputeNormals();

        var header = new StringBuilder();
        header.Append("ply\nformat binary_little_endian 1.0\ncomment Written by Filee\n");
        header.Append(CultureInfo.InvariantCulture, $"element vertex {model.VertexCount}\n");
        header.Append("property float x\nproperty float y\nproperty float z\n");
        if (hasNormals)
            header.Append("property float nx\nproperty float ny\nproperty float nz\n");
        if (hasUvs)
            header.Append("property float s\nproperty float t\n");
        if (hasColors)
            header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\nproperty uchar alpha\n");
        header.Append(CultureInfo.InvariantCulture, $"element face {model.TriangleCount}\n");
        header.Append("property list uchar int vertex_indices\nend_header\n");

        using var stream = new BufferedStream(output, 1 << 20);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(header.ToString()));
        Span<byte> record = stackalloc byte[36];
        foreach (var part in model.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fallback = MeshMath.LinearToSrgb(model.MaterialOf(part)?.BaseColor ?? Vector4.One);
            for (var v = 0; v < part.VertexCount; v++)
            {
                var length = 0;
                Put(record, ref length, MeshMath.ToZUp(part.Position(v)));
                if (hasNormals)
                    Put(record, ref length, MeshMath.ToZUp(part.Normal(v)));
                if (hasUvs)
                {
                    var uv = part.TexCoord(v);
                    BinaryPrimitives.WriteSingleLittleEndian(record[length..], uv.X);
                    BinaryPrimitives.WriteSingleLittleEndian(record[(length + 4)..], uv.Y);
                    length += 8;
                }
                if (hasColors)
                {
                    var c = part.Colors is null ? fallback : part.Color(v);
                    record[length++] = MeshMath.ToByte(c.X);
                    record[length++] = MeshMath.ToByte(c.Y);
                    record[length++] = MeshMath.ToByte(c.Z);
                    record[length++] = MeshMath.ToByte(c.W);
                }
                stream.Write(record[..length]);
            }
        }

        var offset = 0;
        Span<byte> face = stackalloc byte[13];
        face[0] = 3;
        foreach (var part in model.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = part.IndexSpan;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                BinaryPrimitives.WriteInt32LittleEndian(face[1..], indices[i] + offset);
                BinaryPrimitives.WriteInt32LittleEndian(face[5..], indices[i + 1] + offset);
                BinaryPrimitives.WriteInt32LittleEndian(face[9..], indices[i + 2] + offset);
                stream.Write(face);
            }
            offset += part.VertexCount;
        }

        static void Put(Span<byte> record, ref int length, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(record[length..], v.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[(length + 4)..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record[(length + 8)..], v.Z);
            length += 12;
        }
    }
}
