// Entry point. Order matters:
//  0. The log and the handlers for unhandled exceptions, so even a start that fails leaves a line; then one line
//     describing this launch (version, path, rights, OS).
//  1. Single instance: a second launch (e.g. from the Explorer context menu) forwards its arguments
//     to the running instance and exits. When it can't (the running Filee is elevated, or hung) it says so.
//  2. Installer commands (--quit, --uninstall-cleanup) run without any window.
//  3. Start Avalonia. The app lives in the tray, so it only exits via the tray "Quit" command (or --quit).

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Filee.App.Services;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App;

internal static class Program
{
    /// <summary>Parsed command line of this process.</summary>
    public static CommandLine Options { get; private set; } = CommandLine.Parse([]);

    /// <summary>Owned by the first instance for its whole lifetime.</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>How long the installer waits for a running Filee to save and exit.</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a later launch waits for a Filee that is still starting to take its arguments.</summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Integrity levels of this process and Explorer, read at start-up (Unknown off Windows).</summary>
    public static ElevationState Elevation { get; private set; } = ElevationState.Unknown;

    /// <summary>Program's own log, set up before anything else so even a failed start leaves a line.</summary>
    internal static ILogger Log { get; private set; } = NullLogger.Instance;

    /// <summary>Settings loaded before macOS initializes its Dock policy.</summary>
    internal static UserDataStore? PreloadedStore { get; private set; }

    /// <summary>Raised on the UI thread when the single-instance lock was taken again after a failed restart.</summary>
    public static event Action<SingleInstance>? InstanceAcquired;

    [STAThread]
    public static int Main(string[] args)
    {
        Options = CommandLine.Parse(args);
        PreloadedStore = null;
        Log = StartupDiagnostics.CreateEarlyLogger(Path.Combine(UserDataStore.DefaultDirectory, "logs"), out var earlyStoreLogger);
        StartupDiagnostics.LogUnhandledExceptions(Log);
        Elevation = Filee.Platform.Windows.Elevation.Probe();
        var startup = StartupDiagnostics.Describe(UpdateService.CurrentVersion, Environment.ProcessPath,
            StartupDiagnostics.InstallKind(UpdateService.IsInstalled, UpdateService.IsReleaseBuild), Elevation,
            StartupDiagnostics.OperatingSystemText(), args.Length);
        var installerCommand = Options.Quit || Options.UninstallCleanup;

        SingleInstance instance;
        try
        {
            instance = SingleInstance.Acquire();
        }
        catch (UnauthorizedAccessException ex)
        {
            // The lock belongs to a Filee running as administrator: this process may not even open it. Before this
            // was caught, Filee ended here without a word (Explorer's menu showed a busy cursor, then nothing).
            Log.LogWarning("{Startup}", startup);
            Log.LogWarning(ex, "Another Filee is running with administrator rights; this launch can't reach it");
            if (!installerCommand)
                TellUserOnce("startup.elevated_other");
            return 1;
        }
        Instance = instance;
        if (installerCommand)
        {
            Log.LogInformation("{Startup}", startup);
            return RunInstallerCommand(instance);
        }
        if (!instance.IsFirst)
        {
            var result = instance.Forward(args, ForwardTimeout);
            if (result != ForwardResult.Delivered)
            {
                // Logged only when it failed: Explorer starts one Filee per selected file.
                Log.LogWarning("{Startup}", startup);
                Log.LogWarning("Could not hand {Count} argument(s) to the running Filee: {Result}", args.Length, result);
                TellUserOnce(result == ForwardResult.AccessDenied ? "startup.elevated_other" : "startup.no_answer");
            }
            ReleaseInstance();
            return result == ForwardResult.Delivered ? 0 : 1;
        }

        Log.LogInformation("{Startup}", startup);
        if (Elevation.ShellCannotReachFilee)
            Log.LogWarning("Filee runs as administrator ({Elevation}) while Explorer doesn't: Explorer can't drop files on it, " +
                           "use its menu or have its selection read", Elevation);

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                PreloadedStore = new UserDataStore(UserDataStore.DefaultDirectory, earlyStoreLogger);
                PreloadedStore.Load();
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (InvalidOperationException ex) when (OperatingSystem.IsMacOS() && ex.Message.Contains("RenderTimer", StringComparison.Ordinal))
        {
            // The window system has no display link for this screen: a Mac in a virtual machine without graphics
            // acceleration (VMware). Avalonia can't draw there; say so instead of quitting without a word.
            StartupFailure.Report(ex, "Filee can't open its window on this Mac: macOS reports no display it can draw on. " +
                                      "This happens in virtual machines without graphics acceleration (such as VMware). " +
                                      "The filee command still works in Terminal.");
            return 1;
        }
        finally
        {
            ReleaseInstance();
        }
    }

    /// <summary>Lets go of the single-instance lock (at exit, or before restarting without administrator rights).</summary>
    public static void ReleaseInstance()
    {
        Instance?.Dispose();
        Instance = null;
    }

