// KEPUB, the EPUB flavour Kobo e-readers read with their own engine (page turns, reading statistics, highlights):
// an EPUB whose XHTML chapters mark every sentence as <span class="koboSpan" id="kobo.P.S">, P counting the
// paragraphs (block elements) of the chapter and S the sentences in it; pictures get a span of their own. The body's
// content is wrapped in <div id="book-columns"><div id="book-inner">, which Kobo's reader styles. Files are named
// "name.kepub.epub" so Kobo devices pick the KEPUB engine when they are copied over USB.
//
// Written from the public descriptions of the format (MobileRead wiki, kepubify's documentation). EPUB → KEPUB
// changes only the spine's XHTML documents; every other file of the book (styles, fonts, pictures, the package
// document) is copied unchanged. KEPUB → EPUB removes the markup again.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Ebooks;

internal static partial class Kepub
{
    private const string SpanClass = "koboSpan";
    private const string ColumnsId = "book-columns";
    private const string InnerId = "book-inner";

    /// <summary>Elements whose text is not reading text.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "select", "option", "noscript", "head", "title",
    };

    /// <summary>Elements that start a new paragraph number.</summary>
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ol", "ul", "dl", "dt", "dd", "blockquote", "pre",
        "td", "th", "tr", "table", "caption", "figure", "figcaption", "section", "article", "aside", "header", "footer",
        "nav", "address", "main", "hr",
    };

    /// <summary>Copies an EPUB as a KEPUB.</summary>
    public static void FromEpub(string epub, string output, CancellationToken cancellationToken) =>
        Rewrite(epub, output, AddSpans, cancellationToken);

    /// <summary>Copies a KEPUB as a plain EPUB without Kobo's markup.</summary>
    public static void ToEpub(string kepub, string output, CancellationToken cancellationToken) =>
        Rewrite(kepub, output, RemoveSpans, cancellationToken);

    /// <summary>
    /// Copies the book, passing each XHTML document of the spine through <paramref name="transform"/> (which returns
    /// false to keep it as it was). "mimetype" is written first and stored, as EPUB requires.
    /// </summary>
    private static void Rewrite(string input, string output, Func<XDocument, bool> transform, CancellationToken cancellationToken)
    {
        using var source = OpenZip(input);
        EpubReader.CheckDrm(source);
        var documents = ContentDocuments(source);

        using var stream = File.Create(output);
        using var target = new ZipArchive(stream, ZipArchiveMode.Create);
        var mimetype = target.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = mimetype.Open())
            writer.Write("application/epub+zip"u8);

        foreach (var entry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName == "mimetype" || entry.FullName.EndsWith('/'))
                continue;
            var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            copy.LastWriteTime = entry.LastWriteTime;
            byte[]? rewritten = null;
            if (documents.Contains(entry.FullName))
            {
                using var read = entry.Open();
                var bytes = ReadAll(read);
                if (Load(bytes) is { } document && transform(document))
                    rewritten = Save(document);
                rewritten ??= bytes;
            }
            using var write = copy.Open();
            if (rewritten is not null)
            {
                write.Write(rewritten);
            }
            else
            {
                using var read = entry.Open();
                read.CopyTo(write);
            }
        }
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("This is not a valid EPUB file (it is not a ZIP package).", ex);
        }
    }

    // ───────────────────────── Package ─────────────────────────

    /// <summary>
    /// ZIP entry names of the XHTML documents in the spine (container.xml → package document); every .xhtml / .html
    /// file when the package can't be read.
    /// </summary>
    internal static HashSet<string> ContentDocuments(ZipArchive zip)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (PackagePath(zip) is { } opfPath && zip.GetEntry(opfPath) is { } opfEntry && LoadEntry(opfEntry) is { Root: { } package })
        {
            var folder = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
            var manifest = new Dictionary<string, (string Path, string MediaType)>(StringComparer.Ordinal);
            foreach (var item in package.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                if ((string?)item.Attribute("id") is { } id && (string?)item.Attribute("href") is { } href
                    && Resolve(folder, Uri.UnescapeDataString(href.Split('#')[0])) is { } path)
                    manifest.TryAdd(id, (path, ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant()));
            }
            foreach (var itemRef in package.Descendants().Where(e => e.Name.LocalName == "itemref"))
            {
                if ((string?)itemRef.Attribute("idref") is { } idRef && manifest.TryGetValue(idRef, out var item)
                    && (item.MediaType is "application/xhtml+xml" or "text/html" || IsHtmlName(item.Path)))
                    names.Add(item.Path);
            }
        }
        if (names.Count == 0)
            names.UnionWith(zip.Entries.Select(e => e.FullName).Where(IsHtmlName));
        return names;
    }

    private static bool IsHtmlName(string name) =>
        name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static string? PackagePath(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/container.xml") is { } container && LoadEntry(container) is { } document
            && document.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile") is { } rootFile
            && (string?)rootFile.Attribute("full-path") is { Length: > 0 } fullPath)
            return Uri.UnescapeDataString(fullPath).TrimStart('/');
        return zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName;
    }

    /// <summary>"OEBPS/" + "../Text/ch1.xhtml" → "Text/ch1.xhtml"; null when it leaves the book.</summary>
    internal static string? Resolve(string folder, string href)
    {
        var parts = new List<string>();
        foreach (var part in (folder + href).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static XDocument? LoadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Load(ReadAll(stream));
    }

    // ───────────────────────── XML ─────────────────────────

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Parses an XHTML document with its whitespace. HTML entities that XML does not define (&amp;nbsp; …) become
    /// character references first; external DTDs are never loaded. Null when it is not well-formed XML.
    /// </summary>
    internal static XDocument? Load(byte[] bytes)
    {
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        text = NamedEntity().Replace(text, match =>
        {
            if (match.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos")
                return match.Value;
            var decoded = WebUtility.HtmlDecode(match.Value);
            if (decoded == match.Value)
                return match.Value;
            var references = new StringBuilder();
            for (var i = 0; i < decoded.Length; i += char.IsSurrogatePair(decoded, i) ? 2 : 1)
                references.Append("&#x").Append(char.ConvertToUtf32(decoded, i).ToString("X")).Append(';');
            return references.ToString();
        });
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse, // keeps the DOCTYPE; nothing is fetched without a resolver
            XmlResolver = null,
            MaxCharactersFromEntities = 1_000_000,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static byte[] Save(XDocument document)
    {
        using var memory = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using (var writer = XmlWriter.Create(memory, settings))
            document.Save(writer);
        return memory.ToArray();
    }

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex NamedEntity();

    // ───────────────────────── Kobo spans ─────────────────────────

    /// <summary>Adds Kobo's sentence spans and wrapper divs. False for a document that has them already.</summary>
    internal static bool AddSpans(XDocument document)
    {
        if (document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") is not { } body
            || document.Root.Descendants().Any(IsKoboSpan))
            return false;
        var ns = body.Name.Namespace;
        var state = new SpanState(ns);
        Visit(body, state);

        var inner = new XElement(ns + "div", new XAttribute("id", InnerId), body.Nodes().ToList());
        body.RemoveNodes();
        body.Add(new XElement(ns + "div", new XAttribute("id", ColumnsId), inner));
        return true;
    }

    private sealed class SpanState(XNamespace ns)
    {
        public XNamespace Namespace { get; } = ns;
        public XElement? Block { get; set; }
        public int Paragraph { get; set; }
        public int Sentence { get; set; }

        /// <summary>A new span id, starting a new paragraph number when <paramref name="block"/> changed.</summary>
        public string NextId(XElement block)
        {
            if (!ReferenceEquals(block, Block))
            {
                Block = block;
                Paragraph++;
                Sentence = 0;
            }
            Sentence++;
            return $"kobo.{Paragraph}.{Sentence}";
        }
    }

    private static void Visit(XElement element, SpanState state)
    {
        foreach (var node in element.Nodes().ToList())
        {
            switch (node)
            {
                case XText text when text.Value.Trim().Length > 0:
                    Wrap(text, state);
                    break;
                case XElement child when child.Name.Namespace != state.Namespace || Skipped.Contains(child.Name.LocalName):
                    break; // SVG, MathML, scripts, ...
                case XElement child when child.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase):
                    child.ReplaceWith(Span(state, BlockOf(child), child));
                    break;
                case XElement child:
                    Visit(child, state);
                    break;
            }
        }
    }

    /// <summary>Replaces a text node with one span per sentence (whitespace before the first stays outside).</summary>
    private static void Wrap(XText text, SpanState state)
    {
        var value = text.Value;
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start]))
            start++;
        var block = BlockOf(text);
        var nodes = new List<XNode>();
        if (start > 0)
            nodes.Add(new XText(value[..start]));
        foreach (var sentence in Sentences(value[start..]))
            nodes.Add(Span(state, block, new XText(sentence)));
        text.ReplaceWith(nodes);
    }

    private static XElement Span(SpanState state, XElement block, XNode content) =>
        new(state.Namespace + "span", new XAttribute("class", SpanClass), new XAttribute("id", state.NextId(block)), content);

    private static XElement BlockOf(XNode node)
    {
        var parent = node.Parent!;
        while (!Blocks.Contains(parent.Name.LocalName) && parent.Parent is { } up)
            parent = up;
        return parent;
    }

    /// <summary>
    /// Splits text after sentence ends: . ! ? … (with closing quotes and brackets) followed by whitespace, which
    /// stays with the sentence, and the full-width 。！？ of Chinese and Japanese, which need no space.
    /// </summary>
    internal static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is not ('.' or '!' or '?' or '…' or '。' or '！' or '？'))
            {
                i++;
                continue;
            }
            var end = i + 1;
            while (end < text.Length && (text[end] is '.' or '!' or '?' or '…' or '。' or '！' or '？'
                                         || text[end] is '"' or '\'' or ')' or ']' or '”' or '’' or '」' or '』' or '）'))
                end++;
            var wide = c is '。' or '！' or '？';
            if (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                    end++;
            }
            else if (!wide)
            {
                i = end;
                continue; // "3.14", "e.g.x", an ellipsis inside a sentence
            }
            if (end < text.Length)
            {
                yield return text[start..end];
                start = end;
            }
            i = end;
        }
        if (start < text.Length)
            yield return text[start..];
    }

    // ───────────────────────── Back to EPUB ─────────────────────────

    /// <summary>Removes Kobo's spans and wrapper divs. False when there are none.</summary>
    internal static bool RemoveSpans(XDocument document)
    {
        if (document.Root is not { } root)
            return false;
        // Innermost first: ReplaceWith copies the children, so an outer element must go after the ones inside it.
        var markup = root.Descendants().Where(e => IsKoboSpan(e) || IsWrapper(e)).Reverse().ToList();
        foreach (var element in markup)
            element.ReplaceWith(element.Nodes());
        return markup.Count > 0;
    }

    private static bool IsKoboSpan(XElement element) =>
        element.Name.LocalName == "span" && ((string?)element.Attribute("class") ?? "").Split(' ').Contains(SpanClass);

    private static bool IsWrapper(XElement element) =>
        element.Name.LocalName == "div" && (string?)element.Attribute("id") is ColumnsId or InnerId;
}
