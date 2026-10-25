// Update check against GitHub Releases. When a newer version is out the app says so (sidebar, notice, tray). In an
// installed copy "Update" downloads the new installer, checks it against the release's SHA256SUMS file and that the
// publisher who signed this copy signed it too (Authenticode), and runs it;
// Setup closes Filee, updates it in place (settings are kept) and starts it again. A portable copy, a copy run from
// source, or a failed download opens a download page instead. The check works in every build.

using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Settings;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

/// <summary>Where an in-app update stands.</summary>
public enum UpdateStep
{
    Idle,
    Downloading,
    /// <summary>The installer is running (it shows its own progress window and closes Filee).</summary>
    Installing,
    /// <summary>The update did not work; the download page was opened instead.</summary>
    Failed,
    /// <summary>The user said no to the administrator prompt, or closed Setup.</summary>
    Cancelled,
}

public sealed partial class UpdateService : ObservableObject
{
    /// <summary>How often a running Filee (it lives in the tray for days) looks for a new version.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>A download that receives nothing for this long is given up.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Win32 ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Setup without the wizard: only its progress window shows. Updates keep the earlier choices anyway, and Setup
    /// starts Filee again when it is done.
    /// </summary>
    internal const string InstallerArguments = "/SILENT /NORESTART";

    private readonly ILogger<UpdateService> _log;
    private readonly HttpClient _http;
    private DispatcherTimer? _timer;
    private ReleaseFile? _installer;
    private ReleaseFile? _checksums;

    public UpdateService(ILogger<UpdateService> log) : this(log, new HttpClientHandler())
    {
    }

    /// <param name="handler">Network access; tests pass a handler that simulates being offline.</param>
    internal UpdateService(ILogger<UpdateService> log, HttpMessageHandler handler)
    {
        _log = log;
        // Downloads use their own stall timeout (StallTimeout); this one covers the API call and the response headers.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Filee", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>GitHub repository from Directory.Build.props (FileeRepositoryUrl).</summary>
    public static string RepositoryUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "https://github.com/KnifeLemon/Filee";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>GitHub's "new issue" page (bug report or feature request template).</summary>
    public static string NewIssueUrl => RepositoryUrl.TrimEnd('/') + "/issues/new/choose";

    /// <summary>Release page with every file, including the portable zip.</summary>
    public static string LatestReleaseUrl => RepositoryUrl.TrimEnd('/') + "/releases/latest";

    /// <summary>The website: one big download button, friendlier than the GitHub release page.</summary>
    public const string WebsiteUrl = "https://filee.sh/";

    /// <summary>
    /// True when this copy was installed by Setup on Windows (uninstall.exe; the Inno Setup installer of 1.x put unins000.exe there)
    /// or by install.sh on Linux (.filee-package marker containing com.filee.app).
    /// Only then can the new installer update it in place; the portable zip and a copy run from source have no uninstaller.
    /// </summary>
    public static bool IsInstalled { get; } =
        OperatingSystem.IsWindows() && IsInstalledIn(AppContext.BaseDirectory)
        || OperatingSystem.IsLinux() && IsLinuxInstalled(AppContext.BaseDirectory);

    internal static bool IsInstalledIn(string folder) =>
        File.Exists(Path.Combine(folder, "uninstall.exe")) || File.Exists(Path.Combine(folder, "unins000.exe"));

    internal static bool IsLinuxInstalled(string baseDirectory)
    {
        var marker = Path.Combine(baseDirectory, ".filee-package");
        try
        {
            return File.Exists(marker) && File.ReadAllText(marker).Trim() == "com.filee.app";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Returns the RID for the current Linux process architecture ("linux-x64" or "linux-arm64").</summary>
    internal static string GetLinuxRid() => GetLinuxRid(RuntimeInformation.ProcessArchitecture);

    /// <summary>Maps <paramref name="architecture"/> to supported Linux RIDs or throws <see cref="PlatformNotSupportedException"/>.</summary>
    internal static string GetLinuxRid(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            var arch => throw new PlatformNotSupportedException($"Unsupported Linux architecture: {arch}"),
        };

    /// <summary>Target platform for release assets ("win", "linux-x64", "linux-arm64"). Tests can override this.</summary>
    internal string TargetPlatform { get; set; } = DefaultTargetPlatform();

    private static string DefaultTargetPlatform()
    {
        if (OperatingSystem.IsWindows())
            return "win";
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return GetLinuxRid();
            }
            catch (PlatformNotSupportedException)
            {
                return "unsupported";
            }
        }
        return "unsupported";
    }

    /// <summary>Starts an external process. Tests can override this to intercept installer execution.</summary>
    internal Func<ProcessStartInfo, Process?> StartProcess { get; set; } = psi => Process.Start(psi);

    /// <summary>Invoked after a successful in-app update to gracefully exit the running application.</summary>
    internal Action? RequestApplicationShutdown { get; set; }

    /// <summary>Allows tests to redirect Linux installation target directory to an isolated temporary folder.</summary>
    internal string? LinuxInstallDirectoryOverride { get; set; }

    /// <summary>Allows tests to redirect update failure marker path to an isolated temporary location.</summary>
    internal string? FailureMarkerPathOverride { get; set; }

    /// <summary>The log file path recorded in the update failure marker from the previous run, if any.</summary>
    internal string? LastFailureLogPath { get; private set; }

    /// <summary>Whether <see cref="UpdateAsync"/> installs by itself (<see cref="IsInstalled"/>; tests set it).</summary>
    internal bool InstallsItself { get; init; } = IsInstalled;

    /// <summary>
    /// Who signed a program, if Windows trusts the signature (<see cref="Authenticode.TrustedSigner"/>; the flag also
    /// checks revocation). Tests replace it.
    /// </summary>
    internal Func<string, bool, string?> SignerOf { get; init; } = Authenticode.TrustedSigner;

    /// <summary>This copy of Filee, whose publisher must have signed the installer too (tests set it).</summary>
    internal string? OwnProgram { get; init; } = Environment.ProcessPath;

    /// <summary>The newer version found by the last check (e.g. "1.0.2"), or null when up to date / unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private string? _latestVersion;

    public bool IsUpdateAvailable => LatestVersion is not null;

    /// <summary>What the versions between this one and <see cref="LatestVersion"/> change, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level), nameof(IsRequired), nameof(IsOptional))]
    private IReadOnlyList<ReleaseNote> _notes = [];

    /// <summary>How much the update matters: the highest level of the versions it brings.</summary>
    public UpdateLevel Level => ReleaseNotes.Highest(Notes);

    public bool IsRequired => Level == UpdateLevel.Required;
    public bool IsOptional => Level == UpdateLevel.Optional;

    /// <summary>Progress of an in-app update.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private UpdateStep _step;

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty] private int _downloadPercent;

