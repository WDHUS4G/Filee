// Runs Filee's own copy of the 7-Zip console program (engines/7zip: 7z.exe + 7z.dll, GNU LGPL with the unRAR
// restriction, used as a separate process) to list, unpack and create archives, and turns its messages into clear
// errors. 7-Zip installed on the system is never used.

using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>One entry of a 7-Zip listing (<c>7z l -slt</c>).</summary>
/// <param name="Path">Path inside the archive as 7-Zip reports it (with backslashes on Windows).</param>
/// <param name="SymbolicLink">Target of a symbolic link entry (tar, cpio, zip from Unix), otherwise null.</param>
/// <param name="HardLink">Target of a hard link entry (tar), otherwise null.</param>
internal sealed record SevenZipEntry(string Path, bool IsFolder, long Size, bool IsEncrypted, string? SymbolicLink, string? HardLink)
{
    /// <summary>Unix mode or attribute string of the entry, used to spot links that carry no target field.</summary>
    public string? Mode { get; init; }

    /// <summary>True for symbolic links, which Filee never unpacks (they could point anywhere).</summary>
    public bool IsSymbolicLink => !string.IsNullOrEmpty(SymbolicLink)
                                  || Mode is { } mode && mode.Split(' ').Any(part => part.Length == 10 && part[0] == 'l');
}

/// <summary>The bundled 7-Zip console program.</summary>
internal sealed class SevenZip(string executable)
{
    /// <summary>Archives can be many gigabytes; the timeout only ends a process that hangs.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Password passed when reading, so 7-Zip never waits for one on the console. Unencrypted archives ignore it; for
    /// encrypted ones 7-Zip reports "Wrong password", which becomes the "password-protected" message.
    /// </summary>
    private const string NoPassword = "-pfilee-no-password";

    public string Executable { get; } = executable;

    /// <summary>Filee's own copy (engines/7zip), never one installed on the system.</summary>
    public static SevenZip? Locate() =>
        EngineEnvironment.FindBundled("7zip") is { } folder
        && EngineEnvironment.FirstExisting(Path.Combine(folder, OperatingSystem.IsWindows() ? "7z.exe" : "7zz")) is { } exe
            ? new SevenZip(exe)
            : null;

