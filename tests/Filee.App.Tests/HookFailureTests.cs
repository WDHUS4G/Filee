using Filee.App.Services.Triggers;
using Filee.App.ViewModels.Pages;
using SharpHook;
using SharpHook.Data;

namespace Filee.App.Tests;

public class HookFailureTests
{
    [Fact]
    public void A_Linux_back_end_that_does_not_load_is_recognised()
    {
        Assert.True(TriggerService.IsMissingLinuxBackend(new HookException(UioHookResult.ErrorLinuxLoadBackend)));
        Assert.False(TriggerService.IsMissingLinuxBackend(new HookException(UioHookResult.ErrorAxApiDisabled)));
        Assert.False(TriggerService.IsMissingLinuxBackend(new InvalidOperationException("ErrorLinuxLoadBackend")));
    }

    [Fact]
    public void A_missing_Linux_back_end_is_explained_instead_of_showing_the_raw_error()
    {
        var loc = new KeyLocalizer();
        Assert.Equal("triggers.hook_linux_backend", TriggersPageViewModel.HookFailure(loc, "ErrorLinuxLoadBackend", linuxBackendMissing: true));
        Assert.Equal("triggers.hook_failed|denied", TriggersPageViewModel.HookFailure(loc, "denied", linuxBackendMissing: false));
    }
}
