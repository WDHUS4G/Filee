// The "filee" command on PATH: the install folder's cli subfolder in the system PATH (per-machine) or the user's PATH
// (per-user), written as REG_EXPAND_SZ like Windows' own value so %SystemRoot% entries keep working.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public static class PathVariable
{
    public static (Hive Hive, string Key) Location(InstallScope scope) => scope == InstallScope.Machine
        ? (Hive.LocalMachine, SetupNames.MachineEnvironmentKey)
        : (Hive.CurrentUser, SetupNames.UserEnvironmentKey);

    /// <summary>True when <paramref name="paths"/> (a PATH value) has <paramref name="folder"/> (case and a trailing backslash don't matter).</summary>
    public static bool Contains(string? paths, string folder) =>
        Entries(paths).Any(entry => Same(entry, folder));

    public static string Add(string? paths, string folder)
    {
        if (Contains(paths, folder))
            return paths!;
        var value = paths ?? "";
        if (value.Length > 0 && !value.EndsWith(';'))
            value += ";";
        return value + Path.TrimEndingDirectorySeparator(folder);
    }

    /// <summary>Removes every entry for <paramref name="folder"/>; the rest stays exactly as it was.</summary>
    public static string Remove(string? paths, string folder) =>
        string.Join(';', (paths ?? "").Split(';').Where(entry => entry.Trim().Length == 0 || !Same(entry, folder)));

    public static bool IsOnPath(ISetupRegistry registry, InstallScope scope, string folder)
    {
        var (hive, key) = Location(scope);
        return Contains(registry.Get(hive, key, "Path")?.Text, folder);
    }

    /// <summary>Adds or removes the folder; true when PATH changed (running programs should then be told).</summary>
    public static bool Set(ISetupRegistry registry, InstallScope scope, string folder, bool present)
    {
        var (hive, key) = Location(scope);
        var entry = registry.Get(hive, key, "Path");
        var current = entry?.Text;
        var updated = present ? Add(current, folder) : Remove(current, folder);
        if (string.Equals(current ?? "", updated, StringComparison.Ordinal))
            return false;
        // The system PATH is REG_EXPAND_SZ; a user PATH keeps the kind it had (new ones get REG_EXPAND_SZ too).
        registry.SetString(hive, key, "Path", updated, expandable: entry?.Expandable ?? true);
        return true;
    }

    private static IEnumerable<string> Entries(string? paths) =>
        (paths ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Same(string entry, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(entry.Trim()), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
}
