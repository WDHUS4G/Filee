// Text of scripts that need shaping (Arabic, Hebrew, the Indic scripts, Thai, ...) for PdfDocumentReader.
//
// A PDF holds glyphs, not characters: a shaped cluster (a conjunct, a vowel sign drawn before its consonant, a letter
// with its marks) is one or several glyphs whose ToUnicode entries can't say the cluster's characters, and the glyphs of
// right-to-left text are drawn left to right. So:
//  * /ActualText of marked content (LibreOffice writes it for every cluster whose glyphs don't map one to one) replaces
//    the glyphs it covers, on every page.
//  * On a page with such text, words are built from the glyphs in content-stream order, not by their positions:
//    combining marks sit on or before their letter (zero width, overlapping), so sorting by position mixes them up, and
//    rhwp's PDFs (made by Filee before it routed these documents to LibreOffice) place them anywhere. Marks join the
//    letter before them in the stream; a vowel sign that is drawn before its consonant in the stream (Word's PDFs)
//    joins the consonant after it.
//  * A right-to-left word is read from right to left (its clusters by position), with numbers and Latin words in it
//    kept left to right; the words of a right-to-left line likewise (Runs), and such a paragraph is marked right to left.
// Not recovered: PDFs that draw a cluster as one glyph whose ToUnicode entry is wrong and give no ActualText.

using System.Globalization;
using System.Text;
using Filee.Engines.Infrastructure;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Filee.Engines.Pdf;

internal sealed partial class PdfDocumentReader
{
    /// <summary>A word of a page before it gets its position relative to the crop box and its formatting.</summary>
    private sealed record PageWord(string Text, PdfRectangle Box, IReadOnlyList<Letter> Letters, Letter Lead);

    /// <summary>An ActualText that covers more glyphs or lines than this is a label for a passage, not a cluster's text.</summary>
    private const int MaxActualTextLetters = 64;

    // ───────────────────────── ActualText ─────────────────────────

