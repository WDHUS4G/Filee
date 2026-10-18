// Writers of every subtitle target. All write UTF-8: SRT, ASS, SAMI and MicroDVD with a byte order mark (Windows
// players such as PotPlayer and KMPlayer take files without one for the PC's code page), WebVTT, TTML and LRC without
// (browsers, XML and music players expect plain UTF-8).

using System.Globalization;
using System.Text;
using System.Xml;

namespace Filee.Engines.Subtitles;

/// <summary>Writes a <see cref="SubtitleDocument"/> in one of the subtitle formats.</summary>
internal static class SubtitleWriter
{
    /// <summary>Format ids this engine writes.</summary>
    public static readonly string[] Formats = ["srt", "ass", "vtt", "smi", "lrc", "ttml", "sub"];

    /// <summary>The frame rate MicroDVD files are written with (stated in their first line).</summary>
    internal const double MicroDvdFps = SubtitleReader.MicroDvdDefaultFps;

    public static byte[] Write(SubtitleDocument document, string format)
    {
        var (text, bom) = format switch
        {
            "srt" => (Srt(document), true),
            "ass" => (Ass(document), true),
            "vtt" => (Vtt(document), false),
            "smi" => (Sami(document), true),
            "lrc" => (Lrc(document), false),
            "ttml" => (Ttml(document), false),
            "sub" => (MicroDvd(document), true),
            _ => throw new NotSupportedException($"Subtitles can't be written as '{format}'."),
        };
        return [.. (bom ? Encoding.UTF8.GetPreamble() : []), .. new UTF8Encoding(false).GetBytes(text)];
    }

    // ───────────────────────── SubRip ─────────────────────────

