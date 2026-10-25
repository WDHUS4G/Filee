using System.Buffers.Binary;
using System.Text;
using Filee.Setup.Installation;

namespace Filee.Setup.Tests;

public class RuntimeCheckTests
{
    [Fact]
    public void Imports_of_a_windows_program_are_read()
    {
        // Windows' own programs import system DLLs (some through API sets).
        var imports = RuntimeCheck.ImportedDlls(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        Assert.NotEmpty(imports);
        Assert.All(imports, d => Assert.EndsWith(".dll", d, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(RuntimeCheck.ImportedDlls(typeof(RuntimeCheckTests).Assembly.Location.Replace(".dll", ".deps.json", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("vcruntime140.dll", true)]
    [InlineData("VCRUNTIME140_1.dll", true)]
    [InlineData("msvcp140.dll", true)]
    [InlineData("msvcp140_codecvt_ids.dll", true)]
    [InlineData("concrt140.dll", true)]
    [InlineData("vcomp140.dll", true)]
    [InlineData("ucrtbase.dll", false)]
    [InlineData("api-ms-win-crt-runtime-l1-1-0.dll", false)]
    [InlineData("kernel32.dll", false)]
    public void Visual_cpp_runtime_dlls_are_recognised(string dll, bool runtime) => Assert.Equal(runtime, RuntimeCheck.IsVcRuntime(dll));

    [Fact]
    public void A_bundled_engine_without_its_runtime_is_reported()
    {
        using var temp = new TempFolder();
        var app = temp.Sub("app");
        var system = temp.Sub("system32");
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(Path.Combine(app, "engines", "rhwp"));
        File.WriteAllBytes(Path.Combine(app, "engines", "rhwp", "rhwp.exe"), PeImporting("VCRUNTIME140.dll", "KERNEL32.dll"));

        var missing = Assert.Single(RuntimeCheck.FindMissing(app, system));
        Assert.Equal(@"engines\rhwp\rhwp.exe", missing.Program);
        Assert.Equal("vcruntime140.dll", missing.Dll);

        // App-local copies next to it (what fix/rhwp-vc-runtime ships) or the Redistributable in System32 both do.
        File.WriteAllText(Path.Combine(app, "engines", "rhwp", "vcruntime140.dll"), "");
        Assert.Empty(RuntimeCheck.FindMissing(app, system));
        File.Delete(Path.Combine(app, "engines", "rhwp", "vcruntime140.dll"));
        File.WriteAllText(Path.Combine(system, "VCRUNTIME140.dll"), "");
        Assert.Empty(RuntimeCheck.FindMissing(app, system));
    }

    /// <summary>A PE32+ file with one section holding an import table that names <paramref name="dlls"/>.</summary>
    private static byte[] PeImporting(params string[] dlls)
    {
        var bytes = PayloadTests.MinimalPe(importRva: 0x1000, sections: 1, size: 0x600);
        // Section header right after the optional header (0x58 + 240): ".idata", RVA 0x1000, raw data at 0x200.
        var section = 0x58 + 240;
        Encoding.ASCII.GetBytes(".idata").CopyTo(bytes, section);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 8), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 16), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 20), 0x200);
        // Import descriptors at RVA 0x1000 (file 0x200), names from RVA 0x1100 (file 0x300); a zero descriptor ends them.
        var nameRva = 0x1100;
        for (var i = 0; i < dlls.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200 + i * 20), 0x1080);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x200 + i * 20 + 12), (uint)nameRva);
            Encoding.ASCII.GetBytes(dlls[i]).CopyTo(bytes, 0x200 + (nameRva - 0x1000));
            nameRva += dlls[i].Length + 1;
        }
        return bytes;
    }
}
