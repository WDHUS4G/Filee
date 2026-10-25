// Well-known text (WKT / EWKT) for geometries: POINT, LINESTRING, POLYGON, their MULTI* forms and GEOMETRYCOLLECTION,
// with Z (or M, which is dropped) and EMPTY. Used for the geometry column of a CSV. An "SRID=4326;" prefix is accepted
// and ignored (the CSV reader treats coordinates as longitude / latitude).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Geo;

internal static class WktFormat
{
    private const int MaxDepth = 16;

    /// <summary>Parses WKT; <see cref="FormatException"/> says what is wrong. An empty geometry gives null.</summary>
    public static GeoGeometry? Parse(string text)
    {
        var parser = new Parser(text);
        var geometry = parser.ReadGeometry(0);
        parser.ExpectEnd();
        return geometry;
    }

    // ---- Writing ----

    public static string Write(GeoGeometry geometry)
    {
        var builder = new StringBuilder();
        Write(builder, geometry);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, GeoGeometry geometry)
    {
        var z = geometry.HasZ;
        builder.Append(geometry.TypeName.ToUpperInvariant()).Append(z ? " Z " : " ");
        switch (geometry)
        {
            case GeoPoint point:
                builder.Append('(');
                Position(builder, point.Position, z);
                builder.Append(')');
                break;
            case GeoMultiPoint points:
                builder.Append('(');
                Join(builder, points.Points, c => { builder.Append('('); Position(builder, c, z); builder.Append(')'); });
                builder.Append(')');
                break;
            case GeoLineString line:
                Line(builder, line.Points, z);
                break;
            case GeoMultiLineString lines:
                builder.Append('(');
                Join(builder, lines.Lines, l => Line(builder, l, z));
                builder.Append(')');
                break;
            case GeoPolygon polygon:
                Polygon(builder, polygon, z);
                break;
            case GeoMultiPolygon polygons:
                builder.Append('(');
                Join(builder, polygons.Polygons, p => Polygon(builder, p, z));
                builder.Append(')');
                break;
            case GeoCollection collection:
                builder.Length -= z ? 3 : 1; // "GEOMETRYCOLLECTION" has no Z of its own: members carry it
                builder.Append(" (");
                Join(builder, collection.Geometries, g => Write(builder, g));
                builder.Append(')');
                break;
        }
    }

    private static void Polygon(StringBuilder builder, GeoPolygon polygon, bool z)
    {
        builder.Append('(');
        var index = 0;
        Join(builder, polygon.Rings, ring => Line(builder, GeoRings.Oriented(ring, counterClockwise: index++ == 0), z));
        builder.Append(')');
    }

    private static void Line(StringBuilder builder, IReadOnlyList<Coord> points, bool z)
    {
        builder.Append('(');
        Join(builder, points, c => Position(builder, c, z));
        builder.Append(')');
    }

    private static void Position(StringBuilder builder, Coord c, bool z)
    {
        builder.Append(GeoText.Degrees(c.X)).Append(' ').Append(GeoText.Degrees(c.Y));
        if (z)
            builder.Append(' ').Append(GeoText.Height(c.HasZ ? c.Z : 0));
    }

    private static void Join<T>(StringBuilder builder, IEnumerable<T> items, Action<T> write)
    {
        var first = true;
        foreach (var item in items)
        {
            if (!first)
                builder.Append(", ");
            first = false;
            write(item);
        }
    }

    // ---- Parsing ----

    private sealed class Parser(string text)
    {
        private int _position;