    /// <summary>Takes the lock back after a restart that didn't happen, so later launches find this Filee again.</summary>
    public static void ReacquireInstance()
    {
        if (Instance is not null)
            return;
        try
        {
            var instance = SingleInstance.Acquire();
            if (!instance.IsFirst)
            {
                // Another Filee started meanwhile (perhaps the restart did happen after all): it is the running one.
                Log.LogWarning("Another Filee took the single-instance lock during the restart");
                instance.Dispose();
                return;
            }
            Instance = instance;
            InstanceAcquired?.Invoke(instance);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Could not take the single-instance lock back");
        }
    }

    /// <summary>
    /// Shows a launch problem in a system message box (Filee's own windows aren't available in this process). Explorer
    /// starts one Filee per selected file: a lock shared between them lets only the first one show it.
    /// </summary>
    private static void TellUserOnce(string key)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var shown = new Mutex(initiallyOwned: true, @"Local\" + SingleInstance.DefaultName + "-notice", out var createdNew);
            if (!createdNew)
                return;
            try
            {
                Filee.Platform.Windows.Elevation.ShowWarning(LocalizationService.EarlyText(key));
            }
            finally
            {
                shown.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            Log.LogWarning(ex, "Could not show the launch problem");
        }
    }

    /// <summary>
    /// <c>--quit</c> and <c>--uninstall-cleanup</c> from the installer: ask the running Filee (if any) to exit and wait
    /// for it, so its files can be replaced or removed; then clean up for the uninstaller. Exit code 1 when Filee is
    /// still running.
    /// </summary>
    private static int RunInstallerCommand(SingleInstance instance)
    {
        using (instance)
        {
            var deadline = DateTime.UtcNow + QuitTimeout;
            if (!instance.IsFirst)
            {
                // A Filee that is still starting takes the request once it listens; then it has the rest to exit.
                instance.ForwardToFirst(["--quit"], QuitTimeout);
                var left = deadline - DateTime.UtcNow;
                if (!instance.WaitForFirstToExit(left > TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5)))
                    return 1;
            }
            // The mutex is released just before the process ends: wait for the end, so Filee.exe is no longer in use.
            if (!WaitForOtherProcessesOfThisExe(deadline))
                return 1;
            if (Options.UninstallCleanup)
                return UninstallCleanup.Run() ? 0 : 1;
            return 0;
        }
    }

    private static bool WaitForOtherProcessesOfThisExe(DateTime deadline)
    {
        if (Environment.ProcessPath is not { } exe)
            return true;
        if (!OperatingSystem.IsWindows())
        {
            // Process.WaitForExit counts a process that has ended but not been reaped by its parent (a zombie) as still
            // running, so it would wait for the deadline. Such a process no longer has an executable path: look again
            // until no other process runs this exe.
            while (true)
            {
                var others = OtherProcessesOfThisExe(exe);
                var running = others.Count > 0;
                others.ForEach(p => p.Dispose());
                if (!running)
                    return true;
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(100);
            }
        }
        foreach (var process in OtherProcessesOfThisExe(exe))
        {
            using (process)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !process.WaitForExit(left))
                        return false;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile.
                }
            }
        }
        return true;
    }

    /// <summary>Other running processes started from <paramref name="exe"/> (the caller disposes them).</summary>
    private static List<Process> OtherProcessesOfThisExe(string exe)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try
            {
                if (process.Id != Environment.ProcessId
                    && string.Equals(process.MainModule?.FileName, exe, Filee.Core.Platform.FileSystemPaths.Comparison))
                {
                    found.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Exited meanwhile, or another user's process we may not inspect.
            }
            process.Dispose();
        }
        return found;
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
#if FILEE_NATIVE_WAYLAND
        // The native backend is only compiled into Linux builds and only used when asked for (FILEE_USE_WAYLAND=1).
        builder = Filee.Platform.Linux.WaylandSupport.NativeWaylandRequested ? builder.UseWayland() : builder.UsePlatformDetect();
#else
        builder = builder.UsePlatformDetect();
#endif

        if (OperatingSystem.IsMacOS())
            builder = builder.With(new MacOSPlatformOptions { ShowInDock = PreloadedStore?.Settings.ShowInDock ?? true });
        if (OperatingSystem.IsWindows())
            builder = builder.With(WindowsRendering.Options(
                WindowsRendering.Normalize(Environment.GetEnvironmentVariable(WindowsRendering.Variable)),
                adapters => Log.LogInformation("Graphics adapters: {Adapters}", adapters)));

        builder = builder
            .ConfigureFonts(fonts => fonts.AddFontCollection(new FileeFontCollection()))
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();

        AvaloniaLogForwarder.Install(Log);
        return builder;
    }
}

/// <summary>Embedded Noto fonts: <c>fonts:Filee#Noto Sans</c>, <c>#Noto Sans JP</c>, <c>#Noto Sans KR</c>,
/// <c>#Noto Sans SC</c>, <c>#Noto Sans Devanagari</c>, <c>#Noto Sans Arabic</c>, <c>#Noto Sans Thai</c>.</summary>
internal sealed class FileeFontCollection() : Avalonia.Media.Fonts.EmbeddedFontCollection(
    new Uri("fonts:Filee", UriKind.Absolute),
    new Uri("avares://Filee/Assets/Fonts", UriKind.Absolute));
