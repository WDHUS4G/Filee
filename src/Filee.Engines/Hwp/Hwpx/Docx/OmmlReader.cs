// Word equations (Office Math, m:oMath) → the equation tree of EquationScript, which the HWPX writer stores as a 한글
// equation (and other writers as linear text). Covers what Word's equation editor makes: fractions, scripts, roots,
// sums and integrals with limits, brackets, matrices, equation arrays and cases, functions and limits, accents and bars.
// Boxes, border boxes, phantoms and group characters keep their content. Plain (m:sty="p") and normal text is upright.

using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal static class OmmlReader
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Unicode symbols written as 한글 equation commands (all known to rhwp too).</summary>
    private static readonly Dictionary<char, string> Commands = new()
    {
        ['α'] = "alpha",
        ['β'] = "beta",
        ['γ'] = "gamma",
        ['δ'] = "delta",
        ['ε'] = "epsilon",
        ['ϵ'] = "epsilon",
        ['ζ'] = "zeta",
        ['η'] = "eta",
        ['θ'] = "theta",
        ['ϑ'] = "vartheta",
        ['ι'] = "iota",
        ['κ'] = "kappa",
        ['λ'] = "lambda",
        ['μ'] = "mu",
        ['ν'] = "nu",
        ['ξ'] = "xi",
        ['ο'] = "omicron",
        ['π'] = "pi",
        ['ϖ'] = "varpi",
        ['ρ'] = "rho",
        ['σ'] = "sigma",
        ['ς'] = "varsigma",
        ['τ'] = "tau",
        ['υ'] = "upsilon",
        ['φ'] = "phi",
        ['ϕ'] = "phi",
        ['χ'] = "chi",
        ['ψ'] = "psi",
        ['ω'] = "omega",
        ['Α'] = "Alpha",
        ['Β'] = "Beta",
        ['Γ'] = "Gamma",
        ['Δ'] = "Delta",
        ['Ε'] = "Epsilon",
        ['Ζ'] = "Zeta",
        ['Η'] = "Eta",
        ['Θ'] = "Theta",
        ['Ι'] = "Iota",
        ['Κ'] = "Kappa",
        ['Λ'] = "Lambda",
        ['Μ'] = "Mu",
        ['Ν'] = "Nu",
        ['Ξ'] = "Xi",
        ['Ο'] = "Omicron",
        ['Π'] = "Pi",
        ['Ρ'] = "Rho",
        ['Σ'] = "Sigma",
        ['Τ'] = "Tau",
        ['Υ'] = "Upsilon",
        ['Φ'] = "Phi",
        ['Χ'] = "Chi",
        ['Ψ'] = "Psi",
        ['Ω'] = "Omega",
        ['×'] = "TIMES",
        ['÷'] = "DIV",
        ['±'] = "PLUSMINUS",
        ['∓'] = "MINUSPLUS",
        ['·'] = "CDOT",
        ['⋅'] = "CDOT",
        ['∘'] = "CIRC",
        ['•'] = "BULLET",
        ['∙'] = "BULLET",
        ['≠'] = "NEQ",
        ['≤'] = "LEQ",
        ['≦'] = "LEQ",
        ['≥'] = "GEQ",
        ['≧'] = "GEQ",
        ['≈'] = "APPROX",
        ['≡'] = "EQUIV",
        ['≅'] = "CONG",
        ['∼'] = "SIM",
        ['≃'] = "SIMEQ",
        ['∝'] = "PROPTO",
        ['∞'] = "INF",
        ['∂'] = "PARTIAL",
        ['∈'] = "IN",
        ['∉'] = "NOTIN",
        ['∋'] = "OWNS",
        ['⊂'] = "SUBSET",
        ['⊃'] = "SUPERSET",
        ['⊆'] = "SUBSETEQ",
        ['⊇'] = "SUPSETEQ",
        ['∪'] = "CUP",
        ['∩'] = "CAP",
        ['∀'] = "FORALL",
        ['∃'] = "EXIST",
        ['¬'] = "LNOT",
        ['∧'] = "WEDGE",
        ['∨'] = "VEE",
        ['∅'] = "EMPTYSET",
        ['∴'] = "THEREFORE",
        ['∵'] = "BECAUSE",
        ['→'] = "rarrow",
        ['←'] = "larrow",
        ['↔'] = "lrarrow",
        ['↑'] = "uparrow",
        ['↓'] = "downarrow",
        ['⇒'] = "RARROW",
        ['⇐'] = "LARROW",
        ['⇔'] = "LRARROW",
        ['⋯'] = "CDOTS",
        ['…'] = "LDOTS",
        ['⋮'] = "VDOTS",
        ['⋱'] = "DDOTS",
        ['°'] = "DEG",
        ['′'] = "prime",
        ['ℓ'] = "ELL",
        ['ℏ'] = "HBAR",
        ['ℵ'] = "ALEPH",
        ['∠'] = "ANGLE",
        ['△'] = "TRIANGLE",
        ['⊥'] = "BOT",
        ['{'] = "LBRACE",
        ['}'] = "RBRACE",
        ['\\'] = "RSLANT",
        ['~'] = "SIM",
    };

    /// <summary>Large operators (m:nary, or the character typed as text) → their command.</summary>
    private static readonly Dictionary<string, string> BigOperators = new()
    {
        ["∑"] = "SUM",
        ["∏"] = "PROD",
        ["∐"] = "COPROD",
        ["⋃"] = "BIGCUP",
        ["⋂"] = "BIGCAP",
        ["⋁"] = "BIGVEE",
        ["⋀"] = "BIGWEDGE",
        ["∫"] = "INT",
        ["∬"] = "DINT",
        ["∭"] = "TINT",
        ["∮"] = "OINT",
        ["∯"] = "ODINT",
        ["∰"] = "OTINT",
    };

    /// <summary>Function names both 한글 and rhwp write upright by themselves.</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth",
        "log", "ln", "lg", "exp", "det", "dim", "ker", "hom", "arg", "gcd", "lcm", "max", "min", "mod",
    };

    /// <summary>
    /// Words of the equation language, compared ignoring case: a variable spelled like one ("in", "to", "max", "Pi") is
    /// written one letter at a time so that 한글 and rhwp do not read it as a command.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "over", "atop", "sup", "sub", "sqrt", "root", "of", "left", "right", "bigg", "int", "integral", "oint", "smallint",
        "lim", "sum", "union", "inter", "prod", "rel", "buildrel", "pile", "lpile", "rpile", "cases", "choose", "binom",
        "matrix", "pmatrix", "bmatrix", "dmatrix", "vmatrix", "col", "lcol", "rcol", "lsub", "lsup", "ladder", "sladder",
        "longdiv", "color", "arch", "under", "not", "rm", "it", "bold", "from", "to", "inf", "infty", "in", "le", "ge",
        "ne", "ll", "gg", "text", "frac", "begin", "end", "quad", "qquad", "phantom", "eqalign", "benzene", "base",
        "top", "bot", "well", "att", "hund", "thou", "ohm", "mho", "wp", "ell", "liter", "image", "imag", "dots", "vert",
        "nabla", "deg", "prime", "star", "ast", "pm", "mp", "neg", "land", "lor", "xor", "gets", "implies", "iff",
        "mapsto", "pr", "limsup", "liminf", "ni", "exists", "angle", "triangle", "sim", "times", "div", "divide",
    };

    /// <summary>
    /// Beginnings that rhwp (and Filee's equation fixes before rhwp, RhwpInput) split off a longer word as a command
    /// ("overa" → over a, "sinx" → sin x, "lek" → le k, "INPUT" → IN PUT).
    /// </summary>
    private static readonly string[] ReservedPrefixes =
    [
        "over", "atop", "bold", "it", "rm", "times", "sim", "prime", "root", "sqrt", "sin", "cos", "tan", "sec", "csc",
        "cot", "bar", "vec", "hat", "tilde", "dot", "ddot", "acute", "grave", "check", "breve", "leq", "geq", "neq",
        "triangle", "angle", "cap", "cup", "cdot", "ldots", "vdots", "ddots", "widehat", "widetilde", "le", "ge", "in",
        "circ", "subset", "superset", "notin", "approx", "equiv", "div", "pm", "mp", "smallinter", "smallunion", "owns",
        "inter", "union", "cong", "plusminus", "minusplus", "bullet", "oplus", "forall", "exist", "therefore", "because",
        "deg", "inf", "partial", "rarrow", "larrow", "lrarrow", "emptyset", "propto", "underline",
    ];

    /// <summary>A word that stays one variable (italic letters side by side) in 한글 and rhwp.</summary>
    private static bool IsPlainWord(string word) =>
        word.Length == 1
        || (!Reserved.Contains(word)
            && !Commands.Values.Concat(BigOperators.Values).Concat(Functions).Concat(Accents.Values).Contains(word, StringComparer.OrdinalIgnoreCase)
            && !ReservedPrefixes.Any(p => word.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Accent characters (m:acc) → decoration command.</summary>
    private static readonly Dictionary<char, string> Accents = new()
    {
        ['\u0302'] = "hat",
        ['^'] = "hat",
        ['\u030C'] = "check",
        ['\u0303'] = "tilde",
        ['~'] = "tilde",
        ['\u0301'] = "acute",
        ['\u0300'] = "grave",
        ['\u0307'] = "dot",
        ['\u0308'] = "ddot",
        ['\u0304'] = "bar",
        ['\u0305'] = "bar",
        ['¯'] = "bar",
        ['\u20D7'] = "vec",
        ['\u20D6'] = "vec",
        ['→'] = "vec",
        ['\u20E1'] = "dyad",
    };

    /// <summary>The equation tree of one m:oMath.</summary>
    public static MathNode Read(XElement oMath) => Content(oMath, roman: false);

    // ───────────────────────── Containers ─────────────────────────

    /// <summary>The children of an argument (m:e, m:num, m:sub ...) side by side; null and empty give an empty row.</summary>
    private static MathRow Content(XElement? container, bool roman)
    {
        var items = new List<MathNode>();
        foreach (var child in container?.Elements() ?? [])
            Element(child, items, roman);
        return new MathRow(items);
    }

    private static MathRow Argument(XElement parent, string name, bool roman = false) => Content(parent.Element(M + name), roman);

    /// <summary>Adds a row as one group, or its only item by itself (the same to 한글, a simpler script).</summary>
    private static void Add(List<MathNode> output, MathRow row)
    {
        if (row.Items.Count == 1)
            output.Add(row.Items[0]);
        else if (row.Items.Count > 0)
            output.Add(row);
    }

    private static void Element(XElement element, List<MathNode> output, bool roman)
    {
        if (element.Name.Namespace == W)
        {
            switch (element.Name.LocalName)
            {
                case "r":
                    // A non-math run inside an equation: upright text.
                    Text(string.Concat(element.Elements(W + "t").Select(t => t.Value)), output, Style.Normal);
                    break;
                case "ins" or "smartTag" or "customXml" or "sdt" or "sdtContent":
                    foreach (var child in element.Elements())
                        Element(child, output, roman);
                    break;
            }
            return;
        }
        if (element.Name.Namespace != M)
            return;

        switch (element.Name.LocalName)
        {
            case "r":
                Run(element, output, roman);
                break;
            case "f":
                output.Add(Fraction(element));
                break;
            case "sSub":
                output.Add(Scripts(Argument(element, "e", roman), Argument(element, "sub"), null));
                break;
            case "sSup":
                output.Add(Scripts(Argument(element, "e", roman), null, Argument(element, "sup")));
                break;
            case "sSubSup":
                output.Add(Scripts(Argument(element, "e", roman), Argument(element, "sub"), Argument(element, "sup")));
                break;
            case "sPre":
                // Scripts before the base: {} _{a} ^{b} base.
                output.Add(new MathScripts(MathRow.Empty, Argument(element, "sub"), Argument(element, "sup")));
                Add(output, Argument(element, "e"));
                break;
            case "rad":
                {
                    var degree = Argument(element, "deg");
                    var hidden = Flag(element.Element(M + "radPr")?.Element(M + "degHide"));
                    output.Add(new MathRoot(hidden || IsEmpty(degree) ? null : degree, Argument(element, "e")));
                    break;
                }
            case "nary":
                Nary(element, output);
                break;
            case "d":
                output.Add(Delimiter(element));
                break;
            case "m":
                output.Add(Matrix(element, "matrix"));
                break;
            case "eqArr":
                output.Add(new MathPile(element.Elements(M + "e").Select(e => (MathNode)WithoutAlign(Content(e, roman))).ToList()));
                break;
            case "func":
                Add(output, Argument(element, "fName", roman: true));
                Add(output, Argument(element, "e"));
                break;
            case "limLow":
                {
                    var name = Argument(element, "e", roman);
                    var limit = Argument(element, "lim");
                    output.Add(name.Items is [MathLimit] ? new MathLimit(limit) : Scripts(name, limit, null));
                    break;
                }
            case "limUpp":
                output.Add(Scripts(Argument(element, "e", roman), null, Argument(element, "lim")));
                break;
            case "acc":
                {
                    var chr = Val(element.Element(M + "accPr")?.Element(M + "chr"));
                    var body = Argument(element, "e", roman);
                    var mark = string.IsNullOrEmpty(chr) ? '\u0302' : chr[0];
                    output.Add(Accents.TryGetValue(mark, out var command) ? new MathAccent(command, body) : body);
                    break;
                }
            case "bar":
                {
                    var top = Val(element.Element(M + "barPr")?.Element(M + "pos")) == "top";
                    output.Add(new MathAccent(top ? "OVERLINE" : "UNDERLINE", Argument(element, "e", roman)));
                    break;
                }
            case "box" or "borderBox" or "phant" or "groupChr":
                Add(output, Argument(element, "e", roman));
                break;
            case "oMath":
                Add(output, Content(element, roman));
                break;
        }
    }

    private static MathNode Fraction(XElement f)
    {
        var numerator = Argument(f, "num");
        var denominator = Argument(f, "den");
        return Val(f.Element(M + "fPr")?.Element(M + "type")) switch
        {
            "noBar" => new MathFraction(numerator, denominator, Bar: false),
            "lin" or "skw" => new MathRow([numerator, new MathSymbol("/"), denominator]),
            _ => new MathFraction(numerator, denominator),
        };
    }

    /// <summary>Scripts; a large operator character as the base (∑ typed as text) takes them as limits.</summary>
    private static MathNode Scripts(MathRow baseRow, MathRow? sub, MathRow? sup)
    {
        if (baseRow.Items is [MathBigOperator { Lower: null, Upper: null } big])
            return big with { Lower = sub, Upper = sup };
        return new MathScripts(baseRow.Items.Count == 1 ? baseRow.Items[0] : baseRow, sub, sup);
    }

    private static void Nary(XElement nary, List<MathNode> output)
    {
        var properties = nary.Element(M + "naryPr");
        var chr = properties?.Element(M + "chr") is { } chrElement ? Val(chrElement) ?? "∫" : "∫";
        var sub = Flag(properties?.Element(M + "subHide")) ? null : Argument(nary, "sub");
        var sup = Flag(properties?.Element(M + "supHide")) ? null : Argument(nary, "sup");
        if (sub is not null && IsEmpty(sub))
            sub = null;
        if (sup is not null && IsEmpty(sup))
            sup = null;
        output.Add(BigOperators.TryGetValue(chr, out var command)
            ? new MathBigOperator(command, chr, sub, sup)
            : new MathScripts(new MathText(chr), sub, sup));
        Add(output, Argument(nary, "e"));
    }

    private static MathNode Delimiter(XElement d)
    {
        var properties = d.Element(M + "dPr");
        string Character(string name, string fallback) =>
            properties?.Element(M + name) is { } element ? Val(element) ?? "" : fallback;
        var open = Character("begChr", "(");
        var close = Character("endChr", ")");
        var separator = Character("sepChr", "|");
        var parts = d.Elements(M + "e").ToList();

        // A matrix in brackets is a pmatrix / bmatrix / dmatrix; a brace before an equation array is cases.
        if (parts.Count == 1 && Arguments(parts[0]) is [var only])
        {
            var style = (open, close) switch
            {
                ("(", ")") => "pmatrix",
                ("[", "]") => "bmatrix",
                ("|", "|") => "dmatrix",
                _ => null,
            };
            if (only.Name == M + "m" && style is not null)
                return Matrix(only, style);
            if (only.Name == M + "eqArr" && open == "{" && close.Length == 0)
                return Cases(only);
        }

        var items = new List<MathNode>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                Text(separator, items, Style.Plain);
            items.AddRange(Content(parts[i], roman: false).Items);
        }
        var body = new MathRow(items);
        if (EquationScript.IsBracket(open) && EquationScript.IsBracket(close))
            return new MathBrackets(open, close, body);

        // Brackets 한글 cannot stretch (rare): written as characters around the content.
        var row = new List<MathNode>();
        Text(open, row, Style.Plain);
        row.Add(body);
        Text(close, row, Style.Plain);
        return new MathRow(row);
    }

    private static MathMatrix Matrix(XElement m, string command) =>
        new(command, m.Elements(M + "mr").Select(row => (IReadOnlyList<MathNode>)row.Elements(M + "e").Select(e => (MathNode)Content(e, roman: false)).ToList()).ToList());

    /// <summary>Cases: each line of the equation array split into cells at its alignment marks.</summary>
    private static MathCases Cases(XElement eqArr)
    {
        var rows = new List<IReadOnlyList<MathNode>>();
        foreach (var line in eqArr.Elements(M + "e"))
        {
            var cells = new List<MathNode>();
            var cell = new List<MathNode>();
            foreach (var item in Content(line, roman: false).Items)
            {
                if (item is MathAlign)
                {
                    cells.Add(new MathRow(cell));
                    cell = [];
                }
                else
                {
                    cell.Add(item);
                }
            }
            cells.Add(new MathRow(cell));
            rows.Add(cells);
        }
        return new MathCases(rows);
    }

    private static MathRow WithoutAlign(MathRow row) => new(row.Items.Where(i => i is not MathAlign).ToList());

    /// <summary>The argument elements of a container (properties left out).</summary>
    private static List<XElement> Arguments(XElement container) =>
        container.Elements().Where(e => e.Name.Namespace == M && !e.Name.LocalName.EndsWith("Pr", StringComparison.Ordinal)).ToList();

    // ───────────────────────── Text ─────────────────────────

    private enum Style
    {
        Italic,
        Plain,
        Bold,
        Normal, // non-math text: upright, spaces kept
    }

    private static void Run(XElement r, List<MathNode> output, bool roman)
    {
        var properties = r.Element(M + "rPr");
        var style = Flag(properties?.Element(M + "nor")) ? Style.Normal
            : Val(properties?.Element(M + "sty")) switch
            {
                "p" => Style.Plain,
                "b" or "bi" => Style.Bold,
                _ => roman ? Style.Plain : Style.Italic,
            };
        var text = new StringBuilder();
        foreach (var child in r.Elements())
        {
            if (child.Name == M + "t" || child.Name == W + "t")
                text.Append(child.Value);
            else if (child.Name == W + "sym" && int.TryParse((string?)child.Attribute(W + "char"), System.Globalization.NumberStyles.HexNumber, null, out var code))
                text.Append(SymbolFonts.Map((string?)child.Attribute(W + "font"), code) ?? ((char)code).ToString());
        }
        Text(text.ToString(), output, style);
    }

    /// <summary>Tokens of a text: letters one by one, numbers, operators, symbols as commands.</summary>
    private static void Text(string text, List<MathNode> output, Style style)
    {
        if (text.Length == 0)
            return;
        var upright = style is Style.Plain or Style.Normal;
        if (upright && Functions.Contains(text.Trim()))
        {
            output.Add(new MathFunction(text.Trim()));
            return;
        }
        if (upright && text.Trim() == "lim")
        {
            output.Add(new MathLimit(null));
            return;
        }

        var items = new List<MathNode>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i + 1 < text.Length && (char.IsAsciiDigit(text[i + 1]) || (text[i + 1] == '.' && i + 2 < text.Length && char.IsAsciiDigit(text[i + 2]))))
                    i++;
                items.Add(new MathNumber(text[start..(i + 1)]));
            }
            else if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i + 1 < text.Length && char.IsAsciiLetter(text[i + 1]))
                    i++;
                var word = text[start..(i + 1)];
                if (IsPlainWord(word))
                    items.Add(new MathText(word));
                else
                    items.AddRange(word.Select(letter => new MathText(letter.ToString())));
            }
            else if (c is ' ' or '\u00A0' or '\u2009' or '\u2005' or '\u2006' or '\u200A')
            {
                if (style == Style.Normal)
                    items.Add(new MathSpace());
            }
            else if (c == '&')
            {
                items.Add(new MathAlign());
            }
            else if (c is '\u2212' or '\u2013')
            {
                items.Add(new MathSymbol("-"));
            }
            else if (c is '+' or '-' or '=' or '<' or '>' or ',' or '.' or '!' or '|' or '/' or '*' or ':' or '\'' or '(' or ')' or '[' or ']')
            {
                items.Add(new MathSymbol(c.ToString()));
            }
            else if (Commands.TryGetValue(c, out var command))
            {
                items.Add(new MathCommand(command, c.ToString()));
            }
            else if (BigOperators.TryGetValue(c.ToString(), out var big))
            {
                items.Add(new MathBigOperator(big, c.ToString(), null, null));
            }
            else if (c is '#' or '_' or '^' or '`')
            {
                items.Add(new MathQuoted(c.ToString()));
            }
            else if (c == '"' || char.IsControl(c) || c == '\u200B')
            {
                // Quote marks would end a quoted text; zero-width and control characters show nothing.
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                items.Add(new MathText(MathAlphanumeric(char.ConvertToUtf32(c, text[i + 1]))));
                i++;
            }
            else
            {
                items.Add(new MathText(c.ToString()));
            }
        }

        if (style is Style.Plain or Style.Normal or Style.Bold && items.Any(i => i is MathText))
            output.Add(new MathStyled(style == Style.Bold ? "bold" : "rm", new MathRow(items)));
        else
            output.AddRange(items);
    }

    /// <summary>
    /// Mathematical alphanumeric symbols (U+1D400…, italic 𝑥 from some converters) → the plain letter or digit; the
    /// equation's own style makes them italic or bold.
    /// </summary>
    private static string MathAlphanumeric(int codePoint)
    {
        if (codePoint is >= 0x1D400 and <= 0x1D6A3)
        {
            var index = (codePoint - 0x1D400) % 52;
            return ((char)(index < 26 ? 'A' + index : 'a' + index - 26)).ToString();
        }
        if (codePoint is >= 0x1D7CE and <= 0x1D7FF)
            return ((char)('0' + (codePoint - 0x1D7CE) % 10)).ToString();
        return char.ConvertFromUtf32(codePoint);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string? Val(XElement? element) => (string?)element?.Attribute(M + "val");

    /// <summary>An on/off property: present without a value, or with 1 / on / true.</summary>
    private static bool Flag(XElement? element) => element is not null && Val(element) is null or "1" or "on" or "true";

    private static bool IsEmpty(MathRow row) => row.Items.All(i => i is MathRow inner ? IsEmpty(inner) : i is MathAlign);
}
