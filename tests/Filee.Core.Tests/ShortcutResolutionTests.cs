using Filee.Core.Platform;

namespace Filee.Core.Tests;

public class ShortcutResolutionTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "filee-shortcuts");
    private static readonly string Report = Path.Combine(Folder, "report.docx");
    private static readonly string Photo = Path.Combine(Folder, "photo.jpg");
    private static readonly string ToReport = Path.Combine(Folder, "report.lnk");
    private static readonly string AlsoToReport = Path.Combine(Folder, "report (2).lnk");
    private static readonly string Broken = Path.Combine(Folder, "broken.lnk");

    private static string? Target(string path) =>
        path == ToReport || path == AlsoToReport ? Report : null;

    [Fact]
    public void Shortcuts_become_their_targets_in_place()
    {
        var files = IPlatformServices.ResolveShortcuts([Photo, ToReport, Broken], Target);

        Assert.Equal([Photo, Report, Broken], files);
    }

    [Fact]
    public void A_shortcut_dropped_with_its_target_gives_the_file_once()
    {
        Assert.Equal([Report, Photo], IPlatformServices.ResolveShortcuts([ToReport, Photo, Report], Target));
        Assert.Equal([Report], IPlatformServices.ResolveShortcuts([ToReport, AlsoToReport], Target));
    }

    [Fact]
    public void Without_shortcuts_the_list_is_left_as_it_is()
    {
        IReadOnlyList<string> files = [Photo, Report];
        IPlatformServices elsewhere = new NullPlatformServices();

        Assert.Same(files, IPlatformServices.ResolveShortcuts(files, Target));
        // Where Filee doesn't follow shortcuts, one stays a file of its own.
        Assert.Equal([ToReport], elsewhere.ResolveShortcuts([ToReport]));
    }
}
