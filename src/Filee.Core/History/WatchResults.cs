// What a watch folder converted lately, from the history: how many files went through and which failed (a file that
// failed and then converted on a retry or a later copy counts as converted).

using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Core.History;

/// <summary>A file of a watch folder whose last conversion failed, with the preset that job ran with.</summary>
public sealed record WatchFailure(HistoryFailure Failure, Preset? Preset);

/// <summary>Recent results of one watch folder.</summary>
public sealed record WatchResults(int Converted, IReadOnlyList<WatchFailure> Failures)
{
    /// <summary>Results of the watch folder <paramref name="ruleId"/> in <paramref name="history"/> (newest first).</summary>
    public static WatchResults Of(IEnumerable<HistoryEntry> history, string ruleId)
    {
        var seen = new HashSet<string>(FileSystemPaths.Comparer);
        var converted = 0;
        var failures = new List<WatchFailure>();
        foreach (var entry in history.Where(e => e.WatchRuleId == ruleId))
        {
            foreach (var source in entry.Sources)
            {
                if (!seen.Add(source))
                    continue; // a newer job already decided this file
                var failure = entry.Failures.FirstOrDefault(f => FileSystemPaths.Comparer.Equals(f.Source, source));
                if (failure is null)
                    converted++;
                else
                    failures.Add(new WatchFailure(failure, entry.Preset));
            }
        }
        return new WatchResults(converted, failures);
    }

    public bool IsEmpty => Converted == 0 && Failures.Count == 0;
}
