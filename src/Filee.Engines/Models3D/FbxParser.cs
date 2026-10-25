// The node tree of an Autodesk FBX file, binary or ASCII, for FbxFormat. Binary: the "Kaydara FBX Binary" header,
// node records with 32-bit offsets (64-bit from version 7.5), scalar, string, raw and array properties, arrays plain
// or zlib-compressed. ASCII: "Name: value, value { children }" with ";" comments and "*count { a: ... }" arrays.
// Animation curves, poses, deformers and the other parts the reader never looks at are skipped without decoding.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Filee.Engines.Models3D;

/// <summary>One FBX node: a name, its properties (long, double, string, bool, byte[] or an array) and its children.</summary>
internal sealed class FbxNode(string name)
{
    public string Name { get; } = name;
    public List<object> Properties { get; } = [];
    public List<FbxNode> Children { get; } = [];

    /// <summary>The first child called <paramref name="name"/>, or null.</summary>
    public FbxNode? Child(string name) => Children.Find(c => c.Name == name);

    public IEnumerable<FbxNode> ChildrenNamed(string name) => Children.Where(c => c.Name == name);

    /// <summary>Property <paramref name="index"/> as a string, or null.</summary>
    public string? Text(int index) => index < Properties.Count ? Properties[index] as string : null;
}

internal static class FbxParser
{
    internal const string Damaged = "The FBX file is cut short or damaged.";
    private const int MaxDepth = 64;

    private static ReadOnlySpan<byte> BinaryMagic => "Kaydara FBX Binary  \0"u8;

