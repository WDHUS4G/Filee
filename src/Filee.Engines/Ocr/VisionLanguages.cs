// Which languages of the preset (PdfOptions.OcrLanguage, a Tesseract code from OcrLanguages) Apple Vision reads, as
// the language identifiers Vision takes. Vision says at run time which it reads (more with each macOS version); a
// language it doesn't is read by Tesseract instead (VisionConverter).

namespace Filee.Engines.Ocr;

internal static class VisionLanguages
{
    /// <summary>Tesseract code → Vision identifiers it may go by, the usual one first.</summary>
    private static readonly Dictionary<string, string[]> ByCode = new()
    {
        ["eng"] = ["en-US"],
        ["deu"] = ["de-DE"],
        ["fra"] = ["fr-FR"],
        ["spa"] = ["es-ES"],
        ["por"] = ["pt-BR"],
        ["ita"] = ["it-IT"],
        ["nld"] = ["nl-NL"],
        ["pol"] = ["pl-PL"],
        ["ces"] = ["cs-CZ"],
        ["ron"] = ["ro-RO"],
        ["swe"] = ["sv-SE"],
        ["dan"] = ["da-DK"],
        ["nor"] = ["nb-NO", "no-NO"],
        ["tur"] = ["tr-TR"],
        ["ind"] = ["id-ID"],
        ["msa"] = ["ms-MY"],
        ["rus"] = ["ru-RU"],
        ["ukr"] = ["uk-UA"],
        ["ara"] = ["ar-SA"],
        ["tha"] = ["th-TH"],
        ["vie"] = ["vi-VT", "vi-VN"],
        ["kor"] = ["ko-KR"],
        ["jpn"] = ["ja-JP"],
        ["chi_sim"] = ["zh-Hans"],
        ["chi_tra"] = ["zh-Hant"],
    };

    /// <summary>The languages Vision reads on every macOS Filee runs on (13 and later), when it can't be asked.</summary>
    internal static readonly IReadOnlyList<string> Fallback =
        ["en-US", "fr-FR", "it-IT", "de-DE", "es-ES", "pt-BR", "zh-Hans", "zh-Hant", "ko-KR", "ja-JP", "ru-RU", "uk-UA"];

    /// <summary>
    /// The languages to ask Vision for when the text is in <paramref name="code"/>: that language, then English
    /// (scans in other languages often have English words in them), or null when Vision doesn't read it.
    /// </summary>
    /// <param name="supported">What this Mac's Vision reads (<see cref="ITextRecognizer.Languages"/>).</param>
    public static IReadOnlyList<string>? For(string code, IReadOnlyCollection<string> supported)
    {
        if (!ByCode.TryGetValue(code, out var candidates))
            return null;
        var match = candidates.Select(c => supported.FirstOrDefault(s => string.Equals(s, c, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(s => s is not null);
        if (match is null)
            return null;
        var english = supported.FirstOrDefault(s => string.Equals(s, "en-US", StringComparison.OrdinalIgnoreCase));
        return english is null || english == match ? [match] : [match, english];
    }
}
