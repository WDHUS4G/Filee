// Factory defaults: presets and toolbar profiles created on first run or "Reset to defaults".

using Filee.Core.Formats;
using Filee.Core.Profiles;

namespace Filee.Core.Presets;

/// <summary>Built-in presets and profiles.</summary>
public static class BuiltInData
{
    /// <summary>Special target format meaning "same format as the source file" (e.g. resize only).</summary>
    public const string SameAsSource = "*";

    /// <summary>Creates the default preset list.</summary>
    public static List<Preset> CreatePresets() =>
    [
        new() { Id = "to-png", Name = "PNG", TargetFormat = "png" },
        new() { Id = "to-jpg", Name = "JPG", TargetFormat = "jpg", Image = { Quality = 90 } },
        new()
        {
            Id = "jpg-70", Name = "JPG 70%", TargetFormat = "jpg",
            Image = { Quality = 70 }, Output = { FileNamePattern = "{name}_70" },
        },
        new() { Id = "to-webp", Name = "WEBP", TargetFormat = "webp", Image = { Quality = 85 } },
        new() { Id = "to-tiff", Name = "TIFF", TargetFormat = "tiff" },
        new() { Id = "to-bmp", Name = "BMP", TargetFormat = "bmp" },
        new() { Id = "to-ico", Name = "ICO", TargetFormat = "ico" },
        new() { Id = "to-gif", Name = "GIF", TargetFormat = "gif" },
        new() { Id = "to-avif", Name = "AVIF", TargetFormat = "avif", Image = { Quality = 60 } },
        new() { Id = "to-heic", Name = "HEIC", TargetFormat = "heic", Image = { Quality = 80 } },
        new()
        {
            Id = "half-size", NameKey = "preset.half_size", TargetFormat = SameAsSource,
            Image = { Resize = ResizeMode.Percent, ResizePercent = 50 }, Output = { FileNamePattern = "{name}_50" },
        },
        new()
        {
            Id = "grayscale", NameKey = "preset.grayscale", TargetFormat = SameAsSource,
            Image = { Grayscale = true }, Output = { FileNamePattern = "{name}_gray" },
        },
        new()
        {
            Id = "remove-background", NameKey = "preset.remove_background", TargetFormat = "png",
            Image = { RemoveBackground = true }, Output = { FileNamePattern = "{name}_transparent" },
        },
        .. ImageToolPresets(),
        new()
        {
            Id = "tiff-300", Name = "TIFF 300dpi", TargetFormat = "tiff",
            Image = { Dpi = 300 }, Pdf = { RenderDpi = 300 }, Output = { FileNamePattern = "{name}_300dpi" },
        },
        new() { Id = "to-pdf", Name = "PDF", TargetFormat = "pdf" },
        new()
        {
            Id = "merge-pdf", NameKey = "preset.merge_pdf", TargetFormat = "pdf",
            Pdf = { MergeIntoSingle = true }, Output = { FileNamePattern = "{name}_merged" },
        },
        new()
        {
            Id = "pdf-long-image", NameKey = "preset.long_image", TargetFormat = "png",
            Pdf = { CombinePages = true }, Output = { FileNamePattern = "{name}_long" },
        },
        new()
        {
            Id = "pdf-split", NameKey = "preset.split_pdf", TargetFormat = "pdf",
            Pdf = { SplitPages = true }, Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" },
        },
        new() { Id = "to-docx", Name = "DOCX", TargetFormat = "docx" },
        new() { Id = "to-hwpx", Name = "HWPX", TargetFormat = "hwpx" },
        .. HwpPresets(),
        new() { Id = "to-odt", Name = "ODT", TargetFormat = "odt" },
        new() { Id = "to-rtf", Name = "RTF", TargetFormat = "rtf" },
        new() { Id = "to-txt", Name = "TXT", TargetFormat = "txt" },
        new() { Id = "to-md", Name = "Markdown", TargetFormat = "md" },
        new() { Id = "to-html", Name = "HTML", TargetFormat = "html" },
        .. SpreadsheetPresets(),
        .. CatalogPresets(),
        .. DataPresets(),
        .. CodecAndSubtitlePresets(),
        .. ModelPresets(),
        .. TracePresets(),
    ];

    /// <summary>Raster → SVG tracing, in colour and as black line art (added in settings schema 16).</summary>
    public static List<Preset> TracePresets() =>
    [
        new() { Id = "trace-svg", NameKey = "preset.trace_svg", TargetFormat = "svg" },
        new()
        {
            Id = "trace-line-art", NameKey = "preset.trace_line_art", TargetFormat = "svg",
            Trace = { Mode = TracingMode.BlackAndWhite },
        },
    ];

