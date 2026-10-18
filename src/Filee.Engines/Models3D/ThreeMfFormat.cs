// 3MF (3D Manufacturing Format): a ZIP package with XML models (core specification 1.3). Z up, unit from the model's
// "unit" attribute. Read: objects with meshes and components (also in other model files of the package, as slicers
// such as Bambu Studio write them), build items with transforms, base materials (one part per material) and the
// materials extension's colour groups (per-corner colours). Objects of type "support" and "other" are skipped.
// Written: millimetres, one object per part with its vertices welded by position (slicers need closed meshes),
// base materials from the material colours and colour groups from the vertex colours. UVs, normals and textures
// are not written.

using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace Filee.Engines.Models3D;

internal static class ThreeMfFormat
{
    private const string CoreNs = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private const string MaterialNs = "http://schemas.microsoft.com/3dmanufacturing/material/2015/02";
    private const string ProductionNs = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
    private const string ModelRelationship = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
    private const int MaxDepth = 32;

    /// <summary>Millimetres per 3MF unit.</summary>
    private static double UnitScale(string? unit) => unit switch
    {
        null or "" or "millimeter" => 1,
        "micron" => 0.001,
        "centimeter" => 10,
        "inch" => 25.4,
        "foot" => 304.8,
        "meter" => 1000,
        _ => throw new InvalidDataException($"The 3MF model has an unknown unit \"{unit}\"."),
    };

    private sealed class ModelFile
    {
        public double Scale = 1;
        public Dictionary<int, ObjectDef> Objects { get; } = [];
        public Dictionary<int, List<(string Name, Vector4 Color)>> BaseMaterials { get; } = [];
        public Dictionary<int, List<Vector4>> ColorGroups { get; } = [];
        public List<(int ObjectId, string? Path, Matrix4x4 Transform)> Build { get; } = [];
    }

