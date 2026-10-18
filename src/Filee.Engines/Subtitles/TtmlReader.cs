// TTML / DFXP (W3C Timed Text): XML with <p begin=… end=…> paragraphs. Elements are matched by local name, so both
// the TTML 1 namespace and the older DFXP one (2006/10/ttaf1) read. Times are relative to the parent's begin;
// clock times, frames (ttp:frameRate) and offsets in h / m / s / ms / f / ticks are understood.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Subtitles;

/// <summary>Reads TTML subtitles.</summary>
internal static partial class TtmlReader
{
    public static SubtitleDocument Read(string text)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The TTML file is not valid XML: " + ex.Message, ex);
        }
        var root = xml.Root ?? throw new InvalidDataException("The TTML file is empty.");
        var document = new SubtitleDocument { Language = Attribute(root, "lang") };
        var context = new Context(
            Number(Attribute(root, "frameRate"), 30) * Ratio(Attribute(root, "frameRateMultiplier")),
            Number(Attribute(root, "tickRate"), 0),
            root.Descendants().Where(e => e.Name.LocalName == "style" && Attribute(e, "id") is not null)
                .GroupBy(e => Attribute(e, "id")!)
                .ToDictionary(g => g.Key, g => g.First()));
        if (context.TickRate <= 0)
            context = context with { TickRate = context.FrameRate > 0 ? context.FrameRate : 1 };

        if (root.Descendants().FirstOrDefault(e => e.Name.LocalName == "title") is { } title && title.Value.Trim().Length > 0)
            document.Title = title.Value.Trim();
        if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "body") is { } body)
            Visit(body, TimeSpan.Zero, null, document, context);
        return document;
    }

    private sealed record Context(double FrameRate, double TickRate, Dictionary<string, XElement> Styles);

    /// <summary>Walks body / div / p, adding each parent's begin to its children's times.</summary>
    private static void Visit(XElement element, TimeSpan parentBegin, TimeSpan? parentEnd, SubtitleDocument document, Context context)
    {
        var begin = parentBegin + (Time(Attribute(element, "begin"), context) ?? TimeSpan.Zero);
        TimeSpan? end = Time(Attribute(element, "end"), context) is { } e ? parentBegin + e
            : Time(Attribute(element, "dur"), context) is { } d ? begin + d
            : parentEnd;

        if (element.Name.LocalName == "p")
        {
            var sb = new StringBuilder();
            AppendContent(element, sb, context);
            document.Add(begin, end ?? begin + SubtitleDocument.DefaultDuration, sb.ToString());
            return;
        }
        foreach (var child in element.Elements().Where(c => c.Name.LocalName is "div" or "p"))
            Visit(child, begin, end, document, context);
    }

    /// <summary>The text of a p or span with its styles as markup; br breaks the line.</summary>
    private static void AppendContent(XElement element, StringBuilder sb, Context context)
    {
        var styles = Styles(element, context);
        foreach (var style in styles)
            sb.Append(style.StartsWith('#') ? $"<font color=\"{style}\">" : $"<{style}>");
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(Whitespace().Replace(text.Value, " "));
                    break;
                case XElement { Name.LocalName: "br" }:
                    sb.Append('\n');
                    break;
                case XElement { Name.LocalName: "span" } span:
                    AppendContent(span, sb, context);
                    break;
            }
        }
        foreach (var style in Enumerable.Reverse(styles))
            sb.Append(style.StartsWith('#') ? "</font>" : $"</{style}>");
    }

    /// <summary>Italic, bold, underline and colour of an element: its own tts: attributes and the styles it references.</summary>
    private static List<string> Styles(XElement element, Context context)
    {
        List<XElement> sources = [];
        foreach (var id in (Attribute(element, "style") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (context.Styles.TryGetValue(id, out var style))
                sources.Add(style);
        }
        sources.Add(element);

        List<string> result = [];
        foreach (var source in sources)
        {
            if (Attribute(source, "fontStyle") is "italic" or "oblique" && !result.Contains("i"))
                result.Add("i");
            if (Attribute(source, "fontWeight") == "bold" && !result.Contains("b"))
                result.Add("b");
            if (Attribute(source, "textDecoration")?.Contains("underline", StringComparison.Ordinal) == true && !result.Contains("u"))
                result.Add("u");
            if (Attribute(source, "color") is { } value && SubtitleMarkup.ParseColor(value) is { } color && color != "#FFFFFF")
            {
                result.RemoveAll(s => s.StartsWith('#'));
                result.Add(color);
            }
        }
        return result;
    }

    /// <summary>An attribute by local name in any namespace (tts:color, xml:lang, ttp:frameRate, plain begin).</summary>
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value.Trim();

    /// <summary>A TTML time expression, null when absent or unreadable.</summary>
    internal static TimeSpan? Time(string? value, double frameRate, double tickRate) =>
        Time(value, new Context(frameRate, tickRate, new Dictionary<string, XElement>()));

    private static TimeSpan? Time(string? value, Context context)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Clock().Match(value) is { Success: true } clock)
        {
            var seconds = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600.0
                          + int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                          + double.Parse(clock.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (clock.Groups["frames"].Success && context.FrameRate > 0)
                seconds += double.Parse(clock.Groups["frames"].Value, CultureInfo.InvariantCulture) / context.FrameRate;
            return TimeSpan.FromSeconds(seconds);
        }
        if (Offset().Match(value) is { Success: true } offset)
        {
            var number = double.Parse(offset.Groups["n"].Value, CultureInfo.InvariantCulture);
            var seconds = offset.Groups["unit"].Value switch
            {
                "h" => number * 3600,
                "m" => number * 60,
                "ms" => number / 1000,
                "f" => context.FrameRate > 0 ? number / context.FrameRate : 0,
                "t" => context.TickRate > 0 ? number / context.TickRate : 0,
                _ => number,
            };
            return TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    private static double Number(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    /// <summary><c>ttp:frameRateMultiplier="1000 1001"</c> → 0.999 (29.97 fps written as 30 × 1000/1001).</summary>
    private static double Ratio(string? text) =>
        text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var n, var d]
        && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
        && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && num > 0 && den > 0
            ? num / den
            : 1;

    [GeneratedRegex(@"^(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)(?::(?<frames>\d+(?:\.\d+)?))?$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)(?<unit>h|ms|m|s|f|t)$")]
    private static partial Regex Offset();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
