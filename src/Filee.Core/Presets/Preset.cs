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

    /// <summary>
    /// Opens its own window on the dropped files instead of converting them (the interactive Crop, Cut a clip; see
    /// <see cref="Preset.Tool"/>). Like a group, never offered where a conversion is picked.
    /// </summary>
    Tool,
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

    /// <summary>Options used when a DWG or DXF is written.</summary>
    public CadOptions Cad { get; set; } = new();

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

    /// <summary>True for "Compress into one archive": files and folders go into one archive as they are.</summary>
    [JsonIgnore]
    public bool PacksIntoOneArchive =>
        Archive.CombineIntoOne
        && Formats.FormatRegistry.FindById(TargetFormat) is { Category: Formats.FormatCategory.Archive } archive
        && archive.Id != Formats.FormatRegistry.Folder;

    /// <summary>
    /// True if converting a <paramref name="sourceFormat"/> file saves every frame as a picture
    /// (<see cref="MediaOptions.SaveEveryFrame"/>): the file is a video and the target one of
    /// <see cref="MediaOptions.FrameTargets"/>. Other files, and other targets, convert as usual.
    /// </summary>
    public bool SavesFramesOf(string sourceFormat) =>
        Media.SaveEveryFrame
        && !MakesLivePhotoOf(sourceFormat)
        && MediaOptions.FrameTargets.Contains(TargetFormat)
        && Formats.FormatRegistry.FindById(sourceFormat)?.Category == Formats.FormatCategory.Video;

    /// <summary>
    /// True if converting a <paramref name="sourceFormat"/> file takes the video of a Live / Motion Photo instead of a
    /// video of the still picture: the target is one of <see cref="MediaOptions.MotionPhotoTargets"/> and the source one
    /// of <see cref="MediaOptions.MotionPhotoSources"/>, with <see cref="MediaOptions.ExtractMotionPhoto"/> on. A LIVP
    /// (an iPhone photo and its MOV in one ZIP) always gives its video. Other pictures (GIF, PNG, …) convert as usual.
    /// </summary>
    public bool ExtractsMotionOf(string sourceFormat) =>
        MediaOptions.MotionPhotoTargets.Contains(TargetFormat)
        && MediaOptions.MotionPhotoSources.Contains(sourceFormat)
        && (Media.ExtractMotionPhoto || sourceFormat == "livp");

    /// <summary>
    /// True if converting a <paramref name="sourceFormat"/> file makes a Live / Motion Photo of a short clip
    /// (<see cref="MediaOptions.LivePhoto"/>): the file is a video and the target one the kind is written as
    /// (<see cref="MediaOptions.LivePhotoTargets"/>). Other files, and other targets, convert as usual.
    /// </summary>
    public bool MakesLivePhotoOf(string sourceFormat) =>
        MediaOptions.LivePhotoTargets(Media.LivePhoto).Contains(TargetFormat)
        && Formats.FormatRegistry.FindById(sourceFormat)?.Category == Formats.FormatCategory.Video;

    /// <summary>
    /// True if converting a <paramref name="sourceFormat"/> file recognizes its text first (<see cref="PdfOptions.Ocr"/>):
    /// the file is a PDF or a picture and the target one of <see cref="PdfOptions.OcrTargets"/>.
    /// </summary>
    public bool RecognizesTextOf(string sourceFormat) =>
        Pdf.Ocr
        && PdfOptions.OcrTargets.Contains(TargetFormat)
        && (sourceFormat == "pdf" || Formats.FormatRegistry.FindById(sourceFormat)?.Category == Formats.FormatCategory.Image);

    /// <summary>
    /// True for "Merge videos", "Merge audio" and "Video from images": every dropped file goes, in order, into one
    /// video or audio file (<see cref="MediaOptions.JoinIntoOne"/>, <see cref="MediaOptions.Slideshow"/>).
    /// </summary>
    [JsonIgnore]
    public bool JoinsIntoOneMedia =>
        (Media.JoinIntoOne || Media.Slideshow) && MediaOptions.JoinTargets.Contains(TargetFormat);

    /// <summary>True for a <see cref="PresetKind.Tool"/>.</summary>
    [JsonIgnore]
    public bool IsTool => Kind == PresetKind.Tool;

    /// <summary><see cref="Tool"/> of the interactive crop (also a tool saved without one, from before clips).</summary>
    public const string CropTool = "crop";

    /// <summary><see cref="Tool"/> of "Cut a clip": a part of a video, chosen on a timeline.</summary>
    public const string ClipTool = "clip";

    /// <summary>
    /// <see cref="PresetKind.Tool"/>: which window it opens, <see cref="CropTool"/> (or null, as saved before there were
    /// other tools) or <see cref="ClipTool"/>. A tool this version doesn't know (from a newer Filee) opens nothing.
    /// Ignored for other kinds.
    /// </summary>
    public string? Tool { get; set; }

    /// <summary>True for the interactive crop.</summary>
    [JsonIgnore]
    public bool IsCropTool => IsTool && Tool is null or CropTool;

    /// <summary>True for "Cut a clip".</summary>
    [JsonIgnore]
    public bool IsClipTool => IsTool && Tool == ClipTool;

    /// <summary>
    /// True if converting a <paramref name="sourceFormat"/> file cuts a clip of it (<see cref="MediaOptions.Clip"/>,
    /// set by the clip window on the conversion it starts): the file is a video. Other files convert as usual.
    /// </summary>
    public bool CutsClipOf(string sourceFormat) =>
        Media.Clip is { Parts.Count: > 0 } && Formats.FormatRegistry.FindById(sourceFormat)?.Category == Formats.FormatCategory.Video;

    /// <summary>Creates a new random preset id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Deep copy, used by editors so changes can be cancelled.</summary>
    /// <summary>
    /// This preset without its PDF password, for copies that leave the library (history, an export).
    /// <paramref name="mark"/> records that it was taken out (<see cref="PdfOptions.PasswordLeftOut"/>).
    /// </summary>
    public Preset WithoutPassword(bool mark)
    {
        if (Pdf.Password.Length == 0)
            return this;
        var copy = Clone();
        copy.Pdf.Password = "";
        copy.Pdf.PasswordLeftOut = mark;
        return copy;
    }

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
        Cad = Cad.Clone(),
        Output = Output.Clone(),
        Kind = Kind,
        Tool = Tool,
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

