using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public sealed class SystemIntegrationTests
{
    [Fact]
    public void Unrelated_settings_do_not_reregister_the_file_manager()
    {
        var platform = new RecordingPlatform();
        var service = Create(platform);
        var settings = new AppSettings();
        service.Apply(settings);
        settings.Donut.OuterRadius = 170;
        service.Apply(settings);
        Assert.Equal(1, platform.StartupCalls);
        Assert.Equal(1, platform.MenuCalls);
        settings.ContextMenuEnabled = false;
        service.Apply(settings);
        Assert.Equal(1, platform.StartupCalls);
        Assert.Equal(2, platform.MenuCalls);
    }

    [Fact]
    public void Failed_registration_is_reported_and_can_be_retried_without_repeating_successful_work()
    {
        var platform = new RecordingPlatform { FailMenu = true };
        var service = Create(platform);
        var settings = new AppSettings();
        service.Apply(settings);
        Assert.Contains("denied", service.Error);
        platform.FailMenu = false;
        service.Apply(settings);
        Assert.Null(service.Error);
        Assert.Equal(1, platform.StartupCalls);
        Assert.Equal(2, platform.MenuCalls);
    }

    [Fact]
    public void A_startup_entry_that_did_not_stay_shows_under_the_switch_and_is_written_again_next_time()
    {
        // #450: security software removing the Run entry left the switch on with nothing said.
        var platform = new RecordingPlatform { LoseStartupEntry = true };
        var service = Create(platform);
        var settings = new AppSettings { StartWithSystem = true };
        service.Apply(settings);
        Assert.Equal("general.integration_failed: general.startup_entry_missing", service.Error);

        platform.LoseStartupEntry = false;
        service.Apply(settings);
        Assert.Null(service.Error);
        Assert.Equal(2, platform.StartupCalls);
    }

    [Fact]
    public void Source_builds_do_not_register_os_integration()
    {
        var platform = new RecordingPlatform();
        var service = new SystemIntegrationService(platform, new Texts(), NullLogger<SystemIntegrationService>.Instance)
        {
            RegistrationEnabled = false,
        };
        service.Apply(new AppSettings());
        Assert.Equal(0, platform.StartupCalls);
        Assert.Equal(0, platform.MenuCalls);
    }

    [Fact]
    public void A_copy_with_its_own_data_folder_does_not_register_os_integration()
    {
        var platform = new RecordingPlatform();
        var service = new SystemIntegrationService(platform, new Texts(), NullLogger<SystemIntegrationService>.Instance)
        {
            RegistrationEnabled = true,
            SeparateDataDirectory = true,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "Filee test", "Filee"),
        };
        service.Apply(new AppSettings { StartWithSystem = true, ContextMenuEnabled = true });
        Assert.Equal(0, platform.StartupCalls);
        Assert.Equal(0, platform.MenuCalls);
    }

    private static SystemIntegrationService Create(RecordingPlatform platform) =>
        new(platform, new Texts(), NullLogger<SystemIntegrationService>.Instance)
        {
            RegistrationEnabled = true,
            SeparateDataDirectory = false,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "Filee test", "Filee"),
        };

    private sealed class Texts : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key + ": " + string.Join(", ", args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private sealed class RecordingPlatform : IPlatformServices
    {
        public int StartupCalls { get; private set; }
        public int MenuCalls { get; private set; }
        public bool FailMenu { get; set; }
        public bool LoseStartupEntry { get; set; }
        public void SetStartWithSystem(bool enabled, string executablePath)
        {
            StartupCalls++;
            if (LoseStartupEntry)
                throw new StartupEntryNotKeptException("gone");
        }
        public void SetContextMenu(bool enabled, string executablePath, string label)
        {
            MenuCalls++;
            if (FailMenu)
                throw new IOException("denied");
        }
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
            Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
