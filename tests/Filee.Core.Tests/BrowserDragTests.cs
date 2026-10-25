// What in a drag from a browser counts as a file (BrowserDrag): pictures always, links only when they point at a file
// Filee converts, never a page or a selection of text.

using Filee.Core.Web;

namespace Filee.Core.Tests;

public class BrowserDragTests
{
    private static BrowserDragContent Drag(params (string Format, string Text)[] texts) =>
        new() { Texts = texts.ToDictionary(t => t.Format, t => t.Text, StringComparer.OrdinalIgnoreCase) };

    private static IReadOnlyList<string> Names(BrowserDragContent content) =>
        BrowserDrag.Files(content).Select(f => f.Name).ToList();

    private sealed class Tracked(string name) : IDisposable
    {
        public string Name { get; } = name;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_picture_the_browser_hands_over_is_taken_and_its_link_shortcut_is_not()
    {
        // Chrome on Windows: the picture as a virtual file. A dragged link comes as "Title.url" instead.
        var picture = new Tracked("cat.webp");
        var shortcut = new Tracked("Cats - Example.url");
        var content = new BrowserDragContent
        {
            Files =
            [
                WebDropItem.FromStream(picture.Name, _ => Task.FromResult<Stream>(new MemoryStream()), owner: picture),
                WebDropItem.FromStream(shortcut.Name, _ => Task.FromResult<Stream>(new MemoryStream()), owner: shortcut),
            ],
            Texts = new Dictionary<string, string> { [BrowserDragFormats.UniformResourceLocatorW] = "https://example.com/cats" },
        };

        Assert.Equal(["cat.webp"], Names(content));
        Assert.False(picture.Disposed);
        Assert.True(shortcut.Disposed);
    }

    [Theory]
    [InlineData("https://example.com/files/Q3%20report.pdf?download=1", "Q3 report.pdf")]
    [InlineData("https://example.com/a/photo.JPG", "photo.JPG")]
    [InlineData("http://example.com/sheet.xlsx", "sheet.xlsx")]
    public void A_link_to_a_file_Filee_converts_is_a_file(string url, string name) =>
        Assert.Equal([name], Names(Drag((BrowserDragFormats.MozUrl, url + "\nTitle"))));

    [Theory]
    [InlineData("https://example.com/news/article")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/index.html")]
    [InlineData("https://example.com/view.php?id=3")]
    [InlineData("https://example.com/setup.exe")]
    [InlineData("ftp://example.com/a.pdf")]
    [InlineData("file:///C:/Users/me/a.pdf")]
    [InlineData("javascript:alert(1)")]
    public void A_link_to_a_page_a_program_or_anything_but_the_web_is_not(string url) =>
        Assert.Empty(Names(Drag((BrowserDragFormats.UniformResourceLocatorW, url))));

    [Fact]
    public void A_text_selection_is_not_a_file_even_with_a_picture_in_it()
    {
        Assert.Empty(Names(Drag((BrowserDragFormats.Html, "<p>See <img src=\"https://example.com/a.png\"> here</p>"))));
        Assert.Empty(Names(Drag()));
    }

    [Fact]
    public void A_dragged_picture_is_its_address_even_inside_a_link_and_without_an_extension()
    {
        // Firefox/Chrome on Linux and macOS: the link the picture sits in, and the picture in the HTML.
        var inLink = Drag(
            (BrowserDragFormats.MozUrl, "https://example.com/post/42\nPost"),
            (BrowserDragFormats.Html, "<a href=\"https://example.com/post/42\"><img src=\"/media/sunset.png\"></a>"));
        var item = Assert.Single(BrowserDrag.Files(inLink));
        Assert.Equal("sunset.png", item.Name);

        var noExtension = Drag(
            (BrowserDragFormats.NetscapeUrl, "https://cdn.example.com/image?id=7\nimage"),
            (BrowserDragFormats.Html, "<img src=\"https://cdn.example.com/image?id=7\">"));
        Assert.Equal(["image.jpg"], Names(noExtension)); // the bytes decide later
    }

    [Fact]
    public void An_inline_data_picture_is_decoded()
    {
        var content = Drag(
            (BrowserDragFormats.UniformResourceLocatorW, "data:image/png;base64,iVBORw0KGgo="),
            (BrowserDragFormats.Html, "<img src=\"data:image/png;base64,iVBORw0KGgo=\">"));
        Assert.Equal(["image.png"], Names(content));
    }

    [Fact]
    public void A_download_a_page_offers_is_named_as_the_page_says()
    {
        var content = Drag((BrowserDragFormats.DownloadUrlFormat, "application/pdf:Invoice 2026-10.pdf:https://mail.example.com/attachment?id=9"));
        Assert.Equal(["Invoice 2026-10.pdf"], Names(content));

        var noExtension = Drag((BrowserDragFormats.DownloadUrlFormat, "image/png:screenshot:https://example.com/dl?id=1"));
        Assert.Equal(["screenshot.png"], Names(noExtension));
    }

    [Fact]
    public void A_Firefox_file_promise_is_a_picture()
    {
        var content = Drag(
            (BrowserDragFormats.MozFilePromiseUrl, "https://example.com/render?id=3"),
            (BrowserDragFormats.MozFilePromiseName, "render.png"));
        Assert.Equal(["render.png"], Names(content));
    }

    [Fact]
    public void Several_links_give_several_files_once_each()
    {
        var content = Drag(
            (BrowserDragFormats.UriList, "https://example.com/a.pdf\r\nhttps://example.com/b.png\r\nhttps://example.com/page"),
            (BrowserDragFormats.MozUrl, "https://example.com/a.pdf\nA"));
        Assert.Equal(["a.pdf", "b.png"], Names(content));
    }

    [Fact]
    public void Pixels_only_stand_in_when_the_picture_has_no_address_Filee_can_fetch()
    {
        var asked = 0;
        WebDropItem Pixels()
        {
            asked++;
            return WebDropItem.FromBytes("image.png", [1, 2, 3]);
        }

        var blob = new BrowserDragContent
        {
            Texts = new Dictionary<string, string> { [BrowserDragFormats.UniformResourceLocatorW] = "blob:https://example.com/0b1c" },
            Picture = Pixels,
        };
        Assert.Equal(["image.png"], Names(blob));

        var fetchable = new BrowserDragContent
        {
            Texts = new Dictionary<string, string> { [BrowserDragFormats.UniformResourceLocatorW] = "https://example.com/photo.jpg" },
            Picture = Pixels,
        };
        Assert.Equal(["photo.jpg"], Names(fetchable));
        Assert.Equal(1, asked);

        // Pixels from an app that isn't a browser (no address at all) are not taken.
        Assert.Empty(Names(new BrowserDragContent { Picture = Pixels }));
    }

    [Fact]
    public void Placeholders_are_named_like_the_files_and_kept_apart()
    {
        using var drop = new WebDrop([WebDropItem.FromBytes("image.png", [1]), WebDropItem.FromBytes("image.png", [2])], WebDrop.DefaultWorkRoot);

        Assert.Equal(2, drop.Placeholders.Count);
        Assert.All(drop.Placeholders, p => Assert.Equal("image.png", Path.GetFileName(p)));
        Assert.NotEqual(drop.Placeholders[0], drop.Placeholders[1]);
        Assert.All(drop.Placeholders, p => Assert.True(WebDrop.IsPlaceholder(p)));
        Assert.All(drop.Placeholders, p => Assert.False(File.Exists(p)));
        Assert.False(WebDrop.IsPlaceholder(Path.Combine(Path.GetTempPath(), "image.png")));
    }
}
