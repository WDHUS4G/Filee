// macOS asks for Accessibility with its own alert once, not at every start (#316): after an update of the ad-hoc
// signed app the list can show Filee switched on while macOS still denies it, so the alert kept coming back.

using Filee.App.Services.Triggers;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class AccessibilityPromptTests
{
    [Fact]
    public void The_alert_is_shown_once_and_not_at_later_starts()
    {
        var directory = Directory.CreateTempSubdirectory("filee-accessibility-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            Assert.True(TriggerService.ShouldAskForPermission(store.Settings, needsPermission: true));

            // What App does when it shows the alert.
            store.Settings.AccessibilityPromptShown = true;
            store.SaveSettings();
            Assert.False(TriggerService.ShouldAskForPermission(store.Settings, needsPermission: true));

            // The next start reads the flag back: still denied, but no alert again.
            var nextStart = new UserDataStore(directory);
            nextStart.Load();
            Assert.True(nextStart.Settings.AccessibilityPromptShown);
            Assert.False(TriggerService.ShouldAskForPermission(nextStart.Settings, needsPermission: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void No_alert_with_the_permission_or_without_a_drag_gesture()
    {
        var settings = new AppSettings();
        Assert.False(TriggerService.ShouldAskForPermission(settings, needsPermission: false));

        foreach (var gesture in settings.Triggers.Where(t => t.Kind == TriggerKind.Drag))
            gesture.Enabled = false;
        Assert.False(TriggerService.ShouldAskForPermission(settings, needsPermission: true));
    }
}
