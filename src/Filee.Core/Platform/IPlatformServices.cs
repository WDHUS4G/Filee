namespace Filee.Core.Platform;

/// <summary>Operating-system specific services.</summary>
public interface IPlatformServices
{
    bool SupportsGlobalPointerGestures => true;
    bool SupportsSelectionShortcut => true;

    /// <summary>
    /// False where mouse presses arrive without a position (Wayland reports them at (0, 0)), so the gesture has to
    /// start from the last known cursor estimate instead.
    /// </summary>
    bool PressCarriesPosition => true;

    /// <summary>Whether this platform can show or hide Filee in the Dock.</summary>
    bool SupportsDockVisibility => false;

    /// <summary>Sets whether Filee appears in the Dock, returning whether the platform accepted the change.</summary>
    bool TrySetShowInDock(bool show) => false;

    /// <summary>True if the window at the screen point belongs to the file manager or desktop. Coordinates are pixels on Windows/Linux and desktop points on macOS.</summary>
    bool IsFileManagerAt(int x, int y);

    /// <summary>
    /// Moves a file to the Recycle Bin / Trash, where the user can restore it (<see cref="Presets.OutputRule.ReplaceOriginal"/>),
    /// and never deletes it for good. Null when it worked; otherwise why not, and the file is left where it is.
    /// </summary>
    TrashRefusal? MoveToTrash(string path) => new TrashRefusal("trash.no_recycle_bin");

    /// <summary>Process name (without extension) owning the window under the point, or <c>null</c>.</summary>
    string? ProcessNameAt(int x, int y);

    /// <summary>
    /// The window in front, the one keys go to: a keyboard shortcut's scope goes by it, not by what the pointer is over.
    /// Null where the platform can't tell, and the pointer decides.
    /// </summary>
    ForegroundWindowInfo? GetForegroundWindow() => null;

    /// <summary>
    /// Names of the apps that have a window open now, as <see cref="ProcessNameAt"/> reports them, sorted, without
    /// Filee. Windows reports a main window per process; macOS and Linux list the windows on screen.
    /// </summary>
    IReadOnlyList<string> RunningAppNames() => AppNames(System.Diagnostics.Process.GetProcesses()
        .Where(p => { try { return p.MainWindowHandle != 0; } catch (InvalidOperationException) { return false; } })
        .Select(p => { using (p) return p.ProcessName; }));

