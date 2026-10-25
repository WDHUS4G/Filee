// The contract every conversion engine implements. Adding a new engine = one class implementing IConverter.
// See docs/ADDING-A-CONVERTER.md.

using Filee.Core.Presets;

namespace Filee.Core.Conversion;

/// <summary>
/// One direct conversion a converter can perform, e.g. <c>docx → pdf</c>.
/// </summary>
/// <param name="From">Source format id.</param>
/// <param name="To">Target format id. May equal <paramref name="From"/> (e.g. re-encode a JPG at lower quality).</param>
/// <param name="Cost">
/// Relative cost used by <see cref="RoutePlanner"/>. Use 10 for a normal direct conversion,
/// higher for lossy or slow paths. Multi-step routes add up their costs.
/// </param>
/// <param name="ImageSourcesOnly">
/// The edge is used only in routes that start from a raster image (<see cref="Formats.FormatCategory.Image"/>): image
/// tracing may follow ICNS → PNG, but must never redraw a PDF, a vector drawing or a video frame that became a PNG.
/// </param>
/// <param name="NotForImageSources">
/// The opposite: never used in routes that start from a raster image. PDF → SVG through LibreOffice keeps a PDF's
/// vectors, but a photo that became a PDF would only come back as a picture inside an SVG, so images are traced.
/// </param>
/// <param name="DirectOnly">
/// The edge is a route of its own only: the file being converted is a <paramref name="From"/> file and
/// <paramref name="To"/> is what the user asked for. PDF → PPTX puts pictures of the pages on slides; a Word document
/// rendered to PDF must not become pictures on slides, and a picture deck must not be a step towards ODP or PPSX.
/// </param>
/// <param name="FrameSequence">
/// The edge writes every frame of a video as numbered pictures into a folder (<see cref="Presets.MediaOptions.SaveEveryFrame"/>).
/// It is never part of an ordinary route (a video → JPG stays one still frame), and with
/// <see cref="RoutePlanner.Plan(string, string, bool, bool, bool, bool, bool)"/> asking for frames it is the whole route.
/// </param>
/// <param name="Unshaped">
/// The engine draws text one character after the other, left to right, without complex-script shaping (no joined
/// Arabic letters, no right-to-left order, no reordered vowel signs, conjuncts or placed marks). A source whose text
/// needs shaping is never routed through the edge: <see cref="JobQueue"/> asks the engine
/// (<see cref="ITextShapingCheck"/>) and plans again without it. Text the engine draws but has no widths for
/// (letters outside ASCII and Latin-1 other than full-width CJK — Polish, Turkish, Vietnamese, Greek, Cyrillic —
/// rhwp#8143) takes a route without the edge only when one exists.
/// </param>
/// <param name="MotionPhoto">
/// The edge takes the video out of a Live / Motion Photo (<see cref="Presets.MediaOptions.ExtractMotionPhoto"/>). It is
/// never part of an ordinary route (a photo → MP4 is otherwise a video of the still picture), and with
/// <see cref="RoutePlanner.Plan(string, string, bool, bool, bool, bool, bool)"/> asking for it it is the whole route.
/// </param>
/// <param name="LivePhoto">
/// The edge makes a Live / Motion Photo of a short clip of a video (<see cref="Presets.MediaOptions.LivePhoto"/>). It is
/// never part of an ordinary route (a video → JPG is otherwise one still frame), and with
/// <see cref="RoutePlanner.Plan(string, string, bool, bool, bool, bool, bool)"/> asking for it it is the whole route.
/// </param>
/// <param name="Ocr">
/// The edge recognizes the text of a scan or a picture and writes a searchable PDF (<see cref="Presets.PdfOptions.Ocr"/>).
/// It is never part of an ordinary route, and with <see cref="RoutePlanner.Plan(string, string, bool, bool, bool, bool, bool)"/>
/// asking for it the route starts with it.
/// </param>
public readonly record struct ConversionEdge(
    string From, string To, int Cost = 10, bool ImageSourcesOnly = false, bool NotForImageSources = false,
    bool DirectOnly = false, bool FrameSequence = false, bool Unshaped = false, bool MotionPhoto = false, bool LivePhoto = false,
    bool Ocr = false);

/// <summary>
/// A conversion an engine refuses with a reason the user can act on, shown in their language (the file's
/// <see cref="FileResult.ErrorKey"/>) instead of "The engine reported an error".
/// </summary>
/// <param name="errorKey">Localization key of the reason, e.g. <c>error.too_many_frames</c>.</param>
/// <param name="detail">Non-localized detail shown after it in brackets (numbers, a name), or null.</param>
public sealed class ConversionRefusedException(string errorKey, string? detail = null)
    : Exception(detail is null ? errorKey : $"{errorKey} ({detail})")
{
    public string ErrorKey { get; } = errorKey;

    public string? Detail { get; } = detail;
}

