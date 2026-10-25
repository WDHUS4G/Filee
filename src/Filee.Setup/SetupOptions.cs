// Command line of Setup and the uninstaller. Setup takes the switches of the Inno Setup installer it replaces, because
// Filee 1.x updates itself by running the new Setup with "/SILENT /NORESTART" (UpdateService.InstallerArguments) and
// package managers (winget) pass Inno's silent switches:
//
//   /SILENT                 no wizard: only the progress window
//   /VERYSILENT             no window at all
//   /SUPPRESSMSGBOXES       no message boxes (errors are logged; the default answer is taken)
//   /NORESTART /SP- /NOCANCEL /RESTARTEXITCODE= /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS (and their /NO… forms)
//                           accepted; Setup never restarts Windows and always closes a running Filee
//   /NOCLOSEAPPLICATIONS    don't force-close programs that still run from the install folder after "Filee --quit"
//   /DIR="folder"           install folder (environment variables are expanded)
//   /LANG=ko                language of Setup (and of a newly installed Filee): en, es, ja, ko, zh-CN (Inno: zhcn),
//                           zh-TW, pl, hi, ru, ar, pt-PT, pt-BR, fr, ne, th, tr
//   /TASKS="a,b"            exactly these options; /MERGETASKS="a,!b" changes the defaults. "!" unticks, "*" ticks
//                           the children too. Names: contextmenu, contextmenu\top, startup, cli, desktopicon
//   /NOICONS                no Start menu and desktop shortcuts
//   /ALLUSERS               install for all users into Program Files (the default; needs administrator rights)
//   /CURRENTUSER            install for this user only into %LocalAppData%\Programs (no administrator rights; the
//                           Windows 11 top-level menu needs them and is left out)
//   /PORTABLE=1             only unpack Filee into /DIR (no uninstaller, registry, shortcuts or PATH)
//   /LOG, /LOG="file"       Setup always logs (%TEMP%\Filee-Setup-*.log); /LOG= names the file
//   /COMPONENTS= /TYPE= /GROUP= /PASSWORD= /LOADINF= /SAVEINF=   accepted and ignored (Filee has none of these)
//
// Filee's own:
//   /NOLAUNCH               don't start Filee at the end
//   /SANDBOX="folder"       test run: everything (registry, Start menu, desktop, PATH, %APPDATA%) goes into this folder,
//                           nothing on the PC is touched and no administrator rights are asked for
//   /UNINSTALL              uninstall (uninstall.exe, which has no payload, does this without the switch)
//   /LAUNCHFILE="file"      internal: the unelevated Setup that asked for administrator rights starts Filee as the
//                           signed-in user from what the elevated one writes into this file
//   /FIRSTPHASE=<id>        internal: the uninstaller's copy in %TEMP% lets the uninstaller in the install folder
//                           (process <id>) end before it deletes its file

namespace Filee.Setup;

/// <summary>How much of Setup the user sees.</summary>
public enum SetupUi
{
    /// <summary>The wizard.</summary>
    Wizard,

    /// <summary>/SILENT: only the progress window.</summary>
    Silent,

    /// <summary>/VERYSILENT: nothing.</summary>
    VerySilent,
}

/// <summary>Where Filee goes and who it is installed for.</summary>
public enum InstallScope
{
    /// <summary>All users, Program Files, HKLM (administrator rights).</summary>
    Machine,

    /// <summary>This user, %LocalAppData%\Programs, HKCU.</summary>
    User,
}

/// <summary>A parsed Setup command line.</summary>
public sealed record SetupOptions
{
    public SetupUi Ui { get; init; }
    public bool SuppressMessageBoxes { get; init; }
    public string? Directory { get; init; }
    public string? Language { get; init; }
    public string? Tasks { get; init; }
    public string? MergeTasks { get; init; }
    public bool NoIcons { get; init; }
    public InstallScope Scope { get; init; } = InstallScope.Machine;
    public bool Portable { get; init; }
    public string? LogFile { get; init; }
    public bool ForceCloseApplications { get; init; } = true;
    public bool NoLaunch { get; init; }
    public bool Uninstall { get; init; }
    public string? Sandbox { get; init; }
    public string? LaunchFile { get; init; }
    public int? FirstPhase { get; init; }

