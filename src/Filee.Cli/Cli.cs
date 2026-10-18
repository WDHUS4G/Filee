// The "filee" commands: convert, watch, formats, presets. Writes results for people (one line per file) or, with
// --json, one JSON document for other programs. Exit codes: 0 everything converted, 1 some files failed,
// 2 wrong arguments or a missing input, 3 nothing to convert.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Watching;

namespace Filee.Cli;

internal static class Cli
{
    public const int Success = 0;
    public const int SomeFailed = 1;
    public const int UsageError = 2;
    public const int NothingToDo = 3;

    /// <param name="createHost">Engines and settings (tests pass a host with a temporary settings folder).</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken, Func<CliHost>? createHost = null)
    {
        var options = CliOptions.Parse(args);
        if (options.Error is not null)
        {
            errors.WriteLine($"filee: {options.Error}");
            errors.WriteLine("Run 'filee help' for usage.");
            return UsageError;
        }
        switch (options.Command)
        {
            case "help":
                output.Write(Help.Text(options.Inputs.FirstOrDefault()));
                return Success;
            case "version":
                output.WriteLine($"filee {Help.Version}");
                return Success;
        }

        await using var host = (createHost ?? (() => CliHost.Create()))();
        return options.Command switch
        {
            "convert" => await ConvertAsync(options, host, output, errors, cancellationToken),
            "watch" => await WatchAsync(options, host, output, errors, cancellationToken),
            "formats" => ListFormats(options, host, output, errors),
            _ => ListPresets(host, output),
        };
    }

    private static async Task<int> ConvertAsync(CliOptions options, CliHost host, TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (ResolvePreset(options, host, errors) is not { } preset)
            return UsageError;
        if (options.Output is not null)
            UseFolder(preset, options.Output);

        var missing = new List<string>();
        var files = ExpandInputs(options.Inputs, options.Recursive, missing);
        foreach (var path in missing)
            errors.WriteLine($"filee: not found: {path}");
        if (missing.Count > 0)
            return UsageError;
        if (files.Count == 0)
        {
            errors.WriteLine("filee: no files to convert.");
            return NothingToDo;
        }

        var job = await RunJobAsync(files, preset, host, options.Json || options.Quiet ? null : output, cancellationToken);
        var done = job.Files.Count(f => f.State == FileState.Done);
        var failed = job.Files.Count(f => f.State is FileState.Failed or FileState.Cancelled);
        if (options.Json)
            WriteJson(job, host.Texts, output);
        else if (!options.Quiet)
            output.WriteLine(failed == 0
                ? $"Converted {done} of {job.Files.Count} file(s)."
                : $"Converted {done} of {job.Files.Count} file(s), {failed} failed.");
        return failed == 0 ? Success : SomeFailed;
    }

