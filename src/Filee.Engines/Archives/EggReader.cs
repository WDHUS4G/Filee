// Reads EGG archives, ALZip's newer format, which 7-Zip does not support. Layout as documented by EggDotNet (MIT)
// and unegg (MIT); Filee's reader is its own. All numbers are little endian.
//
//   "EGGA" version u16, header id u32, reserved u32, extra fields…, END      archive header
//   FILE (0x0A8590E3) file id u32, size u64, extra fields…, END              one per entry, followed by
//   BLOCK (0x02B50C13) method u8, hint u8, size u32, packed u32, CRC-32 u32, END, data   one or more blocks
//   END (0x08E28222)                                                         end of archive
//
// An extra field is magic u32, flags u8, size u16 (u32 when flags bit 0 is set) and data. The file name field
// has a locale u16 before the name when flags & 0x08 (else the name is UTF-8) and a parent id u32 when flags & 0x10.
// Methods: 0 store, 1 deflate, 2 bzip2, 3 AZO (ESTsoft's own, not supported), 4 LZMA (ZIP-style: 4-byte version
// and size header, 5 property bytes, raw data; decoded by 7-Zip). Solid, split and encrypted archives are refused.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks EGG archives (store, deflate, bzip2 and LZMA blocks).</summary>
internal static class EggReader
{
    private const uint ArchiveMagic = 0x41474745;   // "EGGA"
    private const uint EndMagic = 0x08E28222;
    private const uint FileMagic = 0x0A8590E3;
    private const uint BlockMagic = 0x02B50C13;
    private const uint FileNameMagic = 0x0A8591AC;
    private const uint WindowsInfoMagic = 0x2C86950B;
    private const uint PosixInfoMagic = 0x1EE922E5;
    private const uint EncryptMagic = 0x08D1470F;
    private const uint SplitMagic = 0x24F5A262;
    private const uint SolidMagic = 0x24E5A060;

    /// <summary>One compressed block of an entry's data.</summary>
    private sealed record Block(int Method, long Size, long PackedSize, uint Crc, long DataOffset);

