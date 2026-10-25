// Symbol fonts (Wingdings, Wingdings 2, Wingdings 3, Webdings, Symbol) draw their own pictures in place of letters:
// a check box picked in Word's Insert → Symbol is stored as a code of the font (w:sym w:font="Wingdings 2"
// w:char="F052"), in the private use area F020–F0FF, and text typed in such a font as the plain byte ("R"). Another
// font shows nothing or a wrong sign for those codes, so the readers write the Unicode character the picture stands
// for (#350): check boxes, check marks and crosses, circles, squares, diamonds and stars (bullets), arrows, pointing
// hands, phone and mail signs, circled numbers, and Symbol's Greek letters and math signs. Codes the tables do not
// know keep the readers' old handling.
//
// Every character here is in the Basic Multilingual Plane and in Segoe UI Symbol, which every Windows has: rhwp
// draws a character its font lacks with the first installed font that has it, so these always come out.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class SymbolFonts
{
    /// <summary>Unicode text by font (name without spaces) and code (20–FF); a space in a row skips a code.</summary>
    private static readonly Dictionary<string, Dictionary<int, string>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wingdings"] = Table(
            (0x21, "✏✂✁"),
            (0x28, "☎✆✉✉"),
            (0x36, "⌛⌨"),
            (0x3E, "✇✍ ✌"),
            (0x45, "☜☞☝☟ ☺ ☹ ☠⚐ ✈☼ ❄ ✞ ✠✡☪☯ ☸♈♉♊♋♌♍♎♏♐♑♒♓"),
            (0x6C, "●❍■□□❑❒⬧⧫◆❖⬥⌧ ⌘"),
            (0x7D, "❝❞"),
            (0x80, "⓪①②③④⑤⑥⑦⑧⑨⑩⓿❶❷❸❹❺❻❼❽❾❿"),
            (0x9E, "·•▪○○ ◉◎ ▪☐ ✦★✶✴✹✵ ⌖⟡⌑ ✪✰"),
            (0xD5, "⌫⌦ ➢"),
            (0xDC, "➲"),
            (0xDF, "←→↑↓↖↗↙↘⬅➡⬆⬇⬉⬈⬋⬊⇦⇨⇧⇩⬄⇳⬁⬀⬃⬂"),
            (0xFB, "✗✓☒☑")),
        ["Wingdings2"] = Table(
            (0x25, "✄✂☏"),
            (0x3E, "☜☞☚☛☜☞☚☛☝☟☝☟☝☟☝☟"),
            (0x4F, "✗✓☒☑☒☒⊗⊗⊘⊘&&&&‽‽‽‽"),
            (0x69, "⓪①②③④⑤⑥⑦⑧⑨⑩⓿❶❷❸❹❺❻❼❽❾❿"),
            (0x80, "☉○☽☾ ✝✝"),
            (0x95, "·•●●○○  ⊙◉▪▪■■☐□□□▣▣▣ ⬩⬩⬩◆◇◈◈◈ ⬪⬪⬪⧫◊ ◖◗◓◒"),
            (0xCD, "✕✕✕✖✖✖✖"),
            (0xE7, "✦✦★★✶✶"),
            (0xF2, "✧☆"),
            (0xF6, "✦ ※⁂")),
        ["Wingdings3"] = Table(
            (0x21, "←→↑↓↖↗↙↘⇤⇥⤒⤓⇱⇲⇞⇟↔↕⇠⇢⇡⇣↯↵↳↰↱"),
            (0x44, "⇆⇅  ⇇⇉⇈⇊"),
            (0x50, "↻↺⎋  ⌥␣  ⇪⇦⇨"),
            (0x66, "←→↑↓↖↗↙↘↔↕▲▼△▽◀▶◁▷◣◢◤◥◂▸▴"),
            (0x80, "▾▴▾◂▸⮜⮞⮝⮟"),
            (0x89, "←→↑↓←→↑↓←→↑↓⬅➡⬆⬇←→↑↓←→↑↓⬅➡⬆⬇←→←→⬅➡"),
            (0xB1, "⬅➡"),
            (0xC5, "⬅➡⬆⬇"),
            (0xD1, "←→↑↓↖↗↙↘←→↑↓↖↗↙↘⬅➡⬆⬇⬉⬈⬋⬊")),
        ["Webdings"] = Table(
            (0x33, "◀▶▲▼⏪⏩⏮⏭⏸■●"),
            (0x59, "♥"),
            (0x61, "✔ ☐   ■ ⓘ    ●"),
            (0x72, "✖"),
            (0xC9, "☎"),
            (0xEA, "△"),
            (0xF1, "✈")),
        ["Symbol"] = Table(
            (0x21, "!∀#∃%&∋()∗+,−./0123456789:;<=>?≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_ αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼"),
            (0xA0, "€ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…  ↵ℵℑℜ℘⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇®©™∏√⋅¬∧∨⇔⇐⇑⇒⇓◊⟨®©™∑"),
            (0xF1, "⟩∫")),
    };

    private static Dictionary<int, string> Table(params (int First, string Characters)[] rows)
    {
        var table = new Dictionary<int, string>();
        foreach (var (first, characters) in rows)
        {
            for (var i = 0; i < characters.Length; i++)
            {
                if (characters[i] != ' ')
                    table[first + i] = characters[i].ToString();
            }
        }
        return table;
    }

    private static Dictionary<int, string>? Of(string? font) =>
        font is not null && Tables.TryGetValue(font.Replace(" ", "", StringComparison.Ordinal), out var table) ? table : null;

    /// <summary>Wingdings, Wingdings 2, Wingdings 3, Webdings or Symbol (any case, with or without spaces).</summary>
    public static bool IsSymbolFont(string? font) => Of(font) is not null;

    /// <summary>
    /// The Unicode character code <paramref name="code"/> of a symbol font stands for (F020–F0FF or 20–FF), or null
    /// for another font or a code the tables do not know.
    /// </summary>
    public static string? Map(string? font, int code)
    {
        if (Of(font) is not { } table)
            return null;
        if (code is >= 0xF000 and <= 0xF0FF)
            code -= 0xF000;
        return table.GetValueOrDefault(code);
    }

    /// <summary>A whole text of a symbol font in Unicode, or null unless every character is known.</summary>
    public static string? Map(string? font, string text)
    {
        if (text.Length == 0 || Of(font) is null)
            return null;
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (Map(font, c) is not { } mapped)
                return null;
            result.Append(mapped);
        }
        return result.ToString();
    }

    /// <summary>The format without symbol fonts: the Unicode characters are drawn with the text font.</summary>
    public static HCharFormat WithoutSymbolFont(HCharFormat format)
    {
        var eastAsian = IsSymbolFont(format.EastAsianFont) ? null : format.EastAsianFont;
        return format with { Font = IsSymbolFont(format.Font) ? eastAsian : format.Font, EastAsianFont = eastAsian };
    }

    /// <summary>
    /// Text in <paramref name="format"/>: in a symbol font, the characters the tables know become their Unicode
    /// characters without the symbol font, and the others stay as they are.
    /// </summary>
    public static IEnumerable<HText> Runs(string text, HCharFormat format)
    {
        if (!IsSymbolFont(format.Font))
        {
            yield return new HText(text, format);
            yield break;
        }
        var plain = WithoutSymbolFont(format);
        var run = new StringBuilder();
        var mappedRun = false;
        foreach (var c in text)
        {
            var mapped = Map(format.Font, c);
            if (run.Length > 0 && mappedRun != mapped is not null)
            {
                yield return new HText(run.ToString(), mappedRun ? plain : format);
                run.Clear();
            }
            mappedRun = mapped is not null;
            run.Append(mapped ?? c.ToString());
        }
        if (run.Length > 0)
            yield return new HText(run.ToString(), mappedRun ? plain : format);
    }
}