    /// <summary>Inno Setup switches that are accepted but change nothing.</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    /// <summary>Arguments Setup doesn't know (logged, otherwise ignored like Inno Setup does).</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>No window: errors can't be shown in the wizard.</summary>
    public bool IsSilent => Ui != SetupUi.Wizard;

    /// <summary>Message boxes may be shown (not with /SUPPRESSMSGBOXES; /VERYSILENT shows only errors).</summary>
    public bool CanShowMessages => !SuppressMessageBoxes;

    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SetupOptions();
        var ignored = new List<string>();
        var unknown = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Length < 2 || arg[0] != '/')
            {
                unknown.Add(arg);
                continue;
            }
            var equals = arg.IndexOf('=');
            var name = (equals < 0 ? arg[1..] : arg[1..equals]).ToUpperInvariant();
            var value = equals < 0 ? null : Unquote(arg[(equals + 1)..]);
            switch (name)
            {
                case "SILENT":
                    if (options.Ui == SetupUi.Wizard)
                        options = options with { Ui = SetupUi.Silent };
                    break;
                case "VERYSILENT":
                    options = options with { Ui = SetupUi.VerySilent };
                    break;
                case "SUPPRESSMSGBOXES":
                    options = options with { SuppressMessageBoxes = true };
                    break;
                case "DIR" when !string.IsNullOrWhiteSpace(value):
                    options = options with { Directory = value };
                    break;
                case "LANG" when !string.IsNullOrWhiteSpace(value):
                    options = options with { Language = value };
                    break;
                case "TASKS" when value is not null:
                    options = options with { Tasks = value };
                    break;
                case "MERGETASKS" when value is not null:
                    options = options with { MergeTasks = value };
                    break;
                case "NOICONS":
                    options = options with { NoIcons = true };
                    break;
                case "ALLUSERS":
                    options = options with { Scope = InstallScope.Machine };
                    break;
                case "CURRENTUSER":
                    options = options with { Scope = InstallScope.User };
                    break;
                case "PORTABLE":
                    options = options with { Portable = value is null || value == "1" };
                    break;
                case "LOG":
                    options = options with { LogFile = string.IsNullOrWhiteSpace(value) ? options.LogFile : value };
                    break;
                case "NOCLOSEAPPLICATIONS":
                    options = options with { ForceCloseApplications = false };
                    break;
                case "CLOSEAPPLICATIONS" or "FORCECLOSEAPPLICATIONS":
                    options = options with { ForceCloseApplications = true };
                    break;
                case "NOLAUNCH":
                    options = options with { NoLaunch = true };
                    break;
                case "UNINSTALL":
                    options = options with { Uninstall = true };
                    break;
                case "SANDBOX" when !string.IsNullOrWhiteSpace(value):
                    options = options with { Sandbox = value };
                    break;
                case "FIRSTPHASE" when int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id):
                    options = options with { FirstPhase = id };
                    break;
                case "LAUNCHFILE" when !string.IsNullOrWhiteSpace(value):
                    options = options with { LaunchFile = value };
                    break;
                case "SP-" or "NORESTART" or "NOCANCEL" or "RESTARTEXITCODE" or "NOFORCECLOSEAPPLICATIONS"
                    or "LOGCLOSEAPPLICATIONS" or "RESTARTAPPLICATIONS" or "NORESTARTAPPLICATIONS" or "COMPONENTS"
                    or "TYPE" or "GROUP" or "NOGROUP" or "PASSWORD" or "LOADINF" or "SAVEINF" or "HELP" or "?":
                    ignored.Add(arg);
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }
        return options with { Ignored = ignored, Unknown = unknown };
    }

    /// <summary>Inno Setup takes /DIR="C:\a b" and /DIR=C:\a b alike; quotes left over from a hand-made command line go.</summary>
    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    /// <summary>
    /// One command line from <paramref name="args"/> with the quoting Windows programs (CommandLineToArgvW and the C
    /// runtime) undo: for starting Setup again with administrator rights.
    /// </summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>Quotes one argument for <see cref="Join"/>.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return arg;
        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes before a quote are doubled, and the quote is escaped.
            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }
        // Backslashes before the closing quote are doubled.
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