    /// <summary>
    /// HEVC / AV1 video, ALAC and the subtitle targets (added in settings schema 11, see SettingsMigrations).
    /// </summary>
    public static List<Preset> CodecAndSubtitlePresets() =>
    [
        // Video codecs and lossless audio
        new()
        {
            Id = "mp4-hevc", Name = "MP4 HEVC", TargetFormat = "mp4",
            Media = { VideoCodec = VideoCodec.Hevc }, Output = { FileNamePattern = "{name}_hevc" },
        },
        new()
        {
            Id = "mp4-av1", Name = "MP4 AV1", TargetFormat = "mp4",
            Media = { VideoCodec = VideoCodec.Av1 }, Output = { FileNamePattern = "{name}_av1" },
        },
        new() { Id = "to-alac", Name = "ALAC", TargetFormat = "m4a", Media = { AudioCodec = AudioCodec.Alac } },

        // Subtitles; on the video donut, SRT takes the first text subtitle track out of the video.
        new() { Id = "extract-subtitles", NameKey = "preset.extract_subtitles", TargetFormat = "srt" },
        new() { Id = "to-srt", Name = "SRT", TargetFormat = "srt" },
        new() { Id = "to-vtt", Name = "WebVTT", TargetFormat = "vtt" },
        new() { Id = "to-ass", Name = "ASS", TargetFormat = "ass" },
        new() { Id = "to-smi", Name = "SAMI", TargetFormat = "smi" },
        new() { Id = "to-lrc", Name = "LRC", TargetFormat = "lrc" },
        new() { Id = "to-ttml", Name = "TTML", TargetFormat = "ttml" },
    ];

    /// <summary>HWP as a target, through HWPX (added in settings schema 7, see SettingsMigrations).</summary>
    public static List<Preset> HwpPresets() =>
    [
        new() { Id = "to-hwp", Name = "HWP", TargetFormat = "hwp" },
    ];

    /// <summary>Spreadsheet targets (added in settings schema 5, see SettingsMigrations).</summary>
    public static List<Preset> SpreadsheetPresets() =>
    [
        new() { Id = "to-xlsx", Name = "XLSX", TargetFormat = "xlsx" },
        new() { Id = "to-csv", Name = "CSV", TargetFormat = "csv" },
    ];

    /// <summary>3D model targets (added in settings schema 15, see SettingsMigrations). glTF is on no donut: GLB is the same in one file.</summary>
    public static List<Preset> ModelPresets() =>
    [
        new() { Id = "to-glb", Name = "GLB", TargetFormat = "glb" },
        new() { Id = "to-stl", Name = "STL", TargetFormat = "stl" },
        new() { Id = "to-obj", Name = "OBJ", TargetFormat = "obj" },
        new() { Id = "to-3mf", Name = "3MF", TargetFormat = "3mf" },
        new() { Id = "to-ply", Name = "PLY", TargetFormat = "ply" },
        new() { Id = "to-gltf", Name = "glTF", TargetFormat = "gltf" },
    ];

