// JSON and JSON Lines with System.Text.Json. Reading allows comments and trailing commas (JSONC, as in settings files);
// writing is indented by two spaces, keeps non-ASCII text as it is (Korean, Chinese) and ends with a line break.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Filee.Engines.Data;

internal static class JsonData
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = DataTree.MaxDepth,
    };

    public static object? Read(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, ReadOptions);
            return FromElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>JSON Lines: one value per line (blank lines skipped), read as an array.</summary>
    public static List<object?> ReadLines(string text)
    {
        var values = new List<object?>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            try
            {
                using var document = JsonDocument.Parse(line, ReadOptions);
                values.Add(FromElement(document.RootElement));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Line {i + 1} is not valid JSON: {ex.Message}", ex);
            }
        }
        return values;
    }

    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new DataMap();
                foreach (var property in element.EnumerateObject())
                    map[property.Name] = FromElement(property.Value);
                return map;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromElement).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return DataTree.Number(element.GetRawText());
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>Indented JSON (two spaces, "\n").</summary>
    public static string Write(object? root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options(indented: true)))
            WriteValue(writer, root, 0);
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// JSON Lines: one compact line per record (see <see cref="DataTree.UnwrapRecords"/>); anything else becomes one
    /// line.
    /// </summary>
    public static string WriteLines(object? root)
    {
        var records = DataTree.UnwrapRecords(root) is List<object?> list ? list : [root];
        var sb = new StringBuilder();
        foreach (var record in records)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, Options(indented: false)))
                WriteValue(writer, record, 0);
            sb.Append(Encoding.UTF8.GetString(buffer.ToArray())).Append('\n');
        }
        return sb.ToString();
    }

    private static JsonWriterOptions Options(bool indented) => new()
    {
        Indented = indented,
        IndentSize = 2,
        NewLine = "\n",
        // Readable text: only what JSON requires is escaped (quotes, backslashes, control characters).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = DataTree.MaxDepth + 1,
    };

    /// <summary>
    /// A big integer or a float ("3.0" stays a float). A decimal keeps its digits and scale and is indented like any
    /// value; raw values are not, so they are only the fallback for numbers beyond decimal's range (1e300).
    /// </summary>
    private static void WriteNumber(Utf8JsonWriter writer, string text, bool isFloat)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && (number != 0 || !text.Contains('e', StringComparison.OrdinalIgnoreCase)))
            writer.WriteNumberValue(isFloat && number.Scale == 0 ? number * 1.0m : number);
        else
            writer.WriteRawValue(text);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The data is nested too deeply.");
        switch (value)
        {
            case DataMap map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case BigInteger big:
                WriteNumber(writer, big.ToString(CultureInfo.InvariantCulture), isFloat: false);
                break;
            case double d when double.IsFinite(d):
                WriteNumber(writer, DataTree.FormatDouble(d), isFloat: true);
                break;
            default:
                // Strings, TOML dates, and NaN / infinities, which JSON has no number for.
                writer.WriteStringValue(DataTree.Text(value));
                break;
        }
    }
}
