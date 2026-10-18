// Runs Type 2 charstrings (Adobe Technical Note #5177) and records the outline as cubic Bézier contours. Hints are
// parsed only as far as needed to skip them; subroutines, flex, the arithmetic operators and seac accents work.

namespace Filee.Engines.Fonts.Cff;

/// <summary>A 2D point or vector in font units.</summary>
internal readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
}

/// <summary>A line (<see cref="IsCurve"/> false) or cubic Bézier segment ending at <see cref="End"/>.</summary>
internal readonly record struct PathSegment(bool IsCurve, Vec2 C1, Vec2 C2, Vec2 End);

/// <summary>A closed contour: a start point and the segments that follow it (the closing line is implied).</summary>
internal sealed class PathContour(Vec2 start)
{
    public Vec2 Start { get; set; } = start;
    public List<PathSegment> Segments { get; } = [];
}

/// <summary>Interprets the charstrings of one CFF font. Not thread-safe: use one instance per thread.</summary>
internal sealed class Type2Interpreter(CffFont font)
{
    private const int MaxSubrDepth = 10;

    private readonly double[] _stack = new double[513];
    private readonly double[] _transient = new double[32];
    private readonly int _globalBias = Bias(font.GlobalSubrs.Length);
    private List<PathContour> _contours = [];
    private PathContour? _current;
    private CffPrivate _private = new();
    private int _localBias;
    private int _count;
    private int _stems;
    private bool _widthParsed;
    private bool _ended;
    private double _x;
    private double _y;
    private (double Adx, double Ady, int Base, int Accent)? _seac;

    /// <summary>Runs a glyph's charstring and returns its contours (seac accents are drawn in place).</summary>
    public List<PathContour> Run(int glyph) => Run(glyph, allowSeac: true);

    private List<PathContour> Run(int glyph, bool allowSeac)
    {
        if (glyph < 0 || glyph >= font.GlyphCount)
            throw new InvalidDataException("A CFF accent refers to a missing glyph.");
        _contours = [];
        _current = null;
        _private = font.Privates[Math.Min(font.FontDictIndex(glyph), font.Privates.Length - 1)];
        _localBias = Bias(_private.Subrs.Length);
        _count = _stems = 0;
        _widthParsed = _ended = false;
        _x = _y = 0;
        _seac = null;

        Execute(font.CharStrings[glyph], 0);
        ClosePath();

        var contours = _contours;
        if (_seac is { } seac)
        {
            if (!allowSeac)
                throw new InvalidDataException("A CFF accent is itself an accented glyph.");
            var baseGlyph = font.GlyphBySid(CffStandardData.StandardEncoding[seac.Base & 0xFF]);
            var accentGlyph = font.GlyphBySid(CffStandardData.StandardEncoding[seac.Accent & 0xFF]);
            contours.AddRange(Run(baseGlyph, allowSeac: false));
            foreach (var contour in Run(accentGlyph, allowSeac: false))
                contours.Add(Translate(contour, new Vec2(seac.Adx, seac.Ady)));
        }
        return contours;
    }

    private static PathContour Translate(PathContour contour, Vec2 offset)
    {
        var moved = new PathContour(contour.Start + offset);
        foreach (var s in contour.Segments)
            moved.Segments.Add(s with { C1 = s.C1 + offset, C2 = s.C2 + offset, End = s.End + offset });
        return moved;
    }