/// <summary>What a FITS astronomy picture becomes in a TIFF (<see cref="ImageOptions.FitsTiff"/>).</summary>
public enum FitsTiffMode
{
    /// <summary>The data as it is, at the file's own bit depth: for further processing.</summary>
    Linear,
    /// <summary>Automatically stretched like a capture program's preview, at the file's own bit depth.</summary>
    Stretched,
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

    /// <summary>
    /// "Compress image": PNG with zlib's best compression, and a JPEG never saved above the quality it had (a photo
    /// saved at 75 would otherwise come back at the preset's quality, bigger and no better).
    /// </summary>
    public bool MaxCompression { get; set; }

    /// <summary>
    /// JPEG → JPEG at the quality the photo had (ImageMagick's estimate), not at <see cref="Quality"/>: "Remove
    /// metadata" re-encodes the picture as little as it can. Other targets use <see cref="Quality"/>.
    /// </summary>
    public bool KeepSourceQuality { get; set; }

    /// <summary>Convert to grayscale.</summary>
    public bool Grayscale { get; set; }

    /// <summary>
    /// Crop away the fully transparent padding around a picture's visible pixels (PNGs exported with a margin, sprites,
    /// icons). Pictures without transparency, and animations, stay as they are.
    /// </summary>
    public bool TrimTransparent { get; set; }

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

    /// <summary>
    /// FITS → TIFF: the data as it is (the default) or stretched. Either way the TIFF follows the file's BITPIX: 8 and
    /// 16 bits stay so, 32-bit integer and floating-point data become 32-bit floating point.
    /// </summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<FitsTiffMode>))]
    public FitsTiffMode FitsTiff { get; set; }