        public GeoGeometry? ReadGeometry(int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("geometry nested too deeply");
            SkipSpace();
            // EWKT: "SRID=4326;POINT(...)".
            if (text.AsSpan(_position).StartsWith("SRID=", StringComparison.OrdinalIgnoreCase))
            {
                var semicolon = text.IndexOf(';', _position);
                if (semicolon < 0)
                    throw new FormatException("SRID prefix without ';'");
                _position = semicolon + 1;
                SkipSpace();
            }
            var word = ReadWord();
            if (word.Length == 0)
                throw new FormatException($"expected a geometry type at position {_position}");
            var kind = word.ToUpperInvariant();
            // "POINTZ", "POINT Z", "POINT ZM", "POINT M".
            var hasZ = false;
            var hasM = false;
            foreach (var suffix in new[] { "ZM", "Z", "M" })
            {
                if (kind.Length > suffix.Length && kind.EndsWith(suffix, StringComparison.Ordinal) && kind[..^suffix.Length] is "POINT" or "LINESTRING" or "POLYGON"
                    or "MULTIPOINT" or "MULTILINESTRING" or "MULTIPOLYGON" or "GEOMETRYCOLLECTION")
                {
                    hasZ = suffix.Contains('Z', StringComparison.Ordinal);
                    hasM = suffix.Contains('M', StringComparison.Ordinal);
                    kind = kind[..^suffix.Length];
                    break;
                }
            }
            SkipSpace();
            var flag = PeekWord();
            if (flag is "Z" or "ZM" or "M" or "z" or "zm" or "m")
            {
                ReadWord();
                hasZ = flag.Contains('Z', StringComparison.OrdinalIgnoreCase);
                hasM = flag.Contains('M', StringComparison.OrdinalIgnoreCase);
                SkipSpace();
            }
            var dimension = new Dimension(hasZ, hasM);
            if (PeekWord().Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
            {
                ReadWord();
                return null;
            }
            switch (kind)
            {
                case "POINT":
                    Open();
                    var point = ReadPosition(dimension);
                    Close();
                    return new GeoPoint(point);
                case "LINESTRING":
                    {
                        var line = ReadLine(dimension);
                        return line.Count < 2 ? throw new FormatException("a LINESTRING needs two positions") : new GeoLineString(line);
                    }
                case "POLYGON":
                    return ReadPolygon(dimension);
                case "MULTIPOINT":
                    {
                        Open();
                        var points = new List<Coord>();
                        do
                        {
                            SkipSpace();
                            if (Peek() == '(')
                            {
                                Open();
                                points.Add(ReadPosition(dimension));
                                Close();
                            }
                            else if (PeekWord().Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
                            {
                                ReadWord();
                            }
                            else
                            {
                                points.Add(ReadPosition(dimension));
                            }
                        }
                        while (Comma());
                        Close();
                        return points.Count == 0 ? null : new GeoMultiPoint(points);
                    }
                case "MULTILINESTRING":
                    {
                        Open();
                        var lines = new List<IReadOnlyList<Coord>>();
                        do
                        {
                            if (TryEmpty())
                                continue;
                            var line = ReadLine(dimension);
                            if (line.Count >= 2)
                                lines.Add(line);
                        }
                        while (Comma());
                        Close();
                        return lines.Count == 0 ? null : new GeoMultiLineString(lines);
                    }
                case "MULTIPOLYGON":
                    {
                        Open();
                        var polygons = new List<GeoPolygon>();
                        do
                        {
                            if (TryEmpty())
                                continue;
                            if (ReadPolygon(dimension) is { } polygon)
                                polygons.Add(polygon);
                        }
                        while (Comma());
                        Close();
                        return polygons.Count == 0 ? null : new GeoMultiPolygon(polygons);
                    }
                case "GEOMETRYCOLLECTION":
                    {
                        Open();
                        var parts = new List<GeoGeometry>();
                        do
                        {
                            if (ReadGeometry(depth + 1) is { } part)
                                parts.Add(part);
                        }
                        while (Comma());
                        Close();
                        return parts.Count == 0 ? null : new GeoCollection(parts);
                    }
                default:
                    throw new FormatException($"unknown geometry type \"{word}\"");
            }
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position < text.Length)
                throw new FormatException($"unexpected text at position {_position}");
        }

        private GeoPolygon? ReadPolygon(Dimension dimension)
        {
            Open();
            var rings = new List<IReadOnlyList<Coord>>();
            do
            {
                if (TryEmpty())
                    continue;
                if (GeoRings.Close(ReadLine(dimension)) is { } ring)
                    rings.Add(ring);
            }
            while (Comma());
            Close();
            return rings.Count == 0 ? null : new GeoPolygon(rings);
        }

        private List<Coord> ReadLine(Dimension dimension)
        {
            Open();
            var line = new List<Coord>();
            do
            {
                line.Add(ReadPosition(dimension));
            }
            while (Comma());
            Close();
            return line;
        }

        private Coord ReadPosition(Dimension dimension)
        {
            var numbers = new List<double>(4);
            while (true)
            {
                SkipSpace();
                var start = _position;
                while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] is '-' or '+' or '.' or 'e' or 'E'))
                    _position++;
                if (_position == start)
                    break;
                if (!double.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new FormatException($"bad number at position {start}");
                numbers.Add(number);
            }
            if (numbers.Count < 2)
                throw new FormatException($"a position needs two numbers (position {_position})");
            // X Y [Z] [M]: with M only (POINT M) the third number is the measure; without a flag three numbers mean Z.
            var z = numbers.Count >= 3 && (dimension.Z || !dimension.M) ? numbers[2] : double.NaN;
            return new Coord(numbers[0], numbers[1], z);
        }

        private bool TryEmpty()
        {
            SkipSpace();
            if (!PeekWord().Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
                return false;
            ReadWord();
            return true;
        }

        private void Open()
        {
            SkipSpace();
            if (Peek() != '(')
                throw new FormatException($"expected '(' at position {_position}");
            _position++;
        }

        private void Close()
        {
            SkipSpace();
            if (Peek() != ')')
                throw new FormatException($"expected ')' at position {_position}");
            _position++;
        }

        private bool Comma()
        {
            SkipSpace();
            if (Peek() != ',')
                return false;
            _position++;
            return true;
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                _position++;
        }

        private string ReadWord()
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiLetter(text[_position]))
                _position++;
            return text[start.._position];
        }

        private string PeekWord()
        {
            var saved = _position;
            var word = ReadWord();
            _position = saved;
            return word;
        }
    }

    private readonly record struct Dimension(bool Z, bool M);
}
