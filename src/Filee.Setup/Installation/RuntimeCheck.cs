// Does every program Setup installs find the Microsoft Visual C++ runtime it was built against? Filee itself is a
// self-contained .NET app and needs nothing; a bundled engine built with MSVC (rhwp.exe) needs vcruntime140.dll and
// friends, either next to it (app-local, as the build copies them) or installed in System32 by the Visual C++
// Redistributable. Setup reads the import table of each .exe and .dll and reports what would be missing on this PC,
// so a missing runtime is a clear message instead of an engine that fails to start.

using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Setup.Installation;

/// <summary>A program and a runtime DLL it imports that is neither next to it nor in System32.</summary>
/// <param name="Program">Relative to the install folder.</param>
public sealed record MissingRuntime(string Program, string Dll);

public static partial class RuntimeCheck
{
    /// <summary>The Visual C++ runtime DLLs (vcruntime140.dll, msvcp140_1.dll, concrt140.dll, vcomp140.dll …).</summary>
    [GeneratedRegex(@"^(vcruntime|msvcp|concrt|vcomp|vccorlib|mfc|mfcm)140[a-z0-9_]*\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex VcRuntimeDll();

    public static bool IsVcRuntime(string dll) => VcRuntimeDll().IsMatch(dll);

    /// <summary>Checks every program in <paramref name="folder"/>.</summary>
    public static IReadOnlyList<MissingRuntime> FindMissing(string folder, string systemFolder)
    {
        var missing = new List<MissingRuntime>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = Path.GetDirectoryName(file)!;
            foreach (var dll in ImportedDlls(file).Where(IsVcRuntime))
            {
                if (!File.Exists(Path.Combine(directory, dll)) && !File.Exists(Path.Combine(systemFolder, dll)))
                    missing.Add(new MissingRuntime(Path.GetRelativePath(folder, file), dll.ToLowerInvariant()));
            }
        }
        return missing;
    }

    /// <summary>The DLL names in a PE file's import table (empty for anything else, .NET assemblies included).</summary>
    public static IReadOnlyList<string> ImportedDlls(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.Length < 0x40)
                return [];
            var headers = new byte[Math.Min(stream.Length, 4096)];
            stream.ReadExactly(headers);
            if (headers[0] != 'M' || headers[1] != 'Z')
                return [];
            var pe = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C));
            if (pe <= 0 || pe + 24 > headers.Length || BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(pe)) != 0x00004550)
                return [];
            var sections = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 20));
            var optional = pe + 24;
            if (optional + optionalSize > headers.Length)
                return [];
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(optional));
            var directories = optional + (magic == 0x20B ? 112 : 96);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(directories - 4));
            if (count < 2)
                return [];
            var importRva = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(directories + 8));
            if (importRva == 0)
                return [];
            var table = optional + optionalSize;
            var sectionList = new List<(uint Rva, uint Size, uint Raw)>();
            for (var i = 0; i < sections && table + (i + 1) * 40 <= headers.Length; i++)
            {
                var s = table + i * 40;
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(s + 8));
                var rva = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(s + 12));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(s + 16));
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(headers.AsSpan(s + 20));
                sectionList.Add((rva, Math.Max(virtualSize, rawSize), raw));
            }
            long? Offset(uint rva)
            {
                foreach (var (start, size, raw) in sectionList)
                    if (rva >= start && rva < start + size)
                        return raw + (long)(rva - start);
                return null;
            }

            var names = new List<string>();
            var descriptor = Offset(importRva);
            if (descriptor is null)
                return [];
            var entry = new byte[20];
            for (var i = 0; i < 4096; i++)
            {
                stream.Position = descriptor.Value + i * 20L;
                if (stream.Read(entry) < 20)
                    break;
                var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12));
                if (nameRva == 0 && BinaryPrimitives.ReadUInt32LittleEndian(entry) == 0)
                    break;
                if (Offset(nameRva) is not { } nameOffset)
                    continue;
                stream.Position = nameOffset;
                var name = new StringBuilder();
                for (int b; name.Length < 260 && (b = stream.ReadByte()) > 0;)
                    name.Append((char)b);
                if (name.Length > 0)
                    names.Add(name.ToString());
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }
}
