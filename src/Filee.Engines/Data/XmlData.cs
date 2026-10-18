// XML ↔ the data tree with System.Xml, in the widespread "@attribute / #text" convention (as xmltodict and Json.NET):
// - the root element becomes an object with one key, its name;
// - an element with only text becomes that text; an empty element (<a/>) becomes null, <a></a> empty text;
// - attributes become "@name" keys, text next to attributes or child elements becomes "#text" (mixed content is
//   joined into one text); child elements become keys, and repeated ones an array (a single child is no array, so
//   the shape of one row can differ from several);
// - XML has no types: values read are text. Comments, processing instructions and the DTD are dropped; external
//   entities are never loaded.
// Writing does the reverse. A root that is not one object with one key goes into <root>, array items without a name
// into <item>; names that are not valid XML names are escaped as XmlConvert does ("first name" → first_x0020_name)
// and unescaped when read back. Prefixed names ("x:a") keep their namespace when an "@xmlns:x" key declares it.

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Data;

internal static class XmlData
{
    public const string RootName = "root";
    public const string ItemName = "item";
    public const string TextKey = "#text";

    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    public static object? Read(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 0,
        };
        XDocument document;
        try
        {
            // The reader picks the encoding from the BOM or the declaration (<?xml encoding="EUC-KR"?>).
            using var reader = XmlReader.Create(path, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Not valid XML: {ex.Message}", ex);
        }
        var root = document.Root ?? throw new InvalidDataException("The XML file has no root element.");
        return new DataMap { [Name(root)] = Element(root, 0) };
    }

