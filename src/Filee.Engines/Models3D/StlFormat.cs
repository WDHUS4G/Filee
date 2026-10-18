// STL (stereolithography): binary and ASCII. Triangles only, no units (read as millimetres), Z up, no colours,
// UVs or materials. Binary files are streamed in blocks and vertices merged by position, so a 1,000,000-triangle
// file needs about 40 MB.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Models3D;

internal static class StlFormat
{
    private const int HeaderSize = 84;
    private const int RecordSize = 50;

    /// <summary>Bytes of an ASCII STL: printable ASCII, tab, CR and LF.</summary>
    private static readonly System.Buffers.SearchValues<byte> TextBytes =
        System.Buffers.SearchValues.Create([9, 10, 13, .. Enumerable.Range(32, 95).Select(b => (byte)b)]);

    public static Model3D Read(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var length = stream.Length;
        Span<byte> header = stackalloc byte[HeaderSize];
        var headerRead = stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        var count = headerRead == HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(header[80..]) : 0;
        var exactBinary = headerRead == HeaderSize && HeaderSize + (long)RecordSize * count == length;
        var startsWithSolid = Encoding.ASCII.GetString(header[..headerRead]).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        var fitsBinary = headerRead == HeaderSize && count > 0 && HeaderSize + (long)RecordSize * count <= length;
        if (startsWithSolid && fitsBinary && !exactBinary)
        {
            // Some exporters start a binary header with "solid" and add bytes at the end: text has no control bytes.
            Span<byte> sample = stackalloc byte[512];
            var read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            startsWithSolid = !sample[..read].ContainsAnyExcept(TextBytes);
            stream.Position = HeaderSize;
        }

        Model3D model;
        if (exactBinary || (!startsWithSolid && fitsBinary))
        {
            model = ReadBinary(stream, (int)Math.Min(count, int.MaxValue / 3), progress, cancellationToken);
        }
        else if (startsWithSolid)
        {
            stream.Position = 0;
            model = ReadAscii(stream, progress, cancellationToken);
        }
        else
        {
            throw new InvalidDataException(headerRead < HeaderSize
                ? "The file is too short to be an STL file."
                : $"The binary STL file is cut short: it announces {count:N0} triangles but holds only {(length - HeaderSize) / RecordSize:N0}.");
        }

        foreach (var part in model.Parts)
            part.Transform(MeshMath.ZUpToYUp);
        return model;
    }

    private static Model3D ReadBinary(Stream stream, int count, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part = new MeshPart { Name = "STL" };
        part.Indices.Capacity = count * 3;
        var merger = new PositionMerger(part, count / 2);
        var buffer = new byte[RecordSize * 4096];
        var done = 0;
        while (done < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Math.Min(4096, count - done);
            stream.ReadExactly(buffer, 0, batch * RecordSize);
            for (var r = 0; r < batch; r++)
            {
                var record = buffer.AsSpan(r * RecordSize, RecordSize);
                var a = ReadVector(record[12..]);
                var b = ReadVector(record[24..]);
                var c = ReadVector(record[36..]);
                if (!Finite(a) || !Finite(b) || !Finite(c))
                    continue;
                part.AddTriangle(merger.Add(a), merger.Add(b), merger.Add(c));
            }
            done += batch;
            progress?.Report((double)done / count);
        }
        return new Model3D { Parts = { part } };

        static Vector3 ReadVector(ReadOnlySpan<byte> data) => new(
            BinaryPrimitives.ReadSingleLittleEndian(data),
            BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[8..]));
    }

    private static Model3D ReadAscii(Stream stream, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = new Model3D();
        MeshPart? part = null;
        PositionMerger? merger = null;
        Span<int> corners = stackalloc int[3];
        var cornerCount = 0;
        var length = Math.Max(1, stream.Length);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, 1 << 16);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var text = line.AsSpan().Trim();
            if (text.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                if (part is null)
                {
                    part = new MeshPart { Name = "STL" };
                    merger = new PositionMerger(part, 1024);
                    model.Parts.Add(part);
                }
                var v = ParseVector(text[6..], lineNumber);
                if (cornerCount < 3)
                    corners[cornerCount++] = merger!.Add(v);
            }
            else if (text.StartsWith("endloop", StringComparison.OrdinalIgnoreCase))
            {
                if (cornerCount == 3)
                    part!.AddTriangle(corners[0], corners[1], corners[2]);
                cornerCount = 0;
            }
            else if (text.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                part = new MeshPart { Name = text[5..].Trim().ToString() is { Length: > 0 } name ? name : "STL" };
                merger = new PositionMerger(part, 1024);
                model.Parts.Add(part);
            }
            if ((lineNumber & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)stream.Position / length);
            }
        }
        return model;
    }

    private static Vector3 ParseVector(ReadOnlySpan<char> text, int lineNumber)
    {
        Span<float> values = stackalloc float[3];
        var found = 0;
        foreach (var range in text.SplitAny(' ', '\t'))
        {
            var token = text[range].Trim();
            if (token.IsEmpty)
                continue;
            if (found == 3 || !float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[found++]))
                throw new InvalidDataException($"Line {lineNumber} of the STL file has an invalid vertex.");
        }
        if (found != 3)
            throw new InvalidDataException($"Line {lineNumber} of the STL file has an invalid vertex.");
        return new Vector3(values[0], values[1], values[2]);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Writes a binary STL (Z up, millimetres) with every part in one solid and facet normals from the winding.</summary>
    public static void Write(Model3D model, Stream output, CancellationToken cancellationToken)
    {
        using var stream = new BufferedStream(output, 1 << 20);
        var header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("Binary STL written by Filee").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), (uint)model.TriangleCount);
        stream.Write(header);

        Span<byte> record = stackalloc byte[RecordSize];
        foreach (var part in model.Parts)
        {
            var indices = part.IndexSpan;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                if ((t & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var a = MeshMath.ToZUp(part.Position(indices[t]));
                var b = MeshMath.ToZUp(part.Position(indices[t + 1]));
                var c = MeshMath.ToZUp(part.Position(indices[t + 2]));
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
                if (!float.IsFinite(n.X))
                    n = Vector3.Zero;
                WriteVector(record, n);
                WriteVector(record[12..], a);
                WriteVector(record[24..], b);
                WriteVector(record[36..], c);
                record[48] = 0;
                record[49] = 0;
                stream.Write(record);
            }
        }

        static void WriteVector(Span<byte> data, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(data[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(data[8..], v.Z);
        }
    }
}

/// <summary>Adds positions to a part, reusing the vertex of an identical position.</summary>
internal sealed class PositionMerger(MeshPart part, int capacity)
{
    private readonly Dictionary<Vector3, int> _seen = new(Math.Clamp(capacity, 16, 1 << 24));

    public int Add(Vector3 position)
    {
        // -0 and 0 are the same point.
        position += Vector3.Zero;
        if (_seen.TryGetValue(position, out var index))
            return index;
        index = part.AddVertex(position);
        _seen.Add(position, index);
        return index;
    }
}