/// <summary>Whether an engine can currently be used.</summary>
/// <param name="IsAvailable">True if conversions can run.</param>
/// <param name="ReasonKey">Localization key explaining why the engine is unavailable (e.g. <c>engine.reason.not_installed</c>).</param>
/// <param name="Detail">Extra, non-localized detail such as the program's path (shown as a tooltip).</param>
/// <param name="Version">Version label shown in Settings → Engines, e.g. <c>"Markdig 1.4.0"</c> or <c>"0.8.6"</c>.</param>
public sealed record EngineStatus(bool IsAvailable, string? ReasonKey = null, string? Detail = null, string? Version = null)
{
    public static EngineStatus Available(string? detail = null, string? version = null) => new(true, null, detail, version);
    public static EngineStatus Unavailable(string reasonKey, string? detail = null) => new(false, reasonKey, detail);
}

/// <summary>Hands out output paths to a converter.</summary>
public interface IOutputAllocator
{
    /// <summary>
    /// Returns the path the converter should write the next output file to.
    /// </summary>
    /// <param name="extension">
    /// Extension of the file being written, without dot. Empty asks for a folder path (archive extraction); the
    /// converter creates the folder.
    /// </param>
    /// <param name="suffix">Optional name suffix such as <c>"_p2"</c> when one input produces several files.</param>
    /// <returns>A path, or <c>null</c> if this output must be skipped (existing file + "skip" policy).</returns>
    string? Allocate(string extension, string? suffix = null);
}

/// <summary>Everything a converter needs to perform one step.</summary>
/// <param name="InputPath">File to read.</param>
/// <param name="From">Format id of <paramref name="InputPath"/>.</param>
/// <param name="To">Format id to produce.</param>
/// <param name="Preset">Options chosen by the user.</param>
/// <param name="Output">Where to write results.</param>
/// <param name="WorkDirectory">Private scratch directory for this job; deleted afterwards.</param>
/// <param name="FrameSequence">
/// The step takes a <see cref="ConversionEdge.FrameSequence"/> edge: every frame of the video becomes a picture of
/// format <paramref name="To"/> in one output folder.
/// </param>
/// <param name="Notice">
/// Records a note on a step that worked, as a localization key (e.g. <c>notice.pdf_not_smaller</c>): shown with the
/// file's result. Null when nobody listens.
/// </param>
/// <param name="MissingFonts">
/// Reports document font families that aren't installed (and have no known metric-compatible installed substitute).
/// Rendering continues: graphical clients can offer trusted sources without blocking CLI or watched jobs.
/// </param>
/// <param name="OriginalSourcePath">The user's original input, before any intermediate conversion steps.</param>
/// <param name="MotionPhoto">
/// The step takes a <see cref="ConversionEdge.MotionPhoto"/> edge: the video of the Live / Motion Photo becomes the
/// output, not its still picture.
/// </param>
/// <param name="LivePhoto">
/// The step takes a <see cref="ConversionEdge.LivePhoto"/> edge: a clip of the video becomes a Live / Motion Photo
/// (<see cref="Presets.MediaOptions.LivePhoto"/> says which kind).
/// </param>
public sealed record ConversionStep(
    string InputPath,
    string From,
    string To,
    Preset Preset,
    IOutputAllocator Output,
    string WorkDirectory,
    bool FrameSequence = false,
    Action<string>? Notice = null,
    bool MotionPhoto = false,
    bool LivePhoto = false,
    Action<IReadOnlyList<string>>? MissingFonts = null,
    string? OriginalSourcePath = null);

/// <summary>
/// A conversion engine (ImageMagick, LibreOffice, rhwp, ...).
/// </summary>
/// <remarks>
/// Implementations must be thread-safe up to <see cref="MaxParallelism"/> concurrent calls,
/// must honour the cancellation token, and must never modify the input file.
/// </remarks>
public interface IConverter
{
    /// <summary>Stable id used in settings (engine priority, custom paths). Lower-case, no spaces.</summary>
    string Id { get; }

    /// <summary>Human readable engine name (not localized, e.g. "LibreOffice").</summary>
    string DisplayName { get; }

    /// <summary>Direct conversions this engine supports.</summary>
    IReadOnlyList<ConversionEdge> Edges { get; }

    /// <summary>How many conversions may run at the same time. 0 means "one per CPU core".</summary>
    int MaxParallelism { get; }

    /// <summary>Checks if the engine is installed / usable. Should be cheap; results may be cached.</summary>
    EngineStatus GetStatus();

