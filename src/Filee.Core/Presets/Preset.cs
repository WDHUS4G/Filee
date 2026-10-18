// A preset is one slice of the donut toolbar: "convert to <TargetFormat> with these options".

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>What a preset is: a conversion, or a group of conversions fanned out on the donut.</summary>
public enum PresetKind
{
    /// <summary>Converts files.</summary>
    Convert,

    /// <summary>
    /// Converts nothing itself: its donut slice fans out <see cref="Preset.SubPresetIds"/>. Never offered where a
    /// conversion is picked (preset list, watch folders, <c>filee --preset</c>).
    /// </summary>
    Group,
}

/// <summary>
/// Reads an enum by name. A value this version doesn't know (a newer Filee's, or a number outside the enum) becomes
/// <see cref="Unknown"/>, the first value unless a subclass says otherwise: the preset still loads, instead of the
/// whole preset file failing to read (which resets every preset to the defaults).
/// </summary>
public class LenientEnumJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    protected virtual TEnum Unknown => default;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            // A name, matched whole: Enum.TryParse would also take "Left90, Rotate180" (the flags OR'd into another
            // value) or "2".
            var text = reader.GetString();
            return Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) is { } name
                ? Enum.Parse<TEnum>(name)
                : Unknown;
        }
        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) &&
            Enum.IsDefined((TEnum)Enum.ToObject(typeof(TEnum), number))
                ? (TEnum)Enum.ToObject(typeof(TEnum), number)
                : Unknown;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>An unknown kind is read as a group: never run as a conversion.</summary>
public sealed class PresetKindJsonConverter : LenientEnumJsonConverter<PresetKind>
{
    protected override PresetKind Unknown => PresetKind.Group;
}

/// <summary>
/// A named conversion recipe. The donut toolbar shows presets; dropping files on one runs it.
/// </summary>
/// <remarks>
/// Presets are plain mutable objects so they serialize to JSON directly and the editor can bind to them.
/// Use <see cref="Clone"/> before editing so that "Cancel" can discard changes.
/// </remarks>
public sealed class Preset
{
    /// <summary>Stable identifier referenced by toolbar profiles. Never shown to users.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>User-defined name. When empty, <see cref="NameKey"/> is localized and shown instead.</summary>
    public string Name { get; set; } = "";

    /// <summary>Localization key for built-in presets (e.g. <c>preset.merge_pdf</c>). Ignored when <see cref="Name"/> is set.</summary>
    public string? NameKey { get; set; }

    /// <summary>Id of the output format (see <see cref="Formats.FormatRegistry"/>).</summary>
    public string TargetFormat { get; set; } = "png";

    /// <summary>Options used when the output is a raster image.</summary>
    public ImageOptions Image { get; set; } = new();

    /// <summary>Options used when the input or output is a PDF.</summary>
    public PdfOptions Pdf { get; set; } = new();

    /// <summary>Options used for office / HWP documents.</summary>
    public DocumentOptions Document { get; set; } = new();

    /// <summary>Options used for video and audio.</summary>
    public MediaOptions Media { get; set; } = new();

    /// <summary>Options used for archives (ZIP, 7Z, TAR ...).</summary>
    public ArchiveOptions Archive { get; set; } = new();

    /// <summary>Options used when a raster image is traced into vector shapes (PNG → SVG, DXF ...).</summary>
    public TracingOptions Trace { get; set; } = new();

    /// <summary>Where and how output files are written.</summary>
    public OutputRule Output { get; set; } = new();

    /// <summary>A conversion or a group. A kind this version doesn't know (from a newer Filee) is read as a group.</summary>
    [JsonConverter(typeof(PresetKindJsonConverter))]
    public PresetKind Kind { get; set; } = PresetKind.Convert;

    /// <summary>Most presets one group fans out: more slices would get too narrow to hit.</summary>
    public const int MaxSubPresets = 6;

    /// <summary>
    /// <see cref="TargetFormat"/> of a group: no format at all. A Filee from before groups reads a group as a
    /// conversion; with this it finds no route and greys the slice out instead of re-encoding files "as the same format".
    /// </summary>
    public const string GroupTarget = "group";

    /// <summary>
    /// <see cref="PresetKind.Group"/>: the presets (by <see cref="Id"/>) its donut slice fans out in a small ring
    /// outside it, e.g. "Rotate" → left 90° / right 90° / 180°. At most <see cref="MaxSubPresets"/>, never groups.
    /// Ignored for a conversion.
    /// </summary>
    public List<string> SubPresetIds { get; set; } = [];

    /// <summary>True for a <see cref="PresetKind.Group"/>.</summary>
    [JsonIgnore]
    public bool IsGroup => Kind == PresetKind.Group;

