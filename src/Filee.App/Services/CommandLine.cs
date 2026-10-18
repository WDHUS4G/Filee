// Command line options:
//   Filee.exe                          open the main window
//   Filee.exe --background             start in the tray only (used by "start with Windows")
//   Filee.exe --settings               open the main window
//   Filee.exe --convert a b ...        open the donut for these files (Explorer context menu / Send To)
//   Filee.exe --convert-list <file>    same, with one path per line in a UTF-8 file (Windows 11 Explorer menu, for
//                                      selections too long for a command line); the app deletes the file afterwards
// Used by the installer (installer/Filee.iss):
//   Filee.exe --install-engines=a,b    download these engine packages (EngineDownloads ids) in the engine window;
//                                      an empty list means the user chose none, so the first-run choice is skipped
//   Filee.exe --start-with-windows=on|off, --context-menu=on|off
//                                      set the matching options on the General page
//   Filee.exe --quit                   ask a running Filee to exit, and wait until it has (no window otherwise)
//   Filee.exe --uninstall-cleanup      quit Filee, then remove what it registered for this user (UninstallCleanup)

using System.Text;

namespace Filee.App.Services;

/// <summary>Parsed command line.</summary>
/// <param name="ListFile">The <c>--convert-list</c> file, if any (see <see cref="DeleteListFile"/>).</param>
/// <param name="InstallEngines">Engine packages chosen in the installer; null when the option is absent.</param>
/// <param name="StartWithWindows">Value of <c>--start-with-windows</c>; null when absent.</param>
/// <param name="ContextMenu">Value of <c>--context-menu</c>; null when absent.</param>
public sealed record CommandLine(
    bool Background,
    bool ShowSettings,
    IReadOnlyList<string> ConvertFiles,
    string? ListFile = null,
    bool Quit = false,
    bool UninstallCleanup = false,
    IReadOnlyList<string>? InstallEngines = null,
    bool? StartWithWindows = null,
    bool? ContextMenu = null)
{
    /// <summary>File name prefix of list files written by FileeExplorerMenu.dll (LIST_PREFIX there) into %TEMP%.</summary>
    public const string ListFilePrefix = "Filee-convert-";

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var background = false;
        var settings = false;
        var files = new List<string>();
        string? listFile = null;
        var collecting = false;
        var expectList = false;
        var quit = false;
        var cleanup = false;
        List<string>? engines = null;
        bool? startWithWindows = null;
        bool? contextMenu = null;

        foreach (var arg in args)
        {
            if (expectList)
            {
                expectList = false;
                listFile = arg;
                files.AddRange(ReadListFile(arg).Where(Exists).Select(Path.GetFullPath));
                continue;
            }
            var (name, value) = SplitOption(arg);
            switch (name)
            {
                case "--install-engines" when value is not null:
                    engines = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => id.ToLowerInvariant()).Distinct().ToList();
                    break;
                case "--start-with-windows" or "--start-with-system" when OnOff(value) is { } on:
                    startWithWindows = on;
                    break;
                case "--context-menu" when OnOff(value) is { } on:
                    contextMenu = on;
                    break;
                case "--quit":
                    quit = true;
                    break;
                case "--uninstall-cleanup":
                    cleanup = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case "--settings":
                    settings = true;
                    break;
                case "--convert":
                    collecting = true;
                    break;
                case "--convert-list":
                    expectList = true;
                    break;
                default:
                    // Ignore anything unknown that isn't a file (e.g. options of a newer version).
                    if (collecting && !arg.StartsWith("--", StringComparison.Ordinal) && Exists(arg))
                        files.Add(Path.GetFullPath(arg));
                    break;
            }
        }
        return new CommandLine(background, settings, files, listFile, quit, cleanup, engines, startWithWindows, contextMenu);
    }

    /// <summary>"--Name=Value" → ("--name", "Value"); "--name" → ("--name", null).</summary>
    private static (string Name, string? Value) SplitOption(string arg)
    {
        var equals = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=') : -1;
        return equals < 0 ? (arg.ToLowerInvariant(), null) : (arg[..equals].ToLowerInvariant(), arg[(equals + 1)..]);
    }

    private static bool? OnOff(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" => true,
        "off" or "0" or "false" or "no" => false,
        _ => null,
    };

    /// <summary>
    /// Deletes the list file once its paths were taken over. Only files the Explorer menu wrote (prefix, temp
    /// folder) are deleted, so a crafted command line cannot make Filee delete anything else.
    /// </summary>
    public void DeleteListFile()
    {
        if (ListFile is null || !IsOwnListFile(ListFile))
            return;
        try
        {
            File.Delete(ListFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>True for <c>%TEMP%\Filee-convert-*.txt</c>.</summary>
    internal static bool IsOwnListFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return Path.GetFileName(full).StartsWith(ListFilePrefix, StringComparison.OrdinalIgnoreCase)
                   && full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? ""),
                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Filee.Core.Platform.FileSystemPaths.Comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>One path per line (UTF-8, with or without BOM); blank lines are skipped. Unreadable → empty.</summary>
    internal static IEnumerable<string> ReadListFile(string path)
    {
        try
        {
            return File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
