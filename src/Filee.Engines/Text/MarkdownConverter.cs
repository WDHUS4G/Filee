// Markdown → HTML and plain text with Markdig (BSD-2-Clause), in-process: Markdown works without any optional engine.
// The HTML also serves as an intermediate step (Markdown → HTML → PDF / DOCX through LibreOffice), so local images
// get absolute file URIs whenever the page is written away from the Markdown file.

using System.Net;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Filee.Engines.Text;

/// <summary>Markdown to HTML and TXT without external programs.</summary>
public sealed class MarkdownConverter : IConverter
{
    public string Id => "markdown";
    public string DisplayName => "Markdown (built-in)";
    public int MaxParallelism => 0;

    // Cheaper than Pandoc's Markdown edges, so the built-in path wins for HTML and TXT.
    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("md", "html", 4), new("md", "txt", 5)];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.Library("Markdig", typeof(Markdown)));

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var markdown = HwpxConverter.DecodeText(await File.ReadAllBytesAsync(step.InputPath, cancellationToken));
        progress?.Report(0.3);
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        var content = step.To switch
        {
            "html" => ToHtml(markdown, Path.GetFullPath(step.InputPath), output),
            "txt" => Markdown.ToPlainText(markdown, MarkdownReader.Pipeline),
            _ => throw new NotSupportedException(step.To),
        };
        await File.WriteAllTextAsync(output, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: step.To == "txt"), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    /// <summary>A complete HTML page for the Markdown file at <paramref name="inputPath"/>, written to <paramref name="outputPath"/>.</summary>
    internal static string ToHtml(string markdown, string inputPath, string outputPath)
    {
        var document = Markdown.Parse(markdown, MarkdownReader.Pipeline);
        var inputFolder = Path.GetDirectoryName(inputPath)!;
        if (!string.Equals(inputFolder, Path.GetDirectoryName(Path.GetFullPath(outputPath)), StringComparison.OrdinalIgnoreCase))
        {
            // Relative image paths would break away from the Markdown file: point them at the original files.
            foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage))
                if (MarkdownReader.ResolveImage(image.Url, inputFolder) is { } path)
                    image.Url = new Uri(path).AbsoluteUri;
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        MarkdownReader.Pipeline.Setup(renderer);
        renderer.Render(document);
        var title = document.Descendants<HeadingBlock>().FirstOrDefault() is { Inline: { } heading }
            ? string.Concat(heading.Descendants<LiteralInline>().Select(l => l.Content.ToString()))
            : Path.GetFileNameWithoutExtension(inputPath);
        return HtmlPage(title, writer.ToString());
    }

    /// <summary>Wraps an HTML fragment into a readable, self-contained page (UTF-8, Korean / Chinese fonts).</summary>
    internal static string HtmlPage(string title, string body) =>
        $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>body{font-family:'Noto Sans KR','Malgun Gothic','Microsoft YaHei',sans-serif;max-width:860px;margin:2rem auto;line-height:1.7;padding:0 1rem}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 8px}pre,code{background:#f4f3f8;border-radius:4px}pre{padding:.6rem .8rem;overflow:auto}img{max-width:100%}blockquote{margin-left:0;padding-left:1rem;border-left:3px solid #ccc;color:#555}</style>
        </head>
        <body>
        {{body}}
        </body>
        </html>
        """;
}
