using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class AppLocalRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-runtime-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(-1073741515)] // 0xC0000135 STATUS_DLL_NOT_FOUND, as reported in #248
    [InlineData(-1073741511)] // 0xC0000139 STATUS_ENTRYPOINT_NOT_FOUND
    public void A_program_Windows_could_not_load_is_refused_with_the_runtime_message(int exitCode)
    {
        var rhwp = Path.Combine(_root, "engines", "rhwp", "rhwp.exe");
        var refused = Assert.Throws<ConversionRefusedException>(() => AppLocalRuntime.ThrowIfMissing(rhwp, exitCode));

        Assert.Equal("error.runtime_missing", refused.ErrorKey);
        Assert.Equal($"rhwp.exe, 0x{unchecked((uint)exitCode):X8}", refused.Detail);
        Assert.StartsWith("rhwp.exe, 0xC000013", refused.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-1073741819)] // 0xC0000005 access violation: the program ran and crashed
    public void Other_exit_codes_are_left_to_the_engine(int exitCode)
    {
        AppLocalRuntime.ThrowIfMissing("rhwp.exe", exitCode);
        Assert.False(AppLocalRuntime.IsMissingDll(exitCode));
    }

    [Fact]
    public async Task ProcessRunner_reports_a_missing_DLL_instead_of_the_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows exit status.");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            ProcessRunner.RunAsync(cmd, ["/c", "exit -1073741515"], TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        Assert.Equal(AppLocalRuntime.ErrorKey, refused.ErrorKey);
        Assert.Equal("cmd.exe, 0xC0000135", refused.Detail);
        var ordinary = await ProcessRunner.RunAsync(cmd, ["/c", "exit 3"], TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(3, ordinary.ExitCode);
    }

    [Fact]
    public void LibreOffice_gets_its_own_runtime_next_to_soffice()
    {
        var lo = Path.Combine(_root, "libreoffice");
        Directory.CreateDirectory(Path.Combine(lo, "program"));
        Directory.CreateDirectory(Path.Combine(lo, "System64"));
        Directory.CreateDirectory(Path.Combine(lo, "System"));
        File.WriteAllText(Path.Combine(lo, "System64", "msvcp140.dll"), "x64 c++");
        File.WriteAllText(Path.Combine(lo, "System64", "vcruntime140.dll"), "x64 runtime");
        File.WriteAllText(Path.Combine(lo, "System", "vcruntime140.dll"), "x86 runtime");
        File.WriteAllText(Path.Combine(lo, "program", "vcruntime140.dll"), "newer runtime");

        Assert.Equal(1, AppLocalRuntime.CopyLibreOfficeRuntime(lo));

        Assert.Equal("x64 c++", File.ReadAllText(Path.Combine(lo, "program", "msvcp140.dll")));
        Assert.Equal("newer runtime", File.ReadAllText(Path.Combine(lo, "program", "vcruntime140.dll")));
        Assert.Equal(0, AppLocalRuntime.CopyLibreOfficeRuntime(lo)); // already repaired
    }

    [Fact]
    public void Other_LibreOffice_layouts_are_left_alone()
    {
        var installed = Path.Combine(_root, "LibreOffice");
        Directory.CreateDirectory(Path.Combine(installed, "program"));

        Assert.Equal(0, AppLocalRuntime.CopyLibreOfficeRuntime(installed));
        Assert.Equal(0, AppLocalRuntime.CopyLibreOfficeRuntime(Path.Combine(_root, "missing")));
        Assert.Empty(Directory.GetFiles(Path.Combine(installed, "program")));
    }
}
