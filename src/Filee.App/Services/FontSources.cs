// Trusted places offered for fonts a document references but the computer doesn't have. Filee never downloads an
// arbitrary search result: open/free families go to Google Fonts, and proprietary Microsoft families to Microsoft's
// own typography pages, where the user can see how they are licensed and supplied.

namespace Filee.App.Services;

public static class FontSources
{
    private static readonly IReadOnlyDictionary<string, string> Microsoft =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] = "arial",
            ["Calibri"] = "calibri",
            ["Calibri Light"] = "calibri",
            ["Cambria"] = "cambria",
            ["Cambria Math"] = "cambria-math",
            ["Courier New"] = "courier-new",
            ["Tahoma"] = "tahoma",
            ["Times New Roman"] = "times-new-roman",
        };

    private static readonly IReadOnlyDictionary<string, string> OtherOfficial =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Helvetica Neue"] = "https://support.apple.com/en-us/108939",
            ["Lohit Devanagari"] = "https://github.com/pravins/lohit",
        };

    private static readonly HashSet<string> NoPublicFontPage = new(StringComparer.OrdinalIgnoreCase)
    {
        "맑은 고딕", "Malgun Gothic", "함초롬바탕", "HCR Batang", "함초롬돋움", "HCR Dotum",
        "MS Mincho", "MS Gothic", "MS PMincho", "MS PGothic", "Yu Mincho", "Yu Gothic",
        "Wingdings", "Wingdings 2", "Wingdings 3", "Webdings", "Symbol", "OpenSymbol",
    };

    public static Uri? Url(string family) => NoPublicFontPage.Contains(family) ? null
        : Microsoft.TryGetValue(family, out var page)
        ? new Uri("https://learn.microsoft.com/en-us/typography/font-list/" + page)
        : OtherOfficial.TryGetValue(family, out var official) ? new Uri(official)
        : new Uri("https://fonts.google.com/?query=" + Uri.EscapeDataString(family));
}
