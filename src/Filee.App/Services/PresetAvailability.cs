// Decides whether a preset can run for a set of files and why not — used to grey out donut slices.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Services;

public enum PresetUse { Usable, NeedsPackage, Unusable }

/// <summary>Why a preset can or cannot run; usable presets have no reason.</summary>
public readonly record struct PresetJudgement(PresetUse Use, string? Reason);

public sealed class PresetAvailability
{
    private readonly ConverterCatalog _catalog;
    private readonly ILocalizer _loc;
    private readonly IReadOnlyList<EnginePackageState> _packages;

    public PresetAvailability(ConverterCatalog catalog, ILocalizer loc, EngineDownloadService downloads)
        : this(catalog, loc, downloads.Packages) { }

    internal PresetAvailability(ConverterCatalog catalog, ILocalizer loc, IReadOnlyList<EnginePackageState> packages)
    {
        _catalog = catalog;
        _loc = loc;
        _packages = packages;
    }

    private static readonly HashSet<string> WritableSingleFrameRasterFormats =
    [
        "jpg", "png", "webp", "bmp", "avif", "jxl", "jp2", "tga", "qoi", "dds", "pcx",
    ];
    /// <summary>
    /// Returns <c>null</c> when at least one of <paramref name="formats"/> can be converted with the preset,
    /// otherwise a localized reason — naming the optional engine to download when that would help.
    /// </summary>
    public string? Check(Preset preset, IReadOnlyList<FileFormat> formats) =>
        Evaluate(preset, formats, _catalog.CreatePlanner,
            possible => PackageEnabling(possible, p => _catalog.CreatePlanner(p.Package.ConverterIds))).Reason;

    private EnginePackageState? PackageEnabling(Func<RoutePlanner, bool> possible, Func<EnginePackageState, RoutePlanner> plannerFor) =>
        _packages.FirstOrDefault(p => !p.IsInstalled && possible(plannerFor(p)));

    public PaletteSession ForPalette(IReadOnlyList<FileFormat> formats) => new(this, formats);

    public sealed class PaletteSession
    {
        private readonly PresetAvailability _owner;
        private readonly IReadOnlyList<FileFormat> _formats;
        private RoutePlanner? _installed;
        private readonly Dictionary<EnginePackageState, RoutePlanner> _packagePlanners = [];

        internal PaletteSession(PresetAvailability owner, IReadOnlyList<FileFormat> formats)
        {
            _owner = owner;
            _formats = formats;
        }

        private RoutePlanner Installed() => _installed ??= _owner._catalog.CreatePlanner();

        private EnginePackageState? Enabling(Func<RoutePlanner, bool> possible) =>
            _owner.PackageEnabling(possible, p => _packagePlanners.TryGetValue(p, out var r)
                ? r : _packagePlanners[p] = _owner._catalog.CreatePlanner(p.Package.ConverterIds));

        public PresetJudgement Judge(Preset preset)
        {
            if (preset.IsGroup)
                return new(PresetUse.Usable, null);
            if (preset.IsTool && _formats.Count > 0)
            {
                PresetJudgement first = default;
                foreach (var format in _formats)
                {
                    var result = _owner.Evaluate(preset, [format], Installed, Enabling);
                    if (result.Use == PresetUse.Usable)
                        return result;
                    if (first == default)
                        first = result;
                }
                return first;
            }
            return _owner.Evaluate(preset, _formats, Installed, Enabling);
        }
    }

