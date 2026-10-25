// The Microsoft C++ runtime (MSVCP140.dll, VCRUNTIME140.dll, ...) that engines built with Visual C++ need.
//
// Windows has no copy of its own: a PC gets one from the Visual C++ Redistributable, which many programs install but a
// fresh Windows lacks. Filee therefore puts the runtime next to the programs that need it (app-local deployment,
// Windows looks in a program's folder first): the "vcruntime" download (conda-forge) next to rhwp.exe, Filee.exe
// (for unhwp.dll) and gswin64c.exe, LibreOffice's own copy next to soffice.exe (see CopyLibreOfficeRuntime).
// When a DLL is still missing, Windows does not start the program at all and reports STATUS_DLL_NOT_FOUND as its
// exit code; ThrowIfMissing turns that into a message the user can act on.

using Filee.Core.Conversion;

namespace Filee.Engines.Infrastructure;

public static class AppLocalRuntime
{
    /// <summary>Exit code of a program Windows could not start because a DLL it imports is missing (0xC0000135).</summary>
    public const int DllNotFound = unchecked((int)0xC0000135);

    /// <summary>Exit code of a program whose DLL is too old to have a function it imports (0xC0000139).</summary>
    public const int EntryPointNotFound = unchecked((int)0xC0000139);

    /// <summary>Localization key of the message for <see cref="DllNotFound"/> and <see cref="EntryPointNotFound"/>.</summary>
    public const string ErrorKey = "error.runtime_missing";

    /// <summary>True when Windows refused to start a program because of a missing or outdated DLL.</summary>
    public static bool IsMissingDll(int exitCode) => exitCode is DllNotFound or EntryPointNotFound;

    /// <summary>
    /// Throws a <see cref="ConversionRefusedException"/> saying that a runtime file is missing (with the program and
    /// the exit code as detail) when <paramref name="exitCode"/> is <see cref="DllNotFound"/> or
    /// <see cref="EntryPointNotFound"/>, instead of the raw exit code each engine would report.
    /// </summary>
    public static void ThrowIfMissing(string executable, int exitCode)
    {
        if (IsMissingDll(exitCode))
            throw new ConversionRefusedException(ErrorKey, $"{Path.GetFileName(executable)}, 0x{exitCode:X8}");
    }

    /// <summary>
    /// LibreOffice's MSI installs the C++ runtime into the system folder; unpacked with an administrative install
    /// (msiexec /a) it lands in System64 next to program/ instead, where soffice.exe never looks. Copies those DLLs
    /// into program/ (never replacing one that is there). Returns the number of files copied; 0 for any other layout.
    /// </summary>
    public static int CopyLibreOfficeRuntime(string root)
    {
        var system64 = Path.Combine(root, "System64");
        var program = Path.Combine(root, "program");
        if (!Directory.Exists(system64) || !Directory.Exists(program))
            return 0;
        var copied = 0;
        foreach (var dll in Directory.EnumerateFiles(system64, "*.dll"))
        {
            var target = Path.Combine(program, Path.GetFileName(dll));
            if (File.Exists(target))
                continue;
            File.Copy(dll, target);
            copied++;
        }
        return copied;
    }
}
