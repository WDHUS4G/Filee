using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Filee.Core.Platform;

namespace Filee.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSPlatformServices : IPlatformServices
{
    private const string FinderSelectionScript = """
        ObjC.import('Foundation');
        const finder = Application('com.apple.finder');
        JSON.stringify(finder.selection().map(item => ObjC.unwrap($.NSURL.URLWithString(item.url()).path)));
        """;

    public bool HasAccessibilityPermission => NativeMethods.AXIsProcessTrusted();

    /// <summary>Asks for Accessibility with macOS's own alert (nothing happens when Filee already has it).</summary>
    public bool RequestAccessibilityPermission() => NativeMethods.RequestAccessibility();

    public bool SupportsGlobalPointerGestures => HasAccessibilityPermission;

    public bool SupportsSelectionShortcut => true;

    public string? LastSelectionError { get; private set; }

    public bool IsFileManagerAt(int x, int y) =>
        string.Equals(ProcessNameAt(x, y), "Finder", StringComparison.OrdinalIgnoreCase);

    public string? ProcessNameAt(int x, int y) => NativeMethods.ProcessNameAt(x, y);

    public IReadOnlyList<string> RunningAppNames() => IPlatformServices.AppNames(NativeMethods.WindowOwnerNames().ToList());

    public IReadOnlyList<string> GetFileManagerSelection()
    {
        LastSelectionError = null;
        if (!NativeMethods.IsFinderFrontmost())
            return [];

        try
        {
            var output = Run("/usr/bin/osascript", ["-l", "JavaScript", "-e", FinderSelectionScript], 30_000);
            return ParseSelection(output).Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or JsonException)
        {
            LastSelectionError = ex.Message;
            Trace.TraceWarning($"Reading Finder selection failed: {ex.Message}");
            throw new InvalidOperationException($"Could not read Finder selection. Allow Filee to control Finder in System Settings > Privacy & Security > Automation. {ex.Message}", ex);
        }
    }

    // AppKit exposes no public system drag-distance preference; match its usual four-point threshold.
    public int SystemDragThreshold => 4;

    public bool PrefersReducedMotion => NativeMethods.PrefersReducedMotion();

    public void SetStartWithSystem(bool enabled, string executablePath)
    {
        var path = Path.Combine(UserLibrary, "LaunchAgents", "com.filee.app.plist");
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        RequireExecutable(executablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MacOSIntegrationFiles.LaunchAgent(executablePath), new UTF8Encoding(false));
    }

    /// <summary>
    /// Shows or hides Filee's own service in Finder's right-click menu (the same switch as System Settings >
    /// Keyboard > Keyboard Shortcuts > Services), and removes the Automator Quick Action earlier builds installed.
    /// </summary>
    public void SetContextMenu(bool enabled, string executablePath, string label)
    {
        RemoveLegacyWorkflow();
        Run("/usr/bin/defaults", ["write", "pbs", "NSServicesStatus", "-dict-add",
            MacOSIntegrationFiles.ServiceStatusKey, MacOSIntegrationFiles.ServiceStatus(enabled)], 10_000);
        Run("/System/Library/CoreServices/pbs", ["-update"], 10_000);
    }

    private static void RemoveLegacyWorkflow()
    {
        var workflow = Path.Combine(UserLibrary, "Services", "Filee.workflow");
        if (Directory.Exists(workflow))
            Directory.Delete(workflow, recursive: true);
    }

    public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;

    public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
        Task.FromResult(new ModernContextMenuResult(false, Error: "The Windows Explorer extension is unavailable on macOS. Use the Finder Quick Action."));

    public void RevealInFileManager(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Run("/usr/bin/open", File.Exists(fullPath) ? ["-R", fullPath] : [fullPath], 10_000);
    }

    public void OpenAccessibilitySettings() =>
        Run("/usr/bin/open", ["x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"], 10_000);

    public void RaiseTopmost(nint windowHandle)
    {
        if (windowHandle != 0)
            NativeMethods.RaiseTopmost(windowHandle);
    }

    internal static IReadOnlyList<string> ParseSelection(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static string UserLibrary => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");

    private static void RequireExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("Filee's executable must have an existing absolute path.", path);
    }

    private static string Run(string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(executable)} did not respond within {timeoutMs / 1000} seconds.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error.GetAwaiter().GetResult().Trim()}");
        return output.GetAwaiter().GetResult();
    }
}
