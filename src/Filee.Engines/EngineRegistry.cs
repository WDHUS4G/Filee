// THE place where conversion engines are registered. Adding an engine = one line in CreateAll.

using Filee.Core.Conversion;
using Filee.Engines.Archives;
using Filee.Engines.Cad;
using Filee.Engines.Data;
using Filee.Engines.Ebooks;
using Filee.Engines.Email;
using Filee.Engines.Fonts;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Icns;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Media;
using Filee.Engines.Models3D;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using Filee.Engines.Subtitles;
using Filee.Engines.Text;
using Filee.Engines.Vector;

namespace Filee.Engines;

/// <summary>Creates every built-in converter.</summary>
public static class EngineRegistry
{
    /// <summary>
    /// Instantiates all converters. Order only matters as a tie-breaker when no priority is set, so it mirrors
    /// AppSettings.EnginePriority: built-in engines first, the optional downloads last. Software installed on the
    /// system (Microsoft Office, a system-wide LibreOffice) is never used.
    /// </summary>
    public static IReadOnlyList<IConverter> CreateAll(EngineEnvironment env) =>
    [
        new MagickImageConverter(),
        new PdfSharpConverter(),
        new PdfiumConverter(),
        new RhwpConverter(),
        new UnhwpConverter(),
        new MarkdownConverter(),
        new SpreadsheetConverter(),
        new OoxmlConverter(),
        new HwpxConverter(),
        new ArchiveConverter(),
        new VectorConverter(),
        new IcnsConverter(),
        new FontConverter(),
        new DocxWriterConverter(),
        new PdfTextConverter(),
        new EmlConverter(),
        new DataConverter(),
        new SubtitleConverter(),
        new FfmpegConverter(),
        new CadConverter(),
        new ModelConverter(),
        new EbookConverter(),
        new PandocConverter(),
        new GhostscriptConverter(),
        new CalibreConverter(),
        new LibreOfficeConverter(env),
    ];

    /// <summary>Finds the PDF merger among the converters.</summary>
    public static IPdfMerger? FindPdfMerger(IEnumerable<IConverter> converters) =>
        converters.OfType<IPdfMerger>().FirstOrDefault();

    /// <summary>Finds the engine that writes several pages into one multi-page TIFF.</summary>
    public static ITiffMerger? FindTiffMerger(IEnumerable<IConverter> converters) =>
        converters.OfType<ITiffMerger>().FirstOrDefault();

    /// <summary>Finds the engine that packs several files into one archive ("Compress into one archive").</summary>
    public static IFileCombiner? FindFileCombiner(IEnumerable<IConverter> converters) =>
        converters.OfType<IFileCombiner>().FirstOrDefault();
}
