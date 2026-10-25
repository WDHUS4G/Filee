// The languages Tesseract can read text in (tessdata_fast 4.1.0, Apache-2.0), pinned in ocr-languages.json like the
// engines in engines.json. A language is downloaded the first time a preset needs it, checked against its SHA-256 and
// kept in engines/ocr-languages/<code>/<code>.traineddata by EngineInstaller, so a damaged or partial download never
// counts as installed.

using System.Globalization;
using System.Text.Json;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Ocr;

/// <summary>One language Tesseract reads, by its Tesseract code (<c>eng</c>, <c>chi_sim</c>).</summary>
/// <param name="Culture">The language as a culture name, for its name in the UI.</param>
public sealed record OcrLanguage(string Code, string Culture, long Size)
{
    /// <summary>The language's own name ("Deutsch", "日本語"), the same in every UI language.</summary>
    public string NativeName
    {
        get
        {
            try
            {
                var name = CultureInfo.GetCultureInfo(Culture).NativeName;
                return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(Culture)) + name[1..] : Code;
            }
            catch (CultureNotFoundException)
            {
                return Code;
            }
        }
    }
}

public static class OcrLanguages
{
    /// <summary>Read when a preset doesn't say (and the first language offered).</summary>
    public const string Default = "eng";

    private const string Version = "4.1.0";

    private static readonly (IReadOnlyList<OcrLanguage> All, IReadOnlyDictionary<string, EngineComponent> Components) List = Load();

    /// <summary>Every language offered, English first.</summary>
    public static IReadOnlyList<OcrLanguage> All => List.All;

    /// <summary>The downloads, for <see cref="EngineInstaller"/> (one component per language).</summary>
    internal static IReadOnlyDictionary<string, EngineComponent> Components => List.Components;

    /// <summary>Folder that receives one sub folder per language.</summary>
    public static string Root => Path.Combine(EngineEnvironment.DownloadRoot, "ocr-languages");

    public static OcrLanguage? Find(string code) => All.FirstOrDefault(l => l.Code == code);

    /// <summary>The language data, or null while it isn't downloaded.</summary>
    public static string? FileOf(string code) =>
        EngineEnvironment.FirstExisting(Path.Combine(Root, code, code + ".traineddata")) is { } file
        && new EngineInstaller(Http.Value, Root, Components).IsInstalled(PackageOf(code))
            ? file : null;

    public static bool IsInstalled(string code) => FileOf(code) is not null;

    /// <summary>Downloads <paramref name="code"/> if it isn't there yet; one download at a time.</summary>
    public static async Task<string> EnsureAsync(string code, CancellationToken cancellationToken)
    {
        if (!Components.ContainsKey(code))
            throw new ArgumentException($"Tesseract has no language '{code}' in Filee's list.", nameof(code));
        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (FileOf(code) is { } file)
                return file;
            await new EngineInstaller(Http.Value, Root, Components).InstallAsync(PackageOf(code), progress: null, cancellationToken);
            return FileOf(code) ?? throw new InvalidDataException($"The {code} language data is missing after its download.");
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Redirects are followed by the installer, which checks every file against its SHA-256.</summary>
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Filee/{typeof(OcrLanguages).Assembly.GetName().Version}");
        return http;
    });

    private static EnginePackage PackageOf(string code) => new("ocr-" + code, [code], Components[code].Size, ["tesseract"]);

    private static (IReadOnlyList<OcrLanguage>, IReadOnlyDictionary<string, EngineComponent>) Load()
    {
        using var stream = typeof(OcrLanguages).Assembly.GetManifestResourceStream("Filee.Engines.ocr-languages.json")
                           ?? throw new InvalidOperationException("Embedded ocr-languages.json is missing.");
        using var json = JsonDocument.Parse(stream);
        var url = json.RootElement.GetProperty("url").GetString()!;
        var languages = new List<OcrLanguage>();
        var components = new Dictionary<string, EngineComponent>();
        foreach (var entry in json.RootElement.GetProperty("languages").EnumerateObject())
        {
            var size = entry.Value.GetProperty("size").GetInt64();
            languages.Add(new OcrLanguage(entry.Name, entry.Value.GetProperty("culture").GetString()!, size));
            components[entry.Name] = new EngineComponent(entry.Name, Version, string.Format(CultureInfo.InvariantCulture, url, entry.Name),
                entry.Value.GetProperty("sha256").GetString()!, size, "traineddata");
        }
        return (languages, components);
    }
}
