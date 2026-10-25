// Composition root: every long-lived service is created here. Views get services through AppHost.Get<T>().

using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Core.Web;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using Filee.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public static class AppHost
{
    private static IServiceProvider? _services;

    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("AppHost not built.");

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static IServiceProvider Build(string? dataDirectory = null, UserDataStore? preloadedStore = null)
    {
        var dataDir = dataDirectory ?? UserDataStore.DefaultDirectory;
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs")))
            .SetMinimumLevel(LogLevel.Information));

        services.AddSingleton(sp => preloadedStore ?? new UserDataStore(dataDir, sp.GetRequiredService<ILogger<UserDataStore>>()));
        services.AddSingleton<IPlatformServices>(_ =>
            OperatingSystem.IsWindows() ? new WindowsPlatformServices()
            : OperatingSystem.IsMacOS() ? new MacOSPlatformServices()
            : OperatingSystem.IsLinux() ? new LinuxPlatformServices()
            : new NullPlatformServices());

        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizer>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SystemIntegrationService>();
        services.AddSingleton<ElevationService>();

        // Conversion engines
        services.AddSingleton(_ => new EngineEnvironment(dataDir));
        services.AddSingleton(sp => EngineRegistry.CreateAll(sp.GetRequiredService<EngineEnvironment>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new ConverterCatalog(sp.GetRequiredService<IReadOnlyList<IConverter>>()));
        services.AddSingleton(sp => new JobQueue(
            sp.GetRequiredService<ConverterCatalog>(),
            EngineRegistry.FindPdfMerger(sp.GetRequiredService<IReadOnlyList<IConverter>>()),
            sp.GetRequiredService<ILogger<JobQueue>>(),
            EngineRegistry.FindFileCombiner(sp.GetRequiredService<IReadOnlyList<IConverter>>()),
            EngineRegistry.FindTiffMerger(sp.GetRequiredService<IReadOnlyList<IConverter>>()),
            EngineRegistry.FindMediaJoiner(sp.GetRequiredService<IReadOnlyList<IConverter>>()),
            sp.GetRequiredService<IPlatformServices>().MoveToTrash));

        // Files dragged from a browser with only an address are downloaded without the browser's cookies.
        services.AddSingleton(_ => new WebDownloader(userAgent: $"Filee/{typeof(AppHost).Assembly.GetName().Version?.ToString(3)}"));
        services.AddSingleton<EngineDownloadService>();
        services.AddSingleton<PresetAvailability>();
        services.AddSingleton<RadialViewModel>();
        services.AddSingleton<ConversionService>();
        services.AddSingleton<SpreadsheetPrintService>();
        services.AddSingleton<WatchFolderService>();
        services.AddSingleton<TriggerService>();
        services.AddSingleton<RadialController>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MemoryTrimmer>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<WhatsNewService>();

        _services = services.BuildServiceProvider();
        return _services;
    }
}
