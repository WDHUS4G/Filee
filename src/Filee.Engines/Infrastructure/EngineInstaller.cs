// Downloads, verifies and unpacks optional engines into the per-user engines folder (EngineEnvironment.DownloadRoot).
//
//  * Every download is checked against the SHA-256 pinned in engines.json before anything is unpacked.
//  * Unpacking happens in a staging folder that replaces the engine folder only when complete, so a failed or
//    cancelled install never leaves a half-installed engine behind.
//  * MSIs (LibreOffice, calibre) are unpacked with an administrative install (msiexec /a): files only, no registry
//    entries, no admin rights. Parts headless LibreOffice never uses (help, gallery, most dictionaries) are removed.
//  * conda-forge packages (Ghostscript, the Microsoft C++ runtime) are zip files holding zstd-compressed tarballs;
//    only their Windows binaries (Library/bin) are unpacked, with a managed decompressor (no 7-Zip, no installer).
//  * A marker file with the component's hash records what is installed, so an engines.json update is detected.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace Filee.Engines.Infrastructure;

public enum EngineInstallStage
{
    Downloading,
    Unpacking,
}

/// <summary>Progress of an install: <paramref name="Fraction"/> covers the whole package (0..1).</summary>
/// <param name="BytesDone">Bytes of the package downloaded so far (all components).</param>
/// <param name="BytesTotal">Download size of the components being installed.</param>
public readonly record struct EngineInstallProgress(EngineInstallStage Stage, string ComponentId, double Fraction, long BytesDone = 0, long BytesTotal = 0);

/// <summary>Installs and removes <see cref="EnginePackage"/>s.</summary>
public sealed class EngineInstaller
{
    private const string MarkerName = ".filee-component";
    private static readonly TimeSpan MsiTimeout = TimeSpan.FromMinutes(15);

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, EngineComponent> _components;
    private readonly ILogger _logger;

