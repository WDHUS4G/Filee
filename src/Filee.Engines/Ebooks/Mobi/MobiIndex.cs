// INDX records of Kindle books: the skeleton and fragment tables of KF8 (AZW3) text are stored as indexes. Written
// from the format description on the MobileRead wiki: a primary INDX record with the tag table (TAGX), then INDX
// records whose entries (located through the IDXT offset table) are a key followed by control bytes and
// variable-width tag values; strings live in CNCX records.

using System.Text;

namespace Filee.Engines.Ebooks;

/// <summary>One index entry: its key and tag values.</summary>
internal sealed record MobiIndexEntry(string Key, IReadOnlyDictionary<int, List<long>> Tags)
{
    public long Tag(int tag, int position = 0) =>
        Tags.TryGetValue(tag, out var values) && position < values.Count ? values[position] : -1;
}

internal static class MobiIndex
{
    private readonly record struct TagDefinition(int Tag, int ValuesPerEntry, int Mask, bool EndFlag);

    /// <summary>Reads the index starting at record <paramref name="first"/>; CNCX strings go to <paramref name="strings"/>.</summary>
    public static List<MobiIndexEntry> Read(PalmDatabase database, int first, Dictionary<long, string> strings)
    {
        var entries = new List<MobiIndexEntry>();
        var primary = database.Record(first);
        if (primary.Length < 56 || !primary[..4].SequenceEqual("INDX"u8))
            throw new InvalidDataException("The book's index is damaged.");
        var headerLength = (int)PalmDatabase.U32(primary, 4);
        var recordCount = (int)PalmDatabase.U32(primary, 24);
        var cncxCount = (int)Math.Max(0, PalmDatabase.U32(primary, 52));
        var (controlBytes, tags) = TagTable(primary, headerLength);

        // CNCX records follow the index records; offsets in tag values address them as record * 0x10000 + offset.
        for (var c = 0; c < cncxCount; c++)
        {
            var cncx = database.Record(first + recordCount + 1 + c);
            var offset = 0;
            while (offset < cncx.Length && cncx[offset] != 0)
            {
                var start = offset;
                var (length, consumed) = ForwardVarint(cncx, offset);
                offset += consumed;
                length = Math.Min(length, cncx.Length - offset);
                strings[c * 0x10000L + start] = Encoding.UTF8.GetString(cncx.Slice(offset, (int)length));
                offset += (int)length;
            }
        }

        for (var r = 1; r <= recordCount; r++)
        {
            var record = database.Record(first + r);
            if (record.Length < 28 || !record[..4].SequenceEqual("INDX"u8))
                continue;
            var idxt = (int)PalmDatabase.U32(record, 20);
            var count = (int)PalmDatabase.U32(record, 24);
            if (idxt < 0 || idxt + 4 + count * 2 > record.Length)
                continue;
            var offsets = new int[count + 1];
            for (var i = 0; i < count; i++)
                offsets[i] = PalmDatabase.U16(record, idxt + 4 + i * 2);
            offsets[count] = idxt;
            for (var i = 0; i < count; i++)
            {
                var start = offsets[i];
                if (start >= record.Length)
                    continue;
                var keyLength = record[start];
                var key = Encoding.Latin1.GetString(record.Slice(start + 1, Math.Min(keyLength, record.Length - start - 1)));
                var values = TagValues(record, start + 1 + keyLength, offsets[i + 1], controlBytes, tags);
                entries.Add(new MobiIndexEntry(key, values));
            }
        }
        return entries;
    }

    private static (int ControlBytes, List<TagDefinition> Tags) TagTable(ReadOnlySpan<byte> record, int start)
    {
        var tags = new List<TagDefinition>();
        if (start < 0 || start + 12 > record.Length || !record.Slice(start, 4).SequenceEqual("TAGX"u8))
            throw new InvalidDataException("The book's index has no tag table.");
        var length = (int)PalmDatabase.U32(record, start + 4);
        var controlBytes = (int)PalmDatabase.U32(record, start + 8);
        for (var i = 12; i + 4 <= length && start + i + 4 <= record.Length; i += 4)
        {
            var p = start + i;
            tags.Add(new TagDefinition(record[p], record[p + 1], record[p + 2], record[p + 3] == 1));
        }
        return (controlBytes, tags);
    }

    /// <summary>
    /// Tag values of one entry. Each tag's mask selects bits of a control byte: a partial value is the number of
    /// value groups; all bits set with a multi-bit mask means a byte count follows; a one-bit mask means one group.
    /// </summary>
    private static Dictionary<int, List<long>> TagValues(ReadOnlySpan<byte> record, int start, int end, int controlBytes, List<TagDefinition> tags)
    {
        var result = new Dictionary<int, List<long>>();
        var controlIndex = 0;
        var position = start + controlBytes;
        var headers = new List<(int Tag, int? Count, int? Bytes, int PerEntry)>();
        foreach (var tag in tags)
        {
            if (tag.EndFlag)
            {
                controlIndex++;
                continue;
            }
            if (start + controlIndex >= record.Length)
                break;
            var value = record[start + controlIndex] & tag.Mask;
            if (value == 0)
                continue;
            if (value == tag.Mask)
            {
                if (System.Numerics.BitOperations.PopCount((uint)tag.Mask) > 1)
                {
                    var (bytes, consumed) = ForwardVarint(record, position);
                    position += consumed;
                    headers.Add((tag.Tag, null, (int)bytes, tag.ValuesPerEntry));
                }
                else
                {
                    headers.Add((tag.Tag, 1, null, tag.ValuesPerEntry));
                }
            }
            else
            {
                var mask = tag.Mask;
                while ((mask & 1) == 0)
                {
                    mask >>= 1;
                    value >>= 1;
                }
                headers.Add((tag.Tag, value, null, tag.ValuesPerEntry));
            }
        }

        foreach (var (tag, count, bytes, perEntry) in headers)
        {
            var values = new List<long>();
            if (count is { } groups)
            {
                for (var i = 0; i < groups * perEntry && position < end; i++)
                {
                    var (value, consumed) = ForwardVarint(record, position);
                    position += consumed;
                    values.Add(value);
                }
            }
            else
            {
                var read = 0;
                while (read < bytes && position < end)
                {
                    var (value, consumed) = ForwardVarint(record, position);
                    position += consumed;
                    read += consumed;
                    values.Add(value);
                }
            }
            result[tag] = values;
        }
        return result;
    }

    /// <summary>A forward variable-width integer: 7 bits per byte, the last byte has the high bit set.</summary>
    internal static (long Value, int Consumed) ForwardVarint(ReadOnlySpan<byte> data, int offset)
    {
        long value = 0;
        var consumed = 0;
        while (offset + consumed < data.Length && consumed < 9)
        {
            var b = data[offset + consumed++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) != 0)
                break;
        }
        return (value, Math.Max(1, consumed));
    }
}
