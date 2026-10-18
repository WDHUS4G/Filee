// MOBI / AZW / AZW3 / PRC → Book, and the PDF inside AZW4 (Print Replica). Written from the MOBI format description
// on the MobileRead wiki (no code from KindleUnpack or calibre):
//  * PalmDB records; record 0 holds the PalmDOC header, the MOBI header and EXTH metadata (title, authors, cover);
//  * text records are PalmDOC (LZ77) or HUFF/CDIC compressed, with trailing entries stripped first;
//  * MOBI 6 (KF7): one HTML document; filepos links become anchors, recindex pictures come from image records;
//  * KF8 (AZW3, also the KF8 half of joint MOBI files): the text is cut into files by the skeleton (SKEL) and
//    fragment (FRAG) indexes; kindle:pos, kindle:embed and kindle:flow references are resolved;
//  * plain PalmDOC books (TEXtREAd) are text.
// Books with DRM are refused with a clear message; KFX and Topaz books are recognised and refused too.

using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static partial class MobiReader
{
    /// <summary>Reads a Kindle book's content (for the reader registry of the HWPX writer).</summary>
    public static HDocument Read(string path, string workFolder) => ReadBook(path, workFolder).Document;

    public static Book ReadBook(string path, string workFolder, CancellationToken cancellationToken = default)
    {
        var database = Open(path);
        var folder = EbookFiles.NewFolder(workFolder, "mobi");
        if (database.TypeCreator == "TEXtREAd")
            return new Book(PlainText.ToDocument(DecodeText(PalmDocText(database))), new BookMetadata { Title = database.Name });

        var header = MobiHeader.Parse(database, 0);
        if (header.Encryption != 0)
            throw new InvalidOperationException(EbookFiles.DrmMessage);
        var metadata = header.Metadata(database.Name);

        HDocument document;
        Resources resources;
        var kf8Start = header.Version >= 8 ? 0 : header.Kf8Boundary;
        if (kf8Start is { } start && start >= 0 && start < database.Count)
        {
            var kf8 = start == 0 ? header : MobiHeader.Parse(database, start);
            if (kf8.Encryption != 0)
                throw new InvalidOperationException(EbookFiles.DrmMessage);
            // Joint files keep the pictures once, before the KF8 half, numbered from the MOBI 6 header.
            resources = new Resources(database, start == 0 ? kf8.FirstImage : header.FirstImage, folder);
            document = Kf8(database, kf8, start, resources, folder, cancellationToken);
        }
        else
        {
            resources = new Resources(database, header.FirstImage, folder);
            document = Mobi6(database, header, resources, folder, cancellationToken);
        }

        if (header.CoverOffset is { } cover && resources.Save(cover) is { } coverFile)
            metadata.CoverImage = coverFile;
        document.Title = metadata.Title;
        return new Book(document, metadata);
    }

    /// <summary>The first PDF of an AZW4 (Print Replica) book.</summary>
    public static byte[] PrintReplicaPdf(string path)
    {
        var database = Open(path);
        var header = MobiHeader.Parse(database, 0);
        if (header.Encryption != 0)
            throw new InvalidOperationException(EbookFiles.DrmMessage);
        var text = Text(database, header, 0);
        if (text.AsSpan().StartsWith("%MOP"u8) && text.Length >= 12)
        {
            // "%MOP", table count, section count per table, then (offset, length) per section; the first is the PDF.
            var tables = BinaryPrimitives.ReadUInt32BigEndian(text.AsSpan(4));
            var index = 8 + 4 * (int)Math.Min(tables, 1000);
            var offset = (int)PalmDatabase.U32(text, index);
            var length = (int)PalmDatabase.U32(text, index + 4);
            if (offset > 0 && length > 0 && offset + length <= text.Length)
                return text.AsSpan(offset, length).ToArray();
        }
        var pdf = text.AsSpan().IndexOf("%PDF-"u8);
        if (pdf < 0)
            throw new InvalidDataException("This book is not a Print Replica (AZW4) book: it contains no PDF.");
        return text.AsSpan(pdf).ToArray();
    }

    private static PalmDatabase Open(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data is [(byte)'C', (byte)'O', (byte)'N', (byte)'T', ..] or [0xEA, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', ..])
            throw new InvalidDataException("This is a KFX book (Kindle's newest format), which cannot be converted. Download it from Amazon as an AZW3 or MOBI file (\"Download & transfer via USB\").");
        if (data.AsSpan().StartsWith("TPZ"u8))
            throw new InvalidDataException("This is a Topaz book, which cannot be converted.");
        var database = PalmDatabase.Open(data);
        if (database.TypeCreator is not ("BOOKMOBI" or "TEXtREAd"))
            throw new InvalidDataException($"This is not a Mobipocket or Kindle book (type \"{database.TypeCreator}\").");
        return database;
    }

    // ───────────────────────── Text records ─────────────────────────

    /// <summary>The decompressed text of the book whose header is at record <paramref name="start"/>.</summary>
    private static byte[] Text(PalmDatabase database, MobiHeader header, int start)
    {
        Func<ReadOnlySpan<byte>, byte[]> decompress = header.Compression switch
        {
            1 => data => data.ToArray(),
            2 => PalmDocCompression.Decompress,
            17480 => HuffCdicReader(database, header, start).Decompress,
            var other => throw new InvalidDataException($"The book uses an unknown compression ({other})."),
        };
        using var text = new MemoryStream();
        for (var i = 1; i <= header.TextRecords; i++)
        {
            var record = database.Record(start + i);
            var size = record.Length - TrailingSize(record, header.ExtraFlags);
            text.Write(decompress(record[..Math.Max(0, size)]));
        }
        var bytes = text.ToArray();
        return header.TextLength > 0 && header.TextLength < bytes.Length ? bytes[..(int)header.TextLength] : bytes;
    }

    private static HuffCdic HuffCdicReader(PalmDatabase database, MobiHeader header, int start)
    {
        var huff = start + header.HuffRecord;
        return new HuffCdic(database.Record(huff), Enumerable.Range(huff + 1, Math.Max(0, header.HuffCount - 1)).Select(database.RecordArray));
    }

    /// <summary>
    /// Size of the extra data at the end of a text record (multibyte overlap and indexing entries), described by the
    /// header's extra flags: each set bit above bit 0 adds an entry whose size is a backward varint at the end.
    /// </summary>
    internal static int TrailingSize(ReadOnlySpan<byte> record, int flags)
    {
        var size = 0;
        for (var bits = flags >> 1; bits != 0; bits >>= 1)
        {
            if ((bits & 1) == 0)
                continue;
            // Backward varint: 7 bits per byte read from the end, the first byte (from the start) has the high bit set.
            var value = 0;
            var shift = 0;
            for (var p = record.Length - size - 1; p >= 0; p--)
            {
                var b = record[p];
                value |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) != 0 || shift >= 28 || p == 0)
                    break;
            }
            size += value;
        }
        if ((flags & 1) != 0 && record.Length - size - 1 >= 0)
            size += (record[record.Length - size - 1] & 0x3) + 1;
        return Math.Clamp(size, 0, record.Length);
    }

    private static byte[] PalmDocText(PalmDatabase database)
    {
        var header = database.Record(0);
        var compression = PalmDatabase.U16(header, 0);
        var records = PalmDatabase.U16(header, 8);
        using var text = new MemoryStream();
        for (var i = 1; i <= records && i < database.Count; i++)
            text.Write(compression == 2 ? PalmDocCompression.Decompress(database.Record(i)) : database.Record(i));
        return text.ToArray();
    }

    /// <summary>UTF-8 when valid, else Windows-1252 (the Mobipocket default).</summary>
    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    // ───────────────────────── MOBI 6 ─────────────────────────

    private static HDocument Mobi6(PalmDatabase database, MobiHeader header, Resources resources, string folder, CancellationToken cancellationToken)
    {
        // Latin-1 keeps one character per byte, so filepos values (byte offsets) index the string directly.
        var text = Encoding.Latin1.GetString(Text(database, header, 0));
        cancellationToken.ThrowIfCancellationRequested();

        var positions = FilePos().Matches(text).Select(m => long.Parse(m.Groups[1].Value)).Where(p => p <= text.Length).Distinct().OrderDescending();
        var sb = new StringBuilder(text);
        foreach (var position in positions)
        {
            // A position inside a tag moves to the start of that tag.
            var at = (int)position;
            var open = at > 0 ? text.LastIndexOf('<', at - 1) : -1;
            var close = at > 0 ? text.LastIndexOf('>', at - 1) : -1;
            if (open > close)
                at = open;
            sb.Insert(at, $"<a id=\"filepos{position}\"></a>");
        }
        var html = FilePos().Replace(sb.ToString(), m => $"href=\"#filepos{long.Parse(m.Groups[1].Value)}\"");
        html = RecIndex().Replace(html, m => resources.Save(int.Parse(m.Groups[1].Value) - 1) is { } file ? $"src=\"{Path.GetFileName(file)}\"" : "");

        var encoding = header.Codepage == 65001 ? Encoding.UTF8 : CodePage(header.Codepage);
        var page = Path.Combine(folder, "book.html");
        File.WriteAllText(page, encoding.GetString(Encoding.Latin1.GetBytes(html)), new UTF8Encoding(true));
        return ChapterReader.Read([page], Path.Combine(folder, "~media"), cancellationToken);
    }

    private static Encoding CodePage(int codepage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(codepage is 0 ? 1252 : codepage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    // Digit counts are capped so the numbers always fit (filepos values are 10 digits, recindex 5).
    [GeneratedRegex(@"\bfilepos\s*=\s*['""]?0*(\d{1,12})['""]?", RegexOptions.IgnoreCase)]
    private static partial Regex FilePos();

    [GeneratedRegex(@"\b(?:hi|lo)?recindex\s*=\s*['""]?(\d{1,9})['""]?", RegexOptions.IgnoreCase)]
    private static partial Regex RecIndex();

    // ───────────────────────── KF8 ─────────────────────────

    private static HDocument Kf8(PalmDatabase database, MobiHeader header, int start, Resources resources, string folder, CancellationToken cancellationToken)
    {
        var text = Text(database, header, start);
        cancellationToken.ThrowIfCancellationRequested();

        // Flows: flow 0 is the XHTML text, the others are style sheets and SVG images.
        var flows = new List<byte[]>();
        if (header.Fdst >= 0 && database.Record(start + (int)header.Fdst) is var fdst && fdst.Length >= 12 && fdst[..4].SequenceEqual("FDST"u8))
        {
            var tableOffset = (int)PalmDatabase.U32(fdst, 4);
            var count = (int)PalmDatabase.U32(fdst, 8);
            for (var i = 0; i < count; i++)
            {
                var from = (int)Math.Clamp(PalmDatabase.U32(fdst, tableOffset + i * 8), 0, text.Length);
                var to = (int)Math.Clamp(PalmDatabase.U32(fdst, tableOffset + i * 8 + 4), from, text.Length);
                flows.Add(text[from..to]);
            }
        }
        if (flows.Count == 0)
            flows.Add(text);
        var markup = flows[0];

        var strings = new Dictionary<long, string>();
        var skeletons = header.Skeleton >= 0 ? MobiIndex.Read(database, start + (int)header.Skeleton, strings) : [];
        var fragments = header.Fragment >= 0 ? MobiIndex.Read(database, start + (int)header.Fragment, strings) : [];
        var parts = Assemble(markup, skeletons, fragments, strings);

        // Work on Latin-1 strings: positions in kindle:pos links are byte offsets.
        var texts = parts.Select(p => Encoding.Latin1.GetString(p.Text)).ToList();
        var insertPositions = fragments.Select(f => long.TryParse(f.Key, out var p) ? p : 0).ToList();
        var linkedAids = new HashSet<string>(StringComparer.Ordinal);
        string Resolve(Match m)
        {
            var fragment = (int)Base32(m.Groups[1].Value);
            var position = (fragment < insertPositions.Count ? insertPositions[fragment] : 0) + Base32(m.Groups[2].Value);
            var part = parts.FindIndex(p => position >= p.Start && position < p.Start + p.Text.Length);
            if (part < 0)
                return PartName(0);
            var id = IdBefore(texts[part], (int)(position - parts[part].Start), linkedAids);
            return id.Length > 0 ? $"{PartName(part)}#{id}" : PartName(part);
        }

        var files = new List<string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var xhtml = KindlePos().Replace(texts[i], Resolve);
            xhtml = KindleEmbed().Replace(xhtml, m => resources.Save((int)Base32(m.Groups[1].Value) - 1) is { } file ? Path.GetFileName(file) : "missing");
            xhtml = KindleFlow().Replace(xhtml, m => Flow(flows, (int)Base32(m.Groups[1].Value), m.Groups[2].Value, folder));
            texts[i] = xhtml;
        }
        for (var i = 0; i < texts.Count; i++)
        {
            // Links that point at an element by its Amazon "aid" get an id to land on.
            var xhtml = linkedAids.Count == 0 ? texts[i] : Aid().Replace(texts[i], m => linkedAids.Contains(m.Groups[1].Value) ? $"{m.Value} id=\"aid-{m.Groups[1].Value}\"" : m.Value);
            var file = Path.Combine(folder, PartName(i));
            File.WriteAllBytes(file, [.. Encoding.UTF8.Preamble, .. Encoding.Latin1.GetBytes(xhtml)]);
            files.Add(file);
        }
        return ChapterReader.Read(files, Path.Combine(folder, "~media"), cancellationToken);
    }

    private static string PartName(int index) => $"part{index:0000}.xhtml";

    /// <summary>
    /// Rebuilds the XHTML files: each skeleton is followed in the text by its fragments, which are inserted into it
    /// at their insert positions (positions in the rebuilt file, so the skeleton's own start is subtracted).
    /// </summary>
    private static List<(byte[] Text, long Start)> Assemble(byte[] markup, List<MobiIndexEntry> skeletons, List<MobiIndexEntry> fragments, Dictionary<long, string> strings)
    {
        if (skeletons.Count == 0)
            return [(markup, 0)];
        var parts = new List<(byte[], long)>();
        var next = 0;
        foreach (var skeleton in skeletons)
        {
            var skeletonStart = Math.Clamp(skeleton.Tag(6, 0), 0, markup.Length);
            var skeletonLength = Math.Clamp(skeleton.Tag(6, 1), 0, markup.Length - skeletonStart);
            var part = new List<byte>(markup.AsSpan((int)skeletonStart, (int)skeletonLength).ToArray());
            var position = skeletonStart + skeletonLength;
            var count = Math.Max(0, skeleton.Tag(1));
            for (var i = 0; i < count && next < fragments.Count; i++, next++)
            {
                var fragment = fragments[next];
                var length = (int)Math.Clamp(fragment.Tag(6, 1), 0, markup.Length - position);
                var slice = markup.AsSpan((int)position, length).ToArray();
                position += length;
                var insert = (int)Math.Clamp((long.TryParse(fragment.Key, out var p) ? p : 0) - skeletonStart, 0, part.Count);
                if (InsideTag(part, insert))
                    insert = AfterAidTag(part, strings.GetValueOrDefault(fragment.Tag(2), "")) ?? NextTagEnd(part, insert);
                part.InsertRange(insert, slice);
            }
            parts.Add(([.. part], skeletonStart));
        }
        return parts;
    }

    private static bool InsideTag(List<byte> text, int position)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (text[i] == '>')
                return false;
            if (text[i] == '<')
                return true;
        }
        return false;
    }

    private static int NextTagEnd(List<byte> text, int position)
    {
        var end = text.IndexOf((byte)'>', position);
        return end < 0 ? text.Count : end + 1;
    }

    /// <summary>Insert position after the tag named by a fragment selector such as P-//*[@aid='3'].</summary>
    private static int? AfterAidTag(List<byte> text, string selector)
    {
        var match = SelectorAid().Match(selector);
        if (!match.Success)
            return null;
        var needle = Encoding.ASCII.GetBytes($"aid=\"{match.Groups[1].Value}\"");
        var at = text.ToArray().AsSpan().IndexOf(needle);
        return at < 0 ? null : NextTagEnd(text, at);
    }

    /// <summary>
    /// The id a link to <paramref name="position"/> lands on: the nearest id or name attribute of a tag before it
    /// (an "aid" is noted so an id can be added), or "" for the top of the file.
    /// </summary>
    private static string IdBefore(string text, int position, HashSet<string> linkedAids)
    {
        position = Math.Clamp(position, 0, text.Length);
        var open = text.IndexOf('<', position);
        var close = text.IndexOf('>', position);
        if (close >= 0 && (open == position || open < 0 || close < open))
            position = close + 1; // inside a tag (or at its start): that tag counts
        var end = position;
        while (end > 0)
        {
            var tagEnd = text.LastIndexOf('>', end - 1);
            if (tagEnd < 0)
                break;
            var tagStart = text.LastIndexOf('<', tagEnd);
            if (tagStart < 0)
                break;
            var tag = text[tagStart..(tagEnd + 1)];
            end = tagStart;
            if (tag.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
                return "";
            if (tag.StartsWith("<meta", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IdAttribute().Match(tag) is { Success: true } id)
                return id.Groups[1].Value;
            if (AidAttribute().Match(tag) is { Success: true } aid)
            {
                linkedAids.Add(aid.Groups[1].Value);
                return "aid-" + aid.Groups[1].Value;
            }
        }
        return "";
    }

    private static string Flow(List<byte[]> flows, int index, string mime, string folder)
    {
        if (index <= 0 || index >= flows.Count)
            return "missing";
        var extension = mime.Contains("svg", StringComparison.OrdinalIgnoreCase) ? "svg" : mime.Contains("css", StringComparison.OrdinalIgnoreCase) ? "css" : "txt";
        var name = $"flow{index:0000}.{extension}";
        var file = Path.Combine(folder, name);
        if (!File.Exists(file))
            File.WriteAllBytes(file, flows[index]);
        return name;
    }

    /// <summary>Kindle's base-32 numbers: digits 0-9 then A-V.</summary>
    internal static long Base32(string text)
    {
        long value = 0;
        foreach (var ch in text.ToUpperInvariant())
            value = value * 32 + (ch <= '9' ? ch - '0' : ch - 'A' + 10);
        return value;
    }

    [GeneratedRegex(@"kindle:pos:fid:([0-9A-Va-v]{4}):off:([0-9A-Va-v]{10})")]
    private static partial Regex KindlePos();

    [GeneratedRegex(@"kindle:embed:([0-9A-Va-v]{4})(?:\?mime=[^'""\)\s]*)?")]
    private static partial Regex KindleEmbed();

    [GeneratedRegex(@"kindle:flow:([0-9A-Va-v]{4})(?:\?mime=([^'""\)\s]*))?")]
    private static partial Regex KindleFlow();

    [GeneratedRegex(@"\said\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex Aid();

    [GeneratedRegex(@"^<[^>]*\s(?:id|name)\s*=\s*['""]([^'""]*)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"^<[^>]+\said\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex AidAttribute();

    [GeneratedRegex(@"@aid='([^']+)'")]
    private static partial Regex SelectorAid();

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>Picture records, numbered from the first image record; written to files on first use.</summary>
    private sealed class Resources(PalmDatabase database, long firstImage, string folder)
    {
        private readonly Dictionary<int, string?> _saved = [];

        /// <summary>Saves resource <paramref name="index"/> (0-based) and returns its file, or null if it is no picture.</summary>
        public string? Save(int index)
        {
            if (firstImage < 0 || index < 0)
                return null;
            if (_saved.TryGetValue(index, out var known))
                return known;
            var record = database.Record((int)firstImage + index);
            var extension = record switch
            {
                [0xFF, 0xD8, 0xFF, ..] => "jpg",
                [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "png",
                [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "gif",
                [(byte)'B', (byte)'M', ..] => "bmp",
                _ => null,
            };
            string? file = null;
            if (extension is not null)
            {
                file = Path.Combine(folder, $"image{index + 1:00000}.{extension}");
                File.WriteAllBytes(file, record.ToArray());
            }
            _saved[index] = file;
            return file;
        }
    }
}

/// <summary>The MOBI header of record 0 (or of the KF8 half of a joint file) and its EXTH metadata.</summary>
internal sealed class MobiHeader
{
    public int Compression { get; private init; }
    public long TextLength { get; private init; }
    public int TextRecords { get; private init; }
    public int Encryption { get; private init; }
    public int Codepage { get; private init; } = 1252;
    public long Version { get; private init; }
    public long FirstImage { get; private init; } = -1;
    public int HuffRecord { get; private init; }
    public int HuffCount { get; private init; }
    public int ExtraFlags { get; private init; }
    public long Fdst { get; private init; } = -1;
    public long Fragment { get; private init; } = -1;
    public long Skeleton { get; private init; } = -1;

    /// <summary>Record of the KF8 header in a joint MOBI 6 + KF8 file (EXTH 121).</summary>
    public int? Kf8Boundary { get; private set; }

    /// <summary>Cover picture as an offset from the first image record (EXTH 201).</summary>
    public int? CoverOffset { get; private set; }

    private string? _fullName;
    private int _locale;
    private readonly Dictionary<int, List<byte[]>> _exth = [];

    public static MobiHeader Parse(PalmDatabase database, int record)
    {
        var data = database.Record(record);
        if (data.Length < 16)
            throw new InvalidDataException("The book's header is damaged.");
        var hasMobi = data.Length >= 24 && data[16..20].SequenceEqual("MOBI"u8);
        var length = hasMobi ? (int)PalmDatabase.U32(data, 20) : 0;
        var header = new MobiHeader
        {
            Compression = PalmDatabase.U16(data, 0),
            TextLength = PalmDatabase.U32(data, 4),
            TextRecords = PalmDatabase.U16(data, 8),
            Encryption = PalmDatabase.U16(data, 12),
            Codepage = hasMobi ? (int)PalmDatabase.U32(data, 28) : 1252,
            Version = hasMobi ? PalmDatabase.U32(data, 36) : 0,
            FirstImage = hasMobi ? PalmDatabase.U32(data, 108) : -1,
            HuffRecord = hasMobi ? (int)Math.Max(0, PalmDatabase.U32(data, 112)) : 0,
            HuffCount = hasMobi ? (int)Math.Max(0, PalmDatabase.U32(data, 116)) : 0,
            ExtraFlags = hasMobi && length >= 0xE4 ? PalmDatabase.U16(data, 0xF2) : 0,
            Fdst = hasMobi && length >= 0xE4 ? PalmDatabase.U32(data, 0xC0) : -1,
            Fragment = hasMobi && length >= 0xE8 ? PalmDatabase.U32(data, 0xF8) : -1,
            Skeleton = hasMobi && length >= 0xEC ? PalmDatabase.U32(data, 0xFC) : -1,
        };
        if (!hasMobi)
            return header;
        // MOBI 6 files reuse 0xC0 for "first content record": only KF8 headers have an FDST index there.
        if (header.Version < 8)
            header = header.WithoutKf8Fields();

        var nameOffset = (int)PalmDatabase.U32(data, 84);
        var nameLength = (int)PalmDatabase.U32(data, 88);
        header._locale = (int)Math.Max(0, PalmDatabase.U32(data, 92));
        if (nameOffset > 0 && nameLength > 0 && nameOffset + nameLength <= data.Length)
            header._fullName = header.Decode(data.Slice(nameOffset, nameLength).ToArray());

        if ((PalmDatabase.U32(data, 128) & 0x40) != 0 && 16 + length + 12 <= data.Length && data.Slice(16 + length, 4).SequenceEqual("EXTH"u8))
        {
            var exth = 16 + length;
            var count = (int)PalmDatabase.U32(data, exth + 8);
            var position = exth + 12;
            for (var i = 0; i < count && position + 8 <= data.Length; i++)
            {
                var type = (int)PalmDatabase.U32(data, position);
                var size = (int)PalmDatabase.U32(data, position + 4);
                if (size < 8 || position + size > data.Length)
                    break;
                if (!header._exth.TryGetValue(type, out var values))
                    header._exth[type] = values = [];
                values.Add(data.Slice(position + 8, size - 8).ToArray());
                position += size;
            }
            header.Kf8Boundary = header.Number(121) is { } boundary and > 0 ? boundary : null;
            header.CoverOffset = header.Number(201);
        }
        return header;
    }

    private MobiHeader WithoutKf8Fields() => new()
    {
        Compression = Compression,
        TextLength = TextLength,
        TextRecords = TextRecords,
        Encryption = Encryption,
        Codepage = Codepage,
        Version = Version,
        FirstImage = FirstImage,
        HuffRecord = HuffRecord,
        HuffCount = HuffCount,
        ExtraFlags = ExtraFlags,
    };

    /// <summary>Title (EXTH 503, else the full name, else the database name), authors, publisher, language.</summary>
    public BookMetadata Metadata(string databaseName)
    {
        var metadata = new BookMetadata
        {
            Title = Strings(503).FirstOrDefault() ?? _fullName ?? databaseName.Replace('_', ' '),
            Publisher = Strings(101).FirstOrDefault(),
            Description = Strings(103).FirstOrDefault(),
            Language = Strings(524).FirstOrDefault() ?? Language(_locale),
        };
        foreach (var author in Strings(100))
            foreach (var name in author.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!metadata.Authors.Contains(name))
                    metadata.Authors.Add(name);
        return metadata;
    }

    private IEnumerable<string> Strings(int type) =>
        _exth.TryGetValue(type, out var values) ? values.Select(Decode).Select(s => s.Trim('\0', ' ')).Where(s => s.Length > 0) : [];

    private int? Number(int type) =>
        _exth.TryGetValue(type, out var values) && values[0].Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(values[0]) is var value && value != uint.MaxValue
            ? (int)Math.Min(value, int.MaxValue)
            : null;

    private string Decode(byte[] bytes)
    {
        if (Codepage == 65001)
            return Encoding.UTF8.GetString(bytes);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252).GetString(bytes);
    }

    /// <summary>Windows language id (low byte of the MOBI locale) → language tag, for the common languages.</summary>
    private static string? Language(int locale) => (locale & 0xFF) switch
    {
        0x04 => "zh",
        0x07 => "de",
        0x09 => "en",
        0x0A => "es",
        0x0C => "fr",
        0x10 => "it",
        0x11 => "ja",
        0x12 => "ko",
        0x13 => "nl",
        0x16 => "pt",
        0x19 => "ru",
        _ => null,
    };
}
