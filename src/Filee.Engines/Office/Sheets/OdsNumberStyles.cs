// Excel number format codes ↔ OpenDocument data styles (number:number-style, number:date-style, ...), so an ODS
// written from XLSX / XLS keeps what the cells show and an XLSX written from ODS keeps real number formats.
// Covered: digits with decimals, grouping and thousands scaling, percent, scientific, currency symbols, literal
// text, colours, positive / negative (/ zero) sections, dates, times and durations. Anything else (conditions,
// fractions written as Excel codes, text between digits, eras) falls back to General.

using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Office.Sheets;

internal static class OdsNumberStyles
{
    public static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    public static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    /// <summary>LibreOffice's name for number:min-decimal-places in ODF 1.2 files.</summary>
    private static readonly XNamespace Loext = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    /// <summary>Excel's named colours ([Red]) and their RGB values.</summary>
    private static readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = "#000000",
        ["Blue"] = "#0000FF",
        ["Cyan"] = "#00FFFF",
        ["Green"] = "#00FF00",
        ["Magenta"] = "#FF00FF",
        ["Red"] = "#FF0000",
        ["White"] = "#FFFFFF",
        ["Yellow"] = "#FFFF00",
    };

    // ───────────────────────── OpenDocument → Excel ─────────────────────────

    /// <summary>
    /// The Excel format code of a data style, or null when it has none Excel could use (boolean styles,
    /// unsupported parts). <paramref name="find"/> looks up the styles that style:map applies.
    /// </summary>
    public static string? ToExcel(XElement style, Func<string, XElement?> find)
    {
        var main = SectionCode(style);
        if (main is null)
            return null;
        var maps = style.Elements(Style + "map")
            .Select(m => (Condition: ((string?)m.Attribute(Style + "condition") ?? "").Replace(" ", "", StringComparison.Ordinal),
                          Target: find((string?)m.Attribute(Style + "apply-style-name") ?? "")))
            .ToList();
        string? Mapped(string condition) =>
            maps.FirstOrDefault(m => m.Condition == condition).Target is { } target ? SectionCode(target) : null;

        // LibreOffice keeps the last section in the style itself and maps the others by condition.
        if (Mapped("value()>=0") is { } positive)
            return positive + ";" + main;
        if (Mapped("value()>0") is { } plus && Mapped("value()<0") is { } minus)
            return plus + ";" + minus + ";" + main;
        return main;
    }

    private static string? SectionCode(XElement style)
    {
        var kind = style.Name.LocalName;
        var isDate = kind is "date-style" or "time-style";
        var elapsed = (string?)style.Attribute(Number + "truncate-on-overflow") == "false";
        var sb = new StringBuilder();
        if ((string?)style.Element(Style + "text-properties")?.Attribute(Fo + "color") is { } color &&
            Colors.FirstOrDefault(c => c.Value.Equals(color, StringComparison.OrdinalIgnoreCase)).Key is { } colorName)
            sb.Append('[').Append(colorName).Append(']');

        foreach (var part in style.Elements().Where(e => e.Name.Namespace == Number))
        {
            var isLong = (string?)part.Attribute(Number + "style") == "long";
            switch (part.Name.LocalName)
            {
                case "text" when kind == "percentage-style":
                    // The percent sign is part of the text; everything around it is literal.
                    var pieces = part.Value.Split('%');
                    sb.Append(string.Join("%", pieces.Select(p => Literal(p, isDate: false))));
                    break;
                case "text":
                    sb.Append(Literal(part.Value, isDate));
                    break;
                case "number":
                    sb.Append(Digits(part));
                    break;
                case "scientific-number":
                    var mantissa = Math.Max(1, Int(part, "min-integer-digits") ?? 1);
                    var places = Int(part, "decimal-places") ?? 0;
                    sb.Append('0', mantissa);
                    if (places > 0)
                        sb.Append('.').Append('0', places);
                    sb.Append("E+").Append('0', Math.Max(1, Int(part, "min-exponent-digits") ?? 2));
                    break;
                case "fraction":
                    if (Int(part, "min-integer-digits") is > 0)
                        sb.Append("# ");
                    sb.Append('?', Math.Max(1, Int(part, "min-numerator-digits") ?? 1)).Append('/');
                    if (Int(part, "denominator-value") is { } denominator)
                        sb.Append(denominator.ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append('?', Math.Max(1, Int(part, "min-denominator-digits") ?? 1));
                    break;
                case "currency-symbol":
                    sb.Append(Literal(part.Value, isDate: false));
                    break;
                case "year":
                    sb.Append(isLong ? "yyyy" : "yy");
                    break;
                case "month":
                    sb.Append(((string?)part.Attribute(Number + "textual") == "true", isLong) switch
                    {
                        (true, true) => "mmmm",
                        (true, false) => "mmm",
                        (false, true) => "mm",
                        _ => "m",
                    });
                    break;
                case "day":
                    sb.Append(isLong ? "dd" : "d");
                    break;
                case "day-of-week":
                    sb.Append(isLong ? "dddd" : "ddd");
                    break;
                case "hours":
                    var hours = isLong ? "hh" : "h";
                    sb.Append(elapsed ? "[" + hours + "]" : hours);
                    break;
                case "minutes":
                    sb.Append(isLong ? "mm" : "m");
                    break;
                case "seconds":
                    sb.Append(isLong ? "ss" : "s");
                    if (Int(part, "decimal-places") is > 0 and var decimals)
                        sb.Append('.').Append('0', decimals);
                    break;
                case "am-pm":
                    sb.Append("AM/PM");
                    break;
                case "text-content":
                    sb.Append('@');
                    break;
                case "fill-character":
                    break;
                default:
                    return null; // boolean, era, quarter, week of year: nothing Excel shows the same way
            }
        }
        return sb.ToString();
    }

    /// <summary>number:number → "#,##0.00" and the like; "General" when it gives no decimal places.</summary>
    private static string Digits(XElement number)
    {
        if (Int(number, "decimal-places") is not { } places)
            return "General";
        var minPlaces = Math.Min(places, Int(number, "min-decimal-places") ?? (int?)number.Attribute(Loext + "min-decimal-places") ?? places);
        var minInteger = Int(number, "min-integer-digits") ?? 1;
        var sb = new StringBuilder();
        if ((string?)number.Attribute(Number + "grouping") == "true")
        {
            var integer = new string('0', minInteger).PadLeft(4, '#');
            sb.Append(integer.AsSpan(0, integer.Length - 3)).Append(',').Append(integer.AsSpan(integer.Length - 3));
        }
        else
        {
            sb.Append(minInteger > 0 ? new string('0', minInteger) : "#");
        }
        if (places > 0)
            sb.Append('.').Append('0', minPlaces).Append('#', places - minPlaces);
        if (double.TryParse((string?)number.Attribute(Number + "display-factor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
        {
            for (; factor >= 1000; factor /= 1000)
                sb.Append(',');
        }
        return sb.ToString();
    }

    private static int? Int(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(Number + attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// Literal text in an Excel code: unquoted when Excel shows those characters as they are, otherwise in quotes
    /// (a quote itself is escaped with a backslash).
    /// </summary>
    private static string Literal(string text, bool isDate)
    {
        var plain = isDate ? " -/:.,()$+" : " -:()$+";
        if (text.All(ch => plain.Contains(ch)))
            return text;
        return string.Join("\\\"", text.Split('"').Select(p => p.Length == 0 ? "" : "\"" + p + "\""));
    }

    // ───────────────────────── Excel → OpenDocument ─────────────────────────

    private enum Kind
    {
        Literal,
        Digit,
        Point,
        Comma,
        Percent,
        Exponent,
        Slash,
        Year,
        Month,
        Minute,
        Day,
        Hour,
        Second,
        AmPm,
        Text,
        Color,
        Currency,
        General,
        Unsupported,
    }

    private readonly record struct Token(Kind Kind, string Text, bool Elapsed = false);

    /// <summary>One section of a format code as a data style body.</summary>
    private sealed record Section(string Element, string Body, string? Color, bool Elapsed);

    /// <summary>
    /// The data style for an Excel format code: XML of the style named <paramref name="name"/> (plus the styles its
    /// sections map to) and the office:value-type its cells use. Null for General, text formats and codes that have
    /// no data style equivalent; such cells are written as plain numbers.
    /// </summary>
    public static (string Xml, string ValueType)? ToOdf(string? code, string name)
    {
        if (NumberFormatter.IsGeneral(code))
            return null;
        var sections = SplitSections(code!);
        if (sections.Count > 4)
            return null;
        // The fourth section formats text cells: nothing to do for numbers.
        var parsed = sections.Take(3).Select(s => BuildSection(Tokenize(s))).ToList();
        if (parsed.Any(p => p is null) || parsed[0]!.Element == "text-style")
            return null;
        var first = parsed[0]!;
        var element = first.Element;
        if (parsed.Count > 1 && element is "date-style" or "time-style")
            parsed = [first]; // dates with several sections are rare; the first one shows positive dates

        var sb = new StringBuilder();
        string StyleXml(string styleName, Section section, string maps) =>
            $"<number:{element} style:name=\"{styleName}\"{(styleName == name ? "" : " style:volatile=\"true\"")}" +
            (section.Elapsed ? " number:truncate-on-overflow=\"false\"" : "") + ">" +
            (section.Color is { } color ? $"<style:text-properties fo:color=\"{color}\"/>" : "") +
            section.Body + maps + $"</number:{element}>";

        switch (parsed.Count)
        {
            case 1:
                sb.Append(StyleXml(name, first, ""));
                break;
            case 2:
                sb.Append(StyleXml(name + "P0", first, ""));
                sb.Append(StyleXml(name, parsed[1]!, $"<style:map style:condition=\"value()&gt;=0\" style:apply-style-name=\"{name}P0\"/>"));
                break;
            default:
                sb.Append(StyleXml(name + "P0", first, ""));
                sb.Append(StyleXml(name + "P1", parsed[1]!, ""));
                sb.Append(StyleXml(name, parsed[2]!,
                    $"<style:map style:condition=\"value()&gt;0\" style:apply-style-name=\"{name}P0\"/><style:map style:condition=\"value()&lt;0\" style:apply-style-name=\"{name}P1\"/>"));
                break;
        }
        var valueType = element switch
        {
            "percentage-style" => "percentage",
            "currency-style" => "currency",
            "date-style" => "date",
            "time-style" => "time",
            _ => "float",
        };
        return (sb.ToString(), valueType);
    }

    /// <summary>Splits at ";" outside quotes, brackets and escapes.</summary>
    private static List<string> SplitSections(string code)
    {
        var sections = new List<string>();
        var start = 0;
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '\\' && !quoted)
                i++;
            else if (ch == '"')
                quoted = !quoted;
            else if (!quoted && ch == '[')
                bracket = true;
            else if (!quoted && ch == ']')
                bracket = false;
            else if (!quoted && !bracket && ch == ';')
            {
                sections.Add(code[start..i]);
                start = i + 1;
            }
        }
        sections.Add(code[start..]);
        return sections;
    }

    private static List<Token> Tokenize(string section)
    {
        if (section.Trim().Equals("General", StringComparison.OrdinalIgnoreCase))
            return [new Token(Kind.General, "")];
        var tokens = new List<Token>();
        for (var i = 0; i < section.Length; i++)
        {
            var ch = section[i];
            switch (ch)
            {
                case '"':
                    var end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    tokens.Add(new Token(Kind.Literal, section[(i + 1)..end]));
                    i = end;
                    break;
                case '\\':
                    if (++i < section.Length)
                        tokens.Add(new Token(Kind.Literal, section[i].ToString()));
                    break;
                case '_':
                    i++; // room as wide as the next character: a space is close enough
                    tokens.Add(new Token(Kind.Literal, " "));
                    break;
                case '*':
                    i++; // repeat the next character to fill the cell: nothing in a fixed layout
                    break;
                case '[':
                    var close = section.IndexOf(']', i + 1);
                    if (close < 0)
                        return [new Token(Kind.Unsupported, "")];
                    tokens.Add(Bracket(section[(i + 1)..close]));
                    i = close;
                    break;
                case '0' or '#' or '?':
                    tokens.Add(new Token(Kind.Digit, ch.ToString()));
                    break;
                case '.':
                    tokens.Add(new Token(Kind.Point, "."));
                    break;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ","));
                    break;
                case '%':
                    tokens.Add(new Token(Kind.Percent, "%"));
                    break;
                case '/':
                    tokens.Add(new Token(Kind.Slash, "/"));
                    break;
                case '@':
                    tokens.Add(new Token(Kind.Text, "@"));
                    break;
                case 'E' or 'e' when i + 1 < section.Length && section[i + 1] is '+' or '-':
                    tokens.Add(new Token(Kind.Exponent, "E" + section[++i]));
                    break;
                default:
                    if (AmPmLength(section, i) is > 0 and var length)
                    {
                        tokens.Add(new Token(Kind.AmPm, section.Substring(i, length)));
                        i += length - 1;
                        break;
                    }
                    var kind = char.ToLowerInvariant(ch) switch
                    {
                        'y' => Kind.Year,
                        'm' => Kind.Month,
                        'd' => Kind.Day,
                        'h' => Kind.Hour,
                        's' => Kind.Second,
                        'e' or 'g' or 'b' => Kind.Unsupported, // era and Buddhist years
                        _ => Kind.Literal,
                    };
                    var run = 1;
                    if (kind != Kind.Literal)
                    {
                        while (i + run < section.Length && char.ToLowerInvariant(section[i + run]) == char.ToLowerInvariant(ch))
                            run++;
                    }
                    tokens.Add(new Token(kind, kind == Kind.Literal ? ch.ToString() : new string(char.ToLowerInvariant(ch), run)));
                    i += run - 1;
                    break;
            }
        }

        // "m" means minutes right after hours or right before seconds.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Month)
                continue;
            var before = tokens.Take(i).LastOrDefault(t => IsDatePart(t.Kind));
            var after = tokens.Skip(i + 1).FirstOrDefault(t => IsDatePart(t.Kind));
            if (before.Kind == Kind.Hour || after.Kind == Kind.Second)
                tokens[i] = tokens[i] with { Kind = Kind.Minute };
        }
        return tokens;
    }

    private static bool IsDatePart(Kind kind) => kind is Kind.Year or Kind.Month or Kind.Minute or Kind.Day or Kind.Hour or Kind.Second or Kind.AmPm;

    private static int AmPmLength(string text, int index)
    {
        foreach (var marker in new[] { "AM/PM", "A/P" })
        {
            if (string.Compare(text, index, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return marker.Length;
        }
        return 0;
    }

    /// <summary>[Red], [$₩-412], [h], [&gt;100], ...</summary>
    private static Token Bracket(string inner)
    {
        if (inner.StartsWith('$'))
        {
            var dash = inner.IndexOf('-', StringComparison.Ordinal);
            var symbol = dash < 0 ? inner[1..] : inner[1..dash];
            return symbol.Length > 0 ? new Token(Kind.Currency, symbol) : new Token(Kind.Literal, "");
        }
        if (inner.Length > 0 && inner.All(ch => char.ToLowerInvariant(ch) == char.ToLowerInvariant(inner[0])) && char.ToLowerInvariant(inner[0]) is 'h' or 'm' or 's')
        {
            var kind = char.ToLowerInvariant(inner[0]) switch { 'h' => Kind.Hour, 'm' => Kind.Minute, _ => Kind.Second };
            return new Token(kind, inner.ToLowerInvariant(), Elapsed: true);
        }
        // Conditions ([>100]), numbered colours and number systems ([DBNum1]) change what is shown.
        return Colors.TryGetValue(inner, out var color) ? new Token(Kind.Color, color) : new Token(Kind.Unsupported, inner);
    }

    private static Section? BuildSection(List<Token> tokens)
    {
        if (tokens.Any(t => t.Kind == Kind.Unsupported))
            return null;
        if (tokens.Any(t => t.Kind == Kind.Text))
            return new Section("text-style", "", null, false);
        var color = tokens.LastOrDefault(t => t.Kind == Kind.Color).Text;
        return tokens.Any(t => IsDatePart(t.Kind)) ? DateSection(tokens, color) : NumberSection(tokens, color);
    }

    private static Section? DateSection(List<Token> tokens, string? color)
    {
        var body = new StringBuilder();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
                body.Append("<number:text>").Append(Escape(literal.ToString())).Append("</number:text>");
            literal.Clear();
        }
        var elapsed = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var length = token.Text.Trim('[', ']').Length;
            switch (token.Kind)
            {
                case Kind.Literal or Kind.Point or Kind.Comma or Kind.Slash or Kind.Percent:
                    literal.Append(token.Text);
                    continue;
                case Kind.Color:
                    continue;
                case Kind.Digit or Kind.Exponent or Kind.Currency or Kind.General:
                    return null;
            }
            Flush();
            switch (token.Kind)
            {
                case Kind.Year:
                    body.Append(length > 2 ? "<number:year number:style=\"long\"/>" : "<number:year/>");
                    break;
                case Kind.Month:
                    body.Append(length switch
                    {
                        1 => "<number:month/>",
                        2 => "<number:month number:style=\"long\"/>",
                        3 or 5 => "<number:month number:textual=\"true\"/>",
                        _ => "<number:month number:style=\"long\" number:textual=\"true\"/>",
                    });
                    break;
                case Kind.Day:
                    body.Append(length switch
                    {
                        1 => "<number:day/>",
                        2 => "<number:day number:style=\"long\"/>",
                        3 => "<number:day-of-week/>",
                        _ => "<number:day-of-week number:style=\"long\"/>",
                    });
                    break;
                case Kind.Hour:
                    elapsed |= token.Elapsed;
                    body.Append(length > 1 ? "<number:hours number:style=\"long\"/>" : "<number:hours/>");
                    break;
                case Kind.Minute:
                    body.Append(length > 1 ? "<number:minutes number:style=\"long\"/>" : "<number:minutes/>");
                    break;
                case Kind.Second:
                    // Fractions of a second: "ss.00".
                    var places = 0;
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Point)
                    {
                        while (i + 2 + places < tokens.Count && tokens[i + 2 + places] is { Kind: Kind.Digit, Text: "0" })
                            places++;
                        if (places > 0)
                            i += 1 + places;
                    }
                    body.Append(CultureInfo.InvariantCulture, $"<number:seconds{(length > 1 ? " number:style=\"long\"" : "")}{(places > 0 ? $" number:decimal-places=\"{places}\"" : "")}/>");
                    break;
                case Kind.AmPm:
                    body.Append("<number:am-pm/>");
                    break;
            }
        }
        Flush();
        var isDate = tokens.Any(t => t.Kind is Kind.Year or Kind.Month or Kind.Day);
        return new Section(isDate ? "date-style" : "time-style", body.ToString(), color, elapsed && !isDate);
    }

    private static Section? NumberSection(List<Token> tokens, string? color)
    {
        if (tokens.Any(t => t.Kind == Kind.Slash))
            return null; // fractions
        var start = tokens.FindIndex(t => t.Kind is Kind.Digit or Kind.Point or Kind.General);
        var end = start;
        if (start >= 0)
        {
            while (end + 1 < tokens.Count && tokens[end + 1].Kind is Kind.Digit or Kind.Point or Kind.Comma or Kind.Exponent)
                end++;
            if (tokens.Skip(end + 1).Any(t => t.Kind is Kind.Digit or Kind.Point or Kind.General))
                return null; // text between the digits ("000-0000")
        }

        var body = new StringBuilder();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
                body.Append("<number:text>").Append(Escape(literal.ToString())).Append("</number:text>");
            literal.Clear();
        }
        var percent = false;
        var currency = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i == start)
            {
                Flush();
                body.Append(tokens[i].Kind == Kind.General ? "<number:number number:min-integer-digits=\"1\"/>" : Digits(tokens[start..(end + 1)]));
                i = end;
                continue;
            }
            var token = tokens[i];
            switch (token.Kind)
            {
                case Kind.Percent:
                    percent = true;
                    literal.Append('%');
                    break;
                case Kind.Currency:
                    currency = true;
                    Flush();
                    body.Append("<number:currency-symbol>").Append(Escape(token.Text)).Append("</number:currency-symbol>");
                    break;
                case Kind.Literal or Kind.Comma or Kind.Point:
                    literal.Append(token.Text);
                    break;
            }
        }
        Flush();
        return new Section(currency ? "currency-style" : percent ? "percentage-style" : "number-style", body.ToString(), color, false);
    }

    /// <summary>The digits of a number section → number:number or number:scientific-number.</summary>
    private static string Digits(List<Token> digits)
    {
        var exponent = digits.FindIndex(t => t.Kind == Kind.Exponent);
        var mantissa = exponent < 0 ? digits : digits[..exponent];
        var point = mantissa.FindIndex(t => t.Kind == Kind.Point);
        var integer = point < 0 ? mantissa : mantissa[..point];
        var fraction = point < 0 ? [] : mantissa[(point + 1)..];

        var minInteger = integer.Count(t => t.Text == "0");
        var lastIntegerDigit = integer.FindLastIndex(t => t.Kind == Kind.Digit);
        var grouping = integer.Take(Math.Max(0, lastIntegerDigit)).Any(t => t.Kind == Kind.Comma);
        // Commas after the last digit divide by 1000 each ("#,##0,").
        var scaling = mantissa.Skip(mantissa.FindLastIndex(t => t.Kind == Kind.Digit) + 1).Count(t => t.Kind == Kind.Comma);
        var places = fraction.Count(t => t.Kind == Kind.Digit);
        var minPlaces = fraction.Count(t => t.Text == "0");

        if (exponent >= 0)
        {
            var exponentDigits = digits[(exponent + 1)..].Count(t => t.Kind == Kind.Digit);
            return string.Create(CultureInfo.InvariantCulture,
                $"<number:scientific-number number:decimal-places=\"{places}\" number:min-integer-digits=\"{Math.Max(1, minInteger)}\" number:min-exponent-digits=\"{Math.Max(1, exponentDigits)}\"/>");
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"<number:number number:decimal-places=\"{places}\" number:min-decimal-places=\"{minPlaces}\" number:min-integer-digits=\"{minInteger}\"") +
            (grouping ? " number:grouping=\"true\"" : "") +
            (scaling > 0 ? string.Create(CultureInfo.InvariantCulture, $" number:display-factor=\"{Math.Pow(1000, scaling)}\"") : "") + "/>";
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}
