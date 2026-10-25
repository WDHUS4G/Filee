// Wavefront OBJ with its MTL material library. Unitless (read as millimetres), Y up. Read: v (with the common
// "v x y z r g b" vertex colour extension), vt, vn, f with negative (relative) indices and polygons, o / g / usemtl
// as parts, mtllib with Kd, d / Tr and map_Kd textures next to the file. Written: one "o" per part, an .mtl next
// to the .obj and the textures next to that.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Models3D;

internal static class ObjFormat
{
    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = new Model3D();
        var positions = new List<float>();
        List<float>? colors = null;
        var uvs = new List<float>();
        var normals = new List<float>();
        // Materials of the libraries by name; only those a face uses become materials of the model.
        var library = new Dictionary<string, Material3D>(StringComparer.Ordinal);
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = new Dictionary<(string, string?), PartBuilder>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        string objectName = "", groupName = "";
        string? material = null;
        PartBuilder? current = null;
        Span<int> corners = stackalloc int[256];
        Span<float> numbers = stackalloc float[7];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var length = Math.Max(1, stream.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16);
        var lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            if ((lineNumber & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)stream.Position / length);
            }
            var line = raw;
            // A trailing backslash continues the statement on the next line.
            while (line.EndsWith('\\') && reader.ReadLine() is { } next)
            {
                line = line[..^1] + " " + next;
                lineNumber++;
            }
            var text = line.AsSpan().Trim();
            if (text.IsEmpty || text[0] == '#')
                continue;
            var space = text.IndexOfAny(' ', '\t');
            var keyword = space < 0 ? text : text[..space];
            var rest = space < 0 ? [] : text[(space + 1)..].Trim();

            switch (keyword)
            {
                case "v":
                    {
                        var values = numbers;
                        var n = ParseFloats(rest, values, lineNumber);
                        if (n < 3)
                            throw new InvalidDataException($"Line {lineNumber} of the OBJ file has a vertex with fewer than three coordinates.");
                        positions.Add(values[0]);
                        positions.Add(values[1]);
                        positions.Add(values[2]);
                        if (n >= 6)
                        {
                            colors ??= [.. Enumerable.Repeat(1f, (positions.Count / 3 - 1) * 3)];
                            var scale = values[3] > 1 || values[4] > 1 || values[5] > 1 ? 1 / 255f : 1;
                            colors.Add(Math.Clamp(values[3] * scale, 0, 1));
                            colors.Add(Math.Clamp(values[4] * scale, 0, 1));
                            colors.Add(Math.Clamp(values[5] * scale, 0, 1));
                        }
                        else if (colors is not null)
                        {
                            colors.Add(1);
                            colors.Add(1);
                            colors.Add(1);
                        }
                        break;
                    }
                case "vt":
                    {
                        var values = numbers[..3];
                        var n = ParseFloats(rest, values, lineNumber);
                        uvs.Add(n > 0 ? values[0] : 0);
                        uvs.Add(n > 1 ? values[1] : 0);
                        break;
                    }
                case "vn":
                    {
                        var values = numbers[..3];
                        if (ParseFloats(rest, values, lineNumber) < 3)
                            throw new InvalidDataException($"Line {lineNumber} of the OBJ file has a normal with fewer than three values.");
                        normals.Add(values[0]);
                        normals.Add(values[1]);
                        normals.Add(values[2]);
                        break;
                    }
                case "f" or "fo":
                    {
                        current ??= Part(groupName.Length > 0 ? groupName : objectName, material);
                        var count = 0;
                        foreach (var range in rest.SplitAny(' ', '\t'))
                        {
                            var token = rest[range];
                            if (token.IsEmpty)
                                continue;
                            if (count == corners.Length)
                                throw new InvalidDataException($"Line {lineNumber} of the OBJ file has a face with more than {corners.Length} corners.");
                            corners[count++] = current.Corner(token, positions, colors, uvs, normals, lineNumber);
                        }
                        Triangulator.AddPolygon(current.Part, corners[..count]);
                        break;
                    }
                case "o":
                    objectName = rest.ToString();
                    groupName = "";
                    current = null;
                    break;
                case "g":
                    groupName = rest.ToString();
                    current = null;
                    break;
                case "usemtl":
                    material = rest.ToString();
                    current = null;
                    break;
                case "mtllib":
                    foreach (var file in LibraryNames(rest.ToString(), directory))
                        MtlFormat.Read(file, library);
                    break;
            }
        }

        foreach (var builder in parts.Values)
        {
            var part = builder.Part;
            if (part.Normals is not null && HasZeroNormal(part.Normals))
                part.ComputeNormals();
            model.Parts.Add(part);
        }
        return model;

        PartBuilder Part(string name, string? materialName)
        {
            if (parts.TryGetValue((name, materialName), out var existing))
                return existing;
            var index = -1;
            if (materialName is not null && !materials.TryGetValue(materialName, out index))
            {
                // usemtl without a definition (missing .mtl) keeps the name, white.
                index = model.Materials.Count;
                model.Materials.Add(library.GetValueOrDefault(materialName) ?? new Material3D { Name = materialName });
                materials[materialName] = index;
            }
            var builder = new PartBuilder(new MeshPart
            {
                Name = name.Length > 0 ? name : materialName ?? "OBJ",
                Material = index,
            });
            parts[(name, materialName)] = builder;
            return builder;
        }
    }

    private static bool HasZeroNormal(List<float> normals)
    {
        for (var i = 0; i + 2 < normals.Count; i += 3)
        {
            if (normals[i] == 0 && normals[i + 1] == 0 && normals[i + 2] == 0)
                return true;
        }
        return false;
    }

    /// <summary>The .mtl files of an mtllib statement: the whole text as one name first (names may have spaces), else each word.</summary>
    private static IEnumerable<string> LibraryNames(string text, string directory)
    {
        var whole = Resolve(directory, text);
        if (whole is not null)
            return [whole];
        return text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(n => Resolve(directory, n)).OfType<string>();
    }

    /// <summary>
    /// Finds a file referenced from a 3D model (an .mtl, a texture, a glTF buffer) inside <paramref name="directory"/>
    /// or a folder below it, else by name only in <paramref name="directory"/>. A path that climbs out or points
    /// elsewhere is never opened: a downloaded model must not pull a picture from anywhere on the disk into its output.
    /// </summary>
    internal static string? Resolve(string directory, string reference)
    {
        reference = reference.Trim().Trim('"').Replace('\\', '/');
        if (reference.Length == 0)
            return null;
        try
        {
            var root = Path.GetFullPath(directory);
            if (!Path.EndsInDirectorySeparator(root))
                root += Path.DirectorySeparatorChar; // a drive or "/" already ends in one
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var candidate = Path.GetFullPath(Path.Combine(root, reference));
            if (candidate.StartsWith(root, comparison) && File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(root, Path.GetFileName(reference));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static int ParseFloats(ReadOnlySpan<char> text, Span<float> values, int lineNumber)
    {
        var n = 0;
        foreach (var range in text.SplitAny(' ', '\t'))
        {
            var token = text[range];
            if (token.IsEmpty)
                continue;
            if (n == values.Length)
                break;
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[n++]))
                throw new InvalidDataException($"Line {lineNumber} of the OBJ file has an invalid number: \"{token}\".");
        }
        return n;
    }

    /// <summary>Collects the vertices of one part, one per distinct (position, uv, normal) corner.</summary>
    private sealed class PartBuilder(MeshPart part)
    {
        private readonly Dictionary<(int, int, int), int> _corners = [];

        public MeshPart Part { get; } = part;

        public int Corner(ReadOnlySpan<char> token, List<float> positions, List<float>? colors, List<float> uvs, List<float> normals, int lineNumber)
        {
            int v = -1, t = -1, n = -1, slot = 0;
            foreach (var range in token.Split('/'))
            {
                var field = token[range];
                if (slot > 2)
                    break;
                if (!field.IsEmpty)
                {
                    if (!int.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value == 0)
                        throw new InvalidDataException($"Line {lineNumber} of the OBJ file has an invalid face index: \"{token}\".");
                    var count = slot switch { 0 => positions.Count / 3, 1 => uvs.Count / 2, _ => normals.Count / 3 };
                    var index = value > 0 ? value - 1 : count + value;
                    if ((uint)index >= (uint)count)
                        throw new InvalidDataException($"Line {lineNumber} of the OBJ file refers to {(slot switch { 0 => "vertex", 1 => "texture coordinate", _ => "normal" })} {value}, which does not exist (there are {count}).");
                    if (slot == 0) v = index;
                    else if (slot == 1) t = index;
                    else n = index;
                }
                slot++;
            }
            if (v < 0)
                throw new InvalidDataException($"Line {lineNumber} of the OBJ file has a face corner without a vertex: \"{token}\".");

            if (_corners.TryGetValue((v, t, n), out var existing))
                return existing;
            if (t >= 0 && Part.TexCoords is null)
                Part.TexCoords = [.. new float[Part.VertexCount * 2]];
            if (n >= 0 && Part.Normals is null)
                Part.Normals = [.. new float[Part.VertexCount * 3]];
            if (colors is not null && Part.Colors is null)
                Part.Colors = [.. Enumerable.Repeat(1f, Part.VertexCount * 4)];
            var vertex = Part.AddVertex(
                new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]),
                n >= 0 ? new Vector3(normals[n * 3], normals[n * 3 + 1], normals[n * 3 + 2]) : null,
                t >= 0 ? new Vector2(uvs[t * 2], uvs[t * 2 + 1]) : null,
                colors is not null ? new Vector4(colors[v * 3], colors[v * 3 + 1], colors[v * 3 + 2], 1) : null);
            _corners[(v, t, n)] = vertex;
            return vertex;
        }
    }

    /// <summary>
    /// Writes <paramref name="model"/> as OBJ. <paramref name="mtlPath"/> (null: no material library) gets the
    /// materials; <paramref name="texturePaths"/> maps each texture to the file it was written to.
    /// </summary>
    public static void Write(Model3D model, string objPath, string? mtlPath, IReadOnlyDictionary<Texture3D, string> texturePaths, CancellationToken cancellationToken)
    {
        var anyColors = model.Parts.Any(p => p.Colors is not null);
        var materialNames = UniqueNames(model.Materials.Select(m => m.Name), "Material");
        using (var writer = new StreamWriter(objPath, false, new UTF8Encoding(false), 1 << 16))
        {
            writer.NewLine = "\n";
            writer.WriteLine("# Written by Filee");
            if (mtlPath is not null)
                writer.WriteLine($"mtllib {RelativeReference(objPath, mtlPath)}");

            var partNames = UniqueNames(model.Parts.Select(p => p.Name), "Part");
            int vBase = 1, tBase = 1, nBase = 1;
            Span<char> buffer = stackalloc char[256];
            for (var p = 0; p < model.Parts.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = model.Parts[p];
                writer.WriteLine($"o {partNames[p]}");

                // Without vertex colours, a material's colour becomes the vertex colour, so parts look the same.
                var fallback = MeshMath.LinearToSrgb(model.MaterialOf(part)?.BaseColor ?? Vector4.One);
                for (var v = 0; v < part.VertexCount; v++)
                {
                    var position = part.Position(v);
                    var line = new LineBuilder(buffer);
                    line.Append("v");
                    line.Append(position.X);
                    line.Append(position.Y);
                    line.Append(position.Z);
                    if (anyColors)
                    {
                        var color = part.Colors is null ? fallback : part.Color(v);
                        line.Append(color.X);
                        line.Append(color.Y);
                        line.Append(color.Z);
                    }
                    writer.WriteLine(line.Text);
                }
                if (part.TexCoords is not null)
                {
                    for (var v = 0; v < part.VertexCount; v++)
                    {
                        var uv = part.TexCoord(v);
                        var line = new LineBuilder(buffer);
                        line.Append("vt");
                        line.Append(uv.X);
                        line.Append(uv.Y);
                        writer.WriteLine(line.Text);
                    }
                }
                if (part.Normals is not null)
                {
                    for (var v = 0; v < part.VertexCount; v++)
                    {
                        var n = part.Normal(v);
                        var line = new LineBuilder(buffer);
                        line.Append("vn");
                        line.Append(n.X);
                        line.Append(n.Y);
                        line.Append(n.Z);
                        writer.WriteLine(line.Text);
                    }
                }

                if (mtlPath is not null)
                    writer.WriteLine($"usemtl {(part.Material >= 0 && part.Material < model.Materials.Count ? materialNames[part.Material] : DefaultMaterial)}");
                var indices = part.IndexSpan;
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var line = new LineBuilder(buffer);
                    line.Append("f");
                    for (var k = 0; k < 3; k++)
                        line.AppendCorner(indices[i + k], vBase, part.TexCoords is null ? -1 : tBase, part.Normals is null ? -1 : nBase);
                    writer.WriteLine(line.Text);
                }
                vBase += part.VertexCount;
                if (part.TexCoords is not null)
                    tBase += part.VertexCount;
                if (part.Normals is not null)
                    nBase += part.VertexCount;
            }
        }

        if (mtlPath is not null)
            MtlFormat.Write(model, materialNames, mtlPath, texturePaths);
    }

    /// <summary>Name of the white material parts without one use when the file has a material library.</summary>
    internal const string DefaultMaterial = "Default";

    /// <summary>OBJ / MTL names without spaces, unique, never empty.</summary>
    internal static List<string> UniqueNames(IEnumerable<string> names, string fallback)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { DefaultMaterial };
        var result = new List<string>();
        foreach (var name in names)
        {
            var clean = new string([.. name.Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c)]);
            if (clean.Length == 0)
                clean = fallback;
            var unique = clean;
            for (var i = 2; !used.Add(unique); i++)
                unique = $"{clean}_{i}";
            result.Add(unique);
        }
        return result;
    }

    /// <summary>The path of <paramref name="target"/> as written in a file next to <paramref name="from"/> (forward slashes).</summary>
    internal static string RelativeReference(string from, string target) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(from))!, Path.GetFullPath(target)).Replace('\\', '/');

    /// <summary>Formats one OBJ statement into a stack buffer without allocating per number.</summary>
    private ref struct LineBuilder(Span<char> buffer)
    {
        private readonly Span<char> _buffer = buffer;
        private int _length;

        public readonly ReadOnlySpan<char> Text => _buffer[.._length];

        public void Append(string keyword)
        {
            keyword.AsSpan().CopyTo(_buffer[_length..]);
            _length += keyword.Length;
        }

        public void Append(float value)
        {
            _buffer[_length++] = ' ';
            if (!float.IsFinite(value))
                value = 0;
            value.TryFormat(_buffer[_length..], out var written, default, CultureInfo.InvariantCulture);
            _length += written;
        }

        public void AppendCorner(int index, int vBase, int tBase, int nBase)
        {
            _buffer[_length++] = ' ';
            AppendInt(index + vBase);
            if (tBase < 0 && nBase < 0)
                return;
            _buffer[_length++] = '/';
            if (tBase >= 0)
                AppendInt(index + tBase);
            if (nBase >= 0)
            {
                _buffer[_length++] = '/';
                AppendInt(index + nBase);
            }
        }

        private void AppendInt(int value)
        {
            value.TryFormat(_buffer[_length..], out var written, default, CultureInfo.InvariantCulture);
            _length += written;
        }
    }
}