    public bool IsBusy => Step is UpdateStep.Downloading or UpdateStep.Installing;

    /// <summary>Raised on the UI thread when a check finds a newer version than before.</summary>
    public event EventHandler<string>? UpdateFound;

    /// <summary>
    /// True for published builds (the installer and the portable zip are published with <c>-p:FileeRelease=true</c>),
    /// false when running from source. Keeps development builds out of the registry.
    /// </summary>
    public static bool IsReleaseBuild =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "FileeRelease" && a.Value == "true");

    /// <summary>Checks the latest release now. Returns the newer version, or null when up to date or offline.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(announce: true, cancellationToken);
        return LatestVersion;
    }

    /// <summary>
    /// Asks GitHub for the latest release and keeps its files for <see cref="UpdateAsync"/>. With
    /// <paramref name="announce"/>, a newer version than before raises <see cref="UpdateFound"/>. False when the
    /// answer didn't come (offline, an error): what the last check found stays.
    /// </summary>
    internal async Task<bool> RefreshAsync(bool announce, CancellationToken cancellationToken = default)
    {
        try
        {
            var api = RepositoryUrl.TrimEnd('/').Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.OrdinalIgnoreCase)
                      + "/releases/latest";
            using var response = await _http.GetAsync(api, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogInformation("Update check: {Status}", response.StatusCode); // e.g. 404 before the first release
                return false;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var value) ? value.GetString() : null;
            var newer = IsNewer(tag, CurrentVersion) ? Normalize(tag!) : null;
            (_installer, _checksums) = TargetPlatform != "unsupported"
                ? FindFiles(json.RootElement, TargetPlatform)
                : (null, null);
            IReadOnlyList<ReleaseNote> notes = [];
            if (newer is not null)
            {
                var body = json.RootElement.TryGetProperty("body", out var text) ? text.GetString() : null;
                notes = await NotesAsync(api[..^"/latest".Length], newer, cancellationToken)
                        ?? ReleaseNotes.Between([(tag!, body)], CurrentVersion, newer);
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Notes = notes; // before Publish: UpdateFound decides by the level
                Publish(newer, announce);
            });
            return true;
        }
        // Offline, DNS failure, timeout, a connection dropped while reading, or an unexpected answer: Filee works
        // offline, so a failed check is only logged and the next one is tried later.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException
                                       or InvalidOperationException)
        {
            _log.LogInformation("Update check failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The notes of every release from this version up to <paramref name="latest"/> (someone a few versions behind sees
    /// all of them), or null when the list doesn't come.
    /// </summary>
    private async Task<IReadOnlyList<ReleaseNote>?> NotesAsync(string releasesApi, string latest, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(releasesApi + "?per_page=30", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var releases = json.RootElement.EnumerateArray()
                .Where(r => !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                            && !(r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True))
                .Select(r => (Tag: r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                              Body: r.TryGetProperty("body", out var b) ? b.GetString() : null))
                .ToList();
            return ReleaseNotes.Between(releases, CurrentVersion, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException
                                       or InvalidOperationException)
        {
            _log.LogInformation("Release notes: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Checks shortly after start and then every few hours while <paramref name="enabled"/> says so.</summary>
    public void StartPeriodicChecks(Func<bool> enabled, TimeSpan firstDelay)
    {
        if (_timer is not null)
            return;
        _timer = new DispatcherTimer { Interval = firstDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (enabled())
                await CheckAsync();
        };
        _timer.Start();
    }

    /// <summary>
    /// What every "Update" button does. An installed copy downloads, verifies and runs the new installer; anything
    /// else (portable, run from source, the release has no installer) opens a download page. Safe to call twice.
    /// </summary>
    public async Task UpdateAsync()
    {
        if (IsBusy)
            return;
        if (!InstallsItself)
        {
            OpenDownloadPage();
            return;
        }
        // The release found by the last check (up to hours ago) may have a newer one after it: ask again, so Update
        // installs the latest release rather than the one announced earlier. Offline, the earlier answer is used.
        Step = UpdateStep.Downloading;
        DownloadPercent = 0;
        if (await RefreshAsync(announce: false) && !IsUpdateAvailable)
        {
            _log.LogInformation("Update: already on the latest release");
            Step = UpdateStep.Idle;
            return;
        }
        if (_installer is null || _checksums is null)
        {
            Fail("release", new InvalidDataException("The latest release has no installer or no checksum file"));
            return;
        }

        if (TargetPlatform == "win")
        {
            var folder = Path.Combine(Path.GetTempPath(), "Filee-update");
            string installer;
            try
            {
                var progress = new Progress<int>(percent => DownloadPercent = percent);
                installer = await DownloadInstallerAsync(folder, progress);
                // WinVerifyTrust hashes the whole installer and may ask the certificate authority about revocation.
                await Task.Run(() => CheckPublisher(installer));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or InvalidDataException or UnauthorizedAccessException)
            {
                Fail("download", ex);
                return;
            }

            Process? setup;
            try
            {
                setup = StartProcess(new ProcessStartInfo(installer, InstallerArguments) { UseShellExecute = true });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                _log.LogInformation("Update cancelled at the administrator prompt");
                Step = UpdateStep.Cancelled;
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Fail("start", ex);
                return;
            }

            _log.LogInformation("Started the installer for {Version}", LatestVersion);
            Step = UpdateStep.Installing;
            if (setup is null)
                return;

            using (setup)
                await setup.WaitForExitAsync();
            _log.LogInformation("Installer ended with exit code {Code}", setup.ExitCode);
            Step = setup.ExitCode == 0 ? UpdateStep.Idle : UpdateStep.Cancelled;
            return;
        }

        if (TargetPlatform.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
        {
            string? workspace = null;
            string installer;
            try
            {
                workspace = CreateSecureWorkspace();
                var progress = new Progress<int>(percent => DownloadPercent = percent);
                installer = await DownloadInstallerAsync(workspace, progress);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or InvalidDataException or UnauthorizedAccessException)
            {
                DeleteSecureWorkspace(workspace);
                Fail("download", ex);
                return;
            }

            var stagingDir = Path.Combine(workspace, "payload");
            string payloadDir;
            try
            {
                Directory.CreateDirectory(stagingDir);
                payloadDir = ExtractLinuxArchive(installer, stagingDir);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                DeleteSecureWorkspace(workspace);
                Fail("extract", ex);
                return;
            }

            var scriptPath = Path.Combine(payloadDir, "install.sh");
            var targetInstallDir = LinuxInstallDirectoryOverride ?? GetLinuxInstallDirectory();
            var newExe = Path.Combine(targetInstallDir, "Filee");
            var currentPid = Environment.ProcessId;
            var logDir = Path.Combine(UserDataStore.DefaultDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"filee-{DateTime.Now:yyyyMMdd}.log");
            var markerFile = FailureMarkerPathOverride ?? Path.Combine(UserDataStore.DefaultDirectory, ".update-failed");

            Process? helper;
            try
            {
                var startInfo = new ProcessStartInfo("/bin/sh")
                {
                    WorkingDirectory = payloadDir,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(
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
                    "fi"
                );
                startInfo.ArgumentList.Add("_");
                startInfo.ArgumentList.Add(currentPid.ToString());
                startInfo.ArgumentList.Add(scriptPath);
                startInfo.ArgumentList.Add(newExe);
                startInfo.ArgumentList.Add(logFile);
                startInfo.ArgumentList.Add(workspace);
                startInfo.ArgumentList.Add(markerFile);

                helper = StartProcess(startInfo);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                DeleteSecureWorkspace(workspace);
                Fail("start", ex);
                return;
            }

            _log.LogInformation("Started update helper for {Version}, exiting current process", LatestVersion);
            Step = UpdateStep.Installing;
            RequestApplicationShutdown?.Invoke();
            return;
        }

        throw new PlatformNotSupportedException("In-app update is only supported on Windows and Linux");
    }

    /// <summary>
    /// Downloads the installer of the latest release into <paramref name="folder"/> and checks its SHA-256 against
    /// the release's checksum file. Returns the verified file; throws <see cref="InvalidDataException"/> when it does
    /// not match.
    /// </summary>
    internal async Task<string> DownloadInstallerAsync(string folder, IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        var installer = _installer ?? throw new InvalidOperationException("No installer in the latest release");
        var checksums = _checksums ?? throw new InvalidOperationException("No checksum file in the latest release");

        var sums = await GetTextAsync(checksums.Url, cancellationToken);
        var expected = ParseChecksum(sums, installer.Name)
                       ?? throw new InvalidDataException($"{checksums.Name} has no line for {installer.Name}");

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, installer.Name);
        // Downloaded already (Update pressed again after a declined prompt): reuse it if it is still intact.
        if (File.Exists(target) && await Sha256Async(target, cancellationToken) == expected)
        {
            progress?.Report(100);
            return target;
        }
        foreach (var old in Directory.EnumerateFiles(folder))
            TryDelete(old);

        var partial = target + ".part";
        TryDelete(partial);
        await DownloadAsync(installer, partial, progress, cancellationToken);
        var actual = await Sha256Async(partial, cancellationToken);
        if (actual != expected)
        {
            TryDelete(partial);
            throw new InvalidDataException($"{installer.Name}: SHA-256 {actual} does not match {expected}");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> (and deletes the installer) unless the publisher that signed this copy
    /// of Filee signed <paramref name="installer"/> as well. The checksum file sits on the same release as the
    /// installer, so it can't tell a replaced installer from the real one; the signature can. A copy that is not
    /// signed itself (built from source) has no publisher to compare with and keeps to the checksum.
    /// </summary>
    internal void CheckPublisher(string installer)
    {
        var publisher = OwnProgram is null ? null : SignerOf(OwnProgram, false);
        if (publisher is null)
        {
            _log.LogInformation("This copy of Filee is not signed: the installer is checked by its SHA-256 only");
            return;
        }
        var signer = SignerOf(installer, true);
        if (signer == publisher)
            return;
        // A signature that holds without the revocation check failed only that check: the certificate authority
        // could not be reached (offline, a proxy or a firewall) or the certificate was revoked.
        var reason = signer is null && SignerOf(installer, false) == publisher
            ? $"is signed by {publisher}, but the revocation check failed (the certificate authority could not be " +
              "reached, or the certificate was revoked)"
            : $"is signed by {signer ?? "no publisher Windows trusts"}, not by {publisher}";
        TryDelete(installer);
        throw new InvalidDataException($"{Path.GetFileName(installer)} {reason}");
    }

    private async Task DownloadAsync(ReleaseFile file, string path, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? file.Size;
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(StallTimeout); // the timeout is for silence, not for the whole 100 MB
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            var percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
            if (percent != lastPercent)
                progress?.Report(lastPercent = percent);
        }
        if (file.Size > 0 && received != file.Size)
            throw new IOException($"{file.Name}: got {received} of {file.Size} bytes");
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Opens a download page: the website for installed copies, the release page (portable zip) otherwise.</summary>
    public void OpenDownloadPage()
    {
        var url = IsInstalled ? WebsiteUrl : LatestReleaseUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open {Url}", url);
        }
    }

    /// <summary>Last error encountered by <see cref="UpdateAsync"/>.</summary>
    internal Exception? LastError { get; private set; }

    /// <summary>
    /// Raised on the UI thread when an installed copy could not download or start the new installer. The app says so
    /// and, once the user presses OK, opens <see cref="WebsiteUrl"/> (<see cref="OpenDownloadPage"/>).
    /// </summary>
    public event EventHandler? UpdateFailed;

    private void Fail(string stage, Exception ex)
    {
        LastError = ex;
        _log.LogWarning(ex, "In-app update failed ({Stage})", stage);
        Step = UpdateStep.Failed;
        UpdateFailed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Checks whether a previous in-app update attempt failed before restarting.
    /// If an update failure marker exists, safely reads the log path, consumes (deletes) the marker,
    /// logs the failure, sets <see cref="Step"/> to <see cref="UpdateStep.Failed"/>, and returns <c>true</c>.
    /// </summary>
    public bool CheckPendingFailure(string? dataDir = null)
    {
        var markerPath = FailureMarkerPathOverride ?? Path.Combine(dataDir ?? UserDataStore.DefaultDirectory, ".update-failed");
        if (!File.Exists(markerPath))
            return false;

        string? logPath = null;
        try
        {
            var content = File.ReadAllText(markerPath).Trim();
            if (!string.IsNullOrWhiteSpace(content)
                && Path.IsPathRooted(content)
                && !content.Any(char.IsControl)
                && (File.Exists(content) || content.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            {
                logPath = content;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read update failure marker at {MarkerPath}", markerPath);
        }
        finally
        {
            try
            {
                File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete update failure marker at {MarkerPath}", markerPath);
            }
        }

        LastFailureLogPath = logPath;
        _log.LogWarning("In-app update failed during previous run. Log: {LogPath}", logPath ?? "(unknown)");
        Step = UpdateStep.Failed;
        return true;
    }

    private void Publish(string? newer, bool announce)
    {
        var previous = LatestVersion;
        LatestVersion = newer;
        if (announce && newer is not null && newer != previous)
            UpdateFound?.Invoke(this, newer);
    }

    /// <summary>The installer the next update downloads (tests).</summary>
    internal ReleaseFile? Installer => _installer;

    /// <summary>A file attached to a release.</summary>
    internal sealed record ReleaseFile(string Name, string Url, long Size);

    /// <summary>Picks the installer/archive and checksum file matching the target platform from a GitHub release (null when missing).</summary>
    internal static (ReleaseFile? Installer, ReleaseFile? Checksums) FindFiles(JsonElement release, string? target = "win")
    {
        target ??= "win";
        ReleaseFile? installer = null, checksums = null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        var isLinux = target.StartsWith("linux-", StringComparison.OrdinalIgnoreCase);
        var expectedInstallerSuffix = isLinux ? $"-{target}.tar.gz" : "-win-Setup.exe";
        var expectedChecksumsSuffix = isLinux ? $"-{target}-SHA256SUMS.txt" : "-SHA256SUMS.txt";

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            if (name.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.EndsWith(expectedInstallerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                installer = new ReleaseFile(name, url, size);
            }
            else if (name.EndsWith(expectedChecksumsSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // When targeting Windows, exclude platform-specific checksum files (e.g. -linux-x64-SHA256SUMS.txt, -osx-...)
                if (!isLinux && (name.Contains("-linux-", StringComparison.OrdinalIgnoreCase) || name.Contains("-osx-", StringComparison.OrdinalIgnoreCase)))
                    continue;

                checksums = new ReleaseFile(name, url, size);
            }
        }
        return (installer, checksums);
    }

    private const UnixFileMode RwxMask =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Extracts a Linux release archive (.tar.gz) into <paramref name="destinationDirectory"/> with security validation
    /// (rejecting path traversal, absolute paths, unsafe entry types, directory symlinks, and escaping/component symlinks).
    /// Returns the path to the extracted payload folder containing <c>install.sh</c>.
    /// </summary>
    internal static string ExtractLinuxArchive(string archivePath, string destinationDirectory)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found", archivePath);

        var fullDestRoot = Path.GetFullPath(destinationDirectory);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
            fullDestRoot += Path.DirectorySeparatorChar;

        Directory.CreateDirectory(fullDestRoot);

        using (var fileStream = File.OpenRead(archivePath))
        using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
        using (var tarReader = new TarReader(gzipStream))
        {
            while (tarReader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;

                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException("Archive contains an entry with an empty name.");

                if (Path.IsPathRooted(entry.Name) || entry.Name.StartsWith('/') || entry.Name.StartsWith('\\') || entry.Name.Contains('\\'))
                    throw new InvalidDataException($"Archive entry '{entry.Name}' has an invalid or rooted path.");

                var destinationPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.Name));
                if (!destinationPath.StartsWith(fullDestRoot, StringComparison.Ordinal))
                    throw new InvalidDataException($"Archive entry '{entry.Name}' attempts path traversal outside destination.");

                // Validate that no ancestor directory component between fullDestRoot and destinationPath is a symlink on disk
                ValidateNoSymlinkComponents(fullDestRoot, destinationPath);

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        if (Path.Exists(destinationPath))
                        {
                            var info = new DirectoryInfo(destinationPath);
                            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                throw new InvalidDataException($"Archive entry '{entry.Name}' conflicts with an existing symbolic link.");
                        }
                        Directory.CreateDirectory(destinationPath);
                        break;

                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive entry '{entry.Name}' conflicts with an existing directory.");

                        var parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(parentDir);

                        // If destinationPath already exists and is a symlink, delete it rather than writing through it
                        if (Path.Exists(destinationPath))
                        {
                            var existingInfo = new FileInfo(destinationPath);
                            if (existingInfo.LinkTarget is not null || existingInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                File.Delete(destinationPath);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);

                        if (OperatingSystem.IsLinux())
                        {
                            var mode = (entry.Mode & RwxMask) | UnixFileMode.UserRead | UnixFileMode.UserWrite;
                            File.SetUnixFileMode(destinationPath, mode);
                        }
                        break;

                    case TarEntryType.SymbolicLink:
                        if (string.IsNullOrWhiteSpace(entry.LinkName)
                            || Path.IsPathRooted(entry.LinkName)
                            || entry.LinkName.StartsWith('/')
                            || entry.LinkName.StartsWith('\\')
                            || entry.LinkName.Contains('\\'))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' has an invalid target '{entry.LinkName}'.");
                        }

                        var linkParent = Path.GetDirectoryName(destinationPath)!;
                        var linkTarget = Path.GetFullPath(Path.Combine(linkParent, entry.LinkName));
                        if (!linkTarget.StartsWith(fullDestRoot, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' points outside destination directory: '{entry.LinkName}'.");
                        }

                        // Directory symlinks are disallowed to prevent symlink-component traversal and link-order attacks
                        if (Directory.Exists(linkTarget))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' points to a directory, which is prohibited.");
                        }

                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive symlink '{entry.Name}' conflicts with an existing directory.");

                        if (!string.IsNullOrEmpty(linkParent))
                            Directory.CreateDirectory(linkParent);

                        if (Path.Exists(destinationPath))
                            File.Delete(destinationPath);

                        File.CreateSymbolicLink(destinationPath, entry.LinkName);
                        break;

                    case TarEntryType.HardLink:
                        if (string.IsNullOrWhiteSpace(entry.LinkName)
                            || Path.IsPathRooted(entry.LinkName)
                            || entry.LinkName.StartsWith('/')
                            || entry.LinkName.StartsWith('\\')
                            || entry.LinkName.Contains('\\'))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' has an invalid target '{entry.LinkName}'.");
                        }

                        var hardTarget = Path.GetFullPath(Path.Combine(fullDestRoot, entry.LinkName));
                        if (!hardTarget.StartsWith(fullDestRoot, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' points outside destination directory: '{entry.LinkName}'.");
                        }

                        if (Directory.Exists(hardTarget))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' points to a directory, which is prohibited.");
                        }

                        if (!File.Exists(hardTarget))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' target '{entry.LinkName}' does not exist.");
                        }

                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive hard link '{entry.Name}' conflicts with an existing directory.");

                        var hardParent = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(hardParent))
                            Directory.CreateDirectory(hardParent);

                        if (Path.Exists(destinationPath))
                            File.Delete(destinationPath);

                        File.Copy(hardTarget, destinationPath, overwrite: true);
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported or unsafe tar entry type '{entry.EntryType}' for entry '{entry.Name}'.");
                }
            }
        }

        var rootTrimmed = Path.TrimEndingDirectorySeparator(fullDestRoot);
        string payloadDir;
        if (File.Exists(Path.Combine(rootTrimmed, "install.sh")))
        {
            payloadDir = rootTrimmed;
        }
        else
        {
            var candidates = Directory.GetDirectories(rootTrimmed)
                .Where(d => File.Exists(Path.Combine(d, "install.sh")))
                .ToList();
            if (candidates.Count == 1)
            {
                payloadDir = candidates[0];
            }
            else
            {
                throw new InvalidDataException("Archive does not contain a single valid Filee payload with install.sh");
            }
        }

        var installSh = Path.Combine(payloadDir, "install.sh");
        var fileeExe = Path.Combine(payloadDir, "Filee");
        if (!File.Exists(installSh) || !File.Exists(fileeExe))
        {
            throw new InvalidDataException("Archive is missing install.sh or Filee executable in the payload directory.");
        }

        if (OperatingSystem.IsLinux())
        {
            EnsureExecutable(installSh);
            EnsureExecutable(fileeExe);
            var cli = Path.Combine(payloadDir, "filee-cli");
            if (File.Exists(cli))
                EnsureExecutable(cli);
            var uninstall = Path.Combine(payloadDir, "uninstall.sh");
            if (File.Exists(uninstall))
                EnsureExecutable(uninstall);
        }

        return payloadDir;
    }

    /// <summary>
    /// Verifies that no intermediate directory component between <paramref name="rootPath"/> and <paramref name="targetPath"/> is a symbolic link.
    /// </summary>
    private static void ValidateNoSymlinkComponents(string rootPath, string targetPath)
    {
        var current = rootPath.TrimEnd(Path.DirectorySeparatorChar);
        var relative = Path.GetRelativePath(current, targetPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (Path.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"Archive path component '{segments[i]}' in '{relative}' is a symbolic link.");
                }
            }
        }
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsLinux() && File.Exists(path))
        {
            var mode = (File.GetUnixFileMode(path) & RwxMask) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>Finds the hash for <paramref name="fileName"/> in a <c>sha256sum</c>-style file ("hash  name" lines).</summary>
    internal static string? ParseChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == fileName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Lets tests hand in a release without calling GitHub.</summary>
    internal void UseRelease(ReleaseFile? installer, ReleaseFile? checksums) => (_installer, _checksums) = (installer, checksums);

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Gets the default root directory where secure workspaces are allocated.</summary>
    internal static string GetDefaultWorkspaceBaseDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(dataHome)
                ? dataHome
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "filee");
        }
        return Path.GetTempPath();
    }

    /// <summary>
    /// Creates a private, dedicated temporary workspace for this update instance.
    /// Mode 0700 on Unix, inaccessible to other local users, non-predictable, and never reusing pre-existing paths.
    /// </summary>
    internal static string CreateSecureWorkspace(string? baseDir = null)
    {
        baseDir = string.IsNullOrWhiteSpace(baseDir) ? GetDefaultWorkspaceBaseDirectory() : baseDir;

        Directory.CreateDirectory(baseDir);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var candidate = Path.Combine(baseDir, $".update-{Environment.ProcessId}-{token}");
            if (Path.Exists(candidate))
                continue;

            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(candidate);
            }

            var dirInfo = new DirectoryInfo(candidate);
            if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Secure workspace candidate '{candidate}' is a symbolic link.");
            }

            return candidate;
        }

        throw new IOException("Failed to create a unique secure workspace directory after multiple attempts.");
    }

    /// <summary>Safely deletes a secure workspace directory created by this update, refusing to follow symlinks.</summary>
    internal static void DeleteSecureWorkspace(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Path.Exists(workspacePath))
            return;

        var dirInfo = new DirectoryInfo(workspacePath);
        if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        try
        {
            Directory.Delete(workspacePath, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    /// <summary>
    /// Checks whether a process with the given <paramref name="pid"/> is currently running.
    /// Used to protect active update workspaces from being swept.
    /// </summary>
    internal static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            if (OperatingSystem.IsLinux())
                return Directory.Exists($"/proc/{pid}");

            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            // If in doubt (e.g. access denied), assume alive to safeguard against deleting active workspaces
            return true;
        }
    }

    /// <summary>
    /// Checks whether <paramref name="name"/> matches the Filee-owned workspace pattern
    /// <c>.update-&lt;pid&gt;-&lt;32-hex-characters&gt;</c> and extracts the valid positive process ID.
    /// </summary>
    internal static bool TryParseWorkspaceName(string name, out int pid)
    {
        pid = 0;
        var parts = name.Split('-');
        if (parts.Length != 3 || parts[0] != ".update")
            return false;

        // The final component must contain exactly 32 hexadecimal characters (0-9, a-f, or A-F)
        if (parts[2].Length != 32 || !parts[2].All(char.IsAsciiHexDigit))
            return false;

        // <pid> must be a valid positive process ID in decimal (digits only, no signs)
        if (parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit) || !int.TryParse(parts[1], out var parsedPid) || parsedPid <= 0)
            return false;

        pid = parsedPid;
        return true;
    }

    /// <summary>
    /// Deletes stale update workspaces left behind by crashes, forced kills, or aborted updates.
    /// Protects active workspaces by checking both an age threshold (default 1 day) and process liveness
    /// (never deleting a workspace belonging to a running PID). Restricts cleanup strictly to Filee-owned
    /// workspace directories matching <c>.update-&lt;pid&gt;-&lt;32-hex-characters&gt;</c>.
    /// </summary>
    public static void DeleteStaleWorkspaces(string? root = null, TimeSpan? age = null, ILogger? log = null)
    {
        // Workspaces are only made on Linux, under Filee's own data folder. Elsewhere the default root is the shared
        // temp folder, where other programs' folders live.
        if (root is null && !OperatingSystem.IsLinux())
            return;
        var threshold = age ?? TimeSpan.FromDays(1);
        try
        {
            var baseDir = root ?? GetDefaultWorkspaceBaseDirectory();
            var dirInfo = new DirectoryInfo(baseDir);
            if (!dirInfo.Exists || dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            foreach (var folder in dirInfo.EnumerateDirectories(".update-*"))
            {
                if (folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                // Restrict cleanup strictly to Filee-owned workspace names: .update-<pid>-<32-hex-chars>
                // Foreign directories (e.g. .update-something or .update-cache) are rejected immediately.
                if (!TryParseWorkspaceName(folder.Name, out var pid))
                    continue;

                try
                {
                    if (IsProcessAlive(pid))
                        continue;

                    if (DateTime.UtcNow - folder.LastWriteTimeUtc > threshold)
                    {
                        DeleteSecureWorkspace(folder.FullName);
                        log?.LogInformation("Deleted stale update workspace {Workspace}", folder.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.LogWarning(ex, "Could not delete stale update workspace {Workspace}", folder.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning(ex, "Failed to enumerate update workspaces in {Root}", root);
        }
    }

    /// <summary>Locates the installation directory on Linux.</summary>
    internal static string GetLinuxInstallDirectory()
    {
        if (IsLinuxInstalled(AppContext.BaseDirectory))
            return AppContext.BaseDirectory;
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = !string.IsNullOrWhiteSpace(dataHome)
            ? dataHome
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "filee", "application");
    }

    /// <summary>True when release tag <paramref name="tag"/> ("v1.2.0") is a higher version than <paramref name="current"/>.</summary>
    internal static bool IsNewer(string? tag, string current) =>
        TryParse(tag, out var latest) && TryParse(current, out var installed) && latest > installed;

    /// <summary>"v1.2" / "1.2.0-beta+abc" → 1.2.0 (missing parts are 0, suffixes ignored).</summary>
    internal static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed))
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    private static string Normalize(string tag) => TryParse(tag, out var version) ? version.ToString(3) : tag;
}