    /// <summary>
    /// FITS → any other picture (stretched automatically): a raw frame of a colour camera becomes colour with the Bayer
    /// pattern in its header. Off keeps it mono. Mono cameras and three-plane colour files are not affected.
    /// </summary>
    public bool FitsDebayer { get; set; } = true;

    /// <summary>Icon sizes embedded in an ICO file.</summary>
    public List<int> IcoSizes { get; set; } = [256, 128, 64, 48, 32, 16];

    /// <summary>The playback speeds the preset editor offers (<see cref="AnimationSpeed"/>).</summary>
    public static readonly double[] AnimationSpeeds = [0.25, 0.5, 0.75, 1, 1.5, 2, 4];

    /// <summary>
    /// Plays an animation (GIF, WebP, APNG, MNG; video → GIF or APNG) this many times as fast: 2 halves every frame
    /// delay, 0.5 doubles it. 1 (the default, also for presets saved before the option) leaves the timing as it is.
    /// </summary>
    public double AnimationSpeed { get; set; } = 1;

    /// <summary>
    /// <see cref="AnimationSpeed"/> as the engines use it: between 0.1 and 10, and 1 for a value that makes no sense
    /// (zero, negative; a hand-edited preset file).
    /// </summary>
    [JsonIgnore]
    public double PlaybackSpeed => double.IsFinite(AnimationSpeed) && AnimationSpeed > 0 ? Math.Clamp(AnimationSpeed, 0.1, 10) : 1;

    /// <summary>True when animations are played faster or slower than they were made (<see cref="PlaybackSpeed"/>).</summary>
    [JsonIgnore]
    public bool ChangesSpeed => PlaybackSpeed != 1;

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

/// <summary>
/// How hard a PDF → PDF conversion shrinks the file: pictures are scaled down to the level's resolution and saved as
/// JPEG at its quality, the rest is compressed losslessly. Text, fonts and drawings stay as they are.
/// </summary>
public enum PdfCompression
{
    /// <summary>Pages are copied as they are.</summary>
    None,
    /// <summary>Smallest: pictures at 72 dpi (reading on screen).</summary>
    Screen,
    /// <summary>Balanced: pictures at 150 dpi (e-mail, uploads).</summary>
    Ebook,
    /// <summary>High quality: pictures at 300 dpi (printing).</summary>
    Print,
}

/// <summary>A lossless turn applied to every page of a PDF.</summary>
public enum PdfRotation
{
    None,
    Left90,
    Right90,
    Rotate180,
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

    /// <summary>For PDF → PDF: rotate every selected page without rendering it.</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<PdfRotation>))]
    public PdfRotation Rotation { get; set; }

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

    /// <summary>
    /// For PDF → PDF: make the file smaller (also with a page range or one file per page). A level from a newer Filee
    /// reads as <see cref="PdfCompression.None"/>.
    /// </summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<PdfCompression>))]
    public PdfCompression Compression { get; set; }

    /// <summary>
    /// For PDF → PDF: drop the document information (title, author, dates, creator) and every XMP packet. Pages,
    /// links, bookmarks and form fields stay as they are.
    /// </summary>
    public bool RemoveMetadata { get; set; }

    /// <summary>
    /// With <see cref="Compression"/>: also let Ghostscript rewrite the PDF when it is installed, and keep whichever
    /// result is smaller. Off by default: it rewrites the whole file (fonts, structure), not only the pictures.
    /// </summary>
    public bool CompressWithGhostscript { get; set; }

    /// <summary>Text written large and faint across every page of a PDF ("DRAFT", "CONFIDENTIAL"); empty for none.</summary>
    public string Watermark { get; set; } = "";

    /// <summary>"1 / 5" at the bottom of every page of a PDF.</summary>
    public bool PageNumbers { get; set; }

    /// <summary>
    /// A password the PDF opens with (AES-256); empty for none. Saved with the preset in the settings file, like the
    /// rest of it.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// The <see cref="Password"/> was left out of this copy (the one history keeps to convert failed files again): a
    /// retry takes it from the preset again, and fails rather than write a PDF without it.
    /// </summary>
    public bool PasswordLeftOut { get; set; }

