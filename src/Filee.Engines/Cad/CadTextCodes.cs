// Decodes the control codes inside CAD text: %%d / %%c / %%p specials and \U+ / \M+ escapes in TEXT, and the MTEXT
// formatting language (\P paragraphs, \H height, \C colour, \f font, \S stacked fractions, {} groups ...).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Cad;

/// <summary>Formatting state of an MTEXT run. Values not set by codes come from the entity.</summary>
internal readonly record struct MTextFormat(
    double Height,
    short? ColorIndex,
    int? TrueColor,
    string? FontFamily,
    bool Bold,
    bool Italic,
    double WidthFactor,
    double ObliqueDegrees);

/// <summary>A piece of MTEXT with one format.</summary>
internal readonly record struct MTextRun(string Text, MTextFormat Format);

/// <summary>Parsers for text control codes.</summary>
internal static class CadTextCodes
{
    /// <summary>Multi-byte code pages of <c>\M+n</c> escapes (AutoCAD's numbering).</summary>
    private static readonly int[] MultiByteCodePages = [0, 932, 950, 949, 1361, 936];

    static CadTextCodes() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Decodes a single-line TEXT / ATTRIB value: <c>%%d</c> → °, <c>%%c</c> → Ø, <c>%%p</c> → ±, <c>%%nnn</c> → character
    /// nnn, underline / overline toggles removed, <c>\U+XXXX</c> and <c>\M+nXXXX</c> escapes decoded.
    /// </summary>
    public static string DecodeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (TryEscape(value, ref i, sb) || TrySpecial(value, ref i, sb))
                continue;
            sb.Append(value[i] == '\t' ? ' ' : value[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses an MTEXT value into paragraphs of formatted runs. Paragraph, line and column breaks start a new
    /// paragraph; stacked text becomes an inline fraction ("1/2"); codes that only affect layout details
    /// (tracking, paragraph indents, underline) are dropped.
    /// </summary>
    public static List<List<MTextRun>> ParseMText(string? value, MTextFormat initial)
    {
        var paragraphs = new List<List<MTextRun>> { new() };
        if (string.IsNullOrEmpty(value))
            return paragraphs;

        var state = initial;
        var stack = new Stack<MTextFormat>();
        var text = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                Flush();
                stack.Push(state);
                continue;
            }
            if (ch == '}')
            {
                Flush();
                if (stack.Count > 0)
                    state = stack.Pop();
                continue;
            }
            if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                    i++;
                NewParagraph();
                continue;
            }
            if (ch != '\\' || i + 1 >= value.Length)
            {
                if (!TrySpecial(value, ref i, text))
                    text.Append(ch == '\t' ? "    " : ch);
                continue;
            }

            var code = value[i + 1];
            switch (code)
            {
                case '\\' or '{' or '}':
                    text.Append(code);
                    i++;
                    break;
                case 'P' or 'N' or 'X':
                    i++;
                    NewParagraph();
                    break;
                case '~':
                    text.Append(' ');
                    i++;
                    break;
                case 'U' or 'M':
                    if (!TryEscape(value, ref i, text))
                        i++;
                    break;
                case 'H' or 'W' or 'Q':
                    {
                        var argument = ReadArgument(value, ref i);
                        Flush();
                        state = ApplyNumeric(state, code, argument);
                        break;
                    }
                case 'C' or 'c':
                    {
                        var argument = ReadArgument(value, ref i);
                        Flush();
                        if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                            state = code == 'C'
                                ? state with { ColorIndex = (short)Math.Clamp(number, 0, 256), TrueColor = null }
                                : state with { TrueColor = number & 0xFFFFFF, ColorIndex = null };
                        break;
                    }
                case 'f' or 'F':
                    {
                        var argument = ReadArgument(value, ref i);
                        Flush();
                        state = ApplyFont(state, argument);
                        break;
                    }
                case 'S':
                    {
                        var argument = ReadArgument(value, ref i);
                        text.Append(Stacked(argument));
                        break;
                    }
                case 'A' or 'T' or 'p' or 'q':
                    ReadArgument(value, ref i); // alignment, tracking, paragraph properties: layout details
                    break;
                default:
                    // \L \l \O \o \K \k (underline, overline, strike-through) and unknown codes: drop the code.
                    i++;
                    break;
            }
        }
        Flush();
        return paragraphs;

        void Flush()
        {
            if (text.Length == 0)
                return;
            paragraphs[^1].Add(new MTextRun(text.ToString(), state));
            text.Clear();
        }

