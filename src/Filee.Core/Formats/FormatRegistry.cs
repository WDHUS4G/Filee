// Central list of supported formats and extension lookup.
// To add a new format: add one line to Known and give at least one converter an edge for it.

namespace Filee.Core.Formats;

/// <summary>Lookup table of every format Filee knows about.</summary>
public static class FormatRegistry
{
    /// <summary>Pseudo format of the "Extract" preset: the output is a folder with the archive's files.</summary>
    public const string Folder = "folder";

    /// <summary>All known formats. Order is the order shown in format pickers.</summary>
    public static IReadOnlyList<FileFormat> Known { get; } =
    [
        // Raster images
        new("png", "PNG", FormatCategory.Image, ["png"]),
        new("jpg", "JPG", FormatCategory.Image, ["jpg", "jpeg", "jpe", "jfif"]),
        new("webp", "WEBP", FormatCategory.Image, ["webp"]),
        new("tiff", "TIFF", FormatCategory.Image, ["tiff", "tif"]),
        new("bmp", "BMP", FormatCategory.Image, ["bmp", "dib"]),
        new("gif", "GIF", FormatCategory.Image, ["gif"]),
        new("ico", "ICO", FormatCategory.Image, ["ico"]),
        new("avif", "AVIF", FormatCategory.Image, ["avif"]),
        new("heic", "HEIC", FormatCategory.Image, ["heic", "heif", "hif"]),
        new("jxl", "JPEG XL", FormatCategory.Image, ["jxl"]),
        new("jp2", "JPEG 2000", FormatCategory.Image, ["jp2", "j2k", "jpf", "jpx"]),
        new("psd", "PSD", FormatCategory.Image, ["psd"]),
        new("psb", "PSB", FormatCategory.Image, ["psb"]),
        new("tga", "TGA", FormatCategory.Image, ["tga", "icb", "vda", "vst"]),
        new("ppm", "PPM", FormatCategory.Image, ["ppm", "pgm", "pbm", "pnm"]),
        new("icns", "ICNS", FormatCategory.Image, ["icns"]),
        new("apng", "APNG", FormatCategory.Image, ["apng"]),
        new("mng", "MNG", FormatCategory.Image, ["mng"]),
        new("qoi", "QOI", FormatCategory.Image, ["qoi"]),
        new("dds", "DDS", FormatCategory.Image, ["dds"]),
        new("pcx", "PCX", FormatCategory.Image, ["pcx"]),
        new("cur", "CUR", FormatCategory.Image, ["cur"]),
        new("xpm", "XPM", FormatCategory.Image, ["xpm"]),
        new("xbm", "XBM", FormatCategory.Image, ["xbm"]),
        new("wbmp", "WBMP", FormatCategory.Image, ["wbmp"]),
        new("pam", "PAM", FormatCategory.Image, ["pam"]),
        new("pfm", "PFM", FormatCategory.Image, ["pfm"]),
        new("sgi", "SGI", FormatCategory.Image, ["sgi", "rgb", "rgba", "bw"]),
        new("ras", "Sun Raster", FormatCategory.Image, ["ras", "sun"]),
        new("miff", "MIFF", FormatCategory.Image, ["miff"]),
        new("dcm", "DICOM", FormatCategory.Image, ["dcm", "dicom"], Writable: false),
        new("pcd", "Photo CD", FormatCategory.Image, ["pcd"], Writable: false),
        new("exr", "OpenEXR", FormatCategory.Image, ["exr"], Writable: false),
        new("hdr", "Radiance HDR", FormatCategory.Image, ["hdr"], Writable: false),
        new("xcf", "XCF", FormatCategory.Image, ["xcf"], Writable: false),
        new("raw", "RAW", FormatCategory.Image,
            ["dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "orf", "rw2", "raf", "pef", "dcr", "kdc",
             "erf", "mos", "mrw", "x3f", "3fr", "fff", "iiq", "mef", "rwl", "srw", "raw"], Writable: false),

        // Vector graphics
        new("svg", "SVG", FormatCategory.Vector, ["svg"]),
        new("svgz", "SVGZ", FormatCategory.Vector, ["svgz"]),
        new("eps", "EPS", FormatCategory.Vector, ["eps", "epsf", "epsi"]),
        new("ps", "PS", FormatCategory.Vector, ["ps"]),
        new("ai", "AI", FormatCategory.Vector, ["ai"], Writable: false),
        new("emf", "EMF", FormatCategory.Vector, ["emf"]),
        new("wmf", "WMF", FormatCategory.Vector, ["wmf"]),
        new("cdr", "CDR", FormatCategory.Vector, ["cdr"], Writable: false),
        new("cgm", "CGM", FormatCategory.Vector, ["cgm"], Writable: false),
        new("vsd", "VSD", FormatCategory.Vector, ["vsd", "vsdx"], Writable: false),
        new("odg", "ODG", FormatCategory.Vector, ["odg"]),
        new("fodg", "FODG", FormatCategory.Vector, ["fodg"], Writable: false),

        // PDF
        new("pdf", "PDF", FormatCategory.Pdf, ["pdf"]),

        // Word processing
        new("docx", "DOCX", FormatCategory.Document, ["docx"]),
        new("docm", "DOCM", FormatCategory.Document, ["docm"]),
        new("dotx", "DOTX", FormatCategory.Document, ["dotx"]),
        new("dotm", "DOTM", FormatCategory.Document, ["dotm"]),
        new("doc", "DOC", FormatCategory.Document, ["doc"]),
        new("dot", "DOT", FormatCategory.Document, ["dot"], Writable: false),
        new("odt", "ODT", FormatCategory.Document, ["odt"]),
        new("fodt", "FODT", FormatCategory.Document, ["fodt"]),
        new("ott", "OTT", FormatCategory.Document, ["ott"]),
        new("rtf", "RTF", FormatCategory.Document, ["rtf"]),
        new("wps", "WPS", FormatCategory.Document, ["wps"], Writable: false),
        new("wpd", "WPD", FormatCategory.Document, ["wpd"], Writable: false),
        new("lwp", "LWP", FormatCategory.Document, ["lwp"], Writable: false),
        new("abw", "ABW", FormatCategory.Document, ["abw", "zabw"], Writable: false),
        new("pages", "Pages", FormatCategory.Document, ["pages"], Writable: false),
        new("sdw", "SDW", FormatCategory.Document, ["sdw"], Writable: false),
        new("cwk", "ClarisWorks", FormatCategory.Document, ["cwk"], Writable: false),
        new("mcw", "MacWrite", FormatCategory.Document, ["mcw"], Writable: false),
        new("602", "T602", FormatCategory.Document, ["602"], Writable: false),
        new("psw", "Pocket Word", FormatCategory.Document, ["psw"], Writable: false),
        new("pub", "PUB", FormatCategory.Document, ["pub"], Writable: false),
        new("eml", "EML", FormatCategory.Document, ["eml"]),
        new("msg", "MSG", FormatCategory.Document, ["msg"], Writable: false),

        // Korean word processor (Hancom)
        new("hwpx", "HWPX", FormatCategory.Hwp, ["hwpx"]),
        new("hwp", "HWP", FormatCategory.Hwp, ["hwp"]),

        // Spreadsheets
        new("xlsx", "XLSX", FormatCategory.Spreadsheet, ["xlsx"]),
        new("xlsm", "XLSM", FormatCategory.Spreadsheet, ["xlsm"]),
        new("xltx", "XLTX", FormatCategory.Spreadsheet, ["xltx"]),
        new("xls", "XLS", FormatCategory.Spreadsheet, ["xls"]),
        new("xlt", "XLT", FormatCategory.Spreadsheet, ["xlt"]),
        new("ods", "ODS", FormatCategory.Spreadsheet, ["ods"]),
        new("ots", "OTS", FormatCategory.Spreadsheet, ["ots"]),
        new("fods", "FODS", FormatCategory.Spreadsheet, ["fods"], Writable: false),
        new("csv", "CSV", FormatCategory.Spreadsheet, ["csv"]),
        new("tsv", "TSV", FormatCategory.Spreadsheet, ["tsv", "tab"]),
        new("et", "ET", FormatCategory.Spreadsheet, ["et"], Writable: false),
        new("numbers", "Numbers", FormatCategory.Spreadsheet, ["numbers"], Writable: false),
        new("sdc", "SDC", FormatCategory.Spreadsheet, ["sdc"], Writable: false),

        // Presentations
        new("pptx", "PPTX", FormatCategory.Presentation, ["pptx"]),
        new("pptm", "PPTM", FormatCategory.Presentation, ["pptm"]),
        new("potx", "POTX", FormatCategory.Presentation, ["potx"]),
        new("ppsx", "PPSX", FormatCategory.Presentation, ["ppsx"]),
        new("ppt", "PPT", FormatCategory.Presentation, ["ppt", "pps", "pot"]),
        new("odp", "ODP", FormatCategory.Presentation, ["odp"]),
        new("otp", "OTP", FormatCategory.Presentation, ["otp"]),
        new("fodp", "FODP", FormatCategory.Presentation, ["fodp"], Writable: false),
        new("key", "Keynote", FormatCategory.Presentation, ["key"], Writable: false),
        new("dps", "DPS", FormatCategory.Presentation, ["dps"], Writable: false),
        new("sda", "SDA", FormatCategory.Presentation, ["sda", "sdd"], Writable: false),

        // Plain text / markup
        new("txt", "TXT", FormatCategory.Text, ["txt"]),
        new("md", "Markdown", FormatCategory.Text, ["md", "markdown"]),
        new("html", "HTML", FormatCategory.Text, ["html", "htm", "xhtml"]),
        new("rst", "reStructuredText", FormatCategory.Text, ["rst"]),
        new("tex", "LaTeX", FormatCategory.Text, ["tex", "latex"]),
        // Read and written by Pandoc. Man pages claim only ".man": the section numbers (.1 … .9) are also the
        // extensions of rotated logs ("app.log.1"). DocBook claims ".dbk", not every ".xml".
        new("org", "Org", FormatCategory.Text, ["org"]),
        new("adoc", "AsciiDoc", FormatCategory.Text, ["adoc", "asciidoc"]),
        new("typ", "Typst", FormatCategory.Text, ["typ"]),
        new("mediawiki", "MediaWiki", FormatCategory.Text, ["mediawiki", "wiki"]),
        new("dbk", "DocBook", FormatCategory.Text, ["dbk", "docbook"]),
        new("ipynb", "Jupyter", FormatCategory.Text, ["ipynb"]),
        new("textile", "Textile", FormatCategory.Text, ["textile"]),
        new("man", "Man page", FormatCategory.Text, ["man"]),
        new("opml", "OPML", FormatCategory.Text, ["opml"]),

        // Structured data. CSV stays a spreadsheet (its donut and routes); the data engine adds CSV ↔ JSON / YAML /
        // TOML / XML edges. ".xml" means a data document here: XML-based formats Filee converts have their own
        // extensions (.svg, .fb2, .dbk, ...).
        new("json", "JSON", FormatCategory.Data, ["json"]),
        new("jsonl", "JSON Lines", FormatCategory.Data, ["jsonl", "ndjson"]),
        new("yaml", "YAML", FormatCategory.Data, ["yaml", "yml"]),
        new("toml", "TOML", FormatCategory.Data, ["toml"]),
        new("xml", "XML", FormatCategory.Data, ["xml"]),
        new("vcf", "vCard", FormatCategory.Data, ["vcf", "vcard"]),

        // E-books
        new("epub", "EPUB", FormatCategory.Ebook, ["epub"]),
        new("kepub", "KEPUB", FormatCategory.Ebook, ["kepub.epub", "kepub"]),
        new("mobi", "MOBI", FormatCategory.Ebook, ["mobi"]),
        new("azw3", "AZW3", FormatCategory.Ebook, ["azw3"]),
        new("azw", "AZW", FormatCategory.Ebook, ["azw"], Writable: false),
        new("azw4", "AZW4", FormatCategory.Ebook, ["azw4"], Writable: false),
        new("prc", "PRC", FormatCategory.Ebook, ["prc"], Writable: false),
        new("fb2", "FB2", FormatCategory.Ebook, ["fb2"]),
        new("cbz", "CBZ", FormatCategory.Ebook, ["cbz"]),
        new("cbr", "CBR", FormatCategory.Ebook, ["cbr"], Writable: false),
        new("cb7", "CB7", FormatCategory.Ebook, ["cb7"], Writable: false),
        new("cbt", "CBT", FormatCategory.Ebook, ["cbt"], Writable: false),
        new("cbc", "CBC", FormatCategory.Ebook, ["cbc"], Writable: false),
        new("htmlz", "HTMLZ", FormatCategory.Ebook, ["htmlz"]),
        new("txtz", "TXTZ", FormatCategory.Ebook, ["txtz"]),
        new("chm", "CHM", FormatCategory.Ebook, ["chm"], Writable: false),
        new("lit", "LIT", FormatCategory.Ebook, ["lit"]),
        new("lrf", "LRF", FormatCategory.Ebook, ["lrf"]),
        new("pdb", "PDB", FormatCategory.Ebook, ["pdb"]),
        new("pml", "PML", FormatCategory.Ebook, ["pml"]),
        new("rb", "RB", FormatCategory.Ebook, ["rb"]),
        new("snb", "SNB", FormatCategory.Ebook, ["snb"]),
        new("tcr", "TCR", FormatCategory.Ebook, ["tcr"]),
        new("oeb", "OEB", FormatCategory.Ebook, ["oeb", "opf"], Writable: false),

        // Video
        new("mp4", "MP4", FormatCategory.Video, ["mp4"]),
        new("m4v", "M4V", FormatCategory.Video, ["m4v"]),
        new("mov", "MOV", FormatCategory.Video, ["mov", "qt"]),
        new("mkv", "MKV", FormatCategory.Video, ["mkv"]),
        new("webm", "WEBM", FormatCategory.Video, ["webm"]),
        new("avi", "AVI", FormatCategory.Video, ["avi", "divx"]),
        new("wmv", "WMV", FormatCategory.Video, ["wmv", "asf"]),
        new("flv", "FLV", FormatCategory.Video, ["flv", "f4v"]),
        new("mpg", "MPEG", FormatCategory.Video, ["mpg", "mpeg", "mpe", "m1v", "m2v"]),
        new("ts", "TS", FormatCategory.Video, ["ts"]),
        new("m2ts", "M2TS", FormatCategory.Video, ["m2ts", "mts", "m2t"]),
        new("3gp", "3GP", FormatCategory.Video, ["3gp", "3gpp"]),
        new("3g2", "3G2", FormatCategory.Video, ["3g2"]),
        new("ogv", "OGV", FormatCategory.Video, ["ogv"]),
        new("vob", "VOB", FormatCategory.Video, ["vob"]),
        new("mod", "MOD", FormatCategory.Video, ["mod", "tod"], Writable: false),
        new("dv", "DV", FormatCategory.Video, ["dv", "dif"]),
        new("mxf", "MXF", FormatCategory.Video, ["mxf"]),
        new("rm", "RM", FormatCategory.Video, ["rm", "rmvb"], Writable: false),
        new("swf", "SWF", FormatCategory.Video, ["swf"], Writable: false),
        new("wtv", "WTV", FormatCategory.Video, ["wtv", "dvr-ms", "dvr"], Writable: false),
        new("cavs", "CAVS", FormatCategory.Video, ["cavs"], Writable: false),

        // Audio
        new("mp3", "MP3", FormatCategory.Audio, ["mp3"]),
        new("m4a", "M4A", FormatCategory.Audio, ["m4a"]),
        new("m4b", "M4B", FormatCategory.Audio, ["m4b"]),
        new("aac", "AAC", FormatCategory.Audio, ["aac"]),
        new("wav", "WAV", FormatCategory.Audio, ["wav", "wave"]),
        new("flac", "FLAC", FormatCategory.Audio, ["flac"]),
        new("ogg", "OGG", FormatCategory.Audio, ["ogg", "oga"]),
        new("opus", "OPUS", FormatCategory.Audio, ["opus"]),
        new("wma", "WMA", FormatCategory.Audio, ["wma"]),
        new("aiff", "AIFF", FormatCategory.Audio, ["aiff", "aif", "aifc"]),
        new("ac3", "AC3", FormatCategory.Audio, ["ac3"]),
        new("amr", "AMR", FormatCategory.Audio, ["amr"]),
        new("au", "AU", FormatCategory.Audio, ["au", "snd"]),
        new("caf", "CAF", FormatCategory.Audio, ["caf"]),
        new("weba", "WEBA", FormatCategory.Audio, ["weba"]),
        new("mka", "MKA", FormatCategory.Audio, ["mka"]),
        new("mp2", "MP2", FormatCategory.Audio, ["mp2", "mpa"]),
        new("dss", "DSS", FormatCategory.Audio, ["dss"], Writable: false),
        new("voc", "VOC", FormatCategory.Audio, ["voc"]),
        new("wv", "WavPack", FormatCategory.Audio, ["wv"]),
        new("tta", "TTA", FormatCategory.Audio, ["tta"]),
        new("w64", "W64", FormatCategory.Audio, ["w64"]),
        new("eac3", "E-AC3", FormatCategory.Audio, ["eac3", "ec3"]),
        new("ape", "APE", FormatCategory.Audio, ["ape"], Writable: false),
        new("mpc", "Musepack", FormatCategory.Audio, ["mpc", "mpp", "mp+"], Writable: false),
        new("tak", "TAK", FormatCategory.Audio, ["tak"], Writable: false),
        new("dsf", "DSF", FormatCategory.Audio, ["dsf"], Writable: false),
        new("dff", "DFF", FormatCategory.Audio, ["dff"], Writable: false),
        new("dts", "DTS", FormatCategory.Audio, ["dts"], Writable: false),
        new("spx", "Speex", FormatCategory.Audio, ["spx"], Writable: false),

        // Archives
        new("zip", "ZIP", FormatCategory.Archive, ["zip"]),
        new("7z", "7Z", FormatCategory.Archive, ["7z"]),
        new("rar", "RAR", FormatCategory.Archive, ["rar"], Writable: false),
        new("tar", "TAR", FormatCategory.Archive, ["tar"]),
        new("tgz", "TAR.GZ", FormatCategory.Archive, ["tar.gz", "tgz", "taz"]),
        new("tbz2", "TAR.BZ2", FormatCategory.Archive, ["tar.bz2", "tbz2", "tar.bz", "tbz"]),
        new("txz", "TAR.XZ", FormatCategory.Archive, ["tar.xz", "txz"]),
        new("tar7z", "TAR.7Z", FormatCategory.Archive, ["tar.7z"], Writable: false),
        new("tlz", "TAR.LZ", FormatCategory.Archive, ["tar.lz", "tlz"], Writable: false),
        new("tz", "TAR.Z", FormatCategory.Archive, ["tar.z", "tz"], Writable: false),
        new("gz", "GZ", FormatCategory.Archive, ["gz"]),
        new("bz2", "BZ2", FormatCategory.Archive, ["bz2", "bz"]),
        new("xz", "XZ", FormatCategory.Archive, ["xz"]),
        new("lz", "LZ", FormatCategory.Archive, ["lz"], Writable: false),
        new("lzma", "LZMA", FormatCategory.Archive, ["lzma"], Writable: false),
        new("z", "Z", FormatCategory.Archive, ["z"], Writable: false),
        new("jar", "JAR", FormatCategory.Archive, ["jar"], Writable: false),
        new("cab", "CAB", FormatCategory.Archive, ["cab"], Writable: false),
        new("cpio", "CPIO", FormatCategory.Archive, ["cpio"], Writable: false),
        new("deb", "DEB", FormatCategory.Archive, ["deb"], Writable: false),
        new("rpm", "RPM", FormatCategory.Archive, ["rpm"], Writable: false),
        new("dmg", "DMG", FormatCategory.Archive, ["dmg"], Writable: false),
        new("iso", "ISO", FormatCategory.Archive, ["iso"], Writable: false),
        new("img", "IMG", FormatCategory.Archive, ["img"], Writable: false),
        new("lha", "LHA", FormatCategory.Archive, ["lha", "lzh"], Writable: false),
        new("arj", "ARJ", FormatCategory.Archive, ["arj"], Writable: false),
        new("alz", "ALZ", FormatCategory.Archive, ["alz"], Writable: false),
        new("egg", "EGG", FormatCategory.Archive, ["egg"], Writable: false),
        new(Folder, "Folder", FormatCategory.Archive, [""]),

        // CAD
        new("dwg", "DWG", FormatCategory.Cad, ["dwg"]),
        new("dxf", "DXF", FormatCategory.Cad, ["dxf"]),

        // Fonts
        new("ttf", "TTF", FormatCategory.Font, ["ttf"]),
        new("otf", "OTF", FormatCategory.Font, ["otf"]),
        new("woff", "WOFF", FormatCategory.Font, ["woff"]),
        new("woff2", "WOFF2", FormatCategory.Font, ["woff2"]),
        new("eot", "EOT", FormatCategory.Font, ["eot"]),

        // Subtitles (text)
        new("srt", "SRT", FormatCategory.Subtitle, ["srt"]),
        new("ass", "ASS", FormatCategory.Subtitle, ["ass"]),
        new("ssa", "SSA", FormatCategory.Subtitle, ["ssa"], Writable: false),
        new("vtt", "WebVTT", FormatCategory.Subtitle, ["vtt"]),
        new("smi", "SAMI", FormatCategory.Subtitle, ["smi", "sami"]),
        new("lrc", "LRC", FormatCategory.Subtitle, ["lrc"]),
        new("ttml", "TTML", FormatCategory.Subtitle, ["ttml", "dfxp"]),
        new("sub", "MicroDVD", FormatCategory.Subtitle, ["sub"]),
        new("scc", "SCC", FormatCategory.Subtitle, ["scc"], Writable: false),

        // 3D models
        new("glb", "GLB", FormatCategory.Model3D, ["glb"]),
        new("gltf", "glTF", FormatCategory.Model3D, ["gltf"]),
        new("stl", "STL", FormatCategory.Model3D, ["stl"]),
        new("obj", "OBJ", FormatCategory.Model3D, ["obj"]),
        new("3mf", "3MF", FormatCategory.Model3D, ["3mf"]),
        new("ply", "PLY", FormatCategory.Model3D, ["ply"]),
    ];