    /// <summary>A PDF this preset writes gets a watermark, page numbers or a password (<see cref="Conversion.IPdfFinisher"/>).</summary>
    [JsonIgnore]
    public bool FinishesPdf => !string.IsNullOrWhiteSpace(Watermark) || PageNumbers || Password.Length > 0 || PasswordLeftOut;

    /// <summary>Formats text read from scans can go into (<see cref="Ocr"/>): those Filee writes from a PDF's text itself.</summary>
    public static readonly IReadOnlyList<string> OcrTargets = ["pdf", "docx", "txt", "md", "hwpx"];

    /// <summary>
    /// Recognize the text of scanned pages and pictures with Tesseract (<see cref="Preset.RecognizesTextOf"/>): a PDF
    /// gets a searchable text layer, a DOCX, TXT, Markdown or HWPX gets the text. Pages that already have text stay as
    /// they are.
    /// </summary>
    public bool Ocr { get; set; }

    /// <summary>With <see cref="Ocr"/>: the language of the text, as a Tesseract code (<c>eng</c>, <c>deu</c>, <c>chi_sim</c>).</summary>
    public string OcrLanguage { get; set; } = "eng";

    public PdfOptions Clone() => (PdfOptions)MemberwiseClone();
}

/// <summary>Options for office and HWP documents.</summary>
public sealed class DocumentOptions
{
    /// <summary>Export PDF/A (archival) when producing PDF from documents.</summary>
    public bool PdfA { get; set; }

    /// <summary>
    /// Markdown output: the text only, without the document's pictures (no "&lt;name&gt;_files" folder next to it).
    /// </summary>
    public bool LeaveOutPictures { get; set; }

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

    /// <summary>
    /// Encode H.264, HEVC and AV1 video with the graphics chip's encoder (NVENC, Quick Sync, AMF, VideoToolbox or VA-API;
    /// AV1 only on recent chips and not on a Mac) when one works on this computer, else with x264 / x265 / SVT-AV1 as
    /// usual. Much faster and lighter on power, a little larger at the same quality. Off by default (and for presets
    /// saved before it existed), so results never change without the user asking for it.
    /// </summary>
    public bool HardwareEncoding { get; set; }

    /// <summary>Picture formats a video's frames can be saved as (<see cref="SaveEveryFrame"/>).</summary>
    public static readonly IReadOnlyList<string> FrameTargets = ["png", "jpg"];

    /// <summary>
    /// Video → PNG or JPG: save every frame as a picture, in a folder, instead of one still frame. Only for video
    /// sources and a target in <see cref="FrameTargets"/> (<see cref="Preset.SavesFramesOf"/>).
    /// </summary>
    public bool SaveEveryFrame { get; set; }

    /// <summary>Formats a Live / Motion Photo video can be taken into (<see cref="ExtractMotionPhoto"/>).</summary>
    public static readonly IReadOnlyList<string> MotionPhotoTargets = ["mp4", "mov"];

    /// <summary>
    /// Photos that can hold a video (<see cref="ExtractMotionPhoto"/>): Android Motion Photos (JPEG with the video
    /// appended, HEIC / AVIF with an <c>mpvd</c> box), iPhone Live Photos (a JPEG or HEIC with its MOV next to it) and
    /// LIVP packages.
    /// </summary>
    public static readonly IReadOnlyList<string> MotionPhotoSources = ["jpg", "heic", "avif", "livp"];

    /// <summary>
    /// MP4 / MOV: a Live / Motion Photo becomes its video (with sound) instead of a video of the still picture. Only
    /// for sources in <see cref="MotionPhotoSources"/> (<see cref="Preset.ExtractsMotionOf"/>); a photo without a video
    /// fails with <c>error.no_motion_photo</c>.
    /// </summary>
    public bool ExtractMotionPhoto { get; set; }