    /// <summary>
    /// Targets of the formats added in settings schema 6: images, vector, office, e-books, video, audio, archives,
    /// CAD and fonts (see SettingsMigrations).
    /// </summary>
    public static List<Preset> CatalogPresets() =>
    [
        // Images and vector graphics
        new() { Id = "to-jxl", Name = "JPEG XL", TargetFormat = "jxl", Image = { Quality = 85 } },
        new() { Id = "to-psd", Name = "PSD", TargetFormat = "psd" },
        new() { Id = "to-tga", Name = "TGA", TargetFormat = "tga" },
        new() { Id = "to-icns", Name = "ICNS", TargetFormat = "icns" },
        new() { Id = "to-svg", Name = "SVG", TargetFormat = "svg" },
        new() { Id = "to-eps", Name = "EPS", TargetFormat = "eps" },

        // Office and text
        new() { Id = "to-ods", Name = "ODS", TargetFormat = "ods" },
        new() { Id = "to-pptx", Name = "PPTX", TargetFormat = "pptx" },
        new() { Id = "to-odp", Name = "ODP", TargetFormat = "odp" },

        // E-books
        new() { Id = "to-epub", Name = "EPUB", TargetFormat = "epub" },
        new() { Id = "to-mobi", Name = "MOBI", TargetFormat = "mobi" },
        new() { Id = "to-azw3", Name = "AZW3", TargetFormat = "azw3" },
        new() { Id = "to-kepub", Name = "KEPUB", TargetFormat = "kepub" },
        new() { Id = "to-fb2", Name = "FB2", TargetFormat = "fb2" },
        new() { Id = "to-cbz", Name = "CBZ", TargetFormat = "cbz" },

        // Video
        new() { Id = "to-mp4", Name = "MP4", TargetFormat = "mp4" },
        new()
        {
            Id = "mp4-720", Name = "MP4 720p", TargetFormat = "mp4",
            Media = { MaxHeight = 720, Quality = MediaQuality.Small }, Output = { FileNamePattern = "{name}_720p" },
        },
        new() { Id = "to-webm", Name = "WEBM", TargetFormat = "webm" },
        new() { Id = "to-mov", Name = "MOV", TargetFormat = "mov" },
        new() { Id = "to-mkv", Name = "MKV", TargetFormat = "mkv" },
        new() { Id = "to-avi", Name = "AVI", TargetFormat = "avi" },

        // Audio
        new() { Id = "to-mp3", Name = "MP3", TargetFormat = "mp3" },
        new()
        {
            Id = "mp3-128", Name = "MP3 128k", TargetFormat = "mp3",
            Media = { AudioBitrateKbps = 128 }, Output = { FileNamePattern = "{name}_128k" },
        },
        new() { Id = "to-m4a", Name = "M4A", TargetFormat = "m4a" },
        new() { Id = "to-wav", Name = "WAV", TargetFormat = "wav" },
        new() { Id = "to-flac", Name = "FLAC", TargetFormat = "flac" },
        new() { Id = "to-ogg", Name = "OGG", TargetFormat = "ogg" },
        new() { Id = "to-opus", Name = "OPUS", TargetFormat = "opus" },
        new() { Id = "to-aac", Name = "AAC", TargetFormat = "aac" },

        // Archives
        new() { Id = "to-zip", Name = "ZIP", TargetFormat = "zip" },
        new() { Id = "to-7z", Name = "7Z", TargetFormat = "7z" },
        new() { Id = "to-tgz", Name = "TAR.GZ", TargetFormat = "tgz" },
        new() { Id = "to-tar", Name = "TAR", TargetFormat = "tar" },
        new() { Id = "extract", NameKey = "preset.extract", TargetFormat = FormatRegistry.Folder },
        new()
        {
            Id = "zip-all", NameKey = "preset.zip_all", TargetFormat = "zip",
            Archive = { CombineIntoOne = true }, Output = { FileNamePattern = "{name}_files" },
        },

        // CAD
        new() { Id = "to-dxf", Name = "DXF", TargetFormat = "dxf" },
        new() { Id = "to-dwg", Name = "DWG", TargetFormat = "dwg" },

        // Fonts
        new() { Id = "to-ttf", Name = "TTF", TargetFormat = "ttf" },
        new() { Id = "to-otf", Name = "OTF", TargetFormat = "otf" },
        new() { Id = "to-woff", Name = "WOFF", TargetFormat = "woff" },
        new() { Id = "to-woff2", Name = "WOFF2", TargetFormat = "woff2" },
        new() { Id = "to-eot", Name = "EOT", TargetFormat = "eot" },
    ];

    /// <summary>
    /// Rotate and flip presets, and the two groups that fan them out on the donut (added in settings schema 17). On no
    /// default donut: users add "Rotate" / "Flip" (or single presets) in Settings → Toolbar.
    /// </summary>
    public static List<Preset> ImageToolPresets() =>
    [
        Tool("rotate-left", "preset.rotate_left", "{name}_left", i => i.Rotation = ImageRotation.Left90),
        Tool("rotate-right", "preset.rotate_right", "{name}_right", i => i.Rotation = ImageRotation.Right90),
        Tool("rotate-180", "preset.rotate_180", "{name}_180", i => i.Rotation = ImageRotation.Rotate180),
        Tool("flip-horizontal", "preset.flip_horizontal", "{name}_mirrored", i => i.Flip = ImageFlip.Horizontal),
        Tool("flip-vertical", "preset.flip_vertical", "{name}_flipped", i => i.Flip = ImageFlip.Vertical),
        new()
        {
            Id = "rotate", NameKey = "preset.group_rotate", TargetFormat = Preset.GroupTarget, Kind = PresetKind.Group,
            SubPresetIds = ["rotate-left", "rotate-right", "rotate-180"],
        },
        new()
        {
            Id = "flip", NameKey = "preset.group_flip", TargetFormat = Preset.GroupTarget, Kind = PresetKind.Group,
            SubPresetIds = ["flip-horizontal", "flip-vertical"],
        },
    ];

