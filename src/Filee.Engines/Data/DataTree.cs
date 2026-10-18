// The in-memory tree every data format is read into and written from, the common ground of JSON, YAML, TOML, XML and
// CSV: objects (DataMap, keys in file order), arrays (List<object?>) and scalars: string, bool, long, BigInteger,
// double, DataDate (a TOML date / time, kept as written) and null.

using System.Globalization;
using System.Numerics;

namespace Filee.Engines.Data;

/// <summary>An object: keys in the order the file has them; a repeated key keeps its first place and the last value.</summary>
internal sealed class DataMap() : OrderedDictionary<string, object?>(StringComparer.Ordinal);

/// <summary>A TOML date, time or date-time as written in the file (RFC 3339), e.g. <c>1979-05-27T07:32:00Z</c>.</summary>
internal sealed record DataDate(string Text)
{
    public override string ToString() => Text;
}

internal static class DataTree
{
    /// <summary>Deepest nesting read or written; deeper input is rejected instead of overflowing the stack.</summary>
    public const int MaxDepth = 256;

    /// <summary>A number in JSON syntax ("12", "-3.5", "1e3") as long, BigInteger or double.</summary>
    public static object Number(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (text.IndexOfAny(['.', 'e', 'E']) < 0 && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big))
            return big;
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A float as the shortest text that reads back the same, always with a fraction or exponent so it stays a float
    /// in YAML and TOML ("3.0", not "3"). Not for NaN or infinities, which every format spells differently.
    /// </summary>
    public static string FormatDouble(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'e']) < 0 ? text + ".0" : text;
    }

    /// <summary>Text of a scalar for formats without types (XML, CSV): "true", "12", "3.5", "" for null.</summary>
    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d when double.IsNaN(d) => "NaN",
        double d when double.IsPositiveInfinity(d) => "Infinity",
        double d when double.IsNegativeInfinity(d) => "-Infinity",
        double d => FormatDouble(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static bool IsScalar(object? value) => value is not (DataMap or List<object?>);

    /// <summary>
    /// The list of records a table-like target (CSV, JSON Lines) is made of: the root array, or the array inside
    /// objects that hold nothing else (<c>{"users": [...]}</c>, XML's <c>&lt;rows&gt;&lt;row/&gt;…&lt;/rows&gt;</c>).
    /// </summary>
    public static object? UnwrapRecords(object? root)
    {
        var node = root;
        for (var i = 0; i < MaxDepth && node is DataMap { Count: 1 } map; i++)
        {
            var only = map.GetAt(0).Value;
            // {"name": ["a", "b"]} is one column, not a wrapper around a list of values.
            if (only is DataMap || (only is List<object?> list && list.Any(item => item is DataMap)))
                node = only;
            else
                break;
        }
        return node;
    }
}
