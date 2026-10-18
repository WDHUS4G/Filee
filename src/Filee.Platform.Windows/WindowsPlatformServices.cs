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

    public void RaiseTopmost(nint windowHandle)
    {
        if (windowHandle != 0)
            NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    public bool IsFileManagerAt(int x, int y)
    {
        var root = RootWindowAt(x, y);
        if (root == 0)
            return false;
        return FileManagerClasses.Contains(NativeMethods.ClassNameOf(root))
               || string.Equals(ProcessNameOf(root), "explorer", StringComparison.OrdinalIgnoreCase);
    }

    public string? ProcessNameAt(int x, int y)
    {
        var root = RootWindowAt(x, y);
        return root == 0 ? null : ProcessNameOf(root);
    }

    /// <summary>
    /// Reads the selection of the foreground Explorer window through the Shell automation object.
    /// Must be called on an STA thread (the Avalonia UI thread is STA).
    /// </summary>
    public IReadOnlyList<string> GetFileManagerSelection()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return [];

        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null)
            return [];

        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;

            // With Explorer tabs (Windows 11) several entries share one HWND. The active tab is the one
            // that has a selection in practice, so take the first matching window with selected items.
            for (var i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window is null)
                    continue;
                try
                {
                    if ((nint)(long)window.HWND != foreground)
                        continue;
                    dynamic items = window.Document.SelectedItems();
                    var paths = new List<string>();
                    foreach (dynamic item in items)
                    {
                        string path = item.Path;
                        if (File.Exists(path))
                            paths.Add(path);
                    }
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

    public int SystemDragThreshold => Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG));

    public bool PrefersReducedMotion =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) && !enabled;

    public void SetStartWithSystem(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(AppName, $"\"{executablePath}\" --background");
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
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
