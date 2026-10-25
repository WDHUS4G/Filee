// The payload appended to Setup.exe:
//
//   [Setup program][LZMA streams: the app's files, one after the other][manifest JSON][trailer, 72 bytes]
//   (then, once Setup is signed, the Authenticode signature: the trailer sits right before it)
//
// A release signs the Setup program first (it becomes the uninstaller) and Setup.exe last. The program's signature
// stays at the end of the program, Setup.exe's header stops pointing to it, and the manifest says where it is so the
// uninstaller written from Setup.exe is signed as well.
//
// Trailer: "FILEE-PAYLOAD-01", payload offset, compressed length, manifest length (little-endian Int64 each) and the
// SHA-256 of the manifest. The manifest holds the SHA-256 of the compressed data and of every file. Files are packed
// into a few large LZMA streams (segments, compressed in parallel; SharpCompress, which the engines use already)
// rather than one per file: similar files next to each other compress far better than a zip (the .NET assemblies share
// much), and programs (.exe, .dll) go through the x86 branch filter first, like 7-Zip and Inno Setup do. Setup checks
// the hash of the compressed data before it changes anything and every file's hash while unpacking.

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace Filee.Setup.Payload;

public sealed class SetupPayload : IDisposable
{
    public const string Magic = "FILEE-PAYLOAD-01";
    public const int TrailerLength = 16 + 8 + 8 + 8 + 32;
    private const int BufferSize = 1 << 20;

    private readonly FileStream _file;
    private readonly string _path;

    private SetupPayload(string path, FileStream file, long offset, long length, PayloadManifest manifest)
    {
        _path = path;
        _file = file;
        Offset = offset;
        CompressedLength = length;
        Manifest = manifest;
    }

    public PayloadManifest Manifest { get; }

    /// <summary>Where the payload starts: the Setup program before it is the uninstaller.</summary>
    public long Offset { get; }

    public long CompressedLength { get; }

