using Avalonia;
using Avalonia.Headless;
using Filee.Setup.Installation;
using Filee.Setup.Platform;

[assembly: AvaloniaTestApplication(typeof(Filee.Setup.Tests.TestAppBuilder))]
// One application for all tests, like Filee.App.Tests.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Filee.Setup.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so the pages can be captured.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A scratch folder that goes away with the test.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "filee-setup-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file (and its folder) below the scratch folder; returns its full path.</summary>
    public string Write(string relative, string content = "x")
    {
        var path = Sub(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Records what Setup asks Windows to do; never touches the PC.</summary>
public sealed class FakeSystem : ISetupSystem
{
    public bool ExplorerMenuSupported { get; set; }
    public bool MenuRegistered { get; set; }
    public bool MenuCurrent { get; set; }
    public string? RegisterError { get; set; }
    public List<string> Calls { get; } = [];
    public List<RunningProgram> Running { get; } = [];
    public Func<string, string, int> Run { get; set; } = (_, _) => 0;
    public Action<string>? OnCreateShortcut { get; set; }

    public bool IsExplorerMenuRegistered() => MenuRegistered;

    public bool IsExplorerMenuCurrent(string installFolder) => MenuRegistered && MenuCurrent;

    public string? RegisterExplorerMenu(string installFolder, bool removeFirst)
    {
        Calls.Add($"register-menu {installFolder} {removeFirst}");
        if (RegisterError is null)
            MenuRegistered = true;
        return RegisterError;
    }

    public void UnregisterExplorerMenu()
    {
        Calls.Add("unregister-menu");
        MenuRegistered = false;
    }

    public void CreateShortcut(string path, string target, string workingFolder, string description)
    {
        Calls.Add($"shortcut {path} -> {target}");
        OnCreateShortcut?.Invoke(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, target);
    }

    public void BroadcastEnvironmentChange() => Calls.Add("broadcast");

    public int RunAndWait(string executable, string arguments, TimeSpan timeout)
    {
        Calls.Add($"run {System.IO.Path.GetFileName(executable)} {arguments}");
        return Run(executable, arguments);
    }

    public IReadOnlyList<RunningProgram> ProgramsIn(string folder) =>
        Running.Where(p => p.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Kill(int processId)
    {
        Calls.Add($"kill {processId}");
        Running.RemoveAll(p => p.Id == processId);
    }

    public void DeleteAtRestart(string path) => Calls.Add($"delete-at-restart {path}");
}

/// <summary>A Setup.exe with a payload, a registry and a PC of its own, all in a scratch folder.</summary>
public sealed class SetupBench : IDisposable
{
    private readonly TempFolder _temp = new();

    public SetupBench(string version = "2.0.0", Action<TempFolder, string>? app = null)
    {
        Paths = SetupPaths.Sandbox(_temp.Sub("pc"));
        Directory.CreateDirectory(Paths.Temp);
        AppFolder = _temp.Sub("app");
        WriteApp(app);
        SetupExe = MakeSetup(version);
        Context = new SetupContext(Registry, System, Paths, Log);
    }

    public SetupPaths Paths { get; }
    public MemoryRegistry Registry { get; } = new();
    public FakeSystem System { get; } = new();
    public SetupLog Log { get; } = new(null);
    public SetupContext Context { get; }
    public string AppFolder { get; }
    public string SetupExe { get; private set; }
    public TempFolder Temp => _temp;

    public string InstallFolder => Paths.DefaultInstallFolder(InstallScope.Machine);

    /// <summary>The published app: Filee.exe, the "filee" command, an engine and a few more files.</summary>
    private void WriteApp(Action<TempFolder, string>? app)
    {
        _temp.Write(@"app\Filee.exe", "Filee 2");
        _temp.Write(@"app\Filee.dll", new string('a', 20_000));
        _temp.Write(@"app\cli\filee.cmd", "@\"%~dp0..\\filee-cli.exe\" %*");
        _temp.Write(@"app\filee-cli.exe", "cli");
        _temp.Write(@"app\engines\rhwp\rhwp.exe", "rhwp");
        _temp.Write(@"app\FileeExplorerMenu.dll", "menu");
        _temp.Write(@"app\FileeExplorerMenu.msix", "package");
        app?.Invoke(_temp, AppFolder);
    }

    /// <summary>Packs the app behind a stand-in Setup program (any bytes do: only the payload is read).</summary>
    public string MakeSetup(string version)
    {
        var program = _temp.Write("program.exe", "MZ stand-in for the Setup program " + new string('p', 3000));
        var output = _temp.Sub($"Filee-{version}-win-Setup.exe");
        Filee.Setup.Payload.SetupPayload.Write(program, AppFolder, output, version,
            [new Filee.Setup.Payload.EngineOffer("pandoc", ["pandoc"], 42_000_000, 236_000_000, true)], ".filee-component", fast: true);
        return SetupExe = output;
    }

    public InstallChoices Choices(SetupTasks? tasks = null) =>
        new(InstallFolder, InstallScope.Machine, tasks ?? SetupTasks.Defaults, ["pandoc"], "en");

    public InstallResult Install(InstallChoices? choices = null, PreviousInstall? previous = null)
    {
        using var payload = Filee.Setup.Payload.SetupPayload.Open(SetupExe)!;
        return new Installer(Context, payload).Run(choices ?? Choices(), previous ?? PreviousInstall.Find(Registry, InstallScope.Machine));
    }

    public void Dispose() => _temp.Dispose();
}
