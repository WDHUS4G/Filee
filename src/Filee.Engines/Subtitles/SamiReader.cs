// SAMI (.smi), Microsoft's HTML-like subtitle format and the usual one for Korean subtitles. Files are hand-made and
// rarely valid HTML: <SYNC> and <P> are seldom closed, attribute values are unquoted, and one file may hold several
// languages as classes (KRCC, ENCC, …). The text is read with regular expressions, not an HTML parser.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Filee.Engines.Subtitles;

/// <summary>Reads SAMI subtitles.</summary>
internal static partial class SamiReader
{
    /// <summary>
    /// Reads every <c>&lt;SYNC Start=ms&gt;</c>: its text lasts until the next SYNC, and a SYNC with only
    /// <c>&amp;nbsp;</c> clears the screen. Of several languages, the one with the most lines is read (the first
    /// one on a tie).
    /// </summary>
    public static SubtitleDocument Read(string text)
    {
        var document = new SubtitleDocument();
        if (Title().Match(text) is { Success: true } title && System.Net.WebUtility.HtmlDecode(title.Groups["text"].Value).Trim() is { Length: > 0 } name)
            document.Title = name;

        // Body only: the <STYLE> block's comment would otherwise look like text.
        var body = BodyStart().Match(text) is { Success: true } start ? text[(start.Index + start.Length)..] : text;
        body = Comment().Replace(body, "");
        var syncs = Sync().Matches(body);
        List<(TimeSpan Time, string Class, string Text)> entries = [];
        for (var i = 0; i < syncs.Count; i++)
        {
            if (!int.TryParse(syncs[i].Groups["start"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                continue;
            var contentStart = syncs[i].Index + syncs[i].Length;
            var contentEnd = i + 1 < syncs.Count ? syncs[i + 1].Index : body.Length;
            var content = BodyEnd().Replace(body[contentStart..contentEnd], "");
            var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));

            var paragraphs = Paragraph().Matches(content);
            if (paragraphs.Count == 0)
            {
                entries.Add((time, "", Text(content)));
                continue;
            }
            for (var p = 0; p < paragraphs.Count; p++)
            {
                var end = p + 1 < paragraphs.Count ? paragraphs[p + 1].Index : content.Length;
                var paragraph = content[(paragraphs[p].Index + paragraphs[p].Length)..end];
                var language = ClassAttribute().Match(paragraphs[p].Groups["attributes"].Value) is { Success: true } c ? c.Groups["name"].Value.ToUpperInvariant() : "";
                entries.Add((time, language, Text(paragraph)));
            }
        }

        // The language with the most lines; lines without a class belong to every language.
        var chosen = entries.Where(e => e.Class.Length > 0 && SubtitleMarkup.PlainText(e.Text).Trim().Length > 0)
            .GroupBy(e => e.Class)
            .Select((g, order) => (g.Key, Count: g.Count(), Order: order))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Order)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";
        var lines = entries.Where(e => e.Class.Length == 0 || e.Class == chosen).OrderBy(e => e.Time).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var end = i + 1 < lines.Count ? lines[i + 1].Time : lines[i].Time + SubtitleDocument.DefaultDuration;
            // Two entries at the same time: the later one wins.
            if (end == lines[i].Time && i + 1 < lines.Count)
                continue;
            document.Add(lines[i].Time, end, lines[i].Text);
        }

        if (chosen.Length > 0 && LanguageOf(text, chosen) is { } lang)
            document.Language = lang;
        return document;
    }

    /// <summary>The paragraph's text as cue markup: HTML whitespace rules (source line breaks are spaces), <c>&lt;br&gt;</c> breaks.</summary>
    private static string Text(string html) =>
        SubtitleMarkup.FromHtml(Whitespace().Replace(LooseNbsp().Replace(html, "&nbsp;"), " "), decodeEntities: true);

    /// <summary>Hand-written files clear the screen with <c>&amp;nbsp</c> without the semicolon, or in capitals.</summary>
    [GeneratedRegex("&nbsp;?", RegexOptions.IgnoreCase)]
    private static partial Regex LooseNbsp();

    /// <summary>The <c>lang:</c> of a class in the style sheet: <c>.KRCC { Name: Korean; lang: ko-KR; }</c>.</summary>
    private static string? LanguageOf(string text, string className)
    {
        var rule = Regex.Match(text, @"\." + Regex.Escape(className) + @"\s*\{(?<rule>[^}]*)\}", RegexOptions.IgnoreCase);
        return rule.Success && Lang().Match(rule.Groups["rule"].Value) is { Success: true } lang ? lang.Groups["value"].Value : null;
    }

    [GeneratedRegex(@"<title[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"<body[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyStart();

    [GeneratedRegex(@"</body\s*>.*|</sami\s*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyEnd();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("""<sync\b[^>]*?start\s*=\s*["']?(?<start>-?\d+)[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex Sync();

    [GeneratedRegex(@"<p\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Paragraph();

    [GeneratedRegex("""class\s*=\s*["']?(?<name>[\w-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttribute();

    [GeneratedRegex(@"lang\s*:\s*(?<value>[A-Za-z]{2,3}(?:-[A-Za-z0-9]+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex Lang();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
