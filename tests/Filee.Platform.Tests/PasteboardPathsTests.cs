// Paths in the forms Mac apps other than Finder put them on a pasteboard (issue #160: QSpace Pro).

using System.Text;
using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class PasteboardPathsTests
{
    private const string Filenames = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
            <string>/Users/a/사진 1.png</string>
            <string>/Users/a/R&amp;D/notes.txt</string>
            <string>relative.txt</string>
            <string></string>
        </array>
        </plist>
        """;

    [Fact]
    public void FilenamesPropertyListGivesItsAbsolutePaths()
    {
        string[] expected = ["/Users/a/사진 1.png", "/Users/a/R&D/notes.txt"];
        Assert.Equal(expected, PasteboardPaths.FromPropertyList(Filenames));
        Assert.Equal(expected, PasteboardPaths.FromPropertyList(Encoding.UTF8.GetBytes(Filenames)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<plist version=\"1.0\"><dict><key>a</key><string>/tmp/x</string></dict></plist>")]
    [InlineData("<plist><array><string>/tmp/x</string>")]
    [InlineData("/tmp/x")]
    public void AnythingButAnArrayOfPathsGivesNone(string? text) =>
        Assert.Empty(PasteboardPaths.FromPropertyList(text));

    [Fact]
    public void EmptyDataGivesNone()
    {
        Assert.Empty(PasteboardPaths.FromPropertyList((byte[]?)null));
        Assert.Empty(PasteboardPaths.FromPropertyList([]));
    }

    [Theory]
    [InlineData("file:///Users/a/b%20c.png", "/Users/a/b c.png")]
    [InlineData("file://localhost/tmp/x.png", "/tmp/x.png")]
    [InlineData("file:///Users/a/%EC%82%AC%EC%A7%84.png", "/Users/a/사진.png")]
    [InlineData(" file:///tmp/x.png\n", "/tmp/x.png")]
    [InlineData("file:///.file/id=6571367.2773272/", null)]
    [InlineData("file://server/share/x.png", null)]
    [InlineData("https://example.com/x.png", null)]
    [InlineData("/tmp/x.png", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FileUrlGivesItsPath(string? url, string? expected) =>
        Assert.Equal(expected, PasteboardPaths.FromFileUrl(url));

    [Fact]
    public void TextGivesTheLinesThatArePathsOrFileUrls()
    {
        const string text = "/tmp/a.png\nsome words\r\nfile:///tmp/b%20c.png\r\n\r\n  /tmp/d.jpg  \nC:\\x.png\n/";
        Assert.Equal(["/tmp/a.png", "/tmp/b c.png", "/tmp/d.jpg"], PasteboardPaths.FromText(text));
        Assert.Empty(PasteboardPaths.FromText(null));
        Assert.Empty(PasteboardPaths.FromText("a.png"));
    }

    [Fact]
    public void TheFirstSourceNamingAnExistingPathWinsWithoutRepeats()
    {
        var existing = new HashSet<string> { "/tmp/a.png", "/tmp/b.png", "/tmp/c.png" };
        var readLast = false;
        var paths = PasteboardPaths.FirstExisting(
        [
            () => ["/tmp/missing.png"],
            () => ["/tmp/b.png", "/tmp/gone.png", "/tmp/a.png", "/tmp/b.png"],
            () =>
            {
                readLast = true;
                return ["/tmp/c.png"];
            },
        ], existing.Contains);

        Assert.Equal(["/tmp/b.png", "/tmp/a.png"], paths);
        Assert.False(readLast);
        Assert.Empty(PasteboardPaths.FirstExisting([() => ["/tmp/missing.png"], () => []], existing.Contains));
    }

    [Fact]
    public void FilenamesTypeIsKnownByItsNameAndItsDynamicUti()
    {
        Assert.True(PasteboardPaths.IsFilenamesType("NSFilenamesPboardType"));
        Assert.True(PasteboardPaths.IsFilenamesType(PasteboardPaths.FilenamesUti));
        Assert.False(PasteboardPaths.IsFilenamesType("public.file-url"));
        Assert.Contains(PasteboardPaths.FilenamesType, PasteboardPaths.DragTypes);
        Assert.Contains(PasteboardPaths.FileUrlType, PasteboardPaths.DragTypes);
    }
}
