// The "What's new" slides of a version: Assets/WhatsNew/<version>/slides.json lists each slide's picture and the i18n
// keys of its title and text. Shared with Filee.Setup (linked file, assets linked under the same path), which shows the
// newest deck while it copies files.
//
// {
//   "version": "2.0.0",
//   "slides": [
//     { "id": "formats", "media": "formats.webp", "title": "whats_new.2_0_0.formats.title", "text": "whats_new.2_0_0.formats.text" },
//     { "id": "flip", "media": "flip.webp", "poster": 75, "title": "...", "text": "..." }
//   ]
// }

using System.Collections.Frozen;
using System.Text.Json;
using Avalonia.Platform;

namespace Filee.App.Controls;

/// <summary>
/// One slide: a picture or animation, a title and a line or two of text (i18n keys). <paramref name="PosterFrame"/> is
/// the frame shown with reduced motion (the first unless the manifest says <c>"poster"</c>).
/// </summary>
public sealed record Slide(string Id, Uri Media, string TitleKey, string TextKey, int PosterFrame = 0)
{
    /// <summary>Optional recordings of the same demonstration in particular UI languages.</summary>
    public IReadOnlyDictionary<string, SlideMedia> LocalizedMedia { get; init; } =
        FrozenDictionary<string, SlideMedia>.Empty;

    /// <summary>Exact language match only: pt-PT must never borrow a pt-BR recording, or vice versa.</summary>
    public SlideMedia MediaFor(string? language) =>
        language is not null && LocalizedMedia.TryGetValue(language, out var localized)
            ? localized : new SlideMedia(Media, PosterFrame);
}

/// <summary>A recording and its own reduced-motion frame; localized recordings can have different frame counts.</summary>
public sealed record SlideMedia(Uri Source, int PosterFrame = 0);

/// <summary>The slides introducing <see cref="Version"/>.</summary>
public sealed record SlideDeck(Version Version, IReadOnlyList<Slide> Slides)
{
    /// <summary>Where the decks live inside an assembly's resources.</summary>
    public const string Folder = "Assets/WhatsNew";

    /// <summary>"2.0" for 2.0.0, "2.1.3" otherwise: how the window names the version.</summary>
    public string DisplayVersion => Version.Build > 0 ? Version.ToString(3) : Version.ToString(2);

    /// <summary>
    /// Every deck in the resources of <paramref name="assembly"/> ("Filee", "Filee.Setup"), oldest first. A broken
    /// manifest is skipped: the slides are a nicety and must never stop Filee from starting.
    /// </summary>
    public static IReadOnlyList<SlideDeck> LoadAll(string assembly)
    {
        var root = new Uri($"avares://{assembly}/{Folder}/");
        IEnumerable<Uri> assets;
        try
        {
            assets = AssetLoader.GetAssets(root, null).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
        var decks = new List<SlideDeck>();
        foreach (var manifest in assets.Where(u => u.AbsolutePath.EndsWith("/slides.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var reader = new StreamReader(AssetLoader.Open(manifest));
                if (Parse(reader.ReadToEnd(), new Uri(manifest, ".")) is { } deck)
                    decks.Add(deck);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
            {
            }
        }
        return decks.OrderBy(d => d.Version).ToList();
    }

    /// <summary>
    /// The deck a manifest describes, its pictures relative to <paramref name="folder"/>. Null without a valid version
    /// or slides; slides missing a field are left out.
    /// </summary>
    public static SlideDeck? Parse(string json, Uri folder)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionText) || versionText.ValueKind != JsonValueKind.String
            || TryParseVersion(versionText.GetString()) is not { } version
            || !root.TryGetProperty("slides", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var slides = new List<Slide>();
        foreach (var item in list.EnumerateArray())
        {
            if (Text(item, "id") is { } id && Text(item, "media") is { } media
                && Text(item, "title") is { } title && Text(item, "text") is { } text
                && IsPlainRelative(media))
            {
                var localized = new Dictionary<string, SlideMedia>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("localizedMedia", out var variants) && variants.ValueKind == JsonValueKind.Object)
                    foreach (var variant in variants.EnumerateObject())
                        if (Text(variant.Value, "media") is { } path && IsPlainRelative(path))
                            localized[variant.Name] = new SlideMedia(new Uri(folder, path), Poster(variant.Value));
                slides.Add(new Slide(id, new Uri(folder, media), title, text, Poster(item))
                {
                    LocalizedMedia = localized.Count == 0 ? FrozenDictionary<string, SlideMedia>.Empty
                        : localized.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                });
            }
        }
        return slides.Count == 0 ? null : new SlideDeck(version, slides);
    }

    /// <summary>
    /// The deck to show after an update from <paramref name="seen"/> (exclusive; null: none seen) to
    /// <paramref name="current"/> (inclusive): the newest in between, or null.
    /// </summary>
    public static SlideDeck? Newest(IEnumerable<SlideDeck> decks, Version? seen, Version current) =>
        decks.Where(d => (seen is null || d.Version > seen) && d.Version <= current).MaxBy(d => d.Version);

    /// <summary>"2.0.0" / "v2.0" / "2.0.0-beta" → 2.0.0 (three parts, so 2.0 and 2.0.0 compare equal).</summary>
    public static Version? TryParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build))
            : null;
    }

    /// <summary>
    /// A file name or path inside the deck's folder: not "..", not rooted ("/x", "\x") and no drive or scheme ("C:",
    /// "https:"), whatever the platform the manifest is read on.
    /// </summary>
    private static bool IsPlainRelative(string media) =>
        !media.Contains("..", StringComparison.Ordinal) && !media.Contains(':') && media[0] is not ('/' or '\\');

    private static int Poster(JsonElement item) =>
        item.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Number
        && poster.TryGetInt32(out var frame) && frame > 0 ? frame : 0;

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text ? text : null;
}