    /// <summary>Nodes no reader needs (by name, at the top level and in Objects): animation can be most of a file.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "Takes", "References", "Documents", "Version5",
        "AnimationStack", "AnimationLayer", "AnimationCurveNode", "AnimationCurve", "Pose", "Deformer",
        "NodeAttribute", "CollectionExclusive", "Constraint", "Implementation", "BindingTable",
    };

    /// <summary>Stands for a skipped node while reading.</summary>
    private static readonly FbxNode Skip = new("");

    /// <summary>Reads a whole file; the returned node (no name) has the top-level nodes as children.</summary>
    public static FbxNode Parse(byte[] data, CancellationToken cancellationToken)
    {
        if (data.AsSpan().StartsWith(BinaryMagic))
            return ParseBinary(data, cancellationToken);
        if (LooksLikeAscii(data))
            return new AsciiReader(Encoding.UTF8.GetString(data), cancellationToken).Document();
        throw new InvalidDataException("The file is not an FBX model.");
    }

    private static bool LooksLikeAscii(byte[] data)
    {
        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.Contains('\0') is false
            && (head.StartsWith("; FBX", StringComparison.Ordinal) || head.Contains("FBXHeaderExtension:", StringComparison.Ordinal));
    }

    // ───────────────────────── Binary ─────────────────────────

    private static FbxNode ParseBinary(byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length < 27)
            throw new InvalidDataException(Damaged);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(23));
        var reader = new BinaryRecords(data, Wide: version >= 7500, cancellationToken);
        var root = new FbxNode("");
        long position = 27;
        while (reader.Node(ref position, data.Length, 0) is { } node)
        {
            if (!ReferenceEquals(node, Skip))
                root.Children.Add(node);
        }
        if (root.Children.Count == 0)
            throw new InvalidDataException(Damaged);
        return root;
    }

    private readonly record struct BinaryRecords(byte[] Data, bool Wide, CancellationToken CancellationToken)
    {
        private int HeaderSize => Wide ? 25 : 13;

        /// <summary>The node at <paramref name="position"/>, <see cref="Skip"/>, or null at the end of a list.</summary>
        public FbxNode? Node(ref long position, long limit, int depth)
        {
            if (depth <= 2)
                CancellationToken.ThrowIfCancellationRequested();
            if (position + HeaderSize > limit)
            {
                // The top-level list may end without its null record; a nested one may not.
                if (depth == 0)
                    return null;
                throw new InvalidDataException(Damaged);
            }
            var header = Data.AsSpan((int)position, HeaderSize);
            long end, count, listLength;
            if (Wide)
            {
                end = (long)BinaryPrimitives.ReadUInt64LittleEndian(header);
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
                listLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            }
            else
            {
                end = BinaryPrimitives.ReadUInt32LittleEndian(header);
                count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                listLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            }
            int nameLength = header[^1];
            position += HeaderSize;
            if (end == 0)
                return null; // the null record that ends a list
            var propertiesStart = position + nameLength;
            if (depth > MaxDepth || end > limit || propertiesStart > end || listLength < 0 || listLength > end - propertiesStart
                || count < 0 || count > listLength)
                throw new InvalidDataException(Damaged);

            var name = Encoding.UTF8.GetString(Data, (int)position, nameLength);
            if (depth <= 1 && Skipped.Contains(name))
            {
                position = end;
                return Skip;
            }
            var node = new FbxNode(name);
            position = propertiesStart;
            var propertiesEnd = propertiesStart + listLength;
            for (var i = 0; i < count; i++)
                node.Properties.Add(Property(ref position, propertiesEnd));
            position = propertiesEnd;
            while (position < end && Node(ref position, end, depth + 1) is { } child)
            {
                if (!ReferenceEquals(child, Skip))
                    node.Children.Add(child);
            }
            position = end;
            return node;
        }

        private object Property(ref long position, long end)
        {
            Need(position, 1, end);
            var type = (char)Data[position++];
            switch (type)
            {
                case 'C':
                    Need(position, 1, end);
                    return Data[position++] != 0;
                case 'Y':
                    Need(position, 2, end);
                    position += 2;
                    return (long)BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)position - 2));
                case 'I':
                    Need(position, 4, end);
                    position += 4;
                    return (long)BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan((int)position - 4));
                case 'L':
                    Need(position, 8, end);
                    position += 8;
                    return BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan((int)position - 8));
                case 'F':
                    Need(position, 4, end);
                    position += 4;
                    return (double)BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)position - 4));
                case 'D':
                    Need(position, 8, end);
                    position += 8;
                    return BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan((int)position - 8));
                case 'S' or 'R':
                    {
                        Need(position, 4, end);
                        long length = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)position));
                        position += 4;
                        Need(position, length, end);
                        var start = (int)position;
                        position += length;
                        return type == 'S'
                            ? Encoding.UTF8.GetString(Data, start, (int)length)
                            : Data.AsSpan(start, (int)length).ToArray();
                    }
                case 'f' or 'd' or 'l' or 'i' or 'b':
                    return Array(type, ref position, end);
                default:
                    throw new InvalidDataException(Damaged);
            }
        }

        private object Array(char type, ref long position, long end)
        {
            Need(position, 12, end);
            var header = Data.AsSpan((int)position, 12);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var encoding = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long stored = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            position += 12;
            Need(position, stored, end);
            var size = type switch { 'f' or 'i' => 4, 'd' or 'l' => 8, _ => 1 };
            var bytes = length * size;
            // Deflate packs at most about 1032:1, so a larger length is a lie that would only allocate memory.
            if (bytes > int.MaxValue - 64 || (encoding == 0 ? bytes != stored : bytes > stored * 1032 + 64))
                throw new InvalidDataException(Damaged);
            var raw = Data.AsSpan((int)position, (int)stored);
            byte[]? inflated = null;
            if (encoding == 1)
            {
                inflated = new byte[bytes];
                try
                {
                    using var zlib = new ZLibStream(new MemoryStream(Data, (int)position, (int)stored, writable: false), CompressionMode.Decompress);
                    zlib.ReadExactly(inflated);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    throw new InvalidDataException(Damaged, ex);
                }
                raw = inflated;
            }
            else if (encoding != 0)
            {
                throw new InvalidDataException(Damaged);
            }
            position += stored;
            return type switch
            {
                'f' => MemoryMarshal.Cast<byte, float>(raw).ToArray(),
                'd' => MemoryMarshal.Cast<byte, double>(raw).ToArray(),
                'i' => MemoryMarshal.Cast<byte, int>(raw).ToArray(),
                'l' => MemoryMarshal.Cast<byte, long>(raw).ToArray(),
                _ => raw.ToArray().Select(b => b != 0).ToArray(),
            };
        }

        private static void Need(long position, long length, long end)
        {
            if (length < 0 || position + length > end)
                throw new InvalidDataException(Damaged);
        }
    }

    // ───────────────────────── ASCII ─────────────────────────

    private sealed class AsciiReader(string text, CancellationToken cancellationToken)
    {
        private int _position;
        private int _nodes;

        public FbxNode Document()
        {
            var root = new FbxNode("");
            while (true)
            {
                SkipSpace(newLines: true);
                if (_position >= text.Length)
                    break;
                var node = Node(0);
                if (!Skipped.Contains(node.Name))
                    root.Children.Add(node);
            }
            if (root.Children.Count == 0)
                throw new InvalidDataException("The file is not an FBX model.");
            return root;
        }

        private FbxNode Node(int depth)
        {
            if ((++_nodes & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDepth)
                throw Invalid();
            var start = _position;
            while (_position < text.Length && text[_position] is not (':' or '{' or '}' or ',' or '"') && !char.IsWhiteSpace(text[_position]))
                _position++;
            if (_position == start || _position >= text.Length || text[_position] != ':')
                throw Invalid();
            var node = new FbxNode(text[start.._position]);
            _position++;
            // The values of an array ("a: 1,2,3,...") go straight into one list instead of a boxed property each.
            var numbers = node.Name == "a" ? new List<double>() : null;

            SkipSpace(newLines: false);
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '{')
                {
                    _position++;
                    Children(node, depth);
                    break;
                }
                if (c is '\r' or '\n' or ';' or '}')
                    break;
                if (c == ',')
                {
                    _position++;
                    SkipSpace(newLines: true);
                    continue;
                }
                Value(node, numbers);
                SkipSpace(newLines: false);
            }
            if (numbers is not null)
                node.Properties.Add(numbers.ToArray());
            return node;
        }

        private void Children(FbxNode node, int depth)
        {
            while (true)
            {
                SkipSpace(newLines: true);
                if (_position >= text.Length)
                    throw new InvalidDataException(Damaged);
                if (text[_position] == '}')
                {
                    _position++;
                    break;
                }
                var child = Node(depth + 1);
                if (depth == 0 && Skipped.Contains(child.Name))
                    continue;
                node.Children.Add(child);
            }
            // "Vertices: *24 { a: ... }": the array becomes the node's only property.
            if (node.Children is [{ Name: "a" } array])
            {
                node.Properties.Clear();
                node.Properties.AddRange(array.Properties);
                node.Children.Clear();
            }
        }

        private void Value(FbxNode node, List<double>? numbers)
        {
            var c = text[_position];
            if (c == '"')
            {
                var end = text.IndexOf('"', _position + 1);
                if (end < 0)
                    throw new InvalidDataException(Damaged);
                node.Properties.Add(text[(_position + 1)..end].Replace("&quot;", "\"", StringComparison.Ordinal));
                _position = end + 1;
                return;
            }
            var start = _position;
            while (_position < text.Length && text[_position] is not (',' or '{' or '}' or ';') && !char.IsWhiteSpace(text[_position]))
                _position++;
            if (_position == start)
            {
                _position++; // a stray character (another kind of space)
                return;
            }
            var token = text.AsSpan(start, _position - start);
            if (token[0] == '*')
                return; // the element count of an array that follows in braces
            if (numbers is not null)
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw Invalid();
                numbers.Add(value);
            }
            else if (token.IndexOfAny('.', 'e', 'E') < 0 && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                node.Properties.Add(integer);
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                node.Properties.Add(real);
            else
                node.Properties.Add(token.ToString()); // T, Y, W, ...
        }

        private void SkipSpace(bool newLines)
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == ';' && newLines)
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end + 1;
                }
                else if (c is ' ' or '\t' or '﻿' || (newLines && c is '\r' or '\n'))
                    _position++;
                else
                    break;
            }
        }

        private InvalidDataException Invalid()
        {
            var line = 1;
            for (var i = 0; i < _position && i < text.Length; i++)
            {
                if (text[i] == '\n')
                    line++;
            }
            return new InvalidDataException($"Line {line} of the FBX file is not valid.");
        }
    }
}
