// What Filee changes in an HWPX before rhwp renders it to PDF, in a copy (the user's file is never touched):
//
// - Fonts (RhwpFonts): installed fonts for the text's language and characters, no CFF2 variable fonts (#61, #64).
// - Punctuation widths (RhwpInput.Punctuation): quotes and CJK punctuation in Chinese and Japanese fonts get letter
//   spacing up to their real width, which rhwp guesses as half a character (#237).
// - Shape sizes: a box inside a text box stores its size relative to the column (hp:sz widthRelTo="COLUMN", 9920 =
//   99.2 %); rhwp takes the number as absolute there and drew the exam paper's "보기" boxes 35 mm wide with their
//   pictures shrunk to a few pixels (#65). The size 한글 laid the object out at (hp:curSz) is written instead.
// - Equation scripts: 한글 2022 saves a command glued to the next letter ("f CIRCf", "A SMALLINTERB", "lek", "rootx")
//   and writes != and +- for ≠ and ±. rhwp printed those as italic text ("fCIRCf"); they are spaced and spelled out.
// - Tables floating in a table cell, in an HWPX rhwp exported from an HWP (#49): 한글 stores the line of the paragraph
//   that anchors a table (hp:pos vertRelTo="PARA", text above and below it) under the table. rhwp follows the stored
//   lines of an HWP (it reads the HWP itself and an HWPX with its META-INF/rhwp-hwp5-origin marker that way) and, in
//   a cell taller than its text, drew the table from that line instead of the paragraph's top, over the paragraphs
//   below. The same document saved as HWPX by 한글 (no marker) came out right. When the anchor paragraph has no
//   text, its line is moved up to the paragraph's top (the line minus the table's offset, margins and height):
//   nothing is drawn on it, and rhwp then puts the table where 한글 does.
//
// - Equation lines (#143): in paragraphs without stored lines (Word equations written by Filee) rhwp draws a fraction,
//   matrix or sum partly below its line, over the next line. Those paragraphs get the overhang as space after them.
//
// rhwp's equation fonts are Latin Modern Math, STIX Two and Times New Roman; without the first two or Arial Unicode MS
// (which Office installs) ∪ ∈ ⊂ ∘ ∼ came out empty, so Cambria Math (part of Windows) is asked for then.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Filee.Engines.Hwp;

internal static partial class RhwpInput
{
    /// <summary>Equation commands 한글 separates from a following identifier (found in 한글 2022 documents).</summary>
    private static readonly string[] UpperCommands =
    [
        "CIRC", "SMALLINTER", "SMALLUNION", "SUBSET", "SUPERSET", "IN", "NOTIN", "OWNS", "INTER", "UNION", "APPROX",
        "EQUIV", "CONG", "DIV", "PLUSMINUS", "MINUSPLUS", "CDOT", "BULLET", "OPLUS", "FORALL", "EXIST", "THEREFORE",
        "BECAUSE", "DEG", "INF", "PARTIAL", "RARROW", "LARROW", "LRARROW", "EMPTYSET", "PROPTO",
    ];

    /// <summary>Lowercase commands: only split before one or two letters or digits ("lek", "le28", "rootxy").</summary>
    private static readonly string[] LowerCommands =
        ["le", "ge", "root", "sqrt", "circ", "subset", "superset", "in", "notin", "approx", "equiv", "div", "pm", "mp", "cdot"];

    /// <summary>Longer commands that start like one of the above and stay whole.</summary>
    private static readonly HashSet<string> Whole = new(StringComparer.Ordinal)
    {
        "left", "leq", "geq", "int", "inf", "inter", "cdots", "CDOTS", "SUBSETEQ", "SUPSETEQ", "INTER", "INF", "NOTIN",
        "notin", "pmatrix", "LEFT", "RIGHT", "LEQ", "GEQ", "INT", "INFTY", "infty", "DIVIDE",
    };

    private static readonly (string Text, string Command)[] Operators =
        [("!=", " NEQ "), ("<=", " LEQ "), (">=", " GEQ "), ("+-", " PLUSMINUS "), ("-+", " MINUSPLUS ")];

    /// <summary>The entry rhwp's <c>export-hwpx</c> adds to an HWPX it wrote for an HWP file.</summary>
    internal const string Hwp5OriginMarker = "META-INF/rhwp-hwp5-origin";

    private static readonly string[] SizedObjects =
        ["rect", "ellipse", "arc", "polygon", "curve", "line", "connectLine", "container", "pic", "ole", "textart"];

