// Decides whether a preset can run for a set of files and why not — used to grey out donut slices.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Services;

public sealed class PresetAvailability(ConverterCatalog catalog, ILocalizer loc, EngineDownloadService downloads)
{
    /// <summary>
    /// Returns <c>null</c> when at least one of <paramref name="formats"/> can be converted with the preset,
    /// otherwise a localized reason — naming the optional engine to download when that would help.
    /// </summary>
    public string? Check(Preset preset, IReadOnlyList<FileFormat> formats)
    {
        // "Compress into one archive" packs the files as they are, so any file works, even of an unknown type.
        if (preset.Archive.CombineIntoOne
            && FormatRegistry.FindById(preset.TargetFormat) is { Category: FormatCategory.Archive } archive
            && archive.Id != FormatRegistry.Folder)
        {
            return catalog.All.OfType<IFileCombiner>().Any(c => c.CanCombineInto(archive.Id)) ? null : loc["error.no_route"];
        }

        if (formats.Count == 0)
            return loc["error.unsupported_source"];

        var merge = preset.Pdf.MergeIntoSingle && preset.TargetFormat == "pdf";
        var turns = preset.Image.TurnsOrMirrors;
        string TargetFor(FileFormat f) => preset.TargetFormat == BuiltInData.SameAsSource ? f.Id : preset.TargetFormat;
        var copiesUnturned = false;
        bool Possible(RoutePlanner planner) => formats.Any(f =>
        {
            var target = TargetFor(f);
            if (merge && f.Id == "pdf" && !turns)
                return true; // merged as it is
            if (!turns)
                return planner.CanConvert(f.Id, target);
            // Rotate / flip: a route only counts when an engine on it turns images (not a PDF or a video copied).
            if (planner.Plan(f.Id, target) is not { } route)
                return false;
            if (route.FirstTransformStep() >= 0)
                return true;
            copiesUnturned = true;
            return false;
        });

        if (Possible(catalog.CreatePlanner()))
            return null;
        // It converts these files, just without turning them: say that, not "no engine" or "download one".
        if (copiesUnturned)
            return loc["error.cannot_turn"];
        return downloads.PackageEnabling(Possible) is { } package
            ? loc.Format("engine.reason.needs_package", package.Name)
            : loc["error.no_route"];
    }
}
