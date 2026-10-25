// A later launch hands its arguments to the running Filee, also while that one is still starting.

using Filee.App.Services;

namespace Filee.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task Arguments_reach_a_first_instance_that_starts_listening_late()
    {
        // The installer's --quit (or a second launch) came 8 s after Filee started, on a computer where Filee took 12 s
        // to start listening: one 3-second try gave up and the request was lost.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        Assert.True(first.IsFirst);
        var received = new TaskCompletionSource<string[]>();
        first.ArgumentsReceived += args => received.TrySetResult(args);

        var forward = Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(name);
            Assert.False(second.IsFirst);
            return second.ForwardToFirst(["--quit"], TimeSpan.FromSeconds(15));
        }, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken); // longer than the old single try
        first.StartListening();

        Assert.True(await forward.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal(["--quit"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_copy_with_its_own_data_folder_never_reaches_the_users_filee()
    {
        // The name without a data folder must stay the same: a new version forwards to an older one that runs.
        var usual = "Filee-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("someone"u8.ToArray()))[..12];
        Assert.Equal(usual, SingleInstance.NameFor("someone", null));
        var test = SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-a"));
        Assert.NotEqual(usual, test);
        Assert.NotEqual(test, SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-b")));
        Assert.Equal(test, SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-a")));
    }

    [Fact]
    public async Task Forwarding_gives_up_when_nobody_listens()
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);

        var delivered = await Task.Run(() =>
        {
            using var instance = SingleInstance.Acquire(name);
            return instance.ForwardToFirst(["--quit"], TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        Assert.False(delivered);
    }

    [Fact]
    public async Task Forwarding_tells_that_nobody_answered()
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);

        var result = await Task.Run(() =>
        {
            using var instance = SingleInstance.Acquire(name);
            return instance.Forward(["--convert", "a.txt"], TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ForwardResult.NoAnswer, result);
    }

    [Fact]
    public void Releasing_twice_is_harmless()
    {
        // A restart without administrator rights releases the lock early; Program releases it again at exit.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        var first = SingleInstance.Acquire(name);
        first.Dispose();
        first.Dispose();

        using var next = SingleInstance.Acquire(name);
        Assert.True(next.IsFirst);
    }
}

/// <summary>Windows: a running Filee with other rights (as administrator) refuses a normal launch.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SingleInstanceAccessTests
{
    [Fact]
    public async Task A_pipe_that_refuses_this_process_is_given_up_on_quickly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows pipe security.");
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        // Only SYSTEM may write: what a pipe of an elevated Filee looks like to a normal process.
        var security = new System.IO.Pipes.PipeSecurity();
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        await using var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 0, security);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(name);
            return second.Forward(["--convert", "a.txt"], TimeSpan.FromSeconds(30));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ForwardResult.AccessDenied, result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_integrity_level_of_this_process_can_be_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows tokens.");
        var state = Filee.Platform.Windows.Elevation.Probe();
        Assert.True(state.Process >= Filee.Core.Platform.IntegrityLevel.Medium, state.ToString());
        // Explorer may not run (CI): then its level is unknown and nothing is reported.
        if (state.Shell == Filee.Core.Platform.IntegrityLevel.Unknown)
            Assert.False(state.ShellCannotReachFilee);
    }
}
