// Runs the watch folders of the settings (Settings → Watch folders) while Filee is in the tray: one FolderWatcher
// per enabled rule, rebuilt shortly after the rules change. Conversions go through ConversionService, so they show in
// the toast and the history like any other.

using Avalonia.Threading;
using Filee.Core.Settings;
using Filee.Core.Watching;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class WatchFolderService(UserDataStore store, ConversionService conversions, ILogger<WatchFolderService> log)
    : IAsyncDisposable
{
    /// <summary>Typing a folder path saves on every key: wait for a pause before (re)starting watchers.</summary>
    private static readonly TimeSpan ApplyDelay = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<string, (string Signature, FolderWatcher Watcher)> _running = [];
    private readonly Dictionary<string, string?> _errors = [];
    private IDisposable? _pendingApply;

    /// <summary>Raised (UI thread) when a rule started, stopped or failed to start.</summary>
    public event EventHandler? StatusChanged;

    public void Start()
    {
        conversions.WatchRetry = RetryAsync;
        _ = ApplyAsync();
        store.SettingsChanged += (_, _) =>
        {
            _pendingApply?.Dispose();
            _pendingApply = DispatcherTimer.RunOnce(() => _ = ApplyAsync(), ApplyDelay);
        };
    }

    /// <summary>True when the rule's folder is being watched.</summary>
    public bool IsWatching(string ruleId) => _running.ContainsKey(ruleId);

    /// <summary>Why an enabled rule is not being watched (folder missing, …), or null.</summary>
    public string? ErrorOf(string ruleId) => _errors.GetValueOrDefault(ruleId);

    /// <summary>Starts, restarts and stops watchers so they match the settings.</summary>
    private async Task ApplyAsync()
    {
        var wanted = store.Settings.WatchFolders
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Folder) && !string.IsNullOrWhiteSpace(r.PresetId))
            .ToDictionary(r => r.Id, r => r.Clone());

        foreach (var (id, (signature, watcher)) in _running.ToList())
        {
            if (wanted.TryGetValue(id, out var rule) && Signature(rule) == signature)
                continue;
            _running.Remove(id);
            await watcher.DisposeAsync();
        }
        _errors.Clear();
        foreach (var (id, rule) in wanted)
        {
            if (_running.ContainsKey(id))
                continue;
            var watcher = new FolderWatcher(rule, (files, _) => ConvertAsync(rule, files), log);
            if (watcher.Start())
            {
                _running[id] = (Signature(rule), watcher);
                log.LogInformation("Watching {Folder}", rule.Folder);
            }
            else
            {
                _errors[id] = watcher.Error;
                await watcher.DisposeAsync();
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<Core.Conversion.ConversionJob?> ConvertAsync(WatchRule rule, IReadOnlyList<string> files)
    {
        var preset = store.FindConversion(rule.PresetId);
        if (preset is null)
        {
            log.LogWarning("Watch folder {Folder}: preset {Preset} no longer exists", rule.Folder, rule.PresetId);
            return null;
        }
        return await conversions.RunAsync(files, rule.Apply(preset), rule.Id);
    }

    /// <summary>
    /// Converts files of a watch folder again (a retry from the toast or the history) with the preset as it ran, then
    /// moves the originals that converted when the folder does that.
    /// </summary>
    internal async Task RetryAsync(string ruleId, IReadOnlyList<string> files, Core.Presets.Preset preset)
    {
        var job = await conversions.RunAsync(files, preset, ruleId);
        if (job is null || store.Settings.WatchFolders.FirstOrDefault(r => r.Id == ruleId) is not { Originals: AfterConversion.MoveToOriginals } rule)
            return;
        foreach (var file in job.Files.Where(f => f.State == Core.Conversion.FileState.Done))
            FolderWatcher.MoveToOriginals(rule, file.SourcePath, log);
    }

    /// <summary>Everything that needs a new watcher when it changes.</summary>
    private static string Signature(WatchRule rule) =>
        string.Join('|', rule.Folder, rule.PresetId, rule.IncludeSubfolders, rule.OutputFolder, rule.Originals,
            string.Join('/', rule.Include), rule.FileNamePattern,
            string.Join('/', rule.Renames.Select(r => $"{r.Kind}:{r.Find}>{r.Replace}")));

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, watcher) in _running.Values)
            await watcher.DisposeAsync();
        _running.Clear();
    }
}
