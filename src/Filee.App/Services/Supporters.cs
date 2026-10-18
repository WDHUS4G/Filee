// The people and companies who sponsor Filee, shown in the Sponsor window and on the About page, and listed in the
// README's Sponsor section. They ship with each release (Assets/Sponsors/sponsors.json); nothing is downloaded.
//
//   "sponsors": the logo tier, { "name", "url", "logo", "logoDark" }. The logos are transparent PNGs next to the
//               file; "logoDark" is optional, for the dark theme.
//   "backers":  names, { "name", "url" }, "url" optional. One-time supporters who asked to be listed go here too.
//
// Someone added starts showing with the next release, someone whose sponsorship ended is removed in the next one.

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;

namespace Filee.App.Services;

public sealed record Supporter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("logo")] string? Logo = null,
    [property: JsonPropertyName("logoDark")] string? LogoDark = null);

public sealed record SupporterList(IReadOnlyList<Supporter> Sponsors, IReadOnlyList<Supporter> Backers)
{
    public static readonly SupporterList Empty = new([], []);

    public bool IsEmpty => Sponsors.Count == 0 && Backers.Count == 0;
}

public static class Supporters
{
    public const string Folder = "avares://Filee/Assets/Sponsors/";

    private static SupporterList? _bundled;

    /// <summary>The list shipped with this release.</summary>
    public static SupporterList Bundled => _bundled ??= Load();

    private static SupporterList Load()
    {
        var uri = new Uri(Folder + "sponsors.json");
        if (!AssetLoader.Exists(uri))
            return SupporterList.Empty;
        using var stream = AssetLoader.Open(uri);
        return Parse(new StreamReader(stream).ReadToEnd());
    }

    public static SupporterList Parse(string json)
    {
        var file = JsonSerializer.Deserialize(json, AppJsonContext.Default.SupportersFile);
        static IReadOnlyList<Supporter> Named(List<Supporter>? list) =>
            list?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList() ?? [];
        return file is null ? SupporterList.Empty : new(Named(file.Sponsors), Named(file.Backers));
    }
}

internal sealed class SupportersFile
{
    [JsonPropertyName("sponsors")] public List<Supporter>? Sponsors { get; set; }
    [JsonPropertyName("backers")] public List<Supporter>? Backers { get; set; }
}
