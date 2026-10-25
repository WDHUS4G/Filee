// Rewrites a ZIP written by System.IO.Compression the way other writers lay out Office files (issue #141): directory
// entries, data descriptors, uncompressed parts, bytes between entries, a name stored twice. System.IO.Compression
// reads them all; LibreOffice 26.2 refuses data descriptors after uncompressed entries (folders included), gaps and
// duplicate names. Also reads back the raw layout of a ZIP.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Archives;

namespace Filee.Engines.Tests;

public static class QuirkyZip
{
    [Flags]
    public enum Quirks
    {
        None = 0,

        /// <summary>A "folder/" entry for every folder, before the parts.</summary>
        DirectoryEntries = 1,

        /// <summary>Every entry flagged with bit 3: zero CRC and sizes in the local header, a data descriptor after the data.</summary>
        DataDescriptors = 2,

        /// <summary>Parts stored uncompressed instead of deflated.</summary>
        Stored = 4,

        /// <summary>16 zero bytes after every entry.</summary>
        Gaps = 8,

        /// <summary>The last part a second time under the same name.</summary>
        DuplicateName = 16,

        /// <summary>The parts in reverse order: [Content_Types].xml or mimetype last.</summary>
        Reversed = 32,
    }

    /// <summary>One local file header as written in the file.</summary>
    public readonly record struct LocalEntry(string Name, ushort Flags, ushort Method, uint Crc, uint CompressedSize, uint Size, int ExtraLength);

    /// <summary>The parts of a ZIP (directories left out), in order: name → content.</summary>
    public static List<(string Name, byte[] Data)> Parts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => (e.FullName, Read(e)))];
    }

    /// <summary>Writes the parts of <paramref name="source"/> to <paramref name="target"/> with <paramref name="quirks"/>.</summary>
    public static string Rewrite(string source, string target, Quirks quirks)
    {
        var parts = Parts(source).Select(p => (p.Name, (byte[]?)p.Data)).ToList();
        if (quirks.HasFlag(Quirks.Reversed))
            parts.Reverse();
        if (quirks.HasFlag(Quirks.DirectoryEntries))
        {
            var folders = parts.SelectMany(p => Folders(p.Name)).Distinct().Order(StringComparer.Ordinal).ToList();
            parts.InsertRange(0, folders.Select(f => (f, (byte[]?)null)));
        }
        if (quirks.HasFlag(Quirks.DuplicateName))
            parts.Add(parts[^1]);

        using var output = new MemoryStream();
        using var central = new MemoryStream();
        var writer = new BinaryWriter(output);
        var directory = new BinaryWriter(central);
        foreach (var (name, data) in parts)
        {
            var content = data ?? [];
            var stored = data is null || quirks.HasFlag(Quirks.Stored);
            var packed = stored ? content : Deflate(content);
            var crc = Crc32.Compute(content);
            var descriptor = quirks.HasFlag(Quirks.DataDescriptors);
            var flags = (ushort)((descriptor ? 0x0008 : 0) | 0x0800);
            var method = (ushort)(stored ? 0 : 8);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var offset = (uint)output.Position;

            writer.Write(0x04034b50u);
            writer.Write((ushort)20);
            writer.Write(flags);
            writer.Write(method);
            writer.Write(0u); // DOS time and date
            writer.Write(descriptor ? 0u : crc);
            writer.Write(descriptor ? 0u : (uint)packed.Length);
            writer.Write(descriptor ? 0u : (uint)content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Write(packed);
            if (descriptor)
            {
                writer.Write(0x08074b50u);
                writer.Write(crc);
                writer.Write((uint)packed.Length);
                writer.Write((uint)content.Length);
            }
            if (quirks.HasFlag(Quirks.Gaps))
                writer.Write(new byte[16]);

            directory.Write(0x02014b50u);
            directory.Write((ushort)0x031E);
            directory.Write((ushort)20);
            directory.Write(flags);
            directory.Write(method);
            directory.Write(0u);
            directory.Write(crc);
            directory.Write((uint)packed.Length);
            directory.Write((uint)content.Length);
            directory.Write((ushort)nameBytes.Length);
            directory.Write((ushort)0); // extra
            directory.Write((ushort)0); // comment
            directory.Write((ushort)0); // disk
            directory.Write((ushort)0); // internal attributes
            directory.Write(data is null ? 0x10u : 0u);
            directory.Write(offset);
            directory.Write(nameBytes);
        }
        var centralOffset = (uint)output.Position;
        writer.Write(central.ToArray());
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)parts.Count);
        writer.Write((ushort)parts.Count);
        writer.Write((uint)central.Length);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        writer.Flush();
        File.WriteAllBytes(target, output.ToArray());
        return target;
    }

    /// <summary>
    /// The local headers from the start of the file, each followed directly by its data (no data descriptors, no
    /// gaps), up to the central directory; fails when the layout is anything else.
    /// </summary>
    public static List<LocalEntry> LocalEntries(string path)
    {
        var bytes = File.ReadAllBytes(path).AsSpan();
        var entries = new List<LocalEntry>();
        var position = 0;
        while (BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]) == 0x04034b50)
        {
            var header = bytes[position..];
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var entry = new LocalEntry(
                Encoding.UTF8.GetString(header.Slice(30, nameLength)),
                BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[14..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[18..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[22..]),
                extraLength);
            entries.Add(entry);
            position += 30 + nameLength + extraLength + (int)entry.CompressedSize;
        }
        Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]));
        return entries;
    }

    private static IEnumerable<string> Folders(string name)
    {
        for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
            yield return name[..(slash + 1)];
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return buffer.ToArray();
    }
}
