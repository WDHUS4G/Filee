// Reads lzip files (.lz, .tar.lz), which 7-Zip does not open. An lzip member is a 6-byte header ("LZIP", version 1,
// coded dictionary size), a raw LZMA stream (lc=3, lp=0, pb=2, always ended by an end marker) and a 20-byte
// trailer (CRC-32 u32, data size u64, member size u64). Each member is rewritten as a classic .lzma file, which the
// bundled 7-Zip decodes; the result is checked against the trailer.

namespace Filee.Engines.Archives;

/// <summary>Decompresses lzip files with the help of 7-Zip's LZMA decoder.</summary>
internal static class LzipReader
{
    private const int HeaderSize = 6;
    private const int TrailerSize = 20;

    /// <summary>Decompresses <paramref name="lzipFile"/> into <paramref name="outputFile"/>.</summary>
    public static async Task DecompressAsync(string lzipFile, string outputFile, SevenZip sevenZip, string workDirectory,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(lzipFile);
        await using var input = new FileStream(lzipFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var members = FindMembers(input, name);
        ArchiveSafety.EnsureRoom(members.Sum(m => m.DataSize), Path.GetDirectoryName(Path.GetFullPath(outputFile))!, name);

        var folder = Path.Combine(workDirectory, "lzip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            await using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lzma = Path.Combine(folder, "member.lzma");
                await using (var file = new FileStream(lzma, FileMode.Create, FileAccess.Write))
                {
                    input.Position = member.Start + 5;
                    var dictionary = DictionarySize(input.ReadByte());
                    // .lzma header: properties (lc=3, lp=0, pb=2), dictionary size, unknown size (-1: until the end marker).
                    file.WriteByte(0x5D);
                    file.Write(BitConverter.GetBytes(dictionary));
                    file.Write(BitConverter.GetBytes(-1L));
                    ArchiveFiles.CopyLimited(new BoundedStream(input, member.Size - HeaderSize - TrailerSize), file, long.MaxValue, cancellationToken);
                }

                var unpacked = Path.Combine(folder, "out");
                ArchiveFiles.TryDeleteFolder(unpacked);
                await sevenZip.ExtractAsync(lzma, unpacked, [], folder, cancellationToken);
                var result = Path.Combine(unpacked, "member");
                if (!File.Exists(result))
                    throw ArchiveSafety.Damaged(name, "LZMA data could not be decoded");

                var crc = new Crc32();
                await using (var decoded = File.OpenRead(result))
                await using (var checkedOutput = new CrcStream(new NonClosingStream(output), crc))
                {
                    if (ArchiveFiles.CopyLimited(decoded, checkedOutput, member.DataSize, cancellationToken) != member.DataSize)
                        throw ArchiveSafety.Damaged(name, "wrong data size");
                }
                if (crc.Value != member.Crc)
                    throw ArchiveSafety.Damaged(name, "checksum error");
            }
        }
        catch
        {
            ArchiveFiles.TryDeleteFile(outputFile);
            throw;
        }
        finally
        {
            ArchiveFiles.TryDeleteFolder(folder);
        }
        File.SetLastWriteTimeUtc(outputFile, File.GetLastWriteTimeUtc(lzipFile));
    }

    /// <summary>One lzip member.</summary>
    private sealed record Member(long Start, long Size, uint Crc, long DataSize);

    /// <summary>
    /// Finds the members from the end of the file backwards (every trailer ends with its member's size), as lzip does.
    /// </summary>
    private static List<Member> FindMembers(FileStream input, string name)
    {
        var members = new List<Member>();
        var trailer = new byte[TrailerSize];
        var header = new byte[5];
        var end = input.Length;
        while (end > 0)
        {
            if (end < HeaderSize + TrailerSize)
                throw InvalidLzip(name);
            input.Position = end - TrailerSize;
            input.ReadExactly(trailer);
            var crc = BitConverter.ToUInt32(trailer, 0);
            var dataSize = BitConverter.ToInt64(trailer, 4);
            var size = BitConverter.ToInt64(trailer, 12);
            if (size < HeaderSize + TrailerSize || size > end || dataSize < 0)
                throw InvalidLzip(name);

            var start = end - size;
            input.Position = start;
            input.ReadExactly(header);
            if (header[0] != 'L' || header[1] != 'Z' || header[2] != 'I' || header[3] != 'P' || header[4] != 1)
                throw InvalidLzip(name);
            members.Insert(0, new Member(start, size, crc, dataSize));
            end = start;
        }
        if (members.Count == 0)
            throw InvalidLzip(name);
        return members;
    }

    /// <summary>
    /// The coded dictionary size of the header: bits 0-4 are the base-2 logarithm, bits 5-7 subtract that many
    /// sixteenths.
    /// </summary>
    internal static uint DictionarySize(int coded)
    {
        var size = 1u << (coded & 0x1F);
        return size - (size / 16) * (uint)((coded >> 5) & 7);
    }

    private static InvalidDataException InvalidLzip(string name) =>
        new($"{name} is not a valid lzip file, or it is damaged.");
}
