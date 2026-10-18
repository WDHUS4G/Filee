// OPF package documents (EPUB 2 and 3, also the metadata.opf of HTMLZ and TXTZ): metadata, manifest and spine.
// Element names are matched by local name so that files from sloppy generators (missing or odd prefixes) still read.

using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Ebooks;

/// <summary>A manifest item: a file of the book.</summary>
/// <param name="Path">Full path of the extracted file.</param>
internal sealed record OpfItem(string Id, string Path, string MediaType, string Properties);

/// <summary>A parsed OPF package document.</summary>
internal sealed class OpfPackage
{
    public BookMetadata Metadata { get; } = new();
    public Dictionary<string, OpfItem> Manifest { get; } = new(StringComparer.Ordinal);

    /// <summary>Reading order (linear and non-linear items, as calibre does).</summary>
    public List<OpfItem> Spine { get; } = [];

    /// <summary>Reads an OPF file whose manifest paths are relative to its folder.</summary>
    public static OpfPackage Load(string opfPath)
    {
        var package = new OpfPackage();
        var document = LoadXml(opfPath);
        var root = document.Root ?? throw new InvalidDataException("The package document is empty.");
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(opfPath))!;

        foreach (var item in Elements(root, "manifest").SelectMany(m => Elements(m, "item")))
        {
            var id = (string?)item.Attribute("id");
            var href = (string?)item.Attribute("href");
            if (id is null || href is null)
                continue;
            var path = EbookFiles.SafePath(folder, Uri.UnescapeDataString(href.Split('#')[0]));
            if (path is not null)
                package.Manifest.TryAdd(id, new OpfItem(id, path, ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant(), (string?)item.Attribute("properties") ?? ""));
        }
        foreach (var itemRef in Elements(root, "spine").SelectMany(s => Elements(s, "itemref")))
        {
            if ((string?)itemRef.Attribute("idref") is { } idRef && package.Manifest.TryGetValue(idRef, out var item))
                package.Spine.Add(item);
        }

        var metadata = Elements(root, "metadata").FirstOrDefault();
        if (metadata is not null)
            ReadMetadata(metadata, package);
        return package;
    }

    private static void ReadMetadata(XElement metadata, OpfPackage package)
    {
        // EPUB 2 puts dc: elements in <dc-metadata>; search descendants.
        string? First(string name) => metadata.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Value.Trim().Length > 0)?.Value.Trim();
        var meta = package.Metadata;
        meta.Title = First("title");
        meta.Language = First("language");
        meta.Description = First("description");
        meta.Publisher = First("publisher");
        foreach (var creator in metadata.Descendants().Where(e => e.Name.LocalName == "creator"))
        {
            var name = creator.Value.Trim();
            var role = creator.Attributes().FirstOrDefault(a => a.Name.LocalName == "role")?.Value;
            if (name.Length > 0 && (role is null or "aut") && !meta.Authors.Contains(name))
                meta.Authors.Add(name);
        }

        // Cover: EPUB 3 "cover-image" property, else EPUB 2 <meta name="cover" content="item id">.
        var cover = package.Manifest.Values.FirstOrDefault(i => i.Properties.Split(' ').Contains("cover-image"));
        if (cover is null
            && metadata.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover") is { } coverMeta
            && (string?)coverMeta.Attribute("content") is { } coverId)
            package.Manifest.TryGetValue(coverId, out cover);
        if (cover is not null && cover.MediaType.StartsWith("image/", StringComparison.Ordinal) && File.Exists(cover.Path))
            meta.CoverImage = cover.Path;
    }

    /// <summary>The first rootfile named by META-INF/container.xml.</summary>
    public static string RootFile(string bookFolder)
    {
        var container = System.IO.Path.Combine(bookFolder, "META-INF", "container.xml");
        if (File.Exists(container))
        {
            var rootFile = LoadXml(container).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile"
                && ((string?)e.Attribute("media-type") ?? "application/oebps-package+xml") == "application/oebps-package+xml");
            if ((string?)rootFile?.Attribute("full-path") is { } fullPath && EbookFiles.SafePath(bookFolder, Uri.UnescapeDataString(fullPath)) is { } path && File.Exists(path))
                return path;
        }
        // Broken container: use the first package document in the book.
        return Directory.EnumerateFiles(bookFolder, "*.opf", SearchOption.AllDirectories).FirstOrDefault()
               ?? throw new InvalidDataException("This is not a valid EPUB file: it has no package document (content.opf).");
    }

    /// <summary>
    /// Loads XML without resolving external DTDs (no network access, no XXE); entity declarations inside the file
    /// are still allowed because some generators declare &amp;nbsp; and friends.
    /// </summary>
    public static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    private static IEnumerable<XElement> Elements(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    /// <summary>A minimal OPF with Dublin Core metadata (HTMLZ and TXTZ keep their metadata this way).</summary>
    public static string MetadataOnly(BookMetadata metadata, string title, string language)
    {
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var meta = new XElement(opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
            new XElement(dc + "title", title),
            new XElement(dc + "language", language),
            new XElement(dc + "identifier", new XAttribute("id", "uuid_id"), $"urn:uuid:{Guid.NewGuid()}"));
        foreach (var author in metadata.Authors)
            meta.Add(new XElement(dc + "creator", new XAttribute(opf + "role", "aut"), author));
        if (metadata.Publisher is { } publisher)
            meta.Add(new XElement(dc + "publisher", publisher));
        if (metadata.Description is { } description)
            meta.Add(new XElement(dc + "description", description));
        var package = new XElement(opf + "package", new XAttribute("version", "2.0"), new XAttribute("unique-identifier", "uuid_id"), meta);
        return new XDeclaration("1.0", "utf-8", null) + "\n" + package;
    }
}
