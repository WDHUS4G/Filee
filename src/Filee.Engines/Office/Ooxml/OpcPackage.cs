// Minimal reader for Office Open XML packages (.xlsx, .pptx): parts as XML, relationships resolved to zip paths.

using System.IO.Compression;
using System.Xml.Linq;

namespace Filee.Engines.Office.Ooxml;

/// <summary>A relationship of a package part; <see cref="Target"/> is a zip path unless it is external.</summary>
internal readonly record struct OpcRelationship(string Type, string Target, bool External);

/// <summary>An open OPC zip package. Parts and relationships are cached.</summary>
internal sealed class OpcPackage : IDisposable
{
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, XDocument?> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, OpcRelationship>> _relationships = new(StringComparer.OrdinalIgnoreCase);

    public OpcPackage(string path) => _zip = ZipFile.OpenRead(path);

    /// <summary>The main part (target of the package's officeDocument relationship), e.g. xl/workbook.xml.</summary>
    public string? MainPartPath =>
        Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target;

    /// <summary>A part as XML (whitespace preserved), or null when it doesn't exist.</summary>
    public XDocument? Part(string path)
    {
        if (_parts.TryGetValue(path, out var cached))
            return cached;
        XDocument? xml = null;
        if (_zip.GetEntry(path) is { } entry)
        {
            using var stream = entry.Open();
            xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        _parts[path] = xml;
        return xml;
    }

    /// <summary>Relationships of a part ("" = the package), by id.</summary>
    public Dictionary<string, OpcRelationship> Relationships(string partPath)
    {
        if (_relationships.TryGetValue(partPath, out var cached))
            return cached;
        var folder = Folder(partPath);
        var relsPath = partPath.Length == 0 ? "_rels/.rels" : $"{folder}/_rels/{partPath[(partPath.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, OpcRelationship>();
        if (_zip.GetEntry(relsPath) is { } entry)
        {
            using var stream = entry.Open();
            foreach (var rel in XDocument.Load(stream).Root?.Elements(PackageRelationships + "Relationship") ?? [])
            {
                var target = (string?)rel.Attribute("Target") ?? "";
                var external = (string?)rel.Attribute("TargetMode") == "External";
                result[(string?)rel.Attribute("Id") ?? ""] = new OpcRelationship(
                    (string?)rel.Attribute("Type") ?? "", external ? target : ResolvePath(folder, target), external);
            }
        }
        _relationships[partPath] = result;
        return result;
    }

    /// <summary>Zip path of the part that relationship <paramref name="id"/> of <paramref name="partPath"/> points to.</summary>
    public string? Target(string partPath, string? id) =>
        id is not null && Relationships(partPath).TryGetValue(id, out var rel) && !rel.External ? rel.Target : null;

    /// <summary>The first relationship target of a type (matched on the end of the type URI, e.g. "/theme").</summary>
    public string? TargetOfType(string partPath, string typeSuffix) =>
        Relationships(partPath).Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)).Target;

    /// <summary>Copies a binary part (a picture) to <paramref name="folder"/>; returns the file path, or null.</summary>
    public string? Extract(string partPath, string folder)
    {
        if (_zip.GetEntry(partPath) is not { } entry)
            return null;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(partPath)}");
        entry.ExtractToFile(file);
        return file;
    }

    public void Dispose() => _zip.Dispose();

    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string ResolvePath(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in Uri.UnescapeDataString(target).Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                parts.Add(segment);
            }
        }
        return string.Join('/', parts);
    }
}
