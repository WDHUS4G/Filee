// The common model every subtitle reader produces and every writer consumes: timed cues whose text uses a tiny
// markup (line breaks plus <i>, <b>, <u> and <font color="#RRGGBB">, SRT's own tags), so styling survives between
// formats that have it and is dropped cleanly by those that don't (LRC, MicroDVD).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Subtitles;

/// <summary>One subtitle shown from <paramref name="Start"/> to <paramref name="End"/>.</summary>
/// <param name="Text">Lines separated by <c>\n</c>, with the markup of <see cref="SubtitleMarkup"/>.</param>
internal sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A subtitle file: its cues in time order, and the title and language when the file says.</summary>
internal sealed class SubtitleDocument
{
    public List<SubtitleCue> Cues { get; } = [];

    public string? Title { get; set; }

    /// <summary>Language tag such as <c>ko-KR</c> or <c>en</c>, when the file names one.</summary>
    public string? Language { get; set; }

    /// <summary>How long a cue without an end time (LRC, the last SAMI line) stays on screen.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    /// <summary>Adds a cue with normalized markup; empty and zero-length cues are dropped.</summary>
    public void Add(TimeSpan start, TimeSpan end, string text)
    {
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        var normalized = SubtitleMarkup.Normalize(text);
        if (end <= start || SubtitleMarkup.PlainText(normalized).Trim().Length == 0)
            return;
        Cues.Add(new SubtitleCue(start, end, normalized));
    }

    /// <summary>Puts the cues in time order (stable) and drops exact duplicates (ASS shadow layers).</summary>
    public SubtitleDocument Finish()
    {
        var ordered = Cues.Distinct().OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        Cues.Clear();
        Cues.AddRange(ordered);
        return this;
    }

    /// <summary>
    /// The language of the text when the file doesn't name one: Korean, Japanese and Chinese by their scripts,
    /// English otherwise.
    /// </summary>
    public string GuessLanguage()
    {
        if (!string.IsNullOrWhiteSpace(Language))
            return Language;
        int hangul = 0, kana = 0, han = 0;
        foreach (var c in Cues.SelectMany(c => c.Text))
        {
            if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
                hangul++;
            else if (c is >= '぀' and <= 'ヿ')
                kana++;
            else if (c is >= '一' and <= '鿿')
                han++;
        }
        if (hangul > 0 && hangul >= kana && hangul >= han / 4)
            return "ko-KR";
        if (kana > 0)
            return "ja-JP";
        return han > 0 ? "zh-CN" : "en-US";
    }
}

/// <summary>Kinds of <see cref="MarkupToken"/>.</summary>
internal enum MarkupKind
{
    Text,
    LineBreak,
    Open,
    Close,
}

/// <summary>A piece of cue text: text, a line break, or the start / end of a style.</summary>
/// <param name="Value">The text, or the style: <c>i</c>, <c>b</c>, <c>u</c> or a colour <c>#RRGGBB</c> (closed as <c>font</c>).</param>
internal readonly record struct MarkupToken(MarkupKind Kind, string Value)
{
    public bool IsColor => Value.StartsWith('#');
}

/// <summary>The cue text markup: parsing, balancing, plain text and colours.</summary>
internal static partial class SubtitleMarkup
{
    /// <summary>Splits normalized cue text into tokens.</summary>
    public static List<MarkupToken> Tokens(string text)
    {
        List<MarkupToken> tokens = [];
        var position = 0;
        foreach (Match match in CanonicalTag().Matches(text))
        {
            AddText(tokens, text[position..match.Index]);
            if (match.Groups["color"].Success)
                tokens.Add(new MarkupToken(MarkupKind.Open, "#" + match.Groups["color"].Value.ToUpperInvariant()));
            else if (match.Value == "</font>")
                tokens.Add(new MarkupToken(MarkupKind.Close, "font"));
            else
                tokens.Add(new MarkupToken(match.Groups["close"].Length > 0 ? MarkupKind.Close : MarkupKind.Open, match.Groups["tag"].Value));
            position = match.Index + match.Length;
        }
        AddText(tokens, text[position..]);
        return tokens;
    }

