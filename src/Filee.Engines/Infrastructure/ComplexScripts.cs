// Scripts whose text needs complex shaping to be drawn: letters that join (Arabic), right-to-left order (Arabic,
// Hebrew), vowel signs drawn before the consonant they follow, conjuncts and stacked marks (the Indic scripts, Thai,
// Khmer, Myanmar, Tibetan, ...). rhwp draws one glyph per character from left to right without any of that, so a
// document with such text goes to LibreOffice instead (RhwpConverter, issues #185 and #202); the PDF reader puts the
// characters of such text back in their logical order (PdfDocumentReader.ComplexText).
//
// FindInFile scans a document's text cheaply, without reading it into the document model: the content parts of an
// Office Open XML / OWPML / OpenDocument / EPUB package as they are (markup is ASCII, so any match is text), a text
// file decoded like the plain text reader does (character references of HTML and RTF included).
//
// The same scan also finds text rhwp has no widths for: its width tables cover only ASCII and Latin-1, general
// punctuation, math, the CJK forms and Hangul (rhwp#8143), so a letter of any other script — Polish, Turkish,
// Vietnamese, Greek, Cyrillic, ... — or a combining mark is laid out at a fixed half-em. Such a document takes
// a route without unshaped edges when one exists and keeps rhwp otherwise (it is never refused for this).

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Infrastructure;

/// <summary>Finds text in scripts that need complex shaping.</summary>
internal static partial class ComplexScripts
{
    /// <summary>Unicode blocks of scripts that need shaping, by first code point, with the script's English name.</summary>
    private static readonly (int Start, int End, string Name)[] Blocks =
    [
        (0x0590, 0x05FF, "Hebrew"),
        (0x0600, 0x06FF, "Arabic"),
        (0x0700, 0x074F, "Syriac"),
        (0x0750, 0x077F, "Arabic"),
        (0x0780, 0x07BF, "Thaana"),
        (0x07C0, 0x07FF, "N'Ko"),
        (0x0800, 0x083F, "Samaritan"),
        (0x0840, 0x085F, "Mandaic"),
        (0x0860, 0x086F, "Syriac"),
        (0x0870, 0x08FF, "Arabic"),
        (0x0900, 0x097F, "Devanagari"),
        (0x0980, 0x09FF, "Bengali"),
        (0x0A00, 0x0A7F, "Gurmukhi"),
        (0x0A80, 0x0AFF, "Gujarati"),
        (0x0B00, 0x0B7F, "Odia"),
        (0x0B80, 0x0BFF, "Tamil"),
        (0x0C00, 0x0C7F, "Telugu"),
        (0x0C80, 0x0CFF, "Kannada"),
        (0x0D00, 0x0D7F, "Malayalam"),
        (0x0D80, 0x0DFF, "Sinhala"),
        (0x0E00, 0x0E7F, "Thai"),
        (0x0E80, 0x0EFF, "Lao"),
        (0x0F00, 0x0FFF, "Tibetan"),
        (0x1000, 0x109F, "Myanmar"),
        (0x1780, 0x17FF, "Khmer"),
        (0x1800, 0x18AF, "Mongolian"),
        (0x1900, 0x194F, "Limbu"),
        (0x1950, 0x19DF, "Tai Le"),
        (0x19E0, 0x19FF, "Khmer"),
        (0x1A00, 0x1A1F, "Buginese"),
        (0x1A20, 0x1AAF, "Tai Tham"),
        (0x1B00, 0x1B7F, "Balinese"),
        (0x1B80, 0x1BBF, "Sundanese"),
        (0x1BC0, 0x1BFF, "Batak"),
        (0x1C00, 0x1C4F, "Lepcha"),
        (0xA800, 0xA82F, "Syloti Nagri"),
        (0xA880, 0xA8DF, "Saurashtra"),
        (0xA8E0, 0xA8FF, "Devanagari"),
        (0xA900, 0xA95F, "Kayah Li"),
        (0xA980, 0xA9DF, "Javanese"),
        (0xA9E0, 0xA9FF, "Myanmar"),
        (0xAA00, 0xAA5F, "Cham"),
        (0xAA60, 0xAA7F, "Myanmar"),
        (0xAA80, 0xAADF, "Tai Viet"),
        (0xAAE0, 0xAAFF, "Meetei Mayek"),
        (0xABC0, 0xABFF, "Meetei Mayek"),
        (0xFB1D, 0xFB4F, "Hebrew"),
        (0xFB50, 0xFDFF, "Arabic"),
        (0xFE70, 0xFEFF, "Arabic"),
        (0x10800, 0x10FFF, "right-to-left"),
        (0x11000, 0x11FFF, "Brahmic"),
    ];

