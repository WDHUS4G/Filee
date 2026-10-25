// When an elevated Filee counts as cut off from Explorer, and engine statuses checked in the background.

using Filee.Core.Conversion;
using Filee.Core.Platform;

namespace Filee.Core.Tests;

public class ElevationStateTests
{
    [Theory]
    [InlineData(IntegrityLevel.High, IntegrityLevel.Medium, true)]    // installer run as administrator started Filee
    [InlineData(IntegrityLevel.System, IntegrityLevel.Medium, true)]
    [InlineData(IntegrityLevel.Medium, IntegrityLevel.Medium, false)] // the normal case
    [InlineData(IntegrityLevel.High, IntegrityLevel.High, false)]     // UAC off: Explorer runs elevated too
    [InlineData(IntegrityLevel.Medium, IntegrityLevel.High, false)]   // Explorer above Filee: not what this warns about
    [InlineData(IntegrityLevel.High, IntegrityLevel.Unknown, false)]  // no Explorer (another shell)
    [InlineData(IntegrityLevel.Unknown, IntegrityLevel.Medium, false)]
    [InlineData(IntegrityLevel.MediumPlus, IntegrityLevel.Medium, false)]
    public void Only_an_administrator_Filee_above_Explorer_is_cut_off(IntegrityLevel process, IntegrityLevel shell, bool cutOff) =>
        Assert.Equal(cutOff, new ElevationState(process, shell, Elevated: process >= IntegrityLevel.High).ShellCannotReachFilee);

    [Fact]
    public void Unknown_state_is_never_cut_off() => Assert.False(ElevationState.Unknown.ShellCannotReachFilee);

    [Theory]
    [InlineData(0x0000u, IntegrityLevel.Untrusted)]
    [InlineData(0x1000u, IntegrityLevel.Low)]
    [InlineData(0x2000u, IntegrityLevel.Medium)]
    [InlineData(0x2100u, IntegrityLevel.MediumPlus)]
    [InlineData(0x3000u, IntegrityLevel.High)]
    [InlineData(0x3001u, IntegrityLevel.High)]
    [InlineData(0x4000u, IntegrityLevel.System)]
    [InlineData(0x5000u, IntegrityLevel.System)] // protected process
    public void Mandatory_label_rids_map_to_levels(uint rid, IntegrityLevel level) =>
        Assert.Equal(level, ElevationState.FromRid(rid));

    [Fact]
    public void Describes_itself_for_the_log()
    {
        Assert.Equal("integrity High (elevated), Explorer Medium",
            new ElevationState(IntegrityLevel.High, IntegrityLevel.Medium, true).ToString());
        Assert.Equal("integrity Medium, Explorer Medium",
            new ElevationState(IntegrityLevel.Medium, IntegrityLevel.Medium, false).ToString());
        Assert.Equal("integrity unknown", ElevationState.Unknown.ToString());
    }
}

// The waits are generous: on a busy CI runner the background check can take seconds just to start.
public class CatalogBackgroundCheckTests
{
    /// <summary>An engine whose check waits for the test, like FFmpeg answering <c>-version</c> slowly.</summary>
    private sealed class SlowEngine : IConverter
    {
        public ManualResetEventSlim Gate { get; } = new(false);
        public ManualResetEventSlim Started { get; } = new(false);
        public int Checks;
        public volatile bool Available;

        public string Id => "slow";
        public string DisplayName => "Slow";
        public IReadOnlyList<ConversionEdge> Edges => [new("png", "jpg")];
        public int MaxParallelism => 0;

        public EngineStatus GetStatus()
        {
            Interlocked.Increment(ref Checks);
            var available = Available; // what the engine looked like when the check started
            Started.Set();
            Gate.Wait(TimeSpan.FromSeconds(10));
            return available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_installed");
        }

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_status_asked_during_the_background_check_waits_for_it_instead_of_checking_again()
    {
        var engine = new SlowEngine { Available = true };
        var catalog = new ConverterCatalog([engine]);
        var background = catalog.CheckInBackgroundAsync();
        Assert.True(engine.Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var asked = Task.Run(() => catalog.StatusOf(engine), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(asked.IsCompleted);
        engine.Gate.Set();

        Assert.True((await asked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).IsAvailable);
        await background.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, engine.Checks);
    }

    [Fact]
    public async Task A_refresh_during_the_background_check_discards_its_result()
    {
        var engine = new SlowEngine { Available = false };
        var catalog = new ConverterCatalog([engine]);
        var background = catalog.CheckInBackgroundAsync();
        Assert.True(engine.Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // The user installed the engine while the first check was still running.
        engine.Available = true;
        catalog.Refresh();
        engine.Gate.Set();
        await background.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(catalog.StatusOf(engine).IsAvailable);
        Assert.Equal(2, engine.Checks);
    }
}
