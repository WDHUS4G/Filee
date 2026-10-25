// Whether Filee runs with more rights than the desktop shell. Windows keeps a lower process (Explorer) from sending
// input to a higher one (User Interface Privilege Isolation): drops from Explorer are refused, the second Filee its
// menu starts can't open the running Filee's mutex or pipe, and the running Filee can't read Explorer's selection.
// The decision is plain logic over integrity levels, so it is tested everywhere; reading the levels is Windows code.

namespace Filee.Core.Platform;

/// <summary>Windows mandatory integrity levels, lowest first.</summary>
public enum IntegrityLevel
{
    /// <summary>Not known: another operating system, or the token could not be read.</summary>
    Unknown,
    Untrusted,
    Low,
    Medium,
    MediumPlus,
    High,
    System,
}

/// <summary>Integrity levels of this process and of the desktop shell (Explorer).</summary>
/// <param name="Process">This process.</param>
/// <param name="Shell">The process that owns the desktop window (Explorer), or Unknown when there is none.</param>
/// <param name="Elevated">True when this process runs with a full administrator token (UAC "Run as administrator").</param>
public sealed record ElevationState(IntegrityLevel Process, IntegrityLevel Shell, bool Elevated)
{
    /// <summary>Nothing known (any OS but Windows).</summary>
    public static ElevationState Unknown { get; } = new(IntegrityLevel.Unknown, IntegrityLevel.Unknown, false);

    /// <summary>
    /// True when Filee runs as administrator while Explorer doesn't: Explorer can't drop files on Filee or hand files
    /// over through its menu. Unknown levels never count, and neither does a PC where Explorer runs elevated too
    /// (UAC switched off).
    /// </summary>
    public bool ShellCannotReachFilee =>
        Process >= IntegrityLevel.High && Shell != IntegrityLevel.Unknown && Shell < Process;

    /// <summary>
    /// Maps the last sub-authority of a mandatory label SID (S-1-16-<i>rid</i>) to a level. Values between the
    /// well-known ones belong to the level below them, as Windows compares them numerically.
    /// </summary>
    public static IntegrityLevel FromRid(uint rid) => rid switch
    {
        >= 0x4000 => IntegrityLevel.System,
        >= 0x3000 => IntegrityLevel.High,
        >= 0x2100 => IntegrityLevel.MediumPlus,
        >= 0x2000 => IntegrityLevel.Medium,
        >= 0x1000 => IntegrityLevel.Low,
        _ => IntegrityLevel.Untrusted,
    };

    /// <summary>For the log: "integrity High (elevated), Explorer Medium".</summary>
    public override string ToString() =>
        Process == IntegrityLevel.Unknown && Shell == IntegrityLevel.Unknown && !Elevated
            ? "integrity unknown"
            : $"integrity {Process}{(Elevated ? " (elevated)" : "")}, Explorer {Shell}";
}
