// TOML: read with Tomlyn (BSD-2-Clause, TOML 1.1), written here so the layout is predictable: plain keys first, then
// [tables] and [[arrays of tables]] in document order, a blank line before each header. Dates and times keep their
// TOML spelling (JSON and YAML get them as text). TOML has no null and needs a table at the root: null values are left
// out, and anything that is not an object (an array of CSV rows, a single value) is written under the key "items".

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Filee.Engines.Data;

internal static partial class TomlData
{
    /// <summary>Key of the root table that holds a value that is not an object.</summary>
    public const string RootKey = "items";

    public static object? Read(string text)
    {
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new InvalidDataException($"Not valid TOML: {ex.Message}", ex);
        }
        return FromModel(table);
    }

    private static object? FromModel(object? value) => value switch
    {
        TomlTable table => Map(table),
        TomlTableArray tables => tables.Select(t => (object?)Map(t)).ToList(),
        TomlArray array => array.Select(FromModel).ToList(),
        TomlDateTime date => new DataDate(date.ToString()),
        int i => (long)i,
        float f => (double)f,
        _ => value,
    };

    private static DataMap Map(TomlTable table)
    {
        var map = new DataMap();
        foreach (var (key, value) in table)
            map[key] = FromModel(value);
        return map;
    }

    // ───────────────────────── Writing ─────────────────────────

    public static string Write(object? root)
    {
        var table = root as DataMap ?? new DataMap { [RootKey] = root };
        var sb = new StringBuilder();
        WriteTable(sb, [], table, 0);
        var text = sb.ToString().TrimStart('\n');
        return text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
    }

    private static void WriteTable(StringBuilder sb, List<string> path, DataMap table, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The data is nested too deeply.");

        // Keys with plain values first: after a [header] every key belongs to that table.
        var nested = new List<(string Key, object Value)>();
        foreach (var (key, value) in table)
        {
            switch (value)
            {
                case null:
                    break; // TOML has no null.
                case DataMap:
                    nested.Add((key, value));
                    break;
                case List<object?> list when list.Count > 0 && list.All(item => item is DataMap):
                    nested.Add((key, value));
                    break;
                default:
                    sb.Append(Key(key)).Append(" = ");
                    Inline(sb, value, depth + 1);
                    sb.Append('\n');
                    break;
            }
        }

        foreach (var (key, value) in nested)
        {
            List<string> child = [.. path, key];
            var header = string.Join('.', child.Select(Key));
            if (value is DataMap map)
            {
                sb.Append('\n').Append('[').Append(header).Append("]\n");
                WriteTable(sb, child, map, depth + 1);
            }
            else
            {
                foreach (var item in (List<object?>)value)
                {
                    sb.Append('\n').Append("[[").Append(header).Append("]]\n");
                    WriteTable(sb, child, (DataMap)item!, depth + 1);
                }
            }
        }
    }

    /// <summary>A value on one line: arrays as [a, b], objects as inline tables { a = 1 }.</summary>
    private static void Inline(StringBuilder sb, object? value, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The data is nested too deeply.");
        switch (value)
        {
            case DataMap map:
                var entries = map.Where(kv => kv.Value is not null).ToList();
                if (entries.Count == 0)
                {
                    sb.Append("{}");
                    break;
                }
                sb.Append("{ ");
                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(Key(entries[i].Key)).Append(" = ");
                    Inline(sb, entries[i].Value, depth + 1);
                }
                sb.Append(" }");
                break;
            case List<object?> list:
                sb.Append('[');
                var first = true;
                foreach (var item in list.Where(item => item is not null))
                {
                    if (!first)
                        sb.Append(", ");
                    first = false;
                    Inline(sb, item, depth + 1);
                }
                sb.Append(']');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(double.IsNaN(d) ? "nan" : double.IsPositiveInfinity(d) ? "inf" : double.IsNegativeInfinity(d) ? "-inf" : DataTree.FormatDouble(d));
                break;
            case DataDate date:
                sb.Append(date.Text);
                break;
            case BigInteger big:
                // TOML integers are 64-bit: a larger one is kept as text rather than rounded.
                Quote(sb, big.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                Quote(sb, DataTree.Text(value));
                break;
        }
    }

    private static string Key(string key)
    {
        if (BareKey().IsMatch(key))
            return key;
        var sb = new StringBuilder();
        Quote(sb, key);
        return sb.ToString();
    }

    /// <summary>A basic string: quotes, backslashes and control characters escaped.</summary>
    private static void Quote(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(ch))
                        sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex BareKey();
}
