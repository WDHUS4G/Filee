// Versions of engine copies Filee didn't download (the user's own, or one found on the PC), compared with the version
// Filee is tested with (engines.json). An older copy still works for most conversions; the Engines page only says
// that an update is recommended.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Filee.Engines.Infrastructure;

public static partial class CopyVersions
{
    private static readonly ConcurrentDictionary<string, Version?> Cache = new();

    /// <summary>The version Filee is tested with ("9.0"), from engines.json; null for unknown packages.</summary>
    // Where Filee offers no download (FFmpeg and Ghostscript on macOS come from Homebrew), the version it is tested
    // with is the one the Windows list names, so an older copy still gets "update recommended".
    public static Version? Expected(string id) =>
        Parse(EngineVersions.Component(id) ?? ReferenceComponents.Value.GetValueOrDefault(id)?.Version);

    private static readonly Lazy<IReadOnlyDictionary<string, EngineComponent>> ReferenceComponents =
        new(() => EngineDownloads.ComponentsForRuntime("win-x64"));

    /// <summary>
    /// The version of the copy in <paramref name="folder"/> (major.minor), or null when it can't be told (e.g. an
    /// FFmpeg git build named by date). Cached per program and file time.
    /// </summary>
    public static Version? Installed(string id, string folder)
    {
        var program = id switch
        {
            "libreoffice" when OperatingSystem.IsWindows() => Path.Combine(folder, "version.ini"),
            _ when EngineEnvironment.OwnCopyPrograms.TryGetValue(id, out var programs) => Path.Combine(folder, programs[0]),
            _ => null,
        };
        if (program is null || !File.Exists(program))
            return null;
        return Cache.GetOrAdd($"{program}|{File.GetLastWriteTimeUtc(program).Ticks}", _ => ParseVersion(id, Read(id, program)));
    }

    /// <summary>True when the copy is older (major.minor) than the version Filee is tested with.</summary>
    public static bool IsOutdated(string id, string folder) =>
        Installed(id, folder) is { } installed && Expected(id) is { } expected && installed < expected;

    /// <summary>The version in a program's version output (or LibreOffice's version.ini).</summary>
    internal static Version? ParseVersion(string id, string text)
    {
        var match = id switch
        {
            "ffmpeg" => FfmpegVersion().Match(text),
            "pandoc" => PandocVersion().Match(text),
            "calibre" => CalibreVersion().Match(text),
            "libreoffice" => LibreOfficeVersion().Match(text),
            _ => LeadingVersion().Match(text),
        };
        return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }

    private static Version? Parse(string? text)
    {
        var match = LeadingVersion().Match(text ?? "");
        return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }

    private static string Read(string id, string program)
    {
        if (id == "libreoffice" && OperatingSystem.IsWindows())
            return File.ReadAllText(program);
        var arguments = id == "ffmpeg" ? "-hide_banner -version" : "--version";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return "";
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return output.Result + "\n" + errors.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"ffmpeg version n?(\d+)\.(\d+)")]
    private static partial Regex FfmpegVersion();

    [GeneratedRegex(@"^pandoc(?:\.exe)? (\d+)\.(\d+)", RegexOptions.Multiline)]
    private static partial Regex PandocVersion();

    [GeneratedRegex(@"calibre (\d+)\.(\d+)")]
    private static partial Regex CalibreVersion();

    [GeneratedRegex(@"(?:MsiProductVersion=|LibreOffice\s+)(\d+)\.(\d+)")]
    private static partial Regex LibreOfficeVersion();

    [GeneratedRegex(@"^\s*(\d+)\.(\d+)", RegexOptions.Multiline)]
    private static partial Regex LeadingVersion();
}