    /// <summary>
    /// Video → JPG (or HEIC for an iPhone): a Live / Motion Photo of a short clip instead of one still frame. Only for
    /// video sources and a target in <see cref="LivePhotoTargets"/> (<see cref="Preset.MakesLivePhotoOf"/>).
    /// </summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<LivePhotoKind>))]
    public LivePhotoKind LivePhoto { get; set; }

    /// <summary>The photo formats a <paramref name="kind"/> of Live / Motion Photo is written as.</summary>
    public static IReadOnlyList<string> LivePhotoTargets(LivePhotoKind kind) => kind switch
    {
        LivePhotoKind.Apple => ["jpg", "heic"],
        LivePhotoKind.Android => ["jpg"],
        _ => [],
    };

    /// <summary>Shortest and longest clip of a Live / Motion Photo, in seconds (phones record about 3).</summary>
    public const double MinLivePhotoLength = 0.5, MaxLivePhotoLength = 5;

    /// <summary>With <see cref="LivePhoto"/>: where the clip starts in the video, in seconds.</summary>
    public double LivePhotoStart { get; set; }

    /// <summary>
    /// With <see cref="LivePhoto"/>: how long the clip is, in seconds (<see cref="MinLivePhotoLength"/> to
    /// <see cref="MaxLivePhotoLength"/>); shorter when the video ends sooner.
    /// </summary>
    public double LivePhotoLength { get; set; } = 3;

    /// <summary>
    /// With <see cref="LivePhoto"/>: the frame that becomes the photo, in seconds from the start of the clip (the
    /// middle, as a phone takes it, by default); the clip's last frame when the clip is shorter.
    /// </summary>
    public double LivePhotoCover { get; set; } = 1.5;

    /// <summary>With <see cref="SaveEveryFrame"/>: extract every Nth frame (1 = every frame).</summary>
    public int FrameInterval { get; set; } = 1;

    /// <summary>
    /// "Cut a clip": cut exactly at the chosen frames by encoding the clip again (with <see cref="Quality"/>,
    /// <see cref="MaxHeight"/>, <see cref="VideoCodec"/>, …). Off by default: the streams are copied as they are, which
    /// takes a moment and loses nothing, but the clip starts at the key frame before the chosen start.
    /// </summary>
    public bool ExactCut { get; set; }

    /// <summary>
    /// A clip of a video (<see cref="Preset.CutsClipOf"/>): its parts, their volume and speed, and its fades. Set by the
    /// clip window on the conversion it starts, never on a saved preset.
    /// </summary>
    public ClipEdit? Clip { get; set; }

    /// <summary>
    /// Most pictures one video may become (<see cref="SaveEveryFrame"/>): about an hour at 30 fps, already more than
    /// anyone looks through. A longer video needs a larger <see cref="FrameInterval"/>.
    /// </summary>
    public const long MaxFrames = 100_000;

    /// <summary>Targets the dropped files can be joined into (<see cref="JoinIntoOne"/>, <see cref="Slideshow"/>).</summary>
    public static readonly IReadOnlyList<string> JoinTargets = ["mp4", "m4a"];

    /// <summary>
    /// MP4 or M4A: every dropped video (or audio file) goes, in order and without gaps, into one file named after the
    /// first. Clips that share codecs and picture size are copied; others are encoded to match the first.
    /// </summary>
    public bool JoinIntoOne { get; set; }

    /// <summary>
    /// MP4: the dropped pictures become one 1080p video, each shown for <see cref="SlideSeconds"/>, fitted in with
    /// black bars.
    /// </summary>
    public bool Slideshow { get; set; }

    /// <summary>
    /// Most pictures one <see cref="Slideshow"/> takes: every picture is an FFmpeg input, held while the video is encoded.
    /// </summary>
    public const int MaxSlides = 300;

    /// <summary>Shortest and longest time one picture of a <see cref="Slideshow"/> is shown, in seconds.</summary>
    public const double MinSlideSeconds = 0.5, MaxSlideSeconds = 60;

