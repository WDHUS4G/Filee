// Fonts for rhwp, chosen from the fonts installed on the computer (RhwpInput writes them into a copy of the HWPX).
// Nothing is downloaded or shipped.
//
// - Language: documents Filee writes (Markdown, text, HTML → HWPX) name Korean fonts in every slot. In a Chinese or
//   Japanese document those drew Korean-style Hanja and, for characters they lack, a different fallback font per
//   character (issue #64). Without Hangul in the text, Korean fonts are swapped for an installed Chinese (Simplified
//   or Traditional) or Japanese font of the same style.
// - Coverage: rhwp draws a character with the font of its slot when that font has it, else with a fallback of its own
//   (Arabic came out in a calligraphic font). In text without Hangul, Han or kana, a slot's font is swapped for an
//   installed one of the same style that has every character of the text the old one had, and more of the rest:
//   Russian, Greek, Thai or Arabic text gets one consistent font.
// - CFF2: variable fonts with CFF2 outlines (Source Han Serif VF, "思源宋体 VF") fail rhwp's font subsetter and the
//   page came out blank (issue #61); they are always swapped.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Filee.Engines.Hwp;

/// <summary>An installed font: whether rhwp can embed it, which characters it has and how wide they are.</summary>
/// <param name="Advance">Advance width of a character in em, null when the font lacks it (or is not measured).</param>
internal sealed record InstalledFont(bool Usable, Func<int, bool> Has, Func<int, double?>? Advance = null);

internal static class RhwpFonts
{
    private static readonly string[] Styles =
        ["Thin", "ExtraLight", "Light", "Normal", "Regular", "Medium", "SemiBold", "Bold", "Heavy", "Black", "ExtraBold"];

    /// <summary>Installed fonts in order of preference, by script, serif first then sans.</summary>
    private static readonly Dictionary<string, (string[] Serif, string[] Sans)> Candidates = new()
    {
        ["zh-Hans"] = (["SimSun", "NSimSun", "Noto Serif SC", "Songti SC", "Noto Serif CJK SC"],
            ["Microsoft YaHei", "DengXian", "Noto Sans SC", "PingFang SC", "Noto Sans CJK SC", "SimHei"]),
        ["zh-Hant"] = (["PMingLiU", "MingLiU", "Noto Serif TC", "Songti TC", "Noto Serif CJK TC"],
            ["Microsoft JhengHei", "Noto Sans TC", "PingFang TC", "Noto Sans CJK TC"]),
        ["ja"] = (["Yu Mincho", "MS Mincho", "Noto Serif JP", "Hiragino Mincho ProN", "Noto Serif CJK JP"],
            ["Yu Gothic", "Meiryo", "MS Gothic", "Noto Sans JP", "Hiragino Sans", "Noto Sans CJK JP"]),
        ["ko"] = (["Batang", "HCR Batang", "Noto Serif KR", "AppleMyungjo", "Noto Serif CJK KR"],
            ["Malgun Gothic", "Noto Sans KR", "Apple SD Gothic Neo", "NanumGothic", "Noto Sans CJK KR"]),
        // Wide coverage first (Tahoma and Times New Roman also have Greek, Cyrillic, Arabic, Hebrew; Tahoma Thai).
        [""] = (["Times New Roman", "Noto Serif", "DejaVu Serif", "Georgia", "Liberation Serif"],
            ["Segoe UI", "Tahoma", "Arial", "Noto Sans", "DejaVu Sans", "Helvetica", "Liberation Sans"]),
    };

    /// <summary>Scripts whose fonts rhwp falls back to on its own and the computer may have better ones for.</summary>
    private static readonly string[] WideFonts = ["Leelawadee UI", "Nirmala UI", "Microsoft Sans Serif", "Arial Unicode MS"];

    /// <summary>
    /// Simplified and Traditional forms of common characters: which form a Chinese text uses. Forms Japanese writes
    /// the same way (国, 学, 会, 点, …) are left out.
    /// </summary>
    private const string SimplifiedOnly = "这们说为时个对发经过还进动东车长门间问见现实从开关书无让应该话样";
    private const string TraditionalOnly = "這們說為時個對發經過還進動東車長門間問見現實從開關書無讓應該話樣";

