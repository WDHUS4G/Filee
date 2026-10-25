using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("v2.0.0-beta.1", "1.9.0", true)]
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("nightly", "1.0.1", false)]
    [InlineData(null, "1.0.1", false)]
    public void Release_tags_compare_by_version(string? tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(tag, current));

    /// <summary>Filee works offline: a failed update check must neither throw nor report an update.</summary>
    public static TheoryData<string> OfflineFailures => ["no network", "timeout", "dropped while reading", "not json"];

    [Theory]
    [MemberData(nameof(OfflineFailures))]
    public async Task Update_check_without_internet_is_quiet(string failure)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork(failure));

        var found = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(found);
        Assert.False(updates.IsUpdateAvailable);
    }

    private sealed class FakeNetwork(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => failure switch
        {
            "no network" => throw new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound)),
            "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            "dropped while reading" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") }),
        };
    }

    /// <summary>A response body whose connection breaks after the headers.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ───────── In-app update: find, download and verify the installer ─────────

    private const string Base = "https://example.test/v9.9.9/";

    [Fact]
    public void Release_files_are_the_installer_and_the_checksums()
    {
        using var release = JsonDocument.Parse($$"""
            {"tag_name": "v9.9.9", "assets": [
              {"name": "Filee-9.9.9-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-win-Portable.zip", "size": 5, "browser_download_url": "{{Base}}Filee-9.9.9-win-Portable.zip"},
              {"name": "Filee-9.9.9-win-Setup.exe", "size": 1234, "browser_download_url": "{{Base}}Filee-9.9.9-win-Setup.exe"}
            ]}
            """);

        var (installer, checksums) = UpdateService.FindFiles(release.RootElement);

        Assert.Equal(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", 1234), installer);
        Assert.Equal("Filee-9.9.9-SHA256SUMS.txt", checksums?.Name);
    }

    [Fact]
    public void Release_files_picks_windows_installer_and_exact_checksums_even_when_linux_and_mac_are_present()
    {
        using var release = JsonDocument.Parse($$"""
            {"tag_name": "v9.9.9", "assets": [
              {"name": "Filee-9.9.9-linux-arm64-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-linux-arm64-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-linux-x64-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-linux-x64-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-osx-arm64-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-osx-arm64-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-osx-x64-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-osx-x64-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-win-Portable.zip", "size": 5000, "browser_download_url": "{{Base}}Filee-9.9.9-win-Portable.zip"},
              {"name": "Filee-9.9.9-win-Setup.exe", "size": 6000, "browser_download_url": "{{Base}}Filee-9.9.9-win-Setup.exe"}
            ]}
            """);

        var (installer, checksums) = UpdateService.FindFiles(release.RootElement);
        Assert.Equal("Filee-9.9.9-win-Setup.exe", installer?.Name);
        Assert.Equal("Filee-9.9.9-SHA256SUMS.txt", checksums?.Name);
    }

    [Fact]
    public void Release_without_files_or_with_plain_http_links_has_no_installer()
    {
        using var empty = JsonDocument.Parse("""{"tag_name": "v9.9.9"}""");
        using var http = JsonDocument.Parse("""
            {"assets": [{"name": "Filee-9.9.9-win-Setup.exe", "size": 1, "browser_download_url": "http://example.test/a.exe"}]}
            """);

        Assert.Equal((null, null), UpdateService.FindFiles(empty.RootElement));
        Assert.Null(UpdateService.FindFiles(http.RootElement).Installer);
    }

    [Fact]
    public void Checksum_lines_are_matched_by_file_name()
    {
        var hash = new string('A', 64);
        var sums = $"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\r\n{hash}  Filee-9.9.9-win-Setup.exe\r\n";

        Assert.Equal(hash.ToLowerInvariant(), UpdateService.ParseChecksum(sums, "Filee-9.9.9-win-Setup.exe"));
        Assert.Equal(hash.ToLowerInvariant(), UpdateService.ParseChecksum($"{hash} *Filee-9.9.9-win-Setup.exe", "Filee-9.9.9-win-Setup.exe"));
        Assert.Null(UpdateService.ParseChecksum(sums, "Filee-9.9.8-win-Setup.exe"));
        Assert.Null(UpdateService.ParseChecksum("tooshort  Filee-9.9.9-win-Setup.exe", "Filee-9.9.9-win-Setup.exe"));
    }

    [Fact]
    public async Task Installer_is_downloaded_verified_and_reused()
    {
        var installer = RandomNumberGenerator.GetBytes(300_000);
        var network = new FakeRelease(installer, Sha256(installer));
        var updates = Updates(network, installer.Length);
        var folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, "Filee-9.9.8-win-Setup.exe"), "an older download");
        var progress = new List<int>();

        var path = await updates.DownloadInstallerAsync(folder, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(folder, "Filee-9.9.9-win-Setup.exe"), path);
        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(folder)); // the older download and the .part file are gone
        Assert.Equal(100, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()), "progress only goes up");

        // Pressing Update again (e.g. after saying no to the administrator prompt) does not download it again.
        await updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, network.InstallerRequests);
    }

    [Fact]
    public async Task Installer_with_the_wrong_checksum_is_thrown_away()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        var updates = Updates(new FakeRelease(installer, new string('0', 64)), installer.Length);
        var folder = TempFolder();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Cut_off_download_fails()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        // GitHub said the file is bigger than what arrived.
        var updates = Updates(new FakeRelease(installer, Sha256(installer)), installer.Length + 500);

        await Assert.ThrowsAsync<IOException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checksum_file_without_the_installer_fails()
    {
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var updates = Updates(new FakeRelease(installer, Sha256(installer), sumsName: "Filee-9.9.8-win-Setup.exe"), installer.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_errors_are_reported_as_http_errors()
    {
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var updates = Updates(new FakeRelease(installer, Sha256(installer), installerStatus: HttpStatusCode.NotFound), installer.Length);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    private const string Publisher = "CN=Open Source Developer, Filee";

    /// <summary>An update service whose own program is signed by <paramref name="own"/> and the installer by <paramref name="installer"/>.</summary>
    private static UpdateService Signed(string? own, string? installer) =>
        new(NullLogger<UpdateService>.Instance, new FakeNetwork("no network"))
        {
            OwnProgram = "Filee.exe",
            SignerOf = (path, _) => path == "Filee.exe" ? own : installer,
        };

    [Fact]
    public void Installer_signed_by_the_same_publisher_is_kept()
    {
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");

        Signed(Publisher, Publisher).CheckPublisher(path);

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("CN=Someone Else")]
    [InlineData(null)] // not signed, or signed with a certificate Windows does not trust
    public void Installer_signed_by_anyone_else_is_thrown_away(string? signer)
    {
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");

        Assert.Throws<InvalidDataException>(() => Signed(Publisher, signer).CheckPublisher(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Failed_revocation_check_is_named_in_the_error()
    {
        // Offline or behind a proxy, the signature is fine but the certificate authority can't be asked: the
        // installer is still thrown away, and the log says why instead of blaming the publisher.
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork("no network"))
        {
            OwnProgram = "Filee.exe",
            SignerOf = (file, revocation) => file == "Filee.exe" || !revocation ? Publisher : null,
        };

        var error = Assert.Throws<InvalidDataException>(() => updates.CheckPublisher(path));

        Assert.Contains("revocation check failed", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Installer_is_checked_for_revocation_but_this_copy_is_not()
    {
        var checks = new List<(string Path, bool Revocation)>();
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork("no network"))
        {
            OwnProgram = "Filee.exe",
            SignerOf = (path, revocation) =>
            {
                checks.Add((path, revocation));
                return Publisher;
            },
        };

        updates.CheckPublisher("Setup.exe");

        Assert.Equal([("Filee.exe", false), ("Setup.exe", true)], checks);
    }

    [Fact]
    public void Unsigned_copy_keeps_to_the_checksum()
    {
        // Built from source: there is no publisher to compare with, so the installer is not refused for its signature.
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");

        Signed(null, null).CheckPublisher(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Unsigned_file_has_no_trusted_signer()
    {
        var path = Path.Combine(TempFolder(), "unsigned.exe");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(4_096));

        Assert.Null(Authenticode.TrustedSigner(path, checkRevocation: false));
    }

    [Fact]
    public void Signed_program_has_its_publisher_as_signer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is Windows only");
        // dotnet.exe carries Microsoft's embedded signature; it sits three folders above the shared runtime
        // (dotnet\shared\Microsoft.NETCore.App\<version>).
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.SkipUnless(File.Exists(dotnet), $"no {dotnet}");

        Assert.Contains("Microsoft", Authenticode.TrustedSigner(dotnet, checkRevocation: false));
    }

    [Fact]
    public void Installer_runs_without_the_wizard_and_without_restarting_windows()
    {
        Assert.Contains("/SILENT", UpdateService.InstallerArguments);
        Assert.Contains("/NORESTART", UpdateService.InstallerArguments);
    }

    private static UpdateService Updates(FakeRelease network, long installerSize)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network);
        updates.UseRelease(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", installerSize),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 100));
        return updates;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts later, which makes the order racy in tests).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Serves a release's checksum file and installer.</summary>
    private sealed class FakeRelease(byte[] installer, string hash, string sumsName = "Filee-9.9.9-win-Setup.exe",
        HttpStatusCode installerStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int InstallerRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\n{hash}  {sumsName}\n", Encoding.ASCII),
                });
            if (url.EndsWith("-win-Setup.exe", StringComparison.Ordinal))
            {
                InstallerRequests++;
                Assert.Contains("application/octet-stream", request.Headers.Accept.ToString());
                return Task.FromResult(new HttpResponseMessage(installerStatus) { Content = new ByteArrayContent(installer) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task A_failed_update_is_reported_instead_of_opening_a_page_right_away()
    {
        // Installed copy, but the release offers no installer to download: the app shows an alert first and opens
        // the website only after OK (WindowService.ShowUpdateFailedAsync).
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork("no network")) { InstallsItself = true };
        var reported = 0;
        updates.UpdateFailed += (_, _) => reported++;

        await updates.UpdateAsync();

        Assert.Equal(1, reported);
        Assert.Equal(UpdateStep.Failed, updates.Step);
    }

    [AvaloniaFact] // the check publishes its answer on the UI thread
    public async Task Update_installs_the_release_that_is_latest_now_not_the_one_found_earlier()
    {
        // An earlier check found 9.9.9; 9.9.10 came out before Update was pressed.
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new LatestRelease("v9.9.10")) { TargetPlatform = "win" };
        updates.UseRelease(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", 1),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 1));
        var announced = 0;
        updates.UpdateFound += (_, _) => announced++;

        Assert.True(await updates.RefreshAsync(announce: false));

        Assert.Equal("Filee-9.9.10-win-Setup.exe", updates.Installer!.Name);
        Assert.Equal("9.9.10", updates.LatestVersion);
        Assert.Equal(0, announced); // no second notice while updating
    }

    /// <summary>GitHub's "latest release" answer for <paramref name="tag"/>, with its installer and checksums.</summary>
    private sealed class LatestRelease(string tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = tag.TrimStart('v');
            var json = $$"""
                {"tag_name":"{{tag}}","assets":[
                  {"name":"Filee-{{version}}-win-Setup.exe","browser_download_url":"{{Base}}Filee-{{version}}-win-Setup.exe","size":1234},
                  {"name":"Filee-{{version}}-SHA256SUMS.txt","browser_download_url":"{{Base}}Filee-{{version}}-SHA256SUMS.txt","size":100}]}
                """;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void Download_page_is_the_latest_release()
    {
        Assert.EndsWith("/releases/latest", UpdateService.LatestReleaseUrl);
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/'), UpdateService.LatestReleaseUrl);
    }

    [Theory]
    [InlineData(Architecture.X64, "linux-x64")]
    [InlineData(Architecture.Arm64, "linux-arm64")]
    public void Architecture_maps_to_linux_rid(Architecture arch, string expectedRid) =>
        Assert.Equal(expectedRid, UpdateService.GetLinuxRid(arch));

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.S390x)]
    [InlineData(Architecture.Wasm)]
    public void Architecture_unsupported_throws(Architecture arch) =>
        Assert.Throws<PlatformNotSupportedException>(() => UpdateService.GetLinuxRid(arch));

    [Fact]
    public void IsLinuxInstalled_checks_marker_file()
    {
        var folder = TempFolder();
        Assert.False(UpdateService.IsLinuxInstalled(folder));

        var marker = Path.Combine(folder, ".filee-package");
        File.WriteAllText(marker, "com.filee.app\n");
        Assert.True(UpdateService.IsLinuxInstalled(folder));

        File.WriteAllText(marker, "org.other.app");
        Assert.False(UpdateService.IsLinuxInstalled(folder));
    }

    [Fact]
    public void FindFiles_selects_linux_assets_for_specific_rid()
    {
        using var release = JsonDocument.Parse($$"""
            {"tag_name": "v1.2.3", "assets": [
              {"name": "Filee-1.2.3-SHA256SUMS.txt", "size": 100, "browser_download_url": "{{Base}}Filee-1.2.3-SHA256SUMS.txt"},
              {"name": "Filee-1.2.3-win-Setup.exe", "size": 1200, "browser_download_url": "{{Base}}Filee-1.2.3-win-Setup.exe"},
              {"name": "Filee-1.2.3-linux-x64.tar.gz", "size": 2000, "browser_download_url": "{{Base}}Filee-1.2.3-linux-x64.tar.gz"},
              {"name": "Filee-1.2.3-linux-x64-SHA256SUMS.txt", "size": 90, "browser_download_url": "{{Base}}Filee-1.2.3-linux-x64-SHA256SUMS.txt"},
              {"name": "Filee-1.2.3-linux-arm64.tar.gz", "size": 1900, "browser_download_url": "{{Base}}Filee-1.2.3-linux-arm64.tar.gz"},
              {"name": "Filee-1.2.3-linux-arm64-SHA256SUMS.txt", "size": 92, "browser_download_url": "{{Base}}Filee-1.2.3-linux-arm64-SHA256SUMS.txt"}
            ]}
            """);

        var (installerX64, sumsX64) = UpdateService.FindFiles(release.RootElement, "linux-x64");
        Assert.Equal("Filee-1.2.3-linux-x64.tar.gz", installerX64?.Name);
        Assert.Equal("Filee-1.2.3-linux-x64-SHA256SUMS.txt", sumsX64?.Name);

        var (installerArm64, sumsArm64) = UpdateService.FindFiles(release.RootElement, "linux-arm64");
        Assert.Equal("Filee-1.2.3-linux-arm64.tar.gz", installerArm64?.Name);
        Assert.Equal("Filee-1.2.3-linux-arm64-SHA256SUMS.txt", sumsArm64?.Name);

        var (installerWin, sumsWin) = UpdateService.FindFiles(release.RootElement, "win");
        Assert.Equal("Filee-1.2.3-win-Setup.exe", installerWin?.Name);
        Assert.Equal("Filee-1.2.3-SHA256SUMS.txt", sumsWin?.Name);
    }

    [Fact]
    public async Task Checksum_file_for_different_artifact_is_rejected()
    {
        var installerData = RandomNumberGenerator.GetBytes(1000);
        var hash = Sha256(installerData);
        var folder = TempFolder();

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeLinuxRelease(
            installerData, hash, sumsArtifact: "Filee-1.2.3-linux-arm64.tar.gz"));
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-1.2.3-linux-x64.tar.gz", Base + "Filee-1.2.3-linux-x64.tar.gz", installerData.Length),
            new UpdateService.ReleaseFile("Filee-1.2.3-linux-x64-SHA256SUMS.txt", Base + "Filee-1.2.3-linux-x64-SHA256SUMS.txt", 100));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExtractLinuxArchive_valid_payload_extracts_and_sets_permissions()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "package.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/Filee", "#!/bin/sh\necho app\n");
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/install.sh", "#!/bin/sh\necho installer\n");
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/filee.png", "fake png data");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        var payloadDir = UpdateService.ExtractLinuxArchive(archiveFile, destFolder);

        Assert.Equal(Path.Combine(destFolder, "Filee-1.0.0-linux-x64"), payloadDir);
        var installSh = Path.Combine(payloadDir, "install.sh");
        var fileeExe = Path.Combine(payloadDir, "Filee");
        Assert.True(File.Exists(installSh));
        Assert.True(File.Exists(fileeExe));

        if (OperatingSystem.IsLinux())
        {
            var installMode = File.GetUnixFileMode(installSh);
            Assert.True((installMode & UnixFileMode.UserExecute) != 0, "install.sh must be executable");
            var fileeMode = File.GetUnixFileMode(fileeExe);
            Assert.True((fileeMode & UnixFileMode.UserExecute) != 0, "Filee must be executable");
        }
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_path_traversal()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "traversal.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "../evil.sh", "#!/bin/sh\n");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_absolute_path()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "absolute.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "/tmp/evil.sh", "#!/bin/sh\n");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_escaping_symlink()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "symlink.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddSymlinkEntry(writer, "Filee-1.0.0/escape_link", "/etc/passwd");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_missing_payload_files()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "missing.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-1.0.0/filee.png", "png data");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [AvaloniaFact]
    public async Task UpdateAsync_on_linux_downloads_extracts_and_delegates_to_detached_helper()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/filee.png", "png data");
        });
        var hash = Sha256(tarBytes);
        var network = new FakeLinuxRelease(tarBytes, hash, "Filee-9.9.9-linux-x64.tar.gz", "9.9.9");

        var mockInstallDir = TempFolder();

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            LinuxInstallDirectoryOverride = mockInstallDir,
        };
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64.tar.gz", Base + "Filee-9.9.9-linux-x64.tar.gz", tarBytes.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));

        var processes = new List<ProcessStartInfo>();
        var shutdownRequested = false;
        updates.RequestApplicationShutdown = () => shutdownRequested = true;
        updates.StartProcess = psi =>
        {
            processes.Add(psi);
            // No real process: `true` is missing on Windows without Git's tools on PATH.
            return null;
        };

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Installing, updates.Step);
        Assert.Single(processes);

        var helperPsi = processes[0];
        Assert.Equal("/bin/sh", helperPsi.FileName);
        Assert.Equal("-c", helperPsi.ArgumentList[0]);
        Assert.Contains("kill -0 \"$1\"", helperPsi.ArgumentList[1]);
        Assert.Contains("\"$2\" --upgrade >> \"$4\" 2>&1", helperPsi.ArgumentList[1]);
        Assert.Contains("exec \"$3\" --background --whats-new", helperPsi.ArgumentList[1]);
        Assert.Contains("exec \"$3\" --background", helperPsi.ArgumentList[1]);
        Assert.Contains("rm -rf \"$5\"", helperPsi.ArgumentList[1]);
        Assert.Equal(Environment.ProcessId.ToString(), helperPsi.ArgumentList[3]);
        Assert.EndsWith("install.sh", helperPsi.ArgumentList[4]);
        Assert.Equal(Path.Combine(mockInstallDir, "Filee"), helperPsi.ArgumentList[5]);
        Assert.EndsWith(".log", helperPsi.ArgumentList[6]);
        Assert.EndsWith(".update-failed", helperPsi.ArgumentList[8]);

        Assert.True(shutdownRequested, "Application shutdown must be requested before file swap");
    }

    [AvaloniaFact]
    public async Task UpdateAsync_on_linux_handles_helper_start_failure()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/filee.png", "png data");
        });
        var hash = Sha256(tarBytes);
        var network = new FakeLinuxRelease(tarBytes, hash, "Filee-9.9.9-linux-x64.tar.gz", "9.9.9");

        var mockInstallDir = TempFolder();

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            LinuxInstallDirectoryOverride = mockInstallDir,
        };
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64.tar.gz", Base + "Filee-9.9.9-linux-x64.tar.gz", tarBytes.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));

        var failedCount = 0;
        updates.UpdateFailed += (_, _) => failedCount++;

        var shutdownRequested = false;
        updates.RequestApplicationShutdown = () => shutdownRequested = true;

        updates.StartProcess = psi =>
            throw new Win32Exception("Cannot spawn helper process");

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.Equal(1, failedCount);
        Assert.NotNull(updates.LastError);
        Assert.Contains("Cannot spawn helper process", updates.LastError.Message);
        Assert.False(shutdownRequested, "Application shutdown must not be requested when helper fails to start");
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_symlink_component_traversal()
    {
        var outsideFolder = TempFolder();
        var sentinelFile = Path.Combine(outsideFolder, "sentinel.txt");
        File.WriteAllText(sentinelFile, "SENTINEL_UNTOUCHED");

        var stagingFolder = TempFolder();
        var archiveFile = Path.Combine(stagingFolder, "evil-traversal.tar.gz");
        var destFolder = Path.Combine(stagingFolder, "dest");

        var tarBytes = CreateTarGz(writer =>
        {
            // Adversarial entry 1: directory symlink pointing outside to outsideFolder
            AddSymlinkEntry(writer, "payload/escape", outsideFolder);
            // Adversarial entry 2: file inside escape pointing to sentinel.txt
            AddFileEntry(writer, "payload/escape/sentinel.txt", "MALICIOUS_OVERWRITE");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
        Assert.Equal("SENTINEL_UNTOUCHED", File.ReadAllText(sentinelFile));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_link_order_attack()
    {
        var stagingFolder = TempFolder();
        var archiveFile = Path.Combine(stagingFolder, "evil-link-order.tar.gz");
        var destFolder = Path.Combine(stagingFolder, "dest");

        var tarBytes = CreateTarGz(writer =>
        {
            // Entry 1: regular directory
            var dirEntry = new PaxTarEntry(TarEntryType.Directory, "payload/sub");
            writer.WriteEntry(dirEntry);
            // Entry 2: attempt to replace directory with a symlink
            AddSymlinkEntry(writer, "payload/sub", "other");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void CreateSecureWorkspace_creates_private_unshared_directory()
    {
        var workspace = UpdateService.CreateSecureWorkspace();
        try
        {
            Assert.True(Directory.Exists(workspace));
            var dirInfo = new DirectoryInfo(workspace);
            Assert.Null(dirInfo.LinkTarget);
            Assert.False(dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint));

            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(workspace);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
            }
        }
        finally
        {
            UpdateService.DeleteSecureWorkspace(workspace);
            Assert.False(Directory.Exists(workspace));
        }
    }

    [Fact]
    public void DeleteSecureWorkspace_refuses_to_follow_symlink()
    {
        var realDir = TempFolder();
        var targetFile = Path.Combine(realDir, "important.txt");
        File.WriteAllText(targetFile, "CRITICAL_DATA");

        var linkContainer = TempFolder();
        var symlinkPath = Path.Combine(linkContainer, "link-to-real");
        try
        {
            File.CreateSymbolicLink(symlinkPath, realDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine doesn't let the test make a symbolic link.");
        }

        UpdateService.DeleteSecureWorkspace(symlinkPath);

        Assert.True(Directory.Exists(realDir), "Real directory must not be deleted when DeleteSecureWorkspace is called on a symlink");
        Assert.True(File.Exists(targetFile), "File inside real directory must survive");
    }

    [Fact]
    public async Task Concurrent_secure_workspaces_are_isolated()
    {
        var isolatedDir = TempFolder();
        try
        {
            var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() => UpdateService.CreateSecureWorkspace(isolatedDir))).ToArray();
            var workspaces = await Task.WhenAll(tasks);

            Assert.Equal(5, workspaces.Distinct().Count());
            Assert.All(workspaces, w =>
            {
                Assert.True(Directory.Exists(w));
                Assert.StartsWith(isolatedDir, w);
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(isolatedDir))
                    Directory.Delete(isolatedDir, recursive: true);
            }
            catch
            {
                // Best effort test cleanup
            }
        }
    }

    [Fact]
    public void Tar_extraction_masks_mode_to_rwx_bits()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            var setuidMode = UnixFileMode.SetUser | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            var setgidMode = UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n", setuidMode);
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n", setgidMode);
        });
        var archiveFile = Path.Combine(TempFolder(), "payload.tar.gz");
        File.WriteAllBytes(archiveFile, tarBytes);

        var destFolder = TempFolder();
        var payloadDir = UpdateService.ExtractLinuxArchive(archiveFile, destFolder);

        if (OperatingSystem.IsLinux())
        {
            var fileeMode = File.GetUnixFileMode(Path.Combine(payloadDir, "Filee"));
            var installMode = File.GetUnixFileMode(Path.Combine(payloadDir, "install.sh"));

            Assert.False(fileeMode.HasFlag(UnixFileMode.SetUser), "setuid bit must be stripped");
            Assert.False(fileeMode.HasFlag(UnixFileMode.SetGroup), "setgid bit must be stripped");
            Assert.False(fileeMode.HasFlag(UnixFileMode.StickyBit), "sticky bit must be stripped");

            Assert.False(installMode.HasFlag(UnixFileMode.SetUser), "setuid bit must be stripped");
            Assert.False(installMode.HasFlag(UnixFileMode.SetGroup), "setgid bit must be stripped");
            Assert.False(installMode.HasFlag(UnixFileMode.StickyBit), "sticky bit must be stripped");
        }
    }

    [Fact]
    public void CheckPendingFailure_returns_false_when_marker_absent()
    {
        var dir = TempFolder();
        var updates = new UpdateService(NullLogger<UpdateService>.Instance)
        {
            FailureMarkerPathOverride = Path.Combine(dir, ".update-failed"),
        };

        var result = updates.CheckPendingFailure(dir);
        Assert.False(result);
        Assert.Equal(UpdateStep.Idle, updates.Step);
        Assert.Null(updates.LastFailureLogPath);
    }

    [Fact]
    public void CheckPendingFailure_consumes_marker_and_records_failure()
    {
        var dir = TempFolder();
        var marker = Path.Combine(dir, ".update-failed");
        var logPath = Path.Combine(dir, "logs", "filee-test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "some error log");
        File.WriteAllText(marker, logPath + "\n");

        var updates = new UpdateService(NullLogger<UpdateService>.Instance)
        {
            FailureMarkerPathOverride = marker,
        };

        var result = updates.CheckPendingFailure(dir);
        Assert.True(result);
        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.Equal(logPath, updates.LastFailureLogPath);
        Assert.False(File.Exists(marker), "Marker file must be consumed (deleted)");
    }

    [Fact]
    public void CheckPendingFailure_handles_malformed_marker_safely()
    {
        var dir = TempFolder();
        var marker = Path.Combine(dir, ".update-failed");
        File.WriteAllText(marker, "not-a-rooted-path\0evil");

        var updates = new UpdateService(NullLogger<UpdateService>.Instance)
        {
            FailureMarkerPathOverride = marker,
        };

        var result = updates.CheckPendingFailure(dir);
        Assert.True(result);
        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.Null(updates.LastFailureLogPath);
        Assert.False(File.Exists(marker), "Marker file must still be consumed");
    }

    [Fact]
    public void DeleteStaleWorkspaces_preserves_active_pid_and_deletes_stale_dead_pid()
    {
        var root = TempFolder();
        var token1 = "0123456789abcdef0123456789abcdef";
        var token2 = "abcdef0123456789abcdef0123456789";
        var token3 = "11223344556677889900aabbccddeeff";
        var activeDir = Path.Combine(root, $".update-{Environment.ProcessId}-{token1}");
        var deadPid = 999999;
        while (UpdateService.IsProcessAlive(deadPid))
            deadPid++;

        var deadStaleDir = Path.Combine(root, $".update-{deadPid}-{token2}");
        var deadFreshDir = Path.Combine(root, $".update-{deadPid}-{token3}");

        Directory.CreateDirectory(activeDir);
        Directory.CreateDirectory(deadStaleDir);
        Directory.CreateDirectory(deadFreshDir);

        Directory.SetLastWriteTimeUtc(deadStaleDir, DateTime.UtcNow.AddDays(-2));

        UpdateService.DeleteStaleWorkspaces(root, TimeSpan.FromDays(1));

        Assert.True(Directory.Exists(activeDir), "Active PID workspace must not be deleted even if stale");
        Assert.False(Directory.Exists(deadStaleDir), "Stale dead PID workspace must be deleted");
        Assert.True(Directory.Exists(deadFreshDir), "Fresh dead PID workspace must be kept");
    }

    [Fact]
    public void DeleteStaleWorkspaces_ignores_foreign_and_malformed_directories()
    {
        var root = TempFolder();
        var token32 = "0123456789abcdef0123456789abcdef";

        // Foreign directories and malformed names
        var foreign1 = Path.Combine(root, ".update-something");
        var foreign2 = Path.Combine(root, ".update-cache");
        var malformedZeroPid = Path.Combine(root, $".update-0-{token32}");
        var malformedNegativePid = Path.Combine(root, $".update--123-{token32}");
        var malformedNonDecimalPid = Path.Combine(root, $".update-abc-{token32}");
        var malformedShortToken = Path.Combine(root, ".update-12345-0123456789abcdef");
        var malformedLongToken = Path.Combine(root, $".update-12345-{token32}extra");
        var malformedNonHexToken = Path.Combine(root, ".update-12345-0123456789abcdef0123456789abcdeg");
        var malformedExtraSegment = Path.Combine(root, $".update-12345-{token32}-extra");

        var allFolders = new[]
        {
            foreign1, foreign2, malformedZeroPid, malformedNegativePid,
            malformedNonDecimalPid, malformedShortToken, malformedLongToken,
            malformedNonHexToken, malformedExtraSegment
        };

        foreach (var dir in allFolders)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "content.txt"), "preserve me");
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-2));
        }

        UpdateService.DeleteStaleWorkspaces(root, TimeSpan.FromDays(1));

        foreach (var dir in allFolders)
        {
            Assert.True(Directory.Exists(dir), $"Directory {Path.GetFileName(dir)} must remain untouched");
            Assert.True(File.Exists(Path.Combine(dir, "content.txt")), $"File inside {Path.GetFileName(dir)} must survive");
        }
    }

    [Theory]
    [InlineData(".update-123-0123456789abcdef0123456789abcdef", true, 123)]
    [InlineData(".update-1-0123456789ABCDEF0123456789ABCDEF", true, 1)]
    [InlineData(".update-999999-abcdef0123456789abcdef0123456789", true, 999999)]
    [InlineData(".update-something", false, 0)]
    [InlineData(".update-cache", false, 0)]
    [InlineData(".update-", false, 0)]
    [InlineData(".update-0-0123456789abcdef0123456789abcdef", false, 0)]
    [InlineData(".update--123-0123456789abcdef0123456789abcdef", false, 0)]
    [InlineData(".update-+123-0123456789abcdef0123456789abcdef", false, 0)]
    [InlineData(".update-abc-0123456789abcdef0123456789abcdef", false, 0)]
    [InlineData(".update-123-short", false, 0)]
    [InlineData(".update-123-0123456789abcdef0123456789abcdefextra", false, 0)]
    [InlineData(".update-123-0123456789abcdef0123456789abcdeg", false, 0)]
    [InlineData(".update-123-0123456789abcdef-0123456789abcdef", false, 0)]
    [InlineData("other-123-0123456789abcdef0123456789abcdef", false, 0)]
    public void TryParseWorkspaceName_validates_pattern(string name, bool expectedResult, int expectedPid)
    {
        var result = UpdateService.TryParseWorkspaceName(name, out var pid);
        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPid, pid);
    }

    [Fact]
    public void Helper_script_restarts_filee_on_failure_and_writes_marker()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempDir = TempFolder();
        var workspace = Path.Combine(tempDir, "workspace");
        Directory.CreateDirectory(workspace);

        var installLog = Path.Combine(tempDir, "install.log");
        var markerFile = Path.Combine(tempDir, ".update-failed");

        var mockInstallSh = Path.Combine(tempDir, "install.sh");
        File.WriteAllText(mockInstallSh, "#!/bin/sh\necho 'Rolling back...' >&2\nexit 2\n");
        File.SetUnixFileMode(mockInstallSh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var argsOut = Path.Combine(tempDir, "filee-args.txt");
        var mockFilee = Path.Combine(tempDir, "Filee");
        File.WriteAllText(mockFilee, $"#!/bin/sh\necho \"$@\" > '{argsOut}'\nexit 0\n");
        File.SetUnixFileMode(mockFilee, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var deadPid = 999999;
        while (UpdateService.IsProcessAlive(deadPid))
            deadPid++;

        var script =
            "for _ in $(seq 1 600); do if ! kill -0 \"$1\" 2>/dev/null; then break; fi; sleep 0.1; done; " +
            "if kill -0 \"$1\" 2>/dev/null; then echo \"Update aborted: Filee (PID $1) did not exit within timeout\" >> \"$4\"; rm -rf \"$5\" 2>/dev/null; exit 1; fi; " +
            "\"$2\" --upgrade >> \"$4\" 2>&1; " +
            "status=$?; " +
            "rm -rf \"$5\" 2>/dev/null; " +
            "if [ $status -eq 0 ]; then " +
            "if [ -x \"$3\" ]; then exec \"$3\" --background --whats-new; else echo \"Update succeeded but '$3' is not executable\" >> \"$4\"; exit 1; fi; " +
            "else " +
            "echo \"Update failed with status $status\" >> \"$4\"; " +
            "echo \"$4\" > \"$6.tmp\" 2>/dev/null && mv -f \"$6.tmp\" \"$6\" 2>/dev/null || true; " +
            "if [ -x \"$3\" ]; then exec \"$3\" --background; else echo \"Cannot restart Filee: '$3' is not executable after rollback\" >> \"$4\"; exit $status; fi; " +
            "fi";

        using var proc = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", script, "_", deadPid.ToString(), mockInstallSh, mockFilee, installLog, workspace, markerFile },
            UseShellExecute = false,
        });

        Assert.NotNull(proc);
        Assert.True(proc.WaitForExit(5000));
        Assert.Equal(0, proc.ExitCode);

        Assert.False(Directory.Exists(workspace), "Workspace directory must be cleaned up on failure");
        Assert.True(File.Exists(markerFile), "Failure marker file must be created");
        Assert.Equal(installLog, File.ReadAllText(markerFile).Trim());

        Assert.True(File.Exists(argsOut), "Restored Filee executable must have been launched");
        var args = File.ReadAllText(argsOut).Trim();
        Assert.Equal("--background", args);
        Assert.DoesNotContain("--whats-new", args);
    }

    [Fact]
    public void Helper_script_cleans_up_workspace_on_timeout()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempDir = TempFolder();
        var workspace = Path.Combine(tempDir, "workspace");
        Directory.CreateDirectory(workspace);

        var installLog = Path.Combine(tempDir, "install.log");
        var markerFile = Path.Combine(tempDir, ".update-failed");
        var mockInstallSh = Path.Combine(tempDir, "install.sh");
        File.WriteAllText(mockInstallSh, "#!/bin/sh\nexit 0\n");
        var mockFilee = Path.Combine(tempDir, "Filee");
        File.WriteAllText(mockFilee, "#!/bin/sh\nexit 0\n");

        var timeoutScript =
            "for _ in $(seq 1 2); do if ! kill -0 \"$1\" 2>/dev/null; then break; fi; sleep 0.01; done; " +
            "if kill -0 \"$1\" 2>/dev/null; then echo \"Update aborted: Filee (PID $1) did not exit within timeout\" >> \"$4\"; rm -rf \"$5\" 2>/dev/null; exit 1; fi; " +
            "\"$2\" --upgrade >> \"$4\" 2>&1; " +
            "status=$?; " +
            "rm -rf \"$5\" 2>/dev/null; " +
            "if [ $status -eq 0 ]; then " +
            "if [ -x \"$3\" ]; then exec \"$3\" --background --whats-new; else echo \"Update succeeded but '$3' is not executable\" >> \"$4\"; exit 1; fi; " +
            "else " +
            "echo \"Update failed with status $status\" >> \"$4\"; " +
            "echo \"$4\" > \"$6.tmp\" 2>/dev/null && mv -f \"$6.tmp\" \"$6\" 2>/dev/null || true; " +
            "if [ -x \"$3\" ]; then exec \"$3\" --background; else echo \"Cannot restart Filee: '$3' is not executable after rollback\" >> \"$4\"; exit $status; fi; " +
            "fi";

        using var proc = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", timeoutScript, "_", Environment.ProcessId.ToString(), mockInstallSh, mockFilee, installLog, workspace, markerFile },
            UseShellExecute = false,
        });

        Assert.NotNull(proc);
        Assert.True(proc.WaitForExit(5000));
        Assert.Equal(1, proc.ExitCode);

        Assert.False(Directory.Exists(workspace), "Workspace must be cleaned up on timeout");
        Assert.Contains("Update aborted: Filee", File.ReadAllText(installLog));
    }

    private static byte[] CreateTarGz(Action<TarWriter> populate)
    {
        using var mem = new MemoryStream();
        using (var gzip = new GZipStream(mem, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            populate(writer);
        }
        return mem.ToArray();
    }

    private static void AddFileEntry(TarWriter writer, string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = mode,
        };
        writer.WriteEntry(entry);
    }

    private static void AddSymlinkEntry(TarWriter writer, string name, string target)
    {
        var entry = new PaxTarEntry(TarEntryType.SymbolicLink, name)
        {
            LinkName = target,
        };
        writer.WriteEntry(entry);
    }

    private sealed class FakeLinuxRelease(byte[] archive, string hash, string sumsArtifact = "Filee-9.9.9-linux-x64.tar.gz", string version = "9.9.9") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var json = $$"""
                    {"tag_name":"v{{version}}","assets":[
                      {"name":"Filee-{{version}}-linux-x64.tar.gz","browser_download_url":"{{Base}}Filee-{{version}}-linux-x64.tar.gz","size":{{archive.Length}}},
                      {"name":"Filee-{{version}}-linux-x64-SHA256SUMS.txt","browser_download_url":"{{Base}}Filee-{{version}}-linux-x64-SHA256SUMS.txt","size":100}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
            if (url.Contains("/releases?per_page=", StringComparison.Ordinal) || url.EndsWith("/releases", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash}  {sumsArtifact}\n", Encoding.ASCII),
                });
            }
            if (url.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                Assert.Contains("application/octet-stream", request.Headers.Accept.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [AvaloniaFact]
    public void UpdateProgressWindow_tracks_download_percentage_and_status()
    {
        TestServices.EnsureInitialized();
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork("no network"));
        updates.LatestVersion = "2.1.0";
        updates.Step = UpdateStep.Downloading;
        updates.DownloadPercent = 45;

        var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
        var window = new Views.UpdateProgressWindow(updates, loc);

        Assert.Equal(45, window.ProgressBar.Value);
        Assert.Equal("45%", window.PercentText.Text);
        Assert.DoesNotContain("45%", window.StatusText.Text);
        Assert.StartsWith(loc["update.downloading"].Split('…')[0], window.StatusText.Text);

        updates.DownloadPercent = 90;
        Assert.Equal(90, window.ProgressBar.Value);
        Assert.Equal("90%", window.PercentText.Text);

        updates.Step = UpdateStep.Installing;
        Assert.True(window.ProgressBar.IsIndeterminate);

        updates.Step = UpdateStep.Cancelled;
        Assert.False(window.ProgressBar.IsVisible);
    }
}
