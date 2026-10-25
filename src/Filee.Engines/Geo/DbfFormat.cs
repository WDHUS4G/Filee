// dBASE (.dbf), the attribute table of a Shapefile. Reading handles dBASE III / IV / FoxPro tables: text (C), numbers (N,
// F), true / false (L), dates (D), binary integers and doubles (I, B); memo fields read as empty. The text encoding comes
// from the .cpg file or the file's language driver byte; without either the text is UTF-8 if it is valid, else Korean
// CP949 (like the CSV reader), else Windows-1252. Records marked as deleted are returned as null.
// Writing makes a dBASE III table in UTF-8 (the .cpg says so): text columns up to 254 bytes, numbers as N with the
// decimals they need, booleans as L. Field names are cut to 10 bytes and made unique, as the format requires.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Filee.Engines.Geo;

internal sealed record DbfField(string Name, char Type, int Length, int Decimals);

internal sealed class DbfTable
{
    public List<DbfField> Fields { get; } = [];

    /// <summary>The values of each record in field order; null for a record marked as deleted.</summary>
    public List<object?[]?> Rows { get; } = [];
}

/// <summary>What the writer had to change to fit the format, so the file can carry a notice.</summary>
internal readonly record struct DbfLosses(bool NamesChanged, bool TextCut, bool ColumnsDropped);

