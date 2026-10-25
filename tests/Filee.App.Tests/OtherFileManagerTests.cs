// macOS file managers other than Finder (issue #160): drags offering NSFilenamesPboardType instead of file URLs, and
// a click-mode donut opened for another process that loses focus once as Filee becomes the active app.

using System.Text;
using Avalonia.Input;
using Filee.App.Services;
using Filee.Platform.MacOS;

namespace Filee.App.Tests;

public class OtherFileManagerTests
{
    private const string Plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><array><string>/Users/a/1.png</string><string>/Users/a/folder</string><string>/Users/a/1.png</string></array></plist>
        """;

    private static readonly HashSet<string> Files = ["/Users/a/1.png", "/Users/a/2.png"];

    private static DataTransfer Drag(string type, byte[] value)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(type), value));
        return data;
    }

    [Theory]
    [InlineData(PasteboardPaths.FilenamesType)]
    [InlineData(PasteboardPaths.FilenamesUti)]
    public void A_drag_offering_an_array_of_paths_brings_its_files(string type)
    {
        using var data = Drag(type, Encoding.UTF8.GetBytes(Plist));

        Assert.True(DraggedFiles.Offered(data));
        Assert.Equal(["/Users/a/1.png"], DraggedFiles.Local(data, Files.Contains));
    }

    [Fact]
    public void A_dragged_folder_counts_like_a_file_so_it_can_go_into_an_archive()
    {
        var folder = Directory.CreateTempSubdirectory("filee-drag-folder-").FullName;
        var file = Path.Combine(folder, "1.png");
        File.WriteAllBytes(file, [1]);
        try
        {
            Assert.True(ConversionService.Exists(file));
            Assert.True(ConversionService.Exists(folder));
            Assert.False(ConversionService.Exists(Path.Combine(folder, "missing.png")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_dragged_folder_loses_its_trailing_slash_and_a_dragged_root_is_ignored()
    {
        const string plist = """
            <plist version="1.0"><array><string>/Users/a/folder/</string><string>/</string><string>/Users/a/1.png</string></array></plist>
            """;
        using var data = Drag(PasteboardPaths.FilenamesType, Encoding.UTF8.GetBytes(plist));
        HashSet<string> existing = ["/Users/a/folder", "/", "/Users/a/1.png"];

        Assert.Equal(["/Users/a/folder", "/Users/a/1.png"], DraggedFiles.Local(data, existing.Contains));
        Assert.True(DraggedFiles.IsRoot(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.False(DraggedFiles.IsRoot(Path.GetTempPath()));
    }

    [Fact]
    public void A_drag_of_text_or_file_promises_brings_no_files_and_says_what_it_offered()
    {
        using var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("/Users/a/1.png"));
        using var promise = Drag("com.apple.NSFilePromiseItemMetaData", [1, 2, 3]);

        Assert.False(DraggedFiles.Offered(text));
        Assert.Empty(DraggedFiles.Local(text, Files.Contains));
        Assert.False(DraggedFiles.Offered(promise));
        Assert.Empty(DraggedFiles.Local(promise, Files.Contains));
        Assert.Equal("com.apple.NSFilePromiseItemMetaData", DraggedFiles.Describe(promise));
        using var empty = new DataTransfer();
        Assert.Equal("nothing", DraggedFiles.Describe(empty));
    }

    [Fact]
    public void The_first_focus_loss_soon_after_opening_for_another_process_is_forgiven_once()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var grace = new ActivationGrace(TimeSpan.FromSeconds(1), () => now);

        Assert.False(grace.Forgives()); // never started: a focus loss closes as before

        grace.Start();
        now += TimeSpan.FromMilliseconds(300);
        Assert.True(grace.Forgives());
        Assert.False(grace.Forgives()); // only once

        grace.Start();
        now += TimeSpan.FromSeconds(2);
        Assert.False(grace.Forgives()); // too late: the user clicked elsewhere

        grace.Start();
        grace.Cancel();
        Assert.False(grace.Forgives());
    }
}
