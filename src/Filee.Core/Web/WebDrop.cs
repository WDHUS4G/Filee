// Files dragged out of a web browser (or another app that holds them only in memory): what they will be called, and
// how they get onto disk. They are saved when their conversion runs (WebDropFetcher), not when they are dropped, so
// the donut opens at once and a download shows its progress and errors like any conversion.

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Web;

/// <summary>One file a drag brought from a browser, not saved yet.</summary>
public abstract class WebDropItem : IDisposable
{
    protected WebDropItem(string name) => Name = WebFileNames.Sanitize(name);

    /// <summary>
    /// The name it will most likely be saved under; its extension decides the donut's slices. The saved file may
    /// still get another extension when its bytes say it is something else.
    /// </summary>
    public string Name { get; }

    /// <summary>Writes the file into <paramref name="folder"/> and returns its path.</summary>
    public abstract Task<string> SaveAsync(string folder, WebDownloader downloader, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    public virtual void Dispose() => GC.SuppressFinalize(this);

    /// <summary>Bytes the browser hands over (a picture's file, a data: address).</summary>
    public static WebDropItem FromBytes(string name, byte[] bytes, string? mime = null) =>
        new StreamItem(name, _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), bytes.Length, mime, null);

    /// <summary>
    /// A file whose bytes the source app streams on request (a Windows virtual file). <paramref name="owner"/> is
    /// disposed with the item, telling the source the transfer is over.
    /// </summary>
    public static WebDropItem FromStream(string name, Func<CancellationToken, Task<Stream>> open, long? size = null,
        IDisposable? owner = null) =>
        new StreamItem(name, open, size, null, owner);

    /// <summary>A file the browser wrote somewhere temporary (Firefox): copied, the browser's copy left alone.</summary>
    public static WebDropItem FromTemporaryFile(string path) =>
        new StreamItem(Path.GetFileName(path),
            _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true)),
            SizeOf(path), null, null);

    private static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An address Filee downloads itself (without the browser's sign-in).</summary>
    public static WebDropItem FromAddress(string name, Uri url) => new AddressItem(name, url);

    private sealed class StreamItem(string name, Func<CancellationToken, Task<Stream>> open, long? size, string? mime, IDisposable? owner)
        : WebDropItem(name)
    {
        public override async Task<string> SaveAsync(string folder, WebDownloader downloader, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            await using var stream = await open(cancellationToken);
            return await downloader.SaveAsync(stream, folder, Name, mime, size, progress, cancellationToken);
        }

        public override void Dispose()
        {
            owner?.Dispose();
            base.Dispose();
        }
    }

    private sealed class AddressItem(string name, Uri url) : WebDropItem(name)
    {
        public Uri Url { get; } = url;

        public override Task<string> SaveAsync(string folder, WebDownloader downloader, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken) =>
            // A name the server sends wins; a guessed extension (".jpg" for a picture address without one) is
            // corrected from the bytes.
            downloader.DownloadAsync(Url, folder, Name, progress, cancellationToken);
    }
}

/// <summary>
/// The files of one browser drag, each standing in the donut and the job as a path that doesn't exist yet (a
/// placeholder in <see cref="WorkFolder"/>, named like the file will be) until its conversion saves it.
/// </summary>
public sealed class WebDrop : IDisposable
{
    private readonly Dictionary<string, WebDropItem> _items = new(StringComparer.Ordinal);
    private readonly List<string> _placeholders = [];
    private int _disposed;

    /// <param name="items">What the drag brought.</param>
    /// <param name="workRoot">Folder for Filee's own copies (<see cref="DefaultWorkRoot"/>).</param>
    public WebDrop(IReadOnlyList<WebDropItem> items, string workRoot)
    {
        WorkFolder = Path.Combine(workRoot, Guid.NewGuid().ToString("N")[..10]);
        for (var i = 0; i < items.Count; i++)
        {
            // A folder per item: two pictures called "image.jpg" stay two files.
            var placeholder = Path.Combine(WorkFolder, "drop", (i + 1).ToString(), items[i].Name);
            _items[placeholder] = items[i];
            _placeholders.Add(placeholder);
        }
    }

    /// <summary>%TEMP%\Filee\web: originals that aren't kept are saved here and deleted after their conversion.</summary>
    public static string DefaultWorkRoot => Path.Combine(Path.GetTempPath(), "Filee", "web");

    /// <summary>This drop's own folder inside the work root.</summary>
    public string WorkFolder { get; }

    /// <summary>The paths the files stand as until they are saved.</summary>
    public IReadOnlyList<string> Placeholders => _placeholders;

    /// <summary>True for a placeholder of any drop (a path in the <see cref="DefaultWorkRoot"/>'s drop folders).</summary>
    public static bool IsPlaceholder(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultWorkRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && full[root.Length..].Split(Path.DirectorySeparatorChar) is [_, "drop", _, _];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True for a path of this drop that isn't a file yet.</summary>
    public bool Holds(string path) => _items.ContainsKey(path);

    internal WebDropItem ItemFor(string placeholder) =>
        _items.TryGetValue(placeholder, out var item) ? item : throw new ArgumentException("Not a file of this drop.", nameof(placeholder));

    /// <summary>The fetcher that saves this drop's files when a conversion runs.</summary>
    public WebDropFetcher CreateFetcher(WebDropOptions options, WebDownloader downloader) => new(this, options, downloader);

    /// <summary>Ends the transfers the items still hold open (the source app may wait for that).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var item in _items.Values)
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The source app went away: nothing left to end.
            }
        }
    }

    /// <summary>
    /// Deletes work folders older than <paramref name="age"/> that a crash or a power cut left behind. A folder still in
    /// use is skipped.
    /// </summary>
    public static void DeleteStale(string workRoot, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(workRoot))
                return;
            foreach (var folder in Directory.EnumerateDirectories(workRoot))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > age)
                    TryDeleteFolder(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { /* a viewer still holds a file; the stale sweep deletes it later */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Where files dragged from a browser are saved (Settings → General).</summary>
/// <param name="Folder">The download folder; "next to the source" means this folder for these files.</param>
/// <param name="SaveOnlyConverted">The original goes into the work folder and is deleted after the conversion.</param>
public sealed record WebDropOptions(string Folder, bool SaveOnlyConverted);

/// <summary>Saves a <see cref="WebDrop"/>'s files for the job that converts them, and cleans up after it.</summary>
public sealed class WebDropFetcher(WebDrop drop, WebDropOptions options, WebDownloader downloader) : ISourceFetcher
{
    public WebDropOptions Options => options;

    /// <summary>
    /// <paramref name="preset"/> with "next to the source" pointing at the download folder, also when the original is
    /// only kept in the work folder.
    /// </summary>
    public Preset Apply(Preset preset)
    {
        var copy = preset.Clone();
        copy.Output.SourceFolder = options.Folder;
        return copy;
    }

    /// <summary>Folder the originals are saved in.</summary>
    public string SaveFolder => options.SaveOnlyConverted ? Path.Combine(drop.WorkFolder, "files") : options.Folder;

    public bool Fetches(string path) => drop.Holds(path);

    public Task<string> FetchAsync(string placeholder, IProgress<TransferProgress> progress, CancellationToken cancellationToken) =>
        drop.ItemFor(placeholder).SaveAsync(SaveFolder, downloader, progress, cancellationToken);

    public void Release(ConversionJob job)
    {
        drop.Dispose();
        // Holds the originals of "Save only the converted file"; otherwise it was never created.
        WebDrop.TryDeleteFolder(drop.WorkFolder);
    }
}
