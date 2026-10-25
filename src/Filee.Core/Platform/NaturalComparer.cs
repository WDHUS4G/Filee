// Orders names the way people count: comic book pages, files to merge.

namespace Filee.Core.Platform;

/// <summary>
/// Compares names the way people number pages and files: digit runs by value ("p2" &lt; "p10"), case-insensitively.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.CompareOrdinal(x, y);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;
                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byValue = numberX.Length != numberY.Length ? numberX.Length.CompareTo(numberY.Length) : string.CompareOrdinal(numberX, numberY);
                if (byValue != 0)
                    return byValue;
                continue;
            }
            var byChar = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (byChar != 0)
                return byChar;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
