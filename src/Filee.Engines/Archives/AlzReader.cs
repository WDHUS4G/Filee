// Reads ALZ archives, the format of the Korean ALZip (ESTsoft), which 7-Zip does not support. Layout as documented by
// unalz (zlib licence) and the "unalz" Rust crate (MIT); Filee's reader is its own:
//
//   "ALZ\x01" + 4 bytes                         archive header
//   "BLZ\x01" + local file header + name + data  one per entry
//   "CLZ\x01" + 12 bytes                         central directory marker
//   "CLZ\x02" (+ 12 bytes of end info)           end of archive; "ELZ\x01" is a comment section
//
// Local file header (little endian): name length u16, attributes u8 (0x10 = folder), DOS time u32, descriptor u8
// (bit 0 = encrypted, bits 4-7 = width of the size fields: 0x10/0x20/0x40/0x80 = 1/2/4/8 bytes), unknown u8; then,
// when the width is not 0: method u8 (0 store, 1 ALZ bzip2, 2 deflate), unknown u8, CRC-32 u32, packed size,
// unpacked size. Names are CP949 (Korean Windows) unless they are valid UTF-8.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks ALZ archives (store, deflate and ALZip's bzip2 variant).</summary>
internal static class AlzReader
{
    private const uint ArchiveSignature = 0x015A4C41; // "ALZ\x01"
    private const uint EntrySignature = 0x015A4C42;   // "BLZ\x01"
    private const uint DirectorySignature = 0x015A4C43; // "CLZ\x01"
    private const uint EndSignature = 0x025A4C43;     // "CLZ\x02"
    private const uint SplitMarker = 0x035A4C43;      // "CLZ\x03"
    private const uint CommentSignature = 0x015A4C45; // "ELZ\x01"
    private const uint SplitSignature = 0x015A4C53;   // "SLZ\x01"

    /// <summary>One entry of the archive.</summary>
    private sealed record Entry(string Name, bool IsFolder, DateTime? Modified, int Method, uint Crc, long PackedSize, long Size, long DataOffset);

    /// <summary>Unpacks every entry of <paramref name="archive"/> into <paramref name="targetDirectory"/>.</summary>
    public static void Extract(string archive, string targetDirectory, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var entries = ReadEntries(stream, archiveName);
        ArchiveSafety.EnsureRoom(entries.Where(e => !e.IsFolder).Sum(e => e.Size), targetDirectory, archiveName);

        var folders = new List<(string Path, DateTime? Modified)>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArchiveSafety.ResolveEntryPath(targetDirectory, entry.Name, archiveName);
            if (entry.IsFolder)
            {
                Directory.CreateDirectory(path);
                folders.Add((path, entry.Modified));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            path = ArchiveFiles.UniquePath(path);
            stream.Position = entry.DataOffset;
            var crc = new Crc32();
            using (var output = new CrcStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), crc))
            {
                var packed = new BoundedStream(stream, entry.PackedSize);
                switch (entry.Method)
                {
                    case 0:
                        ArchiveFiles.CopyLimited(packed, output, entry.Size, cancellationToken);
                        break;
                    case 1:
                        Bzip2Decoder.Decompress(packed, output, alzFraming: true, entry.Size, cancellationToken);
                        break;
                    case 2:
                        using (var inflate = new DeflateStream(packed, CompressionMode.Decompress))
                            ArchiveFiles.CopyLimited(inflate, output, entry.Size, cancellationToken);
                        break;
                    default:
                        throw new InvalidDataException($"{archiveName} uses an unknown ALZ compression method ({entry.Method}).");
                }
            }

