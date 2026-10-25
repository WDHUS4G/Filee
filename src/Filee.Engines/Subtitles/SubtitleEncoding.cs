// Subtitle files come in whatever encoding their author's PC used. Korean SAMI and SRT files are usually CP949
// (EUC-KR plus Microsoft's extra Hangul), Chinese ones GBK, Western ones Windows-1252. Filee reads them all and
// always writes UTF-8.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Subtitles;

/// <summary>Decodes subtitle files.</summary>
internal static partial class SubtitleEncoding
{
    /// <summary>
    /// The text of a subtitle file: by its byte order mark; as UTF-8 when the bytes are valid UTF-8; by the charset the
    /// file declares (SAMI, TTML); as CP949 when the bytes look like Korean (EUC-KR / CP949), or on Korean Windows when
    /// they are valid CP949 (symbols and Hanja too); as the PC's code page on Chinese and Japanese Windows; else as the
    /// PC's Western code page.
    /// </summary>
    public static string Decode(byte[] bytes) => Decode(bytes, CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    /// <summary>
    /// <see cref="Decode(byte[])"/> with the PC's ANSI code page given (for tests). <paramref name="declaredCharset"/>
    /// false ignores a <c>charset=</c> / <c>encoding=</c> in the text: in a TXT, Markdown or CSV file that is content
    /// (a line of code, a quoted HTML tag), not what the file is saved in.
    /// </summary>
    internal static string Decode(byte[] bytes, int ansiCodePage, bool declaredCharset = true)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        // UTF-16 without a byte order mark: every other byte of ASCII text is zero.
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0)
            return Encoding.Unicode.GetString(bytes);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (declaredCharset && Declared(bytes) is { } declared)
            return declared.GetString(bytes);
        if (LooksKorean(bytes) || (ansiCodePage == 949 && IsCp949(bytes)))
            return Encoding.GetEncoding(949).GetString(bytes);
        if (ansiCodePage is 932 or 936 or 950)
            return Encoding.GetEncoding(ansiCodePage).GetString(bytes);
        return Encoding.GetEncoding(ansiCodePage is > 0 and not (949 or 1361) and not 65001 ? ansiCodePage : 1252).GetString(bytes);
    }

    /// <summary>
    /// True when every byte decodes as CP949 without a substitute. Western text fails here (an accented letter followed
    /// by a space or punctuation is no CP949 pair), so a Korean PC still reads a Windows-1252 file as Windows-1252.
    /// </summary>
    private static bool IsCp949(byte[] bytes)
    {
        try
        {
            Encoding.GetEncoding(949, EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The encoding named by <c>charset=…</c> (SAMI's HTML) or <c>encoding="…"</c> (XML) near the start.</summary>
    private static Encoding? Declared(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        if (DeclaredCharset().Match(head) is not { Success: true } match)
            return null;
        try
        {
            var encoding = Encoding.GetEncoding(match.Groups["name"].Value);
            // A file claiming UTF-8 that isn't valid UTF-8 was saved by a program that lied: guess instead.
            return encoding.CodePage is 65001 or 1200 or 1201 ? null : encoding;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the non-ASCII bytes form CP949 pairs, most of them Hangul syllables of KS X 1001 (lead byte B0–C8,
    /// trail byte A1–FE) and hardly any with a lead byte above C8, as Korean text is. Western text decoded as CP949
    /// gives pairs with ASCII trail bytes; Chinese GBK text uses lead bytes above C8 for a good part of its common
    /// characters (的 B5C4 but 是 CAC7, 我 CED2, 这 D5E2), where Korean has only the rare Hanja.
    /// </summary>
    internal static bool LooksKorean(byte[] bytes)
    {
        int pairs = 0, hangul = 0, high = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var lead = bytes[i];
            if (lead < 0x80)
                continue;
            if (lead is < 0x81 or > 0xFE || i + 1 >= bytes.Length)
                return false;
            var trail = bytes[++i];
            if (trail is not (>= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0x81 and <= 0xFE))
                return false;
            pairs++;
            if (lead is >= 0xB0 and <= 0xC8 && trail >= 0xA1)
                hangul++;
            else if (lead > 0xC8)
                high++;
        }
        return pairs > 0 && hangul * 2 >= pairs && high * 10 <= pairs;
    }

    [GeneratedRegex("""(?:charset|encoding)\s*=\s*["']?(?<name>[A-Za-z0-9_.:-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DeclaredCharset();
}