    /// <summary>
    /// A copy of the HWPX in <paramref name="workDirectory"/> with the changes above, or null when nothing needs
    /// changing (or the file is not an HWPX package Filee can read).
    /// </summary>
    /// <param name="lookup">Installed fonts by name (tests pass a fake computer).</param>
    public static string? Prepare(string hwpx, string workDirectory, Func<string, InstalledFont?>? lookup = null)
    {
        try
        {
            var changed = new Dictionary<string, XDocument>(StringComparer.Ordinal);
            XDocument? header = null;
            var sections = new List<(string Name, XDocument Xml)>();
            bool fromHwp;
            using (var zip = ZipFile.OpenRead(hwpx))
            {
                if (zip.GetEntry("Contents/header.xml") is not { } entry)
                    return null;
                header = Load(entry);
                fromHwp = zip.GetEntry(Hwp5OriginMarker) is not null;
                foreach (var section in zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                    sections.Add((section.FullName, Load(section)));
            }

            var text = new StringBuilder();
            foreach (var (_, xml) in sections)
                foreach (var t in xml.Descendants().Where(e => e.Name.LocalName == "t"))
                    if (text.Length < 200_000)
                        text.Append(t.Value);
            lookup ??= RhwpFonts.Find;
            if (ApplyFonts(header, text.ToString(), lookup))
                changed["Contents/header.xml"] = header;
            if (WidenPunctuation(header, sections.Select(s => s.Xml).ToList(), lookup))
            {
                changed["Contents/header.xml"] = header;
                foreach (var (name, xml) in sections)
                    changed[name] = xml;
            }

            foreach (var (name, xml) in sections)
            {
                if (FixShapeSizes(xml) | NormaliseEquations(xml) | (fromHwp && AnchorTablesInCells(xml)))
                    changed[name] = xml;
                if (SpaceEquationLines(xml, header))
                {
                    changed[name] = xml;
                    changed["Contents/header.xml"] = header;
                }
            }
            if (changed.Count == 0)
                return null;

            var copy = Path.Combine(workDirectory, $"rhwp-{Guid.NewGuid():N}.hwpx");
            File.Copy(hwpx, copy);
            using (var zip = ZipFile.Open(copy, ZipArchiveMode.Update))
                foreach (var (name, xml) in changed)
                {
                    zip.GetEntry(name)!.Delete();
                    using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    xml.Save(stream, SaveOptions.DisableFormatting);
                }
            return copy;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return null; // not a package Filee can read: rhwp reports the problem itself
        }
    }

    /// <summary>
    /// Options for <c>rhwp export-pdf</c>: installed generic fallback families (some Linux installations do not have
    /// rhwp's Korean defaults, which otherwise leaves every missing-font run blank), and Cambria Math for equations
    /// when none of the fonts rhwp draws their symbols with is installed.
    /// </summary>
    public static IReadOnlyList<string> PdfOptions(Func<string, InstalledFont?>? lookup = null)
    {
        lookup ??= RhwpFonts.Find;
        var options = new List<string>();
        AddFallback("--fallback-serif", ["Times New Roman", "Noto Serif", "DejaVu Serif", "Georgia", "Liberation Serif"]);
        AddFallback("--fallback-sans", ["Segoe UI", "Tahoma", "Arial", "Noto Sans", "DejaVu Sans", "Helvetica", "Liberation Sans"]);
        AddFallback("--fallback-mono", ["Cascadia Mono", "Consolas", "SF Mono", "Menlo", "Noto Sans Mono", "DejaVu Sans Mono", "Liberation Mono"]);

        var hasSymbols = new[] { "Latin Modern Math", "STIX Two Math", "Arial Unicode MS" }.Any(f => lookup(f) is { Usable: true });
        if (!hasSymbols && lookup("Cambria Math") is { Usable: true })
        {
            options.Add("--equation-font");
            options.Add("Cambria Math");
        }
        return options;

        void AddFallback(string option, IReadOnlyList<string> families)
        {
            if (families.FirstOrDefault(f => lookup(f) is { Usable: true }) is not { } family)
                return;
            options.Add(option);
            options.Add(family);
        }
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace); // spaces in hp:t are text
    }

    private static bool ApplyFonts(XDocument header, string text, Func<string, InstalledFont?> lookup)
    {
        var fonts = header.Descendants()
            .Where(e => e.Name.LocalName == "font" && e.Attribute("face") is not null && e.Parent?.Name.LocalName == "fontface")
            .Select(e => (Element: e, Slot: (string?)e.Parent!.Attribute("lang") ?? "", Face: (string)e.Attribute("face")!))
            .ToList();
        var plan = RhwpFonts.Plan(fonts.Select(f => (f.Slot, f.Face)).ToList(), text, lookup);
        foreach (var font in fonts)
            if (plan.TryGetValue((font.Slot, font.Face), out var replacement))
                font.Element.SetAttributeValue("face", replacement);
        return plan.Count > 0;
    }

