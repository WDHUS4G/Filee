// The pictures of a Markdown file Filee writes (e-books, PDF, HWP → Markdown): a Markdown file can't hold pictures,
// so they go into a folder next to it ("report.md" → "report_files/img0001.jpg") and are linked relatively, which
// every Markdown editor shows and which keeps working when both are moved together.

using Filee.Core.Conversion;
using Filee.Engines.Ebooks;

namespace Filee.Engines.Text;

/// <summary>Writes the pictures of one Markdown output into its "_files" folder and hands out their links.</summary>
internal sealed class MarkdownPictures
{
    /// <summary>Name suffix of the picture folder: "report.md" → "report_files".</summary>
    public const string FolderSuffix = "_files";

    private readonly string _markdown;
    private readonly IOutputAllocator _output;
    private readonly string _convertFolder;
    private readonly Dictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);
    private string? _folder;
    private bool _skipped;

    /// <param name="markdown">The Markdown file being written (already allocated).</param>
    /// <param name="output">The allocator that handed it out: the folder is reserved through it, like any output.</param>
    /// <param name="workDirectory">Scratch space for pictures converted to a common format.</param>
    public MarkdownPictures(string markdown, IOutputAllocator output, string workDirectory)
    {
        _markdown = markdown;
        _output = output;
        _convertFolder = Path.Combine(workDirectory, "md-pictures-" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>
    /// Copies the picture at <paramref name="path"/> into the folder (formats editors don't show, such as BMP or
    /// WebP, as PNG or JPEG) and returns its relative link, or null when it can't be read or the folder is skipped.
    /// The same picture used twice is written once.
    /// </summary>
    public string? Add(string path)
    {
        if (_links.TryGetValue(path, out var known))
            return known;
        if (EbookFiles.CommonImage(path, _convertFolder, gifAndSvg: true) is not ({ } file, _) || Folder() is not { } folder)
            return null;
        var name = $"img{_links.Count + 1:0000}{Path.GetExtension(file).ToLowerInvariant()}";
        File.Copy(file, Path.Combine(folder, name), overwrite: true);
        return _links[path] = $"{Uri.EscapeDataString(Path.GetFileName(folder))}/{name}";
    }

    /// <summary>
    /// The folder, created on the first picture: reserved through the allocator ("report_files", "report_files (2)"
    /// or skipped, as the preset's conflict rule says). An intermediate Markdown file (a step of a longer route) keeps
    /// it right beside itself, so the next step finds the pictures.
    /// </summary>
    private string? Folder()
    {
        if (_folder is not null || _skipped)
            return _folder;
        var beside = Path.GetDirectoryName(Path.GetFullPath(_markdown))!;
        var folder = _output.Allocate("", FolderSuffix);
        if (folder is not null && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), beside, StringComparison.OrdinalIgnoreCase))
            folder = Path.Combine(beside, Path.GetFileNameWithoutExtension(_markdown) + FolderSuffix);
        if (folder is null)
        {
            _skipped = true;
            return null;
        }
        Directory.CreateDirectory(folder);
        return _folder = folder;
    }
}
