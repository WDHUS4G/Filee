// Font preflight for ZIP-based office documents. Renderers otherwise substitute absent families silently, which can
// change wrapping, pagination and table geometry. The scan is deliberately read-only and bounded; a malformed or
// unusually large XML part never prevents conversion.

using System.IO.Compression;
using System.Xml;
using Filee.Engines.Hwp;

namespace Filee.Engines.Office;

internal static class DocumentFonts
{
    private const long MaxPartBytes = 16 * 1024 * 1024;
    private const long MaxTotalBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "sans serif", "monospace", "cursive", "fantasy", "system-ui", "emoji", "math",
    };

    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".hwpx", ".zip",
    };

    // These names designate symbol glyph sets for bullets, not missing document typefaces.
    private static readonly HashSet<string> SymbolFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Symbol", "Wingdings", "Wingdings 2", "Wingdings 3", "Webdings", "OpenSymbol",
    };

    /// <summary>Substitutes designed to preserve the original family's metrics closely enough not to warn.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> MetricAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] = ["Liberation Sans"],
            ["Calibri"] = ["Carlito"],
            ["Calibri Light"] = ["Carlito"],
            ["Cambria"] = ["Caladea"],
            ["Courier New"] = ["Liberation Mono"],
            ["Times New Roman"] = ["Liberation Serif"],
        };

    public static IReadOnlyList<string> Missing(string path) => Missing(path, Installed);

    internal static IReadOnlyList<string> Missing(string path, Func<string, bool> installed)
    {
        try
        {
            if (!PackageExtensions.Contains(Path.GetExtension(path)))
                return [];
            var referenced = Referenced(path);
            return referenced.Where(face => !installed(face)
                    && (!MetricAliases.TryGetValue(face, out var aliases) || !aliases.Any(installed)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static bool Installed(string face)
    {
        // The user may have installed a font since an earlier conversion in this Filee session.
        RhwpFonts.Forget(face);
        return RhwpFonts.Find(face) is { Usable: true };
    }

    internal static IReadOnlyList<string> Referenced(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var embedded = EmbeddedFonts(zip);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries.Where(IsDocumentXml))
        {
            if (entry.Length > MaxPartBytes || (total += entry.Length) > MaxTotalBytes)
                continue;
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, SafeXml());
            var ancestors = new List<string>();
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                while (ancestors.Count > reader.Depth)
                    ancestors.RemoveAt(ancestors.Count - 1);
                var parent = ancestors.Count == reader.Depth && ancestors.Count > 0 ? ancestors[^1] : null;
                switch (reader.LocalName)
                {
                    // WordprocessingML run fonts.
                    case "rFonts":
                        AddAttributes(reader, result, "ascii", "hAnsi", "eastAsia", "cs");
                        break;
                    // DrawingML fonts used by PowerPoint and by drawing/chart parts in every OOXML format.
                    case "latin" or "ea" or "cs":
                        Add(Attribute(reader, "typeface"), result);
                        break;
                    // SpreadsheetML: <font><name val="Aptos"/></font>.
                    case "name" when parent == "font":
                        Add(Attribute(reader, "val"), result);
                        break;
                    // HWPX header fonts: <hh:font face="맑은 고딕" .../>.
                    case "font":
                        Add(Attribute(reader, "face"), result);
                        break;
                }
                ancestors.Add(reader.LocalName);
            }
        }
        result.ExceptWith(embedded);
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> EmbeddedFonts(ZipArchive zip)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("word/fontTable.xml");
        if (entry is null || entry.Length > MaxPartBytes)
            return result;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, SafeXml());
        string? face = null;
        var fontDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "font")
            {
                face = reader.IsEmptyElement ? null : Attribute(reader, "name");
                fontDepth = reader.IsEmptyElement ? -1 : reader.Depth;
            }
            else if (reader.NodeType == XmlNodeType.Element && fontDepth >= 0 && reader.Depth > fontDepth
                     && reader.LocalName.StartsWith("embed", StringComparison.OrdinalIgnoreCase))
                Add(face, result);
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "font" && reader.Depth == fontDepth)
            {
                face = null;
                fontDepth = -1;
            }
        }
        return result;
    }

    private static bool IsDocumentXml(ZipArchiveEntry entry)
    {
        if (entry.Length == 0 || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = entry.FullName;
        if (path.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
        {
            var name = path["word/".Length..];
            return name.Equals("document.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("styles.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("numbering.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("footnotes.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("endnotes.xml", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("header", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("footer", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("theme/", StringComparison.OrdinalIgnoreCase);
        }
        return path.Equals("xl/styles.xml", StringComparison.OrdinalIgnoreCase)
            || path.Equals("Contents/header.xml", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/slideLayouts/slideLayout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/slideMasters/slideMaster", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/theme/", StringComparison.OrdinalIgnoreCase);
    }

    private static XmlReaderSettings SafeXml() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxPartBytes,
    };

    private static void AddAttributes(XmlReader reader, HashSet<string> result, params string[] names)
    {
        foreach (var name in names)
            Add(Attribute(reader, name), result);
    }

    private static string? Attribute(XmlReader reader, string name)
    {
        if (!reader.MoveToFirstAttribute())
            return null;
        do
        {
            if (reader.LocalName == name)
            {
                var value = reader.Value;
                reader.MoveToElement();
                return value;
            }
        } while (reader.MoveToNextAttribute());
        reader.MoveToElement();
        return null;
    }

    private static void Add(string? face, HashSet<string> result)
    {
        face = face?.Trim().Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(face) || face.StartsWith('+') || Generic.Contains(face) || SymbolFonts.Contains(face))
            return;
        result.Add(face);
    }
}
