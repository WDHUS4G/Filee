// Engine checks that must not hang: a network share on the PATH that doesn't answer, an FFmpeg that never exits.

using System.Diagnostics;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class EngineProbeTests
{
    [Theory]
    [InlineData(@"\\server\share\tools\bin", @"\\server\share")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\server", @"\\server")]
    [InlineData(@"C:\Tools\bin", null)]
    [InlineData(@"\\?\C:\Tools", null)]
    [InlineData(@"\\.\pipe\x", null)]
    [InlineData("/usr/local/bin", null)]
    [InlineData("", null)]
    public void Network_folders_on_the_path_are_recognised(string folder, string? share) =>
        Assert.Equal(share, EngineEnvironment.ShareOf(folder));

    [Fact]
    public void A_program_that_never_exits_is_given_up_on_and_killed()
    {
        // Both run for about half a minute (ping waits a second between replies).
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", "30", "127.0.0.1" })
            : ("/bin/sleep", new[] { "30" });
        var watch = Stopwatch.StartNew();

        Assert.Throws<TimeoutException>(() => FfmpegRunner.RunBriefly(program, arguments, TimeSpan.FromSeconds(1)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Output_of_a_program_that_exits_is_returned()
    {
        var result = OperatingSystem.IsWindows()
            ? FfmpegRunner.RunBriefly("cmd.exe", ["/d", "/c", "echo filee-probe"], TimeSpan.FromSeconds(10))
            : FfmpegRunner.RunBriefly("/bin/sh", ["-c", "echo filee-probe"], TimeSpan.FromSeconds(10));

        Assert.Contains("filee-probe", result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
    }
}