    /// <summary>With <see cref="Slideshow"/>: how long each picture is shown, in seconds.</summary>
    public double SlideSeconds { get; set; } = 3;

    /// <summary>With <see cref="Slideshow"/>: fade from one picture into the next instead of cutting.</summary>
    public bool Crossfade { get; set; } = true;

    /// <summary>With <see cref="Crossfade"/>: how long a fade takes, in seconds (at most half a picture's time).</summary>
    public const double CrossfadeSeconds = 1;

    /// <summary>
    /// Audio target: copy the sound as it is when the target takes its codec (AAC from a phone video into M4A), with
    /// no bitrate, channel or Small quality asking for an encode. Off: always encoded, as before the option existed.
    /// </summary>
    public bool CopyAudioWhenPossible { get; set; }

    /// <summary>
    /// Video and audio: drop the tags (title, artist, dates, location, camera, encoder) and chapters, copying the
    /// streams as they are. Only when the file keeps its format.
    /// </summary>
    public bool RemoveMetadata { get; set; }

    /// <summary><see cref="SlideSeconds"/> within its limits (a hand-edited preset file may hold anything).</summary>
    [JsonIgnore]
    public double SlideDuration => double.IsFinite(SlideSeconds) ? Math.Clamp(SlideSeconds, MinSlideSeconds, MaxSlideSeconds) : 3;

    /// <summary>The fade between two pictures in seconds: 0 without <see cref="Crossfade"/>, never over half a picture's time.</summary>
    [JsonIgnore]
    public double FadeDuration => Crossfade ? Math.Min(CrossfadeSeconds, SlideDuration / 2) : 0;

    public MediaOptions Clone()
    {
        var copy = (MediaOptions)MemberwiseClone();
        copy.Clip = Clip?.Clone();
        return copy;
    }
}

/// <summary>The kind of moving photo a video becomes (<see cref="MediaOptions.LivePhoto"/>).</summary>
public enum LivePhotoKind
{
    /// <summary>One still frame, as usual.</summary>
    None,
    /// <summary>An iPhone Live Photo: a JPG or HEIC and a MOV with the same name and content identifier.</summary>
    Apple,
    /// <summary>An Android Motion Photo: one JPG with the clip (MP4) appended, described in its XMP.</summary>
    Android,
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

    /// <summary>Video targets whose usual codec is H.264 (the others have their container's own: VP9, MPEG-2, …).</summary>
    public static IReadOnlySet<string> H264Targets { get; } = new HashSet<string> { "mp4", "m4v", "mov", "mkv", "flv", "ts", "m2ts", "3gp", "3g2" };

    /// <summary>
    /// True when <paramref name="target"/> with <paramref name="codec"/> is written as H.264, HEVC or AV1, the codecs
    /// <see cref="MediaOptions.HardwareEncoding"/> applies to.
    /// </summary>
    public static bool TakesHardwareEncoding(string target, VideoCodec codec) => Effective(target, codec) switch
    {
        VideoCodec.Hevc or VideoCodec.Av1 => true,
        VideoCodec.Default => H264Targets.Contains(target),
        _ => false,
    };

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

/// <summary>The AutoCAD version a DWG or DXF is written in (#489).</summary>
public enum CadVersion
{
    /// <summary>AutoCAD 2018 (AC1032): still current in AutoCAD 2026.</summary>
    AutoCad2018,
    AutoCad2013,
    AutoCad2010,
    /// <summary>A DWG is written as 2004, which AutoCAD 2007 opens: ACadSharp can't write a 2007 DWG.</summary>
    AutoCad2007,
    AutoCad2004,
    AutoCad2000,
    /// <summary>The version of the drawing converted, within what can be written (2000–2018).</summary>
    SameAsSource,
}

/// <summary>Options for writing DWG and DXF.</summary>
public sealed class CadOptions
{
    /// <summary>A version from a newer Filee reads as <see cref="CadVersion.AutoCad2018"/>.</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<CadVersion>))]
    public CadVersion Version { get; set; }

    public CadOptions Clone() => (CadOptions)MemberwiseClone();
}

