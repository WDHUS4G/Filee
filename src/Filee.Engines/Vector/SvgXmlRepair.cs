// Repairs the one mistake that makes many hand-written and AI-generated SVGs unreadable: an "&" that isn't an entity
// ("A & B" in a <text>, a URL query in an href) and HTML entities XML doesn't know (&nbsp;, &copy;). Browsers forgive
// some of it; the XML parser Svg.Skia uses stops at the first one (issue #120).

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Vector;

internal static partial class SvgXmlRepair
{
    /// <summary>The entities every XML document has; anything else must be declared in the DTD.</summary>
    private static readonly HashSet<string> XmlEntities = ["amp", "lt", "gt", "quot", "apos"];

    /// <summary>
    /// Returns <paramref name="svg"/> with every "&amp;" that doesn't start a character reference or a known entity
    /// escaped, and HTML named entities turned into character references. Entities declared in the document's DTD
    /// (Illustrator's &amp;ns_svg;), the DOCTYPE, comments and CDATA sections are left alone. Null when nothing needed repair, or
    /// when the bytes aren't UTF-8 (another declared encoding is read as it is).
    /// </summary>
    public static byte[]? FixAmpersands(byte[] svg)
    {
        if (svg.AsSpan().IndexOf((byte)'&') < 0)
            return null;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(svg);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var declared = DeclaredEntity().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var result = new StringBuilder(text.Length + 64);
        var changed = false;
        var position = 0;
        foreach (Match skip in Untouched().Matches(text))
        {
            changed |= Repair(text.AsSpan(position, skip.Index - position), declared, result);
            result.Append(skip.Value);
            position = skip.Index + skip.Length;
        }
        changed |= Repair(text.AsSpan(position), declared, result);
        if (!changed)
            return null;
        return Encoding.UTF8.GetBytes(result.ToString()); // a byte order mark was decoded as U+FEFF and stays
    }

    /// <summary>Appends <paramref name="part"/> to <paramref name="result"/> repaired; true if anything changed.</summary>
    private static bool Repair(ReadOnlySpan<char> part, HashSet<string> declared, StringBuilder result)
    {
        var changed = false;
        var text = part.ToString();
        var last = 0;
        foreach (Match amp in Ampersand().Matches(text))
        {
            result.Append(text, last, amp.Index - last);
            last = amp.Index + amp.Length;
            var name = amp.Groups["name"];
            if (amp.Groups["number"].Success || name.Success && (XmlEntities.Contains(name.Value) || declared.Contains(name.Value)))
            {
                result.Append(amp.Value);
                continue;
            }
            changed = true;
            if (name.Success && WebUtility.HtmlDecode(amp.Value) is { } decoded && decoded != amp.Value)
            {
                // An HTML entity: the same character as a reference XML reads.
                foreach (var rune in decoded.EnumerateRunes())
                    result.Append("&#").Append(rune.Value).Append(';');
            }
            else
            {
                // A lone "&", or an entity nobody defines: the "&" itself is text.
                result.Append("&amp;").Append(amp.Value, 1, amp.Value.Length - 1);
            }
        }
        result.Append(text, last, text.Length - last);
        return changed;
    }

    // "&", then a character reference, a name with its ";", or neither (a lone "&").
    [GeneratedRegex(@"&(?:(?<number>#[0-9]+;|#[xX][0-9A-Fa-f]+;)|(?<name>[A-Za-z_][A-Za-z0-9._-]*);)?")]
    private static partial Regex Ampersand();

    [GeneratedRegex(@"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<!DOCTYPE[^\[>]*(?:\[.*?\])?\s*>", RegexOptions.Singleline)]
    private static partial Regex Untouched();

    [GeneratedRegex(@"<!ENTITY\s+([A-Za-z_][A-Za-z0-9._-]*)")]
    private static partial Regex DeclaredEntity();
}