    private static async Task<int> WatchAsync(CliOptions options, CliHost host, TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (ResolvePreset(options, host, errors) is not { } preset)
            return UsageError;
        var rule = new WatchRule
        {
            Folder = Path.GetFullPath(options.Inputs[0]),
            IncludeSubfolders = options.Recursive,
            OutputFolder = options.Output is null ? "" : Path.GetFullPath(options.Output),
            Originals = options.MoveOriginals ? AfterConversion.MoveToOriginals : AfterConversion.Keep,
        };
        UseFolder(preset, rule.ResolvedOutputFolder);
        preset.Output.KeepFoldersFrom = rule.Folder; // as the app's watch folders: subfolders stay subfolders

        var settle = options.SettleSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        await using var watcher = new FolderWatcher(rule,
            async (files, token) => await RunJobAsync(files, preset, host, options.Quiet ? null : output, token),
            settleTime: settle);
        if (!watcher.Start())
        {
            errors.WriteLine($"filee: {watcher.Error}");
            return UsageError;
        }
        if (!options.Quiet)
            output.WriteLine($"Watching {rule.Folder} → {host.Texts.DisplayName(preset)}, saving to {rule.ResolvedOutputFolder}. Press Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        return Success;
    }

    /// <summary>Runs one job and prints each file's result as soon as it is final (when <paramref name="output"/> is given).</summary>
    private static async Task<ConversionJob> RunJobAsync(IReadOnlyList<string> files, Preset preset, CliHost host, TextWriter? output,
        CancellationToken cancellationToken)
    {
        var job = new ConversionJob(files, preset, host.Texts.DisplayName(preset));
        var printed = new HashSet<FileResult>();
        void Print(object? sender, ConversionJob updated)
        {
            if (output is null || updated != job)
                return;
            lock (printed)
            {
                foreach (var file in job.Files.Where(f => f.State is not (FileState.Pending or FileState.Running) && printed.Add(f)))
                    output.WriteLine(Describe(file, host.Texts));
            }
        }

        host.Queue.JobUpdated += Print;
        try
        {
            using var _ = cancellationToken.Register(job.Cancel);
            await host.Queue.RunAsync(job);
        }
        finally
        {
            host.Queue.JobUpdated -= Print;
        }
        Print(null, job);
        return job;
    }

    private static string Describe(FileResult file, ILocalizer texts)
    {
        var source = Shorten(file.SourcePath);
        return file.State switch
        {
            FileState.Done => $"ok    {source} -> {string.Join(", ", file.Outputs.Select(Shorten))}",
            FileState.Skipped => $"skip  {source} (the output file already exists)",
            FileState.Cancelled => $"stop  {source} (cancelled)",
            _ => $"fail  {source}: {Error(file, texts)}",
        };
    }

    private static string Error(FileResult file, ILocalizer texts)
    {
        var message = file.ErrorKey is null ? "conversion failed" : texts[file.ErrorKey];
        return string.IsNullOrWhiteSpace(file.ErrorDetail) ? message : $"{message} ({file.ErrorDetail})";
    }

    /// <summary>Relative to the current folder when the file is inside it, else the full path.</summary>
    private static string Shorten(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    private static void WriteJson(ConversionJob job, ILocalizer texts, TextWriter output)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("preset", job.PresetDisplayName);
            json.WriteString("target", job.Preset.TargetFormat);
            json.WriteNumber("converted", job.Files.Count(f => f.State == FileState.Done));
            json.WriteNumber("skipped", job.Files.Count(f => f.State == FileState.Skipped));
            json.WriteNumber("failed", job.Files.Count(f => f.State is FileState.Failed or FileState.Cancelled));
            json.WriteStartArray("files");
            foreach (var file in job.Files)
            {
                json.WriteStartObject();
                json.WriteString("source", file.SourcePath);
                json.WriteString("state", file.State.ToString().ToLowerInvariant());
                json.WriteStartArray("outputs");
                foreach (var path in file.Outputs)
                    json.WriteStringValue(path);
                json.WriteEndArray();
                if (file.State is FileState.Failed or FileState.Cancelled)
                    json.WriteString("error", Error(file, texts));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>The preset from --to or --preset, with --quality and --overwrite/--skip applied (a copy).</summary>
    private static Preset? ResolvePreset(CliOptions options, CliHost host, TextWriter errors)
    {
        Preset preset;
        if (options.To is not null)
        {
            if (FindFormat(options.To) is not { } format)
            {
                errors.WriteLine($"filee: unknown format '{options.To}'. Run 'filee formats' for the list.");
                return null;
            }
            if (!format.Writable)
            {
                errors.WriteLine($"filee: Filee reads {format.DisplayName} but can't write it.");
                return null;
            }
            preset = new Preset { TargetFormat = format.Id };
        }
        else
        {
            var wanted = options.Preset!;
            Preset? Find(IEnumerable<Preset> presets) =>
                presets.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase))
                ?? presets.FirstOrDefault(p => string.Equals(host.Texts.DisplayName(p), wanted, StringComparison.OrdinalIgnoreCase));
            // Conversions first: a group of the same name (say a "Rotate" group next to your own "Rotate" preset)
            // must not hide one. A group is only found to say what it holds.
            var found = Find(host.Store.Presets.Where(p => !p.IsGroup)) ?? Find(host.Store.Presets);
            if (found is null)
            {
                errors.WriteLine($"filee: no preset '{wanted}'. Run 'filee presets' for the list.");
                return null;
            }
            if (found.IsGroup)
            {
                // A donut group converts nothing; name what it holds so the right one is easy to pick.
                var members = found.SubPresetIds.Select(host.Store.FindConversion).OfType<Preset>().Select(p => p.Id).ToList();
                errors.WriteLine(members.Count > 0
                    ? $"filee: '{wanted}' is a group on the donut, not a conversion. Use one of: {string.Join(", ", members)}"
                    : $"filee: '{wanted}' is a group on the donut, not a conversion, and has no presets left. Run 'filee presets' for the list.");
                return null;
            }
            preset = found.Clone();
        }
        preset = host.Store.Settings.WithDefaultOutput(preset);
        if (options.Quality is { } quality)
            preset.Image.Quality = quality;
        if (options.Conflict is { } conflict)
            preset.Output.Conflict = conflict;
        if (options.KeepDates)
            preset.Output.KeepDates = true;
        return preset;
    }

    private static void UseFolder(Preset preset, string folder)
    {
        preset.Output.Location = OutputLocation.CustomFolder;
        preset.Output.CustomFolder = Path.GetFullPath(folder);
    }

    /// <summary>A format by id ("jpg") or by extension ("jpeg", ".jpg").</summary>
    internal static FileFormat? FindFormat(string text)
    {
        var key = text.Trim().TrimStart('.').ToLowerInvariant();
        return FormatRegistry.FindById(key) ?? FormatRegistry.FindByExtension(key);
    }

    /// <summary>
    /// Files from the arguments: files as given, wildcards ("*.heic", which cmd.exe and PowerShell pass on unexpanded)
    /// and folders (their files in formats Filee knows; with --recursive also in sub folders).
    /// </summary>
    internal static List<string> ExpandInputs(IEnumerable<string> inputs, bool recursive, List<string> missing)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (input.IndexOfAny(['*', '?']) >= 0)
            {
                var folder = Path.GetDirectoryName(input) is { Length: > 0 } dir ? dir : ".";
                var matches = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, Path.GetFileName(input), option).ToList()
                    : [];
                if (matches.Count == 0)
                    missing.Add(input);
                files.AddRange(matches);
            }
            else if (Directory.Exists(input))
                files.AddRange(Directory.EnumerateFiles(input, "*", option).Where(f => FormatRegistry.Detect(f) is not null));
            else if (File.Exists(input))
                files.Add(input);
            else
                missing.Add(input);
        }
        return files.Select(Path.GetFullPath).Distinct(FileSystemPaths.Comparer).ToList();
    }

    private static int ListFormats(CliOptions options, CliHost host, TextWriter output, TextWriter errors)
    {
        if (options.Inputs.FirstOrDefault() is { } name)
        {
            if (FindFormat(name) is not { } from)
            {
                errors.WriteLine($"filee: unknown format '{name}'.");
                return UsageError;
            }
            var installed = host.Catalog.CreatePlanner();
            var everything = host.Catalog.CreatePlanner(host.Catalog.All.Select(c => c.Id).ToList());
            var targets = FormatRegistry.Known.Where(t => t.Writable && t.Id != from.Id && t.Id != FormatRegistry.Folder).ToList();
            var now = targets.Where(t => installed.CanConvert(from.Id, t.Id)).Select(t => t.Id).ToList();
            var later = targets.Where(t => !now.Contains(t.Id) && everything.CanConvert(from.Id, t.Id)).Select(t => t.Id).ToList();
            output.WriteLine($"{from.DisplayName} ({string.Join(", ", from.Extensions)}) converts to:");
            output.WriteLine(now.Count > 0 ? "  " + string.Join(", ", now) : "  (nothing with the engines installed now)");
            if (later.Count > 0)
                output.WriteLine($"With optional engines (install them in Filee, Settings → Engines):{Environment.NewLine}  {string.Join(", ", later)}");
            return Success;
        }

        foreach (var group in FormatRegistry.Known.Where(f => f.Id != FormatRegistry.Folder).GroupBy(f => f.Category))
        {
            output.WriteLine($"{group.Key}:");
            foreach (var format in group)
                output.WriteLine($"  {format.Id,-10} {format.DisplayName}{(format.Writable ? "" : " (read only)")}  .{string.Join(" .", format.Extensions)}");
        }
        output.WriteLine();
        output.WriteLine("Run 'filee formats <format>' to see what a format converts to.");
        return Success;
    }

    private static int ListPresets(CliHost host, TextWriter output)
    {
        output.WriteLine($"{"ID",-24} {"NAME",-28} TARGET");
        foreach (var preset in host.Store.Presets.Where(p => !p.IsGroup))
            output.WriteLine($"{preset.Id,-24} {host.Texts.DisplayName(preset),-28} {preset.TargetFormat}");
        output.WriteLine();
        output.WriteLine("Use one with: filee convert <files> --preset <id or name>");
        return Success;
    }
}
