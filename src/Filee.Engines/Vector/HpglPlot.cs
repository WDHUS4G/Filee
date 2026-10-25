// HP-GL and HP-GL/2 plot files (.plt, issue #410): what AutoCAD and other programs "plot to file" for pen plotters
// and large-format printers, also cutting plotters. The pen moves become SVG polylines at the plot's real size (40
// plotter units per millimetre), and the vector engine renders PDF, PNG, … from there.
//
// Read: IN, SP / PC / NP / PW (pens and their colours and widths), PU / PD / PA / PR, CI, AA / AR (arcs as short
// lines), PE (HP-GL/2 encoded polylines, the bulk of a printer driver's plot), PM / EP / FP (polygons, filled with
// FP). A PJL / PCL wrapper around HP-GL/2 is skipped up to "enter HP-GL/2 mode" (ESC % … B), and the device-control
// sequences of HP-GL/1 plotters (ESC . …) are skipped. Not drawn: text (LB), fill patterns, raster data; user scaling
// (SC) is ignored, so a scaled plot keeps its shape but not its size.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Vector;

internal static class HpglPlot
{
    /// <summary>Plotter units per millimetre.</summary>
    private const double UnitsPerMm = 40;

    /// <summary>HP-GL/2's default pen colours: 0 white (paper), 1 black, 2 red, 3 green, 4 yellow, 5 blue, 6 magenta, 7 cyan.</summary>
    private static readonly string[] DefaultColors = ["#FFFFFF", "#000000", "#FF0000", "#00A000", "#E0C000", "#0000FF", "#FF00FF", "#00B0B0"];

    /// <summary>A line or a filled polygon, in plotter units, Y up.</summary>
    internal sealed record Stroke(string Color, double Width, List<(double X, double Y)> Points, bool Filled);

    /// <summary>Reads the drawing of a plot file. Fails with a reason when it holds no HP-GL.</summary>
    internal static List<Stroke> Read(byte[] data)
    {
        var plotter = new Plotter();
        foreach (var (name, args) in Commands(data))
            plotter.Run(name, args);
        plotter.EndLine();
        if (plotter.Strokes.Count == 0)
            throw new InvalidDataException("No HP-GL drawing found: the plot file has no pen moves (it may be PostScript or PCL raster output).");
        return plotter.Strokes;
    }

    /// <summary>The drawing as SVG at its real size, with a small margin and white paper.</summary>
    internal static string ToSvg(byte[] data)
    {
        var strokes = Read(data);
        var points = strokes.SelectMany(s => s.Points).ToList();
        var margin = 2 * UnitsPerMm;
        double left = points.Min(p => p.X) - margin, right = points.Max(p => p.X) + margin;
        double bottom = points.Min(p => p.Y) - margin, top = points.Max(p => p.Y) + margin;
        double width = right - left, height = top - bottom;
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{N(width / UnitsPerMm)}mm" height="{N(height / UnitsPerMm)}mm" viewBox="0 0 {N(width)} {N(height)}">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect width="{N(width)}" height="{N(height)}" fill="#FFFFFF"/>""");
        foreach (var stroke in strokes)
        {
            var path = string.Join(' ', stroke.Points.Select(p => $"{N(p.X - left)},{N(top - p.Y)}"));
            svg.Append(stroke.Filled
                ? $"""<polygon points="{path}" fill="{stroke.Color}"/>"""
                : $"""<polyline points="{path}" fill="none" stroke="{stroke.Color}" stroke-width="{N(stroke.Width)}" stroke-linecap="round" stroke-linejoin="round"/>""");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    // ───────────────────────── Commands ─────────────────────────

    /// <summary>
    /// Splits the plot into commands: two letters and their arguments up to ';' or the next command (quoted text such
    /// as a CO comment included). PE's arguments are its encoded bytes; LB's run to the label terminator (ETX). Any
    /// command may come first.
    /// </summary>
    internal static IEnumerable<(string Name, string Args)> Commands(byte[] s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == 27)
            {
                // ESC . is an HP-GL/1 device-control sequence; any other escape (PCL, PJL) leaves HP-GL/2, which goes on
                // where the plot enters it again (ESC % … B).
                i = i + 1 < s.Length && s[i + 1] == '.' ? DeviceControlEnd(s, i) : EntryPoint(s, i);
                continue;
            }
            if (c <= 32 || c == ';' || !char.IsAsciiLetterUpper((char)c) && !char.IsAsciiLetterLower((char)c))
            {
                i++;
                continue;
            }
            if (i + 1 >= s.Length)
                yield break;
            var name = Encoding.ASCII.GetString(s, i, 2).ToUpperInvariant();
            var start = i + 2;
            var end = start;
            if (name == "PE")
                while (end < s.Length && s[end] != ';' && s[end] != 27) end++;
            else if (name == "LB")
                while (end < s.Length && s[end] != 3) end++;
            else
                while (end < s.Length && s[end] != ';' && s[end] != 27 && !char.IsAsciiLetter((char)s[end]))
                    end = s[end] == '"' ? Math.Min(s.Length, Array.IndexOf(s, (byte)'"', end + 1) is var close and >= 0 ? close + 1 : s.Length) : end + 1;
            yield return (name, Encoding.Latin1.GetString(s, start, end - start));
            i = end < s.Length && s[end] is (byte)';' or 3 ? end + 1 : end;
        }
    }