    /// <summary>Subroutine number bias, which depends on how many subroutines there are.</summary>
    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private void Execute(byte[] code, int depth)
    {
        if (depth > MaxSubrDepth)
            throw new InvalidDataException("CFF subroutines are nested too deeply.");
        var r = new BigEndianReader(code);
        while (r.Remaining > 0 && !_ended)
        {
            int b0 = r.U8();
            if (b0 >= 32 || b0 == 28)
            {
                Push(b0 switch
                {
                    28 => r.I16(),
                    <= 246 => b0 - 139,
                    <= 250 => (b0 - 247) * 256 + r.U8() + 108,
                    <= 254 => -(b0 - 251) * 256 - r.U8() - 108,
                    _ => r.I32() / 65536.0, // 255: 16.16 fixed
                });
                continue;
            }

            switch (b0)
            {
                case 1 or 3 or 18 or 23: // hstem, vstem, hstemhm, vstemhm
                    Stems();
                    break;
                case 19 or 20: // hintmask, cntrmask: pending arguments are an implicit vstemhm
                    Stems();
                    r.Skip((_stems + 7) / 8);
                    break;
                case 21: // rmoveto
                    ParseWidth(2);
                    MoveTo(Arg(0), Arg(1));
                    break;
                case 22: // hmoveto
                    ParseWidth(1);
                    MoveTo(Arg(0), 0);
                    break;
                case 4: // vmoveto
                    ParseWidth(1);
                    MoveTo(0, Arg(0));
                    break;
                case 5: // rlineto
                    for (var i = 0; i + 1 < _count; i += 2)
                        LineTo(_stack[i], _stack[i + 1]);
                    Clear();
                    break;
                case 6 or 7: // hlineto, vlineto: alternating horizontal and vertical lines
                    for (var i = 0; i < _count; i++)
                    {
                        if ((i % 2 == 0) == (b0 == 6))
                            LineTo(_stack[i], 0);
                        else
                            LineTo(0, _stack[i]);
                    }
                    Clear();
                    break;
                case 8: // rrcurveto
                    for (var i = 0; i + 5 < _count; i += 6)
                        CurveTo(i);
                    Clear();
                    break;
                case 24: // rcurveline
                    {
                        var i = 0;
                        for (; i + 7 < _count; i += 6)
                            CurveTo(i);
                        if (i + 1 < _count)
                            LineTo(_stack[i], _stack[i + 1]);
                        Clear();
                        break;
                    }
                case 25: // rlinecurve
                    {
                        var i = 0;
                        for (; i + 7 < _count; i += 2)
                            LineTo(_stack[i], _stack[i + 1]);
                        if (i + 5 < _count)
                            CurveTo(i);
                        Clear();
                        break;
                    }
                case 26: // vvcurveto
                    {
                        var i = _count % 2;
                        var dx1 = i == 1 ? _stack[0] : 0;
                        for (; i + 3 < _count; i += 4, dx1 = 0)
                            CurveTo(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                        Clear();
                        break;
                    }
                case 27: // hhcurveto
                    {
                        var i = _count % 2;
                        var dy1 = i == 1 ? _stack[0] : 0;
                        for (; i + 3 < _count; i += 4, dy1 = 0)
                            CurveTo(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                        Clear();
                        break;
                    }
                case 30 or 31: // vhcurveto, hvcurveto: curves alternating between vertical and horizontal tangents
                    {
                        var horizontal = b0 == 31;
                        for (var i = 0; i + 3 < _count; i += 4, horizontal = !horizontal)
                        {
                            var last = _count - i == 5 ? _stack[i + 4] : 0;
                            if (horizontal)
                                CurveTo(_stack[i], 0, _stack[i + 1], _stack[i + 2], last, _stack[i + 3]);
                            else
                                CurveTo(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], last);
                        }
                        Clear();
                        break;
                    }
                case 10: // callsubr
                    Execute(Subroutine(_private.Subrs, _localBias), depth + 1);
                    break;
                case 29: // callgsubr
                    Execute(Subroutine(font.GlobalSubrs, _globalBias), depth + 1);
                    break;
                case 11: // return
                    return;
                case 14: // endchar, optionally with the four seac arguments
                    if (!_widthParsed && _count is 1 or 5)
                        TakeWidth();
                    _widthParsed = true;
                    if (_count >= 4)
                        _seac = (_stack[_count - 4], _stack[_count - 3], (int)_stack[_count - 2], (int)_stack[_count - 1]);
                    ClosePath();
                    _ended = true;
                    return;
                case 12:
                    Escape(r.U8());
                    break;
                case 15 or 16:
                    throw new NotSupportedException("The font uses CFF2 charstring operators (variable fonts), which aren't supported.");
                default:
                    throw new InvalidDataException($"The CFF charstring uses an unknown operator ({b0}).");
            }
        }
    }

    private void Escape(int op)
    {
        switch (op)
        {
            case 0: // dotsection (deprecated hint)
                Clear();
                break;
            case 3: // and
                Binary((a, b) => a != 0 && b != 0 ? 1 : 0);
                break;
            case 4: // or
                Binary((a, b) => a != 0 || b != 0 ? 1 : 0);
                break;
            case 5: // not
                Push(Pop() == 0 ? 1 : 0);
                break;
            case 9: // abs
                Push(Math.Abs(Pop()));
                break;
            case 10: // add
                Binary((a, b) => a + b);
                break;
            case 11: // sub
                Binary((a, b) => a - b);
                break;
            case 12: // div
                Binary((a, b) => b == 0 ? 0 : a / b);
                break;
            case 14: // neg
                Push(-Pop());
                break;
            case 15: // eq
                Binary((a, b) => a == b ? 1 : 0);
                break;
            case 18: // drop
                Pop();
                break;
            case 20: // put
                {
                    var index = (int)Pop();
                    var value = Pop();
                    if (index is >= 0 and < 32)
                        _transient[index] = value;
                    break;
                }
            case 21: // get
                {
                    var index = (int)Pop();
                    Push(index is >= 0 and < 32 ? _transient[index] : 0);
                    break;
                }
            case 22: // ifelse
                {
                    var v2 = Pop();
                    var v1 = Pop();
                    var s2 = Pop();
                    var s1 = Pop();
                    Push(v1 <= v2 ? s1 : s2);
                    break;
                }
            case 23: // random: any value in (0, 1]; a fixed one keeps conversions reproducible
                Push(0.5);
                break;
            case 24: // mul
                Binary((a, b) => a * b);
                break;
            case 26: // sqrt
                Push(Math.Sqrt(Math.Max(0, Pop())));
                break;
            case 27: // dup
                {
                    var value = Pop();
                    Push(value);
                    Push(value);
                    break;
                }
            case 28: // exch
                {
                    var b = Pop();
                    var a = Pop();
                    Push(b);
                    Push(a);
                    break;
                }
            case 29: // index
                {
                    var i = (int)Pop();
                    if (i < 0)
                        i = 0;
                    Push(i < _count ? _stack[_count - 1 - i] : 0);
                    break;
                }
            case 30: // roll
                {
                    var j = (int)Pop();
                    var n = (int)Pop();
                    if (n > 0 && n <= _count)
                    {
                        var items = _stack.AsSpan(_count - n, n).ToArray();
                        j = ((j % n) + n) % n;
                        for (var i = 0; i < n; i++)
                            _stack[_count - n + (i + j) % n] = items[i];
                    }
                    break;
                }
            case 34: // hflex
                Need(7);
                CurveTo(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                CurveTo(_stack[4], 0, _stack[5], -_stack[2], _stack[6], 0);
                Clear();
                break;
            case 35: // flex
                Need(12);
                CurveTo(0);
                CurveTo(6);
                Clear();
                break;
            case 36: // hflex1
                Need(9);
                CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                CurveTo(_stack[5], 0, _stack[6], _stack[7], _stack[8], -(_stack[1] + _stack[3] + _stack[7]));
                Clear();
                break;
            case 37: // flex1: the last point moves along the dominant direction only
                {
                    Need(11);
                    double dx = 0, dy = 0;
                    for (var i = 0; i < 10; i += 2)
                    {
                        dx += _stack[i];
                        dy += _stack[i + 1];
                    }
                    var horizontal = Math.Abs(dx) > Math.Abs(dy);
                    CurveTo(0);
                    CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], horizontal ? _stack[10] : -dx, horizontal ? -dy : _stack[10]);
                    Clear();
                    break;
                }
            default:
                throw new InvalidDataException($"The CFF charstring uses an unknown operator (12 {op}).");
        }
    }

