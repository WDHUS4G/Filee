// Windows implementation of IPlatformServices.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Filee.Core.Platform;
using Microsoft.Win32;

namespace Filee.Platform.Windows;

/// <summary>Explorer integration, auto-start and system settings on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : IPlatformServices
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ContextMenuKey = @"Software\Classes\*\shell\Filee";
    private const string AppName = "Filee";

    /// <summary>Window classes that belong to Explorer windows and the desktop.</summary>
    private static readonly HashSet<string> FileManagerClasses = new(StringComparer.Ordinal)
    {
        "CabinetWClass", // File Explorer window
        "ExploreWClass", // legacy Explorer
        "Progman",       // desktop
        "WorkerW",       // desktop (when wallpaper slideshow / Win+Tab created a WorkerW)
    };

    /// <summary>
    /// File managers people use instead of Explorer, by process name (issue #124): their windows count as "over the
    /// file manager" for gestures too. Dragging files out of them reaches the donut like a drag from Explorer.
    /// </summary>
    private static readonly HashSet<string> FileManagerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "Files",          // Files (files.community)
        "dopus",          // Directory Opus
        "TOTALCMD64",     // Total Commander
        "TOTALCMD",
        "XYplorer",
        "OneCommander",
        "Q-Dir",
        "FreeCommander",
    };

    public void RaiseTopmost(nint windowHandle)
    {
        if (windowHandle != 0)
            NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    public TrashRefusal? MoveToTrash(string path) => RecycleBin.Move(path);

    public bool IsFileManagerAt(int x, int y)
    {
        var root = RootWindowAt(x, y);
        if (root == 0)
            return false;
        return IsFileManager(NativeMethods.ClassNameOf(root), ProcessNameOf(root));
    }

    /// <summary>True for a top-level window of Explorer, the desktop or another file manager.</summary>
    public static bool IsFileManager(string? windowClass, string? processName) =>
        windowClass is not null && FileManagerClasses.Contains(windowClass)
        || processName is not null && FileManagerProcesses.Contains(processName);

    public string? ProcessNameAt(int x, int y)
    {
        var root = RootWindowAt(x, y);
        return root == 0 ? null : ProcessNameOf(root);
    }

    public ForegroundWindowInfo? GetForegroundWindow()
    {
        var root = ForegroundRoot();
        if (root == 0)
            return null;
        var process = ProcessNameOf(root);
        return new ForegroundWindowInfo(process, IsFileManager(NativeMethods.ClassNameOf(root), process));
    }

    /// <summary>True for the desktop's window classes: its icons are not among Shell.Application's windows.</summary>
    internal static bool IsDesktop(string? windowClass) => windowClass is "Progman" or "WorkerW";

    public string? ShortcutTarget(string path) => ShellLinks.TargetOf(path);

    public string DownloadsFolder => KnownFolders.Downloads
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Reads the selection of the foreground Explorer window, or of the desktop, through the Shell automation objects.
    /// Must be called on an STA thread (the Avalonia UI thread is STA).
    /// </summary>
    public IReadOnlyList<string> GetFileManagerSelection()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return [];
        return IsDesktop(NativeMethods.ClassNameOf(ForegroundRoot())) ? DesktopSelection() : SelectionOf(foreground);
    }

    /// <summary>The files selected on the desktop (#450).</summary>
    internal static IReadOnlyList<string> DesktopSelection()
    {
        try
        {
            return DesktopShellView.Use(view =>
            {
                dynamic folderView = view;
                dynamic items = folderView.SelectedItems();
                try
                {
                    return ExistingFiles((object)items);
                }
                finally
                {
                    Marshal.FinalReleaseComObject((object)items);
                }
            });
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // No Explorer desktop (another shell, Explorer restarting): nothing selected to read.
            return [];
        }
    }

    /// <summary>The paths of a shell item collection that are files on disk.</summary>
    private static List<string> ExistingFiles(object items)
    {
        var paths = new List<string>();
        foreach (dynamic item in (dynamic)items)
        {
            string path = item.Path;
            if (File.Exists(path))
                paths.Add(path);
        }
        return paths;
    }

    /// <summary>The selected files of an Explorer window; with tabs, of its active tab (see <see cref="ExplorerTabs"/>).</summary>
    internal static IReadOnlyList<string> SelectionOf(nint foreground)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null)
            return [];

        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;

            // With Explorer tabs (Windows 11) every tab is an entry with the window's HWND: find the active one.
            var activeTab = ExplorerTabs.ActiveTabOf(foreground);
            var found = new List<object?>(count);
            var entries = new List<ShellWindowEntry>(count);
            for (var i = 0; i < count; i++)
            {
                object? window = null;
                nint topLevel = 0, tab = 0;
                try
                {
                    window = windows.Item(i);
                    if (window is not null)
                    {
                        topLevel = (nint)(long)((dynamic)window).HWND;
                        if (topLevel == foreground && activeTab != 0)
                            tab = ExplorerTabs.TabOf(window);
                    }
                }
                catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { } // e.g. Internet Explorer windows
                found.Add(window);
                entries.Add(new ShellWindowEntry(topLevel, tab));
            }

            foreach (var i in ExplorerTabs.Candidates(entries, foreground, activeTab))
            {
                dynamic? window = found[i];
                if (window is null)
                    continue;
                try
                {
                    dynamic items = window.Document.SelectedItems();
                    var paths = ExistingFiles((object)items);
                    if (paths.Count > 0)
                        return paths;
                }
                catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { } // e.g. Internet Explorer windows
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
        return [];
    }

    public string DescribeForegroundWindow()
    {
        var root = ForegroundRoot();
        return root == 0 ? "no foreground window" : $"{NativeMethods.ClassNameOf(root)} / {ProcessNameOf(root) ?? "?"}";
    }

    /// <summary>The top-level window of the foreground window, or 0 when there is none.</summary>
    private static nint ForegroundRoot()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return 0;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        return root == 0 ? foreground : root;
    }

    public int SystemDragThreshold => Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG));

    public bool PrefersReducedMotion =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) && !enabled;

    public void SetStartWithSystem(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }
        var command = $"\"{executablePath}\" --background";
        key.SetValue(AppName, command);
        // Security software can take the entry away as soon as it is written (#450): read it back, so the switch
        // says so instead of looking on while Filee doesn't start.
        CheckStartupEntry(command, key.GetValue(AppName));
    }

    /// <summary>Throws <see cref="StartupEntryNotKeptException"/> unless <paramref name="readBack"/> is the command written.</summary>
    internal static void CheckStartupEntry(string written, object? readBack)
    {
        if (readBack is string value && string.Equals(value, written, StringComparison.Ordinal))
            return;
        throw new StartupEntryNotKeptException(readBack is null
            ? $"The startup entry HKCU\\{RunKey}\\{AppName} was gone right after it was written."
            : $"The startup entry HKCU\\{RunKey}\\{AppName} reads \"{readBack}\" instead of \"{written}\".");
    }

    /// <summary>
    /// Registers a per-user "Convert with Filee" verb for all files (no admin rights needed) and a Send To shortcut.
    /// On Windows 11 the classic verb appears under "Show more options"; the top-level entry is the packaged
    /// IExplorerCommand (<see cref="ExplorerMenuRegistration"/>), which this switches on and off through its title file.
    /// When that package is registered the classic verb is removed, because Windows also lists the packaged command
    /// under "Show more options" and the entry would appear twice there.
    /// </summary>
    public void SetContextMenu(bool enabled, string executablePath, string label)
    {
        if (ExplorerMenuRegistration.IsSupportedOs)
            ExplorerMenuRegistration.SetTitle(enabled ? label : null);

        if (!enabled)
        {
            RemoveClassicVerb();
            TryDelete(SendToShortcutPath);
            return;
        }

        if (GetModernContextMenuState(executablePath) == ModernContextMenuState.On)
            RemoveClassicVerb();
        else
            WriteClassicVerb(executablePath, label);

        CreateShortcut(SendToShortcutPath, executablePath, "--convert", label);
    }

    public ModernContextMenuState GetModernContextMenuState(string executablePath)
    {
        try
        {
            return Path.GetDirectoryName(executablePath) is { Length: > 0 } folder
                ? ExplorerMenuRegistration.GetState(folder)
                : ModernContextMenuState.Unsupported;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Asking Windows about packages must never take the app or the settings page down.
            Trace.TraceWarning($"Reading the Explorer menu state failed: {ex}");
            return ModernContextMenuState.Unsupported;
        }
    }

    public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.Run(async () =>
    {
        var folder = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(folder))
            return new ModernContextMenuResult(false, Error: "Unknown install folder.");

        ModernContextMenuResult result;
        try
        {
            result = enabled
                ? await ExplorerMenuRegistration.AddAsync(folder)
                : await ExplorerMenuRegistration.RemoveAsync(allowElevation: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new ModernContextMenuResult(false, Error: ex.Message);
        }

        // Keep exactly one entry: the title file exists while the Explorer menu setting is on.
        if (ExplorerMenuRegistration.ReadTitle() is { Length: > 0 } title)
        {
            if (GetModernContextMenuState(executablePath) == ModernContextMenuState.On)
                RemoveClassicVerb();
            else
                WriteClassicVerb(executablePath, title);
        }
        return result;
    });

    private static void WriteClassicVerb(string executablePath, string label)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(ContextMenuKey);
        verb.SetValue("MUIVerb", label);
        verb.SetValue("Icon", $"\"{executablePath}\",0");
        // "Player": Explorer invokes the verb once per selected file; the running instance
        // collects the calls (see SingleInstance / --convert handling in the app).
        verb.SetValue("MultiSelectModel", "Player");
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{executablePath}\" --convert \"%1\"");
    }

    private static void RemoveClassicVerb() =>
        Registry.CurrentUser.DeleteSubKeyTree(ContextMenuKey, throwOnMissingSubKey: false);

    public void RevealInFileManager(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true })?.Dispose();

    public bool SupportsOpenWith => true;

    /// <summary>
    /// The dialog Explorer shows for "Open with → Choose another app". It blocks until it closes, so it runs on a
    /// thread of its own (an STA one, as the shell expects), never on the UI thread.
    /// </summary>
    public void OpenWith(string path)
    {
        var file = Path.GetFullPath(path);
        var thread = new Thread(() =>
        {
            var text = Marshal.StringToHGlobalUni(file);
            try
            {
                var info = new NativeMethods.OPENASINFO
                {
                    File = text,
                    Flags = NativeMethods.OAIF_ALLOW_REGISTRATION | NativeMethods.OAIF_REGISTER_EXT | NativeMethods.OAIF_EXEC,
                };
                // Cancelling returns an error HRESULT: nothing to report.
                NativeMethods.SHOpenWithDialog(0, in info);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        })
        { IsBackground = true, Name = "Open with" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string SendToShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Filee.lnk");

    private static nint RootWindowAt(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        return hwnd == 0 ? 0 : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
    }

    private static string? ProcessNameOf(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Creates a .lnk file through the WScript.Shell automation object.</summary>
    private static void CreateShortcut(string shortcutPath, string target, string arguments, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null)
            return;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.Description = description;
            link.IconLocation = target + ",0";
            link.Save();
        }
        catch (COMException)
        {
        }
        finally
        {
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