    private static void AddText(List<MarkupToken> tokens, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                tokens.Add(new MarkupToken(MarkupKind.LineBreak, ""));
            if (lines[i].Length > 0)
                tokens.Add(new MarkupToken(MarkupKind.Text, lines[i]));
        }
    }

    /// <summary>
    /// Cleans up cue text written by a reader: tags properly nested (overlapping ones are closed and reopened), unclosed
    /// ones closed, stray closing tags and empty styles dropped, spaces at line ends and empty lines removed.
    /// </summary>
    public static string Normalize(string text)
    {
        var tokens = Tokens(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Replace('\t', ' '));
        List<MarkupToken> balanced = [];
        List<string> open = [];
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case MarkupKind.Open when !token.IsColor && open.Contains(token.Value):
                    break; // already on
                case MarkupKind.Open:
                    open.Add(token.Value);
                    balanced.Add(token);
                    break;
                case MarkupKind.Close:
                    var index = open.FindLastIndex(o => Closes(token.Value, o));
                    if (index < 0)
                        break;
                    var reopen = open.Skip(index + 1).ToList();
                    for (var i = open.Count - 1; i >= index; i--)
                        balanced.Add(new MarkupToken(MarkupKind.Close, open[i].StartsWith('#') ? "font" : open[i]));
                    open.RemoveRange(index, open.Count - index);
                    foreach (var tag in reopen)
                    {
                        open.Add(tag);
                        balanced.Add(new MarkupToken(MarkupKind.Open, tag));
                    }
                    break;
                default:
                    balanced.Add(token);
                    break;
            }
        }
        for (var i = open.Count - 1; i >= 0; i--)
            balanced.Add(new MarkupToken(MarkupKind.Close, open[i].StartsWith('#') ? "font" : open[i]));

        // Lines without their surrounding spaces, empty styles removed, no empty lines.
        var lines = Render(RemoveEmptyStyles(balanced)).Split('\n')
            .Select(line => MultipleSpaces().Replace(line, " ").Trim())
            .Where(line => PlainText(line).Trim().Length > 0);
        return string.Join('\n', lines);
    }

    private static bool Closes(string close, string open) => close == "font" ? open.StartsWith('#') : close == open;

    private static List<MarkupToken> RemoveEmptyStyles(List<MarkupToken> tokens)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Kind == MarkupKind.Open && tokens[i + 1].Kind == MarkupKind.Close)
                {
                    tokens.RemoveRange(i, 2);
                    changed = true;
                    break;
                }
            }
        }
        return tokens;
    }

    /// <summary>Tokens back to markup text.</summary>
    public static string Render(IEnumerable<MarkupToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            sb.Append(token.Kind switch
            {
                MarkupKind.Text => token.Value,
                MarkupKind.LineBreak => "\n",
                MarkupKind.Open when token.IsColor => $"<font color=\"{token.Value}\">",
                MarkupKind.Open => $"<{token.Value}>",
                _ => $"</{token.Value}>",
            });
        }
        return sb.ToString();
    }

    /// <summary>The text without any markup (lines still separated by <c>\n</c>).</summary>
    public static string PlainText(string text) =>
        string.Concat(Tokens(text).Select(t => t.Kind switch
        {
            MarkupKind.Text => t.Value,
            MarkupKind.LineBreak => "\n",
            _ => "",
        }));

    /// <summary>
    /// Turns HTML-like subtitle text (SRT, WebVTT, SAMI) into cue markup: <c>i</c>, <c>b</c>, <c>u</c>, font colours and
    /// WebVTT's colour classes (<c>&lt;c.yellow&gt;</c>) are kept, <c>br</c> breaks the line, every other tag is dropped
    /// (its text stays).
    /// </summary>
    /// <param name="decodeEntities">Decode <c>&amp;amp;</c>, <c>&amp;nbsp;</c>, … (WebVTT and SAMI; SRT has no entities).</param>
    public static string FromHtml(string text, bool decodeEntities)
    {
        var sb = new StringBuilder();
        var position = 0;
        var colors = 0;
        Stack<bool> classes = []; // WebVTT <c.yellow>: whether the class gave a colour
        foreach (Match match in AnyTag().Matches(text))
        {
            AppendText(sb, text[position..match.Index], decodeEntities);
            position = match.Index + match.Length;
            var closing = match.Groups["close"].Length > 0;
            var name = match.Groups["name"].Value.ToLowerInvariant();
            if (name == "c" || name.StartsWith("c.", StringComparison.Ordinal))
            {
                if (closing && classes.Count > 0 && classes.Pop())
                    sb.Append("</font>");
                else if (!closing && name.Split('.').Skip(1).Select(c => NamedColors.GetValueOrDefault(c)).FirstOrDefault(c => c is not null) is { } classColor)
                {
                    classes.Push(true);
                    sb.Append($"<font color=\"{classColor}\">");
                }
                else if (!closing)
                {
                    classes.Push(false);
                }
                continue;
            }
            switch (name)
            {
                case "i" or "em":
                    sb.Append(closing ? "</i>" : "<i>");
                    break;
                case "b" or "strong":
                    sb.Append(closing ? "</b>" : "<b>");
                    break;
                case "u":
                    sb.Append(closing ? "</u>" : "<u>");
                    break;
                case "br":
                    sb.Append('\n');
                    break;
                case "font" when closing:
                    if (colors > 0)
                    {
                        colors--;
                        sb.Append("</font>");
                    }
                    break;
                case "font":
                    // A <font face=…> without colour has nothing to keep; its </font> then must not close a colour.
                    if (ColorAttribute().Match(match.Groups["attributes"].Value) is { Success: true } attribute
                        && ParseColor(attribute.Groups["value"].Value) is { } color)
                    {
                        colors++;
                        sb.Append($"<font color=\"{color}\">");
                    }
                    break;
            }
        }
        AppendText(sb, text[position..], decodeEntities);
        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, string text, bool decodeEntities)
    {
        if (decodeEntities)
            text = System.Net.WebUtility.HtmlDecode(text);
        // Bidirectional marks of WebVTT (&lrm; &rlm;) and zero-width characters carry nothing visible.
        sb.Append(text.Replace("‎", "").Replace("‏", "").Replace("​", ""));
    }

    /// <summary>
    /// <c>#RRGGBB</c> from <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, bare hex digits, <c>rgb(…)</c> or a colour name;
    /// null when it can't be read.
    /// </summary>
    public static string? ParseColor(string value)
    {
        value = value.Trim().Trim('"', '\'').Trim();
        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && value.IndexOf('(') is var open and > 0 && value.EndsWith(')'))
        {
            var parts = value[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && parts.Take(3).All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 255))
                return "#" + string.Concat(parts.Take(3).Select(p => int.Parse(p, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture)));
            return null;
        }
        if (NamedColors.TryGetValue(value, out var named))
            return named;
        var hex = value.TrimStart('#');
        if (!hex.All(Uri.IsHexDigit))
            return null;
        return hex.Length switch
        {
            3 => "#" + string.Concat(hex.Select(c => $"{c}{c}")).ToUpperInvariant(),
            6 or 8 => "#" + hex[..6].ToUpperInvariant(),
            _ => null,
        };
    }

    /// <summary>The colour's name when it is one of the basic CSS colours (WebVTT has classes for those).</summary>
    public static string? ColorName(string hex) =>
        NamedColors.FirstOrDefault(pair => pair.Value == hex && pair.Key is "white" or "lime" or "cyan" or "red" or "yellow" or "magenta" or "blue" or "black").Key;

    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#FFFFFF",
        ["black"] = "#000000",
        ["red"] = "#FF0000",
        ["lime"] = "#00FF00",
        ["green"] = "#008000",
        ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00",
        ["cyan"] = "#00FFFF",
        ["aqua"] = "#00FFFF",
        ["magenta"] = "#FF00FF",
        ["fuchsia"] = "#FF00FF",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["silver"] = "#C0C0C0",
        ["orange"] = "#FFA500",
        ["purple"] = "#800080",
        ["maroon"] = "#800000",
        ["navy"] = "#000080",
        ["olive"] = "#808000",
        ["teal"] = "#008080",
        ["pink"] = "#FFC0CB",
    };

    [GeneratedRegex("""<(?<close>/?)(?<tag>i|b|u)>|<font color="#(?<color>[0-9A-Fa-f]{6})">|</font>""")]
    private static partial Regex CanonicalTag();

    [GeneratedRegex("""<\s*(?<close>/?)\s*(?<name>[^\s>/]+)(?<attributes>[^>]*)>""")]
    private static partial Regex AnyTag();

    [GeneratedRegex("""color\s*=\s*(?<value>"[^"]*"|'[^']*'|[^\s>]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ColorAttribute();

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();
}

/// <summary>Time stamps in the notations of the subtitle formats.</summary>
internal static class SubtitleTime
{
    /// <summary>A time from hours, minutes, seconds and a decimal fraction ("5" = 0.5 s, "045" = 45 ms).</summary>
    public static TimeSpan From(string hours, string minutes, string seconds, string? fraction)
    {
        var ms = 0;
        if (!string.IsNullOrEmpty(fraction))
            ms = (int)Math.Round(double.Parse("0." + fraction, CultureInfo.InvariantCulture) * 1000);
        return new TimeSpan(0, Number(hours), Number(minutes), Number(seconds), ms);
    }

    private static int Number(string text) => text.Length == 0 ? 0 : int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary><c>HH:MM:SS{separator}mmm</c> (SRT: comma, WebVTT and TTML: dot).</summary>
    public static string Milliseconds(TimeSpan time, char separator)
    {
        var ms = (long)Math.Round(Math.Max(0, time.TotalMilliseconds));
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}");
    }

    /// <summary><c>H:MM:SS.cc</c> (ASS).</summary>
    public static string Centiseconds(TimeSpan time)
    {
        var cs = (long)Math.Round(Math.Max(0, time.TotalMilliseconds) / 10);
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    /// <summary><c>mm:ss.cc</c> (LRC; minutes go past 99 for long recordings).</summary>
    public static string Lrc(TimeSpan time)
    {
        var cs = (long)Math.Round(Math.Max(0, time.TotalMilliseconds) / 10);
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }
}
