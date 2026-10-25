// What Filee writes to its log before and around start-up: one line describing this launch, and every exception that
// would otherwise end the process (or vanish in a task) without a trace. A report of a Filee that ignored every
// gesture came with an empty log; these lines are what tells such cases apart.

using System.Runtime.InteropServices;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Services;

internal static class StartupDiagnostics
{
    /// <summary>
    /// A logger for Program, before the service container exists. It writes to the same daily file. A log folder that
    /// can't be created gives a logger that drops everything: logging must never stop Filee from starting.
    /// </summary>
    public static ILogger CreateEarlyLogger(string logDirectory)
        => CreateEarlyLogger(logDirectory, out _);

    /// <summary>Shares the early file provider with the logger used to load settings before Avalonia starts.</summary>
    public static ILogger CreateEarlyLogger(string logDirectory, out ILogger<UserDataStore> storeLogger)
    {
        try
        {
            var factory = LoggerFactory.Create(builder => builder.AddProvider(new FileLoggerProvider(logDirectory)));
            storeLogger = factory.CreateLogger<UserDataStore>();
            return factory.CreateLogger("Filee.App.Program");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            storeLogger = NullLogger<UserDataStore>.Instance;
            return NullLogger.Instance;
        }
    }

    /// <summary>
    /// The start-up line: "Filee 1.11.0 starting: C:\…\Filee.exe (installed), integrity Medium, Explorer Medium,
    /// Microsoft Windows 10.0.26200 X64, 2 argument(s)". Arguments are counted, not written: they are file paths.
    /// </summary>
    public static string Describe(string version, string? executable, string installKind, ElevationState elevation,
        string operatingSystem, int argumentCount) =>
        $"Filee {version} starting: {executable ?? "(unknown path)"} ({installKind}), {elevation}, {operatingSystem}, " +
        $"{argumentCount} argument(s)";

    /// <summary>How this copy got here: Setup, a release archive, or a build from source.</summary>
    public static string InstallKind(bool installed, bool release) =>
        installed ? "installed" : release ? "portable" : "development build";

    /// <summary>"Microsoft Windows 10.0.26200 X64".</summary>
    public static string OperatingSystemText() =>
        $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}";

    /// <summary>Writes exceptions from any thread and unobserved task failures to <paramref name="log"/>.</summary>
    public static void LogUnhandledExceptions(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (process ending: {Terminating}): {Object}",
                e.IsTerminating, e.ExceptionObject is Exception ? "see below" : e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.LogError(e.Exception, "A background task failed and nobody looked at the error");
    }
}
