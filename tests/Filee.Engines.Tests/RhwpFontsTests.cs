// Fonts for rhwp from the fonts on the computer: language (#64), coverage, and CFF2 variable fonts (#61).

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Engines.Hwp;

namespace Filee.Engines.Tests;

public class RhwpFontsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,";
    private const string Cyrillic = "АБВГДЕЖЗИКЛМНОПРСТУФХЦЧШЩЭЮЯабвгдежзиклмнопрстуфхцчшщэюя";
    private const string Hangul = "한글문서대한민국섞인장입니다";
    private const string Han = "改造我们的学习这是一个测试文档说话国家和会时间漢字";

    /// <summary>A fake computer: font name → (embeddable, characters it has).</summary>
    private static readonly Dictionary<string, (bool Usable, string Chars)> Computer = new()
    {
        ["맑은 고딕"] = (true, Latin + Hangul + "漢字國"),
        ["함초롬바탕"] = (true, Latin + Hangul + "漢字國"),
        ["Microsoft YaHei"] = (true, Latin + Cyrillic + Han),
        ["SimSun"] = (true, Latin + Han),
        ["Yu Gothic"] = (true, Latin + Han + "日本語のテキスト"),
        ["Segoe UI"] = (true, Latin + Cyrillic),
        ["Times New Roman"] = (true, Latin + Cyrillic),
        ["思源宋体 VF Medium"] = (false, Latin + Han),
    };

    private static InstalledFont? Lookup(string name) =>
        Computer.TryGetValue(name, out var font) ? new InstalledFont(font.Usable, c => font.Chars.Contains((char)c)) : null;

    private static Dictionary<(string Slot, string Face), string> Plan(string text, params (string Slot, string Face)[] slots) =>
        RhwpFonts.Plan(slots, text, Lookup);

    [Fact]
    public void Chinese_text_gets_a_chinese_font_instead_of_the_korean_defaults()
    {
        // Issue #64: Markdown → PDF in Chinese mixed Malgun Gothic and a fallback font character by character.
        var plan = Plan("改造我们的学习 这是一个测试文档。", ("HANJA", "맑은 고딕"), ("LATIN", "맑은 고딕"), ("HANJA", "함초롬바탕"));

        Assert.Equal("Microsoft YaHei", plan[("HANJA", "맑은 고딕")]); // sans for 고딕
        Assert.Equal("Microsoft YaHei", plan[("LATIN", "맑은 고딕")]);
        Assert.Equal("SimSun", plan[("HANJA", "함초롬바탕")]);          // serif for 바탕
    }

    [Fact]
    public void Korean_text_keeps_its_fonts()
    {
        Assert.Empty(Plan("대한민국 漢字 섞인 문장입니다. English 123.", ("HANGUL", "맑은 고딕"), ("HANJA", "맑은 고딕"), ("LATIN", "맑은 고딕")));
    }

    [Fact]
    public void A_font_without_the_text_s_characters_gets_one_that_has_them_all()
    {
        // Russian in a document Filee wrote: Malgun Gothic has no Cyrillic, Segoe UI has it and all the Latin.
        var plan = Plan("Привет мир. Hello.", ("HANGUL", "맑은 고딕"), ("LATIN", "맑은 고딕"));

        Assert.Equal("Segoe UI", plan[("HANGUL", "맑은 고딕")]);
        // With Hangul in the text no font without Hangul replaces it, whatever else it has.
        Assert.Empty(Plan("한글 Привет", ("HANGUL", "맑은 고딕")));
    }

    [Fact]
    public void A_cff2_variable_font_is_always_replaced()
    {
        // Issue #61: 思源宋体 VF left the pages blank.
        Assert.Equal("SimSun", Plan("这是一个测试文档", ("HANJA", "思源宋体 VF Medium"))[("HANJA", "思源宋体 VF Medium")]);
    }

    [Fact]
    public void Fonts_that_are_not_installed_are_left_to_rhwp()
    {
        Assert.Empty(Plan("这是一个测试文档", ("HANJA", "Not Installed")));
    }

    [Theory]
    [InlineData("这是我们的国家", "zh-Hans")]
    [InlineData("這是我們的國家", "zh-Hant")]
    [InlineData("日本語のテキスト", "ja")]
    [InlineData("这是一个测试文档，我们说的话和时间。书名：日本語のテキスト", "zh-Hans")] // a Japanese title in Chinese text
    [InlineData("한국어 漢字", "ko")]
    [InlineData("Привет", "")]
    public void The_language_of_han_characters_comes_from_the_text(string text, string language)
    {
        Assert.Equal(language, RhwpFonts.Language(text));
    }

    [Theory]
    [InlineData("思源宋体 VF Medium", "zh-Hans", true)]
    [InlineData("Source Han Sans SC VF", "zh-Hans", false)]
    [InlineData("Noto Serif TC", "zh-Hant", true)]
    [InlineData("Source Han Serif K VF", "ko", true)]
    [InlineData("함초롬돋움", "ko", false)]
    [InlineData("Malgun Gothic", "ko", false)]
    [InlineData("Source Han Sans JP", "ja", false)]
    [InlineData("Inter", "", false)]
    public void Script_and_style_come_from_the_font_name(string face, string script, bool serif)
    {
        Assert.Equal(script, RhwpFonts.Script(face));
        Assert.Equal(serif, RhwpFonts.IsSerif(face));
    }

    [Fact]
    public async Task A_copy_of_the_hwpx_names_the_replacement_and_still_converts()
    {
        var dir = fx.NewFolder();
        var hwpx = await fx.MakeHwpxAsync(dir); // Korean text with a few Han characters, Filee's default fonts
        var faces = Faces(hwpx);
        var broken = faces.First();

        var copy = RhwpInput.Prepare(hwpx, dir, face => face == broken
            ? new InstalledFont(false, _ => true)
            : face == "Malgun Gothic" ? new InstalledFont(true, _ => true) : null);

        Assert.NotNull(copy);
        Assert.DoesNotContain(broken, Faces(copy));
        Assert.Contains("Malgun Gothic", Faces(copy));
        Assert.Equal(faces, Faces(hwpx)); // the original is untouched
        using (var zip = ZipFile.OpenRead(copy))
            Assert.Equal("mimetype", zip.Entries[0].FullName); // still a valid HWPX package
        Assert.Null(RhwpInput.Prepare(hwpx, dir, _ => new InstalledFont(true, _ => true))); // nothing to replace: no copy
    }

    private static List<string> Faces(string hwpx)
    {
        using var zip = ZipFile.OpenRead(hwpx);
        using var stream = zip.GetEntry("Contents/header.xml")!.Open();
        return XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "font")
            .Select(e => (string?)e.Attribute("face")).OfType<string>().Distinct().ToList();
    }
}
