// Column widths and row heights of an XLS file, read from the BIFF records themselves. ExcelDataReader only reports
// widths whose "set by the user" flag is on (LibreOffice never sets it) and every row's height without telling
// custom from automatic ones; Excel uses the COLINFO width and the ROW "unsynced" flag, so this reads those.
// Encrypted workbooks and pre-Excel 5 files return null: the reader then falls back to ExcelDataReader's values.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Office.Sheets;

internal static class XlsLayout
{
    /// <summary>Columns and rows of one worksheet that differ from the defaults.</summary>
    /// <param name="Columns">Width in characters (as XLSX stores it) and hidden flag, by 0-based column.</param>
    /// <param name="Rows">Height in points, whether it is a custom height, and hidden flag, by 0-based row.</param>
    public sealed record Sheet(Dictionary<int, (double Width, bool Hidden)> Columns, Dictionary<int, (double Points, bool Custom, bool Hidden)> Rows);

    private const int MaxColumn = 16_383;

    /// <summary>The layout of every worksheet in workbook order (chart sheets left out, like ExcelDataReader).</summary>
    public static List<Sheet>? Read(string path)
    {
        try
        {
            return WorkbookStream(File.ReadAllBytes(path)) is { } workbook ? Parse(workbook) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or IOException or OverflowException)
        {
            return null;
        }
    }

    // ───────────────────────── BIFF records ─────────────────────────

    private static List<Sheet>? Parse(byte[] stream)
    {
        var offsets = new List<int>();
        foreach (var (id, data) in Records(stream, 0))
        {
            switch (id)
            {
                case 0x002F: // FILEPASS: everything after it is encrypted
                    return null;
                case 0x0085 when data.Length >= 6 && data[5] == 0: // BOUNDSHEET of a worksheet
                    offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(data));
                    break;
            }
        }

        var sheets = new List<Sheet>();
        foreach (var offset in offsets)
        {
            var sheet = new Sheet([], []);
            foreach (var (id, data) in Records(stream, offset))
            {
                if (id == 0x007D && data.Length >= 10) // COLINFO
                {
                    var first = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    var last = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)), MaxColumn);
                    var width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) / 256.0;
                    var hidden = (data[8] & 0x01) != 0 || width == 0;
                    for (var c = first; c <= last; c++)
                        sheet.Columns[c] = (width, hidden);
                }
                else if (id == 0x0208 && data.Length >= 14) // ROW
                {
                    var height = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) & 0x7FFF) / 20.0;
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
                    sheet.Rows[BinaryPrimitives.ReadUInt16LittleEndian(data)] = (height, (flags & 0x40) != 0, (flags & 0x20) != 0);
                }
            }
            sheets.Add(sheet);
        }
        return sheets;
    }

    /// <summary>The records of one substream: from its BOF up to its EOF (or the next BOF).</summary>
    private static IEnumerable<(int Id, byte[] Data)> Records(byte[] stream, int offset)
    {
        if (offset < 0 || offset + 4 > stream.Length || BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset)) != 0x0809)
            yield break;
        var position = offset;
        var first = true;
        while (position + 4 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position + 2));
            if (id == 0x000A || id == 0x0809 && !first || position + 4 + length > stream.Length)
                yield break;
            yield return (id, stream.AsSpan(position + 4, length).ToArray());
            position += 4 + length;
            first = false;
        }
    }

    // ───────────────────────── Compound file ─────────────────────────

    private const uint LastSector = 0xFFFFFFFA; // sector numbers at and above are markers (end of chain, free, ...)

    /// <summary>The "Workbook" (Excel 97+) or "Book" (Excel 5/95) stream of a compound file; null if there is none.</summary>
    private static byte[]? WorkbookStream(byte[] file)
    {
        if (file.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(file) != 0xE11AB1A1E011CFD0)
            return null;
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(32));
        if (sectorSize is not (512 or 4096) || miniSectorSize > sectorSize)
            return null;
        var sectorCount = (file.Length + sectorSize - 1) / sectorSize;
        ArraySegment<byte> Sector(uint index)
        {
            var start = (int)Math.Min((long)(index + 1) * sectorSize, file.Length);
            return new ArraySegment<byte>(file, start, Math.Min(sectorSize, file.Length - start));
        }

        // The FAT is listed by the header (109 sectors) and, in big files, a chain of DIFAT sectors.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
            fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76 + i * 4)));
        for (var difat = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(68)); difat < LastSector && fatSectors.Count < sectorCount * 2;)
        {
            var sector = Sector(difat);
            if (sector.Count < sectorSize)
                break;
            for (var i = 0; i + 4 < sector.Count; i += 4)
                fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i)));
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(sector.Count - 4));
        }
        var fat = new List<uint>();
        foreach (var sector in fatSectors.Where(s => s < LastSector))
        {
            var data = Sector(sector);
            for (var i = 0; i + 4 <= data.Count; i += 4)
                fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)));
        }

        // A stream's sectors, each pointing to the next one in the table; the step count stops broken, circular chains.
        byte[] Chain(uint start, List<uint> table, Func<uint, ArraySegment<byte>> read)
        {
            var bytes = new List<byte>();
            for (uint s = start, steps = 0; s < LastSector && s < table.Count && steps <= table.Count; s = table[(int)s], steps++)
                bytes.AddRange(read(s));
            return [.. bytes];
        }

        var directory = Chain(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48)), fat, Sector);
        (uint Start, long Size)? workbook = null;
        for (var i = 0; i + 128 <= directory.Length && workbook is null; i += 128)
        {
            var entry = directory.AsSpan(i, 128);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]) - 2, 0, 62);
            var name = Encoding.Unicode.GetString(entry[..nameLength]);
            if (entry[66] == 2 && (name.Equals("Workbook", StringComparison.OrdinalIgnoreCase) || name.Equals("Book", StringComparison.OrdinalIgnoreCase)))
                workbook = (BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[120..]));
        }
        if (workbook is not { } stream || directory.Length < 128)
            return null;

        byte[] bytes;
        if (stream.Size < BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(56)))
        {
            // Small streams live in the mini stream (held by the root entry), addressed through the mini FAT.
            var miniStream = Chain(BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(116)), fat, Sector);
            var miniFatBytes = Chain(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(60)), fat, Sector);
            var miniFat = new List<uint>();
            for (var i = 0; i + 4 <= miniFatBytes.Length; i += 4)
                miniFat.Add(BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i)));
            bytes = Chain(stream.Start, miniFat, s =>
                (long)(s + 1) * miniSectorSize <= miniStream.Length ? new ArraySegment<byte>(miniStream, (int)s * miniSectorSize, miniSectorSize) : []);
        }
        else
        {
            bytes = Chain(stream.Start, fat, Sector);
        }
        return bytes.Length >= stream.Size ? bytes[..(int)stream.Size] : null;
    }
}
