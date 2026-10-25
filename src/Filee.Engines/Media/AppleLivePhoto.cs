// Apple Live Photo pairing: the content identifier the photo (Apple maker note) and its MOV (QuickTime metadata) share,
// read from a pair and written into a new one, with the movie's timed "still image time" marking the photo's frame.

using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using static Filee.Engines.Media.MovieBoxes;

namespace Filee.Engines.Media;

internal static class AppleLivePhoto
{
    internal const string ContentIdentifier = "com.apple.quicktime.content.identifier";
    internal const string StillImageTime = "com.apple.quicktime.still-image-time";
    private const int MaxMovieHeader = 16 * 1024 * 1024;

    /// <summary>Tag 17 of Apple's maker note: the Live Photo's content identifier, an ASCII UUID.</summary>
    private const ushort ContentIdentifierTag = 17;

    /// <summary>"Signed 8-bit integer" among QuickTime's well-known metadata data types.</summary>
    private const uint WellKnownInt8 = 65;

    /// <summary>The content identifier in the photo's Apple maker note (tag 17), or null.</summary>
    public static string? ImageIdentifier(string imagePath)
    {
        using var image = new MagickImage();
        image.Ping(imagePath);
        return ReadMakerNote(image.GetExifProfile()?.GetValue(ExifTag.MakerNote)?.Value);
    }

