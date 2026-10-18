using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class PortableEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-portable-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void Unix_downloads_are_pinned_for_the_requested_runtime(string runtime)
    {
        var components = EngineDownloads.ComponentsForRuntime(runtime);
        Assert.Contains("rhwp", components.Keys);
        Assert.Contains("7zip", components.Keys);
        Assert.Contains("pandoc", components.Keys);
        Assert.Contains("calibre", components.Keys);
        Assert.DoesNotContain("vcruntime", components.Keys);
        Assert.DoesNotContain("ghostscript", components.Keys);
        foreach (var component in components.Values)
        {
            Assert.StartsWith("https://", component.Url);
            Assert.DoesNotContain("windows", component.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(component.Kind, new[] { "msi", "conda" });
            Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
            Assert.True(component.Size > 0);
        }
        var arch = runtime.EndsWith("arm64", StringComparison.Ordinal) ? "aarch64" : "x86_64";
        Assert.Contains(arch, components["rhwp"].Url);
    }

    [Fact]
    public void Unknown_platforms_cannot_fall_back_to_Windows_downloads() =>
        Assert.Empty(EngineDownloads.ComponentsForRuntime("linux-riscv64"));

    [Theory]
    [InlineData("libreoffice", "LibreOffice.app/Contents/MacOS", "soffice")]
    [InlineData("calibre", "calibre.app/Contents/MacOS", "ebook-convert")]
    [InlineData("pandoc", "bin", "pandoc")]
    [InlineData("ghostscript", "bin", "gs")]
    public void Unix_programs_are_found_inside_archives_and_Mac_application_bundles(string id, string relative, string program)
    {
        var folder = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, program), "binary");
        Assert.Equal(folder, EngineEnvironment.FolderWithPrograms(id, _root, windows: false));
        Assert.Equal(folder, EngineEnvironment.FolderWithPrograms(id, Path.Combine(folder, program), windows: false));
        Assert.Null(EngineEnvironment.FolderWithPrograms(id, _root, windows: true));
    }

    [Fact]
    public void Unix_FFmpeg_requires_both_tools_in_the_same_folder()
    {
        File.WriteAllText(Path.Combine(_root, "ffmpeg"), "binary");
        Assert.Null(EngineEnvironment.FolderWithPrograms("ffmpeg", _root, windows: false));
        File.WriteAllText(Path.Combine(_root, "ffprobe"), "binary");
        Assert.Equal(_root, EngineEnvironment.FolderWithPrograms("ffmpeg", _root, windows: false));
    }

    [Fact]
    public async Task Tar_archives_preserve_program_layout_contents_and_Unix_executable_mode()
    {
        var file = TarGzip(new PaxTarEntry(TarEntryType.RegularFile, "tool/bin/tool")
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho ready\n")),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
        });
        var target = Path.Combine(_root, "extract");
        await EngineInstaller.UnpackTarAsync(file, target, "tar.gz", TestContext.Current.CancellationToken);
        var program = Path.Combine(target, "tool", "bin", "tool");
        Assert.Contains("echo ready", File.ReadAllText(program));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Tar_entries_cannot_escape_the_staging_folder()
    {
        var file = TarGzip(new PaxTarEntry(TarEntryType.RegularFile, "../escaped")
        {
            DataStream = new MemoryStream([1]),
        });
        await Assert.ThrowsAsync<IOException>(() => EngineInstaller.UnpackTarAsync(file,
            Path.Combine(_root, "extract"), "tar.gz", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public async Task Tar_symbolic_links_cannot_write_outside_staging()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var file = TarGzip(
            new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../outside" },
            new PaxTarEntry(TarEntryType.RegularFile, "link/escaped") { DataStream = new MemoryStream([1]) });
        var exception = await Record.ExceptionAsync(() => EngineInstaller.UnpackTarAsync(file,
            Path.Combine(_root, "extract"), "tar.gz", TestContext.Current.CancellationToken));
        Assert.NotNull(exception);
        Assert.False(File.Exists(Path.Combine(outside, "escaped")));
    }

    [Fact]
    public void Downloaded_Unix_programs_get_execute_permission_without_changing_data_files()
    {
        if (OperatingSystem.IsWindows())
            return;
        var program = Path.Combine(_root, "pandoc");
        var readme = Path.Combine(_root, "README");
        File.WriteAllText(program, "binary");
        File.WriteAllText(readme, "data");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var dataMode = File.GetUnixFileMode(readme);
        EngineInstaller.EnsureExecutablePrograms("pandoc", _root);
        Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(dataMode, File.GetUnixFileMode(readme));
    }

    private string TarGzip(params TarEntry[] entries)
    {
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar.gz");
        using var output = File.Create(file);
        using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip);
        foreach (var entry in entries)
            writer.WriteEntry(entry);
        return file;
    }
}