/// <summary>MTL material libraries: Kd (diffuse colour), d / Tr (opacity) and map_Kd (diffuse texture).</summary>
internal static class MtlFormat
{
    /// <summary>Adds the materials of <paramref name="path"/> to <paramref name="library"/> (a later definition of a name wins).</summary>
    public static void Read(string path, Dictionary<string, Material3D> library)
    {
        var directory = Path.GetDirectoryName(path)!;
        Material3D? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var text = raw.AsSpan().Trim();
            if (text.IsEmpty || text[0] == '#')
                continue;
            var space = text.IndexOfAny(' ', '\t');
            var keyword = (space < 0 ? text : text[..space]).ToString();
            var rest = space < 0 ? "" : text[(space + 1)..].Trim().ToString();
            if (keyword == "newmtl")
            {
                current = new Material3D { Name = rest };
                library[rest] = current;
                continue;
            }
            if (current is null)
                continue;
            var values = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            switch (keyword.ToLowerInvariant())
            {
                case "kd" when values.Length >= 3 && Floats(values, 3) is { } kd:
                    current.BaseColor = new Vector4(kd[0], kd[1], kd[2], current.BaseColor.W);
                    break;
                case "d" when values.Length >= 1 && Floats(values[^1..], 1) is { } d:
                    current.BaseColor = current.BaseColor with { W = Math.Clamp(d[0], 0, 1) };
                    break;
                case "tr" when values.Length >= 1 && Floats(values[^1..], 1) is { } tr:
                    current.BaseColor = current.BaseColor with { W = Math.Clamp(1 - tr[0], 0, 1) };
                    break;
                case "map_kd" when TextureFile(values) is { } file && ObjFormat.Resolve(directory, file) is { } texture:
                    var data = File.ReadAllBytes(texture);
                    current.BaseColorTexture = new Texture3D(data, Texture3D.Sniff(data, Path.GetExtension(texture)), Path.GetFileNameWithoutExtension(texture));
                    break;
            }
        }
    }

    private static float[]? Floats(string[] values, int count)
    {
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        }
        return result;
    }

    /// <summary>The file name of a map_Kd statement after its options (-s 1 1 1, -bm 0.5, -clamp on, ...).</summary>
    private static string? TextureFile(string[] values)
    {
        var i = 0;
        while (i < values.Length && values[i].StartsWith('-'))
        {
            var option = values[i++].ToLowerInvariant();
            var arguments = option switch
            {
                "-o" or "-s" or "-t" => 3,
                "-mm" => 2,
                "-blendu" or "-blendv" or "-boost" or "-cc" or "-clamp" or "-texres" or "-bm" or "-imfchan" or "-type" => 1,
                _ => 0,
            };
            // -o, -s and -t take one to three numbers.
            for (var a = 0; a < arguments && i < values.Length; a++)
            {
                if (arguments == 3 && a > 0 && !float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    break;
                i++;
            }
        }
        return i < values.Length ? string.Join(' ', values[i..]) : null;
    }

    public static void Write(Model3D model, IReadOnlyList<string> names, string path, IReadOnlyDictionary<Texture3D, string> texturePaths)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.NewLine = "\n";
        writer.WriteLine("# Written by Filee");
        WriteMaterial(writer, ObjFormat.DefaultMaterial, new Material3D());
        for (var m = 0; m < model.Materials.Count; m++)
            WriteMaterial(writer, names[m], model.Materials[m]);

        void WriteMaterial(StreamWriter w, string name, Material3D material)
        {
            var c = material.BaseColor;
            w.WriteLine();
            w.WriteLine($"newmtl {name}");
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Kd {c.X} {c.Y} {c.Z}"));
            w.WriteLine("Ka 0 0 0");
            w.WriteLine("Ks 0 0 0");
            if (c.W < 1)
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"d {c.W}"));
            w.WriteLine("illum 1");
            if (material.BaseColorTexture is { } texture && texturePaths.TryGetValue(texture, out var file))
                w.WriteLine($"map_Kd {ObjFormat.RelativeReference(path, file)}");
        }
    }
}
