// Update check against GitHub Releases. When a newer version is out the app says so (sidebar, notice, tray). In an
// installed copy "Update" downloads the new installer, checks it against the release's SHA256SUMS file and runs it;
// Setup closes Filee, updates it in place (settings are kept) and starts it again. A portable copy, a copy run from
// source, or a failed download opens a download page instead. The check works in every build.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    /// True when this copy was installed by Setup (Inno Setup puts its uninstaller next to Filee.exe). Only then can
    /// the new installer update it in place; the portable zip and a copy run from source have no uninstaller.
    /// </summary>
    public static bool IsInstalled { get; } = OperatingSystem.IsWindows()
                                              && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Whether <see cref="UpdateAsync"/> installs by itself (<see cref="IsInstalled"/>; tests set it).</summary>
    internal bool InstallsItself { get; init; } = IsInstalled;

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
            (_installer, _checksums) = FindFiles(json.RootElement);
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

        string installer;
        try
        {
            var progress = new Progress<int>(percent => DownloadPercent = percent);
            installer = await DownloadInstallerAsync(Path.Combine(Path.GetTempPath(), "Filee-update"), progress);
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
            setup = Process.Start(new ProcessStartInfo(installer, InstallerArguments) { UseShellExecute = true });
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

        // A successful update closes Filee before Setup exits, so getting here means Setup ended without updating:
        // the administrator prompt was declined (Setup asks for it itself), or the user cancelled.
        using (setup)
            await setup.WaitForExitAsync();
        _log.LogInformation("Installer ended with exit code {Code}", setup.ExitCode);
        Step = setup.ExitCode == 0 ? UpdateStep.Idle : UpdateStep.Cancelled;
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

    /// <summary>
    /// Raised on the UI thread when an installed copy could not download or start the new installer. The app says so
    /// and, once the user presses OK, opens <see cref="WebsiteUrl"/> (<see cref="OpenDownloadPage"/>).
    /// </summary>
    public event EventHandler? UpdateFailed;

    private void Fail(string stage, Exception ex)
    {
        _log.LogWarning(ex, "In-app update failed ({Stage})", stage);
        Step = UpdateStep.Failed;
        UpdateFailed?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Picks the Windows installer and the checksum file from a GitHub release (null when missing).</summary>
    internal static (ReleaseFile? Installer, ReleaseFile? Checksums) FindFiles(JsonElement release)
    {
        ReleaseFile? installer = null, checksums = null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            if (name.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.EndsWith("-win-Setup.exe", StringComparison.OrdinalIgnoreCase))
                installer = new ReleaseFile(name, url, size);
            else if (name.EndsWith("-SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                checksums = new ReleaseFile(name, url, size);
        }
        return (installer, checksums);
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