    /// <summary>
    /// Index after a device-control sequence at <paramref name="at"/>: ESC . and one character (ESC . ( turns the
    /// plotter on), with its numeric parameters when they end in ':' (ESC . I 81;;17:, ESC . N ;19:).
    /// </summary>
    private static int DeviceControlEnd(byte[] s, int at)
    {
        var next = at + 3;
        if (next > s.Length)
            return s.Length;
        var end = next;
        while (end < s.Length && (char.IsAsciiDigit((char)s[end]) || s[end] is (byte)';' or (byte)'-' or (byte)'.'))
            end++;
        return end < s.Length && s[end] == ':' ? end + 1 : next;
    }

    /// <summary>Index after the next "enter HP-GL/2 mode" (ESC % 0-3 B, also ESC % -1 B) from <paramref name="from"/>, or the end.</summary>
    private static int EntryPoint(byte[] s, int from)
    {
        for (var i = from; i + 3 < s.Length; i++)
        {
            if (s[i] != 27 || s[i + 1] != '%')
                continue;
            var j = s[i + 2] == '-' ? i + 3 : i + 2;
            if (j + 1 < s.Length && s[j] is >= (byte)'0' and <= (byte)'3' && s[j + 1] == 'B')
                return j + 2;
        }
        return s.Length;
    }

