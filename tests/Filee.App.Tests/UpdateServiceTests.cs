using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
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
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new LatestRelease("v9.9.10"));
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
}
