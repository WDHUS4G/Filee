// Text in a DWG or DXF before AutoCAD 2007 is stored in the drawing's code page ($DWGCODEPAGE), not UTF-8 (#489).
// A drawing from 2007 or later often still says ANSI_1252 while holding Korean or Chinese text, and ACadSharp encodes
// with the code page as it is, so saving it as 2004 would turn that text into "?". So the code page is picked from the
// text, and a character it can't hold is written as \U+XXXX, which AutoCAD reads in TEXT, MTEXT and attributes.

using System.Text;
using ACadSharp;
using ACadSharp.Entities;

namespace Filee.Engines.Cad;

internal static class CadCodePages
{
    /// <summary>
    /// Code pages AutoCAD names ANSI_n, in the order a tie goes after the drawing's own one. The East Asian ones also
    /// hold Greek, Cyrillic and each other's Chinese characters, so they come first only for the script they are for
    /// (<see cref="Choose"/>): Korean for Hangul, Japanese for Kana, Chinese for Chinese characters, Big5 before GBK
    /// (text both hold, traditional characters, is Big5; simplified text, which Big5 lacks, is GBK).
    /// </summary>
    private static readonly int[] Candidates = [1252, 1250, 1251, 1253, 1254, 1255, 1256, 1257, 1258, 874, 950, 936, 949, 932];

    /// <summary>
    /// Gives a drawing about to be written in a pre-2007 version the code page that holds most of its text (its own
    /// one when that ties) and escapes what the code page can't hold. Returns the code page.
    /// </summary>
    public static int Prepare(CadDocument document)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var texts = Texts(document).ToList();
        var page = Choose(texts.Select(t => t.Get()), Number(document.Header.CodePage));
        // ACadSharp 3.8 knows Korean only as KCS5601; CadFile writes the DXF header as ANSI_949, as AutoCAD does.
        document.Header.CodePage = page == 949 ? "KCS5601" : $"ANSI_{page}";
        var encoding = Encoding.GetEncoding(page);
        foreach (var text in texts)
        {
            // Only changed text is set back: a dimension without its own text (null) must not get an empty one.
            var value = text.Get();
            var escaped = Escape(value, encoding);
            if (!ReferenceEquals(value, escaped))
                text.Set(escaped);
        }
        return page;
    }

    /// <summary>The candidate that holds the most distinct non-ASCII characters; <paramref name="current"/> on a tie.</summary>
    internal static int Choose(IEnumerable<string> texts, int? current)
    {
        var characters = texts.SelectMany(t => t).Where(c => c > 0x7F).ToHashSet();
        var first = new List<int>();
        if (current is { } own && Candidates.Contains(own))
            first.Add(own);
        if (characters.Any(c => c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏'))
            first.Add(949);
        if (characters.Any(c => c is >= '぀' and <= 'ヿ'))
            first.Add(932);
        if (characters.Any(c => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿'))
            first.AddRange([950, 936]);
        int[] order = [.. first, .. Candidates.Except(first)];
        return order.Select(page => (Page: page, Held: characters.Count(c => Holds(Encoding.GetEncoding(page), c))))
            .OrderByDescending(p => p.Held).First().Page;
    }

    /// <summary>The text with every character <paramref name="encoding"/> can't hold as <c>\U+XXXX</c>.</summary>
    internal static string Escape(string text, Encoding encoding)
    {
        if (text.All(c => c <= 0x7F || Holds(encoding, c)))
            return text;
        var result = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c <= 0x7F || Holds(encoding, c))
                result.Append(c);
            else if (char.IsHighSurrogate(c))
                result.Append('?'); // \U+ takes four hex digits: nothing outside the BMP fits, one ? per character
            else if (char.IsLowSurrogate(c))
                continue;
            else
                result.Append($"\\U+{(int)c:X4}");
        }
        return result.ToString();
    }

    private static bool Holds(Encoding encoding, char c)
    {
        if (char.IsSurrogate(c))
            return false;
        var one = c.ToString();
        return encoding.GetString(encoding.GetBytes(one)) == one;
    }

    /// <summary>The number of a $DWGCODEPAGE name (ANSI_949, GB2312, BIG5 …), or null when it isn't one of them.</summary>
    internal static int? Number(string? name) => name?.ToUpperInvariant() switch
    {
        null => null,
        "KCS5601" or "JOHAB" => 949,
        "GB2312" => 936,
        "BIG5" => 950,
        "DOS932" or "SHIFT_JIS" => 932,
        var upper when upper.StartsWith("ANSI_", StringComparison.Ordinal) && int.TryParse(upper[5..], out var number) => number,
        _ => null,
    };

    /// <summary>
    /// Every text a drawing holds in its code page: TEXT and attributes (values and tags), MTEXT, dimension text, and
    /// the names of layers, blocks, text and dimension styles, linetypes and layouts. Korean layer names with English
    /// labels are common, so names count when the code page is picked, and are escaped like text.
    /// </summary>
    private static IEnumerable<(Func<string> Get, Action<string> Set)> Texts(CadDocument document)
    {
        foreach (var layer in document.Layers)
            yield return (() => layer.Name, v => layer.Name = v);
        foreach (var style in document.TextStyles)
            yield return (() => style.Name, v => style.Name = v);
        foreach (var style in document.DimensionStyles)
            yield return (() => style.Name, v => style.Name = v);
        foreach (var lineType in document.LineTypes)
            yield return (() => lineType.Name, v => lineType.Name = v);
        foreach (var layout in document.Layouts)
            yield return (() => layout.Name, v => layout.Name = v);
        foreach (var record in document.BlockRecords)
        {
            // *Model_Space, *Paper_Space and anonymous blocks are ASCII by definition.
            if (!record.Name.StartsWith('*'))
                yield return (() => record.Name, v => record.Name = v);
            foreach (var entity in record.Entities)
            {
                switch (entity)
                {
                    case AttributeDefinition definition:
                        yield return (() => definition.Value ?? "", v => definition.Value = v);
                        yield return (() => definition.Tag ?? "", v => definition.Tag = v);
                        break;
                    case TextEntity text:
                        yield return (() => text.Value ?? "", v => text.Value = v);
                        break;
                    case MText mtext:
                        yield return (() => mtext.Value ?? "", v => mtext.Value = v);
                        break;
                    case Dimension dimension:
                        yield return (() => dimension.Text ?? "", v => dimension.Text = v);
                        break;
                    case Insert insert:
                        foreach (var attribute in insert.Attributes)
                        {
                            yield return (() => attribute.Value ?? "", v => attribute.Value = v);
                            yield return (() => attribute.Tag ?? "", v => attribute.Tag = v);
                        }
                        break;
                }
            }
        }
    }
}
