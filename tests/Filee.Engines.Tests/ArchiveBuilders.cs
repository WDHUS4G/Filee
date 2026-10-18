// Builds archive test inputs in code: folder trees with Korean names, ZIPs (System.IO.Compression), ALZ and EGG
// files written per their format documentation (see AlzReader / EggReader), lzip files, and raw bzip2 / LZMA streams
// taken from what the bundled 7-Zip writes.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Archives;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

internal static class ArchiveBuilders
{
    /// <summary>A fixed modification time for checking that times survive.</summary>
    public static readonly DateTime KnownTime = new(2021, 5, 6, 7, 8, 10, DateTimeKind.Local);

    /// <summary>
    /// Creates <paramref name="name"/> below <paramref name="parent"/> with a Korean file name, a nested folder with
    /// a Korean name, an empty folder, and a file with <see cref="KnownTime"/>.
    /// </summary>
    public static string MakeTree(string parent, string name)
    {
        var root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "docs", "보고서"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "readme.txt"), "Filee archive test\n");
        File.WriteAllText(Path.Combine(root, "한글 문서.txt"), "안녕하세요, archive 文件");
        File.WriteAllText(Path.Combine(root, "docs", "보고서", "data.csv"), "a,b\n1,2\n");
        File.WriteAllBytes(Path.Combine(root, "docs", "noise.bin"), Noise(200_000, 7));
        File.SetLastWriteTime(Path.Combine(root, "readme.txt"), KnownTime);
        return root;
    }

    /// <summary>Relative path → SHA-256 of every file below <paramref name="root"/>, and "dir/" for empty folders.</summary>
    public static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[Relative(root, file)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                result[Relative(root, dir) + "/"] = "";
        return result;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Pseudo-random bytes (compress badly, like photos).</summary>
    public static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Text that compresses well but not trivially (for multi-block bzip2).</summary>
    public static byte[] Words(int length)
    {
        var sb = new StringBuilder();
        var random = new Random(42);
        string[] words = ["파일", "변환", "archive", "Filee", "압축", "folder", "데이터", "zip", "문서", "7-Zip", "\n"];
        while (sb.Length < length)
            sb.Append(words[random.Next(words.Length)]).Append(' ');
        return Encoding.UTF8.GetBytes(sb.ToString())[..length];
    }

    /// <summary>The bundled 7-Zip, or null when engines/7zip is missing.</summary>
    public static string? SevenZipExe => SevenZip.Locate()?.Executable;

    /// <summary>Runs 7-Zip with the given arguments and fails the test on an error.</summary>
    public static async Task Run7zAsync(params string[] args)
    {
        var result = await ProcessRunner.RunAsync(SevenZipExe!, args, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"7z {string.Join(' ', args)}: {result.StandardOutput} {result.StandardError}");
    }

    /// <summary>Standard bzip2 stream of <paramref name="data"/> written by 7-Zip at level 9 ("BZh9", 900k blocks).</summary>
    public static async Task<byte[]> Bzip2Async(byte[] data, string workDir)
    {
        var dir = Directory.CreateDirectory(Path.Combine(workDir, "bz-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var input = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(input, data);
        var output = Path.Combine(dir, "data.bz2");
        await Run7zAsync("a", "-tbzip2", "-mx9", "-mmt=off", output, input);
        return await File.ReadAllBytesAsync(output);
    }

    /// <summary>
    /// A raw LZMA stream (lc=3, lp=0, pb=2, 1 MiB dictionary) of <paramref name="data"/>: 7-Zip writes it into a .7z
    /// without header compression, where the only packed stream starts at offset 32 and is as long as the "next
    /// header offset" of the signature header says.
    /// </summary>
    public static async Task<byte[]> RawLzmaAsync(byte[] data, bool endMarker, string workDir)
    {
        var dir = Directory.CreateDirectory(Path.Combine(workDir, "lzma-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var input = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(input, data);
        var output = Path.Combine(dir, "data.7z");
        await Run7zAsync("a", "-t7z", "-m0=LZMA:d=1m:lc=3:lp=0:pb=2" + (endMarker ? ":eos" : ""), "-mhc=off", "-mf=off",
            "-ms=off", "-mmt=off", output, input);
        var archive = await File.ReadAllBytesAsync(output);
        var packedSize = (int)BitConverter.ToInt64(archive, 12);
        return archive[32..(32 + packedSize)];
    }

    /// <summary>An lzip member of <paramref name="data"/> (see LzipReader).</summary>
    public static async Task<byte[]> LzipAsync(byte[] data, string workDir)
    {
        var lzma = await RawLzmaAsync(data, endMarker: true, workDir);
        using var ms = new MemoryStream();
        ms.Write("LZIP"u8);
        ms.WriteByte(1);
        ms.WriteByte(20); // 2^20 = 1 MiB dictionary
        ms.Write(lzma);
        ms.Write(BitConverter.GetBytes(Crc32.Compute(data)));
        ms.Write(BitConverter.GetBytes((long)data.Length));
        ms.Write(BitConverter.GetBytes(6L + lzma.Length + 20));
        return ms.ToArray();
    }

    /// <summary>
    /// Rewrites a standard bzip2 stream into ALZip's framing: no "BZh9", "DLZ\x01" instead of each block's magic, CRC
    /// and randomised bit, "DLZ\x02" instead of the end magic and combined CRC. Blocks are found by their 48-bit magic.
    /// </summary>
    public static byte[] ToAlzBzip2(byte[] standard)
    {
        Assert.Equal("BZh9"u8.ToArray(), standard[..4]);
        var bits = new bool[standard.Length * 8];
        for (var i = 0; i < bits.Length; i++)
            bits[i] = (standard[i >> 3] & (0x80 >> (i & 7))) != 0;

        const ulong blockMagic = 0x314159265359, endMagic = 0x177245385090;
        ulong Read48(int at)
        {
            ulong v = 0;
            for (var i = 0; i < 48; i++)
                v = (v << 1) | (bits[at + i] ? 1UL : 0);
            return v;
        }

        var output = new List<bool>();
        void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                output.Add(((value >> i) & 1) != 0);
        }

        var pos = 32;
        while (true)
        {
            var magic = Read48(pos);
            if (magic == endMagic)
            {
                Write(0x444C5A02, 32);
                break;
            }
            Assert.Equal(blockMagic, magic);
            Write(0x444C5A01, 32);
            pos += 48 + 32 + 1;
            var next = pos;
            while (Read48(next) is not (blockMagic or endMagic))
                next++;
            output.AddRange(bits[pos..next]);
            pos = next;
        }

        var bytes = new byte[(output.Count + 7) / 8];
        for (var i = 0; i < output.Count; i++)
            if (output[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    /// <summary>One entry of a test ALZ file.</summary>
    /// <param name="Method">0 store, 1 ALZ bzip2 (<paramref name="Packed"/> must be given), 2 deflate.</param>
    public sealed record AlzItem(string Name, byte[]? Data, int Method = 0, byte[]? Packed = null, bool Encrypted = false);

    /// <summary>Writes an ALZ archive; names are stored in CP949 like ALZip does.</summary>
    public static void WriteAlz(string path, IEnumerable<AlzItem> items)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("ALZ\x01"u8);
        w.Write([0x0A, 0, 0, 0]);
        foreach (var item in items)
        {
            var name = cp949.GetBytes(item.Name);
            var packed = item.Data is null ? [] : item.Method switch
            {
                0 => item.Data,
                1 => item.Packed!,
                _ => Deflate(item.Data),
            };
            w.Write("BLZ\x01"u8);
            w.Write((ushort)name.Length);
            w.Write((byte)(item.Data is null ? 0x10 : 0x20));
            w.Write(DosTime(KnownTime));
            w.Write((byte)((item.Data is null ? 0x00 : 0x40) | (item.Encrypted ? 1 : 0)));
            w.Write((byte)0);
            if (item.Data is not null)
            {
                w.Write((byte)item.Method);
                w.Write((byte)0);
                w.Write(Crc32.Compute(item.Data));
                w.Write(packed.Length);
                w.Write(item.Data.Length);
            }
            w.Write(name);
            if (item.Encrypted)
                w.Write(new byte[12]);
            w.Write(packed);
        }
        w.Write("CLZ\x01"u8);
        w.Write(new byte[12]);
        w.Write("CLZ\x02"u8);
        w.Write(new byte[12]);
    }

    /// <summary>One block of a test EGG entry.</summary>
    /// <param name="Method">0 store, 1 deflate, 2 bzip2, 4 LZMA (<paramref name="Packed"/> given for 2 and 4).</param>
    public sealed record EggBlock(byte[] Data, int Method, byte[]? Packed = null);

    /// <summary>One entry of a test EGG file; no blocks for folders.</summary>
    public sealed record EggItem(string Name, bool Folder, params EggBlock[] Blocks);

    /// <summary>Writes an EGG archive (UTF-8 names, Windows file info with <see cref="KnownTime"/>).</summary>
    public static void WriteEgg(string path, IEnumerable<EggItem> items, bool solid = false)
    {
        const uint end = 0x08E28222;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x41474745u);
        w.Write((ushort)0x0100);
        w.Write(0x12345678u);
        w.Write(0u);
        if (solid)
        {
            w.Write(0x24E5A060u);
            w.Write((byte)0);
            w.Write((ushort)0);
        }
        w.Write(end);

        var id = 0u;
        foreach (var item in items)
        {
            w.Write(0x0A8590E3u);
            w.Write(id++);
            w.Write((long)item.Blocks.Sum(b => b.Data.Length));

            var name = Encoding.UTF8.GetBytes(item.Name);
            w.Write(0x0A8591ACu);
            w.Write((byte)0);
            w.Write((ushort)name.Length);
            w.Write(name);

            w.Write(0x2C86950Bu);
            w.Write((byte)0);
            w.Write((ushort)9);
            w.Write(KnownTime.ToFileTimeUtc());
            w.Write((byte)(item.Folder ? 0x10 : 0x20));
            w.Write(end);

            foreach (var block in item.Blocks)
            {
                var packed = block.Method switch
                {
                    0 => block.Data,
                    1 => Deflate(block.Data),
                    _ => block.Packed!,
                };
                w.Write(0x02B50C13u);
                w.Write((byte)block.Method);
                w.Write((byte)0);
                w.Write((uint)block.Data.Length);
                w.Write((uint)packed.Length);
                w.Write(Crc32.Compute(block.Data));
                w.Write(end);
                w.Write(packed);
            }
        }
        w.Write(end);
    }

    /// <summary>EGG's LZMA block layout: ZIP's 4-byte LZMA header, 5 property bytes, raw data.</summary>
    public static byte[] EggLzma(byte[] rawLzma)
    {
        byte[] header = [9, 20, 5, 0, 0x5D, 0x00, 0x00, 0x10, 0x00]; // version 9.20, 5 property bytes, 1 MiB dictionary
        return [.. header, .. rawLzma];
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return ms.ToArray();
    }

    private static uint DosTime(DateTime t) =>
        (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
}