    /// <summary>
    /// A rotate / flip preset. A JPG (or WEBP, AVIF) is encoded again when it turns: at 95 rather than the usual 90, so
    /// turning a good photo doesn't visibly cost it quality.
    /// </summary>
    private static Preset Tool(string id, string nameKey, string pattern, Action<ImageOptions> set)
    {
        var preset = new Preset
        {
            Id = id,
            NameKey = nameKey,
            TargetFormat = SameAsSource,
            Image = { Quality = 95 },
            Output = { FileNamePattern = pattern },
        };
        set(preset.Image);
        return preset;
    }

    /// <summary>
    /// Structured data and contacts targets (added in settings schema 14, see SettingsMigrations). EML is for Outlook
    /// MSG files, vCard for contact lists in CSV; neither is on a donut.
    /// </summary>
    public static List<Preset> DataPresets() =>
    [
        new() { Id = "to-json", Name = "JSON", TargetFormat = "json" },
        new() { Id = "to-yaml", Name = "YAML", TargetFormat = "yaml" },
        new() { Id = "to-toml", Name = "TOML", TargetFormat = "toml" },
        new() { Id = "to-xml", Name = "XML", TargetFormat = "xml" },
        new() { Id = "to-eml", Name = "EML", TargetFormat = "eml" },
        new() { Id = "to-vcf", Name = "vCard", TargetFormat = "vcf" },
    ];

    /// <summary>Creates the default toolbar profiles.</summary>
    public static List<ToolbarProfile> CreateProfiles() =>
    [
        new()
        {
            Id = "images", NameKey = "profile.images",
            Extensions = ExtensionsOf(FormatCategory.Image),
            PresetIds = ["to-png", "to-jpg", "to-webp", "to-pdf", "trace-svg", "trace-line-art", "to-tiff", "to-bmp", "to-ico", "half-size"],
        },
        new()
        {
            Id = "pdf", NameKey = "profile.pdf",
            Extensions = ["pdf"],
            PresetIds = ["to-png", "to-jpg", "pdf-long-image", "to-docx", "to-txt", "to-md", "to-epub", "to-hwpx", "to-hwp", "to-tiff", "pdf-split", "merge-pdf"],
        },
        new()
        {
            Id = "office", NameKey = "profile.office",
            Extensions = ExtensionsOf(FormatCategory.Document),
            PresetIds = ["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-odt", "to-txt", "to-html", "to-epub", "to-png", "merge-pdf"],
        },
        SpreadsheetProfile(),
        PresentationProfile(),
        new()
        {
            Id = "hwp", NameKey = "profile.hwp",
            Extensions = ["hwp", "hwpx"],
            PresetIds = ["to-pdf", "to-docx", "to-hwpx", "to-hwp", "to-png", "to-jpg", "to-txt", "to-md", "to-epub", "merge-pdf"],
        },
        TextProfile(),
        .. WithSubtitleProfile(CatalogProfiles()),
        DataProfile(),
        ModelProfile(),
        new()
        {
            Id = "mixed", NameKey = "profile.mixed", IsFallback = true,
            PresetIds = ["to-pdf", "merge-pdf", "zip-all", "to-png", "to-txt"],
        },
    ];

    /// <summary>Excel, OpenDocument and CSV sheets (split from "office" in settings schema 5).</summary>
    public static ToolbarProfile SpreadsheetProfile() => new()
    {
        Id = "spreadsheets",
        NameKey = "profile.spreadsheets",
        Extensions = ExtensionsOf(FormatCategory.Spreadsheet),
        PresetIds = ["to-pdf", "to-xlsx", "to-csv", "to-json", "to-ods", "to-hwpx", "to-hwp", "to-html", "merge-pdf"],
    };

    /// <summary>PowerPoint and OpenDocument slides (split from "office" in settings schema 5).</summary>
    public static ToolbarProfile PresentationProfile() => new()
    {
        Id = "presentations",
        NameKey = "profile.presentations",
        Extensions = ExtensionsOf(FormatCategory.Presentation),
        PresetIds = ["to-pdf", "to-png", "to-jpg", "to-pptx", "to-hwpx", "to-hwp", "merge-pdf"],
    };