    private static readonly ConcurrentDictionary<string, InstalledFont?> Fonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets the fonts looked up so far (<see cref="Infrastructure.EngineCaches.Release"/>).</summary>
    internal static void ReleaseCache() => Fonts.Clear();

    /// <summary>Rechecks one family after the user may have installed it during this session.</summary>
    internal static void Forget(string face) => Fonts.TryRemove(face, out _);

    /// <summary>Replacements by (slot, face): the language, coverage and CFF2 rules above.</summary>
    internal static Dictionary<(string Slot, string Face), string> Plan(
        IReadOnlyList<(string Slot, string Face)> slots, string text, Func<string, InstalledFont?> lookup)
    {
        var characters = Characters(text);
        var language = Language(text);
        var result = new Dictionary<(string, string), string>();
        foreach (var (slot, face) in slots.Distinct())
        {
            if (face.Length == 0)
                continue;
            var serif = IsSerif(face);
            var chosen = face;
            var current = lookup(face);

            // Korean fonts in a Chinese or Japanese text; a font rhwp cannot embed.
            if (language is not ("ko" or "") && Script(face) == "ko" || current is { Usable: false })
            {
                var script = current is { Usable: false } && Script(face) is { Length: > 0 } own ? own : language;
                if (Preferred(script, serif).FirstOrDefault(name => lookup(name) is { Usable: true }) is { } replacement)
                    chosen = replacement;
            }

            // Text without Hangul, Han or kana (Russian, Greek, Thai, Arabic, …): a font of the same style with every
            // character the chosen one has, and more of the others. In Korean, Chinese and Japanese text the language
            // font stays, and rhwp's own fallback draws the odd foreign word as before.
            var mine = language != "" || slot is "HANJA" or "JAPANESE"
                ? []
                : characters.Where(c => !IsHan(c) && !IsKana(c) && (slot == "HANGUL" || !IsHangul(c))).ToList();
            if (mine.Count > 0 && lookup(chosen) is { Usable: true } font)
            {
                var covered = mine.Where(c => font.Has(c)).ToList();
                if (covered.Count < mine.Count)
                {
                    var best = chosen;
                    var bestCount = covered.Count;
                    var sameStyle = (serif ? Styled(language).Serif.Concat(Styled("").Serif) : Styled(language).Sans.Concat(Styled("").Sans).Concat(WideFonts))
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in sameStyle)
                    {
                        if (lookup(name) is not { Usable: true } other || !covered.All(other.Has))
                            continue;
                        var count = mine.Count(other.Has);
                        if (count == mine.Count)
                        {
                            (best, bestCount) = (name, count); // the first one in order that has everything
                            break;
                        }
                        if (count > bestCount)
                            (best, bestCount) = (name, count);
                    }
                    chosen = best;
                }
            }

            if (!string.Equals(chosen, face, StringComparison.Ordinal))
                result[(slot, face)] = chosen;
        }
        return result;
    }

    private static IEnumerable<string> Preferred(string script, bool serif)
    {
        var (serifs, sans) = Styled(script);
        return serif ? serifs.Concat(sans) : sans.Concat(serifs);
    }

    private static (string[] Serif, string[] Sans) Styled(string script) =>
        Candidates.TryGetValue(script, out var c) ? c : Candidates[""];

    private static bool IsHangul(int c) => c is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;

    private static bool IsKana(int c) => c is >= 0x3040 and <= 0x30FF;

    private static bool IsHan(int c) => c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2FFFF;

    /// <summary>
    /// The language the Han characters of a text are in: Korean with Hangul, Japanese with kana, else Simplified or
    /// Traditional Chinese by common characters; "" without Han characters.
    /// </summary>
    internal static string Language(string text)
    {
        int hangul = 0, kana = 0, han = 0, simplified = 0, traditional = 0;
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
                hangul++;
            else if (c is >= '぀' and <= 'ヿ')
                kana++;
            else if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                han++;
                if (SimplifiedOnly.Contains(c))
                    simplified++;
                else if (TraditionalOnly.Contains(c))
                    traditional++;
            }
        }
        if (hangul > 0)
            return "ko";
        // Japanese prose is mostly kana; a Chinese text quoting a Japanese title is not.
        if (kana > 0 && (simplified + traditional == 0 || kana * 3 >= han))
            return "ja";
        if (han > 0)
            return traditional > simplified ? "zh-Hant" : "zh-Hans";
        return "";
    }

    /// <summary>Distinct characters of the text that need a glyph (no spaces or controls), at most 4000.</summary>
    private static List<int> Characters(string text)
    {
        var set = new HashSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (set.Count >= 4000)
                break;
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.SpaceSeparator or UnicodeCategory.Control
                or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned))
                set.Add(rune.Value);
        }
        return set.ToList();
    }

    /// <summary>
    /// An installed font by name, or null. "思源宋体 VF Medium" names a style of "思源宋体 VF", so trailing style words
    /// are tried off too.
    /// </summary>
    internal static InstalledFont? Find(string face) => Fonts.GetOrAdd(face, static name =>
    {
        foreach (var candidate in Names(name))
        {
            var typeface = SKFontManager.Default.MatchFamily(candidate);
            // Windows matches localized names ("思源宋体 VF") and returns nothing for unknown ones; fontconfig always
            // returns some font, so elsewhere only a font with the requested name counts.
            if (typeface is null)
                continue;
            if (OperatingSystem.IsWindows() || string.Equals(typeface.FamilyName, candidate, StringComparison.OrdinalIgnoreCase))
                return new InstalledFont(IsUsable(typeface), codePoint => typeface.GetGlyph(codePoint) != 0, codePoint => Advance(typeface, codePoint));
            // Not disposed: SkiaSharp hands out one wrapper per native typeface, so this may be the very object the
            // CAD font cache, Svg.Skia or the UI's font manager still draws with. Disposing it would leave them a
            // null handle (a native crash on their next call); the finalizer releases it once nobody uses it.
        }
        return null;
    });

    private static double? Advance(SKTypeface typeface, int codePoint)
    {
        var glyph = typeface.GetGlyph(codePoint);
        if (glyph == 0)
            return null;
        using var font = new SKFont(typeface, 1000);
        return font.GetGlyphWidths([glyph])[0] / 1000.0;
    }

    /// <summary>False when the font has CFF2 outlines (variable OpenType fonts made from PostScript outlines).</summary>
    internal static bool IsUsable(SKTypeface typeface) => !typeface.GetTableTags().Contains(Tag("CFF2"));

    private static IEnumerable<string> Names(string face)
    {
        yield return face;
        var words = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Styles.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
            yield return string.Join(' ', words);
        }
    }

    /// <summary>The script a font name points to: "思源宋体" and "SC" are Simplified Chinese, "명조" Korean, ….</summary>
    internal static string Script(string face)
    {
        bool Has(params string[] parts) => parts.Any(p => face.Contains(p, StringComparison.OrdinalIgnoreCase));
        bool Word(params string[] words) => face.Split(' ', '-', '_').Any(w => words.Contains(w, StringComparer.OrdinalIgnoreCase));
        if (Word("SC", "CN", "GB") || Has("简", "宋体", "黑体", "仿宋", "楷体"))
            return "zh-Hans";
        if (Word("TC", "HK", "TW") || Has("繁", "明體", "黑體", "宋體"))
            return "zh-Hant";
        if (Word("JP", "J") || Has("明朝", "ゴシック", "游"))
            return "ja";
        if (Word("KR", "K") || face.Any(c => c is >= '가' and <= '힣')
            || Candidates["ko"].Serif.Concat(Candidates["ko"].Sans).Contains(face, StringComparer.OrdinalIgnoreCase))
            return "ko";
        if (face.Any(c => c is >= '一' and <= '鿿'))
            return "zh-Hans";
        return "";
    }

    /// <summary>Serif (Song/Ming/Mincho/Batang) rather than sans (Hei/Gothic/Dotum).</summary>
    internal static bool IsSerif(string face) =>
        new[] { "Serif", "Song", "Ming", "Mincho", "Myeongjo", "Myungjo", "Batang", "Times", "宋", "明", "바탕", "명조", "仿宋", "楷" }
            .Any(p => face.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static uint Tag(string tag) => (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
}