    /// <summary>
    /// The code point ranges rhwp's width tables are generated for (src/tools/font_metric_gen.rs, rhwp#8143):
    /// ASCII, Latin-1, general punctuation, math, CJK symbols, Hangul jamo and syllables, and the fullwidth
    /// forms. A rhwp release that measures more is a one-line edit here.
    /// </summary>
    private static readonly (int Start, int End)[] MeasuredRanges =
    [
        (0x0020, 0x007E),
        (0x00A0, 0x00FF),
        (0x2000, 0x206F),
        (0x2200, 0x22FF),
        (0x3000, 0x303F),
        (0x3130, 0x318F),
        (0xAC00, 0xD7A3),
        (0xFF00, 0xFF5E),
    ];

    /// <summary>Blocks rhwp does not measure but lays out at a full em, which is right for them (rhwp#8143).</summary>
    private static readonly (int Start, int End)[] FullWidthCjk =
    [
        (0x1100, 0x11FF),
        (0x2E80, 0x2FDF),
        (0x3000, 0x303F),
        (0x3040, 0x30FF),
        (0x3100, 0x312F),
        (0x3130, 0x318F),
        (0x31A0, 0x31FF),
        (0x3200, 0x33FF),
        (0x3400, 0x4DBF),
        (0x4E00, 0x9FFF),
        (0xA960, 0xA97F),
        (0xAC00, 0xD7FF),
        (0xF900, 0xFAFF),
        (0xFF00, 0xFFEF),
        (0x20000, 0x3FFFF),
    ];

    /// <summary>
    /// Variation selectors: zero-width, default-ignorable marks (Mn) that only pick the glyph variant of the
    /// letter they follow — an emoji's text or picture form, a Mongolian letter's shape, an ideograph's
    /// registered variant. They carry no width of their own, so they must not reroute a measured document.
    /// </summary>
    private static readonly (int Start, int End)[] VariationSelectors =
    [
        (0x180B, 0x180D),
        (0xFE00, 0xFE0F),
        (0xE0100, 0xE01EF),
    ];

    /// <summary>Coarse names for the log line, by first code point like <see cref="Blocks"/>; else "U+XXXX".</summary>
    private static readonly (int Start, int End, string Name)[] UnmeasuredNames =
    [
        (0x0100, 0x024F, "Latin Extended"),
        (0x0250, 0x02AF, "IPA"),
        (0x0300, 0x036F, "combining marks"),
        (0x0370, 0x03FF, "Greek"),
        (0x0400, 0x052F, "Cyrillic"),
        (0x0530, 0x058F, "Armenian"),
        (0x10A0, 0x10FF, "Georgian"),
        (0x1C80, 0x1C8F, "Cyrillic"),
        (0x1E00, 0x1EFF, "Latin Extended"),
        (0x1F00, 0x1FFF, "Greek"),
        (0x2DE0, 0x2DFF, "Cyrillic"),
        (0xA640, 0xA69F, "Cyrillic"),
    ];

    /// <summary>The script of <paramref name="codePoint"/> when it needs shaping ("Arabic", "Gujarati"), else null.</summary>
    public static string? ScriptOf(int codePoint)
    {
        // Latin, Greek, Cyrillic, Armenian; CJK, Hangul and punctuation in between: the common case, answered at once.
        if (codePoint < 0x0590 || codePoint is >= 0x2000 and < 0xA800 or >= 0xAC00 and < 0xFB1D)
            return null;
        foreach (var (start, end, name) in Blocks)
        {
            if (codePoint < start)
                return null;
            if (codePoint <= end)
                return name;
        }
        return null;
    }

