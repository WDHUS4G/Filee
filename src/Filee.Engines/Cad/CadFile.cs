// Reads DWG / DXF files and writes DXF / DWG with ACadSharp (MIT). Output is AutoCAD 2018 (AC1032) unless the preset
// asks for an older version (#489): 2018 is the current DWG format (AutoCAD 2018–2026, DWG TrueView, BricsCAD,
// LibreCAD ...), and its text is UTF-8. Before 2007 text is in a code page, which CadCodePages picks from the text.

using System.Text;
using ACadSharp;
using ACadSharp.Exceptions;
using ACadSharp.IO;
using Filee.Core.Presets;

namespace Filee.Engines.Cad;

/// <summary>DWG and DXF input and output.</summary>
internal static class CadFile
{
    /// <summary>Version a DWG or DXF is written in unless the preset says otherwise.</summary>
    public const ACadVersion OutputVersion = ACadVersion.AC1032;

    /// <summary>
    /// The version to write for the preset's choice. "Same as the source" keeps the drawing's version within what
    /// can be written (older than 2000 becomes 2000). ACadSharp can't write a 2007 DWG: chosen, it is written as 2004,
    /// which AutoCAD 2007 opens; kept from a 2007 source, as 2010, which keeps its text in UTF-8.
    /// </summary>
    internal static ACadVersion VersionFor(CadVersion choice, ACadVersion source, string format)
    {
        var version = choice switch
        {
            CadVersion.AutoCad2013 => ACadVersion.AC1027,
            CadVersion.AutoCad2010 => ACadVersion.AC1024,
            CadVersion.AutoCad2007 => ACadVersion.AC1021,
            CadVersion.AutoCad2004 => ACadVersion.AC1018,
            CadVersion.AutoCad2000 => ACadVersion.AC1015,
            CadVersion.SameAsSource => source < ACadVersion.AC1015 ? ACadVersion.AC1015 : source > OutputVersion ? OutputVersion : source,
            _ => OutputVersion,
        };
        if (format == "dwg" && version == ACadVersion.AC1021)
            return choice == CadVersion.SameAsSource ? ACadVersion.AC1024 : ACadVersion.AC1018;
        return version;
    }

    /// <summary>
    /// Reads a drawing. Binary and ASCII DXF are detected automatically. Throws <see cref="InvalidDataException"/>
    /// with a readable message for files that are not drawings, are damaged, or use an unsupported version.
    /// </summary>
    public static CadDocument Read(string path, string format, Action<string>? warning = null)
    {
        void Notify(object? sender, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Warning or NotificationType.Error)
                warning?.Invoke(e.Message);
        }

