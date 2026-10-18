// E-mail messages (.eml, RFC 5322 / MIME) with MimeKitLite (MIT): the message as a web page (the headers and the HTML
// body, or the plain text; pictures sent inline with "cid:" links become data URIs), as plain text, or its attachments
// as a ZIP. MimeKit decodes the transfer encodings (quoted-printable, base64), encoded headers (RFC 2047 / 2231) and
// charsets, including the Korean ones (EUC-KR / CP949, ISO-2022-KR); header text in raw 8-bit that is not UTF-8 is read
// as CP949, as older Korean mail programs wrote it. Outlook .msg files are rebuilt as MIME messages first (see
// OutlookMessage) and take the same path; MSG → EML writes that message.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using MimeKit;

namespace Filee.Engines.Email;

/// <summary>EML and MSG → HTML, TXT and ZIP (attachments); MSG → EML.</summary>
public sealed partial class EmlConverter : IConverter
{
    public string Id => "email";
    public string DisplayName => "E-mail (built-in)";
    public int MaxParallelism => 0;

    // MimeKit learns which charsets exist the first time it is used and never asks again. Register the code pages
    // (CP949/EUC-KR, Shift_JIS, GBK…) before anything touches MimeKit, or a MSG converted first leaves every later
    // Korean EML decoded as Latin-1.
    static EmlConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // MSG has direct edges too: through EML (cost 20) would rebuild and parse the message twice.
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("eml", "html"), new("eml", "txt"), new("eml", "zip"),
        new("msg", "eml"), new("msg", "html"), new("msg", "txt"), new("msg", "zip"),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("EML, MSG → HTML, TXT, ZIP (attachments); MSG → EML",
        $"{EngineVersions.BuiltIn} · {EngineVersions.Library("MimeKitLite", typeof(MimeKit.MimeMessage))} · {EngineVersions.Library("MsgReader", typeof(MsgReader.Outlook.Storage))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            var message = step.From == "msg" ? OutlookMessage.Load(step.InputPath) : Load(step.InputPath, cancellationToken);
            progress?.Report(0.5);
            if (step.To == "eml")
            {
                if (step.Output.Allocate("eml") is not { } eml)
                    return [];
                using (var file = File.Create(eml))
                    message.WriteTo(FormatOptions.Default, file, cancellationToken);
                progress?.Report(1);
                return [eml];
            }
            if (step.To == "zip")
            {
                var attachments = Attachments(message);
                if (attachments.Count == 0)
                    throw new InvalidOperationException("The e-mail has no attachments.");
                if (step.Output.Allocate("zip") is not { } zip)
                    return [];
                WriteZip(attachments, zip, cancellationToken);
                progress?.Report(1);
                return [zip];
            }

            var output = step.Output.Allocate(step.To);
            if (output is null)
                return [];
            var text = step.To == "html" ? Html(message) : PlainText(message);
            File.WriteAllText(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: step.To == "txt"));
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>Parses a message; 8-bit header text that is not UTF-8 is read as CP949 (Korean).</summary>
    internal static MimeMessage Load(string path, CancellationToken cancellationToken = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = ParserOptions.Default.Clone();
        options.CharsetEncoding = Encoding.GetEncoding(949);
        using var stream = File.OpenRead(path);
        return MimeMessage.Load(options, stream, cancellationToken);
    }

    // ───────────────────────── Headers ─────────────────────────

    /// <summary>The headers shown above the body: name and value, only those the message has.</summary>
    private static List<(string Name, string Value)> Headers(MimeMessage message)
    {
        var headers = new List<(string, string)>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                headers.Add((name, value.Trim()));
        }
        Add("From", Addresses(message.From));
        Add("To", Addresses(message.To));
        Add("Cc", Addresses(message.Cc));
        if (message.Headers.Contains(HeaderId.Date))
            Add("Date", message.Date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Add("Subject", message.Subject);
        var attachments = Attachments(message).Select(a => a.Name).ToList();
        if (attachments.Count > 0)
            Add("Attachments", string.Join(", ", attachments));
        return headers;
    }

