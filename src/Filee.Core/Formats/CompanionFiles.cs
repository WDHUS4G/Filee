// Files that only work next to another file: a Shapefile is a .shp with its .shx, .dbf, .prj, .cpg and index and
// metadata files of the same name. They travel with the .shp (packed into an archive with it) and, when they are dropped
// together with it, they are part of it rather than files of their own (no donut for unknown files, no failures).

namespace Filee.Core.Formats;

/// <summary>Companion files of formats that are several files on disk.</summary>
public static class CompanionFiles
{
    /// <summary>
    /// What may follow a Shapefile's name ("roads" + "." + part), in the order they are listed: the parts the format
    /// needs, the code page, ESRI's and QGIS's spatial indexes and metadata.
    /// </summary>
    private static readonly string[] ShapefileParts =
        ["shx", "dbf", "prj", "cpg", "sbn", "sbx", "fbn", "fbx", "ain", "aih", "atx", "ixs", "mxs", "qix", "qmd", "qpj", "shp.xml"];

    /// <summary>The companion files of <paramref name="path"/> that exist next to it; none for a file of another format.</summary>
    public static IReadOnlyList<string> Of(string path)
    {
        if (!IsLocal(path) || !IsShapefile(path))
            return [];
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);
            var name = Path.GetFileNameWithoutExtension(full);
            if (directory is null || !Directory.Exists(directory))
                return [];
            // Any letter case: "ROADS.DBF" next to "roads.shp" on a case-sensitive file system.
            return Directory.EnumerateFiles(directory, name + ".*")
                .Select(file => (File: file, Part: PartOf(Path.GetFileName(file), name)))
                .Where(f => f.Part >= 0)
                .OrderBy(f => f.Part)
                .Select(f => f.File)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// <paramref name="paths"/> without the files that are companions of a Shapefile also in the list (a .dbf selected
    /// together with its .shp). A companion on its own stays: it is a file like any other.
    /// </summary>
    public static IReadOnlyList<string> WithoutCompanions(IReadOnlyList<string> paths)
    {
        // Only files on disk: a source that is still a web address (a file dragged from a browser) is left as it is.
        var shapefiles = paths.Where(p => IsLocal(p) && IsShapefile(p))
            .Select(p => Key(Path.GetDirectoryName(Path.GetFullPath(p)) ?? "", Path.GetFileNameWithoutExtension(p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (shapefiles.Count == 0)
            return paths;
        return paths.Where(p => !IsCompanionOfOneOf(p, shapefiles)).ToList();
    }

    private static bool IsLocal(string path) => !path.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(path);

    private static bool IsCompanionOfOneOf(string path, HashSet<string> shapefiles)
    {
        if (!IsLocal(path) || IsShapefile(path))
            return false;
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? "";
        var fileName = Path.GetFileName(full);
        // "roads.dbf" belongs to "roads"; "roads.shp.xml" also to "roads".
        foreach (var part in ShapefileParts)
        {
            if (fileName.Length > part.Length + 1 && fileName.EndsWith("." + part, StringComparison.OrdinalIgnoreCase)
                && shapefiles.Contains(Key(directory, fileName[..^(part.Length + 1)])))
                return true;
        }
        return false;
    }

    private static int PartOf(string fileName, string name)
    {
        if (fileName.Length <= name.Length + 1 || !fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            return -1;
        return Array.FindIndex(ShapefileParts, part => part.Equals(fileName[(name.Length + 1)..], StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsShapefile(string path) => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase);

    private static string Key(string directory, string name) => directory + Path.DirectorySeparatorChar + name;
}
