// "Request this format": a file type Filee can't read becomes a GitHub issue (the "Format request" form) with its
// extension filled in. Only the extension goes in the link, never a file name; the user reads it and submits it.

using Filee.Core.Formats;

namespace Filee.App.Services;

public static class FormatRequest
{
    /// <summary>Extensions shown in one request at most; the rest of a big mixed drop is left out.</summary>
    private const int MaxExtensions = 3;

    /// <summary>
    /// The extensions (".ncm") of <paramref name="paths"/> that Filee doesn't know, lower-case and each once. Folders
    /// and files without an extension have nothing to request.
    /// </summary>
    public static IReadOnlyList<string> UnknownExtensions(IEnumerable<string> paths) =>
        paths.Where(p => FormatRegistry.Detect(p) is null && !Directory.Exists(p))
            .Select(p => FormatRegistry.ExtensionOf(p).ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .Take(MaxExtensions)
            .Select(e => "." + e)
            .ToList();

    /// <summary>".ncm" or ".ncm, .qmc".</summary>
    public static string Label(IReadOnlyList<string> extensions) => string.Join(", ", extensions);

    /// <summary>The new-issue page of the "Format request" form, its title and extension field filled in.</summary>
    public static Uri Url(IReadOnlyList<string> extensions)
    {
        var label = Label(extensions);
        return new Uri(UpdateService.RepositoryUrl.TrimEnd('/') + "/issues/new?template=format_request.yml" +
            "&title=" + Uri.EscapeDataString("[Format]: " + label) +
            "&extension=" + Uri.EscapeDataString(label));
    }
}
