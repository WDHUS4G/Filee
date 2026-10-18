// YAML with YamlDotNet (MIT). Reading follows the YAML 1.2 core schema: unquoted true / false, null / ~, numbers
// (also 0x / 0o, .inf, .nan) get their type, everything else (and anything quoted) is text; "yes" / "no" stay text as
// in YAML 1.2. Anchors and aliases are resolved (the value is copied wherever it is referenced), merge keys ("<<")
// are applied, comments are dropped, and a file of several documents ("---") becomes an array of them.
// Writing uses block style indented by two spaces; text that would read back as another type ("true", "12", "") is
// quoted, multi-line text becomes a literal block ("|").

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Filee.Engines.Data;

internal static partial class YamlData
{
    /// <summary>Most values an alias may expand to ("billion laughs" files), far above any real document.</summary>
    private const int MaxValues = 5_000_000;

    public static object? Read(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"Not valid YAML: {ex.Message}", ex);
        }
        var reader = new Reader();
        return stream.Documents.Count switch
        {
            0 => null,
            1 => reader.Value(stream.Documents[0].RootNode, 0),
            _ => stream.Documents.Select(d => reader.Value(d.RootNode, 0)).ToList(),
        };
    }

    private sealed class Reader
    {
        private int _values;

        public object? Value(YamlNode node, int depth)
        {
            if (depth > DataTree.MaxDepth)
                throw new InvalidDataException("The YAML is nested too deeply (or an alias refers to itself).");
            if (++_values > MaxValues)
                throw new InvalidDataException("The YAML expands to too many values (aliases repeated too often).");
            return node switch
            {
                YamlMappingNode mapping => Mapping(mapping, depth),
                YamlSequenceNode sequence => sequence.Children.Select(child => Value(child, depth + 1)).ToList(),
                YamlScalarNode scalar => Scalar(scalar),
                _ => null,
            };
        }

        private DataMap Mapping(YamlMappingNode mapping, int depth)
        {
            var map = new DataMap();
            foreach (var (keyNode, valueNode) in mapping.Children)
            {
                if (keyNode is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain or ScalarStyle.Any })
                {
                    // Merge key: the merged mappings' entries, unless this mapping sets them itself (earlier or later).
                    var sources = valueNode is YamlSequenceNode many ? many.Children.ToList() : [valueNode];
                    foreach (var source in sources)
                    {
                        if (Value(source, depth + 1) is not DataMap merged)
                            throw new InvalidDataException("A YAML merge key (<<) must refer to a mapping.");
                        foreach (var (key, value) in merged)
                        {
                            if (!map.ContainsKey(key) && !HasKey(mapping, key))
                                map[key] = value;
                        }
                    }
                    continue;
                }
                map[Key(keyNode, depth)] = Value(valueNode, depth + 1);
            }
            return map;
        }

        private static bool HasKey(YamlMappingNode mapping, string key) =>
            mapping.Children.Keys.Any(k => k is YamlScalarNode { Value: var v } && v == key && v != "<<");

        /// <summary>Keys are text as written ("1", "null"); a complex key (a list or mapping, rare) is written as JSON.</summary>
        private string Key(YamlNode node, int depth) =>
            node is YamlScalarNode scalar ? scalar.Value ?? "" : JsonData.Write(Value(node, depth + 1)).Trim();
    }

    private static object? Scalar(YamlScalarNode node)
    {
        var text = node.Value ?? "";
        var tag = node.Tag.IsEmpty ? "" : node.Tag.Value;
        switch (tag)
        {
            case "tag:yaml.org,2002:str":
                return text;
            case "tag:yaml.org,2002:null":
                return null;
            case "tag:yaml.org,2002:bool":
                return Resolve(text) is bool b ? b : throw new InvalidDataException($"“{text}” is not a YAML boolean.");
            case "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float":
                return Resolve(text) is { } number and not (string or bool) ? number
                    : throw new InvalidDataException($"“{text}” is not a YAML number.");
        }
        // Quoted and block text is always text; only plain scalars are typed.
        return node.Style is ScalarStyle.Plain or ScalarStyle.Any && tag is "" or "?" ? Resolve(text) : text;
    }

    /// <summary>The YAML 1.2 core schema type of a plain scalar.</summary>
    private static object? Resolve(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return true;
            case "false" or "False" or "FALSE":
                return false;
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return double.PositiveInfinity;
            case "-.inf" or "-.Inf" or "-.INF":
                return double.NegativeInfinity;
            case ".nan" or ".NaN" or ".NAN":
                return double.NaN;
        }
        if (DecimalInteger().IsMatch(text))
            return DataTree.Number(text.TrimStart('+'));
        if (text.StartsWith("0x", StringComparison.Ordinal) && HexInteger().IsMatch(text))
            return Small(BigInteger.Parse("0" + text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        if (text.StartsWith("0o", StringComparison.Ordinal) && OctalInteger().IsMatch(text))
            return Small(text[2..].Aggregate(BigInteger.Zero, (n, digit) => n * 8 + (digit - '0')));
        if (Float().IsMatch(text))
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return text;
    }

    private static object Small(BigInteger value) => value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;

    /// <summary>True when a plain scalar with this text would read back as something other than the same text.</summary>
    private static bool NeedsQuotes(string text) =>
        Resolve(text) is not string
        // YAML 1.1 readers (PyYAML, older tools) take these as booleans, numbers with "_" or dates.
        || YamlOneOne().IsMatch(text)
        || text.Trim() != text;

    // ───────────────────────── Writing ─────────────────────────

    public static string Write(object? root)
    {
        var output = new StringWriter { NewLine = "\n" };
        var settings = new EmitterSettings()
            .WithBestIndent(2)
            .WithIndentedSequences()
            .WithBestWidth(int.MaxValue)
            .WithNewLine("\n");
        var emitter = new Emitter(output, settings);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        Emit(emitter, root, 0);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return output.ToString();
    }

    private static void Emit(IEmitter emitter, object? value, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The data is nested too deeply.");
        switch (value)
        {
            case DataMap map:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, map.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, item) in map)
                {
                    emitter.Emit(Text(key, multiLine: false));
                    Emit(emitter, item, depth + 1);
                }
                emitter.Emit(new MappingEnd());
                break;
            case List<object?> list:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, list.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in list)
                    Emit(emitter, item, depth + 1);
                emitter.Emit(new SequenceEnd());
                break;
            case string text:
                emitter.Emit(Text(text, multiLine: true));
                break;
            default:
                emitter.Emit(Plain(value switch
                {
                    null => "null",
                    double d when double.IsNaN(d) => ".nan",
                    double d when double.IsPositiveInfinity(d) => ".inf",
                    double d when double.IsNegativeInfinity(d) => "-.inf",
                    _ => DataTree.Text(value),
                }));
                break;
        }
    }

    private static Scalar Plain(string text) => new(AnchorName.Empty, TagName.Empty, text, ScalarStyle.Plain, true, false);

    private static Scalar Text(string text, bool multiLine)
    {
        var style = multiLine && text.Contains('\n') ? ScalarStyle.Literal
            : NeedsQuotes(text) ? ScalarStyle.DoubleQuoted
            : ScalarStyle.Any;
        return new Scalar(AnchorName.Empty, TagName.Empty, text, style, style == ScalarStyle.Any, true);
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex("^0x[0-9a-fA-F]+$")]
    private static partial Regex HexInteger();

    [GeneratedRegex("^0o[0-7]+$")]
    private static partial Regex OctalInteger();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|on|On|ON|off|Off|OFF|[-+]?[0-9][0-9_]*(\.[0-9_]*)?|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([Tt ].*)?|[0-9]+(:[0-5]?[0-9])+(\.[0-9_]*)?)$")]
    private static partial Regex YamlOneOne();
}