    private static object? Element(XElement element, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The XML is nested too deeply.");
        var attributes = element.Attributes().ToList();
        var children = element.Elements().ToList();
        if (attributes.Count == 0 && children.Count == 0)
            return element.IsEmpty ? null : element.Value;

        var map = new DataMap();
        foreach (var attribute in attributes)
            map["@" + Name(attribute)] = attribute.Value;

        if (children.Count == 0)
        {
            if (!element.IsEmpty)
                map[TextKey] = element.Value;
            return map;
        }

        var text = string.Join(" ", element.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0));
        if (text.Length > 0)
            map[TextKey] = text;
        foreach (var child in children)
        {
            var name = Name(child);
            var value = Element(child, depth + 1);
            // Element values are never arrays themselves, so an array under a name means "repeated element".
            if (!map.TryGetValue(name, out var existing))
                map[name] = value;
            else if (existing is List<object?> repeated)
                repeated.Add(value);
            else
                map[name] = new List<object?> { existing, value };
        }
        return map;
    }

    private static string Name(XElement element) => Qualified(element.Name, element.GetPrefixOfNamespace(element.Name.Namespace));

    private static string Name(XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
            return attribute.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + attribute.Name.LocalName;
        var prefix = attribute.Name.Namespace == XNamespace.Xml ? "xml"
            : attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);
        return Qualified(attribute.Name, prefix);
    }

    private static string Qualified(XName name, string? prefix)
    {
        var local = XmlConvert.DecodeName(name.LocalName);
        return string.IsNullOrEmpty(prefix) ? local : prefix + ":" + local;
    }

    // ───────────────────────── Writing ─────────────────────────

    public static string Write(object? root)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            var scope = new Scope(null);
            if (root is DataMap { Count: 1 } map && map.GetAt(0) is var (name, value)
                && name.Length > 0 && name[0] is not ('@' or '#') && value is not List<object?>)
                WriteElement(writer, name, value, scope, 0);
            else
                WriteElement(writer, RootName, root, scope, 0);
            writer.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void WriteElement(XmlWriter writer, string name, object? value, Scope parent, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The data is nested too deeply.");

        // Namespace declarations of this element ("@xmlns", "@xmlns:x") apply to its own name too.
        var scope = new Scope(parent);
        if (value is DataMap declarations)
        {
            foreach (var (key, declared) in declarations)
            {
                if (key == "@xmlns")
                    scope.Namespaces[""] = DataTree.Text(declared);
                else if (key.StartsWith("@xmlns:", StringComparison.Ordinal))
                    scope.Namespaces[key[7..]] = DataTree.Text(declared);
            }
        }

        var (prefix, local, ns) = scope.Resolve(name, element: true);
        writer.WriteStartElement(prefix, local, ns);
        switch (value)
        {
            case DataMap map:
                foreach (var (key, item) in map.Where(kv => kv.Key.StartsWith('@')))
                {
                    var attribute = key[1..];
                    var text = Clean(DataTree.IsScalar(item) ? DataTree.Text(item) : JsonData.Write(item).Trim());
                    if (attribute == "xmlns")
                        writer.WriteAttributeString("xmlns", text);
                    else if (attribute.StartsWith("xmlns:", StringComparison.Ordinal))
                        writer.WriteAttributeString("xmlns", attribute[6..], XmlnsNamespace, text);
                    else
                    {
                        var (attributePrefix, attributeLocal, attributeNs) = scope.Resolve(attribute, element: false);
                        writer.WriteAttributeString(attributePrefix, attributeLocal, attributeNs, text);
                    }
                }
                if (map.TryGetValue(TextKey, out var content) && content is not null)
                    writer.WriteString(Clean(DataTree.Text(content)));
                foreach (var (key, item) in map.Where(kv => !kv.Key.StartsWith('@') && kv.Key != TextKey))
                {
                    if (item is List<object?> repeated)
                    {
                        foreach (var each in repeated)
                            WriteElement(writer, key, each, scope, depth + 1);
                    }
                    else
                    {
                        WriteElement(writer, key, item, scope, depth + 1);
                    }
                }
                writer.WriteEndElement();
                break;
            case List<object?> list:
                // An array inside an array (or at the root): its items have no name of their own.
                foreach (var item in list)
                    WriteElement(writer, ItemName, item, scope, depth + 1);
                writer.WriteEndElement();
                break;
            case null:
                writer.WriteEndElement(); // <a />
                break;
            default:
                writer.WriteString(Clean(DataTree.Text(value)));
                writer.WriteFullEndElement(); // "" stays <a></a>
                break;
        }
    }

    /// <summary>Removes characters XML 1.0 cannot hold (most control characters).</summary>
    private static string Clean(string text)
    {
        if (XmlConvert.VerifyXmlChars(text) is null)
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(text[i]).Append(text[++i]);
            }
            else if (XmlConvert.IsXmlChar(text[i]))
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Namespace prefixes declared on an element and its ancestors.</summary>
    private sealed class Scope(Scope? parent)
    {
        public Dictionary<string, string> Namespaces { get; } = [];

        private string? Lookup(string prefix) =>
            Namespaces.TryGetValue(prefix, out var ns) ? ns : parent?.Lookup(prefix);

        /// <summary>
        /// Prefix, local name and namespace for a key. Elements without a prefix take the default namespace;
        /// attributes don't. A prefix nobody declared becomes part of the escaped local name.
        /// </summary>
        public (string? Prefix, string Local, string? Namespace) Resolve(string name, bool element)
        {
            var colon = name.IndexOf(':');
            if (colon > 0 && colon < name.Length - 1)
            {
                var prefix = name[..colon];
                var ns = prefix == "xml" ? XmlNamespace : Lookup(prefix);
                if (ns is { Length: > 0 } && IsNcName(prefix))
                    return (prefix, Encode(name[(colon + 1)..]), ns);
            }
            return (null, Encode(name), element ? Lookup("") ?? "" : null);
        }

        private static bool IsNcName(string name)
        {
            try
            {
                XmlConvert.VerifyNCName(name);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static string Encode(string name) => name.Length == 0 ? "_" : XmlConvert.EncodeLocalName(name)!;
    }
}