    /// <summary>
    /// The page's letters in content-stream order, the glyphs of each /ActualText span replaced by one letter with its
    /// text (over their joint box). Glyphs that stand for no text keep their place with an empty value (words still
    /// span them): control characters, and the dotted circles an engine draws before marks it can't attach (rhwp maps
    /// them to U+0002 or to a vowel sign, and draws them wider than any mark).
    /// </summary>
    private static List<Letter> ContentLetters(Page page)
    {
        var spans = ActualTextSpans(page);
        var letters = page.Letters;
        var result = new List<Letter>(letters.Count);
        var done = new HashSet<MarkedContentElement>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            if (spans is not null && spans.TryGetValue(letter, out var span))
            {
                if (done.Add(span.Element) && span.Text.Length > 0)
                    result.Add(Merged(span.Text, span.Letters));
                continue;
            }
            var value = letter.Value;
            var beforeMark = i + 1 < letters.Count && IsMark(letters[i + 1].Value) && OnSameBaseline(letter, letters[i + 1]);
            if ((value.Length > 0 && value.All(char.IsControl))
                || (beforeMark && (value == "◌" || (IsMark(value) && Math.Abs(letter.Width) >= 0.5 * Math.Max(1, LetterSize(letter))))))
            {
                result.Add(Blank(letter));
                continue;
            }
            result.Add(letter);
        }
        return result;
    }

    /// <summary>The glyph's place without its text.</summary>
    private static Letter Blank(Letter letter) =>
        new(string.Empty, letter.BoundingBox, letter.BoundingBox, letter.StartBaseLine, letter.EndBaseLine, letter.Width, letter.FontSize,
            letter.FontDetails, letter.RenderingMode, letter.StrokeColor, letter.FillColor, letter.PointSize, letter.TextSequence);

    /// <summary>Letters covered by an ActualText span (the outermost one) → the span; null when the page has none.</summary>
    private static Dictionary<Letter, (MarkedContentElement Element, string Text, List<Letter> Letters)>? ActualTextSpans(Page page)
    {
        IReadOnlyList<MarkedContentElement> marked;
        try
        {
            marked = page.GetMarkedContents();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return null; // broken marked content: the glyphs are read as they are
        }
        Dictionary<Letter, (MarkedContentElement, string, List<Letter>)>? spans = null;
        void Walk(IEnumerable<MarkedContentElement> elements, int depth)
        {
            foreach (var element in elements)
            {
                if (element.ActualText is { } text)
                {
                    var letters = new List<Letter>();
                    Collect(element, letters, 0);
                    if (letters.Count > 0 && letters.Count <= MaxActualTextLetters && OnOneLine(letters))
                    {
                        spans ??= new Dictionary<Letter, (MarkedContentElement, string, List<Letter>)>(ReferenceEqualityComparer.Instance);
                        // A space stays a space (a word boundary); other text loses the spaces around it.
                        var clean = text.Replace("\0", "", StringComparison.Ordinal);
                        clean = string.IsNullOrWhiteSpace(clean) ? (clean.Length > 0 ? " " : "") : clean.Trim();
                        foreach (var letter in letters)
                            spans.TryAdd(letter, (element, clean, letters));
                        continue;
                    }
                }
                if (depth < 32)
                    Walk(element.Children, depth + 1);
            }
        }
        Walk(marked, 0);
        return spans;

        static void Collect(MarkedContentElement element, List<Letter> letters, int depth)
        {
            letters.AddRange(element.Letters);
            if (depth < 32)
                foreach (var child in element.Children)
                    Collect(child, letters, depth + 1);
        }

        static bool OnOneLine(List<Letter> letters)
        {
            var y = letters[0].StartBaseLine.Y;
            var size = Math.Max(1, letters.Max(LetterSize));
            return letters.All(l => Math.Abs(l.StartBaseLine.Y - y) <= 0.5 * size);
        }
    }

    /// <summary>One letter standing for <paramref name="letters"/>: their joint box, the font and colour of the first.</summary>
    private static Letter Merged(string text, List<Letter> letters)
    {
        var first = letters[0];
        var box = new PdfRectangle(letters.Min(l => l.BoundingBox.Left), letters.Min(l => l.BoundingBox.Bottom),
            letters.Max(l => l.BoundingBox.Right), letters.Max(l => l.BoundingBox.Top));
        var y = first.StartBaseLine.Y;
        var start = new PdfPoint(letters.Min(l => Math.Min(l.StartBaseLine.X, l.EndBaseLine.X)), y);
        var end = new PdfPoint(letters.Max(l => Math.Max(l.StartBaseLine.X, l.EndBaseLine.X)), y);
        return new Letter(text, box, box, start, end, end.X - start.X, first.FontSize, first.FontDetails, first.RenderingMode,
            first.StrokeColor, first.FillColor, first.PointSize, first.TextSequence);
    }

    private static double LetterSize(Letter letter) => letter.PointSize > 0 ? letter.PointSize : letter.FontSize;

    // ───────────────────────── Words in content order ─────────────────────────

    /// <summary>A letter with the marks that belong to it.</summary>
    private sealed class Cluster(Letter lead)
    {
        public Letter Lead { get; } = lead;
        public List<Letter> Letters { get; } = [lead];
        public StringBuilder Text { get; } = new(lead.Value);
        public double Size { get; } = Math.Max(1, LetterSize(lead));
        public double Start => Lead.StartBaseLine.X;
        public double End { get; private set; } = lead.EndBaseLine.X;
        public double Center => (Lead.StartBaseLine.X + Lead.EndBaseLine.X) / 2;
        public double Baseline => Lead.StartBaseLine.Y;
        public bool IsSpace => string.IsNullOrWhiteSpace(Lead.Value);

        public void Add(Letter mark)
        {
            Letters.Add(mark);
            Text.Append(mark.Value);
            End = Math.Max(End, mark.EndBaseLine.X);
        }
    }

    /// <summary>True if any of <paramref name="letters"/> is of a script that needs shaping.</summary>
    private static bool HasComplexText(List<Letter> letters) => letters.Any(l => ComplexScripts.Contains(l.Value));

    /// <summary>
    /// The words of a page with text that needs shaping, from its letters in content-stream order (see the top of this
    /// file): marks joined to their letters, words split at spaces, line changes and gaps, and pieces that touch on
    /// a line (a full stop drawn by another text run) joined again.
    /// </summary>
    private static List<PageWord> ComplexWords(List<Letter> letters)
    {
        var horizontal = letters.Where(l => l.TextOrientation == TextOrientation.Horizontal).ToList();

        // Letters → clusters.
        var clusters = new List<Cluster>();
        Letter? pendingPreBase = null;
        for (var i = 0; i < horizontal.Count; i++)
        {
            var letter = horizontal[i];
            if (letter.Value.Length == 0)
            {
                // A glyph without text (a dotted circle): part of the word it is drawn in.
                if (clusters.Count > 0 && !clusters[^1].IsSpace && OnSameBaseline(clusters[^1].Lead, letter))
                    clusters[^1].Add(letter);
                continue;
            }
            if (IsMark(letter.Value))
            {
                // Drawn before the letter that follows it (Word's PDFs keep the visual order of a vowel sign written
                // before its consonant): it belongs to that letter.
                if (IsPreBase(letter.Value) && i + 1 < horizontal.Count && !IsMark(horizontal[i + 1].Value)
                    && OnSameBaseline(letter, horizontal[i + 1]) && horizontal[i + 1].StartBaseLine.X >= letter.EndBaseLine.X - 0.15 * Math.Max(1, LetterSize(letter)))
                {
                    pendingPreBase = letter;
                    continue;
                }
                if (clusters.Count > 0 && OnSameBaseline(clusters[^1].Lead, letter) && !clusters[^1].IsSpace)
                {
                    clusters[^1].Add(letter);
                    continue;
                }
            }
            var cluster = new Cluster(letter);
            if (pendingPreBase is not null)
            {
                cluster.Add(pendingPreBase);
                pendingPreBase = null;
            }
            clusters.Add(cluster);
        }

        // Clusters → words, in stream order.
        var words = new List<List<Cluster>>();
        List<Cluster>? current = null;
        foreach (var cluster in clusters)
        {
            if (cluster.IsSpace)
            {
                current = null;
                continue;
            }
            if (current is not null)
            {
                var previous = current[^1];
                var size = Math.Min(cluster.Size, previous.Size);
                if (!OnSameBaseline(previous.Lead, cluster.Lead) || cluster.Start > previous.End + 0.25 * size || cluster.Start < previous.Start - 0.1 * size)
                    current = null;
            }
            if (current is null)
                words.Add(current = []);
            current.Add(cluster);
        }

        // Pieces of one word drawn apart in the stream (a run per direction, a full stop in another font) join again
        // where they touch.
        var rows = new List<List<List<Cluster>>>();
        foreach (var word in words)
        {
            var row = rows.FirstOrDefault(r => OnSameBaseline(r[0][0].Lead, word[0].Lead));
            if (row is null)
                rows.Add(row = []);
            row.Add(word);
        }
        var result = new List<PageWord>(words.Count);
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.Min(c => c.Start).CompareTo(b.Min(c => c.Start)));
            var joined = new List<Cluster>(row[0]);
            for (var w = 1; w <= row.Count; w++)
            {
                if (w < row.Count)
                {
                    var right = joined.Max(c => c.End);
                    var left = row[w].Min(c => c.Start);
                    if (left - right < 0.1 * Math.Min(joined[^1].Size, row[w][0].Size))
                    {
                        joined.AddRange(row[w]);
                        continue;
                    }
                }
                result.Add(Word(joined));
                if (w < row.Count)
                    joined = [.. row[w]];
            }
        }
        return result;
    }

    /// <summary>A word of clusters: its characters in logical order and its box.</summary>
    private static PageWord Word(List<Cluster> clusters)
    {
        var rtl = ComplexScripts.Direction(string.Concat(clusters.Select(c => c.Text.ToString()))) < 0;
        IEnumerable<Cluster> ordered = rtl
            ? LogicalOrder(clusters.OrderByDescending(c => c.Center).ToList(), c => ComplexScripts.Direction(c.Text.ToString()), rightToLeft: true)
            : clusters.OrderBy(c => c.Start);
        var text = string.Concat(ordered.Select(c => c.Text.ToString()));
        var letters = clusters.SelectMany(c => c.Letters).ToList();
        var box = new PdfRectangle(letters.Min(l => l.BoundingBox.Left), letters.Min(l => l.BoundingBox.Bottom),
            letters.Max(l => l.BoundingBox.Right), letters.Max(l => l.BoundingBox.Top));
        return new PageWord(text, box, letters, clusters.OrderBy(c => c.Start).First().Lead);
    }

    private static bool OnSameBaseline(Letter a, Letter b) =>
        Math.Abs(a.StartBaseLine.Y - b.StartBaseLine.Y) <= 0.3 * Math.Max(1, Math.Min(LetterSize(a), LetterSize(b)));

    /// <summary>Combining marks only (vowel signs, viramas, nuktas, Arabic harakat, Thai tone marks, joiners).</summary>
    private static bool IsMark(string value) =>
        value.Length > 0 && value.All(c => c is '\u200C' or '\u200D'
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

    /// <summary>Vowel signs written after their consonant but drawn before it.</summary>
    private static bool IsPreBase(string value) => value.Length == 1 && value[0] is '\u093F' or '\u094E' or '\u09BF' or '\u09C7' or '\u09C8'
        or '\u0A3F' or '\u0ABF' or '\u0B47' or '\u0BC6' or '\u0BC7' or '\u0BC8' or '\u0D46' or '\u0D47' or '\u0D48' or '\u0DD9' or '\u0DDA'
        or '\u0DDB' or '\u1031' or '\u17C1' or '\u17C2' or '\u17C3';

    // ───────────────────────── Direction ─────────────────────────

    /// <summary>
    /// Items read in their visual order (left to right, or right to left for <paramref name="rightToLeft"/>) put in
    /// logical order: runs of the other direction (Latin words and numbers in Arabic text, Arabic words in English text)
    /// are turned around, also over neutral items between them ("10, 20").
    /// </summary>
    private static List<T> LogicalOrder<T>(List<T> visual, Func<T, int> direction, bool rightToLeft)
    {
        var other = rightToLeft ? 1 : -1;
        var result = new List<T>(visual);
        for (var i = 0; i < result.Count;)
        {
            if (direction(result[i]) != other)
            {
                i++;
                continue;
            }
            var end = i;
            for (var j = i + 1; j < result.Count; j++)
            {
                var d = direction(result[j]);
                if (d == other)
                    end = j;
                else if (d != 0)
                    break;
            }
            result.Reverse(i, end - i + 1);
            i = end + 1;
        }
        return result;
    }
}
