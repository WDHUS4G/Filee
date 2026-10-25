// Memory is given back once conversions are over: after a quiet spell, never while one runs, and not again without
// a new reason.

using Filee.App.Services;

namespace Filee.App.Tests;

public class MemoryTrimmerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Trims_once_after_a_quiet_spell_and_not_again_without_a_request()
    {
        using var trimmer = new MemoryTrimmer(() => false, () => { }, Delay);
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);
        Assert.Equal(0, trimmer.Trims); // nothing happened yet

        trimmer.Request();
        trimmer.Request();
        await Until(() => trimmer.Trims > 0);
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);
        Assert.Equal(1, trimmer.Trims);
    }

    [Fact]
    public async Task Never_trims_while_a_conversion_runs()
    {
        var busy = true;
        var trimmedWhileBusy = false;
        using var trimmer = new MemoryTrimmer(() => Volatile.Read(ref busy), () => trimmedWhileBusy |= Volatile.Read(ref busy), Delay);
        trimmer.Request();
        await Task.Delay(Delay * 5, TestContext.Current.CancellationToken);
        Assert.Equal(0, trimmer.Trims);

        Volatile.Write(ref busy, false);
        await Until(() => trimmer.Trims > 0);
        Assert.Equal(1, trimmer.Trims);
        Assert.False(trimmedWhileBusy);
    }

    [Fact]
    public async Task A_new_request_moves_the_trim_on()
    {
        using var trimmer = new MemoryTrimmer(() => false, () => { }, TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++)
        {
            trimmer.Request();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Equal(0, trimmer.Trims); // a conversion finishing every 100 ms: still converting
        await Until(() => trimmer.Trims > 0);
        Assert.Equal(1, trimmer.Trims);
    }
}
