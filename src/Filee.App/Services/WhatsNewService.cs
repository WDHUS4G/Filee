// "What's new" slides after an update: decides once per start whether this version's slides are due, shows them when
// the user sees Filee, and remembers the version so they show once.
//
// How an update is told from a fresh install: the first-run flag. A start before FirstRunCompleted is a new install
// (the first-run setup introduces Filee; the slides would only repeat it), so the current version counts as seen.
// After that, WhatsNewSeenVersion says which version's slides were seen; null means the settings come from a Filee
// older than the slides, i.e. it was updated. A version at or below the one seen (same version, downgrade) shows nothing
// and never lowers what was seen. Started by the installer after an update (--whats-new), it is an update whatever the
// settings say: they may have been deleted, or come from a Filee older than the first-run flag.

using Avalonia.Threading;
using Filee.App.Controls;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class WhatsNewService(UserDataStore store, WindowService windows, ILogger<WhatsNewService> log)
{
    private IReadOnlyList<SlideDeck>? _decks;

    /// <summary>The assembly whose resources hold the decks.</summary>
    internal const string Assembly = "Filee";

    /// <summary>Every deck Filee ships, oldest first.</summary>
    public IReadOnlyList<SlideDeck> Decks => _decks ??= SlideDeck.LoadAll(Assembly);

    /// <summary>The version running now (three parts).</summary>
    internal Version CurrentVersion { get; set; } = SlideDeck.TryParseVersion(UpdateService.CurrentVersion) ?? new Version(0, 0, 0);

    /// <summary>The deck waiting for the user to open Filee (it started in the background).</summary>
    public SlideDeck? Pending { get; private set; }

    /// <summary>The newest deck up to this version: what About → "What's new" shows. Null when there is none.</summary>
    public SlideDeck? Latest => SlideDeck.Newest(Decks, seen: null, CurrentVersion);

    /// <summary>What a start should do about the slides.</summary>
    /// <param name="Show">The deck to show, if any.</param>
    /// <param name="Remember">A version to store as seen right away (a fresh install, or an update without slides).</param>
    internal readonly record struct Decision(SlideDeck? Show, Version? Remember);

    /// <summary>The rules described at the top of this file, without side effects.</summary>
    /// <param name="updatedFrom">
    /// The version the installer updated from: what was running before, so it decides what is new over a remembered
    /// version (going back to an older Filee and updating again shows the slides again).
    /// </param>
    internal static Decision Decide(string? seenSetting, bool firstRunCompleted, Version current, IEnumerable<SlideDeck> decks,
        bool updated = false, string? updatedFrom = null)
    {
        if (!firstRunCompleted && !updated)
            return new Decision(null, current);
        var seen = SlideDeck.TryParseVersion(updatedFrom) ?? SlideDeck.TryParseVersion(seenSetting);
        if (seen is not null && current <= seen)
            return new Decision(null, null);
        var deck = SlideDeck.Newest(decks, seen, current);
        return deck is null ? new Decision(null, current) : new Decision(deck, null);
    }

    /// <summary>
    /// Called once at start-up, before the first run is marked complete. <paramref name="background"/> (started at
    /// sign-in, or after an update without <c>--whats-new</c>) keeps the slides until the main window opens.
    /// <paramref name="updated"/>: the installer started Filee after an update (<c>--whats-new</c>), from
    /// <paramref name="updatedFrom"/> when it says.
    /// </summary>
    public void CheckAtStartup(bool background, bool updated = false, string? updatedFrom = null)
    {
        Decision decision;
        try
        {
            decision = Decide(store.Settings.WhatsNewSeenVersion, store.Settings.FirstRunCompleted, CurrentVersion, Decks, updated, updatedFrom);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Could not read the What's new slides");
            return;
        }
        if (decision.Remember is { } remember)
            Remember(remember);
        if (decision.Show is not { } deck)
            return;
        log.LogInformation("What's new in {Version} is due ({Seen} seen before)", deck.Version.ToString(3),
            store.Settings.WhatsNewSeenVersion ?? "nothing");
        if (background)
        {
            Pending = deck;
            windows.MainShown += OnMainShown;
        }
        else
            // After the main window: the slides go on top of it.
            DispatcherTimer.RunOnce(() => Show(deck), TimeSpan.FromMilliseconds(600));
    }

    private void OnMainShown(object? sender, EventArgs e)
    {
        windows.MainShown -= OnMainShown;
        if (Pending is { } deck)
        {
            Pending = null;
            Dispatcher.UIThread.Post(() => Show(deck), DispatcherPriority.Background);
        }
    }

    /// <summary>Shows <paramref name="deck"/> (the latest one when null) and remembers it as seen.</summary>
    public void Show(SlideDeck? deck = null)
    {
        deck ??= Latest;
        if (deck is null)
            return;
        Pending = null;
        windows.ShowWhatsNew(deck);
        Remember(CurrentVersion > deck.Version ? CurrentVersion : deck.Version);
    }

    /// <summary>Stores <paramref name="version"/> as seen, unless a newer one already is.</summary>
    private void Remember(Version version)
    {
        var seen = SlideDeck.TryParseVersion(store.Settings.WhatsNewSeenVersion);
        if (seen is not null && seen >= version)
            return;
        store.Settings.WhatsNewSeenVersion = version.ToString(3);
        store.SaveSettings();
    }
}