    private PresetJudgement Evaluate(Preset preset, IReadOnlyList<FileFormat> formats,
        Func<RoutePlanner> installedPlanner, Func<Func<RoutePlanner, bool>, EnginePackageState?> packageEnabling)
    {
        if (preset.IsClipTool)
        {
            // Any video among the files: the others are left out when the clip window opens.
            var videos = formats.Where(f => f.Category == FormatCategory.Video).ToList();
            if (videos.Count == 0)
                return new(PresetUse.Unusable, _loc["error.unsupported_source"]);
            bool CanCut(RoutePlanner planner) => videos.Any(f => ClipTool.CanCut(f.Id, planner));
            if (CanCut(installedPlanner()))
                return new(PresetUse.Usable, null);
            return packageEnabling(CanCut) is { } needed
                ? new(PresetUse.NeedsPackage, _loc.Format("engine.reason.needs_package", needed.Name))
                : new(PresetUse.Unusable, _loc["error.no_route"]);
        }

        if (preset.IsTool)
        {
            if (preset.Id == "tool-sheet-studio")
            {
                if (formats.Count > 0 && formats.All(format => format.Id == "xlsx"))
                {
                    var libreOffice = _catalog.All.FirstOrDefault(converter => converter.Id == "libreoffice");
                    if (libreOffice?.GetStatus().IsAvailable == true)
                        return new(PresetUse.Usable, null);
                    var libreOfficePackage = _packages.FirstOrDefault(item => !item.IsInstalled
                        && item.Package.ConverterIds.Contains("libreoffice"));
                    return libreOfficePackage is not null
                        ? new(PresetUse.NeedsPackage, _loc.Format("engine.reason.needs_package", libreOfficePackage.Name))
                        : new(PresetUse.Unusable, _loc["error.no_route"]);
                }
                return new(PresetUse.Unusable, _loc["error.unsupported_source"]);
            }
            if (preset.Id.StartsWith("tool-pdf-", StringComparison.Ordinal))
                return formats.Count > 0 && formats.All(f => f.Id == "pdf")
                    ? new(PresetUse.Usable, null)
                    : new(PresetUse.Unusable, _loc["error.unsupported_source"]);
            if (preset.IsCropTool && formats.Count > 0 && formats.All(f => WritableSingleFrameRasterFormats.Contains(f.Id)))
                return new(PresetUse.Usable, null);
            return new(PresetUse.Unusable, _loc["error.unsupported_source"]);
        }

        // "Compress into one archive" packs the files as they are, so any file works, even of an unknown type.
        if (preset.PacksIntoOneArchive)
        {
            var archive = FormatRegistry.Get(preset.TargetFormat).Id;
            return _catalog.All.OfType<IFileCombiner>().Any(c => c.CanCombineInto(archive))
                ? new(PresetUse.Usable, null) : new(PresetUse.Unusable, _loc["error.no_route"]);
        }

        if (formats.Count == 0)
            return new(PresetUse.Unusable, _loc["error.unsupported_source"]);

        // "Trim transparent borders" on JPG and other formats without transparency would only re-encode them.
        if (preset.Image.TrimTransparent
            && formats.All(f => !Filee.Engines.Magick.ImageEncoder.HoldsAlpha(preset.TargetFormat == BuiltInData.SameAsSource ? f.Id : preset.TargetFormat)))
            return new(PresetUse.Unusable, _loc["error.no_transparency"]);
        // "Merge videos" / "Merge audio" / "Video from images": FFmpeg joins the files, so a route into the target (or,
        // for pictures, into PNG and from PNG into a video) says whether it is installed.
        if (preset.JoinsIntoOneMedia)
        {
            var target = preset.TargetFormat;
            bool Joinable(RoutePlanner planner) => formats.Any(f => preset.Media.Slideshow
                ? f.Category is FormatCategory.Image or FormatCategory.Vector
                  && (f.Id == "png" || planner.CanConvert(f.Id, "png")) && planner.CanConvert("png", target)
                : (f.Category == FormatCategory.Video || f.Category == FormatCategory.Audio && target == "m4a")
                  && planner.CanConvert(f.Id, target));
            if (Joinable(installedPlanner()))
                return new(PresetUse.Usable, null);
            return packageEnabling(Joinable) is { } joiner
                ? new(PresetUse.NeedsPackage, _loc.Format("engine.reason.needs_package", joiner.Name))
                : new(PresetUse.Unusable, _loc["error.no_route"]);
        }

        var merge = preset.Pdf.MergeIntoSingle && preset.TargetFormat == "pdf";
        var turns = preset.Image.TurnsOrMirrors;
        string TargetFor(FileFormat f) => preset.TargetFormat == BuiltInData.SameAsSource ? f.Id : preset.TargetFormat;
        var copiesUnturned = false;
        bool Possible(RoutePlanner planner) => formats.Any(f =>
        {
            var target = TargetFor(f);
            if (merge && f.Id == "pdf" && !turns && !preset.RecognizesTextOf(f.Id))
                return true; // merged as it is
            var frames = preset.SavesFramesOf(f.Id);
            var motion = preset.ExtractsMotionOf(f.Id);
            var live = preset.MakesLivePhotoOf(f.Id);
            var ocr = preset.RecognizesTextOf(f.Id);
            if (!turns)
                return planner.CanConvert(f.Id, target, frames, motionPhoto: motion, livePhoto: live, ocr: ocr);
            // Rotate / flip: a route only counts when an engine on it turns images (not a PDF or a video copied).
            if (planner.Plan(f.Id, target, frames, motionPhoto: motion, livePhoto: live, ocr: ocr) is not { } route)
                return false;
            if (route.FirstTransformStep() >= 0)
                return true;
            copiesUnturned = true;
            return false;
        });

        if (Possible(installedPlanner()))
            return new(PresetUse.Usable, null);
        // It converts these files, just without turning them: say that, not "no engine" or "download one".
        if (copiesUnturned)
            return new(PresetUse.Unusable, _loc["error.cannot_turn"]);
        return packageEnabling(Possible) is { } package
            ? new(PresetUse.NeedsPackage, _loc.Format("engine.reason.needs_package", package.Name))
            : new(PresetUse.Unusable, _loc["error.no_route"]);
    }
}
