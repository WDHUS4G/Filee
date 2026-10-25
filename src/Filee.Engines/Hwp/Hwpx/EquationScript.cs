// Equations for 한글 (hp:equation): a small math tree that readers build (DOCX math, see Docx/OmmlReader), written as
// 한글's equation script (Equation Version 60, e.g. "{a} over {b}", "sqrt {x}", "SUM _{i=1} ^{n}"), as linear text
// ("(a)/(b)", "√(x)") for formats without equations, and measured for the equation's box.
//
// The box: 한글 stores the size an equation was laid out at (hp:sz), and rhwp stretches the drawing to that width when
// it renders the document (to PDF), so a box that is too wide or too narrow distorts the equation. Measure follows
// rhwp's own equation layout (rhwp 0.8.7, src/renderer/equation/layout.rs, MIT): its estimated character widths,
// script scale 0.7, fraction, root, bracket and matrix spacing. rhwp then draws the equation at its natural width.
// 한글 lays the script out again when the equation is edited.
//
// The script is written so that 한글 and rhwp read the same tree from it: every token is separated by a space,
// variables are written one letter at a time (so "in", "ln" or "max" in a subscript never become commands), groups
// are braced, and only commands that both know are used.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal abstract record MathNode;

/// <summary>Items side by side.</summary>
internal sealed record MathRow(IReadOnlyList<MathNode> Items) : MathNode
{
    public static readonly MathRow Empty = new([]);
}

/// <summary>One character of a variable or text: italic Latin letters, upright CJK.</summary>
internal sealed record MathText(string Text) : MathNode;

internal sealed record MathNumber(string Digits) : MathNode;

/// <summary>An ASCII operator or bracket written as it is: + - = &lt; &gt; ( ) [ ] , . / | ! * : '</summary>
internal sealed record MathSymbol(string Text) : MathNode;

/// <summary>A symbol written as a command: Greek letters, TIMES, LEQ, INF, rarrow, LBRACE ...</summary>
internal sealed record MathCommand(string Command, string Glyph) : MathNode;

/// <summary>A text written in quotes (characters with a meaning in the script: # &amp; _ ^).</summary>
internal sealed record MathQuoted(string Text) : MathNode;

/// <summary>A function name 한글 writes upright by itself: sin, log, max ...</summary>
internal sealed record MathFunction(string Name) : MathNode;

/// <summary>A fraction, or one part above the other without a bar (ATOP).</summary>
internal sealed record MathFraction(MathNode Numerator, MathNode Denominator, bool Bar = true) : MathNode;

/// <summary>Square root, or a root with an index.</summary>
internal sealed record MathRoot(MathNode? Index, MathNode Body) : MathNode;

/// <summary>Subscript and / or superscript.</summary>
internal sealed record MathScripts(MathNode Base, MathNode? Sub, MathNode? Sup) : MathNode;

/// <summary>A large operator with limits: SUM, PROD ... above and below, INT, OINT ... at the side.</summary>
internal sealed record MathBigOperator(string Command, string Glyph, MathNode? Lower, MathNode? Upper) : MathNode
{
    public bool Integral => Glyph is "∫" or "∬" or "∭" or "∮" or "∯" or "∰";
}

/// <summary>lim with what goes under it.</summary>
internal sealed record MathLimit(MathNode? Lower) : MathNode;

/// <param name="Command">matrix, pmatrix (round brackets), bmatrix (square) or dmatrix (bars).</param>
internal sealed record MathMatrix(string Command, IReadOnlyList<IReadOnlyList<MathNode>> Rows) : MathNode;

/// <summary>Cases: rows of cells after one large left brace.</summary>
internal sealed record MathCases(IReadOnlyList<IReadOnlyList<MathNode>> Rows) : MathNode;

/// <summary>Lines stacked and centred (PILE).</summary>
internal sealed record MathPile(IReadOnlyList<MathNode> Rows) : MathNode;

/// <summary>Brackets that grow with their content (LEFT ... RIGHT); an empty side has no bracket.</summary>
internal sealed record MathBrackets(string Left, string Right, MathNode Body) : MathNode;

/// <summary>A mark over (or a line under) the content: hat, bar, vec, tilde, OVERLINE, UNDERLINE ...</summary>
internal sealed record MathAccent(string Command, MathNode Body) : MathNode;