    /// <summary>Markdown, plain text and HTML (added in settings schema 4, see SettingsMigrations).</summary>
    public static ToolbarProfile TextProfile() => new()
    {
        Id = "text",
        NameKey = "profile.text",
        Extensions = ExtensionsOf(FormatCategory.Text),
        PresetIds = ["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-html", "to-txt", "to-epub"],
    };

    /// <summary>Donuts of the categories added in settings schema 6, in the order they follow "text".</summary>
    public static List<ToolbarProfile> CatalogProfiles() =>
    [
        new()
        {
            Id = "ebooks", NameKey = "profile.ebooks",
            Extensions = ExtensionsOf(FormatCategory.Ebook),
            PresetIds = ["to-pdf", "to-epub", "to-docx", "to-txt", "to-azw3", "to-mobi", "to-kepub", "to-hwpx", "to-hwp", "to-html"],
        },
        new()
        {
            Id = "video", NameKey = "profile.video",
            Extensions = ExtensionsOf(FormatCategory.Video),
            PresetIds = ["to-mp4", "mp4-720", "mp4-hevc", "to-webm", "to-mov", "to-gif", "to-mp3", "extract-subtitles", "to-mkv", "to-avi"],
        },
        new()
        {
            Id = "audio", NameKey = "profile.audio",
            Extensions = ExtensionsOf(FormatCategory.Audio),
            PresetIds = ["to-mp3", "mp3-128", "to-m4a", "to-wav", "to-flac", "to-ogg", "to-opus", "to-aac"],
        },
        new()
        {
            Id = "vector", NameKey = "profile.vector",
            Extensions = ExtensionsOf(FormatCategory.Vector),
            PresetIds = ["to-png", "to-pdf", "to-svg", "to-jpg", "to-webp", "to-eps"],
        },
        new()
        {
            Id = "archives", NameKey = "profile.archives",
            Extensions = ExtensionsOf(FormatCategory.Archive),
            PresetIds = ["extract", "to-zip", "to-7z", "to-tgz", "to-tar"],
        },
        new()
        {
            Id = "cad", NameKey = "profile.cad",
            Extensions = ExtensionsOf(FormatCategory.Cad),
            PresetIds = ["to-pdf", "to-png", "to-svg", "to-dxf", "to-dwg"],
        },
        new()
        {
            Id = "fonts", NameKey = "profile.fonts",
            Extensions = ExtensionsOf(FormatCategory.Font),
            PresetIds = ["to-woff2", "to-woff", "to-ttf", "to-otf", "to-eot"],
        },
    ];

    /// <summary>JSON, YAML, TOML, XML and vCard files (added in settings schema 14, after "fonts").</summary>
    public static ToolbarProfile DataProfile() => new()
    {
        Id = "data",
        NameKey = "profile.data",
        Extensions = ExtensionsOf(FormatCategory.Data),
        PresetIds = ["to-json", "to-yaml", "to-toml", "to-xml", "to-csv", "to-xlsx"],
    };

    /// <summary>Text subtitles: SRT, ASS, WebVTT, SAMI, LRC, ... (added in settings schema 11, after "audio").</summary>
    public static ToolbarProfile SubtitleProfile() => new()
    {
        Id = "subtitles",
        NameKey = "profile.subtitles",
        Extensions = ExtensionsOf(FormatCategory.Subtitle),
        PresetIds = ["to-srt", "to-vtt", "to-ass", "to-smi", "to-lrc", "to-ttml"],
    };

    /// <summary><paramref name="profiles"/> with the subtitle donut after the audio donut.</summary>
    private static List<ToolbarProfile> WithSubtitleProfile(List<ToolbarProfile> profiles)
    {
        profiles.Insert(profiles.FindIndex(p => p.Id == "audio") + 1, SubtitleProfile());
        return profiles;
    }

    /// <summary>3D models: GLB, glTF, STL, OBJ, 3MF, PLY (added in settings schema 15, after "data").</summary>
    public static ToolbarProfile ModelProfile() => new()
    {
        Id = "models",
        NameKey = "profile.models",
        Extensions = ExtensionsOf(FormatCategory.Model3D),
        PresetIds = ["to-glb", "to-stl", "to-obj", "to-3mf", "to-ply", "to-png"],
    };

    /// <summary>Every file extension of the formats in <paramref name="category"/>.</summary>
    public static List<string> ExtensionsOf(FormatCategory category) =>
        FormatRegistry.Known.Where(f => f.Category == category)
            .SelectMany(f => f.Extensions)
            .Where(e => e.Length > 0)
            .ToList();
}