    private static string Addresses(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    // ───────────────────────── HTML ─────────────────────────

    /// <summary>A standalone web page: the headers in a table, then the HTML body (or the text body, escaped).</summary>
    internal static string Html(MimeMessage message)
    {
        var styles = new StringBuilder();
        string body;
        if (message.HtmlBody is { } html)
        {
            html = RemoveScripts(InlinePictures(html, message));
            // Keep the style sheets of the message's own <head>, and the content of its <body>.
            foreach (Match style in StyleBlock().Matches(html))
                styles.Append(style.Value);
            var match = BodyContent().Match(html);
            body = match.Success ? match.Groups[1].Value : HeadBlock().Replace(html, "");
        }
        else
        {
            body = $"<pre class=\"filee-mail-text\">{WebUtility.HtmlEncode(message.TextBody ?? "")}</pre>";
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append($"<title>{WebUtility.HtmlEncode(message.Subject ?? "")}</title>\n");
        sb.Append("<style>\n.filee-mail-headers { border-collapse: collapse; margin: 0 0 12px 0; font-family: sans-serif; font-size: 14px; }\n")
          .Append(".filee-mail-headers th { text-align: left; vertical-align: top; padding: 2px 16px 2px 0; color: #555; font-weight: 600; }\n")
          .Append(".filee-mail-headers td { padding: 2px 0; }\n")
          .Append(".filee-mail-text { white-space: pre-wrap; font-family: inherit; }\n</style>\n");
        sb.Append(styles);
        sb.Append("</head>\n<body>\n<table class=\"filee-mail-headers\">\n");
        foreach (var (name, value) in Headers(message))
            sb.Append($"<tr><th>{name}</th><td>{WebUtility.HtmlEncode(value)}</td></tr>\n");
        sb.Append("</table>\n<hr>\n");
        sb.Append(body);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>"cid:" references → data URIs of the parts with that Content-ID, so the page needs no other files.</summary>
    private static string InlinePictures(string html, MimeMessage message)
    {
        var parts = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.ContentId is { Length: > 0 } id)
                parts.TryAdd(id.Trim('<', '>'), part);
        }
        if (parts.Count == 0)
            return html;
        return ContentIdReference().Replace(html, match =>
        {
            var id = Uri.UnescapeDataString(match.Groups[1].Value);
            if (!parts.TryGetValue(id, out var part) || part.Content is null)
                return match.Value;
            using var data = new MemoryStream();
            part.Content.DecodeTo(data);
            return $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(data.ToArray())}";
        });
    }

    /// <summary>A converted page is opened in a browser: scripts of the message are not carried over.</summary>
    private static string RemoveScripts(string html) => ScriptBlock().Replace(html, "");

    // ───────────────────────── Text ─────────────────────────

    /// <summary>The headers, an empty line and the text body (or the HTML body as text).</summary>
    internal static string PlainText(MimeMessage message)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in Headers(message))
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        var text = message.TextBody ?? (message.HtmlBody is { } html ? HtmlToText(html) : "");
        sb.Append(text.Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd()).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Rough text of an HTML body: blocks and line breaks become new lines, tags go, entities are decoded.</summary>
    internal static string HtmlToText(string html)
    {
        var text = ScriptBlock().Replace(html, "");
        text = StyleBlock().Replace(text, "");
        text = HeadBlock().Replace(text, "");
        text = LineBreakTag().Replace(text, "\n");
        text = BlockEndTag().Replace(text, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => SpaceRun().Replace(l, " ").Trim());
        return MultipleBlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    // ───────────────────────── Attachments ─────────────────────────

    /// <summary>Attached files and messages with a unique, safe file name each.</summary>
    internal static List<(string Name, MimeEntity Entity)> Attachments(MimeMessage message)
    {
        var result = new List<(string, MimeEntity)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in message.BodyParts)
        {
            string? name = entity switch
            {
                MessagePart attached => (attached.ContentDisposition?.FileName ?? attached.Message?.Subject ?? "message") + ".eml",
                MimePart part when part.IsAttachment || (part.FileName is { Length: > 0 } && part.ContentId is null) =>
                    part.FileName ?? $"attachment{MimeTypes.TryGetExtension(part.ContentType.MimeType, out var extension) switch { true => extension, false => ".bin" }}",
                _ => null,
            };
            if (name is null)
                continue;
            name = SafeName(name);
            var unique = name;
            for (var i = 2; !names.Add(unique); i++)
                unique = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
            result.Add((unique, entity));
        }
        return result;
    }

    private static void WriteZip(List<(string Name, MimeEntity Entity)> attachments, string path, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (name, entity) in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                switch (entity)
                {
                    case MessagePart { Message: { } attached }:
                        attached.WriteTo(target, cancellationToken);
                        break;
                    case MimePart { Content: { } content }:
                        content.DecodeTo(target, cancellationToken);
                        break;
                }
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A file name without folders or characters Windows does not allow.</summary>
    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/')[^1]);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string([.. name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)]).Trim(' ', '.');
        return name.Length == 0 ? "attachment" : name.Length > 150 ? name[..150] : name;
    }

    // ───────────────────────── Patterns ─────────────────────────

    [GeneratedRegex("<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyContent();

    [GeneratedRegex("<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadBlock();

    [GeneratedRegex("<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex("cid:([^\"'\\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentIdReference();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("</(p|div|tr|li|h[1-6]|table|blockquote)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("[ \\t]+")]
    private static partial Regex SpaceRun();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