    /// <summary>
    /// The script of <paramref name="codePoint"/> when rhwp draws it but has no width for it ("Cyrillic"), else
    /// null: a letter or combining mark outside <see cref="MeasuredRanges"/> — decomposed e + ´ is drawn at the
    /// same half-em — that is no full-width CJK rhwp estimates at an em (<see cref="FullWidthCjk"/>) and no
    /// shaping script (that is the other scan's job). The name only reaches the log;
    /// <see cref="UnmeasuredNames"/> is intentionally coarse.
    /// </summary>
    private static string? UnmeasuredScriptOf(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF // a reference can name a value outside Unicode
            || Inside(VariationSelectors, codePoint)
            || CharUnicodeInfo.GetUnicodeCategory(codePoint)
                is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            || Inside(MeasuredRanges, codePoint)
            || Inside(FullWidthCjk, codePoint)
            || ScriptOf(codePoint) is not null)
            return null;
        foreach (var (start, end, name) in UnmeasuredNames)
        {
            if (codePoint < start)
                break;
            if (codePoint <= end)
                return name;
        }
        return $"U+{codePoint:X4}";
    }

    /// <summary>True when <paramref name="codePoint"/> is inside one of the sorted <paramref name="ranges"/>.</summary>
    private static bool Inside((int Start, int End)[] ranges, int codePoint)
    {
        foreach (var (start, end) in ranges)
        {
            if (codePoint < start)
                return false;
            if (codePoint <= end)
                return true;
        }
        return false;
    }

    public static bool IsComplex(int codePoint) => ScriptOf(codePoint) is not null;

    /// <summary>A letter of a right-to-left script (Hebrew, Arabic, Syriac, Thaana, N'Ko, ...).</summary>
    public static bool IsRightToLeft(int codePoint) =>
        codePoint is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
            or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

    /// <summary>
    /// The direction of <paramref name="text"/> by its letters: -1 when it has right-to-left letters and at least as many
    /// of them as left-to-right ones, 1 when it has left-to-right letters or digits only, 0 without either.
    /// </summary>
    public static int Direction(string text)
    {
        int rtl = 0, ltr = 0, digits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                codePoint = char.ConvertToUtf32(text[i], text[++i]);
            if (ComplexScripts.IsRightToLeft(codePoint))
            {
                // Arabic-Indic digits are numbers: they keep their left-to-right order within the right-to-left text.
                if (codePoint is >= 0x0660 and <= 0x0669 or >= 0x06F0 and <= 0x06F9)
                    digits++;
                else if (Rune.IsLetter(new Rune(codePoint)))
                    rtl++;
            }
            else if (Rune.IsLetter(new Rune(codePoint)))
            {
                ltr++;
            }
            else if (Rune.IsDigit(new Rune(codePoint)))
            {
                digits++;
            }
        }
        return rtl > 0 && rtl >= ltr ? -1 : ltr + digits > 0 ? 1 : 0;
    }

    /// <summary>The first script in <paramref name="text"/> that needs shaping, or null.</summary>
    public static string? Find(ReadOnlySpan<char> text) => Find(text, Shaping, Blocks[0].Start);

    /// <summary>
    /// Names the script of a code point, or null. <c>before</c> and <c>after</c> are the characters around it, or
    /// <see cref="Unknown"/> for a character reference.
    /// </summary>
    private delegate string? Classifier(int codePoint, int before, int after);

    /// <summary>Neighbour of a character reference: not known, so the reference counts as part of a word.</summary>
    private const int Unknown = -2;

    private static string? Shaping(int codePoint, int before, int after) => ScriptOf(codePoint);

    /// <summary>
    /// The first answer of <paramref name="classifier"/> in <paramref name="text"/>; code points below
    /// <paramref name="skipBelow"/> (the classifier's first block) are not asked about.
    /// </summary>
    private static string? Find(ReadOnlySpan<char> text, Classifier classifier, int skipBelow)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < skipBelow)
                continue;
            var before = i > 0 ? text[i - 1] : -1;
            var codePoint = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(c, text[++i])
                : c;
            var after = i + 1 < text.Length ? text[i + 1] : -1;
            if (classifier(codePoint, before, after) is { } script)
                return script;
        }
        return null;
    }

    /// <summary>True if <paramref name="text"/> has a character of a script that needs shaping.</summary>
    public static bool Contains(string text) => Find(text) is not null;

    /// <summary>
    /// The first script that needs shaping in the text of a document, or null (also for files that can't be read
    /// here: the conversion then reports what is wrong with them).
    /// </summary>
    /// <param name="format">Format id of the file (docx, txt, hwpx, ...).</param>
    public static string? FindInFile(string path, string format) => FindInFile(path, format, Shaping, Blocks[0].Start);

    /// <summary>
    /// The first script in the text of a document that rhwp draws but cannot measure (rhwp#8143), or null.
    /// </summary>
    /// <param name="format">Format id of the file (docx, txt, hwpx, ...).</param>
    public static string? FindUnmeasuredInFile(string path, string format) => FindBothInFile(path, format).Unmeasured;

    /// <summary>
    /// Both answers of the scan in one pass: the first script that needs shaping stops the scan and wins the
    /// file; the first script rhwp cannot measure is remembered while the scan runs on. Only letters inside words
    /// count: a symbol or unit on its own (α, Ω, μm in a Korean report) draws fine, and rhwp, the most faithful
    /// renderer for Korean documents, keeps them. An extended Latin letter or combining mark counts next to any letter
    /// (Polish, Vietnamese words); a letter of another script only next to one of the same script (Отчёт, Αναφορά).
    /// </summary>
    /// <param name="format">Format id of the file (docx, txt, hwpx, ...).</param>
    public static (string? Shaping, string? Unmeasured) FindBothInFile(string path, string format)
    {
        string? unmeasured = null;
        // No letter below 0x0100 is unmeasured: ASCII and Latin-1 are, controls are no letters.
        var shaping = FindInFile(path, format, Both, skipBelow: 0x0100);
        return (shaping, unmeasured);

        string? Both(int codePoint, int before, int after)
        {
            if (ScriptOf(codePoint) is { } script)
                return script;
            if (unmeasured is null && UnmeasuredScriptOf(codePoint) is { } name && InWord(name, before, after))
                unmeasured = name;
            return null;
        }

        static bool InWord(string name, int before, int after)
        {
            if (before == Unknown || after == Unknown)
                return true;
            return name is "Latin Extended" or "IPA" or "combining marks"
                ? IsLetter(before) || IsLetter(after)
                : (before >= 0 && UnmeasuredScriptOf(before) == name) || (after >= 0 && UnmeasuredScriptOf(after) == name);
        }

        static bool IsLetter(int c) => c >= 0 && char.IsLetter((char)c);
    }

    private static string? FindInFile(string path, string format, Classifier classifier, int skipBelow)
    {
        try
        {
            if (IsZip(path))
                return FindInPackage(path, format, classifier, skipBelow);
            // Excel 97–2003 is binary: its cells, as the built-in reader reads them (issue #345).
            if (format is "xls" or "xlt")
                return XlsReader.Read(path).Sheets.SelectMany(sheet => sheet.Rows.Values).SelectMany(cells => cells.Values)
                    .Select(cell => Find(cell.Text, classifier, skipBelow)).FirstOrDefault(script => script is not null);
            // Web archives and mail are MIME: their HTML (or text) part is what reaches the PDF.
            if (format is "mhtml" or "eml")
            {
                var message = MimeKit.MimeMessage.Load(path);
                return FindInString(message.HtmlBody ?? message.TextBody ?? "", classifier, skipBelow);
            }
            return format is "txt" or "md" or "html" or "csv" or "tsv" or "fb2" or "rtf" or "rst" or "tex" or "org" or "adoc"
                or "typ" or "mediawiki" or "dbk" or "textile" or "man" or "opml"
                ? FindInText(path, classifier, skipBelow)
                : null; // binary formats (HWP, DOC, MOBI, ...): left to the engines
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head is [0x50, 0x4B, 0x03, 0x04];
    }

    /// <summary>Text files are read whole (as the readers do), up to this size; a larger one is scanned in its first part.</summary>
    private const int MaxTextBytes = 64 * 1024 * 1024;

    private static string? FindInText(string path, Classifier classifier, int skipBelow)
    {
        byte[] bytes;
        using (var stream = File.OpenRead(path))
        {
            bytes = new byte[(int)Math.Min(stream.Length, MaxTextBytes)];
            stream.ReadExactly(bytes);
        }
        return FindInString(DocumentReaders.DecodeText(bytes), classifier, skipBelow);
    }

    private static string? FindInString(string text, Classifier classifier, int skipBelow)
    {
        if (Find(text, classifier, skipBelow) is { } script)
            return script;
        // &#2693; / &#x0A85; in HTML and XML, ⚓? in RTF.
        foreach (Match match in CharacterReference().Matches(text))
        {
            var value = match.Groups["hex"].Success
                ? int.Parse(match.Groups["hex"].ValueSpan, System.Globalization.NumberStyles.HexNumber, null)
                : int.Parse(match.Groups["dec"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0)
                value += 0x10000; // RTF writes code points above 32767 as negative numbers
            if (value is < 0 or > 0x10FFFF)
                continue; // a reference can name a value outside Unicode (&#1114112;, \u-99999): it is no script
            if (classifier(value, Unknown, Unknown) is { } referenced)
                return referenced;
        }
        return null;
    }

    [GeneratedRegex(@"&#(?:[xX](?<hex>[0-9a-fA-F]{1,6})|(?<dec>[0-9]{1,7}));|\\u(?<dec>-?[0-9]{1,5})", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterReference();

    /// <summary>The parts of a package that hold its text (not styles, themes or font tables, which name fonts per script).</summary>
    private static bool IsContentPart(string name, string format)
    {
        bool Xml(string folder, string prefix) =>
            name.StartsWith(folder + prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            && name.IndexOf('/', folder.Length) < 0;
        return format switch
        {
            "docx" or "docm" or "dotx" or "dotm" => Xml("word/", "document") || Xml("word/", "header") || Xml("word/", "footer")
                                                     || Xml("word/", "footnotes") || Xml("word/", "endnotes"),
            "pptx" or "pptm" or "potx" or "ppsx" => Xml("ppt/slides/", "slide"),
            "xlsx" or "xlsm" or "xltx" => Xml("xl/", "sharedStrings") || Xml("xl/worksheets/", "sheet"),
            "hwpx" => Xml("Contents/", "section"),
            "odt" or "ods" or "odp" => name.Equals("content.xml", StringComparison.OrdinalIgnoreCase),
            // OFD: the pages and templates (Doc_0/Pages/Page_0/Content.xml, Doc_0/Tpls/Tpl_0/Content.xml).
            "ofd" => name.EndsWith("/Content.xml", StringComparison.OrdinalIgnoreCase),
            // EPUB, HTMLZ, TXTZ, ...: the chapters.
            _ => !name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                 && (name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                     || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static string? FindInPackage(string path, string format, Classifier classifier, int skipBelow)
    {
        using var zip = ZipFile.OpenRead(path);
        var buffer = new char[64 * 1024];
        foreach (var entry in zip.Entries)
        {
            if (!IsContentPart(entry.FullName, format))
                continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                // A surrogate pair cut at the end of the buffer only hides a supplementary-plane letter there.
                if (Find(buffer.AsSpan(0, read), classifier, skipBelow) is { } script)
                    return script;
            }
        }
        return null;
    }
}