        var kind = format == "dwg" ? "DWG" : "DXF";
        try
        {
            if (format == "dwg")
            {
                RequireDwgSignature(path);
                return DwgReader.Read(path, new DwgReaderConfiguration { Failsafe = true }, Notify);
            }

            // CreateDefaults adds missing tables (layer 0, standard styles, model space) so that files from simple
            // DXF writers can be rendered and saved as DWG.
            var configuration = new DxfReaderConfiguration { Failsafe = true, CreateDefaults = true };
            using var stream = OpenDxf(path);
            using var reader = new DxfReader(stream, Notify) { Configuration = configuration };
            return reader.Read();
        }
        catch (CadNotSupportedException ex)
        {
            throw new InvalidDataException($"This {kind} version is not supported ({ex.Message}).", ex);
        }
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or UnauthorizedAccessException or OutOfMemoryException)
                                   && (ex is not IOException || ex is EndOfStreamException))
        {
            // File system errors (locked, missing) keep their own message; a truncated file is damage.
            throw new InvalidDataException($"The file is not a valid {kind} drawing or is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the drawing as an ASCII DXF or a DWG, in AutoCAD 2018 or the version the preset chose. Returns how many
    /// objects that version doesn't have were left out.
    /// </summary>
    public static int Write(CadDocument document, string path, string format, CadVersion version = CadVersion.AutoCad2018,
        Action<string>? warning = null)
    {
        void Notify(object? sender, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Warning or NotificationType.Error)
                warning?.Invoke(e.Message);
        }

        document.Header.Version = VersionFor(version, document.Header.Version, format);
        try
        {
            var codePage = document.Header.Version < ACadVersion.AC1021 ? CadCodePages.Prepare(document) : 0;
            var leftOut = DropNewerEntities(document, warning);
            DropStraySequenceEnds(document);
            if (format == "dwg")
            {
                DropUnwritableDimensions(document, warning);
                DwgWriter.Write(path, document, new DwgWriterConfiguration(), Notify);
            }
            else
            {
                DxfWriter.Write(path, document, binary: false, new DxfWriterConfiguration(), Notify);
                if (codePage == 949)
                    NameKoreanCodePage(path);
            }
            return leftOut;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            TryDelete(path);
            throw new InvalidDataException($"The drawing could not be written as {format.ToUpperInvariant()}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Leaves out the objects the version being written doesn't have (AutoCAD before it would not open the file):
    /// tables and arc dimensions before 2004 (AC1018), multileaders and PDF underlays before 2007 (AC1021), meshes before
    /// 2010 (AC1024). Returns how many.
    /// </summary>
    private static int DropNewerEntities(CadDocument document, Action<string>? warning)
    {
        var version = document.Header.Version;
        bool TooNew(ACadSharp.Entities.Entity entity) => entity switch
        {
            ACadSharp.Entities.TableEntity or ACadSharp.Entities.DimensionArc => version < ACadVersion.AC1018,
            ACadSharp.Entities.MultiLeader or ACadSharp.Entities.PdfUnderlay => version < ACadVersion.AC1021,
            ACadSharp.Entities.Mesh => version < ACadVersion.AC1024,
            _ => false,
        };
        var count = 0;
        foreach (var record in document.BlockRecords)
            foreach (var entity in record.Entities.Where(TooNew).ToList())
            {
                record.Entities.Remove(entity);
                count++;
            }
        if (count > 0)
            warning?.Invoke($"{count} object(s) newer than {version} were left out.");
        return count;
    }

    /// <summary>
    /// R12 DXF files can leave SEQEND markers behind as loose entities; the writers reject them (DWG) or would
    /// write them as garbage (DXF). They carry no geometry.
    /// </summary>
    private static void DropStraySequenceEnds(CadDocument document)
    {
        foreach (var record in document.BlockRecords)
            foreach (var marker in record.Entities.OfType<ACadSharp.Entities.Seqend>().ToList())
                record.Entities.Remove(marker);
    }

    /// <summary>
    /// DWG stores each dimension's measured value, which ACadSharp computes from the definition points and cannot
    /// compute for degenerate ones (e.g. an angular dimension with coincident points, seen in old R12 files).
    /// Those dimensions are left out instead of failing the whole file.
    /// </summary>
    private static void DropUnwritableDimensions(CadDocument document, Action<string>? warning)
    {
        foreach (var record in document.BlockRecords)
        {
            var broken = new List<ACadSharp.Entities.Dimension>();
            foreach (var dimension in record.Entities.OfType<ACadSharp.Entities.Dimension>())
            {
                try
                {
                    _ = dimension.Measurement;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArithmeticException)
                {
                    broken.Add(dimension);
                }
            }
            foreach (var dimension in broken)
                record.Entities.Remove(dimension);
            if (broken.Count > 0)
                warning?.Invoke($"{broken.Count} degenerate dimension(s) in {record.Name} were left out of the DWG.");
        }
    }

    /// <summary>DWG files start with "AC10xx"; anything else fails early with a clear message.</summary>
    private static void RequireDwgSignature(string path)
    {
        Span<byte> head = stackalloc byte[6];
        using var stream = File.OpenRead(path);
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length
            || head[0] != 'A' || head[1] != 'C' || !char.IsAsciiDigit((char)head[2]))
            throw new InvalidDataException("The file is not a DWG drawing (it does not start with an AutoCAD version tag).");
    }

    /// <summary>
    /// Opens a DXF for reading. Korean AutoCAD saves pre-2007 DXF with <c>$DWGCODEPAGE ANSI_949</c>, a name
    /// ACadSharp 3.8 does not map (it would decode the text as UTF-8); such headers are rewritten to the
    /// equivalent name it knows, in memory.
    /// </summary>
    private static Stream OpenDxf(string path)
    {
        var stream = File.OpenRead(path);
        var head = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
        stream.ReadExactly(head);
        stream.Position = 0;

        var text = Encoding.ASCII.GetString(head);
        var key = text.IndexOf("$DWGCODEPAGE", StringComparison.Ordinal);
        if (key < 0)
            return stream;
        var value = text.IndexOf("ANSI_949", key, Math.Min(64, text.Length - key), StringComparison.OrdinalIgnoreCase);
        if (value < 0)
            return stream;

        using (stream)
        {
            var bytes = File.ReadAllBytes(path);
            var replacement = Encoding.ASCII.GetBytes("KCS5601");
            var patched = new byte[bytes.Length - 8 + replacement.Length];
            bytes.AsSpan(0, value).CopyTo(patched);
            replacement.CopyTo(patched.AsSpan(value));
            bytes.AsSpan(value + 8).CopyTo(patched.AsSpan(value + replacement.Length));
            return new MemoryStream(patched, writable: false);
        }
    }

    /// <summary>
    /// The DXF's $DWGCODEPAGE as AutoCAD writes it for Korean, ANSI_949, in place of the KCS5601 that ACadSharp needs
    /// to encode the text in it (the reverse of <see cref="OpenDxf"/>).
    /// </summary>
    private static void NameKoreanCodePage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64 * 1024));
        var key = head.IndexOf("$DWGCODEPAGE", StringComparison.Ordinal);
        var value = key < 0 ? -1 : head.IndexOf("KCS5601", key, Math.Min(64, head.Length - key), StringComparison.Ordinal);
        if (value < 0)
            return;
        var name = "ANSI_949"u8;
        using var stream = File.Create(path);
        stream.Write(bytes, 0, value);
        stream.Write(name);
        stream.Write(bytes, value + 7, bytes.Length - value - 7);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
