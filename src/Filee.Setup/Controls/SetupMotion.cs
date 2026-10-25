// Whether Setup animates: Windows' "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION). Off gives the static
// versions of the logo, the background, the clips and the check mark. Tests switch it off for stable screenshots.

using System.Runtime.InteropServices;

namespace Filee.Setup.Controls;

public static partial class SetupMotion
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    /// <summary>False when Windows' animations are off (or a test turned them off).</summary>
    public static bool Enabled { get; set; } = ReadSystem();

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    private static bool ReadSystem()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) || enabled;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }
}
