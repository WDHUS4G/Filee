// The files in a drag. Avalonia reads file URLs (what Explorer, Finder and Linux file managers offer); on macOS
// some other file managers offer the older NSFilenamesPboardType array of paths instead (QSpace Pro, issue #160),
// which Avalonia passes through as a platform format of its own. Pictures, links and downloads dragged out of a web
// browser are read by BrowserDrags and stand as placeholders until their conversion saves them.

using Avalonia.Input;
using Avalonia.Platform.Storage;
using Filee.Core.Platform;
using Filee.Core.Web;
using Filee.Platform.MacOS;

namespace Filee.App.Services;

/// <summary>What a drag brought: local files, or placeholders for files from a browser (<paramref name="Web"/>).</summary>
/// <param name="Files">The paths the donut and the conversion take.</param>
/// <param name="Web">The browser drop the paths stand for, or null for files on disk. Its owner disposes it.</param>
internal sealed record DraggedItems(IReadOnlyList<string> Files, WebDrop? Web)
{
    public static DraggedItems None { get; } = new([], null);
}

internal static class DraggedFiles
{
    /// <summary>
    /// Whether the drag offers files or folders in a form Filee reads (they may still be missing, or a link to a web
    /// page).
    /// </summary>
    public static bool Offered(IDataTransfer data) =>
        data.Contains(DataFormat.File) || data.Items.Any(item => item.Formats.Any(IsFilenames)) || BrowserDrags.MayOffer(data);

    /// <summary>
    /// The files in a drag: from a browser when it brought some (as placeholders, see <see cref="BrowserDrags"/>),
    /// else the local files. <paramref name="forDrop"/>: see <see cref="BrowserDrags.Read(IDataTransfer, bool)"/>.
    /// </summary>
    public static DraggedItems Read(IDataTransfer data, bool forDrop)
    {
        if (BrowserDrags.Read(data, forDrop) is { } web)
            return new DraggedItems(web.Placeholders, web);
        var files = Local(data);
        return files.Count == 0 ? DraggedItems.None : new DraggedItems(files, null);
    }

    /// <summary>
    /// Local file and folder paths in the drag (virtual items are ignored). A shortcut counts as the file or folder it
    /// points to. Folders go into "Compress into one archive" like on the command line (issue #186); other presets
    /// leave them out. A trailing separator is removed, and a drive or the file system root is ignored.
    /// </summary>
    public static IReadOnlyList<string> Local(IDataTransfer data) =>
        Local(data, ConversionService.Exists, AppHost.Get<IPlatformServices>().ResolveShortcuts);

    internal static IReadOnlyList<string> Local(IDataTransfer data, Func<string, bool> exists,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? resolveShortcuts = null)
    {
        IReadOnlyList<string> Usable(IReadOnlyList<string> paths) =>
            (resolveShortcuts?.Invoke(paths) ?? paths)
                .Select(path => Path.TrimEndingDirectorySeparator(path))
                .Where(path => exists(path) && !IsRoot(path))
                .Distinct(FileSystemPaths.Comparer).ToList();

        var files = Usable((data.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().ToList());
        return files.Count > 0 ? files : Usable(Filenames(data).ToList());
    }

    /// <summary>Whether <paramref name="path"/> is a drive or the file system root ("C:\", "/").</summary>
    internal static bool IsRoot(string path) =>
        string.Equals(Path.GetPathRoot(path), path, FileSystemPaths.Comparison);

    /// <summary>The formats a drag offers, for the log when Filee finds no files in it.</summary>
    public static string Describe(IDataTransfer data) =>
        data.Formats.Count == 0 ? "nothing" : string.Join(", ", data.Formats.Select(format => format.Identifier).Distinct());

    private static IEnumerable<string> Filenames(IDataTransfer data) =>
        data.Items.SelectMany(item => item.Formats.Where(IsFilenames).SelectMany(format => item.TryGetRaw(format) switch
        {
            byte[] bytes => PasteboardPaths.FromPropertyList(bytes),
            string text => PasteboardPaths.FromPropertyList(text),
            _ => [],
        }));

    private static bool IsFilenames(DataFormat format) =>
        format.Kind == DataFormatKind.Platform && PasteboardPaths.IsFilenamesType(format.Identifier);
}
