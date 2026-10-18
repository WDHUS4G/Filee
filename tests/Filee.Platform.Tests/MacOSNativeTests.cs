using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class MacOSNativeTests
{
    [Fact]
    public void NativeFrameworksAndPermissionProbeLoadOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS frameworks.");
            return;
        }

        var platform = new MacOSPlatformServices();
        _ = platform.HasAccessibilityPermission;
        _ = platform.PrefersReducedMotion;
        _ = platform.ProcessNameAt(0, 0);
        Assert.Equal(4, platform.SystemDragThreshold);
    }
}
