// macOS: a click-mode donut opened for another process (a later launch handing over files, the Services menu) can
// lose focus right after it opens, before the user did anything: Filee becomes the active app while the donut shows,
// and AppKit may make another of its windows key on the way (issue #160: `open -n -a Filee.app --args --convert`
// logged the files but no donut stayed up). The click-mode donut closes when it loses focus, so it closed at once.
// The first focus loss within a moment of opening is forgiven instead.

namespace Filee.App.Services;

internal sealed class ActivationGrace(TimeSpan length, Func<DateTime> now)
{
    /// <summary>Long enough for an activation to settle; short enough that a quick click elsewhere still closes.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(1);

    private DateTime? _openedAt;

    public ActivationGrace() : this(Default, () => DateTime.UtcNow) { }

    /// <summary>The donut just opened for another process: forgive the next focus loss within the grace.</summary>
    public void Start() => _openedAt = now();

    /// <summary>The donut opened for something else, or closed: focus losses close it again.</summary>
    public void Cancel() => _openedAt = null;

    /// <summary>
    /// The donut lost focus: true when that is forgiven (once, and only within the grace after <see cref="Start"/>).
    /// </summary>
    public bool Forgives()
    {
        if (_openedAt is not { } openedAt)
            return false;
        _openedAt = null;
        return now() - openedAt <= length;
    }
}
