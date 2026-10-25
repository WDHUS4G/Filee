// The registry as Setup uses it: the uninstall entry, PATH and the old Velopack entry. Behind an interface so tests and
// /SANDBOX runs use a registry of their own (MemoryRegistry) and never touch HKLM, HKCU or the user's PATH.

using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Filee.Setup.Platform;

/// <summary>The two hives Setup writes to.</summary>
public enum Hive
{
    LocalMachine,
    CurrentUser,
}

/// <summary>A registry value: a string (REG_SZ or REG_EXPAND_SZ) or a number (REG_DWORD).</summary>
public sealed record RegistryEntry(object Value, bool Expandable = false)
{
    public string? Text => Value as string;
}

public interface ISetupRegistry
{
    bool KeyExists(Hive hive, string key);

    /// <summary>The value, unexpanded (REG_EXPAND_SZ stays as written); null when missing.</summary>
    RegistryEntry? Get(Hive hive, string key, string name);

    IReadOnlyList<string> ValueNames(Hive hive, string key);

    void SetString(Hive hive, string key, string name, string value, bool expandable = false);

    void SetNumber(Hive hive, string key, string name, int value);

    void DeleteValue(Hive hive, string key, string name);

    /// <summary>Deletes the key with everything below it (nothing when it doesn't exist).</summary>
    void DeleteKey(Hive hive, string key);
}

/// <summary>The real registry, 64-bit view (Filee is a 64-bit program; Inno Setup wrote its entry there too).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : ISetupRegistry
{
    private static RegistryKey Base(Hive hive) =>
        RegistryKey.OpenBaseKey(hive == Hive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public bool KeyExists(Hive hive, string key)
    {
        using var root = Base(hive);
        using var opened = root.OpenSubKey(key);
        return opened is not null;
    }

    public RegistryEntry? Get(Hive hive, string key, string name)
    {
        using var root = Base(hive);
        using var opened = root.OpenSubKey(key);
        var value = opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            string text => new RegistryEntry(text, opened!.GetValueKind(name) == RegistryValueKind.ExpandString),
            int number => new RegistryEntry(number),
            _ => null,
        };
    }

    public IReadOnlyList<string> ValueNames(Hive hive, string key)
    {
        using var root = Base(hive);
        using var opened = root.OpenSubKey(key);
        return opened?.GetValueNames() ?? [];
    }

    public void SetString(Hive hive, string key, string name, string value, bool expandable = false)
    {
        using var root = Base(hive);
        using var opened = root.CreateSubKey(key, writable: true);
        opened.SetValue(name, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public void SetNumber(Hive hive, string key, string name, int value)
    {
        using var root = Base(hive);
        using var opened = root.CreateSubKey(key, writable: true);
        opened.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(Hive hive, string key, string name)
    {
        using var root = Base(hive);
        using var opened = root.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKey(Hive hive, string key)
    {
        using var root = Base(hive);
        root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }
}

/// <summary>
/// A registry in memory, optionally saved to a JSON file (the /SANDBOX registry). Key names compare case-insensitively
/// like the real one.
/// </summary>
public sealed class MemoryRegistry : ISetupRegistry
{
    private readonly Dictionary<string, Dictionary<string, RegistryEntry>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _file;

    public MemoryRegistry(string? file = null)
    {
        _file = file;
        if (file is not null && File.Exists(file))
            Load(file);
    }

    private static string Path(Hive hive, string key) => (hive == Hive.LocalMachine ? "HKLM\\" : "HKCU\\") + key.Trim('\\');

    public bool KeyExists(Hive hive, string key)
    {
        var path = Path(hive, key);
        return _keys.Keys.Any(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                                   || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public RegistryEntry? Get(Hive hive, string key, string name) =>
        _keys.TryGetValue(Path(hive, key), out var values) && values.TryGetValue(name, out var entry) ? entry : null;

    public IReadOnlyList<string> ValueNames(Hive hive, string key) =>
        _keys.TryGetValue(Path(hive, key), out var values) ? values.Keys.ToList() : [];

    public void SetString(Hive hive, string key, string name, string value, bool expandable = false) =>
        Set(hive, key, name, new RegistryEntry(value, expandable));

    public void SetNumber(Hive hive, string key, string name, int value) => Set(hive, key, name, new RegistryEntry(value));

    public void DeleteValue(Hive hive, string key, string name)
    {
        if (_keys.TryGetValue(Path(hive, key), out var values) && values.Remove(name))
            Save();
    }

    public void DeleteKey(Hive hive, string key)
    {
        var path = Path(hive, key);
        foreach (var k in _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                                                 || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(k);
        Save();
    }

    private void Set(Hive hive, string key, string name, RegistryEntry entry)
    {
        var path = Path(hive, key);
        if (!_keys.TryGetValue(path, out var values))
            _keys[path] = values = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        values[name] = entry;
        Save();
    }

    private void Save()
    {
        if (_file is null)
            return;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, values) in _keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject(key);
                foreach (var (name, entry) in values)
                {
                    writer.WriteStartObject(name);
                    if (entry.Value is int number)
                        writer.WriteNumber("dword", number);
                    else
                        writer.WriteString(entry.Expandable ? "expand_sz" : "sz", (string)entry.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        File.WriteAllBytes(_file, stream.ToArray());
    }

    private void Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
        foreach (var key in json.RootElement.EnumerateObject())
        {
            var values = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in key.Value.EnumerateObject())
            {
                foreach (var typed in value.Value.EnumerateObject())
                {
                    values[value.Name] = typed.Name switch
                    {
                        "dword" => new RegistryEntry(typed.Value.GetInt32()),
                        "expand_sz" => new RegistryEntry(typed.Value.GetString() ?? "", Expandable: true),
                        _ => new RegistryEntry(typed.Value.GetString() ?? ""),
                    };
                }
            }
            _keys[key.Name] = values;
        }
    }
}
