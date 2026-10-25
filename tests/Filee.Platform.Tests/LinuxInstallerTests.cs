using System.Diagnostics;
using System.Runtime.Versioning;

namespace Filee.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-installer-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _installScriptPath;
    private readonly string _shimBinDir;

    public LinuxInstallerTests()
    {
        Directory.CreateDirectory(_root);

        // Find the repository root containing installer/linux/install.sh
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Filee.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Repository root not found.");
        }
        _installScriptPath = Path.Combine(dir, "installer", "linux", "install.sh");

        _shimBinDir = Path.Combine(_root, "shim-bin");
        Directory.CreateDirectory(_shimBinDir);
        if (OperatingSystem.IsLinux())
        {
            var shimPath = Path.Combine(_shimBinDir, "mv");
            File.WriteAllText(shimPath, @"#!/bin/sh
set -e

# Fault-injection shim for LinuxInstallerTests
REAL_MV=$(PATH=""/usr/bin:/bin"" command -v mv)

if [ ""${SHIM_FAIL_ACTIVATION:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            exit 1
            ;;
    esac
fi

if [ ""${SHIM_FAIL_ACTIVATION_OCCUPIED:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            mkdir -p ""$2""
            touch ""$2/conflict""
            chmod 555 ""$2""
            exit 1
            ;;
    esac
fi

if [ ""${SHIM_FAIL_POST_VALIDATION:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            res=$?
            rm -f ""$2/.filee-package""
            exit ""$res""
            ;;
    esac
fi

if [ ""${SHIM_FAIL_CLEANUP_FAILED_DIR:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            res=$?
            mkdir -p ""$2/locked-folder""
            touch ""$2/locked-folder/file""
            chmod 555 ""$2/locked-folder""
            rm -f ""$2/.filee-package""
            exit ""$res""
            ;;
    esac
fi

if [ ""${SHIM_FAIL_QUARANTINE:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            res=$?
            rm -f ""$2/.filee-package""
            exit ""$res""
            ;;
    esac
    case ""$2"" in
        *.failed-*)
            exit 1
            ;;
    esac
fi

if [ ""${SHIM_FAIL_FOREIGN_DESTINATION:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            rm -rf ""$2""
            mkdir -p ""$2""
            echo ""important-foreign-data"" > ""$2/important.txt""
            exit 1
            ;;
    esac
fi

if [ ""${SHIM_FAIL_ROLLBACK_OCCUPIED:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            rm -rf ""$2""
            mkdir -p ""$2""
            touch ""$2/conflict""
            chmod 555 ""$2""
            exit 1
            ;;
    esac
fi

if [ ""${SHIM_FAIL_ROLLBACK_ABSENT:-0}"" = ""1"" ]; then
    case ""$1"" in
        *.staging-*)
            ""$REAL_MV"" ""$@""
            parent_dir=$(dirname ""$2"")
            rm -rf ""$2""
            chmod 555 ""$parent_dir""
            exit 1
            ;;
    esac
fi

exec ""$REAL_MV"" ""$@""
");
            File.SetUnixFileMode(shimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    [Fact]
    public void First_time_installation_succeeds_in_isolated_environment()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");

        var (exitCode, output, error) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        Assert.True(exitCode == 0, $"Install failed with code {exitCode}. Output: {output}. Error: {error}");
        Assert.Contains("Installed Filee at", output);

        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));
        Assert.True(File.Exists(Path.Combine(installDir, "filee-cli")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());

        var markerPath = Path.Combine(installDir, ".filee-package");
        Assert.True(File.Exists(markerPath));
        Assert.Equal("com.filee.app", File.ReadAllText(markerPath).Trim());
        Assert.False(File.Exists(Path.Combine(installDir, ".filee-upgrade-token")));

        var launcherPath = Path.Combine(env.HomeDir, ".local", "bin", "filee");
        Assert.True(File.Exists(launcherPath));
        Assert.Equal(Path.Combine(installDir, "filee-cli"), File.ResolveLinkTarget(launcherPath, true)?.FullName);

        var desktopPath = Path.Combine(env.DataDir, "applications", "com.filee.app.desktop");
        Assert.True(File.Exists(desktopPath));
        Assert.Contains("X-Filee-Managed=true", File.ReadAllText(desktopPath));
    }

    [Fact]
    public void First_time_installation_fails_if_already_installed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");

        var (exitCode1, _, _) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);
        Assert.Equal(0, exitCode1);

        // Second run without --upgrade must fail and refuse to overwrite
        var (exitCode2, output2, error2) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);
        Assert.NotEqual(0, exitCode2);
        Assert.Contains("An installation already exists", output2 + error2);
    }

    [Fact]
    public void Upgrade_succeeds_and_replaces_binaries_while_preserving_engine_folder_and_settings()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");

        var (exitCode1, _, _) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);
        Assert.Equal(0, exitCode1);

        var installDir = Path.Combine(env.DataDir, "filee", "application");

        // Simulate user downloaded engines in ~/.local/share/Filee/engines
        var engineDir = Path.Combine(env.DataDir, "Filee", "engines", "ffmpeg");
        Directory.CreateDirectory(engineDir);
        var mockEngineBinary = Path.Combine(engineDir, "ffmpeg");
        File.WriteAllText(mockEngineBinary, "ffmpeg-binary-data");

        // Simulate user settings in ~/.config/Filee
        var configDir = Path.Combine(env.HomeDir, ".config", "Filee");
        Directory.CreateDirectory(configDir);
        var mockSettings = Path.Combine(configDir, "settings.json");
        File.WriteAllText(mockSettings, "{\"custom\": true}");

        // Now prepare a v2.0.0 payload in another directory
        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Run upgrade
        var (exitCode2, output2, error2) = RunInstaller(v2Source, env.DataDir, env.HomeDir, "--upgrade");
        Assert.True(exitCode2 == 0, $"Upgrade failed with code {exitCode2}. Output: {output2}. Error: {error2}");
        Assert.Contains("Upgraded Filee at", output2);

        // Verify application was updated to v2.0.0
        Assert.Equal("v2.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());

        // Verify downloaded engines are STILL PRESERVED!
        Assert.True(File.Exists(mockEngineBinary));
        Assert.Equal("ffmpeg-binary-data", File.ReadAllText(mockEngineBinary));

        // Verify settings are STILL PRESERVED!
        Assert.True(File.Exists(mockSettings));
        Assert.Equal("{\"custom\": true}", File.ReadAllText(mockSettings));

        // Verify launcher symlink, package marker, and token cleanup
        var launcherPath = Path.Combine(env.HomeDir, ".local", "bin", "filee");
        Assert.Equal(Path.Combine(installDir, "filee-cli"), File.ResolveLinkTarget(launcherPath, true)?.FullName);
        Assert.Equal("com.filee.app", File.ReadAllText(Path.Combine(installDir, ".filee-package")).Trim());
        Assert.False(File.Exists(Path.Combine(installDir, ".filee-upgrade-token")));
    }

    [Fact]
    public void Upgrade_fails_if_not_already_installed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");

        // Running with --upgrade when no installation exists must fail cleanly
        var (exitCode, output, error) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir, "--upgrade");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("No existing installation found", output + error);
    }

    [Fact]
    public void Upgrade_fails_if_marker_is_invalid_and_preserves_original_install()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var installDir = Path.Combine(env.DataDir, "filee", "application");
        var markerPath = Path.Combine(installDir, ".filee-package");
        File.WriteAllText(markerPath, "alien.app");

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir, "--upgrade");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("not installed by Filee", output + error);

        // Original installation must be untouched
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
    }

    [Fact]
    public void Upgrade_fails_if_payload_is_missing_executables_and_preserves_original_install()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var installDir = Path.Combine(env.DataDir, "filee", "application");

        // Broken v2 source: missing filee-cli
        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");
        File.Delete(Path.Combine(v2Source, "filee-cli"));

        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir, "--upgrade");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("Payload validation failed", output + error);

        // Original installation must still be intact
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
    }

    [Fact]
    public void Upgrade_invokes_quit_on_running_filee_instance()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var installDir = Path.Combine(env.DataDir, "filee", "application");
        var quitLog = Path.Combine(env.DataDir, "quit.log");

        // Replace Filee with a mock script that records --quit
        var fileeScript = Path.Combine(installDir, "Filee");
        File.WriteAllText(fileeScript, $"#!/bin/sh\nif [ \"$1\" = \"--quit\" ]; then echo \"quit-called\" >> \"{quitLog}\"; exit 0; fi\nexit 0\n");
        File.SetUnixFileMode(fileeScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir, "--upgrade");
        Assert.True(exitCode == 0, $"Upgrade with quit failed with code {exitCode}. Output: {output}. Error: {error}");
        Assert.True(File.Exists(quitLog));
        Assert.Contains("quit-called", File.ReadAllText(quitLog));
    }

    [Fact]
    public void Upgrade_rolls_back_if_activation_move_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var installDir = Path.Combine(env.DataDir, "filee", "application");

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate failure during activation move using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_ACTIVATION"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Restored the previous Filee installation", output + error);

        // Original installation must be safely restored
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
        Assert.Equal("com.filee.app", File.ReadAllText(Path.Combine(installDir, ".filee-package")).Trim());
    }

    [Fact]
    public void Upgrade_rolls_back_if_post_swap_validation_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var installDir = Path.Combine(env.DataDir, "filee", "application");

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate failure during post-swap validation using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_POST_VALIDATION"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Activated installation failed post-swap validation", output + error);
        Assert.Contains("Restored the previous Filee installation", output + error);

        // Original installation must be safely restored
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
        Assert.Equal("com.filee.app", File.ReadAllText(Path.Combine(installDir, ".filee-package")).Trim());
    }

    [Fact]
    public void Upgrade_reports_critical_error_and_preserves_backup_if_rollback_destination_is_occupied()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate failure during rollback where destination is occupied using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_ROLLBACK_OCCUPIED"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRITICAL: Update failed and automatic rollback could not complete", output + error);
        Assert.Contains("Your previous installation is preserved at", output + error);
        Assert.Contains("A partial or invalid directory exists at:", output + error);
        Assert.Contains("inspect and remove or rename", output + error);

        // Verify that the backup directory still exists and holds the original installation
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.NotEmpty(backupDirs);
        var backupDir = backupDirs[0];
        Assert.True(File.Exists(Path.Combine(backupDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(backupDir, "version.txt")).Trim());

        // Verify that the occupied conflict file was preserved and not overwritten
        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "conflict")));
    }

    [Fact]
    public void Upgrade_reports_critical_error_if_destination_occupied_during_activation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate destination conflict during activation using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_ACTIVATION_OCCUPIED"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRITICAL: Update failed and automatic rollback could not complete", output + error);
        Assert.Contains("Your previous installation is preserved at", output + error);
        Assert.Contains("A partial or invalid directory exists at:", output + error);
        Assert.Contains("inspect and remove or rename", output + error);

        // Verify that the backup directory still exists and holds the original installation
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.NotEmpty(backupDirs);
        var backupDir = backupDirs[0];
        Assert.True(File.Exists(Path.Combine(backupDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(backupDir, "version.txt")).Trim());

        // Verify that the unmanaged conflict was not touched
        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "conflict")));
    }

    [Fact]
    public void Upgrade_reports_critical_error_and_preserves_backup_if_rollback_destination_is_absent_but_move_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate failure during rollback when install_dir is absent using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_ROLLBACK_ABSENT"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRITICAL: Update failed and automatic rollback could not complete", output + error);
        Assert.Contains("Your previous installation is preserved at", output + error);
        Assert.Contains("To restore manually, run: mv", output + error);
        Assert.DoesNotContain("A partial or invalid directory exists at:", output + error);

        // Verify that the backup directory still exists and holds the original installation
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.NotEmpty(backupDirs);
        var backupDir = backupDirs[0];
        Assert.True(File.Exists(Path.Combine(backupDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(backupDir, "version.txt")).Trim());

        var installDir = Path.Combine(fileeDir, "application");
        Assert.False(Directory.Exists(installDir));
        Assert.Contains($"To restore manually, run: mv '{backupDir}' '{installDir}'", output + error);
    }

    [Fact]
    public void Upgrade_preserves_destination_and_backup_if_quarantine_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate quarantine failure during rollback using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_QUARANTINE"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRITICAL: Update failed and automatic rollback could not complete", output + error);
        Assert.Contains("Your previous installation is preserved at", output + error);
        Assert.Contains("A partial or invalid directory exists at:", output + error);
        Assert.Contains("inspect and remove or rename", output + error);

        // Verify that the destination was NOT deleted or removed via fallback
        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));

        // Verify that the backup directory still exists and holds the original installation
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.NotEmpty(backupDirs);
        var backupDir = backupDirs[0];
        Assert.True(File.Exists(Path.Combine(backupDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(backupDir, "version.txt")).Trim());
    }

    [Fact]
    public void Upgrade_restores_backup_and_reports_quarantine_if_quarantine_cleanup_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate quarantine cleanup failure using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_CLEANUP_FAILED_DIR"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Update failed. Restored the previous Filee installation", output + error);
        Assert.Contains("Quarantined failed update files preserved at:", output + error);

        // Verify that the old installation was successfully restored to application directory
        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());

        // Verify that the quarantined failed directory still exists on disk and was preserved
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var failedDirs = Directory.GetDirectories(fileeDir, ".failed-*");
        Assert.NotEmpty(failedDirs);
        var failedDir = failedDirs[0];
        Assert.True(Directory.Exists(Path.Combine(failedDir, "locked-folder")));

        // Verify backup was safely restored into installDir (not abandoned in backup)
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.Empty(backupDirs);
    }

    [Fact]
    public void Upgrade_preserves_preexisting_backup_and_failed_directories_from_earlier_runs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        // Create a pre-existing backup and failed directory from earlier interrupted runs
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var oldBackupDir = Path.Combine(fileeDir, ".backup-earlier-crash-999");
        Directory.CreateDirectory(oldBackupDir);
        var oldVersionFile = Path.Combine(oldBackupDir, "version.txt");
        File.WriteAllText(oldVersionFile, "v0.9.0-backup-preserved");

        var oldFailedDir = Path.Combine(fileeDir, ".failed-earlier-crash-888");
        Directory.CreateDirectory(oldFailedDir);
        var oldFailedFile = Path.Combine(oldFailedDir, "failed.txt");
        File.WriteAllText(oldFailedFile, "v0.9.0-failed-preserved");

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Run upgrade
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir, "--upgrade");
        Assert.True(exitCode == 0, $"Upgrade failed with code {exitCode}. Output: {output}. Error: {error}");

        var installDir = Path.Combine(fileeDir, "application");
        Assert.Equal("v2.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());

        // Verify the pre-existing backup and failed directories from earlier crash were NOT erased!
        Assert.True(Directory.Exists(oldBackupDir));
        Assert.True(File.Exists(oldVersionFile));
        Assert.Equal("v0.9.0-backup-preserved", File.ReadAllText(oldVersionFile).Trim());

        Assert.True(Directory.Exists(oldFailedDir));
        Assert.True(File.Exists(oldFailedFile));
        Assert.Equal("v0.9.0-failed-preserved", File.ReadAllText(oldFailedFile).Trim());
    }

    [Fact]
    public void Upgrade_preserves_foreign_directory_if_destination_was_replaced_before_rollback()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Simulate replacement of install_dir by a foreign directory before rollback using external mv shim
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["SHIM_FAIL_FOREIGN_DESTINATION"] = "1" }, "--upgrade");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRITICAL: Update failed and automatic rollback could not complete", output + error);
        Assert.Contains("Your previous installation is preserved at", output + error);
        Assert.Contains("A partial or invalid directory exists at:", output + error);
        Assert.Contains("inspect and remove or rename", output + error);

        // Verify that the foreign directory and its contents were NOT deleted
        var installDir = Path.Combine(env.DataDir, "filee", "application");
        var foreignFile = Path.Combine(installDir, "important.txt");
        Assert.True(File.Exists(foreignFile));
        Assert.Equal("important-foreign-data", File.ReadAllText(foreignFile).Trim());

        // Verify that the backup directory still exists and holds the original installation
        var fileeDir = Path.Combine(env.DataDir, "filee");
        var backupDirs = Directory.GetDirectories(fileeDir, ".backup-*");
        Assert.NotEmpty(backupDirs);
        var backupDir = backupDirs[0];
        Assert.True(File.Exists(Path.Combine(backupDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(backupDir, "version.txt")).Trim());
    }

    [Fact]
    public void Test_hook_environment_variables_have_no_effect_on_installer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");
        RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);

        var v2Source = Path.Combine(_root, "source-v2");
        CreateMockPayload(v2Source, "v2.0.0");

        // Setting obsolete internal test hook variables has no effect because install.sh contains no test hooks
        var (exitCode, output, error) = RunInstaller(v2Source, env.DataDir, env.HomeDir,
            new Dictionary<string, string> { ["_FILEE_TEST_FAIL_ACTIVATION"] = "1", ["_FILEE_TEST_HARNESS"] = "com.filee.test.harness" }, "--upgrade");

        Assert.True(exitCode == 0, $"Upgrade failed unexpectedly. Output: {output}. Error: {error}");
        Assert.Contains("Upgraded Filee at", output);

        var installDir = Path.Combine(env.DataDir, "filee", "application");
        Assert.Equal("v2.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
    }

    [Fact]
    public void Installation_and_upgrade_succeed_with_spaces_and_special_characters_in_paths()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var id = Guid.NewGuid().ToString("N");
        var sourceDir = Path.Combine(_root, "source path with spaces " + id);
        var dataDir = Path.Combine(_root, "data path with spaces " + id);
        var homeDir = Path.Combine(_root, "home path with spaces " + id);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(homeDir);

        CreateMockPayload(sourceDir, "v1.0.0");

        // Fresh install with spaces
        var (exitCode1, output1, error1) = RunInstaller(sourceDir, dataDir, homeDir);
        Assert.True(exitCode1 == 0, $"Fresh install failed. Output: {output1}. Error: {error1}");
        Assert.Contains("Installed Filee at", output1);

        var installDir = Path.Combine(dataDir, "filee", "application");
        Assert.True(File.Exists(Path.Combine(installDir, "Filee")));
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());

        // Upgrade with spaces
        var v2Source = Path.Combine(_root, "source-v2 spaces " + id);
        CreateMockPayload(v2Source, "v2.0.0");

        var (exitCode2, output2, error2) = RunInstaller(v2Source, dataDir, homeDir, "--upgrade");
        Assert.True(exitCode2 == 0, $"Upgrade failed. Output: {output2}. Error: {error2}");
        Assert.Contains("Upgraded Filee at", output2);
        Assert.Equal("v2.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
    }

    [Fact]
    public void Upgrade_fails_if_running_filee_cannot_gracefully_shut_down()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Requires Linux.");

        var env = CreateEnvironment();
        CreateMockPayload(env.SourceDir, "v1.0.0");

        var (exitCode1, _, _) = RunInstaller(env.SourceDir, env.DataDir, env.HomeDir);
        Assert.Equal(0, exitCode1);

        var installDir = Path.Combine(env.DataDir, "filee", "application");
        var activeFilee = Path.Combine(installDir, "Filee");

        // Make the installed Filee binary fail if --quit is invoked
        File.WriteAllText(activeFilee, "#!/bin/sh\nif [ \"$1\" = \"--quit\" ]; then exit 42; fi\nexit 0\n");
        File.SetUnixFileMode(activeFilee, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Prepare v2 payload
        var v2Dir = Path.Combine(_root, "source-v2-" + Guid.NewGuid().ToString("N")[..8]);
        CreateMockPayload(v2Dir, "v2.0.0");

        // Upgrade calls Filee --quit, which fails with code 42
        var (failCode, failOut, failErr) = RunInstaller(v2Dir, env.DataDir, env.HomeDir, args: ["--upgrade"]);
        Assert.NotEqual(0, failCode);
        Assert.Contains("Could not gracefully shut down", failOut + failErr);
        Assert.Equal("v1.0.0", File.ReadAllText(Path.Combine(installDir, "version.txt")).Trim());
    }

    private (string SourceDir, string DataDir, string HomeDir) CreateEnvironment()
    {
        var id = Guid.NewGuid().ToString("N");
        var sourceDir = Path.Combine(_root, "source-" + id);
        var dataDir = Path.Combine(_root, "data-" + id);
        var homeDir = Path.Combine(_root, "home-" + id);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(homeDir);
        return (sourceDir, dataDir, homeDir);
    }

    private void CreateMockPayload(string dir, string version)
    {
        Directory.CreateDirectory(dir);
        var filee = Path.Combine(dir, "Filee");
        var cli = Path.Combine(dir, "filee-cli");
        var icon = Path.Combine(dir, "filee.png");
        var ver = Path.Combine(dir, "version.txt");
        var script = Path.Combine(dir, "install.sh");

        File.WriteAllText(filee, "#!/bin/sh\nexit 0\n");
        File.WriteAllText(cli, "#!/bin/sh\nexit 0\n");
        File.WriteAllText(icon, "dummy-png");
        File.WriteAllText(ver, version);
        File.Copy(_installScriptPath, script, overwrite: true);

        File.SetUnixFileMode(filee, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private (int ExitCode, string Output, string Error) RunInstaller(
        string sourceDir, string dataDir, string homeDir, params string[] args) =>
        RunInstaller(sourceDir, dataDir, homeDir, null, args);

    private (int ExitCode, string Output, string Error) RunInstaller(
        string sourceDir, string dataDir, string homeDir, IDictionary<string, string>? extraEnv, params string[] args)
    {
        var script = Path.Combine(sourceDir, "install.sh");
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = sourceDir,
        };
        psi.ArgumentList.Add(script);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment["XDG_DATA_HOME"] = dataDir;
        psi.Environment["HOME"] = homeDir;
        psi.Environment["PATH"] = $"{_shimBinDir}:{Environment.GetEnvironmentVariable("PATH") ?? "/bin:/usr/bin"}";

        if (extraEnv is not null)
        {
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        return (process.ExitCode, output, error);
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsLinux() && Directory.Exists(_root))
            {
                var psi = new ProcessStartInfo("/bin/chmod", ["-R", "u+rwx", _root])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi)?.WaitForExit(5000);
            }
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Ignore temporary cleanup errors
        }
    }
}
