// Walks the HDocument model: every inline of a block list, including those inside tables, notes, text boxes and
// links. Used by readers (bookmark pruning) and the writers that are not HWPX (XHTML, plain text, Markdown, FB2).

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class HDocumentWalker
{
    /// <summary>Every inline, depth first: the content of links, notes and text boxes follows them.</summary>
    public static IEnumerable<HInline> Inlines(IEnumerable<HBlock> blocks) =>
        InlineLists(blocks).SelectMany(list => list);

    /// <summary>Every inline list (paragraph, link content, note and text box paragraphs, table cells).</summary>
    public static IEnumerable<List<HInline>> InlineLists(IEnumerable<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    foreach (var list in InlineLists(paragraph.Inlines))
                        yield return list;
                    break;
                case HTable table:
                    foreach (var list in InlineLists(table.Caption))
                        yield return list;
                    foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                        foreach (var list in InlineLists(cell.Blocks))
                            yield return list;
                    break;
            }
        }
    }

    private static IEnumerable<List<HInline>> InlineLists(List<HInline> inlines)
    {
        yield return inlines;
        foreach (var inline in inlines.ToList())
        {
            var nested = inline switch
            {
                HLink link => InlineLists(link.Content),
                HNote note => InlineLists(note.Blocks),
                HTextBox box => InlineLists(box.Blocks),
                _ => [],
            };
            foreach (var list in nested)
                yield return list;
        }
    }

    /// <summary>The inlines of one paragraph with the content of its links (not notes or text boxes).</summary>
    public static IEnumerable<HInline> InlinesOf(IEnumerable<HInline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is HLink link)
                foreach (var child in InlinesOf(link.Content))
                    yield return child;
        }
    }

    /// <summary>Every picture of the document, in reading order.</summary>
    public static IEnumerable<HImage> Images(HDocument document) =>
        document.Sections.SelectMany(s => Inlines(s.Blocks)).OfType<HImage>();

    /// <summary>The visible text of a paragraph's inlines (links included, notes left out), line breaks as spaces.</summary>
    public static string Text(IEnumerable<HInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in InlinesOf(inlines))
        {
            switch (inline)
            {
                case HText text:
                    sb.Append(text.Text);
                    break;
                case HLineBreak or HTab:
                    sb.Append(' ');
                    break;
            }
        }
        return sb.ToString().Trim();
    }
}
