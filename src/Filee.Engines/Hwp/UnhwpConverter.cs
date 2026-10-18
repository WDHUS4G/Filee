// HWP / HWPX → plain text, Markdown or HTML with Unhwp (MIT, native library shipped in the NuGet package).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Markdig;
using Unhwp;

namespace Filee.Engines.Hwp;

/// <summary>Extracts the text content of HWP documents.</summary>
public sealed class UnhwpConverter : IConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

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
            using var document = UnhwpDocument.ParseFile(step.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);

            var content = step.To switch
            {
                "txt" => document.ToText(),
                "md" => document.ToMarkdown(new MarkdownOptions { IncludeFrontmatter = false }),
                "html" => ToHtml(document.ToMarkdown(new MarkdownOptions()), document.Title ?? Path.GetFileNameWithoutExtension(step.InputPath)),
                _ => throw new NotSupportedException(step.To),
            };

            var path = step.Output.Allocate(step.To);
            if (path is null)
                return [];
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: step.To == "txt"));
            progress?.Report(1);
            return [path];
        }, cancellationToken);

    private static string ToHtml(string markdown, string title) =>
        Text.MarkdownConverter.HtmlPage(title, Markdown.ToHtml(markdown, Pipeline));
}
