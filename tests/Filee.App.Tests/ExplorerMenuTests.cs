// Tests for the Windows 11 File Explorer menu: package manifest, registration scripts, the --convert-list hand-over
// and (when build/build-explorer-menu.ps1 has run) the native FileeExplorerMenu.dll loaded in-process.

using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.App.Services;
using Filee.Platform.Windows;

namespace Filee.App.Tests;

// Registration and the DLL only exist on Windows; the tests that need them skip elsewhere.
[SupportedOSPlatform("windows")]
public class ExplorerMenuTests
{
    private static readonly XNamespace F = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(dir, "Filee.slnx")))
                dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Repository root not found.");
            return dir;
        }
    }

    private static string BuiltFolder => Path.Combine(RepoRoot, "build", ".cache", "explorer-menu");

    [Fact]
    public void Manifest_declares_an_unsigned_sparse_package_with_the_explorer_command()
    {
        var manifest = XDocument.Parse(ExplorerMenuPackage.CreateManifest()).Root!;
        Assert.Equal(F + "Package", manifest.Name);

        var identity = manifest.Element(F + "Identity")!;
        Assert.Equal(ExplorerMenuPackage.PackageName, (string?)identity.Attribute("Name"));
        Assert.Contains("OID.2.25.311729368913984317654407730594956997722=1", (string?)identity.Attribute("Publisher"));
        Assert.Equal(ExplorerMenuPackage.ManifestVersion.ToString(4), (string?)identity.Attribute("Version"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));
        Assert.Equal("true", (string?)manifest.Element(F + "Properties")!.Element(Uap10 + "AllowExternalContent"));
        Assert.Equal("10.0.22000.0", (string?)manifest.Descendants(F + "TargetDeviceFamily").Single().Attribute("MinVersion"));

        var app = manifest.Descendants(F + "Application").Single();
        Assert.Equal("Filee.exe", (string?)app.Attribute("Executable"));
        Assert.Equal("mediumIL", (string?)app.Attribute(Uap10 + "TrustLevel"));
        Assert.Equal("win32App", (string?)app.Attribute(Uap10 + "RuntimeBehavior"));
        Assert.Equal("none", (string?)app.Element(Uap + "VisualElements")!.Attribute("AppListEntry"));

        var clsid = ExplorerMenuPackage.ClassId.ToString("D");
        var itemType = app.Descendants(Desktop4 + "FileExplorerContextMenus").Single().Element(Desktop5 + "ItemType")!;
        Assert.Equal("*", (string?)itemType.Attribute("Type"));
        Assert.Equal(clsid, (string?)itemType.Element(Desktop5 + "Verb")!.Attribute("Clsid"), ignoreCase: true);

        var comClass = app.Descendants(Com + "Class").Single();
        Assert.Equal(clsid, (string?)comClass.Attribute("Id"), ignoreCase: true);
        Assert.Equal(ExplorerMenuPackage.DllFileName, (string?)comClass.Attribute("Path"));
    }

    [Fact]
    public void Native_source_uses_the_same_class_id_list_prefix_and_title_file()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "Filee.ExplorerMenu", "FileeExplorerMenu.c"));
        var match = Regex.Match(source, @"kClsidCommand = \{(?<parts>[^;]+)\};");
        Assert.True(match.Success);
        var n = Regex.Matches(match.Groups["parts"].Value, "0x([0-9a-fA-F]+)")
            .Select(m => ulong.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(11, n.Length);
        var fromSource = new Guid((uint)n[0], (ushort)n[1], (ushort)n[2],
            (byte)n[3], (byte)n[4], (byte)n[5], (byte)n[6], (byte)n[7], (byte)n[8], (byte)n[9], (byte)n[10]);
        Assert.Equal(ExplorerMenuPackage.ClassId, fromSource);

        Assert.Contains($"#define LIST_PREFIX L\"{CommandLine.ListFilePrefix}\"", source);
        Assert.Contains($"#define TITLE_FILE L\"Filee\\\\{ExplorerMenuPackage.TitleFileName}\"", source);
    }

    [Fact]
    public void Family_name_matches_what_Windows_computes_from_the_publisher()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10), "Package APIs need Windows.");
        Assert.Equal(ExplorerMenuPackage.FamilyName, ExplorerMenuRegistration.ComputeFamilyName());
    }

    [Fact]
    public void Every_native_import_exists_in_the_dll_it_names()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        // GetPackagePathByFullName2 was imported from kernel32.dll, which doesn't export it: the call only failed
        // once a package was registered and crashed the app right after "Add to the main menu" (1.1.0).
        var nativeMethods = typeof(ExplorerMenuRegistration).Assembly.GetType("Filee.Platform.Windows.NativeMethods", throwOnError: true)!;
        var imports = nativeMethods.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(m => (Method: m, Import: m.GetCustomAttributes(typeof(LibraryImportAttribute), false).OfType<LibraryImportAttribute>().FirstOrDefault()))
            .Where(m => m.Import is not null)
            .ToList();
        Assert.NotEmpty(imports);
        Assert.All(imports, m =>
        {
            var entryPoint = m.Import!.EntryPoint ?? m.Method.Name;
            Assert.True(NativeLibrary.TryLoad(m.Import.LibraryName, out var library), $"{m.Import.LibraryName} can't be loaded");
            Assert.True(NativeLibrary.TryGetExport(library, entryPoint, out _), $"{m.Import.LibraryName} has no {entryPoint}");
        });
    }

    [Fact]
    public void Reading_the_registered_package_never_throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10), "Package APIs need Windows.");
        Assert.Null(ExplorerMenuRegistration.ExternalLocationOf("Filee.NotInstalled_1.0.0.0_x64__0000000000000"));
        // Whatever is registered on this machine (nothing on CI): a registered sparse package has an external location.
        if (ExplorerMenuRegistration.Find() is { } package)
            Assert.False(string.IsNullOrEmpty(package.ExternalLocation), package.FullName);
        _ = ExplorerMenuRegistration.GetState(BuiltFolder);
    }

    [Fact]
    public void PowerShell_literals_escape_straight_and_typographic_single_quotes()
    {
        Assert.Equal(@"'C:\Users\Kim''s PC\Filee'", ExplorerMenuRegistration.Quote(@"C:\Users\Kim's PC\Filee"));
        Assert.Equal("'Kim\u2019\u2019s'", ExplorerMenuRegistration.Quote("Kim\u2019s"));
    }

    [Fact]
    public void Add_script_registers_the_package_unsigned_with_the_install_folder_as_external_location()
    {
        var script = ExplorerMenuRegistration.BuildAddScript(@"C:\Apps\Filee\current\FileeExplorerMenu.msix",
            @"C:\Apps\Filee\current", @"C:\Temp\err.txt");
        Assert.Contains(@"Add-AppxPackage -Path 'C:\Apps\Filee\current\FileeExplorerMenu.msix' -ExternalLocation 'C:\Apps\Filee\current' -AllowUnsigned", script);
        Assert.Contains(@"[IO.File]::WriteAllText('C:\Temp\err.txt', $_.Exception.Message); exit 1", script);
        Assert.DoesNotContain("\"", script);

        var remove = ExplorerMenuRegistration.BuildRemoveScript(@"C:\Temp\err.txt");
        Assert.Contains("Get-AppxPackage -Name 'Filee.ExplorerMenu' | Remove-AppxPackage", remove);
    }

    [Fact]
    public void Deployment_errors_lose_the_activity_id_note_and_line_breaks()
    {
        const string message = "Deployment failed with HRESULT: 0x80073D2B, The package could not be installed.\r\n\r\n" +
                               "Windows cannot install package Filee.ExplorerMenu.\r\n\r\n" +
                               "NOTE: For additional information, look for [ActivityId] 3a41 in the Event Log";
        Assert.Equal("Deployment failed with HRESULT: 0x80073D2B, The package could not be installed. Windows cannot install package Filee.ExplorerMenu.",
            ExplorerMenuRegistration.ShortenDeploymentError(message));
    }

    [Fact]
    public void PowerShell_start_info_elevates_only_when_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var elevated = ExplorerMenuRegistration.CreateStartInfo("exit 0", elevated: true);
        Assert.True(elevated.UseShellExecute);
        Assert.Equal("runas", elevated.Verb);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", elevated.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("-NoLogo -NoProfile -NonInteractive -Command \"exit 0\"", elevated.Arguments);

        var plain = ExplorerMenuRegistration.CreateStartInfo("exit 0", elevated: false);
        Assert.False(plain.UseShellExecute);
        Assert.True(plain.CreateNoWindow);
        Assert.Throws<ArgumentException>(() => ExplorerMenuRegistration.CreateStartInfo("echo \"x\"", elevated: false));
    }

    [Fact]
    public void Convert_list_reads_existing_paths_and_only_deletes_its_own_temp_file()
    {
        var folder = Directory.CreateTempSubdirectory("filee-list-test-").FullName;
        var list = Path.Combine(Path.GetTempPath(), CommandLine.ListFilePrefix + Guid.NewGuid().ToString("N") + ".txt");
        var foreign = Path.Combine(folder, CommandLine.ListFilePrefix + "elsewhere.txt");
        try
        {
            var a = Path.Combine(folder, "a 1.png");
            var b = Path.Combine(folder, "b, 한글.pdf");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "x");
            File.WriteAllText(list, $"{a}\r\n\r\n{Path.Combine(folder, "missing.txt")}\r\n{b}\r\n", new System.Text.UTF8Encoding(true));
            File.WriteAllText(foreign, a);

            var options = CommandLine.Parse(["--convert-list", list]);
            Assert.Equal(new[] { a, b }, options.ConvertFiles);
            Assert.Equal(list, options.ListFile);
            options.DeleteListFile();
            Assert.False(File.Exists(list));

            var other = CommandLine.Parse(["--convert-list", foreign]);
            Assert.Equal(new[] { a }, other.ConvertFiles);
            other.DeleteListFile();
            Assert.True(File.Exists(foreign)); // not in %TEMP% itself: never deleted

            Assert.Empty(CommandLine.Parse(["--convert-list", Path.Combine(folder, "nope.txt")]).ConvertFiles);
        }
        finally
        {
            File.Delete(list);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Built_package_contains_the_current_manifest()
    {
        var package = Path.Combine(BuiltFolder, ExplorerMenuPackage.PackageFileName);
        Assert.SkipUnless(File.Exists(package), "Run build/build-explorer-menu.ps1 first.");
        using var zip = ZipFile.OpenRead(package);
        Assert.NotNull(zip.GetEntry("AppxBlockMap.xml"));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry(ExplorerMenuPackage.LogoPath.Replace('\\', '/')));
        using var stream = zip.GetEntry("AppxManifest.xml")!.Open();
        Assert.True(XNode.DeepEquals(XDocument.Parse(ExplorerMenuPackage.CreateManifest()).Root, XDocument.Load(stream).Root),
            "The package is stale: run build/build-explorer-menu.ps1 again.");
    }

    [Fact]
    public void Native_dll_serves_the_explorer_command_in_process()
    {
        var dll = Path.Combine(BuiltFolder, ExplorerMenuPackage.DllFileName);
        Assert.SkipUnless(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && File.Exists(dll),
            "Run build/build-explorer-menu.ps1 first (x64 Windows).");

        using var command = NativeCommand.Create(dll);
        // The title comes from the file the app writes into its data folder; without it the English default is used.
        var expectedTitle = ExplorerMenuRegistration.ReadTitle();
        Assert.Equal(expectedTitle ?? "Convert with Filee", command.GetTitle());
        Assert.Equal(Path.Combine(BuiltFolder, "Filee.exe") + ",0", command.GetIcon(), ignoreCase: true);
        Assert.Equal(ExplorerMenuPackage.ClassId, command.GetCanonicalName());

        const int enabled = 0, hidden = 2;
        var file = Path.GetTempFileName();
        var folder = Directory.CreateTempSubdirectory("filee-menu-test-").FullName;
        try
        {
            Assert.Equal(expectedTitle is null ? hidden : enabled, command.GetState(file));
            Assert.Equal(hidden, command.GetState(folder)); // folders never get the entry
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(folder);
        }
    }

    /// <summary>Calls FileeExplorerMenu.dll through its exports and the raw IExplorerCommand vtable.</summary>
    private sealed class NativeCommand : IDisposable
    {
        private static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
        private static readonly Guid IidExplorerCommand = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
        private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IidShellItemArray = new("b63ea76d-1f85-456f-a19c-48159efa858b");

        private readonly nint _library;
        private readonly nint _command;

        private delegate int GetClassObjectFn(ref Guid clsid, ref Guid iid, out nint result);
        private delegate int CreateInstanceFn(nint self, nint outer, ref Guid iid, out nint result);
        private delegate int GetStringFn(nint self, nint items, out nint value);
        private delegate int GetGuidFn(nint self, out Guid value);
        private delegate int GetStateFn(nint self, nint items, int okToBeSlow, out int state);
        private delegate uint ReleaseFn(nint self);

        private NativeCommand(nint library, nint command)
        {
            _library = library;
            _command = command;
        }

        public static NativeCommand Create(string dll)
        {
            var library = NativeLibrary.Load(dll);
            var getClassObject = Marshal.GetDelegateForFunctionPointer<GetClassObjectFn>(NativeLibrary.GetExport(library, "DllGetClassObject"));
            var clsid = ExplorerMenuPackage.ClassId;
            var iid = IidClassFactory;
            Assert.Equal(0, getClassObject(ref clsid, ref iid, out var factory));
            iid = IidExplorerCommand;
            Assert.Equal(0, Method<CreateInstanceFn>(factory, 3)(factory, 0, ref iid, out var command));
            Release(factory);
            return new NativeCommand(library, command);
        }

        public string? GetTitle() => GetString(3);
        public string? GetIcon() => GetString(4);

        public Guid GetCanonicalName()
        {
            Assert.Equal(0, Method<GetGuidFn>(_command, 6)(_command, out var name));
            return name;
        }

        public int GetState(string path)
        {
            var iid = IidShellItem;
            Assert.Equal(0, SHCreateItemFromParsingName(path, 0, ref iid, out var item));
            iid = IidShellItemArray;
            Assert.Equal(0, SHCreateShellItemArrayFromShellItem(item, ref iid, out var array));
            Release(item);
            try
            {
                Assert.Equal(0, Method<GetStateFn>(_command, 7)(_command, array, 1, out var state));
                return state;
            }
            finally
            {
                Release(array);
            }
        }

        public void Dispose()
        {
            Release(_command);
            NativeLibrary.Free(_library);
        }

        private string? GetString(int slot)
        {
            Assert.Equal(0, Method<GetStringFn>(_command, slot)(_command, 0, out var value));
            try
            {
                return Marshal.PtrToStringUni(value);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }

        private static T Method<T>(nint instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        private static void Release(nint instance) => Method<ReleaseFn>(instance, 2)(instance);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out nint item);

        [DllImport("shell32.dll")]
        private static extern int SHCreateShellItemArrayFromShellItem(nint item, ref Guid iid, out nint array);
    }
}
