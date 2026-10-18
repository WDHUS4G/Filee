// Writes archives. ZIP, TAR and TAR.GZ are written in-process (System.IO.Compression / System.Formats.Tar), so file
// names are exactly what Filee chooses: UTF-8 in ZIP (with the language encoding flag), PAX headers in TAR. 7Z, the
// bzip2 / xz TAR variants and single-file GZ / BZ2 / XZ are written by the bundled 7-Zip.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>A file or folder to store in an archive under <paramref name="EntryName"/>; folders go in with their content.</summary>
internal sealed record PackItem(string SourcePath, string EntryName);

/// <summary>Creates archives from files and folders.</summary>
internal sealed class ArchivePacker(SevenZip? sevenZip)
{
    /// <summary>Formats written without 7-Zip.</summary>
    public static readonly IReadOnlySet<string> BuiltInFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zip", "tar", "tgz" };

    /// <summary>Formats that hold exactly one file, without name or folders.</summary>
    public static readonly IReadOnlySet<string> SingleFileFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gz", "bz2", "xz" };

    /// <summary>
    /// Writes <paramref name="items"/> into a new archive of <paramref name="format"/> at <paramref name="outputPath"/>.
    /// The archive is built under a temporary name next to it and renamed when complete, so a failed or cancelled run
    /// never leaves a broken archive (or replaces an existing one).
    /// </summary>
    public async Task PackAsync(IReadOnlyList<PackItem> items, string outputPath, string format, ArchiveLevel level,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.filee-{Guid.NewGuid().ToString("N")[..8]}.tmp");
        try
        {
            switch (format)
            {
                case "zip":
                    await Task.Run(() => WriteZip(items, temp, level, progress, cancellationToken), cancellationToken);
                    break;
                case "tar":
                case "tgz":
                    await Task.Run(() => WriteTar(items, temp, format == "tgz" ? level : null, progress, cancellationToken), cancellationToken);
                    break;
                case "tbz2":
                case "txz":
                    {
                        // 7-Zip compresses a single stream; the TAR is written first, in the work folder.
                        var tar = Path.Combine(workDirectory, $"pack-{Guid.NewGuid():N}.tar");
                        try
                        {
                            await Task.Run(() => WriteTar(items, tar, null, Scaled(progress, 0, 0.3), cancellationToken), cancellationToken);
                            await RunSevenZipAsync(format == "tbz2" ? "bzip2" : "xz", [tar], temp, level, workDirectory, progress, 0.3, cancellationToken);
                        }
                        finally
                        {
                            ArchiveFiles.TryDeleteFile(tar);
                        }
                        break;
                    }
                case "7z":
                    await RunSevenZipAsync("7z", await NamedAsAsync(items, workDirectory, cancellationToken), temp, level, workDirectory, progress, 0, cancellationToken);
                    break;
                case "gz":
                case "bz2":
                case "xz":
                    if (items.Count != 1 || !File.Exists(items[0].SourcePath))
                        throw new InvalidOperationException(
                            $"A .{format} file holds a single file. Choose TAR.{format.ToUpperInvariant()} to compress several files or folders.");
                    var type = format switch { "gz" => "gzip", "bz2" => "bzip2", _ => "xz" };
                    await RunSevenZipAsync(type, await NamedAsAsync(items, workDirectory, cancellationToken), temp, level, workDirectory, progress, 0, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Filee cannot write {format} archives.");
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            ArchiveFiles.TryDeleteFile(temp);
        }
    }

    private async Task RunSevenZipAsync(string type, IReadOnlyList<string> paths, string archive, ArchiveLevel level,
        string workDirectory, IProgress<double>? progress, double from, CancellationToken cancellationToken)
    {
        if (sevenZip is null)
            throw new InvalidOperationException("7-Zip was not found (engines/7zip).");
        // 7-Zip reports nothing while it works: estimate from the amount of data (~40 MB/s at normal level).
        var bytes = paths.Sum(TotalSize);
        var typical = TimeSpan.FromSeconds(Math.Max(0.5, bytes / 40e6));
        using (ProgressEstimate.Start(progress, typical, Math.Max(from, 0.05), 0.95))
            await sevenZip.AddAsync(archive, type, level, paths, workDirectory, cancellationToken);
    }

    /// <summary>
    /// Paths 7-Zip can take as they are: it stores each under its own file name, so items whose entry name differs
    /// (a renamed duplicate) are copied under that name into the work folder first.
    /// </summary>
    private static async Task<IReadOnlyList<string>> NamedAsAsync(IReadOnlyList<PackItem> items, string workDirectory, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (string.Equals(Path.GetFileName(item.SourcePath), item.EntryName, StringComparison.Ordinal))
            {
                paths.Add(item.SourcePath);
                continue;
            }
            var folder = Path.Combine(workDirectory, "rename-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, item.EntryName);
            if (Directory.Exists(item.SourcePath))
                await CopyFolderAsync(new DirectoryInfo(item.SourcePath), copy, cancellationToken);
            else
                await CopyFileAsync(item.SourcePath, copy, cancellationToken);
            paths.Add(copy);
        }
        return paths;
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        await using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            await from.CopyToAsync(to, cancellationToken);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    /// <summary>Copies a folder tree without following links.</summary>
    private static async Task CopyFolderAsync(DirectoryInfo source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var info in source.EnumerateFileSystemInfos())
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var destination = Path.Combine(target, info.Name);
            if (info is DirectoryInfo dir)
                await CopyFolderAsync(dir, destination, cancellationToken);
            else
                await CopyFileAsync(info.FullName, destination, cancellationToken);
        }
        Directory.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
    }

    private static void WriteZip(IReadOnlyList<PackItem> items, string path, ArchiveLevel level, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = Expand(items).ToList();
        var counter = new ByteCounter(files.Sum(f => f.Length), progress);
        var compression = level switch
        {
            ArchiveLevel.Store => CompressionLevel.NoCompression,
            ArchiveLevel.Fast => CompressionLevel.Fastest,
            ArchiveLevel.Maximum => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        // UTF-8 names get the "language encoding" flag, so every unzip tool shows Korean or Chinese names correctly.
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, new UTF8Encoding(false));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.IsFolder)
            {
                zip.CreateEntry(file.EntryName + "/").LastWriteTime = ZipTime(file.Modified);
                continue;
            }
            var entry = zip.CreateEntry(file.EntryName, compression);
            entry.LastWriteTime = ZipTime(file.Modified);
            using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var target = entry.Open();
            ArchiveFiles.CopyLimited(source, target, long.MaxValue, cancellationToken, counter.Add);
        }
    }

    /// <summary>Writes a PAX TAR (UTF-8 names, times), gzip-compressed when <paramref name="gzipLevel"/> is set.</summary>
    private static void WriteTar(IReadOnlyList<PackItem> items, string path, ArchiveLevel? gzipLevel, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = Expand(items).ToList();
        var counter = new ByteCounter(files.Sum(f => f.Length), progress);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using Stream stream = gzipLevel is { } level
            ? new GZipStream(file, level switch
            {
                ArchiveLevel.Store => CompressionLevel.NoCompression,
                ArchiveLevel.Fast => CompressionLevel.Fastest,
                ArchiveLevel.Maximum => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal,
            }, leaveOpen: true)
            : new NonClosingStream(file);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsFolder)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName + "/")
                {
                    ModificationTime = item.Modified,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                           | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                });
                continue;
            }
            using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
            {
                ModificationTime = item.Modified,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = new CountingStream(source, counter.Add, cancellationToken),
            };
            writer.WriteEntry(entry);
        }
    }

    /// <summary>A file or folder in the order it is written, with its name inside the archive.</summary>
    private sealed record PackFile(string Path, string EntryName, bool IsFolder, long Length, DateTimeOffset Modified);

    /// <summary>
    /// Every file and folder of the items, folders before their content. Links are skipped, never followed, so
    /// nothing outside the given folders ends up in the archive.
    /// </summary>
    private static IEnumerable<PackFile> Expand(IReadOnlyList<PackItem> items)
    {
        foreach (var item in items)
        {
            var name = item.EntryName.Replace('\\', '/').Trim('/');
            if (File.Exists(item.SourcePath))
            {
                var info = new FileInfo(item.SourcePath);
                yield return new PackFile(info.FullName, name, false, info.Length, info.LastWriteTimeUtc);
            }
            else if (Directory.Exists(item.SourcePath))
            {
                foreach (var file in ExpandFolder(new DirectoryInfo(item.SourcePath), name))
                    yield return file;
            }
            else
            {
                throw new FileNotFoundException($"{item.SourcePath} was not found.", item.SourcePath);
            }
        }
    }

    private static IEnumerable<PackFile> ExpandFolder(DirectoryInfo folder, string name)
    {
        yield return new PackFile(folder.FullName, name, true, 0, folder.LastWriteTimeUtc);
        foreach (var info in folder.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var child = name + "/" + info.Name;
            if (info is DirectoryInfo dir)
            {
                foreach (var file in ExpandFolder(dir, child))
                    yield return file;
            }
            else if (info is FileInfo file)
            {
                yield return new PackFile(file.FullName, child, false, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    private static long TotalSize(string path) =>
        File.Exists(path) ? new FileInfo(path).Length
        : Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    /// <summary>ZIP stores DOS times (local, 1980–2107).</summary>
    private static DateTimeOffset ZipTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, local.Offset)
            : local.Year > 2107 ? new DateTimeOffset(2107, 12, 31, 0, 0, 0, local.Offset)
            : local;
    }

    private static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(p => progress.Report(from + (to - from) * p));

    /// <summary>Turns bytes written into a 0..1 progress value.</summary>
    private sealed class ByteCounter(long total, IProgress<double>? progress)
    {
        private long _done;
        private double _reported;

        public void Add(long bytes)
        {
            _done += bytes;
            var value = total <= 0 ? 1 : Math.Min(1, _done / (double)total);
            if (value - _reported >= 0.01)
            {
                _reported = value;
                progress?.Report(value);
            }
        }
    }

    /// <summary>Read-only pass-through that reports bytes read and honours cancellation (TarWriter copies the data itself).</summary>
    private sealed class CountingStream(Stream inner, Action<long> onRead, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer);
            onRead(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
