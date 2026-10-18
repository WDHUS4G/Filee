// Plain text → HDocument for e-books: paragraphs are separated by empty lines, and hard-wrapped lines inside a
// paragraph (Project Gutenberg style, ~70 characters) are joined. Text without empty lines keeps one paragraph per
// line. (TXT → HWPX keeps every line as it is, see HwpxConverter.TextDocument.)

using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class PlainText
{
    public static HDocument ToDocument(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        var hasEmptyLines = lines.Any(l => l.Trim().Length == 0);
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 || !hasEmptyLines)
            {
                if (current.Count > 0)
                    paragraphs.Add(current);
                current = [];
                if (line.Trim().Length == 0)
                    continue;
            }
            current.Add(line.TrimEnd());
        }
        if (current.Count > 0)
            paragraphs.Add(current);

        // Hard-wrapped text: most lines of multi-line paragraphs are long and of similar length.
        var multiLine = paragraphs.Where(p => p.Count > 1).SelectMany(p => p.Take(p.Count - 1)).ToList();
        var wrapped = multiLine.Count > 0 && multiLine.Count(l => l.Length is >= 50 and <= 100) > multiLine.Count * 0.7;

        var section = new HSection();
        foreach (var paragraph in paragraphs)
        {
            var hParagraph = new HParagraph();
            for (var i = 0; i < paragraph.Count; i++)
            {
                if (i > 0)
                {
                    if (wrapped)
                        hParagraph.Inlines.Add(new HText(" ", default));
                    else
                        hParagraph.Inlines.Add(new HLineBreak(default));
                }
                hParagraph.Inlines.Add(new HText(i > 0 && wrapped ? paragraph[i].TrimStart() : paragraph[i], default));
            }
            HtmlReader.MergeRuns(hParagraph.Inlines);
            section.Blocks.Add(hParagraph);
        }
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }
}
