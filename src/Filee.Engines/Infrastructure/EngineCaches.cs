// Caches engines keep between conversions (typefaces looked up for HWP and CAD text). The app releases them while no
// conversion runs, so a Filee waiting in the tray gives the memory back. Skia's own caches stay: they are shared with
// the UI's render thread, and purging them while it draws crashed in native code (SKGraphics.PurgeAllCaches).

using Filee.Engines.Cad;
using Filee.Engines.Hwp;

namespace Filee.Engines.Infrastructure;

public static class EngineCaches
{
    /// <summary>
    /// Drops what the engines cache between conversions; the next one looks everything up again. Meant for idle
    /// times, but safe while a conversion runs (the trim's busy check can't stop one from starting right after it):
    /// the caches only forget references, nothing still in use is disposed, and each thread disposes its own
    /// measuring fonts the next time it measures. NativeMemoryStressTests releases them during conversions.
    /// </summary>
    public static void Release()
    {
        RhwpFonts.ReleaseCache();
        CadFonts.ReleaseCache();
    }
}
