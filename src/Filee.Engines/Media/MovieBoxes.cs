// Small, bounds-checked ISO BMFF box readers for photo metadata, and the writers of the few boxes Filee adds; media
// payloads are streamed, never loaded whole.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Media;

internal readonly record struct MovieBox(string Type, long Start, long Length, int HeaderLength)
{
    public long End => checked(Start + Length);
}

internal static class MovieBoxes
{
    /// <summary>Reads the box header at the stream's position; a box reaching past the end of the stream is refused.</summary>
    public static MovieBox ReadHeader(Stream file)
    {
        var start = file.Position;
        Span<byte> header = stackalloc byte[8];
        file.ReadExactly(header);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.Latin1.GetString(header[4..]);
        var headerLength = 8;
        if (size == 1)
        {
            file.ReadExactly(header);
            var extended = BinaryPrimitives.ReadUInt64BigEndian(header);
            if (extended > long.MaxValue)
                throw new InvalidDataException("Media box is too large.");
            size = (long)extended;
            headerLength = 16;
        }
        else if (size == 0)
            size = file.Length - start;
        if (size < headerLength || size > file.Length - start)
            throw new InvalidDataException("Media box extends outside its container.");
        return new(type, start, size, headerLength);
    }

    /// <summary>The boxes in <paramref name="data"/> (a parent's payload), after <paramref name="skip"/> bytes.</summary>
    public static List<(string Type, byte[] Payload)> Children(byte[] data, int skip = 0)
    {
        using var stream = new MemoryStream(data);
        stream.Position = skip;
        var result = new List<(string, byte[])>();
        while (stream.Position < stream.Length)
        {
            var box = ReadHeader(stream);
            var payload = new byte[checked((int)(box.Length - box.HeaderLength))];
            stream.ReadExactly(payload);
            result.Add((box.Type, payload));
        }
        return result;
    }

    /// <summary>A box of <paramref name="type"/> (four Latin-1 characters) around <paramref name="parts"/>.</summary>
    public static byte[] Box(string type, params byte[][] parts)
    {
        var box = new byte[checked(8 + parts.Sum(p => p.Length))];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        if (Encoding.Latin1.GetBytes(type, box.AsSpan(4)) != 4)
            throw new ArgumentException("A box type has four characters.", nameof(type));
        var at = 8;
        foreach (var part in parts)
        {
            part.CopyTo(box, at);
            at += part.Length;
        }
        return box;
    }

    /// <summary>A box that starts with a version and flags (a "full box").</summary>
    public static byte[] FullBox(string type, byte version, uint flags, params byte[][] parts) =>
        Box(type, [U32(((uint)version << 24) | (flags & 0xffffff)), .. parts]);

    public static byte[] U16(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        return bytes;
    }

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>The handler type of a track (<c>vide</c>, <c>soun</c>, <c>meta</c>, …), from its <c>mdia/hdlr</c>.</summary>
    public static string? HandlerOf(byte[] trak)
    {
        var mdia = Children(trak).FirstOrDefault(c => c.Type == "mdia").Payload;
        var hdlr = mdia is null ? null : Children(mdia).FirstOrDefault(c => c.Type == "hdlr").Payload;
        return hdlr is { Length: >= 12 } ? Encoding.Latin1.GetString(hdlr, 8, 4) : null;
    }

    /// <summary>The samples (frames) of the first video track of the movie <paramref name="moov"/> (its payload).</summary>
    public static uint VideoSampleCount(byte[] moov)
    {
        foreach (var trak in Children(moov).Where(c => c.Type == "trak").Select(c => c.Payload))
        {
            if (HandlerOf(trak) != "vide")
                continue;
            var stbl = Descend(trak, "mdia", "minf", "stbl");
            var stsz = stbl is null ? null : Children(stbl).FirstOrDefault(c => c.Type == "stsz").Payload;
            if (stsz is { Length: >= 12 })
                return BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(8));
        }
        throw new InvalidDataException("The movie has no video track.");
    }

    /// <summary>The payload of the box at <paramref name="path"/> below <paramref name="parent"/>, or null.</summary>
    private static byte[]? Descend(byte[] parent, params string[] path)
    {
        byte[]? current = parent;
        foreach (var type in path)
            current = current is null ? null : Children(current).FirstOrDefault(c => c.Type == type).Payload;
        return current;
    }

    /// <summary>Reads the movie header (<c>moov</c>) of <paramref name="file"/>: its box and payload.</summary>
    /// <exception cref="InvalidDataException">The file has none, or one larger than <paramref name="limit"/>.</exception>
    public static (MovieBox Box, byte[] Payload) ReadMovieHeader(Stream file, int limit = 16 * 1024 * 1024)
    {
        file.Position = 0;
        while (file.Position < file.Length)
        {
            var box = ReadHeader(file);
            if (box.Type == "moov")
            {
                if (box.Length > limit)
                    throw new InvalidDataException("The movie header is too large.");
                var data = new byte[checked((int)(box.Length - box.HeaderLength))];
                file.ReadExactly(data);
                return (box, data);
            }
            file.Position = box.End;
        }
        throw new InvalidDataException("The file has no movie header.");
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes; a source that ends sooner is an error.</summary>
    public static async Task CopyRangeAsync(Stream source, Stream target, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), ct);
            if (read == 0)
                throw new EndOfStreamException();
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }
}
