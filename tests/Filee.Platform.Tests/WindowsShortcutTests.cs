using Filee.Platform.Windows;

namespace Filee.Platform.Tests;

/// <summary>Shortcuts (.lnk) dropped on Filee convert the file they point to (#159).</summary>
public sealed class WindowsShortcutTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-lnk-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string Link(string name, string target)
    {
        var path = Path.Combine(_folder, name);
        ShellLinks.Create(path, target);
        return path;
    }

    [Fact]
    public void A_shortcut_resolves_to_its_file_or_folder_and_a_broken_one_to_nothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows shortcuts need the Windows shell.");
            return;
        }
        var report = Write("보고서 report.docx");
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "photos")).FullName;
        var gone = Write("gone.pdf");
        var toReport = Link("report.docx - Shortcut.lnk", report);
        var toFolder = Link("photos.lnk", folder);
        var broken = Link("gone.lnk", gone);
        File.Delete(gone);
        var platform = new WindowsPlatformServices();

        Assert.Equal(report, platform.ShortcutTarget(toReport), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(folder, platform.ShortcutTarget(toFolder), StringComparer.OrdinalIgnoreCase);
        Assert.Null(platform.ShortcutTarget(broken));
        Assert.Null(platform.ShortcutTarget(report));
        Assert.Null(platform.ShortcutTarget(Path.Combine(_folder, "missing.lnk")));
    }

    [Fact]
    public void The_file_format_fallback_reads_the_same_target_as_the_shell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows shortcuts need the Windows shell.");
            return;
        }
        var report = Write("보고서 report.docx");
        var link = Link("report.lnk", report);

        Assert.Equal(report, ShellLinks.ReadFromFile(link), StringComparer.OrdinalIgnoreCase);
        Assert.Null(ShellLinks.ReadFromFile(report));
        Assert.Null(ShellLinks.ParseTarget(new byte[10]));
    }

    [Fact]
    public void A_shortcut_dropped_with_its_target_gives_the_file_once_and_a_broken_one_stays()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows shortcuts need the Windows shell.");
            return;
        }
        var report = Write("report.docx");
        var photo = Write("photo.jpg");
        var toReport = Link("report.lnk", report);
        var broken = Path.Combine(_folder, "broken.lnk");
        File.WriteAllText(broken, "not a shortcut");
        Core.Platform.IPlatformServices platform = new WindowsPlatformServices();

        var files = platform.ResolveShortcuts([toReport, photo, report, broken]);

        Assert.Equal([report, photo, broken], files, StringComparer.OrdinalIgnoreCase);
    }
}