    private static readonly Dictionary<string, FileFormat> ById =
        Known.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FileFormat> ByExtension =
        Known.SelectMany(f => f.Extensions.Where(e => e.Length > 0).Select(e => (e, f)))
             .ToDictionary(x => x.e, x => x.f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions made of two parts ("tar.gz"), checked before the last part alone.</summary>
    private static readonly string[] CompoundExtensions =
        ByExtension.Keys.Where(e => e.Contains('.')).OrderByDescending(e => e.Length).ToArray();

    /// <summary>Returns the format with the given id, or <c>null</c>.</summary>
    public static FileFormat? FindById(string id) => ById.GetValueOrDefault(id);

    /// <summary>Returns the format with the given id or throws if it is unknown.</summary>
    public static FileFormat Get(string id) =>
        FindById(id) ?? throw new KeyNotFoundException($"Unknown format id '{id}'.");

    /// <summary>Returns the format for an extension (with or without the leading dot), or <c>null</c>.</summary>
    public static FileFormat? FindByExtension(string extension) =>
        extension.TrimStart('.') is { Length: > 0 } ext ? ByExtension.GetValueOrDefault(ext) : null;

    /// <summary>Detects the format of a file from its extension, or <c>null</c> if it is not supported.</summary>
    public static FileFormat? Detect(string path) => FindByExtension(ExtensionOf(path));

    /// <summary>
    /// The extension of <paramref name="path"/> without the dot, including known two-part extensions
    /// ("archive.tar.gz" → "tar.gz", "photo.jpg" → "jpg").
    /// </summary>
    public static string ExtensionOf(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var compound in CompoundExtensions)
        {
            if (name.Length > compound.Length + 1
                && name.EndsWith("." + compound, StringComparison.OrdinalIgnoreCase))
                return name[^compound.Length..];
        }
        return Path.GetExtension(name).TrimStart('.');
    }

    /// <summary>The file name without its (possibly two-part) extension: "backup.tar.gz" → "backup".</summary>
    public static string NameWithoutExtension(string path)
    {
        var name = Path.GetFileName(path);
        var extension = ExtensionOf(name);
        return extension.Length == 0 ? name : name[..^(extension.Length + 1)];
    }
}
