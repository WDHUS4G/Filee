// How a file that failed to convert is described in the toast and in Recent conversions.

using Filee.Core.Localization;

namespace Filee.App.ViewModels;

/// <summary>A failed file as listed: its name and the reason.</summary>
public sealed record FailureItem(string Name, string Reason);

internal static class FailureText
{
    /// <summary>"Conversion failed (detail)" in the current language.</summary>
    public static string Reason(ILocalizer loc, string? key, string? detail) =>
        loc[key ?? "error.conversion_failed"] + (string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})");

    /// <summary>
    /// Reads a "file: error.key detail" line as Filee 1.7 and earlier saved it in the history.
    /// </summary>
    public static FailureItem FromLegacy(ILocalizer loc, string line)
    {
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return new FailureItem(line, Reason(loc, null, null));
        var name = line[..colon];
        var rest = line[(colon + 2)..].Trim();
        if (!rest.StartsWith("error.", StringComparison.Ordinal))
            return new FailureItem(name, Reason(loc, null, rest));
        var space = rest.IndexOf(' ');
        return space < 0
            ? new FailureItem(name, Reason(loc, rest, null))
            : new FailureItem(name, Reason(loc, rest[..space], rest[(space + 1)..]));
    }
}
