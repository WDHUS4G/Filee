// What an install or uninstall works with, and what it reports back.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

/// <param name="Sandbox">A /SANDBOX run: Filee is never started.</param>
public sealed record SetupContext(ISetupRegistry Registry, ISetupSystem System, SetupPaths Paths, SetupLog Log, bool Sandbox = false);

/// <summary>What the user chose (or the command line said).</summary>
/// <param name="Folder">The install folder.</param>
/// <param name="Engines">Engine packages Filee downloads when it starts (a fresh install's choice).</param>
/// <param name="Language">Setup's language.</param>
/// <param name="LanguagePicked">The user picked <paramref name="Language"/> (or /LANG= did): a fresh Filee starts in
/// it too. Otherwise Filee keeps following Windows' language.</param>
/// <param name="NoIcons">/NOICONS: no Start menu or desktop shortcut.</param>
/// <param name="Portable">/PORTABLE=1: only the files.</param>
/// <param name="ForceClose">Programs still running from the install folder after "Filee --quit" are ended.</param>
public sealed record InstallChoices(
    string Folder,
    InstallScope Scope,
    SetupTasks Tasks,
    IReadOnlyList<string> Engines,
    string Language,
    bool LanguagePicked = false,
    bool NoIcons = false,
    bool Portable = false,
    bool ForceClose = true);

/// <summary>What Setup is doing (the progress page shows it in the UI language).</summary>
public enum SetupStep
{
    Verifying,
    Closing,
    RemovingOld,
    Copying,
    ExplorerMenu,
    Finishing,
    RollingBack,
    Removing,
}

public sealed record SetupProgress(SetupStep Step, double Fraction);

/// <summary>Something that didn't work but leaves a working Filee.</summary>
public abstract record SetupWarning;

/// <summary>Windows didn't register the top-level Explorer menu (Filee is still under "Show more options").</summary>
public sealed record ExplorerMenuWarning(string Error) : SetupWarning;

/// <summary>Programs that need a Microsoft Visual C++ runtime this PC doesn't have.</summary>
public sealed record RuntimeWarning(IReadOnlyList<MissingRuntime> Missing) : SetupWarning;

/// <summary>Start Filee as the signed-in user with these arguments.</summary>
public sealed record LaunchRequest(string Executable, string Arguments);

/// <summary>A problem with a message for the user (the key of a translated text and its arguments).</summary>
public sealed class SetupException(string textKey, string message, params string[] arguments) : Exception(message)
{
    public string TextKey { get; } = textKey;
    public IReadOnlyList<string> Arguments { get; } = arguments;
}

/// <param name="Error">Why it failed; null when it worked.</param>
/// <param name="RolledBack">The install folder is as it was before (the previous Filee still works).</param>
public sealed record InstallResult(LaunchRequest? Launch, IReadOnlyList<SetupWarning> Warnings, Exception? Error = null, bool RolledBack = false)
{
    public bool Succeeded => Error is null;
}
