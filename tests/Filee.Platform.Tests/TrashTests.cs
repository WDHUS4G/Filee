// Moving an original to the Recycle Bin / Trash for "Replace the original" (#332): it works where there is one, and
// anything else leaves the file where it is with a reason.

using Filee.Platform.Linux;
using Filee.Platform.MacOS;
using Filee.Platform.Windows;

// Each test checks the OS it needs; the mount and missing-file checks are plain string and file work.
#pragma warning disable CA1416

namespace Filee.Platform.Tests;

public sealed class TrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-trash-" + Guid.NewGuid().ToString("N"));

    public TrashTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_file_open_elsewhere_stays_in_the_Recycle_Bins_place()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
            return;
        }
        var file = Path.Combine(_root, "open.png");
        File.WriteAllText(file, "x");

        Filee.Core.Platform.TrashRefusal? refusal;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            refusal = new WindowsPlatformServices().MoveToTrash(file);

        Assert.Equal("trash.in_use", refusal?.ReasonKey);
        Assert.Equal("x", File.ReadAllText(file));
    }

    [Fact]
    public void Windows_recycles_and_never_deletes_for_good()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
            return;
        }
        // A subst drive reports itself as fixed, but has no Recycle Bin: Windows would delete the file outright.
        var letter = "ZYXWVUTSRQPONMLKJ".Select(c => $"{c}:").First(d => !Directory.Exists(d + "\\"));
        Assert.Equal(0, Subst(letter, _root));
        try
        {
            var file = Path.Combine(letter + "\\", "kept.png");
            File.WriteAllText(file, "x");

            var refusal = new WindowsPlatformServices().MoveToTrash(file);

            Assert.Equal("trash.no_recycle_bin", refusal?.ReasonKey);
            Assert.Equal("x", File.ReadAllText(file));
        }
        finally
        {
            Subst(letter, null);
        }

        // On the real drive it goes to the Recycle Bin.
        var recycled = Path.Combine(_root, $"filee-test-recycle-{Guid.NewGuid():N}.txt");
        File.WriteAllText(recycled, "x");
        Assert.Null(new WindowsPlatformServices().MoveToTrash(recycled));
        Assert.False(File.Exists(recycled));

        static int Subst(string drive, string? folder)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst.exe")
            {
                ArgumentList = { drive, folder ?? "/D" },
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    [Fact]
    public void A_mount_holds_only_its_own_folders()
    {
        string[] roots = ["/", "/home", "/media/usb"];

        Assert.Equal("/home", FreedesktopTrash.MountOf("/home/ana/a.png", roots));
        Assert.Equal("/", FreedesktopTrash.MountOf("/home2/ana/a.png", roots));
        Assert.Equal("/media/usb", FreedesktopTrash.MountOf("/media/usb/a.png", roots));
        Assert.Equal("/", FreedesktopTrash.MountOf("/media/usb2/a.png", roots));
    }

    [Fact]
    public void A_missing_file_gives_a_reason()
    {
        var missing = Path.Combine(_root, "gone.png");
        if (OperatingSystem.IsWindows())
            Assert.NotNull(new WindowsPlatformServices().MoveToTrash(missing));
        else if (OperatingSystem.IsMacOS())
            Assert.NotNull(new MacOSPlatformServices().MoveToTrash(missing));
        Assert.NotNull(FreedesktopTrash.Move(missing, Path.Combine(_root, "Trash")));
    }

    [Fact]
    public void The_freedesktop_Trash_records_where_the_file_came_from()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Requires Linux paths.");
            return;
        }
        var trash = Path.Combine(_root, "Trash");
        var first = Path.Combine(_root, "my photo.png");
        File.WriteAllText(first, "1");
        Assert.Null(FreedesktopTrash.Move(first, trash));
        File.WriteAllText(first, "2");
        Assert.Null(FreedesktopTrash.Move(first, trash));

        Assert.False(File.Exists(first));
        Assert.Equal("1", File.ReadAllText(Path.Combine(trash, "files", "my photo.png")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(trash, "files", "my photo 2.png")));
        var info = File.ReadAllText(Path.Combine(trash, "info", "my photo 2.png.trashinfo"));
        Assert.StartsWith("[Trash Info]\nPath=" + FreedesktopTrash.Escape(first) + "\nDeletionDate=", info);
        Assert.Contains("my%20photo.png", info);
        foreach (var folder in new[] { trash, Path.Combine(trash, "files"), Path.Combine(trash, "info") })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));

        // A folder of that name in the Trash is in the way too.
        Directory.CreateDirectory(Path.Combine(trash, "files", "my photo 3.png"));
        File.WriteAllText(first, "3");
        Assert.Null(FreedesktopTrash.Move(first, trash));
        Assert.Equal("3", File.ReadAllText(Path.Combine(trash, "files", "my photo 4.png")));
    }

    [Fact]
    public void The_macOS_Trash_takes_the_file()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS.");
            return;
        }
        var file = Path.Combine(_root, "trash-me.png");
        File.WriteAllText(file, "x");

        Assert.Null(new MacOSPlatformServices().MoveToTrash(file));
        Assert.False(File.Exists(file));
    }
}