    /// <summary>Creates a new random preset id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Deep copy, used by editors so changes can be cancelled.</summary>
    public Preset Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        TargetFormat = TargetFormat,
        Image = Image.Clone(),
        Pdf = Pdf.Clone(),
        Document = Document.Clone(),
        Media = Media.Clone(),
        Archive = Archive.Clone(),
        Trace = Trace.Clone(),
        Output = Output.Clone(),
        Kind = Kind,
        SubPresetIds = [.. SubPresetIds],
    };
}

/// <summary>How an image is resized before it is encoded.</summary>
public enum ResizeMode
{
    /// <summary>Keep the original pixel size.</summary>
    None,
    /// <summary>Scale by <see cref="ImageOptions.ResizePercent"/>.</summary>
    Percent,
    /// <summary>Scale so the longer edge equals <see cref="ImageOptions.LongEdge"/> (never upscales).</summary>
    LongEdge,
    /// <summary>Scale to <see cref="ImageOptions.Width"/> pixels wide, height in proportion (never upscales).</summary>
    Width,
}

/// <summary>How an image is turned, after its EXIF orientation is applied.</summary>
public enum ImageRotation
{
    None,
    /// <summary>90° clockwise.</summary>
    Right90,
    /// <summary>90° counter-clockwise.</summary>
    Left90,
    Rotate180,
}

/// <summary>How an image is mirrored, after it is turned.</summary>
public enum ImageFlip
{
    None,
    /// <summary>Left ↔ right.</summary>
    Horizontal,
    /// <summary>Top ↔ bottom.</summary>
    Vertical,
}

/// <summary>TIFF compression schemes.</summary>
public enum TiffCompression
{
    None,
    Lzw,
    Zip,
}

/// <summary>Raster image encoding options.</summary>
public sealed class ImageOptions
{
    /// <summary>Lossy quality 1-100 (JPG, WEBP, AVIF, HEIC).</summary>
    public int Quality { get; set; } = 90;

    public ResizeMode Resize { get; set; } = ResizeMode.None;

    /// <summary>Turns the image (after its EXIF orientation is applied).</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<ImageRotation>))]
    public ImageRotation Rotation { get; set; }

    /// <summary>Mirrors the image (after <see cref="Rotation"/>).</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<ImageFlip>))]
    public ImageFlip Flip { get; set; }

    /// <summary>Turns or mirrors the image: only engines that do (<see cref="Conversion.IImageTransformer"/>) can run it.</summary>
    [JsonIgnore]
    public bool TurnsOrMirrors => Rotation != ImageRotation.None || Flip != ImageFlip.None;

    /// <summary>Scale in percent when <see cref="Resize"/> is <see cref="ResizeMode.Percent"/>.</summary>
    public int ResizePercent { get; set; } = 50;

    /// <summary>Target long-edge length in pixels when <see cref="Resize"/> is <see cref="ResizeMode.LongEdge"/>.</summary>
    public int LongEdge { get; set; } = 1920;

    /// <summary>Target width in pixels when <see cref="Resize"/> is <see cref="ResizeMode.Width"/>.</summary>
    public int Width { get; set; } = 1200;

    /// <summary>Output DPI written into the file. <c>null</c> keeps the source value.</summary>
    public int? Dpi { get; set; }

    /// <summary>Keep EXIF / XMP / ICC metadata.</summary>
    public bool KeepMetadata { get; set; } = true;

    /// <summary>
    /// When metadata is removed, still keep the ICC colour profile: without it, wide-gamut photos (Display P3 from
    /// phones) look washed out. Has no effect while <see cref="KeepMetadata"/> is on (everything is kept then).
    /// </summary>
    public bool KeepColorProfile { get; set; } = true;

    /// <summary>Convert to grayscale.</summary>
    public bool Grayscale { get; set; }

    /// <summary>
    /// Make a plain background (paper behind a signature or stamp, the backdrop of a logo) transparent. Targets without
    /// an alpha channel get <see cref="Background"/> instead, so this also replaces the backdrop colour.
    /// </summary>
    public bool RemoveBackground { get; set; }

    /// <summary>0-100: how different from the backdrop a pixel may be and still be removed (<see cref="RemoveBackground"/>).</summary>
    public int BackgroundTolerance { get; set; } = 25;

    /// <summary>
    /// With <see cref="RemoveBackground"/>: only remove the backdrop connected to the image edge, keeping enclosed
    /// areas such as the white inside a logo. Off by default: it also keeps the paper inside handwritten loops.
    /// </summary>
    public bool KeepEnclosedBackground { get; set; }