    private static List<double> Numbers(string args) =>
        [.. args.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v))];

    /// <summary>
    /// The numbers of a PE command from <paramref name="start"/> until a flag or the end. Base 64 (or base 32 in
    /// 7-bit mode): digits 63.. are followed by a terminating digit (191.. or 95..), least significant first; the
    /// lowest bit is the sign.
    /// </summary>
    internal static List<double> DecodeNumbers(string data, ref int start, int fractionBits, int numberBase)
    {
        var terminator = numberBase == 64 ? 191 : 95;
        var values = new List<double>();
        long value = 0, scale = 1;
        for (; start < data.Length; start++)
        {
            int c = data[start];
            if (c < 63)
                break;
            if (c >= terminator)
            {
                value += (c - terminator) * scale;
                var signed = (value & 1) == 1 ? -(value >> 1) : value >> 1;
                values.Add(signed / (double)(1L << fractionBits));
                value = 0;
                scale = 1;
            }
            else
            {
                value += (c - 63) * scale;
                scale *= numberBase;
            }
        }
        return values;
    }

    // ───────────────────────── Plotter ─────────────────────────

    private sealed class Plotter
    {
        private readonly Dictionary<int, string> _colors = [];
        private readonly Dictionary<int, double> _widths = [];
        private List<(double X, double Y)>? _line;
        private bool _penDown;
        private bool _relative;
        private int _pen = 1;
        private (double X, double Y) _at;
        private List<List<(double X, double Y)>>? _polygon;
        private bool _inPolygon;

        public List<Stroke> Strokes { get; } = [];

        /// <summary>The pen's colour; pens past the default eight draw black.</summary>
        private string Color => _colors.TryGetValue(_pen, out var c) ? c : DefaultColors[_pen is > 0 and < 8 ? _pen : 1];

        /// <summary>Pen width in plotter units (default 0.35 mm).</summary>
        private double Width => _widths.TryGetValue(_pen, out var w) ? w : 0.35 * UnitsPerMm;

        public void Run(string name, string args)
        {
            switch (name)
            {
                case "IN":
                    EndLine();
                    _penDown = _relative = false;
                    _pen = 1;
                    _at = (0, 0);
                    break;
                case "SP":
                    EndLine();
                    _pen = Numbers(args) is [var pen, ..] ? (int)pen : 0;
                    break;
                case "PC":
                    if (Numbers(args) is [var p, var r, var g, var b, ..])
                        _colors[(int)p] = $"#{Channel(r)}{Channel(g)}{Channel(b)}";
                    break;
                case "PW":
                    // Width in millimetres, for one pen or all of them.
                    var width = Numbers(args);
                    if (width is [var mm, var forPen, ..])
                        _widths[(int)forPen] = Math.Max(0.05, mm) * UnitsPerMm;
                    else if (width is [var all])
                        foreach (var any in Enumerable.Range(0, 256))
                            _widths[any] = Math.Max(0.05, all) * UnitsPerMm;
                    EndLine();
                    break;
                case "PU" or "PD":
                    EndLine(keepPolygon: true);
                    _penDown = name == "PD";
                    MoveAll(Numbers(args));
                    break;
                case "PA" or "PR":
                    _relative = name == "PR";
                    MoveAll(Numbers(args));
                    break;
                case "CI":
                    if (Numbers(args) is [var radius, ..])
                        Circle(radius);
                    break;
                case "AA" or "AR":
                    if (Numbers(args) is [var cx, var cy, var sweep, ..])
                        Arc(name == "AR" ? (_at.X + cx, _at.Y + cy) : (cx, cy), sweep);
                    break;
                case "PE":
                    Encoded(args);
                    break;
                case "PM":
                    // PM0 starts a polygon (its buffer is cleared), PM1 closes an outline of it, PM2 ends it.
                    EndLine(keepPolygon: true);
                    var mode = Numbers(args) is [var m, ..] ? (int)m : 0;
                    if (mode == 0)
                        _polygon = [];
                    _polygon ??= [];
                    _inPolygon = mode != 2;
                    break;
                case "EP" or "FP":
                    EndLine();
                    foreach (var outline in _polygon ?? [])
                        if (outline.Count > 1)
                            Strokes.Add(new Stroke(Color, Width, outline, Filled: name == "FP"));
                    break;
            }
        }

        private static string Channel(double value) => ((int)Math.Clamp(value, 0, 255)).ToString("X2", CultureInfo.InvariantCulture);

        private void MoveAll(List<double> coordinates)
        {
            for (var i = 0; i + 1 < coordinates.Count; i += 2)
                MoveTo(_relative ? (_at.X + coordinates[i], _at.Y + coordinates[i + 1]) : (coordinates[i], coordinates[i + 1]), _penDown);
        }

        private void MoveTo((double X, double Y) to, bool draw)
        {
            if (draw)
            {
                _line ??= [_at];
                _line.Add(to);
            }
            else
            {
                EndLine(keepPolygon: true);
            }
            _at = to;
        }

        /// <summary>
        /// Ends the line being drawn. In polygon mode it becomes an outline of the polygon (drawn by EP / FP), else a
        /// stroke.
        /// </summary>
        public void EndLine(bool keepPolygon = false)
        {
            if (_line is { Count: > 1 } line)
            {
                if (_inPolygon && keepPolygon)
                    _polygon!.Add(line);
                else if (_pen != 0)
                    Strokes.Add(new Stroke(Color, Width, line, Filled: false));
            }
            _line = null;
        }

        private void Circle(double radius)
        {
            EndLine(keepPolygon: true);
            var center = _at;
            var points = Enumerable.Range(0, 73)
                .Select(i => (center.X + radius * Math.Cos(i * Math.PI / 36), center.Y + radius * Math.Sin(i * Math.PI / 36))).ToList();
            if (_inPolygon)
                _polygon!.Add(points);
            else if (_pen != 0)
                Strokes.Add(new Stroke(Color, Width, points, Filled: false));
        }

        private void Arc((double X, double Y) center, double sweepDegrees)
        {
            var radius = Math.Sqrt(Math.Pow(_at.X - center.X, 2) + Math.Pow(_at.Y - center.Y, 2));
            var start = Math.Atan2(_at.Y - center.Y, _at.X - center.X);
            var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepDegrees) / 5));
            for (var i = 1; i <= steps; i++)
            {
                var angle = start + sweepDegrees * Math.PI / 180 * i / steps;
                MoveTo((center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), _penDown);
            }
        }

        /// <summary>
        /// PE: flags ':' (select pen), '&lt;' (the next point is a move), '&gt;' (fraction bits), '=' (the next point is
        /// absolute), '7' (7-bit mode, base 32); points are relative and drawn otherwise. The pen stays down after it.
        /// </summary>
        private void Encoded(string data)
        {
            var penUp = false;
            var absolute = false;
            var fractionBits = 0;
            var numberBase = 64;
            var queue = new List<double>();
            var i = 0;
            while (i < data.Length)
            {
                var flag = data[i];
                if (flag is ':' or '<' or '>' or '=' or '7')
                {
                    i++;
                    switch (flag)
                    {
                        case ':':
                            var pen = DecodeNumbers(data, ref i, 0, numberBase);
                            if (pen.Count > 0)
                            {
                                EndLine();
                                _pen = (int)pen[0];
                                queue.AddRange(pen.Skip(1));
                            }
                            break;
                        case '<':
                            penUp = true;
                            break;
                        case '>':
                            var bits = DecodeNumbers(data, ref i, 0, numberBase);
                            if (bits.Count > 0)
                            {
                                fractionBits = (int)bits[0];
                                queue.AddRange(bits.Skip(1));
                            }
                            break;
                        case '=':
                            absolute = true;
                            break;
                        case '7':
                            numberBase = 32;
                            break;
                    }
                }
                else if (data[i] < 63)
                {
                    i++; // not part of PE
                }
                else
                {
                    queue.AddRange(DecodeNumbers(data, ref i, fractionBits, numberBase));
                }

                for (var k = 0; k + 1 < queue.Count; k += 2)
                {
                    var to = absolute ? (queue[k], queue[k + 1]) : (_at.X + queue[k], _at.Y + queue[k + 1]);
                    MoveTo(to, draw: !penUp);
                    penUp = absolute = false;
                }
                if (queue.Count % 2 == 0)
                    queue.Clear();
                else
                    queue.RemoveRange(0, queue.Count - 1);
            }
            _penDown = true;
        }
    }
}
