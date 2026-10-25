// HWP / HWPX → plain text, Markdown or HTML with Unhwp (MIT, native library shipped in the NuGet package).
//
// Unhwp's Markdown links pictures as "assets/<id>" without writing them: Filee takes each picture from the document
// (GetResourceData) and puts it into a "<name>_files" folder next to the Markdown file (MarkdownPictures), or embeds
// it in the HTML page as a data: URI, so the page stays one file. Pictures it can't provide are left out instead of
// becoming broken links; "Leave out pictures" (DocumentOptions.LeaveOutPictures) leaves out all of them.

using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;
using Markdig;
using Unhwp;

namespace Filee.Engines.Hwp;

/// <summary>Extracts the text content of HWP documents.</summary>
public sealed class UnhwpConverter : IConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>A picture in Unhwp's Markdown: <c>![alt](assets/image1.jpg)</c>, the id being the resource's.</summary>
    private static readonly Regex AssetPicture = new(@"!\[([^\]\n]*)\]\(assets/([^)\s]+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Id => "unhwp";
    public string DisplayName => "Unhwp";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "txt"), new("hwpx", "txt"),
        new("hwp", "md"), new("hwpx", "md"),
        new("hwp", "html", 14), new("hwpx", "html", 14),
    ];

    public EngineStatus GetStatus()
    {
        // The native library only ships for win-x64, osx-x64/arm64 and linux-x64.
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var supported = arch == System.Runtime.InteropServices.Architecture.X64
                        || (OperatingSystem.IsMacOS() && arch == System.Runtime.InteropServices.Architecture.Arm64);
        return supported
            ? EngineStatus.Available(version: EngineVersions.Library("Unhwp", typeof(UnhwpDocument)))
            : EngineStatus.Unavailable("engine.reason.unsupported_platform");
    }

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var document = Parse(step.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);

            var path = step.Output.Allocate(step.To);
            if (path is null)
                return [];
            var content = step.To switch
            {
                "txt" => document.ToText(),
                "md" => WriteMarkdown(document, step, path),
                "html" => ToHtml(Pictures(document.ToMarkdown(new MarkdownOptions()), document, step.WorkDirectory, DataUri),
                    document.Title ?? Path.GetFileNameWithoutExtension(step.InputPath)),
                _ => throw new NotSupportedException(step.To),
            };
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: step.To == "txt"));
            progress?.Report(1);
            return [path];
        }, cancellationToken);

    /// <summary>
    /// Opens the document. unhwp.dll needs VCRUNTIME140.dll, which the Windows packages put next to it (see
    /// <see cref="AppLocalRuntime"/>); without it Windows cannot load the library.
    /// </summary>
    private static UnhwpDocument Parse(string path)
    {
        try
        {
            return UnhwpDocument.ParseFile(path);
        }
        catch (DllNotFoundException ex) when (OperatingSystem.IsWindows())
        {
            throw new ConversionRefusedException(AppLocalRuntime.ErrorKey, ex.Message);
        }
    }

    private static string WriteMarkdown(UnhwpDocument document, ConversionStep step, string output)
    {
        var markdown = document.ToMarkdown(new MarkdownOptions { IncludeFrontmatter = false });
        Func<string, string?> link = step.Preset.Document.LeaveOutPictures
            ? _ => null
            : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
        return Pictures(markdown, document, step.WorkDirectory, link);
    }

    /// <summary>
    /// Points the "assets/…" pictures of Unhwp's Markdown at <paramref name="link"/>(file of the picture); a picture
    /// without a link is left out, with the empty paragraph it stood in.
    /// </summary>
    internal static string Pictures(string markdown, UnhwpDocument document, string workDirectory, Func<string, string?> link)
    {
        if (!AssetPicture.IsMatch(markdown))
            return markdown;
        var ids = document.GetResourceIds().ToHashSet(StringComparer.Ordinal);
        var folder = EbookFiles.NewFolder(workDirectory, "unhwp-pictures");
        var files = new Dictionary<string, string?>(StringComparer.Ordinal);
        var result = AssetPicture.Replace(markdown, match =>
        {
            var id = match.Groups[2].Value;
            if (!files.TryGetValue(id, out var target))
            {
                target = null;
                if (ids.Contains(id) && document.GetResourceData(id) is { Length: > 0 } data)
                {
                    var file = Path.Combine(folder, $"{files.Count + 1}-{Path.GetFileName(id)}");
                    File.WriteAllBytes(file, data);
                    target = link(file);
                }
                files[id] = target;
            }
            return target is null ? "" : $"![{match.Groups[1].Value}](<{target}>)";
        });
        // Paragraphs that held only a picture left behind: at most one empty line between blocks.
        return Regex.Replace(result, @"\n{3,}", "\n\n").Trim() + "\n";
    }

    private static string? DataUri(string file) =>
        EbookFiles.CommonImage(file, Path.GetDirectoryName(file)!, gifAndSvg: true) is ({ } common, { } mediaType)
            ? $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(common))}"
            : null;

    private static string ToHtml(string markdown, string title) =>
        MarkdownConverter.HtmlPage(title, Markdown.ToHtml(markdown, Pipeline));
}