    /// <summary>Fill colour for transparent pixels when the target has no alpha channel (e.g. JPG).</summary>
    public string Background { get; set; } = "#FFFFFF";

    public TiffCompression TiffCompression { get; set; } = TiffCompression.Lzw;

    /// <summary>
    /// TIFF target: every file becomes pages of one multi-page TIFF (one page per image, per PDF page and per page of
    /// a multi-page TIFF), like scanned documents and faxes, instead of one TIFF per file.
    /// </summary>
    public bool MultiPageTiff { get; set; }

    /// <summary>Use lossless WEBP encoding.</summary>
    public bool WebpLossless { get; set; }

    /// <summary>Icon sizes embedded in an ICO file.</summary>
    public List<int> IcoSizes { get; set; } = [256, 128, 64, 48, 32, 16];

    public ImageOptions Clone()
    {
        var copy = (ImageOptions)MemberwiseClone();
        copy.IcoSizes = [.. IcoSizes];
        return copy;
    }
}

/// <summary>Page size used when images are placed into a PDF.</summary>
public enum PdfPageSize
{
    /// <summary>Each page has the size of its image.</summary>
    FitImage,
    A4,
    Letter,
}

/// <summary>PDF related options.</summary>
public sealed class PdfOptions
{
    /// <summary>Combine all dropped files into one PDF instead of one PDF per file.</summary>
    public bool MergeIntoSingle { get; set; }

    public PdfPageSize PageSize { get; set; } = PdfPageSize.FitImage;

    /// <summary>Page margin in millimetres (ignored for <see cref="PdfPageSize.FitImage"/>).</summary>
    public double MarginMm { get; set; }

    /// <summary>Resolution used when PDF pages are rendered to images.</summary>
    public int RenderDpi { get; set; } = 150;

    /// <summary>Pages to process, e.g. <c>"1-3,5"</c>. Empty means all pages.</summary>
    public string PageRange { get; set; } = "";

    /// <summary>For PDF → PDF: write every page as its own file.</summary>
    public bool SplitPages { get; set; }

    /// <summary>For PDF → image: stack the pages into one tall image instead of one image per page.</summary>
    public bool CombinePages { get; set; }

    /// <summary>
    /// With <see cref="CombinePages"/>: cut the blank space above and below each page's content (a small margin stays),
    /// so the pages follow each other without wide white gaps.
    /// </summary>
    public bool TrimPageMargins { get; set; }

    /// <summary>
    /// With <see cref="CombinePages"/>: when the stack is taller than the format stores (JPG 65,500 px, WebP 16,383 px),
    /// write several images split between pages instead of scaling the stack down.
    /// </summary>
    public bool SplitTallImage { get; set; }

    public PdfOptions Clone() => (PdfOptions)MemberwiseClone();
}

/// <summary>Options for office and HWP documents.</summary>
public sealed class DocumentOptions
{
    /// <summary>Export PDF/A (archival) when producing PDF from documents.</summary>
    public bool PdfA { get; set; }

    public DocumentOptions Clone() => (DocumentOptions)MemberwiseClone();
}

/// <summary>Trade-off between quality and file size for video and lossy audio.</summary>
public enum MediaQuality
{
    /// <summary>Visually lossless, large files.</summary>
    High,
    /// <summary>Good quality at a reasonable size (default).</summary>
    Balanced,
    /// <summary>Small files for sharing, visible compression.</summary>
    Small,
}

/// <summary>Video and audio options.</summary>
public sealed class MediaOptions
{
    public MediaQuality Quality { get; set; } = MediaQuality.Balanced;

    /// <summary>Largest output height in pixels (e.g. 720); 0 keeps the source size. Never upscales.</summary>
    public int MaxHeight { get; set; }

    /// <summary>Audio bitrate in kbit/s for lossy audio; 0 uses a good default for the format.</summary>
    public int AudioBitrateKbps { get; set; }

    /// <summary>Drop the audio track of a video.</summary>
    public bool RemoveAudio { get; set; }

    /// <summary>Audio channels of an audio file: 0 keeps the source's, 1 mono, 2 stereo.</summary>
    public int AudioChannels { get; set; }

    /// <summary>Sample rate in Hz of a WAV file (8000 … 48000); 0 keeps the source's.</summary>
    public int AudioSampleRate { get; set; }

    /// <summary>How a WAV (or W64) file stores its samples.</summary>
    public WavSampleFormat WavFormat { get; set; } = WavSampleFormat.Pcm16;

    /// <summary>
    /// Video codec for targets that can hold several (<see cref="MediaCodecs.VideoCodecsFor"/>); targets that can't
    /// hold the chosen one use their usual codec.
    /// </summary>
    public VideoCodec VideoCodec { get; set; } = VideoCodec.Default;