/// <summary>Upright (rm) or bold content.</summary>
internal sealed record MathStyled(string Command, MathNode Body) : MathNode;

/// <summary>A normal space (~).</summary>
internal sealed record MathSpace : MathNode;

/// <summary>An alignment mark (&amp;) of a Word equation array: the cell boundary of cases, nothing elsewhere.</summary>
internal sealed record MathAlign : MathNode;

/// <summary>Width, height and baseline (from the top) of laid out math.</summary>
internal readonly record struct MathBox(double Width, double Height, double Baseline);

internal static class EquationScript
{
    // rhwp's layout constants (fractions of the font size).
    private const double ScriptScale = 0.7;
    private const double FractionPad = 0.2;
    private const double FractionLine = 0.04;
    private const double RootPad = 0.1;
    private const double BracketPad = 0.08;
    private const double BigOperatorScale = 1.5;
    private const double IntegralScale = 2.5;
    private const double BigOperatorTrail = 0.45;
    private const double MatrixColumnGap = 0.8;
    private const double MatrixRowGap = 0.3;
    private const double Axis = 0.25;

    // ───────────────────────── Script ─────────────────────────

    /// <summary>The 한글 equation script of <paramref name="node"/>.</summary>
    public static string Script(MathNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return Collapse(sb.ToString());
    }