    internal static string? ReadMakerNote(byte[]? data)
    {
        if (data is null || data.Length < 16 || !data.AsSpan().StartsWith("Apple iOS\0"u8))
            return null;
        var little = data.AsSpan(12, 2).SequenceEqual("II"u8);
        if (!little && !data.AsSpan(12, 2).SequenceEqual("MM"u8))
            return null;
        ushort Read16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint Read32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        var count = Read16(14);
        if (16L + count * 12L > data.Length)
            return null;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * 12;
            if (Read16(at) != ContentIdentifierTag || Read16(at + 2) != 2)
                continue;
            // Apple maker-note offsets are relative to the note itself.
            var length = Read32(at + 4);
            var offset = Read32(at + 8);
            if (length < 36 || length > 128 || offset > data.Length || length > data.Length - offset)
                return null;
            var id = Encoding.ASCII.GetString(data, (int)offset, (int)length).TrimEnd('\0');
            return Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;
        }
        return null;
    }

    /// <summary>
    /// An Apple maker note holding only <paramref name="identifier"/> (tag 17), laid out as an iPhone writes its own:
    /// "Apple iOS", version 1, big-endian, one IFD entry, no next IFD, the value after it. Offsets count from the
    /// start of the note, so it stays valid wherever the Exif writer puts it.
    /// </summary>
    internal static byte[] MakerNote(string identifier)
    {
        var id = Encoding.ASCII.GetBytes(Guid.Parse(identifier).ToString("D").ToUpperInvariant() + "\0");
        const uint valueOffset = 14 + 2 + 12 + 4;
        return
        [
            .. "Apple iOS\0"u8, 0, 1, .. "MM"u8,
            .. U16(1),
            .. U16(ContentIdentifierTag), .. U16(2), .. U32((uint)id.Length), .. U32(valueOffset),
            .. U32(0),
            .. id,
        ];
    }

    /// <summary>The content identifier in the movie's QuickTime metadata, or null.</summary>
    public static string? MovieIdentifier(string path)
    {
        using var file = File.OpenRead(path);
        var children = Children(ReadMovieHeader(file, MaxMovieHeader).Payload);
        // Apple's movie-level meta is a plain container; other writers put a full-box meta under udta.
        var metadata = children.Where(c => c.Type == "meta").Select(c => c.Payload).ToList();
        foreach (var udta in children.Where(c => c.Type == "udta"))
            metadata.AddRange(Children(udta.Payload).Where(c => c.Type == "meta").Select(c => c.Payload));
        foreach (var data in metadata)
        {
            var atoms = Children(data, data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(new byte[4]) ? 4 : 0);
            var keys = atoms.FirstOrDefault(c => c.Type == "keys").Payload;
            var values = atoms.FirstOrDefault(c => c.Type == "ilst").Payload;
            if (keys is null || values is null || keys.Length < 8)
                continue;
            var keyList = Children(keys, 8);
            var index = keyList.FindIndex(k => k.Type == "mdta" && Encoding.UTF8.GetString(k.Payload) == ContentIdentifier) + 1;
            if (index == 0)
                continue;
            foreach (var value in Children(values).Where(v => v.Type == Encoding.Latin1.GetString(U32((uint)index))))
            {
                var item = Children(value.Payload).FirstOrDefault(c => c.Type == "data").Payload;
                if (item is { Length: > 8 } && Guid.TryParse(Encoding.UTF8.GetString(item, 8, item.Length - 8), out var id))
                    return id.ToString("D");
            }
        }
        return null;
    }

    /// <summary>
    /// Makes the MOV at <paramref name="path"/> (FFmpeg's, movie header at the end) the video of a Live Photo, as an
    /// iPhone's are: <c>com.apple.quicktime.content.identifier</c> in the movie-level metadata (<c>moov/meta</c>:
    /// <c>hdlr</c> "mdta", <c>keys</c>, <c>ilst</c>), and a timed metadata track (<c>mebx</c>, referring to the video
    /// track) whose one sample, <c>com.apple.quicktime.still-image-time</c> = -1, is shown at
    /// <paramref name="stillSeconds"/>, the photo's frame. Only the movie header is rewritten: the sample goes into a
    /// small <c>mdat</c> where the header was, and no media data moves, so every chunk offset stays right.
    /// </summary>
    /// <exception cref="InvalidDataException">The movie is not as FFmpeg writes it.</exception>
    public static void TagMovie(string path, string identifier, double stillSeconds)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (moov, payload) = ReadMovieHeader(file, MaxMovieHeader);
        if (moov.End != file.Length)
            throw new InvalidDataException("The movie header must end the file.");
        var children = Children(payload);
        var mvhd = children.Single(c => c.Type == "mvhd").Payload;
        if (mvhd.Length < 100)
            throw new InvalidDataException("Invalid movie header.");
        var longTimes = mvhd[0] == 1;
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(longTimes ? 20 : 12));
        var duration = longTimes ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
        var created = longTimes ? 0 : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(4));
        var trackId = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(mvhd.Length - 4));
        if (timescale == 0 || trackId is 0 or uint.MaxValue)
            throw new InvalidDataException("Invalid movie timescale or next track id.");
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(mvhd.Length - 4), trackId + 1);

        var video = children.FirstOrDefault(c => c.Type == "trak" && HandlerOf(c.Payload) == "vide").Payload
            ?? throw new InvalidDataException("The movie has no video track.");
        var tkhd = Children(video).Single(c => c.Type == "tkhd").Payload;
        var videoId = BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(tkhd[0] == 1 ? 20 : 12));

        // The still is one tick long and starts at its frame: it must lie inside the movie.
        var still = (ulong)Math.Round(Math.Max(0, stillSeconds) * timescale);
        if (duration > 0)
            still = Math.Min(still, duration - 1);
        if (still >= uint.MaxValue)
            throw new InvalidDataException("The still image time is outside the movie.");

        // One sample: a 9-byte item of local key 1 (still-image-time), the int8 -1 an iPhone writes.
        var sample = Box(LocalKey(1), new byte[] { 0xff });
        var mdat = Box("mdat", sample);
        var sampleOffset = (ulong)moov.Start + 8;
        var track = StillImageTrack(trackId, videoId, created, timescale, (uint)still, sampleOffset, (uint)sample.Length);

        var lastTrack = children.FindLastIndex(c => c.Type == "trak");
        var boxes = new List<byte[]>();
        for (var i = 0; i < children.Count; i++)
        {
            // An identifier the source carried is replaced, never kept next to the new one.
            if (children[i].Type != "meta")
                boxes.Add(Box(children[i].Type, children[i].Payload));
            if (i == lastTrack)
                boxes.Add(track);
        }
        boxes.Add(MovieMetadata(identifier));

        file.SetLength(moov.Start);
        file.Position = moov.Start;
        file.Write(mdat);
        file.Write(Box("moov", [.. boxes]));
    }

    /// <summary>The type of a key in a <c>mebx</c> sample entry, and of its items in a sample: the local id.</summary>
    private static string LocalKey(uint id) => Encoding.Latin1.GetString(U32(id));

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>A QuickTime component name: one length byte, then the characters.</summary>
    private static byte[] Pascal(string text) => [(byte)text.Length, .. Encoding.ASCII.GetBytes(text)];

    /// <summary>
    /// Movie-level metadata as Apple writes it: a plain <c>meta</c> container (no version and flags) with an "mdta"
    /// handler, the key and its UTF-8 value (type 1, no locale).
    /// </summary>
    private static byte[] MovieMetadata(string identifier) =>
        Box("meta",
            Box("hdlr", new byte[8], Ascii("mdta"), new byte[14]),
            FullBox("keys", 0, 0, U32(1), Box("mdta", Encoding.UTF8.GetBytes(ContentIdentifier))),
            Box("ilst", Box(LocalKey(1), Box("data", U32(1), U32(0), Encoding.UTF8.GetBytes(identifier.ToUpperInvariant())))));

    /// <summary>
    /// The timed metadata track of the still image time, laid out like an iPhone's: track flags 0xF, an empty edit up
    /// to the still, then the one-tick sample; media timescale = movie timescale; a "mhlr/meta" handler with a base
    /// media header (<c>gmhd</c>), a self-referencing alias data reference, a <c>mebx</c> sample description whose
    /// key 1 is <c>mdta</c> + still-image-time typed as a signed 8-bit integer; <c>cdsc</c> to the video track.
    /// </summary>
    private static byte[] StillImageTrack(uint id, uint videoId, uint created, uint timescale, uint still, ulong offset, uint sampleSize)
    {
        byte[] identity = [.. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x40000000)];
        var tkhd = FullBox("tkhd", 0, 0xf, U32(created), U32(created), U32(id), U32(0), U32(still + 1), new byte[8],
            U16(0), U16(0), U16(0), U16(0), identity, U32(0), U32(0));
        byte[] Edit(uint length, int mediaTime) => [.. U32(length), .. U32(unchecked((uint)mediaTime)), .. U16(1), .. U16(0)];
        var edits = still > 0 ? new[] { Edit(still, -1), Edit(1, 0) } : [Edit(1, 0)];
        var elst = FullBox("elst", 0, 0, [U32((uint)edits.Length), .. edits]);

        var sampleEntry = Box("mebx", new byte[6], U16(1),
            Box("keys", Box(LocalKey(1),
                Box("keyd", Ascii("mdta"), Encoding.UTF8.GetBytes(StillImageTime)),
                Box("dtyp", U32(0), U32(WellKnownInt8)))));
        var chunkOffset = offset <= uint.MaxValue
            ? FullBox("stco", 0, 0, U32(1), U32((uint)offset))
            : FullBox("co64", 0, 0, U32(1), U64(offset));
        var stbl = Box("stbl",
            FullBox("stsd", 0, 0, U32(1), sampleEntry),
            FullBox("stts", 0, 0, U32(1), U32(1), U32(1)),
            FullBox("stsc", 0, 0, U32(1), U32(1), U32(1), U32(1)),
            FullBox("stsz", 0, 0, U32(0), U32(1), U32(sampleSize)),
            chunkOffset);
        var minf = Box("minf",
            Box("gmhd", FullBox("gmin", 0, 0, U16(0x40), U16(0x8000), U16(0x8000), U16(0x8000), U16(0), U16(0))),
            FullBox("hdlr", 0, 0, Ascii("dhlr"), Ascii("alis"), Ascii("appl"), U32(0), U32(0), Pascal("Core Media Data Handler")),
            Box("dinf", FullBox("dref", 0, 0, U32(1), FullBox("alis", 0, 1))),
            stbl);
        var mdia = Box("mdia",
            FullBox("mdhd", 0, 0, U32(created), U32(created), U32(timescale), U32(1), U16(0x55c4), U16(0)),
            FullBox("hdlr", 0, 0, Ascii("mhlr"), Ascii("meta"), Ascii("appl"), U32(1), U32(0), Pascal("Core Media Metadata")),
            minf);
        return Box("trak", tkhd, Box("edts", elst), Box("tref", Box("cdsc", U32(videoId))), mdia);
    }
}