    /// <summary>SubRip keeps the markup as it is: its own tags.</summary>
    private static string Srt(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < document.Cues.Count; i++)
        {
            var cue = document.Cues[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}\r\n")
              .Append(CultureInfo.InvariantCulture, $"{SubtitleTime.Milliseconds(cue.Start, ',')} --> {SubtitleTime.Milliseconds(cue.End, ',')}\r\n")
              .Append(cue.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    // ───────────────────────── WebVTT ─────────────────────────

    /// <summary>WebVTT: i / b / u tags, colours as the basic colour classes (others are dropped), text escaped.</summary>
    private static string Vtt(SubtitleDocument document)
    {
        var sb = new StringBuilder("WEBVTT\n");
        if (document.Language is { Length: > 0 } language)
            sb.Append("Language: ").Append(language).Append('\n');
        sb.Append('\n');
        foreach (var cue in document.Cues)
        {
            sb.Append(SubtitleTime.Milliseconds(cue.Start, '.')).Append(" --> ").Append(SubtitleTime.Milliseconds(cue.End, '.')).Append('\n');
            Stack<bool> colors = [];
            foreach (var token in SubtitleMarkup.Tokens(cue.Text))
            {
                switch (token.Kind)
                {
                    case MarkupKind.Text:
                        // "-->" would end the cue text early.
                        sb.Append(Escape(token.Value).Replace("--&gt;", "-&gt;"));
                        break;
                    case MarkupKind.LineBreak:
                        sb.Append('\n');
                        break;
                    case MarkupKind.Open when token.IsColor:
                        var name = SubtitleMarkup.ColorName(token.Value);
                        colors.Push(name is not null);
                        if (name is not null)
                            sb.Append("<c.").Append(name).Append('>');
                        break;
                    case MarkupKind.Open:
                        sb.Append('<').Append(token.Value).Append('>');
                        break;
                    case MarkupKind.Close when token.Value == "font":
                        if (colors.Count > 0 && colors.Pop())
                            sb.Append("</c>");
                        break;
                    case MarkupKind.Close:
                        sb.Append("</").Append(token.Value).Append('>');
                        break;
                }
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ───────────────────────── ASS ─────────────────────────

    /// <summary>
    /// Advanced SubStation Alpha for a 1920 × 1080 picture: one "Default" style (white Arial with a black outline at the
    /// bottom centre), markup as override codes.
    /// </summary>
    private static string Ass(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\r\n");
        if (document.Title is { Length: > 0 } title)
            sb.Append("Title: ").Append(title.Replace('\n', ' ')).Append("\r\n");
        sb.Append("ScriptType: v4.00+\r\nWrapStyle: 0\r\nScaledBorderAndShadow: yes\r\nPlayResX: 1920\r\nPlayResY: 1080\r\n\r\n");
        sb.Append("[V4+ Styles]\r\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
        sb.Append("Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,50,1\r\n\r\n");
        sb.Append("[Events]\r\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        foreach (var cue in document.Cues)
        {
            sb.Append("Dialogue: 0,").Append(SubtitleTime.Centiseconds(cue.Start)).Append(',').Append(SubtitleTime.Centiseconds(cue.End))
              .Append(",Default,,0,0,0,,").Append(AssText(cue.Text)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Cue markup as ASS text: \N between lines, {\i1}…{\i0}, colours as &amp;HBBGGRR&amp;.</summary>
    internal static string AssText(string text)
    {
        var sb = new StringBuilder();
        Stack<string> colors = [];
        foreach (var token in SubtitleMarkup.Tokens(text))
        {
            switch (token.Kind)
            {
                case MarkupKind.Text:
                    // A brace would start an override block.
                    sb.Append(token.Value.Replace("{", "(").Replace("}", ")"));
                    break;
                case MarkupKind.LineBreak:
                    sb.Append("\\N");
                    break;
                case MarkupKind.Open when token.IsColor:
                    colors.Push(token.Value);
                    sb.Append("{\\c").Append(AssColor(token.Value)).Append('}');
                    break;
                case MarkupKind.Open:
                    sb.Append("{\\").Append(token.Value).Append("1}");
                    break;
                case MarkupKind.Close when token.Value == "font":
                    // Back to the colour around it, or with a bare \c to the style's colour.
                    if (colors.Count > 0)
                        colors.Pop();
                    sb.Append("{\\c").Append(colors.Count > 0 ? AssColor(colors.Peek()) : "").Append('}');
                    break;
                case MarkupKind.Close:
                    sb.Append("{\\").Append(token.Value).Append("0}");
                    break;
            }
        }
        return sb.ToString().Replace("}{", "");
    }

    private static string AssColor(string hex) => $"&H{hex[5..7]}{hex[3..5]}{hex[1..3]}&";

    // ───────────────────────── SAMI ─────────────────────────

    /// <summary>
    /// SAMI with one language class (KRCC for Korean, ENCC for English, …): a SYNC per cue and an &amp;nbsp; SYNC where
    /// the screen goes blank before the next one.
    /// </summary>
    private static string Sami(SubtitleDocument document)
    {
        var language = document.GuessLanguage();
        var (className, languageName) = SamiClass(language);
        var sb = new StringBuilder();
        sb.Append("<SAMI>\r\n<HEAD>\r\n<TITLE>").Append(Escape(document.Title ?? "")).Append("</TITLE>\r\n");
        sb.Append("<STYLE TYPE=\"text/css\">\r\n<!--\r\n");
        sb.Append("P { margin-left: 8pt; margin-right: 8pt; margin-bottom: 2pt; margin-top: 2pt; text-align: center; font-size: 20pt; font-family: sans-serif; font-weight: normal; color: white; }\r\n");
        sb.Append('.').Append(className).Append(" { Name: ").Append(languageName).Append("; lang: ").Append(language).Append("; SAMIType: CC; }\r\n");
        sb.Append("-->\r\n</STYLE>\r\n</HEAD>\r\n<BODY>\r\n");
        for (var i = 0; i < document.Cues.Count; i++)
        {
            var cue = document.Cues[i];
            sb.Append(CultureInfo.InvariantCulture, $"<SYNC Start={(long)Math.Round(cue.Start.TotalMilliseconds)}><P Class={className}>");
            sb.Append(Html(cue.Text, "<br>")).Append("\r\n");
            var next = i + 1 < document.Cues.Count ? document.Cues[i + 1].Start : (TimeSpan?)null;
            if (next is null || next > cue.End)
                sb.Append(CultureInfo.InvariantCulture, $"<SYNC Start={(long)Math.Round(cue.End.TotalMilliseconds)}><P Class={className}>&nbsp;\r\n");
        }
        sb.Append("</BODY>\r\n</SAMI>\r\n");
        return sb.ToString();
    }

    /// <summary>The usual SAMI class of a language ("ko-KR" → KRCC, "Korean").</summary>
    private static (string Class, string Name) SamiClass(string language)
    {
        var code = language.Split('-')[0].ToLowerInvariant();
        return code switch
        {
            "ko" => ("KRCC", "Korean"),
            "en" => ("ENCC", "English"),
            "ja" => ("JPCC", "Japanese"),
            "zh" => ("ZHCC", "Chinese"),
            _ => ($"{code.ToUpperInvariant()}CC", CultureName(code)),
        };
    }

    private static string CultureName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Cue markup as HTML (SAMI): tags as they are, text escaped, <paramref name="lineBreak"/> between lines.</summary>
    private static string Html(string text, string lineBreak) =>
        string.Concat(SubtitleMarkup.Tokens(text).Select(token => token.Kind switch
        {
            MarkupKind.Text => Escape(token.Value),
            MarkupKind.LineBreak => lineBreak,
            MarkupKind.Open when token.IsColor => $"<font color=\"{token.Value}\">",
            MarkupKind.Open => $"<{token.Value}>",
            _ => $"</{token.Value}>",
        }));

    // ───────────────────────── LRC ─────────────────────────

    /// <summary>
    /// LRC: one <c>[mm:ss.xx]</c> line per cue (its lines joined), plain text, and an empty time line where the screen
    /// goes blank before the next cue.
    /// </summary>
    private static string Lrc(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        if (document.Title is { Length: > 0 } title)
            sb.Append("[ti:").Append(title.Replace('\n', ' ').Replace("]", ")")).Append("]\r\n");
        for (var i = 0; i < document.Cues.Count; i++)
        {
            var cue = document.Cues[i];
            var lyric = string.Join(" ", SubtitleMarkup.PlainText(cue.Text).Split('\n', StringSplitOptions.TrimEntries));
            sb.Append('[').Append(SubtitleTime.Lrc(cue.Start)).Append(']').Append(lyric).Append("\r\n");
            var next = i + 1 < document.Cues.Count ? document.Cues[i + 1].Start : (TimeSpan?)null;
            if (next is null || next - cue.End >= TimeSpan.FromMilliseconds(10))
                sb.Append('[').Append(SubtitleTime.Lrc(cue.End)).Append("]\r\n");
        }
        return sb.ToString();
    }

    // ───────────────────────── TTML ─────────────────────────

    /// <summary>TTML 1: a bottom region, one p per cue, spans for italic / bold / underline / colour.</summary>
    private static string Ttml(SubtitleDocument document)
    {
        const string Tt = "http://www.w3.org/ns/ttml";
        const string Tts = "http://www.w3.org/ns/ttml#styling";
        const string Ttm = "http://www.w3.org/ns/ttml#metadata";
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, NewLineChars = "\n", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true };
        using (var xml = XmlWriter.Create(sb, settings))
        {
            xml.WriteStartElement("tt", Tt);
            xml.WriteAttributeString("xmlns", "tts", null, Tts);
            xml.WriteAttributeString("xmlns", "ttm", null, Ttm);
            xml.WriteAttributeString("xml", "lang", null, document.GuessLanguage());

            xml.WriteStartElement("head", Tt);
            if (document.Title is { Length: > 0 } title)
            {
                xml.WriteStartElement("metadata", Tt);
                xml.WriteElementString("title", Ttm, title);
                xml.WriteEndElement();
            }
            xml.WriteStartElement("styling", Tt);
            xml.WriteStartElement("style", Tt);
            xml.WriteAttributeString("xml", "id", null, "default");
            xml.WriteAttributeString("color", Tts, "white");
            xml.WriteAttributeString("fontFamily", Tts, "proportionalSansSerif");
            xml.WriteAttributeString("textAlign", Tts, "center");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("layout", Tt);
            xml.WriteStartElement("region", Tt);
            xml.WriteAttributeString("xml", "id", null, "bottom");
            xml.WriteAttributeString("origin", Tts, "10% 75%");
            xml.WriteAttributeString("extent", Tts, "80% 20%");
            xml.WriteAttributeString("displayAlign", Tts, "after");
            xml.WriteAttributeString("textAlign", Tts, "center");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement(); // head

            xml.WriteStartElement("body", Tt);
            xml.WriteAttributeString("region", "bottom");
            xml.WriteAttributeString("style", "default");
            xml.WriteStartElement("div", Tt);
            foreach (var cue in document.Cues)
            {
                xml.WriteStartElement("p", Tt);
                xml.WriteAttributeString("begin", SubtitleTime.Milliseconds(cue.Start, '.'));
                xml.WriteAttributeString("end", SubtitleTime.Milliseconds(cue.End, '.'));
                // Written as one piece: the indenting writer would put line breaks (spaces when read) before spans.
                xml.WriteRaw(TtmlContent(cue.Text));
                xml.WriteFullEndElement(); // p
            }
            xml.WriteEndElement(); // div
            xml.WriteEndElement(); // body
            xml.WriteEndElement(); // tt
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb + "\n";
    }

    /// <summary>The content of a TTML p: escaped text, <c>br</c> and <c>span</c>s with tts: styles.</summary>
    private static string TtmlContent(string text) =>
        string.Concat(SubtitleMarkup.Tokens(text).Select(token => token.Kind switch
        {
            MarkupKind.Text => Escape(token.Value),
            MarkupKind.LineBreak => "<br />",
            MarkupKind.Open => token.Value switch
            {
                "i" => "<span tts:fontStyle=\"italic\">",
                "b" => "<span tts:fontWeight=\"bold\">",
                "u" => "<span tts:textDecoration=\"underline\">",
                _ => $"<span tts:color=\"{token.Value}\">",
            },
            _ => "</span>",
        }));

    // ───────────────────────── MicroDVD ─────────────────────────

    /// <summary>
    /// MicroDVD at <see cref="MicroDvdFps"/> frames per second (stated in the first line, as players expect): plain
    /// text with <c>|</c> between lines; a cue that is italic throughout gets <c>{Y:i}</c>.
    /// </summary>
    private static string MicroDvd(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{{1}}{{1}}{MicroDvdFps}\r\n");
        foreach (var cue in document.Cues)
        {
            var start = (long)Math.Round(cue.Start.TotalSeconds * MicroDvdFps);
            var end = Math.Max(start + 1, (long)Math.Round(cue.End.TotalSeconds * MicroDvdFps));
            var tokens = SubtitleMarkup.Tokens(cue.Text);
            var italic = tokens.Count > 2 && tokens[0] is { Kind: MarkupKind.Open, Value: "i" } && tokens[^1] is { Kind: MarkupKind.Close, Value: "i" }
                         && tokens.Count(t => t.Value == "i") == 2;
            var text = string.Join("|", SubtitleMarkup.PlainText(cue.Text).Split('\n'));
            sb.Append(CultureInfo.InvariantCulture, $"{{{start}}}{{{end}}}{(italic ? "{Y:i}" : "")}{text.Replace("{", "(").Replace("}", ")")}\r\n");
        }
        return sb.ToString();
    }
}