    /// <summary>One space between tokens and none inside the braces of a group ("x _{i}"), quoted text left alone.</summary>
    private static string Collapse(string script)
    {
        var sb = new StringBuilder(script.Length);
        var quoted = false;
        foreach (var ch in script)
        {
            if (ch == '"')
                quoted = !quoted;
            if (!quoted && ch == ' ' && (sb.Length == 0 || sb[^1] is ' ' or '{'))
                continue;
            if (!quoted && ch == '}' && sb.Length > 0 && sb[^1] == ' ')
                sb.Length--;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static void Write(MathNode node, StringBuilder sb)
    {
        switch (node)
        {
            case MathRow row:
                for (var i = 0; i < row.Items.Count; i++)
                {
                    sb.Append(' ');
                    if (row.Items[i] is MathRow inner)
                        Group(inner, sb);
                    else
                        Write(row.Items[i], sb);
                }
                break;
            case MathText text:
                sb.Append(' ').Append(text.Text).Append(' ');
                break;
            case MathNumber number:
                sb.Append(' ').Append(number.Digits).Append(' ');
                break;
            case MathSymbol symbol:
                sb.Append(' ').Append(symbol.Text).Append(' ');
                break;
            case MathCommand command:
                sb.Append(' ').Append(command.Command).Append(' ');
                break;
            case MathQuoted quoted:
                sb.Append(" \"").Append(quoted.Text.Replace('"', '\'')).Append("\" ");
                break;
            case MathFunction function:
                sb.Append(' ').Append(function.Name).Append(' ');
                break;
            case MathFraction fraction:
                sb.Append(" {");
                Group(fraction.Numerator, sb);
                sb.Append(fraction.Bar ? " over " : " atop ");
                Group(fraction.Denominator, sb);
                sb.Append("} ");
                break;
            case MathRoot root:
                if (root.Index is null)
                {
                    sb.Append(" sqrt ");
                }
                else
                {
                    sb.Append(" root ");
                    Group(root.Index, sb);
                    sb.Append(" of ");
                }
                Group(root.Body, sb);
                break;
            case MathScripts scripts:
                if (IsAtom(scripts.Base))
                    Write(scripts.Base, sb);
                else
                    Group(scripts.Base, sb);
                if (scripts.Sub is not null)
                {
                    sb.Append(" _");
                    Group(scripts.Sub, sb);
                }
                if (scripts.Sup is not null)
                {
                    sb.Append(" ^");
                    Group(scripts.Sup, sb);
                }
                sb.Append(' ');
                break;
            case MathBigOperator big:
                sb.Append(' ').Append(big.Command);
                if (big.Lower is not null)
                {
                    sb.Append(" _");
                    Group(big.Lower, sb);
                }
                if (big.Upper is not null)
                {
                    sb.Append(" ^");
                    Group(big.Upper, sb);
                }
                sb.Append(' ');
                break;
            case MathLimit limit:
                sb.Append(" lim ");
                if (limit.Lower is not null)
                {
                    sb.Append('_');
                    Group(limit.Lower, sb);
                }
                sb.Append(' ');
                break;
            case MathMatrix matrix:
                sb.Append(' ').Append(matrix.Command).Append(" {");
                Cells(matrix.Rows, sb);
                sb.Append("} ");
                break;
            case MathCases cases:
                sb.Append(" cases {");
                Cells(cases.Rows, sb);
                sb.Append("} ");
                break;
            case MathPile pile:
                sb.Append(" pile {");
                for (var i = 0; i < pile.Rows.Count; i++)
                {
                    if (i > 0)
                        sb.Append(" # ");
                    Write(pile.Rows[i], sb);
                }
                sb.Append("} ");
                break;
            case MathBrackets brackets:
                sb.Append(" LEFT ").Append(BracketToken(brackets.Left)).Append(' ');
                Write(brackets.Body, sb);
                sb.Append(" RIGHT ").Append(BracketToken(brackets.Right)).Append(' ');
                break;
            case MathAccent accent:
                sb.Append(' ').Append(accent.Command).Append(' ');
                Group(accent.Body, sb);
                break;
            case MathStyled styled:
                // Braced so the style ends with the content (rm and bold switch the rest of a group in 한글).
                sb.Append(" {").Append(styled.Command).Append(' ');
                Group(styled.Body, sb);
                sb.Append("} ");
                break;
            case MathSpace:
                sb.Append(" ~ ");
                break;
            case MathAlign:
                break;
        }
    }

    private static void Group(MathNode node, StringBuilder sb)
    {
        sb.Append('{');
        if (node is MathRow row)
            Write(row, sb);
        else
            Write(node, sb);
        sb.Append('}');
    }

    private static void Cells(IReadOnlyList<IReadOnlyList<MathNode>> rows, StringBuilder sb)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0)
                sb.Append(" # ");
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (c > 0)
                    sb.Append(" & ");
                Write(rows[r][c], sb);
            }
        }
    }

    /// <summary>A base that takes scripts without braces: one letter, number, symbol command or function name.</summary>
    private static bool IsAtom(MathNode node) => node is MathText or MathNumber or MathCommand or MathFunction;

    private static string BracketToken(string bracket) => bracket switch
    {
        "" => ".",
        "{" => "LBRACE",
        "}" => "RBRACE",
        "⟨" or "〈" => "LANGLE",
        "⟩" or "〉" => "RANGLE",
        "⌈" => "LCEIL",
        "⌉" => "RCEIL",
        "⌊" => "LFLOOR",
        "⌋" => "RFLOOR",
        "‖" => "VERT",
        _ => bracket,
    };

    /// <summary>Brackets <see cref="MathBrackets"/> can hold; others are written as text inside.</summary>
    public static bool IsBracket(string bracket) =>
        bracket is "" or "(" or ")" or "[" or "]" or "{" or "}" or "|" or "⟨" or "⟩" or "〈" or "〉" or "⌈" or "⌉" or "⌊" or "⌋" or "‖";

    // ───────────────────────── Linear text ─────────────────────────

    /// <summary>The equation as one line of text, for formats without equations: "(a+b)/(2c)", "x_i^2", "√(x)".</summary>
    public static string Linear(MathNode node)
    {
        var sb = new StringBuilder();
        Linear(node, sb);
        return sb.ToString();
    }

    private static void Linear(MathNode node, StringBuilder sb)
    {
        switch (node)
        {
            case MathRow row:
                foreach (var item in row.Items)
                    Linear(item, sb);
                break;
            case MathText text:
                sb.Append(text.Text);
                break;
            case MathNumber number:
                sb.Append(number.Digits);
                break;
            case MathSymbol symbol:
                sb.Append(symbol.Text);
                break;
            case MathCommand command:
                sb.Append(command.Glyph);
                break;
            case MathQuoted quoted:
                sb.Append(quoted.Text);
                break;
            case MathFunction function:
                sb.Append(function.Name).Append(' ');
                break;
            case MathFraction fraction:
                LinearOperand(fraction.Numerator, sb);
                sb.Append(fraction.Bar ? "/" : " ");
                LinearOperand(fraction.Denominator, sb);
                break;
            case MathRoot root:
                if (root.Index is not null)
                {
                    sb.Append('[');
                    Linear(root.Index, sb);
                    sb.Append(']');
                }
                sb.Append('√');
                LinearOperand(root.Body, sb);
                break;
            case MathScripts scripts:
                Linear(scripts.Base, sb);
                LinearScripts(scripts.Sub, scripts.Sup, sb);
                break;
            case MathBigOperator big:
                sb.Append(big.Glyph);
                LinearScripts(big.Lower, big.Upper, sb);
                sb.Append(' ');
                break;
            case MathLimit limit:
                sb.Append("lim");
                LinearScripts(limit.Lower, null, sb);
                sb.Append(' ');
                break;
            case MathMatrix matrix:
                {
                    var (open, close) = matrix.Command switch
                    {
                        "pmatrix" => ("(", ")"),
                        "bmatrix" => ("[", "]"),
                        "dmatrix" => ("|", "|"),
                        _ => ("[", "]"),
                    };
                    sb.Append(open);
                    LinearCells(matrix.Rows, ", ", sb);
                    sb.Append(close);
                    break;
                }
            case MathCases cases:
                sb.Append('{');
                LinearCells(cases.Rows, " ", sb);
                break;
            case MathPile pile:
                for (var i = 0; i < pile.Rows.Count; i++)
                {
                    if (i > 0)
                        sb.Append("; ");
                    Linear(pile.Rows[i], sb);
                }
                break;
            case MathBrackets brackets:
                sb.Append(brackets.Left);
                Linear(brackets.Body, sb);
                sb.Append(brackets.Right);
                break;
            case MathAccent accent:
                {
                    var mark = accent.Command switch
                    {
                        "hat" => "\u0302",
                        "check" => "\u030C",
                        "tilde" => "\u0303",
                        "acute" => "\u0301",
                        "grave" => "\u0300",
                        "dot" => "\u0307",
                        "ddot" => "\u0308",
                        "bar" or "OVERLINE" => "\u0305",
                        "vec" => "\u20D7",
                        "dyad" => "\u20E1",
                        _ => "\u0332", // UNDERLINE, under
                    };
                    var body = Linear(accent.Body);
                    if (body.Length == 1)
                    {
                        sb.Append(body).Append(mark);
                    }
                    else
                    {
                        sb.Append(accent.Command.ToLowerInvariant()).Append('(').Append(body).Append(')');
                    }
                    break;
                }
            case MathStyled styled:
                Linear(styled.Body, sb);
                break;
            case MathSpace:
                sb.Append(' ');
                break;
        }
    }

    private static void LinearScripts(MathNode? sub, MathNode? sup, StringBuilder sb)
    {
        if (sub is not null)
        {
            sb.Append('_');
            LinearOperand(sub, sb);
        }
        if (sup is not null)
        {
            sb.Append('^');
            LinearOperand(sup, sb);
        }
    }

    /// <summary>An operand of /, _ or ^: in brackets unless it is one letter, symbol, number or bracketed group.</summary>
    private static void LinearOperand(MathNode node, StringBuilder sb)
    {
        var text = Linear(node);
        var single = node is MathRow { Items.Count: 1 } row ? row.Items[0] : node;
        var simple = single is MathNumber or MathCommand or MathBrackets or MathText { Text.Length: 1 };
        if (simple || text.Length <= 1)
            sb.Append(text);
        else
            sb.Append('(').Append(text).Append(')');
    }

    private static void LinearCells(IReadOnlyList<IReadOnlyList<MathNode>> rows, string cellSeparator, StringBuilder sb)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0)
                sb.Append("; ");
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (c > 0)
                    sb.Append(cellSeparator);
                Linear(rows[r][c], sb);
            }
        }
    }

    // ───────────────────────── Size ─────────────────────────

    /// <summary>The box of <paramref name="node"/> at font size <paramref name="size"/> (any unit; the box is in the same unit).</summary>
    public static MathBox Measure(MathNode node, double size) => node switch
    {
        MathRow row => MeasureRow(row.Items, size),
        MathText text => Character(TextWidth(text.Text, size, italic: !IsCjk(text.Text)), size),
        MathNumber number => Character(TextWidth(number.Digits, size, false), size),
        MathSymbol symbol => Character(TextWidth(symbol.Text, size, false)
                                       + 2 * size * (symbol.Text is "+" or "-" or "=" or "<" or ">" ? 0.15 : 0.05), size),
        MathCommand command => Character(TextWidth(command.Glyph, size, false), size),
        MathQuoted quoted => Character(TextWidth(quoted.Text, size, false), size),
        MathFunction function => Character(TextWidth(function.Name, size, false) + size * 0.02, size),
        MathFraction fraction => MeasureFraction(fraction, size),
        MathRoot root => MeasureRoot(root, size),
        MathScripts scripts => MeasureScripts(Measure(scripts.Base, size), scripts.Sub, scripts.Sup, size),
        MathBigOperator big => big.Integral ? MeasureIntegral(big, size) : MeasureBigOperator(big, size),
        MathLimit limit => MeasureLimit(limit, size),
        MathMatrix matrix => MeasureMatrix(matrix, size),
        MathCases cases => MeasureCases(cases, size),
        MathPile pile => MeasurePile(pile, size),
        MathBrackets brackets => MeasureBrackets(brackets, size),
        MathAccent accent => Raise(Measure(accent.Body, size), size * 0.25),
        MathStyled styled => Measure(styled.Body, size),
        MathSpace => Character(size * 0.33, size),
        MathTab => Character(size, size),
        _ => default,
    };

    private static MathBox Character(double width, double size) => new(width, size, size * 0.8);

    /// <summary>Room above the content for an accent mark.</summary>
    private static MathBox Raise(MathBox body, double room) => new(body.Width, body.Height + room, body.Baseline + room);

    /// <summary>A group as rhwp parses it: an empty group is nothing, a group of one item is that item.</summary>
    private static MathBox MeasureRow(IReadOnlyList<MathNode> items, double size)
    {
        var parts = items.Where(i => i is not MathAlign).ToList();
        if (parts.Count == 0)
            return default;
        if (parts.Count == 1)
            return Measure(parts[0], size);
        var boxes = parts.Select(i => Measure(i, size)).Where(b => b.Width > 0).ToList();
        if (boxes.Count == 0)
            return new MathBox(0, size, size * 0.8);
        var ascent = boxes.Max(b => b.Baseline);
        var descent = boxes.Max(b => b.Height - b.Baseline);
        return new MathBox(boxes.Sum(b => b.Width), ascent + descent, ascent);
    }

    private static MathBox MeasureFraction(MathFraction fraction, double size)
    {
        var n = Measure(fraction.Numerator, size);
        var d = Measure(fraction.Denominator, size);
        var pad = size * FractionPad;
        var width = Math.Max(n.Width, d.Width) + pad * 2;
        var top = n.Height + pad;
        var bottom = d.Height + pad;
        if (!fraction.Bar)
            return new MathBox(width, top + bottom, top + size * Axis);
        var line = size * FractionLine;
        return new MathBox(width, top + line + bottom, top + line / 2 + size * Axis);
    }

    private static MathBox MeasureRoot(MathRoot root, double size)
    {
        var body = Measure(root.Body, size);
        var pad = size * RootPad;
        var sign = size * 0.6;
        var index = root.Index is null ? 0 : Measure(root.Index, size * ScriptScale).Width;
        var bodyWidth = body.Width + pad;
        return new MathBox(Math.Max(index, sign * 0.5) + sign * 0.5 + bodyWidth, body.Height + pad * 2, pad + body.Baseline);
    }

    private static MathBox MeasureScripts(MathBox b, MathNode? subNode, MathNode? supNode, double size)
    {
        if (subNode is null && supNode is null)
            return b;
        if (subNode is null)
        {
            var s = Measure(supNode!, size * ScriptScale);
            var shift = b.Baseline - s.Height * 0.7;
            var baseY = shift >= 0 ? Math.Max(s.Height - b.Height, 0) : -shift;
            return new MathBox(b.Width + s.Width, Math.Max(baseY + b.Height, s.Height), baseY + b.Baseline);
        }
        if (supNode is null)
        {
            var s = Measure(subNode, size * ScriptScale);
            var shift = b.Baseline * 0.4;
            return new MathBox(b.Width + s.Width, Math.Max(b.Height, shift + s.Height), b.Baseline);
        }

        var sub = Measure(subNode, size * ScriptScale);
        var sup = Measure(supNode, size * ScriptScale);
        var supShift = b.Baseline - sup.Height * 0.7;
        var subShift = b.Baseline * 0.4;
        var ascent = supShift < 0 ? sup.Height - Math.Abs(supShift) : Math.Max(sup.Height, 0);
        var top = Math.Abs(Math.Min(supShift, 0));
        var height = Math.Max(Math.Max(top + b.Height, top + subShift + sub.Height), ascent + b.Height);
        var y = Math.Max(top, supShift > 0 ? 0 : Math.Max(sup.Height - Math.Abs(supShift) - b.Baseline, 0));
        height = Math.Max(Math.Max(height, y + b.Height), y + subShift + sub.Height);
        return new MathBox(b.Width + Math.Max(sup.Width, sub.Width), height, y + b.Baseline);
    }

    private static MathBox MeasureBigOperator(MathBigOperator big, double size)
    {
        var opSize = size * BigOperatorScale;
        var opWidth = TextWidth(big.Glyph, opSize, false);
        var lower = big.Lower is null ? (MathBox?)null : Measure(big.Lower, size * ScriptScale);
        var upper = big.Upper is null ? (MathBox?)null : Measure(big.Upper, size * ScriptScale);
        var upperHeight = upper is { } u ? u.Height + size * 0.05 : 0;
        var lowerHeight = lower is { } l ? l.Height + size * 0.05 : 0;
        var width = Math.Max(opWidth, Math.Max(lower?.Width ?? 0, upper?.Width ?? 0));
        return new MathBox(width + size * BigOperatorTrail, upperHeight + opSize + lowerHeight, upperHeight + opSize * 0.6);
    }

    /// <summary>
    /// An integral is a large symbol (INT) whose limits are ordinary scripts: rhwp places both limits next to the drawn
    /// sign, one limit as a sub- or superscript of the sign.
    /// </summary>
    private static MathBox MeasureIntegral(MathBigOperator integral, double size)
    {
        var height = size * IntegralScale;
        var glyphWidth = size * 0.52;
        var sign = new MathBox(glyphWidth + size * BigOperatorTrail, height, height * 0.7);
        if (integral.Lower is null || integral.Upper is null)
            return MeasureScripts(sign, integral.Lower, integral.Upper, size);

        var sub = Measure(integral.Lower, size * ScriptScale);
        var sup = Measure(integral.Upper, size * ScriptScale);
        var gap = size * 0.22;
        var topY = height * 0.04;
        var bottomY = height * 0.96;
        var head = Math.Max(-(topY - sup.Height * 0.30), 0);
        var supX = size * 0.50 + gap;
        var subX = size * 0.04 + gap;
        var supY = head + topY - sup.Height * 0.30;
        var subY = head + bottomY - sub.Height * 0.72;
        var right = Math.Max(Math.Max(supX + sup.Width, subX + sub.Width), glyphWidth);
        var total = Math.Max(Math.Max(subY + sub.Height, head + height), supY + sup.Height);
        return new MathBox(right + size * BigOperatorTrail, total, head + height * 0.7);
    }

    private static MathBox MeasureLimit(MathLimit limit, double size)
    {
        var nameWidth = TextWidth("lim", size, false);
        if (limit.Lower is null)
            return new MathBox(nameWidth, size, size * 0.8);
        var lower = Measure(limit.Lower, size * ScriptScale);
        return new MathBox(Math.Max(nameWidth, lower.Width), size + lower.Height + size * 0.05, size * 0.8);
    }

    private static MathBox MeasureMatrix(MathMatrix matrix, double size)
    {
        if (matrix.Rows.Count == 0)
            return new MathBox(0, size, size * 0.8);
        var cells = matrix.Rows.Select(r => r.Select(c => Measure(c, size)).ToList()).ToList();
        var columns = cells.Max(r => r.Count);
        var widths = new double[columns];
        foreach (var row in cells)
        {
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Width);
        }
        var height = cells.Sum(r => r.Select(c => c.Height).Append(size).Max()) + size * MatrixRowGap * (cells.Count - 1);
        var width = widths.Sum() + size * MatrixColumnGap * Math.Max(0, columns - 1);
        var brackets = matrix.Command == "matrix" ? 0 : size * 0.3;
        return new MathBox(width + brackets * 2 + size * 0.2 * 2, height, height / 2);
    }

    private static MathBox MeasureCases(MathCases cases, double size)
    {
        var rows = cases.Rows.Select(r => MeasureRow(CaseRow(r), size)).ToList();
        var height = rows.Sum(r => r.Height) + size * MatrixRowGap * Math.Max(0, rows.Count - 1);
        var width = rows.Select(r => r.Width).DefaultIfEmpty(0).Max() + size * 0.6;
        return new MathBox(width + size * 0.3, height, height / 2);
    }

    /// <summary>A row of cases as rhwp reads it: the cells with a tab (&amp;) between them.</summary>
    private static List<MathNode> CaseRow(IReadOnlyList<MathNode> cells)
    {
        var items = new List<MathNode>();
        for (var c = 0; c < cells.Count; c++)
        {
            if (c > 0)
                items.Add(new MathTab());
            items.AddRange(cells[c] is MathRow row ? row.Items : [cells[c]]);
        }
        return items;
    }

    /// <summary>The tab rhwp makes of &amp; in cases: one font size wide.</summary>
    private sealed record MathTab : MathNode;

    private static MathBox MeasurePile(MathPile pile, double size)
    {
        var rows = pile.Rows.Select(r => Measure(r, size)).ToList();
        var height = rows.Sum(r => r.Height) + size * MatrixRowGap * Math.Max(0, rows.Count - 1);
        return new MathBox(rows.Select(r => r.Width).DefaultIfEmpty(0).Max(), height, height / 2);
    }

    private static MathBox MeasureBrackets(MathBrackets brackets, double size)
    {
        var body = Measure(brackets.Body, size);
        var stretch = body.Height > size * 1.2 && brackets.Left == "(" && brackets.Right == ")";
        var pad = stretch ? size * 0.03 : size * BracketPad;
        var bracket = stretch ? size * 0.27 : size * 0.333;
        var left = brackets.Left.Length == 0 ? 0 : bracket;
        var right = brackets.Right.Length == 0 ? 0 : bracket;
        return new MathBox(left + pad + body.Width + pad + right, body.Height, body.Baseline);
    }

    private static bool IsCjk(string text) =>
        text.Any(c => c is >= '\u3000' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uAC00' and <= '\uD7AF');

    /// <summary>rhwp's estimate of a text's width.</summary>
    internal static double TextWidth(string text, double size, bool italic)
    {
        var width = 0.0;
        foreach (var rune in text.EnumerateRunes())
            width += size * CharacterWidth(rune.Value);
        return italic ? width * 1.05 : width;
    }

    private static double CharacterWidth(int c)
    {
        if (c < 0x80)
            return c is >= 'A' and <= 'Z' ? 0.65 : c is >= 'a' and <= 'z' or >= '0' and <= '9' ? 0.55 : 0.5;
        return c switch
        {
            '′' or '″' or '‴' => 0.3,
            >= 'α' and <= 'ω' or 'ϑ' or 'ϖ' => 0.55,
            >= 'Α' and <= 'Ω' or 'ϒ' => 0.65,
            '±' or '∓' or '×' or '÷' or '·' or '∘' or '†' or '‡' or '•' => 0.6,
            '≠' or '≤' or '≥' or '≈' or '≡' or '≅' or '∼' or '≃' or '≍' or '≐' or '∝' or '≺' or '≻' => 0.7,
            '∈' or '∉' or '∋' or '⊂' or '⊃' or '⊆' or '⊇' or '∀' or '∃' or '¬' or '∧' or '∨' => 0.65,
            '⊏' or '⊐' or '⊑' or '⊒' or '⊻' or '⊢' or '⊣' or '⊨' => 0.65,
            '∫' or '∬' or '∭' or '∮' or '∯' or '∰' => 0.5,
            '∑' or '∏' or '∐' => 0.8,
            '∪' or '∩' or '⊔' or '⊓' or '⊎' or '⋀' or '⋁' => 0.7,
            '⊕' or '⊗' or '⊙' or '⊖' or '⊘' => 0.7,
            >= '←' and <= '↕' or >= '⇐' and <= '⇕' or '↖' or '↗' or '↘' or '↙' or '↦' or '↩' or '↪' => 0.8,
            '⋯' or '…' or '⋮' or '⋱' => 0.8,
            '∂' or '∅' or '∇' or '∞' or '∠' or '∡' or '∢' or '⊾' => 0.6,
            '⊥' or '⊤' or '°' or '‰' or '‱' or '♯' => 0.5,
            'ℵ' or 'ℏ' or 'ı' or 'ȷ' or 'ℓ' or '℘' or 'ℑ' or 'ℜ' or 'ℒ' or 'Å' or '℧' => 0.6,
            '℃' or '℉' => 0.9,
            '△' or '▽' or '○' or '◇' or '⋄' => 0.7,
            >= 0x3000 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xAC00 and <= 0xD7AF => 1.0,
            _ => 0.6,
        };
    }
}