internal static class DbfFormat
{
    /// <summary>Names tools write in a .cpg for Korean Windows text, all code page 949.</summary>
    private static readonly HashSet<string> KoreanNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CP949", "UHC", "MS949", "WINDOWS-949", "X-WINDOWS-949", "KS_C_5601-1987",
    };

    private const int MaxTextBytes = 254;
    private const int MaxFields = 255;
    private const int MaxNumberWidth = 24;

    static DbfFormat() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ---- Reading ----

    /// <summary>
    /// Reads a table; <paramref name="cpg"/> is the text of the .cpg file, or null. At most <paramref name="maxRows"/> rows
    /// are read, and only their bytes: a table of millions of rows beside a few shapes costs nothing.
    /// </summary>
    public static DbfTable Read(string path, string? cpg, int maxRows, CancellationToken cancellationToken)
    {
        var bytes = ReadStart(path, maxRows);
        if (bytes.Length < 33)
            throw new InvalidDataException("The .dbf file is too short to be a dBASE table.");
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        if (headerLength < 33 || headerLength > bytes.Length || recordLength < 1)
            throw new InvalidDataException("The .dbf header is damaged.");

        var table = new DbfTable();
        var offsets = new List<int>();
        var position = 1;
        for (var at = 32; at + 32 <= headerLength && bytes[at] != 0x0D; at += 32)
        {
            if (table.Fields.Count >= 2046)
                throw new InvalidDataException("The .dbf has too many fields.");
            var nameEnd = Array.IndexOf(bytes, (byte)0, at, 11);
            var nameBytes = bytes.AsSpan(at, (nameEnd < 0 ? at + 11 : nameEnd) - at);
            var field = new DbfField(Latin1(nameBytes), (char)bytes[at + 11], bytes[at + 16], bytes[at + 17]);
            table.Fields.Add(field);
            offsets.Add(position);
            position += field.Length;
        }
        if (position > recordLength)
            throw new InvalidDataException("The .dbf field lengths do not fit the record length.");

        var rowLimit = (uint)Math.Max(maxRows, 0);
        var encoding = Resolve(cpg, bytes[29]) ?? Detect(bytes, headerLength, recordLength, table.Fields, offsets, Math.Min(recordCount, rowLimit));
        // Field names were read as Latin-1 above; re-read them in the table's encoding (they are ASCII almost always).
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var at = 32 + 32 * i;
            var nameEnd = Array.IndexOf(bytes, (byte)0, at, 11);
            var name = encoding.GetString(bytes, at, (nameEnd < 0 ? at + 11 : nameEnd) - at).Trim();
            table.Fields[i] = table.Fields[i] with { Name = name.Length == 0 ? $"FIELD{i + 1}" : name };
        }

        var available = (bytes.Length - headerLength) / recordLength;
        var count = (int)Math.Min(Math.Min(recordCount, (uint)available), rowLimit);
        for (var r = 0; r < count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = headerLength + r * recordLength;
            if (bytes[start] == (byte)'*')
            {
                table.Rows.Add(null);
                continue;
            }
            var values = new object?[table.Fields.Count];
            for (var f = 0; f < values.Length; f++)
                values[f] = Value(table.Fields[f], bytes.AsSpan(start + offsets[f], table.Fields[f].Length), encoding);
            table.Rows.Add(values);
        }
        return table;
    }

    /// <summary>The header and the first <paramref name="maxRows"/> records of the file (all of it if it is shorter).</summary>
    private static byte[] ReadStart(string path, int maxRows)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[12];
        var got = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (got < head.Length)
            return head[..got];
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        // The records, and the end marker after them.
        var wanted = headerLength + (long)Math.Max(maxRows, 0) * recordLength + 1;
        // At least the fixed header, so a header that claims to be shorter is reported as damaged, not as a short file.
        var bytes = new byte[(int)Math.Min(file.Length, Math.Max(wanted, 33))];
        file.Position = 0;
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static object? Value(DbfField field, ReadOnlySpan<byte> cell, Encoding encoding)
    {
        switch (char.ToUpperInvariant(field.Type))
        {
            case 'C':
                {
                    var end = cell.Length;
                    while (end > 0 && cell[end - 1] is (byte)' ' or 0)
                        end--;
                    return end == 0 ? "" : encoding.GetString(cell[..end]);
                }
            case 'N' or 'F':
                {
                    var text = Latin1(cell).Trim();
                    if (text.Length == 0 || text[0] == '*')
                        return null;
                    if (field.Decimals == 0 && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                        return whole;
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real)
                        ? real == Math.Floor(real) && Math.Abs(real) < 1e15 && field.Decimals == 0 ? (long)real : real
                        : null;
                }
            case 'L' when cell.Length > 0:
                return (char)cell[0] switch
                {
                    'T' or 't' or 'Y' or 'y' => true,
                    'F' or 'f' or 'N' or 'n' => false,
                    _ => null,
                };
            case 'D':
                {
                    var text = Latin1(cell).Trim();
                    return text.Length == 8 && DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : null;
                }
            case 'I' or '+' when cell.Length >= 4:
                return (long)BinaryPrimitives.ReadInt32LittleEndian(cell);
            case 'B' or 'O' when cell.Length >= 8:
                {
                    var real = BitConverter.ToDouble(cell[..8]);
                    return double.IsFinite(real) ? real : null;
                }
            default:
                // Memo, date-time and other types that point elsewhere or need extra files.
                return null;
        }
    }

    private static Encoding? Resolve(string? cpg, byte languageDriver)
    {
        if (!string.IsNullOrWhiteSpace(cpg))
        {
            var name = cpg.Trim().Trim('\0');
            try
            {
                if (name.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) || name.Equals("UTF8", StringComparison.OrdinalIgnoreCase) || name == "65001")
                    return new UTF8Encoding(false);
                if (KoreanNames.Contains(name))
                    return Encoding.GetEncoding(949);
                if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
                    return Encoding.GetEncoding(page);
                if (name.StartsWith("ANSI ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out page))
                    return Encoding.GetEncoding(page);
                return Encoding.GetEncoding(name.Replace("LATIN1", "ISO-8859-1", StringComparison.OrdinalIgnoreCase));
            }
            catch (ArgumentException)
            {
                // An encoding name this system doesn't know: fall back to the language driver byte, then to detection.
            }
        }
        // The dBASE "language driver ID" in the header.
        var codePage = languageDriver switch
        {
            0x01 => 437,
            0x02 => 850,
            0x03 or 0x57 or 0x58 or 0x59 => 1252,
            0x08 => 865,
            0x09 => 437,
            0x0A => 850,
            0x0B => 437,
            0x0D => 437,
            0x0E => 850,
            0x0F => 437,
            0x10 => 850,
            0x11 => 437,
            0x12 => 850,
            0x13 => 932,
            0x14 => 850,
            0x15 => 437,
            0x16 => 850,
            0x17 => 865,
            0x18 => 437,
            0x19 => 437,
            0x1A => 850,
            0x1B => 437,
            0x1C => 863,
            0x1D => 850,
            0x1F => 852,
            0x22 => 852,
            0x23 => 852,
            0x24 => 860,
            0x25 => 850,
            0x26 => 866,
            0x37 => 850,
            0x40 => 852,
            0x4D => 936,
            0x4E => 949,
            0x4F => 950,
            0x50 => 874,
            0x64 => 852,
            0x65 => 866,
            0x66 => 865,
            0x67 => 861,
            0x6A => 737,
            0x6B => 857,
            0x78 => 950,
            0x79 => 949,
            0x7A => 936,
            0x7B => 932,
            0x7C => 874,
            0x7D => 1255,
            0x7E => 1256,
            0xC8 => 1250,
            0xC9 => 1251,
            0xCA => 1254,
            0xCB => 1253,
            0xCC => 1257,
            _ => 0,
        };
        if (codePage == 0)
            return null;
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>UTF-8 when the text of the first records is valid UTF-8, else CP949 if valid, else Windows-1252.</summary>
    private static Encoding Detect(byte[] bytes, int headerLength, int recordLength, List<DbfField> fields, List<int> offsets, uint recordCount)
    {
        var sample = new List<byte>();
        var count = (int)Math.Min(recordCount, (uint)((bytes.Length - headerLength) / recordLength));
        for (var r = 0; r < count && sample.Count < 1 << 20; r++)
        {
            var start = headerLength + r * recordLength;
            for (var f = 0; f < fields.Count; f++)
            {
                if (char.ToUpperInvariant(fields[f].Type) == 'C')
                    sample.AddRange(bytes.AsSpan(start + offsets[f], fields[f].Length).ToArray());
            }
        }
        var data = sample.ToArray();
        foreach (var encoding in new[] { Strict(new UTF8Encoding(false)), Strict(Encoding.GetEncoding(949)) })
        {
            try
            {
                encoding.GetString(data);
                return encoding.CodePage == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(encoding.CodePage);
            }
            catch (DecoderFallbackException)
            {
            }
        }
        return Encoding.GetEncoding(1252);
    }

    private static Encoding Strict(Encoding encoding) =>
        Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    // ---- Writing ----

    /// <summary>
    /// Writes the attributes of <paramref name="features"/> as a dBASE III table in UTF-8. A table needs at least one
    /// field, so features without attributes get a record number field "FID".
    /// </summary>
    public static DbfLosses Write(string path, IReadOnlyList<GeoFeature> features, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            foreach (var (key, _) in feature.Properties)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }
        }
        var columnsDropped = columns.Count > MaxFields;
        if (columnsDropped)
            columns = columns.GetRange(0, MaxFields);

        var rows = features.Select(f =>
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in f.Properties)
                map.TryAdd(key, value);
            return map;
        }).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesChanged = false;
        var layouts = new List<Layout>();
        if (columns.Count == 0)
        {
            layouts.Add(new Layout("FID", 'N', 11, 0, "FID", Fid: true));
            used.Add("FID");
        }
        foreach (var column in columns)
        {
            var name = FieldName(column, used);
            namesChanged |= name != column;
            layouts.Add(Describe(name, column, rows));
        }

        var textCut = false;
        var recordLength = 1 + layouts.Sum(l => l.Length);
        var headerLength = 32 + 32 * layouts.Count + 1;
        if (recordLength > ushort.MaxValue || headerLength > ushort.MaxValue)
            throw new InvalidDataException("The attribute table is too wide for a dBASE file.");

        using var stream = new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), 1 << 16);
        var header = new byte[32];
        header[0] = 0x03;
        var today = DateTime.UtcNow;
        header[1] = (byte)(today.Year - 1900);
        header[2] = (byte)today.Month;
        header[3] = (byte)today.Day;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)rows.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)recordLength);
        stream.Write(header);
        foreach (var layout in layouts)
        {
            var descriptor = new byte[32];
            Encoding.UTF8.GetBytes(layout.Name, descriptor);
            descriptor[11] = (byte)layout.Type;
            descriptor[16] = (byte)layout.Length;
            descriptor[17] = (byte)layout.Decimals;
            stream.Write(descriptor);
        }
        stream.WriteByte(0x0D);

        var record = new byte[recordLength];
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Fill(record, (byte)' ');
            var at = 1;
            foreach (var layout in layouts)
            {
                var value = layout.Fid ? (long)r : rows[r].GetValueOrDefault(layout.Source);
                textCut |= Put(record.AsSpan(at, layout.Length), layout, value);
                at += layout.Length;
            }
            stream.Write(record);
        }
        stream.WriteByte(0x1A);
        return new DbfLosses(namesChanged, textCut, columnsDropped);
    }

    private sealed record Layout(string Name, char Type, int Length, int Decimals, string Source, bool Fid = false);

    private static Layout Describe(string name, string source, List<Dictionary<string, object?>> rows)
    {
        var strings = false;
        var bools = false;
        var longs = false;
        var doubles = false;
        var textBytes = 1;
        var longWidth = 1;
        var decimals = 0;
        foreach (var row in rows)
        {
            if (!row.TryGetValue(source, out var value) || value is null)
                continue;
            switch (value)
            {
                case bool:
                    bools = true;
                    break;
                case long whole:
                    longs = true;
                    longWidth = Math.Max(longWidth, whole.ToString(CultureInfo.InvariantCulture).Length);
                    break;
                case double real when double.IsFinite(real):
                    doubles = true;
                    var text = GeoText.Number(real);
                    var dot = text.IndexOf('.', StringComparison.Ordinal);
                    decimals = Math.Max(decimals, dot < 0 ? 0 : text.Length - dot - 1);
                    break;
                case double:
                    break;
                default:
                    strings = true;
                    break;
            }
            if (strings)
                break;
        }
        if (!strings && !(bools && (longs || doubles)))
        {
            if (bools)
                return new Layout(name, 'L', 1, 0, source);
            if (doubles)
            {
                // Fixed decimals, as many as the values need (15 at most), in a width of 24 at most.
                decimals = Math.Min(decimals, 15);
                var width = 1;
                var fits = true;
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(source, out var value) || value is null)
                        continue;
                    var real = value is long whole ? whole : value is double d && double.IsFinite(d) ? d : double.NaN;
                    if (double.IsNaN(real))
                        continue;
                    width = Math.Max(width, real.ToString("F" + decimals, CultureInfo.InvariantCulture).Length);
                }
                while (width > MaxNumberWidth && decimals > 0)
                {
                    decimals--;
                    width--;
                }
                fits = width <= MaxNumberWidth;
                if (fits)
                    return new Layout(name, 'N', Math.Max(width, 1), decimals, source);
                strings = true;
            }
            else if (longs && longWidth <= 18)
            {
                return new Layout(name, 'N', longWidth, 0, source);
            }
            else if (!longs)
            {
                // Only empty values: a text column of one blank.
                return new Layout(name, 'C', 1, 0, source);
            }
            else
            {
                strings = true;
            }
        }
        foreach (var row in rows)
        {
            if (row.TryGetValue(source, out var value) && value is not null)
                textBytes = Math.Max(textBytes, Encoding.UTF8.GetByteCount(GeoText.ToText(value)));
        }
        return new Layout(name, 'C', Math.Min(textBytes, MaxTextBytes), 0, source);
    }

    /// <summary>Writes one value into its cell; returns true if text had to be cut to the field width.</summary>
    private static bool Put(Span<byte> cell, Layout layout, object? value)
    {
        switch (layout.Type)
        {
            case 'L':
                cell[0] = value switch { true => (byte)'T', false => (byte)'F', _ => (byte)'?' };
                return false;
            case 'N':
                {
                    string? text = value switch
                    {
                        long whole => layout.Decimals == 0 ? whole.ToString(CultureInfo.InvariantCulture) : ((double)whole).ToString("F" + layout.Decimals, CultureInfo.InvariantCulture),
                        double real when double.IsFinite(real) => real.ToString("F" + layout.Decimals, CultureInfo.InvariantCulture),
                        _ => null,
                    };
                    if (text is null)
                        return false;
                    if (text.Length > cell.Length)
                        text = text[..cell.Length];
                    Encoding.ASCII.GetBytes(text, cell[(cell.Length - text.Length)..]);
                    return false;
                }
            default:
                {
                    var text = GeoText.ToText(value);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var length = bytes.Length;
                    var cut = length > cell.Length;
                    if (cut)
                    {
                        length = cell.Length;
                        // Don't end in the middle of a character.
                        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
                            length--;
                    }
                    bytes.AsSpan(0, length).CopyTo(cell);
                    return cut;
                }
        }
    }

    /// <summary>A field name of at most ten bytes of letters, digits and underscores that is not used yet.</summary>
    private static string FieldName(string source, HashSet<string> used)
    {
        var builder = new StringBuilder();
        foreach (var rune in source.EnumerateRunes())
            builder.Append(Rune.IsLetterOrDigit(rune) || rune.Value == '_' ? rune.ToString() : "_");
        var name = builder.Length == 0 ? "FIELD" : builder.ToString();
        var candidate = Cut(name, 10);
        for (var n = 1; !used.Add(candidate); n++)
        {
            var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            candidate = Cut(name, 10 - suffix.Length) + suffix;
        }
        return candidate;
    }

    /// <summary>The start of <paramref name="text"/> that fits in <paramref name="bytes"/> bytes of UTF-8.</summary>
    private static string Cut(string text, int bytes)
    {
        var result = new StringBuilder();
        var size = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            size += rune.Utf8SequenceLength;
            if (size > bytes)
                break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
