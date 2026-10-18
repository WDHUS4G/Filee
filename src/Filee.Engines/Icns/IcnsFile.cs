// Reads and writes Apple icon files (.icns): a list of typed entries, each holding one icon size as PNG, JPEG 2000
// or (in old files) run-length encoded RGB plus a separate 8-bit mask. ImageMagick has no ICNS coder.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Icns;

/// <summary>One entry of an .icns file.</summary>
/// <param name="Type">Four-character type code, e.g. <c>"ic08"</c>.</param>
/// <param name="Data">Entry payload (without the 8-byte entry header).</param>
internal sealed record IcnsEntry(string Type, byte[] Data);

/// <summary>A decoded icon: straight (non-premultiplied) RGBA pixels, row by row.</summary>
internal sealed record IcnsBitmap(int Width, int Height, byte[] Rgba);

/// <summary>ICNS container parsing, writing and the legacy RLE codec.</summary>
internal static class IcnsFile
{
    /// <summary>Entry types written for a new icon, smallest first, with their pixel size (iconutil's set).</summary>
    public static readonly (string Type, int Size)[] WrittenTypes =
    [
        ("icp4", 16),
        ("ic11", 32),   // 16 pt @2x
        ("icp5", 32),
        ("ic12", 64),   // 32 pt @2x
        ("ic07", 128),
        ("ic13", 256),  // 128 pt @2x
        ("ic08", 256),
        ("ic14", 512),  // 256 pt @2x
        ("ic09", 512),
        ("ic10", 1024), // 512 pt @2x
    ];

    /// <summary>Pixel size of the image types that carry PNG or JPEG 2000 data (or ARGB for ic04/ic05).</summary>
    private static readonly Dictionary<string, int> ImageTypes = new(StringComparer.Ordinal)
    {
        ["icp4"] = 16,
        ["icp5"] = 32,
        ["icp6"] = 64,
        ["ic07"] = 128,
        ["ic08"] = 256,
        ["ic09"] = 512,
        ["ic10"] = 1024,
        ["ic11"] = 32,
        ["ic12"] = 64,
        ["ic13"] = 256,
        ["ic14"] = 512,
        ["ic04"] = 16,
        ["ic05"] = 32,
        ["icsb"] = 18,
        ["icsB"] = 36,
        ["sb24"] = 24,
        ["SB24"] = 48,
    };

    /// <summary>Legacy RLE RGB types, their size and the type of their 8-bit mask.</summary>
    private static readonly Dictionary<string, (int Size, string Mask)> LegacyTypes = new(StringComparer.Ordinal)
    {
        ["is32"] = (16, "s8mk"),
        ["il32"] = (32, "l8mk"),
        ["ih32"] = (48, "h8mk"),
        ["it32"] = (128, "t8mk"),
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Jp2Signature => [0, 0, 0, 0x0C, (byte)'j', (byte)'P', 0x20, 0x20];
    private static ReadOnlySpan<byte> J2kSignature => [0xFF, 0x4F, 0xFF, 0x51];

    // ───────────────────────── Container ─────────────────────────

    /// <summary>Parses the top-level entries of an .icns file.</summary>
    /// <exception cref="InvalidDataException">The data is not an ICNS file.</exception>
    public static List<IcnsEntry> Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8 || !file[..4].SequenceEqual("icns"u8))
            throw new InvalidDataException("Not an ICNS file (the 'icns' signature is missing).");
        var declared = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
        var end = (int)Math.Min(declared, (uint)file.Length);

        var entries = new List<IcnsEntry>();
        for (var offset = 8; offset + 8 <= end;)
        {
            var type = Encoding.Latin1.GetString(file.Slice(offset, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(file[(offset + 4)..]);
            if (length < 8 || offset + length > end)
                break; // truncated or corrupt: keep what was readable
            entries.Add(new IcnsEntry(type, file.Slice(offset + 8, (int)length - 8).ToArray()));
            offset += (int)length;
        }
        return entries;
    }

    /// <summary>Writes entries, preceded by a table of contents like iconutil does.</summary>
    public static byte[] Write(IReadOnlyList<IcnsEntry> entries)
    {
        var tocLength = 8 + 8 * entries.Count;
        var total = 8 + tocLength + entries.Sum(e => 8 + e.Data.Length);
        var buffer = new byte[total];
        var span = buffer.AsSpan();
        "icns"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)total);

