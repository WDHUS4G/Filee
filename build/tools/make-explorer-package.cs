// Packs FileeExplorerMenu.msix, the unsigned sparse package behind the Windows 11 "Convert with Filee" menu entry.
// Run by build/build-explorer-menu.ps1:  dotnet run build/tools/make-explorer-package.cs -- <output folder>
// Uses the Windows packaging API (AppxPackaging.dll, part of Windows 10/11): it validates the manifest and writes the
// block map and content types exactly like MakeAppx, so no Windows SDK is needed. The manifest comes from
// ExplorerMenuPackage.CreateManifest (unit tested).
#:project ../../src/Filee.Platform.Windows/Filee.Platform.Windows.csproj
#:property AllowUnsafeBlocks=true
#:property PublishAot=false

using System.Runtime.InteropServices;
using System.Text;
using Filee.Platform.Windows;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run build/tools/make-explorer-package.cs -- <output folder>");
    return 2;
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var repo = Environment.CurrentDirectory;
while (!File.Exists(Path.Combine(repo, "Filee.slnx")))
    repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("Run this from inside the Filee repository.");

var work = Directory.CreateTempSubdirectory("filee-msix-");
try
{
    var manifestPath = Path.Combine(work.FullName, "AppxManifest.xml");
    File.WriteAllText(manifestPath, ExplorerMenuPackage.CreateManifest(), new UTF8Encoding(false));
    var packagePath = Path.Combine(output, ExplorerMenuPackage.PackageFileName);
    var logo = Path.Combine(repo, "src", "Filee.App", "Assets", "Icons", "filee.png");
    Appx.Pack(packagePath, manifestPath, [(ExplorerMenuPackage.LogoPath, logo, "image/png")]);
    Console.WriteLine($"Wrote {packagePath} ({new FileInfo(packagePath).Length:N0} bytes, manifest {ExplorerMenuPackage.ManifestVersion})");
}
finally
{
    work.Delete(recursive: true);
}
return 0;

/// <summary>Minimal raw-vtable client of IAppxFactory / IAppxPackageWriter (no COM interop marshalling needed).</summary>
internal static unsafe class Appx
{
    private static readonly Guid ClsidAppxFactory = new("5842a140-ff9f-4166-8f5c-62f5b7b0c781");
    private static readonly Guid IidAppxFactory = new("beb94909-e451-438b-b5a7-d79e767b75d8");
    private const uint ClsctxInprocServer = 1;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint StgmRead = 0, StgmWrite = 1, StgmShareDenyWrite = 0x20, StgmShareExclusive = 0x10, StgmCreate = 0x1000;
    private const int AppxCompressionNormal = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageSettings
    {
        public int ForceZip32;
        public nint HashMethod; // IUri*
    }

    public static void Pack(string packagePath, string manifestPath, IEnumerable<(string Name, string Path, string Type)> payload)
    {
        var init = CoInitializeEx(0, CoinitMultithreaded);
        if (init != RpcEChangedMode) // the console main thread may already be an MTA/STA; either works
            Check(init, "CoInitializeEx");
        nint factory = 0, output = 0, hash = 0, writer = 0;
        try
        {
            var clsid = ClsidAppxFactory;
            var iid = IidAppxFactory;
            Check(CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, out factory), "CoCreateInstance(AppxFactory)");
            Check(SHCreateStreamOnFileEx(packagePath, StgmCreate | StgmWrite | StgmShareExclusive, 0x80, 1, 0, out output), "create " + packagePath);
            Check(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, 0, out hash), "CreateUri");
            var settings = new PackageSettings { ForceZip32 = 1, HashMethod = hash };
            // IAppxFactory::CreatePackageWriter(IStream*, APPX_PACKAGE_SETTINGS*, IAppxPackageWriter**) is slot 3.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, PackageSettings*, nint*, int>)Slot(factory, 3);
            nint created;
            Check(create(factory, output, &settings, &created), "CreatePackageWriter");
            writer = created;

            foreach (var (name, path, type) in payload)
            {
                Check(SHCreateStreamOnFileEx(path, StgmRead | StgmShareDenyWrite, 0, 0, 0, out var input), "open " + path);
                try
                {
                    // IAppxPackageWriter::AddPayloadFile(LPCWSTR, LPCWSTR, APPX_COMPRESSION_OPTION, IStream*) is slot 3.
                    var add = (delegate* unmanaged[Stdcall]<nint, char*, char*, int, nint, int>)Slot(writer, 3);
                    fixed (char* n = name)
                    fixed (char* t = type)
                        Check(add(writer, n, t, AppxCompressionNormal, input), "AddPayloadFile " + name);
                }
                finally
                {
                    Release(input);
                }
            }

            Check(SHCreateStreamOnFileEx(manifestPath, StgmRead | StgmShareDenyWrite, 0, 0, 0, out var manifest), "open manifest");
            try
            {
                // IAppxPackageWriter::Close(IStream* manifest) is slot 4; it validates the manifest against the schema.
                var close = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(writer, 4);
                Check(close(writer, manifest), "Close (manifest validation)");
            }
            finally
            {
                Release(manifest);
            }
        }
        finally
        {
            Release(writer);
            Release(hash);
            Release(output);
            Release(factory);
        }
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8} {Marshal.GetExceptionForHR(hr)?.Message}");
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, int create, nint template, out nint stream);

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateUri(string uri, uint flags, nuint reserved, out nint result);
}