        void NewParagraph()
        {
            Flush();
            paragraphs.Add([]);
        }
    }

    /// <summary>Reads the argument of a code like <c>\H2.5;</c>, leaving <paramref name="i"/> on the ';'.</summary>
    private static string ReadArgument(string value, ref int i)
    {
        var start = i + 2;
        var end = start;
        while (end < value.Length && value[end] != ';')
        {
            // Inside \S, "\;" is an escaped semicolon.
            if (value[end] == '\\' && end + 1 < value.Length)
                end++;
            end++;
        }
        i = Math.Min(end, value.Length - 1);
        return start < value.Length ? value[start..Math.Min(end, value.Length)] : "";
    }

    private static MTextFormat ApplyNumeric(MTextFormat state, char code, string argument)
    {
        var relative = argument.EndsWith('x') || argument.EndsWith('X');
        if (!double.TryParse(relative ? argument[..^1] : argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return state;
        return code switch
        {
            'H' when number > 0 => state with { Height = relative ? state.Height * number : number },
            'W' when number > 0 => state with { WidthFactor = relative ? state.WidthFactor * number : number },
            'Q' => state with { ObliqueDegrees = Math.Clamp(number, -85, 85) },
            _ => state,
        };
    }

    /// <summary><c>\fArial|b1|i0|c0|p34;</c> (TrueType) or <c>\Ftxt.shx;</c> (SHX: rendered with the default font).</summary>
    private static MTextFormat ApplyFont(MTextFormat state, string argument)
    {
        var parts = argument.Split('|');
        var family = parts[0].Trim();
        var bold = false;
        var italic = false;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length >= 2 && part[0] is 'b')
                bold = part[1] == '1';
            else if (part.Length >= 2 && part[0] is 'i')
                italic = part[1] == '1';
        }
        if (family.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            family = "";
        return state with { FontFamily = family.Length == 0 ? null : family, Bold = bold, Italic = italic };
    }

    /// <summary>Stacked text <c>1/2</c>, <c>1#2</c> (diagonal) or <c>+0.1^-0.2</c> (tolerance) shown inline as "1/2".</summary>
    private static string Stacked(string argument)
    {
        var unescaped = argument.Replace("\\;", ";", StringComparison.Ordinal);
        var split = unescaped.IndexOfAny(['/', '#', '^']);
        if (split < 0)
            return DecodeText(unescaped);
        var top = DecodeText(unescaped[..split]).Trim();
        var bottom = DecodeText(unescaped[(split + 1)..]).Trim();
        return top.Length == 0 ? bottom : bottom.Length == 0 ? top : $"{top}/{bottom}";
    }

    /// <summary>Decodes <c>\U+XXXX</c> (Unicode) and <c>\M+nXXXX</c> (a double-byte character in an Asian code page).</summary>
    private static bool TryEscape(string value, ref int i, StringBuilder sb)
    {
        if (value[i] != '\\' || i + 2 >= value.Length || value[i + 2] != '+')
            return false;
        if (value[i + 1] is 'U' or 'u' && i + 7 <= value.Length
            && int.TryParse(value.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            sb.Append((char)code);
            i += 6;
            return true;
        }
        if (value[i + 1] is 'M' or 'm' && i + 8 <= value.Length
            && value[i + 3] is >= '1' and <= '5'
            && int.TryParse(value.AsSpan(i + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bytes))
        {
            try
            {
                var encoding = Encoding.GetEncoding(MultiByteCodePages[value[i + 3] - '0']);
                sb.Append(encoding.GetString([(byte)(bytes >> 8), (byte)bytes]));
                i += 7;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>The <c>%%</c> specials of AutoCAD text.</summary>
    private static bool TrySpecial(string value, ref int i, StringBuilder sb)
    {
        if (value[i] != '%' || i + 2 >= value.Length || value[i + 1] != '%')
            return false;
        var code = char.ToLowerInvariant(value[i + 2]);
        switch (code)
        {
            case 'd':
                sb.Append('°');
                break;
            case 'c':
                sb.Append('Ø');
                break;
            case 'p':
                sb.Append('±');
                break;
            case '%':
                sb.Append('%');
                break;
            case 'u' or 'o' or 'k':
                break; // underline / overline / strike-through toggles
            case >= '0' and <= '9':
                {
                    var end = i + 2;
                    while (end < value.Length && end < i + 5 && char.IsAsciiDigit(value[end]))
                        end++;
                    sb.Append((char)int.Parse(value.AsSpan(i + 2, end - i - 2), CultureInfo.InvariantCulture));
                    i = end - 1;
                    return true;
                }
            default:
                return false;
        }
        i += 2;
        return true;
    }
}
