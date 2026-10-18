// Labels for the update buttons and status lines, shared by the sidebar, the tray, Settings > General and the notice.

using Filee.Core.Localization;

namespace Filee.App.Services;

public static class UpdateTexts
{
    /// <summary>Button text: "Update" in an installed copy (it installs by itself), "Download" in a portable one.</summary>
    public static string ButtonKey => UpdateService.IsInstalled ? "update.install" : "update.download";

    /// <summary>What the update button does, for the notice and tooltips.</summary>
    public static string HowKey => UpdateService.IsInstalled ? "update.notice_text" : "update.notice_text_portable";

    /// <summary>Short label for the sidebar button and the tray menu ("Update to 1.4.0", "Downloading… 42%").</summary>
    public static string Short(UpdateService updates, ILocalizer loc) => updates.Step switch
    {
        UpdateStep.Downloading => loc.Format("update.downloading", updates.DownloadPercent),
        UpdateStep.Installing => loc["update.installing_short"],
        _ => updates.LatestVersion is { } latest ? loc.Format("update.sidebar", latest) : "",
    };

    /// <summary>A sentence about the update in progress, or null when nothing is going on.</summary>
    public static string? Status(UpdateService updates, ILocalizer loc) => updates.Step switch
    {
        UpdateStep.Downloading => loc.Format("update.downloading", updates.DownloadPercent),
        UpdateStep.Installing => loc["update.installing"],
        UpdateStep.Failed => loc["update.failed"],
        UpdateStep.Cancelled => loc["update.cancelled"],
        _ => null,
    };

    /// <summary>
    /// What the update changes in the app's language, one "• " line per change, newest version first (at most
    /// <paramref name="max"/> lines); empty when the releases say nothing.
    /// </summary>
    public static string Changes(UpdateService updates, ILocalizer loc, int max = 6)
    {
        var lines = updates.Notes.SelectMany(n => n.In(loc.Language)).Distinct().ToList();
        var shown = lines.Take(max).Select(l => "• " + l).ToList();
        if (lines.Count > max)
            shown.Add(loc.Format("update.more_changes", lines.Count - max));
        return string.Join(Environment.NewLine, shown);
    }

    /// <summary>True for property changes that alter the texts above.</summary>
    public static bool Affects(string? propertyName) =>
        propertyName is nameof(UpdateService.LatestVersion) or nameof(UpdateService.Step) or nameof(UpdateService.DownloadPercent)
            or nameof(UpdateService.Notes);
}
