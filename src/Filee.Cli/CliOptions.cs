// Parses the "filee" command line. Kept dependency-free: a handful of commands and options.

using Filee.Core.Presets;

namespace Filee.Cli;

/// <summary>A parsed command line. <see cref="Error"/> is set when the arguments are wrong.</summary>
internal sealed record CliOptions
{
    public string Command { get; init; } = "help";
    public List<string> Inputs { get; } = [];
    public string? To { get; set; }
    public string? Preset { get; set; }
    public string? Output { get; set; }
    public bool Recursive { get; set; }
    public int? Quality { get; set; }
    public ConflictPolicy? Conflict { get; set; }
    public bool Json { get; set; }
    public bool Quiet { get; set; }
    public bool MoveOriginals { get; set; }
    public bool KeepDates { get; set; }
    public double? SettleSeconds { get; set; }
    public string? Error { get; set; }

    public static readonly string[] Commands = ["convert", "watch", "formats", "presets", "help", "version"];

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CliOptions { Command = "help" };
        var first = args[0].ToLowerInvariant();
        var command = first switch
        {
            "-h" or "--help" or "/?" => "help",
            "-v" or "--version" => "version",
            _ => first,
        };
        var options = new CliOptions { Command = command };
        if (!Commands.Contains(command))
        {
            options.Error = $"Unknown command '{args[0]}'.";
            return options;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Count)
                    return args[++i];
                options.Error ??= $"{arg} needs a value.";
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-t" or "--to":
                    options.To = Value();
                    break;
                case "-p" or "--preset":
                    options.Preset = Value();
                    break;
                case "-o" or "--output":
                    options.Output = Value();
                    break;
                case "-r" or "--recursive":
                    options.Recursive = true;
                    break;
                case "-q" or "--quality":
                    var quality = Value();
                    if (quality is not null)
                    {
                        if (int.TryParse(quality, out var q) && q is >= 1 and <= 100)
                            options.Quality = q;
                        else
                            options.Error ??= "--quality must be a number from 1 to 100.";
                    }
                    break;
                case "--overwrite":
                    options.Conflict = ConflictPolicy.Overwrite;
                    break;
                case "--skip":
                    options.Conflict = ConflictPolicy.Skip;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--move-originals":
                    options.MoveOriginals = true;
                    break;
                case "--keep-dates":
                    options.KeepDates = true;
                    break;
                case "--settle":
                    var settle = Value();
                    if (settle is not null)
                    {
                        if (double.TryParse(settle, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0)
                            options.SettleSeconds = s;
                        else
                            options.Error ??= "--settle must be a number of seconds.";
                    }
                    break;
                case "-h" or "--help":
                    options.Inputs.Insert(0, options.Command);
                    return options with { Command = "help" };
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.StartsWith('-') && arg.Length == 2))
                        options.Error ??= $"Unknown option '{arg}'.";
                    else
                        options.Inputs.Add(arg);
                    break;
            }
        }

        options.Error ??= options.Command switch
        {
            "convert" when options.Inputs.Count == 0 => "Give at least one file or folder to convert.",
            "convert" or "watch" when options.To is null == (options.Preset is null) => "Use either --to <format> or --preset <preset>.",
            "watch" when options.Inputs.Count != 1 => "Give exactly one folder to watch.",
            _ => null,
        };
        return options;
    }
}
