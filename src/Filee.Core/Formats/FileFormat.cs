// Describes a file format Filee understands (e.g. "png", "hwpx") and groups formats into categories.

namespace Filee.Core.Formats;

/// <summary>Broad family a format belongs to. Toolbar profiles and the UI group formats by this.</summary>
public enum FormatCategory
{
    Image,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Hwp,
    Text,
    Video,
    Audio,
    Ebook,
    Archive,
    Vector,
    Cad,
    Font,
    Subtitle,
    Data,
    Model3D,
    Geo,
}

/// <summary>
/// A file format known to Filee.
/// </summary>
/// <param name="Id">Stable lower-case identifier used in presets and converter edges (e.g. <c>"jpg"</c>).</param>
/// <param name="DisplayName">Short label shown in the UI (e.g. <c>"JPG"</c>).</param>
/// <param name="Category">Format family.</param>
/// <param name="Extensions">
/// File extensions without the dot; the first one is used for output files. Compound extensions such as
/// <c>"tar.gz"</c> are allowed.
/// </param>
/// <param name="Writable">False for formats Filee only reads (camera RAW, Apple Pages, ...): not offered as a target.</param>
public sealed record FileFormat(string Id, string DisplayName, FormatCategory Category, IReadOnlyList<string> Extensions, bool Writable = true)
{
    /// <summary>Extension (without dot) used when writing a file of this format.</summary>
    public string PrimaryExtension => Extensions[0];
}