    private byte[] Subroutine(byte[][] subrs, int bias)
    {
        var index = (int)Pop() + bias;
        if (index < 0 || index >= subrs.Length)
            throw new InvalidDataException("A CFF charstring calls a missing subroutine.");
        return subrs[index];
    }

    /// <summary>Stem hints: an odd argument count on the first hint means the width comes first.</summary>
    private void Stems()
    {
        if (!_widthParsed && _count % 2 == 1)
            TakeWidth();
        _widthParsed = true;
        _stems += _count / 2;
        Clear();
    }

    /// <summary>
    /// The first stack-clearing operator may carry the advance width as an extra first argument. It is dropped:
    /// hmtx holds the same widths.
    /// </summary>
    private void ParseWidth(int expected)
    {
        if (!_widthParsed && _count > expected)
            TakeWidth();
        _widthParsed = true;
    }

    private void TakeWidth()
    {
        Array.Copy(_stack, 1, _stack, 0, _count - 1);
        _count--;
    }

    private void MoveTo(double dx, double dy)
    {
        ClosePath();
        _x += dx;
        _y += dy;
        _current = new PathContour(new Vec2(_x, _y));
        Clear();
    }

    private void LineTo(double dx, double dy)
    {
        var start = EnsureContour();
        _x += dx;
        _y += dy;
        start.Segments.Add(new PathSegment(false, default, default, new Vec2(_x, _y)));
    }

    private void CurveTo(int i) =>
        CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);

    private void CurveTo(double dxa, double dya, double dxb, double dyb, double dxc, double dyc)
    {
        var contour = EnsureContour();
        var c1 = new Vec2(_x + dxa, _y + dya);
        var c2 = new Vec2(c1.X + dxb, c1.Y + dyb);
        _x = c2.X + dxc;
        _y = c2.Y + dyc;
        contour.Segments.Add(new PathSegment(true, c1, c2, new Vec2(_x, _y)));
    }

    /// <summary>Drawing without a moveto starts a contour at the current point (sloppy but seen in the wild).</summary>
    private PathContour EnsureContour() => _current ??= new PathContour(new Vec2(_x, _y));

    private void ClosePath()
    {
        if (_current is { Segments.Count: > 0 })
            _contours.Add(_current);
        _current = null;
    }

    private double Arg(int i) => i < _count ? _stack[i] : 0;

    private void Need(int count)
    {
        if (_count < count)
            throw new InvalidDataException("A CFF charstring has too few arguments.");
    }

    private void Push(double value)
    {
        if (_count >= _stack.Length)
            throw new InvalidDataException("A CFF charstring overflows the argument stack.");
        _stack[_count++] = value;
    }

    private double Pop() => _count > 0 ? _stack[--_count] : throw new InvalidDataException("A CFF charstring has too few arguments.");

    private void Binary(Func<double, double, double> op)
    {
        var b = Pop();
        var a = Pop();
        Push(op(a, b));
    }

    private void Clear() => _count = 0;
}
