// The ZIP container of an OFD file, read defensively: parts are looked up by name inside the package and never
// written out under their own names, references ("../Res/a.png", "/Doc_0/Document.xml") are resolved against the
// package root and cannot leave it, XML is read without DTDs, and every part is read with a size limit (also checked
// while decompressing, so a lying header does not help a zip bomb).

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Ofd;

/// <summary>An OFD package opened for reading.</summary>
internal sealed class OfdPackage : IDisposable
{
    /// <summary>More parts than this are refused (real OFD files have a few per page).</summary>
    private const int MaxEntries = 100_000;

    /// <summary>Largest XML part read (a page, a resource list).</summary>
    private const long MaxXmlBytes = 64L * 1024 * 1024;

    /// <summary>Largest picture taken; bigger ones are left out.</summary>
    public const long MaxPictureBytes = 32L * 1024 * 1024;

    /// <summary>Everything read from one package together.</summary>
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = MaxXmlBytes,
    };

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxTotal;
    private long _read;

    public OfdPackage(string path) : this(() => ZipFile.OpenRead(path), MaxTotalBytes)
    {
    }

    /// <summary>A package held in memory (the seal picture inside a signature), with its own limit on what it unpacks to.</summary>
    public OfdPackage(byte[] bytes, long maxTotal) : this(() => new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read), maxTotal)
    {
    }

    private OfdPackage(Func<ZipArchive> open, long maxTotal)
    {
        _maxTotal = Math.Min(maxTotal, MaxTotalBytes);
        try
        {
            _zip = open();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The file is not an OFD document (it is not a ZIP package). It may be damaged.", ex);
        }
        if (_zip.Entries.Count > MaxEntries)
        {
            _zip.Dispose();
            throw new InvalidDataException($"The OFD package has more than {MaxEntries:N0} parts; Filee does not read it.");
        }
        foreach (var entry in _zip.Entries)
        {
            // Names that climb out of the package ("../x") are unreachable; of two parts with one name the first wins.
            if (!entry.FullName.EndsWith('/') && Resolve("", entry.FullName) is { } name)
                _entries.TryAdd(name, entry);
        }
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>True when the package has a part of that (resolved) name.</summary>
    public bool Has(string? name) => name is not null && _entries.ContainsKey(name);

    /// <summary>Names of all parts, resolved.</summary>
    public IEnumerable<string> Names => _entries.Keys;

    /// <summary>
    /// A reference inside the package as a part name: relative to <paramref name="baseDir"/>, or to the package root
    /// when it starts with "/". "." and ".." are applied; null when the reference is empty, leaves the package or is
    /// a URL or drive path.
    /// </summary>
    public static string? Resolve(string baseDir, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        reference = reference.Trim().Replace('\\', '/');
        if (reference.Contains(':'))
            return null;
        var parts = new List<string>();
        if (!reference.StartsWith('/'))
            parts.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in reference.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>The folder of a part name ("" at the root).</summary>
    public static string DirName(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash < 0 ? "" : name[..slash];
    }

    /// <summary>An XML part, or null when the package has no such part. Throws for parts that are not XML.</summary>
    public XDocument? LoadXml(string? name)
    {
        if (name is null || !_entries.ContainsKey(name))
            return null;
        var bytes = Read(name, MaxXmlBytes)
                    ?? throw new InvalidDataException($"The OFD part {name} is larger than {MaxXmlBytes / (1024 * 1024)} MB; Filee does not read it.");
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), XmlSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The OFD part {name} is not valid XML. The file may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>The bytes of a part; null when it is missing or larger than <paramref name="max"/>.</summary>
    public byte[]? Read(string name, long max)
    {
        if (!_entries.TryGetValue(name, out var entry) || entry.Length > max)
            return null;
        using var stream = entry.Open();
        using var copy = new MemoryStream((int)Math.Min(entry.Length, 1024 * 1024));
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (copy.Length + count > max)
                return null;
            _read += count;
            if (_read > _maxTotal)
                throw new InvalidDataException($"The OFD package unpacks to more than {_maxTotal / (1024 * 1024)} MB; Filee does not read it.");
            copy.Write(buffer, 0, count);
        }
        return copy.ToArray();
    }
}