    /// <summary>Audio codec for targets that can hold several (<see cref="MediaCodecs.AudioCodecsFor"/>).</summary>
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Default;

    public MediaOptions Clone() => (MediaOptions)MemberwiseClone();
}

/// <summary>Video codec of a video target. <see cref="Default"/> is the target's usual codec (H.264, VP9 for WebM, ...).</summary>
public enum VideoCodec
{
    Default,
    /// <summary>H.265 / HEVC (x265): about half the size of H.264 at the same quality.</summary>
    Hevc,
    /// <summary>AV1 (SVT-AV1, or libaom): smaller still, slower to encode.</summary>
    Av1,
}

/// <summary>Audio codec of an audio target. <see cref="Default"/> is the target's usual codec (AAC for M4A, ...).</summary>
public enum AudioCodec
{
    Default,
    /// <summary>Apple Lossless in M4A / M4B.</summary>
    Alac,
}

/// <summary>Which targets take which codecs besides their default one.</summary>
public static class MediaCodecs
{
    /// <summary>The video codecs <paramref name="target"/> can be written with; only Default for most targets.</summary>
    public static IReadOnlyList<VideoCodec> VideoCodecsFor(string target) => target switch
    {
        // AV1 in MP4 plays in browsers, Android and Windows; QuickTime (MOV) has no AV1 mapping.
        "mp4" or "m4v" or "mkv" => [VideoCodec.Default, VideoCodec.Hevc, VideoCodec.Av1],
        "mov" => [VideoCodec.Default, VideoCodec.Hevc],
        // The WebM specification allows VP8, VP9 and AV1.
        "webm" => [VideoCodec.Default, VideoCodec.Av1],
        _ => [VideoCodec.Default],
    };

    /// <summary>The audio codecs <paramref name="target"/> can be written with; only Default for most targets.</summary>
    public static IReadOnlyList<AudioCodec> AudioCodecsFor(string target) => target switch
    {
        "m4a" or "m4b" => [AudioCodec.Default, AudioCodec.Alac],
        _ => [AudioCodec.Default],
    };

    /// <summary>The codec <paramref name="target"/> is written with: the chosen one if the target takes it, else Default.</summary>
    public static VideoCodec Effective(string target, VideoCodec codec) =>
        VideoCodecsFor(target).Contains(codec) ? codec : VideoCodec.Default;

    /// <summary>The codec <paramref name="target"/> is written with: the chosen one if the target takes it, else Default.</summary>
    public static AudioCodec Effective(string target, AudioCodec codec) =>
        AudioCodecsFor(target).Contains(codec) ? codec : AudioCodec.Default;
}

/// <summary>Sample format of a WAV file. 16-bit PCM is what every player reads (and was the only one before 1.6).</summary>
public enum WavSampleFormat
{
    Pcm16,
    Pcm8,
    Pcm24,
    Pcm32,
    Float32,
}

/// <summary>How hard archives are compressed.</summary>
public enum ArchiveLevel
{
    /// <summary>No compression (fast, for already compressed files).</summary>
    Store,
    Fast,
    Normal,
    Maximum,
}

/// <summary>Archive options.</summary>
public sealed class ArchiveOptions
{
    /// <summary>Put all dropped files into one archive instead of one archive per file.</summary>
    public bool CombineIntoOne { get; set; }

    public ArchiveLevel Level { get; set; } = ArchiveLevel.Normal;

    public ArchiveOptions Clone() => (ArchiveOptions)MemberwiseClone();
}

/// <summary>How a raster image is traced.</summary>
public enum TracingMode
{
    /// <summary>The image is reduced to <see cref="TracingOptions.Colors"/> colours, each traced as stacked shapes.</summary>
    Color,
    /// <summary>Dark pixels become black shapes on a transparent background (logos, signatures, scans).</summary>
    BlackAndWhite,
}

/// <summary>Raster → vector tracing options.</summary>
public sealed class TracingOptions
{
    /// <summary>Smallest and largest <see cref="Colors"/>.</summary>
    public const int MinColors = 2, MaxColors = 64;

    public TracingMode Mode { get; set; } = TracingMode.Color;

    /// <summary>Number of colours in <see cref="TracingMode.Color"/> mode (2–64).</summary>
    public int Colors { get; set; } = 16;

    /// <summary>
    /// 0–100: low values smooth the outlines and drop specks, high values follow the pixels more closely and keep
    /// small details.
    /// </summary>
    public int Detail { get; set; } = 50;

    public TracingOptions Clone() => (TracingOptions)MemberwiseClone();
}
