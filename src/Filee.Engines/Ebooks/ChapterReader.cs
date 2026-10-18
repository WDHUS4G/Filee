// Reads the XHTML chapters of a book (EPUB spine, KF8 parts, HTMLZ page) into one HDocument: every chapter starts
// on a new page, and links between chapters ("ch2.xhtml#note3") become links to bookmarks inside the document.

using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class ChapterReader
{
    /// <summary>Reads chapter files in reading order.</summary>
    /// <param name="mediaFolder">Scratch folder for images embedded as data: URIs.</param>
    public static HDocument Read(IReadOnlyList<string> chapters, string mediaFolder, CancellationToken cancellationToken = default)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chapters.Count; i++)
            index.TryAdd(Path.GetFullPath(chapters[i]), i);

        var section = new HSection();
        var document = new HDocument();
        document.Sections.Add(section);
        for (var i = 0; i < chapters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = Path.GetFullPath(chapters[i]);
            if (!File.Exists(chapter))
                continue; // a spine item missing from the package: skip it like reading systems do
            var number = i;
            var content = HtmlReader.ReadFile(chapter, new HtmlReadOptions
            {
                BaseFolder = Path.GetDirectoryName(chapter)!,
                MediaFolder = mediaFolder,
                MapId = id => Anchor(number, id),
                MapLink = href => Link(href, chapter, number, index),
            });
            document.Title ??= content.Title;
            if (!content.Blocks.Any(HasContent))
                continue;

            // The chapter starts on a new page and carries a bookmark for links to the chapter file itself.
            if (content.Blocks[0] is not HParagraph first)
                content.Blocks.Insert(0, first = new HParagraph());
            first.PageBreakBefore = section.Blocks.Count > 0;
            first.Inlines.Insert(0, new HBookmark(Anchor(number, null)));
            section.Blocks.AddRange(content.Blocks);
        }
        HtmlReader.PruneBookmarks(document);
        return document;
    }

    private static bool HasContent(HBlock block) => block is HTable
        || HDocumentWalker.Inlines([block]).Any(i => i is HImage || i is HText { Text: var text } && !string.IsNullOrWhiteSpace(text));

    /// <summary>Bookmark name of an element id (or the chapter start) in chapter <paramref name="chapter"/>.</summary>
    private static string Anchor(int chapter, string? id)
    {
        if (id is null)
            return $"c{chapter + 1}";
        var clean = new char[id.Length];
        for (var i = 0; i < id.Length; i++)
            clean[i] = char.IsAsciiLetterOrDigit(id[i]) || id[i] is '-' or '_' ? id[i] : '_';
        return $"c{chapter + 1}-{new string(clean)}";
    }

    /// <summary>Links inside the book point at bookmarks; web and mail links stay; links to other files are dropped.</summary>
    private static string? Link(string href, string chapter, int number, Dictionary<string, int> index)
    {
        if (href.StartsWith('#'))
            return href.Length > 1 ? "#" + Anchor(number, Uri.UnescapeDataString(href[1..])) : null;
        var colon = href.IndexOf(':');
        if (colon > 1 && href[..colon].All(char.IsAsciiLetter))
            return href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ? null : href;

        var hash = href.IndexOf('#');
        var file = hash >= 0 ? href[..hash] : href;
        var fragment = hash >= 0 ? Uri.UnescapeDataString(href[(hash + 1)..]) : null;
        var query = file.IndexOf('?');
        if (query >= 0)
            file = file[..query];
        try
        {
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chapter)!, Uri.UnescapeDataString(file)));
            return index.TryGetValue(target, out var chapterIndex)
                ? "#" + Anchor(chapterIndex, string.IsNullOrEmpty(fragment) ? null : fragment)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