    private sealed class ObjectDef
    {
        public string Name = "";
        public string Type = "model";
        public int? Pid;
        public int? PIndex;
        public List<float> Vertices { get; } = [];
        /// <summary>v1, v2, v3, pid, p1, p2, p3 per triangle (-1 when not given).</summary>
        public List<int> Triangles { get; } = [];
        public List<(int ObjectId, string? Path, Matrix4x4 Transform)> Components { get; } = [];
    }

    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The file is not a 3MF package (it is not a valid ZIP file).", ex);
        }
        using (zip)
        {
            var rootPath = FindRootModel(zip);
            var files = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
            var root = Load(zip, rootPath, files, cancellationToken);
            progress?.Report(0.6);

            var model = new Model3D();
            var materials = new Dictionary<(ModelFile, int, int), int>();
            foreach (var (objectId, itemPath, transform) in root.Build)
            {
                var file = itemPath is null ? root : Load(zip, itemPath, files, cancellationToken);
                Instantiate(model, zip, files, file, objectId, transform, materials, 0, cancellationToken);
            }
            var toYUp = Matrix4x4.CreateScale((float)root.Scale) * MeshMath.ZUpToYUp;
            foreach (var part in model.Parts)
                part.Transform(toYUp);
            return model;
        }
    }

    private static string FindRootModel(ZipArchive zip)
    {
        if (zip.GetEntry("_rels/.rels") is { } rels)
        {
            using var stream = rels.Open();
            using var reader = XmlReader.Create(stream, Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Type") == ModelRelationship && reader.GetAttribute("Target") is { } target)
                    return target.TrimStart('/');
            }
        }
        return zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))?.FullName
               ?? throw new InvalidDataException("The 3MF package has no 3D model part.");
    }

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    private static ModelFile Load(ZipArchive zip, string path, Dictionary<string, ModelFile> files, CancellationToken cancellationToken)
    {
        path = path.TrimStart('/');
        if (files.TryGetValue(path, out var loaded))
            return loaded;
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"The 3MF package has no part \"{path}\".");
        var file = new ModelFile();
        files[path] = file;
        using var stream = entry.Open();
        try
        {
            Parse(stream, file, cancellationToken);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The 3MF model \"{path}\" is not valid XML: {ex.Message}", ex);
        }
        return file;
    }

    private static void Parse(Stream stream, ModelFile file, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, Settings);
        ObjectDef? current = null;
        List<(string, Vector4)>? baseMaterials = null;
        List<Vector4>? colors = null;
        var count = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "object":
                        current = null;
                        break;
                    case "basematerials":
                        baseMaterials = null;
                        break;
                    case "colorgroup":
                        colors = null;
                        break;
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if ((++count & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var ns = reader.NamespaceURI;
            switch (reader.LocalName)
            {
                case "model":
                    file.Scale = UnitScale(reader.GetAttribute("unit"));
                    break;
                case "vertex" when current is not null:
                    current.Vertices.Add(Float(reader, "x"));
                    current.Vertices.Add(Float(reader, "y"));
                    current.Vertices.Add(Float(reader, "z"));
                    break;
                case "triangle" when current is not null:
                    current.Triangles.Add(Int(reader, "v1"));
                    current.Triangles.Add(Int(reader, "v2"));
                    current.Triangles.Add(Int(reader, "v3"));
                    current.Triangles.Add(OptionalInt(reader, "pid") ?? -1);
                    current.Triangles.Add(OptionalInt(reader, "p1") ?? -1);
                    current.Triangles.Add(OptionalInt(reader, "p2") ?? -1);
                    current.Triangles.Add(OptionalInt(reader, "p3") ?? -1);
                    break;
                case "object":
                    current = new ObjectDef
                    {
                        Name = reader.GetAttribute("name") ?? "",
                        Type = reader.GetAttribute("type") ?? "model",
                        Pid = OptionalInt(reader, "pid"),
                        PIndex = OptionalInt(reader, "pindex"),
                    };
                    file.Objects[Int(reader, "id")] = current;
                    if (reader.IsEmptyElement)
                        current = null;
                    break;
                case "component" when current is not null:
                    current.Components.Add((Int(reader, "objectid"), reader.GetAttribute("path", ProductionNs), Transform(reader)));
                    break;
                case "item":
                    file.Build.Add((Int(reader, "objectid"), reader.GetAttribute("path", ProductionNs), Transform(reader)));
                    break;
                case "basematerials":
                    baseMaterials = [];
                    file.BaseMaterials[Int(reader, "id")] = baseMaterials;
                    break;
                case "base" when baseMaterials is not null:
                    baseMaterials.Add((reader.GetAttribute("name") ?? "", MeshMath.ParseHex(reader.GetAttribute("displaycolor")) ?? Vector4.One));
                    break;
                case "colorgroup" when ns == MaterialNs || ns.Length == 0:
                    colors = [];
                    file.ColorGroups[Int(reader, "id")] = colors;
                    break;
                case "color" when colors is not null:
                    colors.Add(MeshMath.ParseHex(reader.GetAttribute("color")) ?? Vector4.One);
                    break;
            }
        }
    }

    private static void Instantiate(Model3D model, ZipArchive zip, Dictionary<string, ModelFile> files, ModelFile file, int objectId,
        Matrix4x4 transform, Dictionary<(ModelFile, int, int), int> materials, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("The 3MF model's components are nested too deeply (or refer to themselves).");
        if (!file.Objects.TryGetValue(objectId, out var definition))
            throw new InvalidDataException($"The 3MF model refers to object {objectId}, which does not exist.");
        if (definition.Type is "support" or "other")
            return;

        if (definition.Triangles.Count > 0)
            AddMesh(model, file, definition, transform, materials, cancellationToken);
        foreach (var (childId, childPath, childTransform) in definition.Components)
        {
            var childFile = childPath is null ? file : Load(zip, childPath, files, cancellationToken);
            Instantiate(model, zip, files, childFile, childId, childTransform * transform, materials, depth + 1, cancellationToken);
        }
    }

    private static void AddMesh(Model3D model, ModelFile file, ObjectDef definition, Matrix4x4 transform,
        Dictionary<(ModelFile, int, int), int> materials, CancellationToken cancellationToken)
    {
        var vertexCount = definition.Vertices.Count / 3;
        // One part per base material; per-corner colours split vertices by colour.
        var groups = new Dictionary<int, (MeshPart Part, Dictionary<(int, int), int> Map)>();
        var triangles = definition.Triangles;
        Span<int> corners = stackalloc int[3];
        for (var t = 0; t + 6 < triangles.Count; t += 7)
        {
            if ((t & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var pid = triangles[t + 3] >= 0 ? triangles[t + 3] : definition.Pid ?? -1;
            var p1 = triangles[t + 4] >= 0 ? triangles[t + 4] : definition.PIndex ?? 0;
            var key = -1;
            List<Vector4>? colorGroup = null;
            if (file.BaseMaterials.TryGetValue(pid, out var bases) && p1 < bases.Count)
                key = MaterialIndex(model, file, pid, p1, bases[p1], materials);
            else if (file.ColorGroups.TryGetValue(pid, out colorGroup))
                key = -2;

            if (!groups.TryGetValue(key, out var group))
            {
                var name = definition.Name.Length > 0 ? definition.Name : "Object";
                var part = new MeshPart { Name = name, Material = key >= 0 ? key : -1 };
                if (key == -2)
                    part.Colors = [];
                group = (part, []);
                groups[key] = group;
                model.Parts.Add(part);
            }

            for (var k = 0; k < 3; k++)
            {
                var v = triangles[t + k];
                if ((uint)v >= (uint)vertexCount)
                    throw new InvalidDataException($"A triangle of 3MF object \"{definition.Name}\" refers to vertex {v}, but it has only {vertexCount}.");
                var colorIndex = -1;
                if (colorGroup is not null)
                {
                    var p = k == 0 ? p1 : triangles[t + 4 + k] >= 0 ? triangles[t + 4 + k] : p1;
                    colorIndex = p < colorGroup.Count ? p : -1;
                }
                if (!group.Map.TryGetValue((v, colorIndex), out var index))
                {
                    var position = Vector3.Transform(new Vector3(definition.Vertices[v * 3], definition.Vertices[v * 3 + 1], definition.Vertices[v * 3 + 2]), transform);
                    index = group.Part.AddVertex(position, color: colorIndex >= 0 ? colorGroup![colorIndex] : Vector4.One);
                    group.Map[(v, colorIndex)] = index;
                }
                corners[k] = index;
            }
            if (transform.GetDeterminant() < 0)
                group.Part.AddTriangle(corners[0], corners[2], corners[1]);
            else
                group.Part.AddTriangle(corners[0], corners[1], corners[2]);
        }
    }

    private static int MaterialIndex(Model3D model, ModelFile file, int pid, int index, (string Name, Vector4 Color) material,
        Dictionary<(ModelFile, int, int), int> materials)
    {
        if (materials.TryGetValue((file, pid, index), out var existing))
            return existing;
        existing = model.Materials.Count;
        model.Materials.Add(new Material3D
        {
            Name = material.Name.Length > 0 ? material.Name : $"Material {existing + 1}",
            BaseColor = MeshMath.SrgbToLinear(material.Color),
        });
        materials[(file, pid, index)] = existing;
        return existing;
    }

    private static Matrix4x4 Transform(XmlReader reader)
    {
        var text = reader.GetAttribute("transform");
        if (string.IsNullOrWhiteSpace(text))
            return Matrix4x4.Identity;
        var values = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN)
            .ToArray();
        if (values.Length != 12 || values.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException($"The 3MF model has an invalid transform \"{text}\".");
        return new Matrix4x4(
            values[0], values[1], values[2], 0,
            values[3], values[4], values[5], 0,
            values[6], values[7], values[8], 0,
            values[9], values[10], values[11], 1);
    }

    private static float Float(XmlReader reader, string name) =>
        float.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : throw new InvalidDataException($"The 3MF model has a {reader.LocalName} with an invalid \"{name}\".");

    private static int Int(XmlReader reader, string name) =>
        OptionalInt(reader, name) ?? throw new InvalidDataException($"The 3MF model has a {reader.LocalName} without a valid \"{name}\".");

    private static int? OptionalInt(XmlReader reader, string name) =>
        int.TryParse(reader.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Writes a 3MF package (millimetres, Z up) with one object per part.</summary>
    public static void Write(Model3D model, Stream output, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteText(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>
            """);
        WriteText(zip, "_rels/.rels", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="{ModelRelationship}"/></Relationships>
            """);

        var entry = zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        writer.WriteStartDocument();
        writer.WriteStartElement("model", CoreNs);
        writer.WriteAttributeString("unit", "millimeter");
        writer.WriteAttributeString("xml", "lang", null, "en-US");
        var anyColors = model.Parts.Any(p => p.Colors is not null);
        if (anyColors)
            writer.WriteAttributeString("xmlns", "m", null, MaterialNs);
        writer.WriteStartElement("metadata", CoreNs);
        writer.WriteAttributeString("name", "Application");
        writer.WriteString("Filee");
        writer.WriteEndElement();

        writer.WriteStartElement("resources", CoreNs);
        var nextId = 1;
        var baseMaterialsId = 0;
        if (model.Materials.Count > 0)
        {
            baseMaterialsId = nextId++;
            writer.WriteStartElement("basematerials", CoreNs);
            writer.WriteAttributeString("id", Text(baseMaterialsId));
            for (var m = 0; m < model.Materials.Count; m++)
            {
                writer.WriteStartElement("base", CoreNs);
                writer.WriteAttributeString("name", model.Materials[m].Name.Length > 0 ? model.Materials[m].Name : $"Material {m + 1}");
                writer.WriteAttributeString("displaycolor", MeshMath.ToHex(MeshMath.LinearToSrgb(model.Materials[m].BaseColor)));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        var objectIds = new List<int>();
        foreach (var part in model.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Welded by position: split vertices (for normals or UVs) would leave the mesh open.
            var weld = new Dictionary<Vector3, int>();
            var vertexOf = new int[part.VertexCount];
            var positions = new List<Vector3>();
            for (var v = 0; v < part.VertexCount; v++)
            {
                var p = MeshMath.ToZUp(part.Position(v)) + Vector3.Zero;
                if (!weld.TryGetValue(p, out var index))
                {
                    index = positions.Count;
                    positions.Add(p);
                    weld[p] = index;
                }
                vertexOf[v] = index;
            }

            var colorGroupId = 0;
            int[]? colorOf = null;
            if (part.Colors is not null)
            {
                colorGroupId = nextId++;
                var unique = new Dictionary<uint, int>();
                colorOf = new int[part.VertexCount];
                writer.WriteStartElement("m", "colorgroup", MaterialNs);
                writer.WriteAttributeString("id", Text(colorGroupId));
                for (var v = 0; v < part.VertexCount; v++)
                {
                    var c = part.Color(v);
                    var key = (uint)MeshMath.ToByte(c.X) << 24 | (uint)MeshMath.ToByte(c.Y) << 16 | (uint)MeshMath.ToByte(c.Z) << 8 | MeshMath.ToByte(c.W);
                    if (!unique.TryGetValue(key, out var index))
                    {
                        index = unique.Count;
                        unique[key] = index;
                        writer.WriteStartElement("m", "color", MaterialNs);
                        writer.WriteAttributeString("color", MeshMath.ToHex(c));
                        writer.WriteEndElement();
                    }
                    colorOf[v] = index;
                }
                writer.WriteEndElement();
            }

            var objectId = nextId++;
            objectIds.Add(objectId);
            writer.WriteStartElement("object", CoreNs);
            writer.WriteAttributeString("id", Text(objectId));
            writer.WriteAttributeString("type", "model");
            if (part.Name.Length > 0)
                writer.WriteAttributeString("name", part.Name);
            if (colorGroupId > 0)
            {
                writer.WriteAttributeString("pid", Text(colorGroupId));
                writer.WriteAttributeString("pindex", "0");
            }
            else if (part.Material >= 0 && part.Material < model.Materials.Count)
            {
                writer.WriteAttributeString("pid", Text(baseMaterialsId));
                writer.WriteAttributeString("pindex", Text(part.Material));
            }
            writer.WriteStartElement("mesh", CoreNs);
            writer.WriteStartElement("vertices", CoreNs);
            foreach (var p in positions)
            {
                writer.WriteStartElement("vertex", CoreNs);
                writer.WriteAttributeString("x", Text(p.X));
                writer.WriteAttributeString("y", Text(p.Y));
                writer.WriteAttributeString("z", Text(p.Z));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("triangles", CoreNs);
            var indices = part.IndexSpan;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = vertexOf[indices[i]], b = vertexOf[indices[i + 1]], c = vertexOf[indices[i + 2]];
                if (a == b || b == c || a == c)
                    continue; // collapsed by welding
                writer.WriteStartElement("triangle", CoreNs);
                writer.WriteAttributeString("v1", Text(a));
                writer.WriteAttributeString("v2", Text(b));
                writer.WriteAttributeString("v3", Text(c));
                if (colorOf is not null)
                {
                    writer.WriteAttributeString("pid", Text(colorGroupId));
                    writer.WriteAttributeString("p1", Text(colorOf[indices[i]]));
                    writer.WriteAttributeString("p2", Text(colorOf[indices[i + 1]]));
                    writer.WriteAttributeString("p3", Text(colorOf[indices[i + 2]]));
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("build", CoreNs);
        foreach (var id in objectIds)
        {
            writer.WriteStartElement("item", CoreNs);
            writer.WriteAttributeString("objectid", Text(id));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(float value) => (float.IsFinite(value) ? value : 0).ToString(CultureInfo.InvariantCulture);

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}
