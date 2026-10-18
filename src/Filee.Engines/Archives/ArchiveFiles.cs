// File system helpers for the archive engine: copying with a size cap, unique names, and moving an unpacked folder
// into place (with the "one top folder is not nested twice" rule of the Extract preset).

namespace Filee.Engines.Archives;

/// <summary>Copy, naming and folder-moving helpers.</summary>
internal static class ArchiveFiles
{
    /// <summary>Copies a stream, failing when it holds more than <paramref name="maxBytes"/> (a lying or damaged header).</summary>
    public static long CopyLimited(Stream from, Stream to, long maxBytes, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("the data unpacks to more than the archive declares");
            to.Write(buffer, 0, read);
            progress?.Invoke(read);
        }
        return total;
    }

    /// <summary>
    /// <paramref name="path"/> if it is free, else "name_1.ext", "name_2.ext" … (as 7-Zip names clashing entries), so
    /// entries that differ only in case both survive on Windows.
    /// </summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// The folder whose content forms the result of "Extract": the unpacked folder itself, or its only entry when that
    /// is a folder ("photos.zip" holding "photos/…" becomes "photos/…", not "photos/photos/…").
    /// </summary>
    public static string ContentRoot(string unpacked)
    {
        var entries = Directory.GetFileSystemEntries(unpacked);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : unpacked;
    }

    /// <summary>
    /// Moves the folder <paramref name="source"/> to <paramref name="target"/>. An existing target (the preset
    /// overwrites) receives the files, replacing those with the same names.
    /// </summary>
    public static void MoveFolder(string source, string target)
    {
        if (File.Exists(target))
            throw new IOException($"A file named {Path.GetFileName(target)} is in the way of the extracted folder.");
        if (!Directory.Exists(target))
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (IOException) when (!SameVolume(source, target))
            {
                // Different drives: fall through to copying file by file.
            }
        }
        MergeInto(source, target);
    }

    private static void MergeInto(string source, string target)
    {
        var info = new DirectoryInfo(target);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{target} is a link; Filee does not extract into links.");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(dir));
            if (File.Exists(destination))
                File.Delete(destination);
            if (!Directory.Exists(destination) && SameVolume(dir, destination))
                Directory.Move(dir, destination);
            else
                MergeInto(dir, destination);
        }
        Directory.SetLastWriteTime(target, Directory.GetLastWriteTime(source));
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes a folder tree, ignoring failures (a scanner may still hold a file; temp is cleaned later).</summary>
    public static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes a file, ignoring failures.</summary>
    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Write-only pass-through stream that computes the CRC-32 of what is written.</summary>
internal sealed class CrcStream(Stream inner, Crc32 crc) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        crc.Append(buffer);
        inner.Write(buffer);
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
