// Engines that are downloaded on demand instead of being bundled with the installer (they are large), and the
// pinned download list they come from (engines.json, shared with build/fetch-engines.ps1).

using System.Runtime.InteropServices;
using System.Text.Json;

namespace Filee.Engines.Infrastructure;

/// <summary>One download from engines.json.</summary>
/// <param name="Kind">
/// Archive format: zip, tar.gz, tar.xz, dmg, msi, oxt, conda or nsis.
/// </param>
/// <param name="Size">Download size in bytes.</param>
public sealed record EngineComponent(string Id, string Version, string Url, string Sha256, long Size, string Kind);

/// <summary>A set of components the user installs together, e.g. LibreOffice with its HWP import filter and Java.</summary>
/// <param name="Components">Installed in this order (the LibreOffice extension needs LibreOffice first).</param>
/// <param name="InstalledSize">Approximate size on disk in bytes, shown before installing.</param>
/// <param name="ConverterIds">Engines (<see cref="Filee.Core.Conversion.IConverter.Id"/>) the package provides.</param>
public sealed record EnginePackage(string Id, IReadOnlyList<string> Components, long InstalledSize, IReadOnlyList<string> ConverterIds);

public static class EngineDownloads
{
    /// <summary>The operating system and architecture of the running process.</summary>
    public static string RuntimeIdentifier { get; } =
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// The downloads for this computer. On a Mac older than a download's minimumMacOS (calibre 9 needs macOS 14) that
    /// download is left out, so the package is offered like one Filee has no download of.
    /// </summary>
    public static IReadOnlyDictionary<string, EngineComponent> Components { get; } =
        ComponentsForRuntime(RuntimeIdentifier, OperatingSystem.IsMacOS() ? Environment.OSVersion.Version : null);

    /// <summary>
    /// Packages offered in Settings → Engines and on first run. rhwp and 7-Zip are not listed: they are small and bundled.
    /// </summary>
    public static IReadOnlyList<EnginePackage> Packages { get; } =
    [
        new("libreoffice", OperatingSystem.IsWindows() ? ["libreoffice", "h2orestart", "jre"] : ["libreoffice"], 1_265_000_000, ["libreoffice"]),
        new("pandoc", ["pandoc"], 236_000_000, ["pandoc"]),
        // Ghostscript from conda-forge plus the Microsoft C++ runtime it was built against (copied next to it).
        new("ghostscript", OperatingSystem.IsWindows() ? ["ghostscript", "vcruntime"] : ["ghostscript"], 31_000_000, ["ghostscript"]),
        new("ffmpeg", ["ffmpeg"], 272_000_000, ["ffmpeg"]),
        new("calibre", ["calibre"], 663_000_000, ["calibre"]),
        new("tesseract", ["tesseract"], 168_000_000, ["tesseract"]),
    ];

    /// <summary>Total download size of a package in bytes.</summary>
    public static long DownloadSize(EnginePackage package) => package.Components.Sum(c => Components.GetValueOrDefault(c)?.Size ?? 0);

    public static bool CanDownload(EnginePackage package) => package.Components.Count > 0 && package.Components.All(Components.ContainsKey);

    public static string? UnavailableReason(EnginePackage package)
    {
        if (CanDownload(package))
            return null;
        if (OperatingSystem.IsMacOS())
            return package.Id switch
            {
                "ffmpeg" => "Install FFmpeg with Homebrew (brew install ffmpeg), then check again or select its bin folder.",
                "ghostscript" => "Install Ghostscript with Homebrew (brew install ghostscript), then check again or select its bin folder.",
                "tesseract" => "Install Tesseract with Homebrew (brew install tesseract), then check again or select its bin folder.",
                "calibre" => "The calibre Filee downloads needs macOS 14 or later. Install calibre 7.26 from calibre-ebook.com and select its .app folder.",
                _ => $"No Filee download is available for {RuntimeIdentifier}. Install {package.Id} for this Mac and select its program or .app folder.",
            };
        if (OperatingSystem.IsLinux())
            return package.Id switch
            {
                "libreoffice" => "Install LibreOffice using your distribution's package manager (Ubuntu/Debian: sudo apt install libreoffice), then check again. HWP import through LibreOffice also needs Java and the H2Orestart extension.",
                "ghostscript" => "Install Ghostscript using your distribution's package manager (Ubuntu/Debian: sudo apt install ghostscript), then check again.",
                "tesseract" => "Install Tesseract using your distribution's package manager (Ubuntu/Debian: sudo apt install tesseract-ocr), then check again.",
                _ => $"No Filee download is available for {RuntimeIdentifier}. Install {package.Id} using your distribution's package manager and select its program folder.",
            };
        return $"No Filee download is available for {RuntimeIdentifier}. Select a compatible installed copy.";
    }

    /// <summary>Packages whose download is smaller than this (bytes) are suggested, see <see cref="IsSuggested"/>.</summary>
    public const long SuggestLimit = 60_000_000;

    /// <summary>
    /// Ticked by default in the installer and in the first-run choice. Large downloads are offered, not suggested:
    /// LibreOffice is only needed for older formats (DOC, XLS, PPT, OpenDocument), FFmpeg (~100 MB) only for video and
    /// audio, calibre (~230 MB) only for rare e-book formats and Kindle output. Ghostscript and Tesseract are small but
    /// only needed for EPS / PostScript and for scans.
    /// </summary>
    public static bool IsSuggested(EnginePackage package) =>
        CanDownload(package) && DownloadSize(package) < SuggestLimit && package.Id is not ("ghostscript" or "tesseract");

    /// <param name="macOSVersion">The running macOS; downloads whose minimumMacOS is newer are left out. Null keeps all.</param>
    public static IReadOnlyDictionary<string, EngineComponent> ComponentsForRuntime(string runtimeIdentifier, Version? macOSVersion = null)
    {
        using var stream = typeof(EngineDownloads).Assembly.GetManifestResourceStream("Filee.Engines.engines.json")
                           ?? throw new InvalidOperationException("Embedded engines.json is missing.");
        using var json = JsonDocument.Parse(stream);
        JsonElement components;
        if (runtimeIdentifier == "win-x64")
            components = json.RootElement.GetProperty("components");
        else if (json.RootElement.TryGetProperty("platforms", out var platforms)
                 && platforms.TryGetProperty(runtimeIdentifier, out var platform))
            components = platform.GetProperty("components");
        else
            return new Dictionary<string, EngineComponent>();
        return components.EnumerateObject().Where(p => RunsOn(p.Value, macOSVersion)).ToDictionary(
            p => p.Name,
            p => new EngineComponent(
                p.Name,
                p.Value.GetProperty("version").GetString()!,
                p.Value.GetProperty("url").GetString()!,
                p.Value.GetProperty("sha256").GetString()!,
                p.Value.GetProperty("size").GetInt64(),
                p.Value.GetProperty("kind").GetString()!));
    }

    private static bool RunsOn(JsonElement component, Version? macOSVersion) =>
        macOSVersion is null || !component.TryGetProperty("minimumMacOS", out var minimum)
        || macOSVersion >= Version.Parse(minimum.GetString()!);
}
