// The options Setup (src/Filee.Setup) starts Filee with and the commands it runs. Filee.Setup.Tests checks that what
// Setup writes is what these parse.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public class InstallerTests
{
    [Fact]
    public void Installer_choices_are_parsed()
    {
        var options = CommandLine.Parse(["--install-engines=pandoc, FFmpeg,pandoc", "--start-with-windows=off", "--context-menu=on", "--language=ko"]);
        Assert.Equal(["pandoc", "ffmpeg"], options.InstallEngines);
        Assert.False(options.StartWithWindows);
        Assert.True(options.ContextMenu);
        Assert.Equal("ko", options.Language);
        Assert.False(options.Background);
        Assert.Empty(options.ConvertFiles);

        // Picking no engine is an answer too (the first-run question is skipped), unlike not passing the option.
        Assert.Empty(CommandLine.Parse(["--install-engines="]).InstallEngines!);
        Assert.Null(CommandLine.Parse(["--background"]).InstallEngines);
        Assert.Null(CommandLine.Parse(["--start-with-windows=maybe"]).StartWithWindows);
        Assert.Null(CommandLine.Parse([]).ContextMenu);
        Assert.Null(CommandLine.Parse(["--language="]).Language);
        Assert.True(CommandLine.Parse(["--start-with-system=on"]).StartWithWindows);
    }

    [Fact]
    public void Quit_and_cleanup_are_recognised()
    {
        Assert.True(CommandLine.Parse(["--quit"]).Quit);
        Assert.True(CommandLine.Parse(["--Uninstall-Cleanup"]).UninstallCleanup);
        Assert.False(CommandLine.Parse(["--background"]).Quit);
    }

    [Fact]
    public void Installed_copies_are_recognised_by_their_uninstaller()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-installed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(UpdateService.IsInstalledIn(folder));
            // Filee's own Setup (2.0 and later).
            File.WriteAllText(Path.Combine(folder, "uninstall.exe"), "");
            Assert.True(UpdateService.IsInstalledIn(folder));
            // Inno Setup (1.x), until Setup takes the installation over.
            File.Delete(Path.Combine(folder, "uninstall.exe"));
            File.WriteAllText(Path.Combine(folder, "unins000.exe"), "");
            Assert.True(UpdateService.IsInstalledIn(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Engines_from_the_installer_start_only_when_something_is_left_to_install()
    {
        TestServices.EnsureInitialized("en");
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());
        Assert.False(vm.InstallNow(["no-such-engine"]));
        Assert.False(vm.InstallNow([]));
        Assert.False(vm.Started);
    }
}
