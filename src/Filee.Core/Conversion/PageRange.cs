// Parses user page selections such as "1-3, 5, 8-" into zero-based page indices.

namespace Filee.Core.Conversion;

/// <summary>Page range parsing.</summary>
public static class PageRange
{
    /// <summary>
    /// Parses a 1-based page range expression. Empty or whitespace means every page.
    /// Supported forms: <c>"3"</c>, <c>"2-5"</c>, <c>"7-"</c> (to the end), <c>"-4"</c> (from the start), comma separated.
    /// Invalid parts are ignored; out-of-range pages are clamped away.
    /// </summary>
    /// <returns>Sorted, distinct zero-based page indices.</returns>
    public static IReadOnlyList<int> Parse(string? expression, int pageCount)
    {
        if (pageCount <= 0)
            return [];
        if (string.IsNullOrWhiteSpace(expression))
            return Enumerable.Range(0, pageCount).ToList();

        var pages = new SortedSet<int>();
        foreach (var raw in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            int start, end;
            if (dash < 0)
            {
                if (!int.TryParse(raw, out start))
                    continue;
                end = start;
            }
            else
            {
                var left = raw[..dash].Trim();
                var right = raw[(dash + 1)..].Trim();
                start = left.Length == 0 ? 1 : int.TryParse(left, out var l) ? l : -1;
                end = right.Length == 0 ? pageCount : int.TryParse(right, out var r) ? r : -1;
                if (start < 0 || end < 0)
                    continue;
            }

            if (start > end)
                (start, end) = (end, start);
            for (var p = Math.Max(1, start); p <= Math.Min(pageCount, end); p++)
                pages.Add(p - 1);
        }

        return pages.Count == 0 ? Enumerable.Range(0, pageCount).ToList() : pages.ToList();
    }
}
