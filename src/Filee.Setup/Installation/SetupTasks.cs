// The options ("tasks") of Setup, with the names Inno Setup used for them (installer/Filee.iss, 1.x): /TASKS= and
// /MERGETASKS= take these names, and an update from 1.x reads them from the registry ("Inno Setup: Selected Tasks").

namespace Filee.Setup.Installation;

/// <summary>What Setup sets up besides copying the files.</summary>
/// <param name="ContextMenu">"Convert with Filee" in the Explorer menu (a Filee setting, applied by Filee).</param>
/// <param name="ModernMenu">Also at the top of the Windows 11 menu: the sparse package, registered by Setup.</param>
/// <param name="Startup">Start Filee at sign-in (a Filee setting).</param>
/// <param name="Cli">The <c>filee</c> command on PATH.</param>
/// <param name="DesktopIcon">A desktop shortcut.</param>
public sealed record SetupTasks(bool ContextMenu, bool ModernMenu, bool Startup, bool Cli, bool DesktopIcon)
{
    public const string ContextMenuName = "contextmenu";
    public const string ModernMenuName = @"contextmenu\top";
    public const string StartupName = "startup";
    public const string CliName = "cli";
    public const string DesktopIconName = "desktopicon";

    /// <summary>Every task, parents before their children.</summary>
    public static readonly IReadOnlyList<string> Names = [ContextMenuName, ModernMenuName, StartupName, CliName, DesktopIconName];

    /// <summary>What a fresh install ticks: everything but the desktop shortcut.</summary>
    public static SetupTasks Defaults { get; } = new(true, true, true, true, false);

    /// <summary>
    /// /TASKS (only the listed tasks) and /MERGETASKS (changes to the defaults), as Inno Setup reads them:
    /// "!name" unticks, "*name" ticks the task and its children. A ticked child ticks its parent.
    /// </summary>
    public static SetupTasks FromCommandLine(string? tasks, string? mergeTasks)
    {
        var selected = tasks is null ? Defaults.ToSet() : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Apply(selected, tasks);
        Apply(selected, mergeTasks);
        return FromSet(selected);
    }

    /// <summary>The tasks in Inno Setup's registry format ("contextmenu,contextmenu\top,cli").</summary>
    public string ToInnoString() => string.Join(',', Names.Where(ToSet().Contains));

    /// <summary>Reads Inno Setup's "Inno Setup: Selected Tasks" (or our own copy of it).</summary>
    public static SetupTasks FromInnoString(string? value)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Apply(selected, value);
        return FromSet(selected);
    }

    /// <summary>The task names Setup knows in <paramref name="list"/> that it doesn't (logged).</summary>
    public static IEnumerable<string> UnknownNames(string? list) =>
        Split(list).Select(n => n.TrimStart('!', '*')).Where(n => !Names.Contains(n, StringComparer.OrdinalIgnoreCase));

    private static void Apply(HashSet<string> selected, string? list)
    {
        foreach (var entry in Split(list))
        {
            var name = entry.TrimStart('!', '*');
            if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            var children = Names.Where(n => n.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entry.StartsWith('!'))
            {
                selected.Remove(name);
                // An unticked parent unticks its children (they can't be on without it).
                children.ForEach(c => selected.Remove(c));
                continue;
            }
            selected.Add(name);
            if (entry.StartsWith('*'))
                children.ForEach(c => selected.Add(c));
            for (var slash = name.LastIndexOf('\\'); slash > 0; slash = name.LastIndexOf('\\', slash - 1))
                selected.Add(name[..slash]);
        }
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private HashSet<string> ToSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ContextMenu)
            set.Add(ContextMenuName);
        if (ContextMenu && ModernMenu)
            set.Add(ModernMenuName);
        if (Startup)
            set.Add(StartupName);
        if (Cli)
            set.Add(CliName);
        if (DesktopIcon)
            set.Add(DesktopIconName);
        return set;
    }

    private static SetupTasks FromSet(HashSet<string> set) => new(
        set.Contains(ContextMenuName),
        set.Contains(ContextMenuName) && set.Contains(ModernMenuName),
        set.Contains(StartupName),
        set.Contains(CliName),
        set.Contains(DesktopIconName));
}