    /// <summary>One entry of the archive.</summary>
    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public bool IsFolder { get; set; }
        public DateTime? Modified { get; set; }
        public List<Block> Blocks { get; } = [];
    }

    /// <summary>Unpacks every entry of <paramref name="archive"/> into <paramref name="targetDirectory"/>.</summary>
    /// <param name="sevenZip">Decodes LZMA blocks; null when 7-Zip is missing (LZMA entries then fail).</param>
    public static async Task ExtractAsync(string archive, string targetDirectory, SevenZip? sevenZip, string workDirectory,
        CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(archive);
        await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
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
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                foreach (var block in entry.Blocks)
                {
                    var crc = new Crc32();
                    var start = output.Position;
                    await using (var checkedOutput = new CrcStream(new NonClosingStream(output), crc))
                        await DecodeBlockAsync(stream, block, checkedOutput, sevenZip, workDirectory, archiveName, cancellationToken);
                    if (output.Position - start != block.Size || crc.Value != block.Crc)
                        throw ArchiveSafety.Damaged(archiveName, $"checksum error in {entry.Name}");
                }
            }
            if (new FileInfo(path).Length != entry.Size)
                throw ArchiveSafety.Damaged(archiveName, $"wrong size of {entry.Name}");
            if (entry.Modified is { } modified)
                File.SetLastWriteTimeUtc(path, modified);
        }

        foreach (var (path, modified) in folders)
            if (modified is { } time)
                Directory.SetLastWriteTimeUtc(path, time);
    }

    private static async Task DecodeBlockAsync(FileStream stream, Block block, Stream output, SevenZip? sevenZip,
        string workDirectory, string archiveName, CancellationToken cancellationToken)
    {
        stream.Position = block.DataOffset;
        var packed = new BoundedStream(stream, block.PackedSize);
        switch (block.Method)
        {
            case 0:
                ArchiveFiles.CopyLimited(packed, output, block.Size, cancellationToken);
                break;
            case 1:
                await using (var inflate = new DeflateStream(packed, CompressionMode.Decompress))
                    ArchiveFiles.CopyLimited(inflate, output, block.Size, cancellationToken);
                break;
            case 2:
                Bzip2Decoder.Decompress(packed, output, alzFraming: false, block.Size, cancellationToken);
                break;
            case 3:
                throw new NotSupportedException($"{archiveName} is compressed with ALZip's own AZO method, which Filee cannot unpack. Save it again in ALZip as ZIP or 7Z, or as EGG with another method.");
            case 4:
                await DecodeLzmaAsync(packed, block, output, sevenZip, workDirectory, archiveName, cancellationToken);
                break;
            default:
                throw new InvalidDataException($"{archiveName} uses an unknown EGG compression method ({block.Method}).");
        }
    }

    /// <summary>
    /// LZMA blocks carry ZIP's LZMA header (version u16, property size u16, 5 property bytes). Rewritten as a .lzma
    /// file (properties, unpacked size u64, data) they can be decoded by 7-Zip.
    /// </summary>
    private static async Task DecodeLzmaAsync(Stream packed, Block block, Stream output, SevenZip? sevenZip,
        string workDirectory, string archiveName, CancellationToken cancellationToken)
    {
        if (sevenZip is null)
            throw new InvalidOperationException("7-Zip was not found; it is needed for LZMA data.");
        var header = new byte[9];
        packed.ReadExactly(header);
        var folder = Path.Combine(workDirectory, "egg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var lzma = Path.Combine(folder, "block.lzma");
            await using (var file = new FileStream(lzma, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(header, 4, 5);
                file.Write(BitConverter.GetBytes(block.Size));
                ArchiveFiles.CopyLimited(packed, file, block.PackedSize, cancellationToken);
            }
            var unpacked = Path.Combine(folder, "out");
            await sevenZip.ExtractAsync(lzma, unpacked, [], folder, cancellationToken);
            await using var result = File.OpenRead(Path.Combine(unpacked, "block"));
            ArchiveFiles.CopyLimited(result, output, block.Size, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw ArchiveSafety.Damaged(archiveName, "LZMA data could not be decoded");
        }
        finally
        {
            ArchiveFiles.TryDeleteFolder(folder);
        }
    }

    private static List<Entry> ReadEntries(FileStream stream, string archiveName)
    {
        var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var entries = new List<Entry>();
        try
        {
            if (stream.Length < 14 || reader.ReadUInt32() != ArchiveMagic)
                throw new InvalidDataException($"{archiveName} is not an EGG archive.");
            reader.ReadUInt16(); // version
            reader.ReadUInt32(); // header id
            reader.ReadUInt32(); // reserved
            ReadExtraFields(reader, archiveName, null);

            Entry? current = null;
            while (stream.Position + 4 <= stream.Length)
            {
                var magic = reader.ReadUInt32();
                switch (magic)
                {
                    case FileMagic:
                        if (entries.Count >= ArchiveSafety.MaxEntries)
                            throw new InvalidDataException($"{archiveName} has more than {ArchiveSafety.MaxEntries:N0} entries; Filee does not unpack it.");
                        current = new Entry();
                        reader.ReadUInt32(); // file id
                        current.Size = reader.ReadInt64();
                        ReadExtraFields(reader, archiveName, current);
                        entries.Add(current);
                        break;
                    case BlockMagic when current is not null:
                        var method = reader.ReadByte();
                        reader.ReadByte(); // hint
                        var size = reader.ReadUInt32();
                        var packedSize = reader.ReadUInt32();
                        var crc = reader.ReadUInt32();
                        if (reader.ReadUInt32() != EndMagic)
                            throw ArchiveSafety.Damaged(archiveName, "invalid EGG block header");
                        if (stream.Position + packedSize > stream.Length)
                            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
                        current.Blocks.Add(new Block(method, size, packedSize, crc, stream.Position));
                        stream.Seek(packedSize, SeekOrigin.Current);
                        break;
                    case EndMagic:
                        return Validate(entries, archiveName);
                    default:
                        // Comments and other extra fields between entries.
                        SkipExtraField(reader, archiveName);
                        break;
                }
            }
            return Validate(entries, archiveName);
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        }
    }

    private static List<Entry> Validate(List<Entry> entries, string archiveName)
    {
        foreach (var entry in entries)
        {
            if (entry.Name.Length == 0)
                throw ArchiveSafety.Damaged(archiveName, "an entry has no name");
            if (entry.Name.EndsWith('/') || entry.Name.EndsWith('\\'))
                entry.IsFolder = true;
            if (!entry.IsFolder && entry.Blocks.Sum(b => b.Size) != entry.Size)
                throw ArchiveSafety.Damaged(archiveName, $"the blocks of {entry.Name} do not add up");
        }
        return entries;
    }

    /// <summary>Reads extra fields up to the END marker, filling <paramref name="entry"/> (null: archive header).</summary>
    private static void ReadExtraFields(BinaryReader reader, string archiveName, Entry? entry)
    {
        while (true)
        {
            var magic = reader.ReadUInt32();
            if (magic == EndMagic)
                return;
            var flags = reader.ReadByte();
            switch (magic)
            {
                case SplitMagic:
                    throw new InvalidDataException($"{archiveName} is part of a split EGG archive; Filee cannot unpack split archives.");
                case SolidMagic:
                    throw new NotSupportedException($"{archiveName} is a solid EGG archive, which Filee cannot unpack.");
                case EncryptMagic:
                    throw ArchiveSafety.Encrypted(archiveName);
                case FileNameMagic when entry is not null:
                    var nameLength = (flags & 0x01) != 0 ? (int)reader.ReadUInt32() : reader.ReadUInt16();
                    if ((flags & 0x04) != 0)
                        throw ArchiveSafety.Encrypted(archiveName);
                    Encoding encoding = new UTF8Encoding(false);
                    if ((flags & 0x08) != 0)
                    {
                        var codePage = reader.ReadUInt16();
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        try
                        {
                            encoding = Encoding.GetEncoding(codePage);
                        }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                        {
                            encoding = Encoding.GetEncoding(949);
                        }
                    }
                    if ((flags & 0x10) != 0)
                        reader.ReadUInt32(); // parent path id; names already hold the full path
                    entry.Name = encoding.GetString(reader.ReadBytes(nameLength));
                    break;
                case WindowsInfoMagic when entry is not null:
                    var infoSize = ReadFieldSize(reader, flags);
                    var fileTime = reader.ReadInt64();
                    var attributes = reader.ReadByte();
                    reader.BaseStream.Seek(infoSize - 9, SeekOrigin.Current);
                    entry.Modified = fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : null;
                    entry.IsFolder |= (attributes & 0x10) != 0;
                    break;
                case PosixInfoMagic when entry is not null:
                    var posixSize = ReadFieldSize(reader, flags);
                    var mode = reader.ReadUInt32();
                    reader.BaseStream.Seek(posixSize - 4, SeekOrigin.Current);
                    entry.IsFolder |= (mode & 0xF000) == 0x4000;
                    break;
                default:
                    reader.BaseStream.Seek(ReadFieldSize(reader, flags), SeekOrigin.Current);
                    break;
            }
        }
    }

    /// <summary>Skips an extra field whose magic has been read.</summary>
    private static void SkipExtraField(BinaryReader reader, string archiveName)
    {
        var flags = reader.ReadByte();
        var size = ReadFieldSize(reader, flags);
        if (reader.BaseStream.Position + size > reader.BaseStream.Length)
            throw ArchiveSafety.Damaged(archiveName, "unknown EGG header");
        reader.BaseStream.Seek(size, SeekOrigin.Current);
    }

    private static long ReadFieldSize(BinaryReader reader, byte flags) =>
        (flags & 0x01) != 0 ? reader.ReadUInt32() : reader.ReadUInt16();
}

/// <summary>Forwards writes to a stream without closing it when disposed.</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
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

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