    /// <summary>Distinct, sorted, Filee itself left out.</summary>
    static IReadOnlyList<string> AppNames(IEnumerable<string?> names)
    {
        var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        return [.. names.OfType<string>().Where(n => n.Length > 0 && !n.Equals(own, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The existing file or folder the shortcut at <paramref name="path"/> points to (a .lnk on Windows); null for
    /// anything else, a broken shortcut, or a platform whose shortcuts Filee doesn't follow (macOS aliases and Linux
    /// .desktop links aren't followed yet).
    /// </summary>
    string? ShortcutTarget(string path) => null;

    /// <summary>
    /// <paramref name="paths"/> with every shortcut replaced by what it points to, for files that come into Filee
    /// (drops, the file manager's menu and selection). A broken shortcut stays, so it is reported as unsupported.
    /// </summary>
    IReadOnlyList<string> ResolveShortcuts(IReadOnlyList<string> paths) => ResolveShortcuts(paths, ShortcutTarget);

    /// <summary>
    /// Replaces each path by <paramref name="target"/>'s answer, if any, keeping the order. A shortcut and its target
    /// brought in together, or two shortcuts to one file, give that file once.
    /// </summary>
    static IReadOnlyList<string> ResolveShortcuts(IReadOnlyList<string> paths, Func<string, string?> target)
    {
        var resolved = paths.Select(p => target(p) ?? p).ToList();
        return resolved.SequenceEqual(paths) ? paths : [.. resolved.Distinct(FileSystemPaths.Comparer)];
    }

    /// <summary>Files currently selected in the foreground file manager window (empty if none).</summary>
    IReadOnlyList<string> GetFileManagerSelection();

    /// <summary>
    /// For the log when a shortcut found no selected files: the foreground window's class and process
    /// ("CabinetWClass / explorer"). Empty where the platform can't tell.
    /// </summary>
    string DescribeForegroundWindow() => "";

    /// <summary>
    /// The user's Downloads folder, where files dragged from a browser are saved unless Settings says otherwise.
    /// Windows asks the shell (the folder can be moved); elsewhere ~/Downloads.
    /// </summary>
    string DownloadsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>System drag threshold in the same units as screen coordinates.</summary>
    int SystemDragThreshold { get; }

    /// <summary>True if the user asked the OS to reduce animations.</summary>
    bool PrefersReducedMotion { get; }

    /// <summary>
    /// Adds or removes "start with the system". Throws <see cref="StartupEntryNotKeptException"/> where the platform can
    /// tell that the entry it added didn't stay.
    /// </summary>
    void SetStartWithSystem(bool enabled, string executablePath);

    /// <summary>Adds or removes the file manager context-menu entry ("Convert with Filee") and Send To shortcut.</summary>
    void SetContextMenu(bool enabled, string executablePath, string label);

    /// <summary>State of the Windows 11 top-level File Explorer menu entry for the app at <paramref name="executablePath"/>.</summary>
    ModernContextMenuState GetModernContextMenuState(string executablePath);

    /// <summary>
    /// Adds (the OS asks for administrator permission once) or removes the Windows 11 top-level menu entry.
    /// Runs in the background and never blocks the calling thread.
    /// </summary>
    Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath);

    /// <summary>Opens a folder in the file manager and selects the file, if given.</summary>
    void RevealInFileManager(string path);

    /// <summary>Opens a file with the app the system uses for its type (a folder opens in the file manager).</summary>
    void OpenFile(string path);

    /// <summary>Whether the system has a dialog that lets the user pick the app to open a file with.</summary>
    bool SupportsOpenWith => false;

    /// <summary>
    /// Shows the system's "Open with" dialog for <paramref name="path"/> (only where <see cref="SupportsOpenWith"/>).
    /// Returns at once: the dialog runs on its own.
    /// </summary>
    void OpenWith(string path) { }

    /// <summary>
    /// Puts an always-on-top window back at the top of the z-order without activating it. Windows can push such a
    /// window below normal ones (seen after the Photos app opened) while it still reports itself as topmost.
    /// </summary>
    void RaiseTopmost(nint windowHandle);
}

/// <summary>
/// The "start with the system" entry read back after writing it was gone or different: something (security software,
/// usually) took it away. The message is English, for the log; the UI says it in the user's language.
/// </summary>
public sealed class StartupEntryNotKeptException(string message) : IOException(message);

/// <summary>The window in front: its process name as <see cref="IPlatformServices.ProcessNameAt"/> reports it, and whether it belongs to the file manager or desktop.</summary>
public sealed record ForegroundWindowInfo(string? ProcessName, bool IsFileManager);

/// <summary>Whether "Convert with Filee" is in the top-level Windows 11 File Explorer menu (not just "Show more options").</summary>
public enum ModernContextMenuState
{
    /// <summary>Not possible here: another OS, Windows 10, ARM64, or a build without the Explorer extension files.</summary>
    Unsupported,

    /// <summary>Possible but not registered.</summary>
    Off,

    /// <summary>Registered for this installation.</summary>
    On,

    /// <summary>Registered, but for an older package version or another install folder: registering again fixes it.</summary>
    Outdated,
}

/// <summary>Outcome of <see cref="IPlatformServices.SetModernContextMenuAsync"/>.</summary>
/// <param name="Succeeded">True when the entry is now in the requested state.</param>
/// <param name="Cancelled">True when the user declined the administrator prompt.</param>
/// <param name="Error">Why it failed (English, from the OS), when it failed for another reason.</param>
public sealed record ModernContextMenuResult(bool Succeeded, bool Cancelled = false, string? Error = null);

/// <summary>Fallback used on platforms without an implementation yet. Everything is a no-op.</summary>
public sealed class NullPlatformServices : IPlatformServices
{
    public bool SupportsGlobalPointerGestures => false;
    public bool SupportsSelectionShortcut => false;
    public bool IsFileManagerAt(int x, int y) => false;
    public string? ProcessNameAt(int x, int y) => null;
    public IReadOnlyList<string> GetFileManagerSelection() => [];
    public int SystemDragThreshold => 4;
    public bool PrefersReducedMotion => false;
    public void SetStartWithSystem(bool enabled, string executablePath) { }
    public void SetContextMenu(bool enabled, string executablePath, string label) { }
    public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;

    public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
        Task.FromResult(new ModernContextMenuResult(false, Error: "Not supported on this platform."));

    public void RaiseTopmost(nint windowHandle) { }

    public void RevealInFileManager(string path)
    {
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (target is null)
            return;
        Open(target);
    }

    public void OpenFile(string path) => Open(path);

    private static void Open(string path)
    {
        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var start = new System.Diagnostics.ProcessStartInfo(opener) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
}

/// <summary>
/// Why a file wasn't moved to the Recycle Bin / Trash: a localization key (<c>trash.*</c>) and, when the system said
/// more, its own words.
/// </summary>
public sealed record TrashRefusal(string ReasonKey, string? Detail = null);