    /// <summary>Performs one conversion step and returns the files that were written.</summary>
    Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// An engine that turns and mirrors images (<see cref="ImageOptions.Rotation"/>, <see cref="ImageOptions.Flip"/>) in
/// some of its conversions. A route through none of those can't rotate or flip: <see cref="JobQueue"/> refuses it
/// rather than write the file unturned (a PDF or a video copied "as rotated"), and it turns the image in the first
/// step that can, not again in a later one.
/// </summary>
public interface IImageTransformer
{
    /// <summary>True if converting <paramref name="from"/> → <paramref name="to"/> applies the rotation and flip.</summary>
    bool Transforms(string from, string to);
}

/// <summary>
/// The scripts of a file's text an engine would draw wrong, from one scan.
/// </summary>
/// <param name="NeedsShaping">
/// The name of the first script that needs complex-script shaping (e.g. "Gujarati"), or null. Takes precedence:
/// <see cref="JobQueue"/> refuses a file with it (or routes it around the engine), never merely reroutes it for
/// <paramref name="WithoutWidths"/>.
/// </param>
/// <param name="WithoutWidths">
/// The name of the first script the engine draws but has no widths for (e.g. "Latin Extended" or "Cyrillic"), or
/// null. Weaker than <paramref name="NeedsShaping"/>: such a file is not refused for it, <see cref="JobQueue"/>
/// only takes a route without the unshaped edge when one exists.
/// </param>
public sealed record TextScripts(string? NeedsShaping, string? WithoutWidths);

/// <summary>
/// An engine with <see cref="ConversionEdge.Unshaped"/> edges: tells whether a source file has text that needs
/// complex-script shaping (Arabic, Hebrew, the Indic scripts, Thai, ...), which those edges would draw broken,
/// or text the engine has no widths for (letters outside ASCII and Latin-1 other than full-width CJK — Polish,
/// Turkish, Vietnamese, Greek, Cyrillic, ...), which they lay out wrong.
/// </summary>
public interface ITextShapingCheck
{
    /// <summary>
    /// Scans the text of <paramref name="sourcePath"/> once and answers both questions. Quick: it scans the file's
    /// text, it doesn't convert it — and it is asked again when the file is converted again, never cached.
    /// </summary>
    /// <param name="sourceFormat">Format id of the file.</param>
    TextScripts ScanScripts(string sourcePath, string sourceFormat);
}

/// <summary>Merges several PDF files into one. Implemented by the PDF engine.</summary>
public interface IPdfMerger
{
    Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// Adds a watermark and page numbers to a finished PDF and locks it with a password
/// (<see cref="Presets.PdfOptions.FinishesPdf"/>). Implemented by the PDF engine; <see cref="JobQueue"/> runs it on
/// every PDF a job writes, whichever engine made it, and on a merged PDF once, after merging.
/// </summary>
public interface IPdfFinisher
{
    /// <summary>Rewrites <paramref name="path"/> in place.</summary>
    Task FinishAsync(string path, Presets.PdfOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Writes TIFF pages (and every page of multi-page TIFFs) into one multi-page TIFF, in order. Implemented by the
/// ImageMagick engine; <see cref="JobQueue"/> uses it when a preset sets <see cref="Presets.ImageOptions.MultiPageTiff"/>.
/// </summary>
public interface ITiffMerger
{
    Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, Presets.ImageOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Joins several videos or audio files into one, one after the other, or turns pictures into one video ("Merge
/// videos", "Merge audio", "Video from images"). Implemented by the FFmpeg engine; <see cref="JobQueue"/> uses it when
/// a preset joins files (<see cref="Preset.JoinsIntoOneMedia"/>).
/// </summary>
public interface IMediaJoiner
{
    /// <summary>
    /// Writes every file of <paramref name="inputPaths"/>, in order, into <paramref name="outputPath"/>: video or audio
    /// clips back to back, or pictures shown one after the other (<see cref="MediaOptions.Slideshow"/>).
    /// </summary>
    /// <param name="inputPaths">Videos, audio files, or pictures (PNG, as the image engines wrote them).</param>
    /// <param name="outputPath">File to create; removed again when the join fails.</param>
    /// <param name="format">Target format id, <c>"mp4"</c> or <c>"m4a"</c>.</param>
    /// <param name="preset">Options, e.g. <see cref="MediaOptions.SlideSeconds"/>.</param>
    /// <param name="workDirectory">Scratch directory for intermediate files; deleted after the job.</param>
    Task JoinAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Packs several files, unchanged, into one archive ("Compress into one archive"). Implemented by the archive engine;
/// <see cref="JobQueue"/> uses it when a preset sets <see cref="ArchiveOptions.CombineIntoOne"/>.
/// </summary>
public interface IFileCombiner
{
    /// <summary>True if files can be packed into <paramref name="format"/> now (e.g. the tool it needs is installed).</summary>
    bool CanCombineInto(string format);

    /// <summary>Writes every file of <paramref name="inputPaths"/> into one archive of <paramref name="format"/>.</summary>
    /// <param name="inputPaths">Files to pack, stored under their own names (renamed "name (2)" on clashes).</param>
    /// <param name="outputPath">Archive to create; written only when complete.</param>
    /// <param name="format">Archive format id, e.g. <c>"zip"</c>.</param>
    /// <param name="preset">Options, e.g. <see cref="ArchiveOptions.Level"/>.</param>
    /// <param name="workDirectory">Scratch directory for intermediate files; deleted after the job.</param>
    Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken);
}