    /// <param name="root">Folder that receives one sub folder per engine (default: EngineEnvironment.DownloadRoot).</param>
    /// <param name="components">Download list; engines.json when null (tests pass their own).</param>
    public EngineInstaller(HttpClient http, string? root = null, IReadOnlyDictionary<string, EngineComponent>? components = null, ILogger? logger = null)
    {
        _http = http;
        _root = root ?? EngineEnvironment.DownloadRoot;
        _components = components ?? EngineDownloads.Components;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when every component is installed (any version).</summary>
    public bool IsInstalled(EnginePackage package) => package.Components.All(c => _components.ContainsKey(c) && Marker(c) is not null);

    /// <summary>True when every component is installed in the version engines.json pins.</summary>
    public bool IsUpToDate(EnginePackage package) =>
        package.Components.All(c => _components.TryGetValue(c, out var component)
            && string.Equals(Marker(c), component.Sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads and unpacks the components that are missing or outdated.</summary>
    public async Task InstallAsync(EnginePackage package, IProgress<EngineInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (package.Components.Any(c => !_components.ContainsKey(c)))
            throw new PlatformNotSupportedException(EngineDownloads.UnavailableReason(package)
                ?? $"The {package.Id} package is unavailable on this platform.");
        var pending = package.Components
            .Where(c => !string.Equals(Marker(c), _components[c].Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(c => _components[c])
            .ToList();
        var total = Math.Max(1, pending.Sum(c => c.Size));
        long done = 0;
        Directory.CreateDirectory(_root);

        foreach (var component in pending)
        {
            var offset = done;
            var file = await DownloadAsync(component,
                bytes => progress?.Report(new EngineInstallProgress(EngineInstallStage.Downloading, component.Id,
                    Math.Min(1, (offset + bytes) / (double)total), offset + bytes, total)),
                cancellationToken);
            try
            {
                progress?.Report(new EngineInstallProgress(EngineInstallStage.Unpacking, component.Id,
                    Math.Min(1, (offset + component.Size) / (double)total), offset + component.Size, total));
                await UnpackAsync(component, file, cancellationToken);
            }
            finally
            {
                TryDelete(file);
            }
            done += component.Size;
            _logger.LogInformation("Installed engine component {Component} {Version}", component.Id, component.Version);
        }
        if (pending.Count > 0)
            CopyRuntime(package);
    }

    /// <summary>Removes the package's folders. Fails if an engine is running (its files are locked).</summary>
    public void Uninstall(EnginePackage package)
    {
        foreach (var id in package.Components)
        {
            var folder = FolderOf(id);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ───────────────────────── Download ─────────────────────────

    private async Task<string> DownloadAsync(EngineComponent component, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(_root, ".downloads");
        Directory.CreateDirectory(downloads);
        var file = Path.Combine(downloads, $"{component.Id}-{component.Version}{Path.GetExtension(new Uri(component.Url).AbsolutePath)}");
        var partial = file + ".part";

        using var response = await GetFollowingRedirectsAsync(new Uri(component.Url), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (lastReport.ElapsedMilliseconds > 150)
                {
                    reportBytes(received);
                    lastReport.Restart();
                }
            }
            reportBytes(received);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            throw new InvalidDataException($"{component.Id}: checksum mismatch (expected {component.Sha256}, got {actual}). The download was discarded.");
        }
        File.Move(partial, file, overwrite: true);
        return file;
    }

    /// <summary>
    /// Follows redirects itself: download sites send users to mirrors, some of them plain HTTP, which HttpClient
    /// never follows from HTTPS. That is safe here because every file is checked against its pinned SHA-256.
    /// The HttpClient should be created with automatic redirects off.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < 10)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose();
                continue;
            }
            return response;
        }
    }

    // ───────────────────────── Unpack ─────────────────────────

    private async Task UnpackAsync(EngineComponent component, string file, CancellationToken cancellationToken)
    {
        // Short name: LibreOffice's own folder structure is deep, and msiexec fails beyond MAX_PATH (260 chars).
        var staging = Path.Combine(_root, "~" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            switch (component.Kind)
            {
                case "zip":
                    ZipFile.ExtractToDirectory(file, staging);
                    EnsureExecutablePrograms(component.Id, SingleRoot(staging));
                    Replace(component.Id, SingleRoot(staging));
                    break;
                case "tar.gz":
                case "tar.xz":
                    await UnpackTarAsync(file, staging, component.Kind, cancellationToken);
                    EnsureExecutablePrograms(component.Id, SingleRoot(staging));
                    Replace(component.Id, SingleRoot(staging));
                    break;
                case "dmg":
                    await UnpackDiskImageAsync(component.Id, file, staging, cancellationToken);
                    Replace(component.Id, staging);
                    break;
                case "msi":
                    await UnpackMsiAsync(component.Id, file, staging, Path.Combine(Path.GetTempPath(), "filee-msiexec.log"), cancellationToken);
                    Replace(component.Id, MsiProgramFolder(component.Id, staging));
                    break;
                case "conda":
                    UnpackConda(file, staging, cancellationToken);
                    Replace(component.Id, staging);
                    break;
                case "oxt":
                    // LibreOffice registers bundled extensions (plain folders under share/extensions) on start-up.
                    var extensions = Path.Combine(FolderOf("libreoffice"), "share", "extensions");
                    if (!Directory.Exists(extensions))
                        throw new InvalidOperationException($"{component.Id} needs LibreOffice, which is not installed.");
                    ZipFile.ExtractToDirectory(file, staging);
                    var target = FolderOf(component.Id);
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    Directory.Move(staging, target);
                    break;
                default:
                    throw new NotSupportedException($"Unknown engine package kind '{component.Kind}'.");
            }
            File.WriteAllText(Path.Combine(FolderOf(component.Id), MarkerName), component.Sha256);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    internal static async Task UnpackTarAsync(string file, string target, string kind, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        await using var input = File.OpenRead(file);
        await using Stream content = kind switch
        {
            "tar.gz" => new GZipStream(input, CompressionMode.Decompress),
            "tar.xz" => new XZStream(input),
            _ => throw new NotSupportedException(kind),
        };
        await TarFile.ExtractToDirectoryAsync(content, target, overwriteFiles: false, cancellationToken);
    }

    internal static void EnsureExecutablePrograms(string id, string root)
    {
        var folder = RequiredProgramFolder(id, root, OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows() || folder is null)
            return;
        foreach (var program in EngineEnvironment.OwnCopyPrograms[id])
        {
            var path = Path.Combine(folder, program);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    internal static string? RequiredProgramFolder(string id, string root, bool windows)
    {
        if (!EngineEnvironment.OwnCopyPrograms.ContainsKey(id))
            return null;
        return EngineEnvironment.FolderWithPrograms(id, root, windows)
            ?? throw new InvalidDataException($"The {id} package is missing required executable programs in its expected folders.");
    }

    private async Task UnpackDiskImageAsync(string id, string file, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Disk image packages can only be installed on macOS.");
        var appName = id switch
        {
            "libreoffice" => "LibreOffice.app",
            "calibre" => "calibre.app",
            _ => throw new NotSupportedException($"No application bundle is specified for {id}."),
        };
        // Keep the mount outside staging so failed unmounts cannot be removed by staging cleanup.
        var mount = Path.Combine(_root, ".mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        var attached = false;
        try
        {
            var attach = await ProcessRunner.RunAsync("/usr/bin/hdiutil",
                ["attach", "-readonly", "-nobrowse", "-mountpoint", mount, file], TimeSpan.FromMinutes(3), cancellationToken);
            if (attach.ExitCode != 0)
                throw new InvalidOperationException($"Could not open {id} disk image: {attach.StandardError.Trim()}");
            attached = true;
            var app = Path.Combine(mount, appName);
            if (!Directory.Exists(app) || new DirectoryInfo(app).LinkTarget is not null)
                throw new InvalidDataException($"{appName} is missing from the disk image.");
            Directory.CreateDirectory(target);
            var copy = await ProcessRunner.RunAsync("/usr/bin/ditto", [app, Path.Combine(target, appName)],
                TimeSpan.FromMinutes(10), cancellationToken);
            if (copy.ExitCode != 0)
                throw new InvalidOperationException($"Could not copy {appName}: {copy.StandardError.Trim()}");
        }
        finally
        {
            var detach = await ProcessRunner.RunAsync("/usr/bin/hdiutil", ["detach", mount],
                TimeSpan.FromSeconds(30), CancellationToken.None);
            if (detach.ExitCode != 0 && attached)
                throw new IOException($"Could not unmount the engine disk image at {mount}: {detach.StandardError.Trim()}");
            Directory.Delete(mount);
        }
    }

    /// <summary>
    /// The folder to keep from an administratively installed MSI, which lays files out as they would be installed
    /// (PFiles64\...): LibreOffice's folder with program/ and share/ (trimmed), calibre's folder with ebook-convert.
    /// </summary>
    private static string MsiProgramFolder(string id, string staging)
    {
        if (id == "calibre")
        {
            var convert = Directory.EnumerateFiles(staging, "ebook-convert.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new InvalidDataException("ebook-convert.exe not found in the calibre package.");
            return Path.GetDirectoryName(convert)!;
        }
        var soffice = Directory.EnumerateFiles(staging, "soffice.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidDataException("soffice.exe not found in the LibreOffice package.");
        var libreOffice = Path.GetDirectoryName(Path.GetDirectoryName(soffice)!)!; // folder with program/, share/
        TrimLibreOffice(libreOffice);
        return libreOffice;
    }

    private static async Task UnpackMsiAsync(string id, string msi, string target, string log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"The {id} package is a Windows installer.");
        Directory.CreateDirectory(target);
        // msiexec parses its own command line: the property value must be quoted as TARGETDIR="...", so the
        // argument list escaping of ProcessRunner cannot be used here.
        using var process = Process.Start(new ProcessStartInfo("msiexec.exe", $"/a \"{msi}\" /qn /l*v \"{log}\" TARGETDIR=\"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("msiexec could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MsiTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.ExitCode switch
            {
                1618 => "Another installation is running. Try again when it has finished.",
                _ => $"Unpacking {id} failed (msiexec exit code {process.ExitCode}, log: {log}).",
            });
        }
    }

    /// <summary>
    /// Removes what headless conversion never uses (~500 MB): help, readmes, the MSI copy, the gallery and spelling
    /// dictionaries except English and Korean (their hyphenation patterns affect page layout in PDF export).
    /// </summary>
    internal static void TrimLibreOffice(string root)
    {
        foreach (var folder in new[] { "help", "readmes", Path.Combine("share", "gallery") })
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        foreach (var msi in Directory.EnumerateFiles(root, "*.msi"))
            File.Delete(msi);
        var extensions = Path.Combine(root, "share", "extensions");
        if (Directory.Exists(extensions))
        {
            foreach (var dictionary in Directory.EnumerateDirectories(extensions, "dict-*"))
            {
                if (Path.GetFileName(dictionary) is not ("dict-en" or "dict-ko"))
                    Directory.Delete(dictionary, recursive: true);
            }
        }
    }

    /// <summary>
    /// Unpacks the Windows binaries of a conda package (.conda = zip with pkg-*.tar.zst): the files under Library/bin
    /// land directly in <paramref name="target"/>. Resources such as Ghostscript's fonts are compiled into its DLL.
    /// </summary>
    internal static void UnpackConda(string condaFile, string target, CancellationToken cancellationToken)
    {
        const string prefix = "Library/bin/";
        using var zip = ZipFile.OpenRead(condaFile);
        var payload = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("pkg-", StringComparison.Ordinal) && e.FullName.EndsWith(".tar.zst", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("Not a conda package (pkg-*.tar.zst is missing).");
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var count = 0;
        using (var zstd = new DecompressionStream(payload.Open(), leaveOpen: false))
        using (var tar = new TarReader(zstd))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.Name.Replace('\\', '/');
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var path = Path.GetFullPath(Path.Combine(target, name[prefix.Length..]));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in conda package: {entry.Name}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                count++;
            }
        }
        if (count == 0)
            throw new InvalidDataException("The conda package contains no Windows binaries (Library/bin).");
    }

    /// <summary>
    /// App-local Microsoft C++ runtime: when a package includes the "vcruntime" component, its DLLs are copied next
    /// to the programs of the package's other conda components. Windows looks for DLLs in a program's folder before
    /// System32, so the runtime a program was built for is used even when the PC has none or an older one.
    /// </summary>
    private void CopyRuntime(EnginePackage package)
    {
        const string runtime = "vcruntime";
        if (!package.Components.Contains(runtime) || !Directory.Exists(FolderOf(runtime)))
            return;
        var dlls = Directory.GetFiles(FolderOf(runtime), "*.dll");
        foreach (var id in package.Components.Where(c => c != runtime && _components[c].Kind == "conda"))
        {
            foreach (var dll in dlls)
                File.Copy(dll, Path.Combine(FolderOf(id), Path.GetFileName(dll)), overwrite: true);
        }
    }

    /// <summary>An archive with a single top-level folder is unpacked without that extra level.</summary>
    private static string SingleRoot(string folder)
    {
        var entries = Directory.GetFileSystemEntries(folder);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : folder;
    }

    /// <summary>Makes <paramref name="source"/> the engine folder, replacing a previous version.</summary>
    private void Replace(string id, string source)
    {
        RequiredProgramFolder(id, source, _components[id].Kind is "msi" or "conda" || OperatingSystem.IsWindows());
        var target = FolderOf(id);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(source, target);
    }

    // ───────────────────────── Layout ─────────────────────────

    /// <summary>Folder of a component; the LibreOffice extension lives inside LibreOffice.</summary>
    private string FolderOf(string id) => _components.TryGetValue(id, out var component) && component.Kind == "oxt"
        ? Path.Combine(_root, "libreoffice", "share", "extensions", ExtensionFolder(id))
        : Path.Combine(_root, id);

    private static string ExtensionFolder(string id) => id switch
    {
        "h2orestart" => "H2Orestart",
        _ => id,
    };

    /// <summary>Hash of the installed version, or null when not installed.</summary>
    private string? Marker(string id)
    {
        var marker = Path.Combine(FolderOf(id), MarkerName);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