            if (new FileInfo(path).Length != entry.Size || crc.Value != entry.Crc)
                throw ArchiveSafety.Damaged(archiveName, $"checksum error in {entry.Name}");
            if (entry.Modified is { } modified)
                File.SetLastWriteTime(path, modified);
        }

        // Folder times last: writing files into a folder changes its time.
        foreach (var (path, modified) in folders)
            if (modified is { } time)
                Directory.SetLastWriteTime(path, time);
    }

    private static List<Entry> ReadEntries(FileStream stream, string archiveName)
    {
        var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var entries = new List<Entry>();
        try
        {
            if (stream.Length < 8 || reader.ReadUInt32() != ArchiveSignature)
                throw new InvalidDataException($"{archiveName} is not an ALZ archive.");
            reader.ReadUInt32(); // version / unknown

            while (stream.Position + 4 <= stream.Length)
            {
                switch (reader.ReadUInt32())
                {
                    case EntrySignature:
                        if (entries.Count >= ArchiveSafety.MaxEntries)
                            throw new InvalidDataException($"{archiveName} has more than {ArchiveSafety.MaxEntries:N0} entries; Filee does not unpack it.");
                        entries.Add(ReadEntry(reader, archiveName));
                        break;
                    case DirectorySignature:
                        stream.Seek(12, SeekOrigin.Current);
                        break;
                    case SplitMarker:
                        break;
                    case CommentSignature:
                        SkipComment(stream, reader);
                        break;
                    case EndSignature:
                        return entries;
                    case SplitSignature:
                        throw new InvalidDataException($"{archiveName} is part of a split ALZ archive (.alz + .a00 …); Filee cannot unpack split archives.");
                    default:
                        throw ArchiveSafety.Damaged(archiveName, "unknown ALZ header");
                }
            }
            return entries; // no end record: keep what was found, the data checks catch damage
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        }
    }

    private static Entry ReadEntry(BinaryReader reader, string archiveName)
    {
        var nameLength = reader.ReadUInt16();
        var attributes = reader.ReadByte();
        var dosTime = reader.ReadUInt32();
        var descriptor = reader.ReadByte();
        reader.ReadByte(); // unknown

        if ((descriptor & 0x01) != 0)
            throw ArchiveSafety.Encrypted(archiveName);

        var width = (descriptor & 0xF0) switch
        {
            0x00 => 0,
            0x10 => 1,
            0x20 => 2,
            0x40 => 4,
            0x80 => 8,
            _ => throw ArchiveSafety.Damaged(archiveName, "invalid ALZ size field"),
        };
        int method = 0;
        uint crc = 0;
        long packedSize = 0, size = 0;
        if (width > 0)
        {
            method = reader.ReadByte();
            reader.ReadByte(); // unknown
            crc = reader.ReadUInt32();
            packedSize = ReadSize(reader, width);
            size = ReadSize(reader, width);
        }
        if (nameLength == 0 || packedSize < 0 || size < 0)
            throw ArchiveSafety.Damaged(archiveName, "invalid ALZ entry");

        var name = DecodeName(reader.ReadBytes(nameLength));
        var offset = reader.BaseStream.Position;
        if (offset + packedSize > reader.BaseStream.Length)
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        reader.BaseStream.Seek(packedSize, SeekOrigin.Current);

        var isFolder = (attributes & 0x10) != 0;
        return new Entry(name, isFolder, FromDosTime(dosTime), method, crc, packedSize, size, offset);
    }

    private static long ReadSize(BinaryReader reader, int width)
    {
        Span<byte> bytes = stackalloc byte[8];
        bytes.Clear();
        reader.BaseStream.ReadExactly(bytes[..width]);
        return BitConverter.ToInt64(bytes);
    }

    /// <summary>Skips an "ELZ\x01" comment section; its size is in the 12 bytes after the end record.</summary>
    private static void SkipComment(FileStream stream, BinaryReader reader)
    {
        var position = stream.Position;
        stream.Position = stream.Length - 12;
        var size = reader.ReadUInt32(); // includes the 4-byte signature already read
        stream.Position = position + Math.Max(0, size - 4L);
    }

    /// <summary>CP949 names as written by Korean Windows, UTF-8 when the bytes are valid UTF-8.</summary>
    internal static string DecodeName(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    /// <summary>DOS date and time (local time, 2-second steps), or null when invalid.</summary>
    internal static DateTime? FromDosTime(uint value)
    {
        int second = (int)(value & 0x1F) * 2, minute = (int)(value >> 5) & 0x3F, hour = (int)(value >> 11) & 0x1F;
        int day = (int)(value >> 16) & 0x1F, month = (int)(value >> 21) & 0x0F, year = 1980 + (int)(value >> 25);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }
}