    /// <summary>Lists the entries of an archive.</summary>
    public async Task<IReadOnlyList<SevenZipEntry>> ListAsync(string archive, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["l", "-slt", "-sccUTF-8", "-bsp0", NoPassword, "--", Path.GetFullPath(archive)], cancellationToken);
        if (result.ExitCode is not (0 or 1))
            throw Failure(archive, result);
        return ParseListing(result.StandardOutput);
    }

    /// <summary>
    /// Unpacks every entry of <paramref name="archive"/> except <paramref name="exclude"/> into
    /// <paramref name="outputDirectory"/>. Clashing names ("A.txt" and "a.txt" on Windows) are renamed, not overwritten.
    /// </summary>
    public async Task ExtractAsync(string archive, string outputDirectory, IReadOnlyCollection<string> exclude,
        string workDirectory, CancellationToken cancellationToken)
    {
        List<string> args = ["x", "-y", "-aou", "-sccUTF-8", "-scsUTF-8", "-bsp0", "-bb0", NoPassword, "-o" + Path.GetFullPath(outputDirectory)];
        if (exclude.Count > 0)
        {
            // A list file keeps unusual names intact; -spd turns off wildcard matching so "*" in a name is literal.
            var list = Path.Combine(workDirectory, $"exclude-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(list, exclude, new UTF8Encoding(false), cancellationToken);
            args.AddRange(["-spd", "-x@" + list]);
        }
        args.AddRange(["--", Path.GetFullPath(archive)]);

        var result = await RunAsync(args, cancellationToken);
        if (result.ExitCode is 0 or 1 || result.ExitCode == 2 && OnlyLinkErrors(result))
            return;
        throw Failure(archive, result);
    }

    /// <summary>
    /// Creates <paramref name="archive"/> (which must not exist) from files and folders; each is stored under its own
    /// name, folders with their content.
    /// </summary>
    /// <param name="type">7-Zip archive type: 7z, zip, tar, gzip, bzip2 or xz.</param>
    public async Task AddAsync(string archive, string type, ArchiveLevel level, IReadOnlyList<string> items,
        string workDirectory, CancellationToken cancellationToken)
    {
        var list = Path.Combine(workDirectory, $"items-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, items.Select(Path.GetFullPath), new UTF8Encoding(false), cancellationToken);
        // -ssw packs files other programs have open; -sse stops instead of silently leaving out unreadable files.
        // No "--" here: it would also turn off the @list file.
        var result = await RunAsync(
            ["a", "-t" + type, LevelSwitch(level), "-ssw", "-sse", "-y", "-sccUTF-8", "-scsUTF-8", "-bsp0", "-bb0",
             Path.GetFullPath(archive), "@" + list],
            cancellationToken);
        if (result.ExitCode is not (0 or 1) || !File.Exists(archive))
            throw new InvalidOperationException($"7-Zip could not create the archive (exit {result.ExitCode}). {ErrorText(result)}".Trim());
    }

    /// <summary>7-Zip's -mx switch for a preset level.</summary>
    internal static string LevelSwitch(ArchiveLevel level) => level switch
    {
        ArchiveLevel.Store => "-mx0",
        ArchiveLevel.Fast => "-mx1",
        ArchiveLevel.Maximum => "-mx9",
        _ => "-mx5",
    };

    /// <summary>Parses the technical listing: blocks of "Key = Value" lines, one per entry, after a "----------" line.</summary>
    internal static List<SevenZipEntry> ParseListing(string output)
    {
        var entries = new List<SevenZipEntry>();
        Dictionary<string, string>? fields = null;
        var inEntries = false;

        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (!inEntries)
            {
                inEntries = line.StartsWith("----------", StringComparison.Ordinal);
                continue;
            }
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator > 0)
                (fields ??= new(StringComparer.Ordinal))[line[..separator]] = line[(separator + 3)..];
            else if (line.EndsWith(" =", StringComparison.Ordinal))
                (fields ??= new(StringComparer.Ordinal))[line[..^2]] = "";
        }
        Flush();
        return entries;

        void Flush()
        {
            if (fields is null || !fields.TryGetValue("Path", out var path))
            {
                fields = null;
                return;
            }
            string? Field(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            var attributes = Field("Attributes") ?? "";
            entries.Add(new SevenZipEntry(
                path,
                Field("Folder") == "+" || attributes.StartsWith('D'),
                long.TryParse(Field("Size"), out var size) ? size : 0,
                Field("Encrypted") == "+",
                Field("Symbolic Link"),
                Field("Hard Link"))
            {
                Mode = Field("Mode") ?? Field("Attributes"),
            });
            fields = null;
        }
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(Executable, arguments, Timeout, cancellationToken);

    /// <summary>True when every error 7-Zip reported is about a link it did not create (we never want links).</summary>
    private static bool OnlyLinkErrors(ProcessResult result)
    {
        var errors = ErrorLines(result).ToList();
        return errors.Count > 0 && errors.All(e =>
            e.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Dangerous link", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turns a failed 7-Zip run on an archive into an exception with a message users understand.</summary>
    private static Exception Failure(string archive, ProcessResult result)
    {
        var name = Path.GetFileName(archive);
        var text = result.StandardError + "\n" + result.StandardOutput;
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Wrong password") || Has("encrypted archive"))
            return ArchiveSafety.Encrypted(name);
        if (Has("Cannot open the file as") || Has("Is not archive") || Has("Can not open the file as"))
            return new InvalidDataException($"{name} is not a valid archive, or not the kind its extension says.");
        if (Has("Unexpected end") || Has("Data Error") || Has("CRC Failed") || Has("Headers Error") || Has("Missing volume"))
            return ArchiveSafety.Damaged(name, ErrorText(result));
        return new InvalidOperationException($"7-Zip could not read {name} (exit {result.ExitCode}). {ErrorText(result)}".Trim());
    }

    /// <summary>
    /// The lines of 7-Zip's output that describe errors ("ERROR: ...", and the lines under an "ERRORS:" heading),
    /// without its banner and statistics.
    /// </summary>
    private static IEnumerable<string> ErrorLines(ProcessResult result)
    {
        var underHeading = false;
        foreach (var line in (result.StandardError + "\n" + result.StandardOutput).Split('\n').Select(l => l.Trim()))
        {
            if (line is "ERRORS:" or "WARNINGS:")
            {
                underHeading = true;
                continue;
            }
            if (line.Length == 0)
            {
                underHeading = false;
                continue;
            }
            if (underHeading
                || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Open ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                yield return line;
        }
    }

    private static string ErrorText(ProcessResult result)
    {
        var lines = ErrorLines(result).Distinct().Take(3).ToList();
        return string.Join(" ", lines.Count > 0 ? lines : result.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
    }
}