    /// <summary>
    /// Opens the payload of <paramref name="exe"/>; null when it has none (uninstall.exe, a development build).
    /// Throws <see cref="InvalidDataException"/> when there is one but it is damaged.
    /// </summary>
    public static SetupPayload? Open(string exe)
    {
        var file = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        try
        {
            var end = OverlayEnd(file);
            if (end < TrailerLength)
            {
                file.Dispose();
                return null;
            }
            var trailer = new byte[TrailerLength];
            file.Position = end - TrailerLength;
            file.ReadExactly(trailer);
            if (Encoding.ASCII.GetString(trailer, 0, 16) != Magic)
            {
                file.Dispose();
                return null;
            }
            var offset = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(16));
            var length = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(24));
            var manifestLength = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(32));
            var manifestHash = trailer.AsSpan(40, 32).ToArray();
            if (offset <= 0 || length < 0 || manifestLength <= 0 || manifestLength > 64 << 20
                || offset + length + manifestLength + TrailerLength != end)
                throw new InvalidDataException("The payload trailer is damaged.");
            var manifestBytes = new byte[manifestLength];
            file.Position = offset + length;
            file.ReadExactly(manifestBytes);
            if (!SHA256.HashData(manifestBytes).AsSpan().SequenceEqual(manifestHash))
                throw new InvalidDataException("The payload manifest is damaged.");
            return new SetupPayload(exe, file, offset, length, PayloadManifest.FromJson(manifestBytes));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// End of the data appended to the program: the file's end, or where its Authenticode signature starts (signing
    /// appends the certificate table after the payload).
    /// </summary>
    internal static long OverlayEnd(Stream file)
    {
        var length = file.Length;
        var signature = Signature(file);
        return signature is null ? length : signature.Offset;
    }

    /// <summary>The certificate table a PE file ends with (its Authenticode signature), or null.</summary>
    internal static ProgramSignature? Signature(Stream file)
    {
        var entry = SecurityDirectory(file);
        if (entry < 0)
            return null;
        Span<byte> buffer = stackalloc byte[8];
        file.Position = entry;
        file.ReadExactly(buffer);
        var certificates = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        // The certificate table's "address" is a file offset.
        return certificates > 0 && size > 0 && certificates + (long)size == file.Length ? new ProgramSignature(certificates, (int)size) : null;
    }

    /// <summary>File position of the PE header's security directory entry (IMAGE_DIRECTORY_ENTRY_SECURITY), or -1.</summary>
    private static long SecurityDirectory(Stream file)
    {
        var length = file.Length;
        try
        {
            Span<byte> buffer = stackalloc byte[4];
            file.Position = 0x3C;
            file.ReadExactly(buffer);
            var pe = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (pe <= 0 || pe > length - 24)
                return -1;
            file.Position = pe;
            file.ReadExactly(buffer);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer) != 0x00004550) // "PE\0\0"
                return -1;
            var optionalHeader = pe + 4 + 20;
            file.Position = optionalHeader;
            file.ReadExactly(buffer[..2]);
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            var entry = optionalHeader + (magic == 0x20B ? 112 : 96) + 4 * 8;
            return entry + 8 <= length ? entry : -1;
        }
        catch (EndOfStreamException)
        {
            return -1;
        }
    }

    /// <summary>Points the security directory of the PE file in <paramref name="file"/> at a certificate table (null: none).</summary>
    private static void SetSecurityDirectory(Stream file, ProgramSignature? signature)
    {
        var entry = SecurityDirectory(file);
        if (entry < 0)
            throw new InvalidDataException("The Setup program is not a PE file.");
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(signature?.Offset ?? 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], (uint)(signature?.Size ?? 0));
        file.Position = entry;
        file.Write(buffer);
    }

    /// <summary>Checks the compressed stream against the manifest. Throws <see cref="InvalidDataException"/>.</summary>
    public void Verify(IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            _file.Position = Offset;
            var left = CompressedLength;
            while (left > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = _file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0)
                    throw new InvalidDataException("The payload is shorter than its manifest says.");
                hash.AppendData(buffer, 0, read);
                left -= read;
                progress?.Report(1 - (double)left / Math.Max(1, CompressedLength));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (PayloadManifest.Hex(hash.GetHashAndReset()) != Manifest.CompressedSha256)
            throw new InvalidDataException("The payload doesn't match its checksum.");
    }

    /// <summary>
    /// Unpacks every file into <paramref name="folder"/> and checks its size and SHA-256. Reports the bytes written.
    /// Throws <see cref="InvalidDataException"/> when a file doesn't match (the folder is then incomplete).
    /// </summary>
    public void ExtractTo(string folder, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var properties = Convert.FromBase64String(Manifest.LzmaProperties);
        var starts = new long[Manifest.Segments.Count];
        for (long i = 0, start = Offset; i < starts.Length; start += Manifest.Segments[(int)i], i++)
            starts[i] = start;
        long written = 0;
        void Written(long bytes) => progress?.Report(Interlocked.Add(ref written, bytes));
        try
        {
            // The segments are independent streams: unpacked side by side (LZMA decodes on one core each).
            Parallel.For(0, Manifest.Segments.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = cancellation },
                segment => ExtractSegment(segment, starts[segment], properties, folder, Written, cancellation));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            var first = ex.InnerExceptions[0];
            if (first is InvalidDataException or OperationCanceledException or IOException or UnauthorizedAccessException or OutOfMemoryException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
            // The decoder's own errors on damaged data.
            throw new InvalidDataException("The payload can't be unpacked: " + first.Message, first);
        }
    }

    private void ExtractSegment(int segment, long start, byte[] properties, string folder, Action<long> written, CancellationToken cancellation)
    {
        var files = Manifest.Files.Where(f => f.Segment == segment).ToList();
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        file.Position = start;
        using var compressed = new BoundedReadStream(file, Manifest.Segments[segment]);
        using var lzma = new LzmaStream(properties, compressed, Manifest.Segments[segment], files.Sum(f => f.Size));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var entry in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.Combine(folder, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                {
                    if (entry.Filter == PayloadFile.X86)
                    {
                        // The filter works on the whole file: unpack it in memory, undo the filter, then write it.
                        var content = new byte[entry.Size];
                        ReadExactly(lzma, content, entry.Path);
                        X86Filter.Decode(content);
                        hash.AppendData(content);
                        output.Write(content);
                        written(content.Length);
                    }
                    else
                    {
                        var left = entry.Size;
                        while (left > 0)
                        {
                            var read = lzma.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (read == 0)
                                throw new InvalidDataException($"The payload ends inside {entry.Path}.");
                            hash.AppendData(buffer, 0, read);
                            output.Write(buffer, 0, read);
                            left -= read;
                            written(read);
                        }
                    }
                }
                if (PayloadManifest.Hex(hash.GetHashAndReset()) != entry.Sha256)
                    throw new InvalidDataException($"{entry.Path} doesn't match its checksum.");
            }
        }
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            throw new InvalidDataException("The payload can't be unpacked: " + ex.Message, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> target, string path)
    {
        while (!target.IsEmpty)
        {
            var read = stream.Read(target);
            if (read == 0)
                throw new InvalidDataException($"The payload ends inside {path}.");
            target = target[read..];
        }
    }

    /// <summary>
    /// Writes the Setup program without the payload: the uninstaller. A program signed before the payload was
    /// appended gets its own signature back.
    /// </summary>
    public void CopyProgramTo(string path)
    {
        _file.Position = 0;
        using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize);
        using (var program = new BoundedReadStream(_file, Offset))
            program.CopyTo(output, BufferSize);
        if (Manifest.Signature is { } signature)
        {
            if (signature.Offset + signature.Size != Offset)
                throw new InvalidDataException("The program signature in the payload doesn't fit the program.");
            SetSecurityDirectory(output, signature);
        }
    }

    public void Dispose() => _file.Dispose();

    /// <summary>Raw size from which files start a new segment (each segment is compressed on its own core).</summary>
    public const long SegmentSize = 96L << 20;

    /// <summary>Segment size of a fast (checks only) payload: small, so tests reach several segments cheaply.</summary>
    public const long FastSegmentSize = 4L << 20;

    /// <summary>
    /// LZMA dictionary: matches reach 64 MB back, past the largest file. Unpacking needs this much memory per
    /// segment (at most four at once).
    /// </summary>
    private const int Dictionary = 64 << 20;

    /// <summary>Programs whose x86/x64 code the branch filter makes more compressible.</summary>
    public static bool IsProgram(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Appends <paramref name="appFolder"/> to a copy of <paramref name="setupProgram"/>: writes <paramref name="output"/>,
    /// the finished Setup. <paramref name="fast"/> uses a small dictionary (checks only); the segments are compressed in
    /// parallel (each LZMA encoder needs about 11 times its dictionary in memory, so at most four at a time).
    /// </summary>
    public static PayloadManifest Write(string setupProgram, string appFolder, string output, string version,
        IReadOnlyList<EngineOffer> engines, string engineMarker, bool fast = false, Action<string>? log = null)
    {
        appFolder = Path.GetFullPath(appFolder);
        // Similar files next to each other compress better: by extension, then by path.
        var paths = Directory.EnumerateFiles(appFolder, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(appFolder, p))
            .OrderBy(p => Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var relative in paths)
            PayloadManifest.CheckPath(relative);

        // Consecutive files up to SegmentSize per segment.
        var segmentSize = fast ? FastSegmentSize : SegmentSize;
        var groups = new List<List<string>>();
        long groupSize = segmentSize;
        foreach (var relative in paths)
        {
            var size = new FileInfo(Path.Combine(appFolder, relative)).Length;
            if (groupSize >= segmentSize)
            {
                groups.Add([]);
                groupSize = 0;
            }
            groups[^1].Add(relative);
            groupSize += size;
        }
        if (groups.Count == 0)
            groups.Add([]);

        var encoderProperties = new LzmaEncoderProperties(eos: true, dictionary: fast ? 1 << 20 : Dictionary, numFastBytes: fast ? 32 : 273);
        var compressed = new byte[groups.Count][];
        var hashed = new PayloadFile[groups.Count][];
        byte[]? properties = null;
        Parallel.For(0, groups.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, segment =>
        {
            using var buffer = new MemoryStream();
            var segmentFiles = new List<PayloadFile>();
            using (var lzma = new LzmaStream(encoderProperties, false, buffer))
            {
                foreach (var relative in groups[segment])
                {
                    var content = File.ReadAllBytes(Path.Combine(appFolder, relative));
                    var sha256 = PayloadManifest.Hex(SHA256.HashData(content));
                    var program = IsProgram(relative);
                    if (program)
                        X86Filter.Encode(content);
                    lzma.Write(content);
                    segmentFiles.Add(new PayloadFile(relative, content.Length, sha256, segment, program ? PayloadFile.X86 : ""));
                }
                properties ??= lzma.Properties;
            }
            compressed[segment] = buffer.ToArray();
            hashed[segment] = [.. segmentFiles];
            log?.Invoke($"segment {segment + 1}/{groups.Count}: {groups[segment].Count} files, {segmentFiles.Sum(f => f.Size) / 1_000_000} -> {compressed[segment].Length / 1_000_000} MB");
        });

        var partial = output + ".partial";
        using (var target = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize))
        {
            // A signed program keeps its signature in front of the payload, but Setup.exe stops pointing to it: the
            // signature of the whole Setup.exe goes at the end. The uninstaller (CopyProgramTo) points to it again.
            ProgramSignature? signature;
            using (var program = File.OpenRead(setupProgram))
            {
                signature = Signature(program);
                program.Position = 0;
                program.CopyTo(target);
            }
            if (signature is not null)
            {
                SetSecurityDirectory(target, null);
                target.Seek(0, SeekOrigin.End);
            }
            var offset = target.Position;
            using var compressedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var segment in compressed)
            {
                compressedHash.AppendData(segment);
                target.Write(segment);
            }
            var length = target.Position - offset;
            var manifest = new PayloadManifest(version, hashed.SelectMany(f => f).ToList(), PayloadManifest.Hex(compressedHash.GetHashAndReset()),
                engines, engineMarker, compressed.Select(c => (long)c.Length).ToList(), Convert.ToBase64String(properties ?? []), signature);
            var json = manifest.ToJson();
            // Spaces after the JSON end the file on an 8-byte boundary: signtool puts the certificate table there and
            // would pad in front of it otherwise, and the trailer has to sit right before the table.
            var padding = (int)((8 - (target.Position + json.Length + TrailerLength) % 8) % 8);
            if (padding > 0)
                json = [.. json, .. Enumerable.Repeat((byte)' ', padding)];
            target.Write(json);
            var trailer = new byte[TrailerLength];
            Encoding.ASCII.GetBytes(Magic).CopyTo(trailer, 0);
            BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(16), offset);
            BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(24), length);
            BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(32), json.Length);
            SHA256.HashData(json).CopyTo(trailer, 40);
            target.Write(trailer);
        }
        File.Move(partial, output, overwrite: true);
        using var check = Open(output) ?? throw new InvalidDataException("The payload was not written.");
        return check.Manifest;
    }

    /// <summary>Reads at most <c>length</c> bytes from the current position of a stream it doesn't own.</summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0)
                return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