    /// <summary>
    /// Writes the laid-out size (hp:curSz) of objects sized relative to the column, paragraph or page as their absolute
    /// size. True when something changed.
    /// </summary>
    internal static bool FixShapeSizes(XDocument section)
    {
        var changed = false;
        foreach (var shape in section.Descendants().Where(e => SizedObjects.Contains(e.Name.LocalName)))
        {
            var size = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "sz");
            var current = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "curSz");
            if (size is null || current is null)
                continue;
            var relative = (string?)size.Attribute("widthRelTo") is { } w && w != "ABSOLUTE"
                           || (string?)size.Attribute("heightRelTo") is { } h && h != "ABSOLUTE";
            if (!relative || !int.TryParse((string?)current.Attribute("width"), out var width) || width <= 0
                || !int.TryParse((string?)current.Attribute("height"), out var height) || height <= 0)
                continue;
            size.SetAttributeValue("width", width);
            size.SetAttributeValue("height", height);
            size.SetAttributeValue("widthRelTo", "ABSOLUTE");
            size.SetAttributeValue("heightRelTo", "ABSOLUTE");
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// In table cells: moves the stored line of a paragraph that holds nothing but one floating table (vertRelTo="PARA",
    /// top aligned, text above and below) from under the table up to the paragraph's top. Only when the line is where
    /// 한글 puts it, below the table. True when something changed.
    /// </summary>
    internal static bool AnchorTablesInCells(XDocument section)
    {
        var changed = false;
        foreach (var paragraph in section.Descendants().Where(e => e.Name.LocalName == "p" && e.Parent is { Name.LocalName: "subList", Parent.Name.LocalName: "tc" }))
        {
            var content = paragraph.Elements().Where(e => e.Name.LocalName == "run").SelectMany(r => r.Elements()).ToList();
            if (content.Where(e => e.Name.LocalName != "t").ToList() is not [{ Name.LocalName: "tbl" } table]
                || content.Any(e => e.Name.LocalName == "t" && !string.IsNullOrWhiteSpace(e.Value))
                || Child(paragraph, "linesegarray")?.Elements().ToList() is not [{ } line]
                || Child(table, "pos") is not { } pos || Child(table, "sz") is not { } size
                || (string?)table.Attribute("textWrap") != "TOP_AND_BOTTOM" || (string?)pos.Attribute("treatAsChar") != "0"
                || (string?)pos.Attribute("vertRelTo") != "PARA" || (string?)pos.Attribute("vertAlign") is { } align && align != "TOP"
                || Number(line, "vertpos") is not { } vertpos || Number(pos, "vertOffset") is not { } offset || Number(size, "height") is not { } height)
                continue;

            var margin = Child(table, "outMargin");
            var top = vertpos - offset - height - (Number(margin, "top") ?? 0) - (Number(margin, "bottom") ?? 0);
            // The previous paragraph's last line ends above the anchor paragraph: otherwise the line was not under the table.
            var previous = paragraph.ElementsBeforeSelf().LastOrDefault(e => e.Name.LocalName == "p");
            var above = Child(previous, "linesegarray")?.Elements().LastOrDefault() is { } last
                ? (Number(last, "vertpos") ?? 0) + (Number(last, "vertsize") ?? 0)
                : 0;
            if (top < above || top >= vertpos)
                continue;
            line.SetAttributeValue("vertpos", top);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Paragraphs without stored lines (written by Filee) that hold equations: room above and below for what rhwp
    /// draws outside the line. rhwp makes a line as tall as its tallest equation, puts the line's baseline at 85 % of
    /// that height and the equation's own baseline on it, so an equation whose baseline is higher (a fraction, a matrix,
    /// a sum with limits) reaches below the line into the next one. The paragraph gets that much more space after it
    /// (and before it, for an equation reaching above). True when something changed.
    /// </summary>
    internal static bool SpaceEquationLines(XDocument section, XDocument header)
    {
        const int MinimumOverhang = 100;
        var paraProperties = header.Descendants().FirstOrDefault(e => e.Name.LocalName == "paraProperties");
        if (paraProperties is null)
            return false;
        var shapes = paraProperties.Elements().Where(e => e.Name.LocalName == "paraPr").ToDictionary(e => (string?)e.Attribute("id") ?? "");
        var nextId = shapes.Keys.Select(k => int.TryParse(k, out var id) ? id : 0).DefaultIfEmpty(-1).Max() + 1;
        var spaced = new Dictionary<(string Id, int Before, int After), string>();
        var changed = false;

        foreach (var paragraph in section.Descendants().Where(e => e.Name.LocalName == "p" && Child(e, "linesegarray") is null))
        {
            var boxes = paragraph.Elements().Where(e => e.Name.LocalName == "run").Elements()
                .Where(e => e.Name.LocalName == "equation" && (string?)Child(e, "pos")?.Attribute("treatAsChar") == "1")
                .Select(e => (Height: Number(Child(e, "sz"), "height") ?? 0, Baseline: Number(e, "baseLine") ?? 85))
                .Where(b => b.Height > 0)
                .ToList();
            if (boxes.Count == 0)
                continue;
            var line = boxes.Max(b => b.Height);
            var lineBaseline = line * 0.85;
            var before = (int)Math.Ceiling(boxes.Max(b => Math.Max(0, b.Height * b.Baseline / 100.0 - lineBaseline)));
            var after = (int)Math.Ceiling(boxes.Max(b => Math.Max(0, lineBaseline - b.Height * b.Baseline / 100.0 + b.Height - line)));
            // Less than a point (a subscript in running text) stays in the line spacing.
            before = before < MinimumOverhang ? 0 : before;
            after = after < MinimumOverhang ? 0 : after;
            var id = (string?)paragraph.Attribute("paraPrIDRef") ?? "";
            if ((before == 0 && after == 0) || !shapes.TryGetValue(id, out var shape))
                continue;

            if (!spaced.TryGetValue((id, before, after), out var newId))
            {
                var copy = new XElement(shape);
                newId = (nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                copy.SetAttributeValue("id", newId);
                foreach (var margin in copy.Descendants().Where(e => e.Name.LocalName is "prev" or "next"))
                {
                    var extra = margin.Name.LocalName == "prev" ? before : after;
                    // 한글 writes the margins doubled in hp:default (hp:case holds the real values).
                    if (margin.Ancestors().Any(a => a.Name.LocalName == "default"))
                        extra *= 2;
                    margin.SetAttributeValue("value", (Number(margin, "value") ?? 0) + extra);
                }
                paraProperties.Add(copy);
                spaced[(id, before, after)] = newId;
            }
            paragraph.SetAttributeValue("paraPrIDRef", newId);
            changed = true;
        }
        if (changed)
            paraProperties.SetAttributeValue("itemCnt", paraProperties.Elements().Count(e => e.Name.LocalName == "paraPr"));
        return changed;
    }

    private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static int? Number(XElement? element, string attribute) =>
        int.TryParse((string?)element?.Attribute(attribute), out var value) ? value : null;

    private static bool NormaliseEquations(XDocument section)
    {
        var changed = false;
        foreach (var script in section.Descendants().Where(e => e.Name.LocalName == "script" && e.Parent?.Name.LocalName == "equation"))
        {
            var normalised = NormaliseEquation(script.Value);
            if (normalised != script.Value)
            {
                script.Value = normalised;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// The script as rhwp reads it: commands glued to the next identifier spaced ("f CIRCf" → "f CIRC f"), and !=, +-
    /// written as NEQ, PLUSMINUS. Quoted text stays as it is.
    /// </summary>
    internal static string NormaliseEquation(string script)
    {
        var parts = script.Split('"');
        for (var i = 0; i < parts.Length; i += 2) // odd parts are inside quotes
        {
            var part = Identifier().Replace(parts[i], m => Split(m.Value));
            foreach (var (text, command) in Operators)
                part = part.Replace(text, command, StringComparison.Ordinal);
            parts[i] = part;
        }
        return string.Join('"', parts);
    }

    private static string Split(string identifier)
    {
        var words = new List<string>();
        while (true)
        {
            if (Whole.Contains(identifier))
            {
                words.Add(identifier);
                break;
            }
            var command = UpperCommands.Where(c => identifier.StartsWith(c, StringComparison.Ordinal)).MaxBy(c => c.Length);
            if (command is null)
            {
                var lower = LowerCommands.Where(c => identifier.StartsWith(c, StringComparison.Ordinal)).MaxBy(c => c.Length);
                var rest = lower is null ? "" : identifier[lower.Length..];
                command = rest.Length > 0 && (rest.Length <= 2 || rest.All(char.IsAsciiDigit)) ? lower : null;
            }
            if (command is null || command.Length == identifier.Length || Whole.Any(w => identifier.StartsWith(w, StringComparison.Ordinal) && w.Length > command.Length))
            {
                words.Add(identifier);
                break;
            }
            words.Add(command);
            identifier = identifier[command.Length..];
        }
        return string.Join(' ', words);
    }

    [GeneratedRegex("[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex Identifier();
}
