// CLI help must describe the watcher's platform default without loading settings or engines.

using Filee.Core.Watching;

namespace Filee.Cli.Tests;

public sealed class HelpTests
{
    [Theory]
    [InlineData("help", "watch")]
    [InlineData("watch", "--help")]
    [InlineData("watch", "-h")]
    public async Task Watch_help_reports_the_platform_default_without_loading_engines(string command, string option)
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var code = await Cli.RunAsync([command, option], output, errors, TestContext.Current.CancellationToken,
            () => throw new InvalidOperationException("Help must not initialize engines."));

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors.ToString());
        Assert.Contains(FormattableString.Invariant(
            $"--settle <seconds> How long a file must stay unchanged (default {FolderWatcher.DefaultSettleTime.TotalSeconds})"), output.ToString());
    }
}