        var offset = 8;
        "TOC "u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], (uint)tocLength);
        offset += 8;
        foreach (var entry in entries)
        {
            Encoding.Latin1.GetBytes(entry.Type, span.Slice(offset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], (uint)(8 + entry.Data.Length));
            offset += 8;
        }
        foreach (var entry in entries)
        {
            Encoding.Latin1.GetBytes(entry.Type, span.Slice(offset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], (uint)(8 + entry.Data.Length));
            entry.Data.CopyTo(span[(offset + 8)..]);
            offset += 8 + entry.Data.Length;
        }
        return buffer;
    }

    // ───────────────────────── Choosing an image ─────────────────────────

    /// <summary>An icon image found in the file, not decoded yet.</summary>
    /// <param name="Size">Nominal pixel size from the entry type.</param>
    /// <param name="Encoded">PNG or JPEG 2000 bytes, decoded by ImageMagick; null for legacy/ARGB data.</param>
    /// <param name="Decode">Decoder for legacy/ARGB data.</param>
    public sealed record Candidate(string Type, int Size, byte[]? Encoded, Func<IcnsBitmap>? Decode);

    /// <summary>All images of the file, largest first (PNG / JPEG 2000 before legacy data of the same size).</summary>
    public static List<Candidate> Candidates(IReadOnlyList<IcnsEntry> entries)
    {
        var byType = entries.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<Candidate>();
        foreach (var entry in entries)
        {
            if (ImageTypes.TryGetValue(entry.Type, out var size))
            {
                if (IsEncodedImage(entry.Data))
                    result.Add(new Candidate(entry.Type, size, entry.Data, null));
                else if (entry.Data.AsSpan().StartsWith("ARGB"u8))
                    result.Add(new Candidate(entry.Type, size, null, () => DecodeArgb(entry.Data, size)));
            }
            else if (LegacyTypes.TryGetValue(entry.Type, out var legacy))
            {
                var mask = byType.GetValueOrDefault(legacy.Mask)?.Data;
                result.Add(new Candidate(entry.Type, legacy.Size, null, () => DecodeLegacy(entry.Type, entry.Data, mask, legacy.Size)));
            }
        }
        return result.OrderByDescending(c => c.Size).ThenBy(c => c.Encoded is null ? 1 : 0).ToList();
    }

    /// <summary>True for PNG and JPEG 2000 (JP2 container or raw codestream) data.</summary>
    public static bool IsEncodedImage(ReadOnlySpan<byte> data) =>
        data.StartsWith(PngSignature) || data.StartsWith(Jp2Signature) || data.StartsWith(J2kSignature);

    // ───────────────────────── Legacy codecs ─────────────────────────

    /// <summary>
    /// Decodes is32/il32/ih32/it32: RGB stored channel after channel (all red, then green, then blue), each run-length
    /// encoded, plus an optional 8-bit mask entry as alpha. it32 data starts with four zero bytes. Data exactly
    /// 4 × size² bytes long is uncompressed ARGB, which some writers use for is32/il32.
    /// </summary>
    public static IcnsBitmap DecodeLegacy(string type, byte[] data, byte[]? mask, int size)
    {
        var pixels = size * size;
        var rgba = new byte[pixels * 4];
        if (data.Length == pixels * 4)
        {
            for (var i = 0; i < pixels; i++)
            {
                rgba[i * 4] = data[i * 4 + 1];
                rgba[i * 4 + 1] = data[i * 4 + 2];
                rgba[i * 4 + 2] = data[i * 4 + 3];
            }
        }
        else
        {
            var planes = UnpackBits(data.AsSpan(type == "it32" ? 4 : 0), pixels * 3);
            for (var i = 0; i < pixels; i++)
            {
                rgba[i * 4] = planes[i];
                rgba[i * 4 + 1] = planes[pixels + i];
                rgba[i * 4 + 2] = planes[2 * pixels + i];
            }
        }
        for (var i = 0; i < pixels; i++)
            rgba[i * 4 + 3] = mask is not null && mask.Length >= pixels ? mask[i] : (byte)255;
        return new IcnsBitmap(size, size, rgba);
    }

    /// <summary>Decodes ic04/ic05 "ARGB" entries: the tag, then A, R, G and B planes run-length encoded.</summary>
    public static IcnsBitmap DecodeArgb(byte[] data, int size)
    {
        var pixels = size * size;
        var planes = UnpackBits(data.AsSpan(4), pixels * 4);
        var rgba = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            rgba[i * 4] = planes[pixels + i];
            rgba[i * 4 + 1] = planes[2 * pixels + i];
            rgba[i * 4 + 2] = planes[3 * pixels + i];
            rgba[i * 4 + 3] = planes[i];
        }
        return new IcnsBitmap(size, size, rgba);
    }

    /// <summary>
    /// Apple's icon RLE: a control byte below 0x80 copies the next (n + 1) bytes, a control byte of 0x80 or more
    /// repeats the next byte (n − 125) times. Missing data (truncated files) stays zero.
    /// </summary>
    public static byte[] UnpackBits(ReadOnlySpan<byte> source, int length)
    {
        var output = new byte[length];
        var written = 0;
        var i = 0;
        while (written < length && i < source.Length)
        {
            int control = source[i++];
            if (control < 0x80)
            {
                var count = Math.Min(control + 1, Math.Min(length - written, source.Length - i));
                source.Slice(i, count).CopyTo(output.AsSpan(written));
                i += control + 1;
                written += count;
            }
            else
            {
                if (i >= source.Length)
                    break;
                var value = source[i++];
                var count = Math.Min(control - 125, length - written);
                output.AsSpan(written, count).Fill(value);
                written += count;
            }
        }
        return output;
    }
}
