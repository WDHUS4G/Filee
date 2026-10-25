// The forms browsers put dragged pictures, links and downloads into (BrowserDragFormats), the names files from the web
// get (WebFileNames) and what they are taken for before and after their bytes are known (WebFormats).

using System.Runtime.InteropServices;
using System.Text;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class BrowserDragFormatsTests
{
    /// <summary>FILEDESCRIPTORW as shlobj.h declares it, to check the parser's offsets against the real layout.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FileDescriptorW
    {
        public uint dwFlags;
        public Guid clsid;
        public long sizel;
        public long pointl;
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    private static byte[] Native(params FileDescriptorW[] entries)
    {
        var size = Marshal.SizeOf<FileDescriptorW>();
        Assert.Equal(592, size);
        var data = new byte[4 + entries.Length * size];
        BitConverter.GetBytes((uint)entries.Length).CopyTo(data, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < entries.Length; i++)
            {
                Marshal.StructureToPtr(entries[i], buffer, false);
                Marshal.Copy(buffer, data, 4 + i * size, size);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return data;
    }

    [Fact]
    public void Reads_names_sizes_and_folders_from_a_native_FileGroupDescriptorW()
    {
        var data = Native(
            new FileDescriptorW { dwFlags = 0x40, nFileSizeHigh = 1, nFileSizeLow = 5, cFileName = "사진 1.jpg" },
            new FileDescriptorW { dwFlags = 0x4, dwFileAttributes = 0x10, cFileName = "album" },
            new FileDescriptorW { dwFlags = 0, cFileName = "Example Domain.url" });

        var entries = BrowserDragFormats.ParseFileGroupDescriptor(data);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new FileDescriptor("사진 1.jpg", (1L << 32) + 5, false), entries[0]);
        Assert.Equal(new FileDescriptor("album", null, true), entries[1]);
        Assert.Equal("Example Domain.url", entries[2].Name);
    }

    [Fact]
    public void A_descriptor_cut_short_or_absurd_gives_nothing()
    {
        var data = BrowserDragFormats.BuildFileGroupDescriptor([new FileDescriptor("a.png", 3, false), new FileDescriptor("b.png", null, false)]);
        Assert.Equal(2, BrowserDragFormats.ParseFileGroupDescriptor(data).Count);
        Assert.Empty(BrowserDragFormats.ParseFileGroupDescriptor(data.AsSpan(0, data.Length - 1)));
        Assert.Empty(BrowserDragFormats.ParseFileGroupDescriptor([0xFF, 0xFF, 0xFF, 0x7F]));
        Assert.Empty(BrowserDragFormats.ParseFileGroupDescriptor([]));
    }

    [Fact]
    public void Reads_DownloadURL_whose_address_has_colons()
    {
        var download = BrowserDragFormats.ParseDownloadUrl("application/pdf:Q3 report.pdf:https://mail.example.com:8443/attachment?id=1:2");

        Assert.NotNull(download);
        Assert.Equal("application/pdf", download.Mime);
        Assert.Equal("Q3 report.pdf", download.Name);
        Assert.Equal("https://mail.example.com:8443/attachment?id=1:2", download.Url.OriginalString);
        Assert.Null(BrowserDragFormats.ParseDownloadUrl("https://example.com/a.pdf"));
        Assert.Null(BrowserDragFormats.ParseDownloadUrl("pdf:a.pdf:https://example.com/a.pdf")); // no media type
        Assert.Null(BrowserDragFormats.ParseDownloadUrl("application/pdf::https://example.com/a.pdf")); // no name
    }

    [Fact]
    public void Reads_uri_lists_without_comments()
    {
        var urls = BrowserDragFormats.ParseUriList("# dragged from Firefox\r\nhttps://example.com/a.png\r\n\r\nhttps://example.com/b.pdf\r\n");
        Assert.Equal(["https://example.com/a.png", "https://example.com/b.pdf"], urls);
    }

    [Fact]
    public void Reads_moz_url_pairs_from_UTF16_bytes_padded_with_NULs()
    {
        var bytes = Encoding.Unicode.GetBytes("https://example.com/a.png\nPicture A\nhttps://example.com/b.pdf\nReport\0\0\0");
        var text = BrowserDragFormats.DecodeText(bytes);

        Assert.Equal(["https://example.com/a.png", "https://example.com/b.pdf"], BrowserDragFormats.ParseMozUrl(text));
    }

    [Theory]
    [InlineData(new byte[] { 0x68, 0x00, 0x69, 0x00 }, "hi")]                    // UTF-16 LE
    [InlineData(new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00 }, "hi")]        // with BOM
    [InlineData(new byte[] { 0x68, 0x69, 0x00, 0x00 }, "hi")]                    // UTF-8, padded
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xED, 0x95, 0x9C }, "한")]          // UTF-8 with BOM
    [InlineData(new byte[] { 0xED, 0x95, 0x9C, 0x2E, 0x70, 0x6E, 0x67 }, "한.png")] // UTF-8
    public void Decodes_text_the_way_each_platform_writes_it(byte[] bytes, string expected) =>
        Assert.Equal(expected, BrowserDragFormats.DecodeText(bytes));

    [Fact]
    public void Reads_the_address_of_an_internet_shortcut()
    {
        Assert.Equal("https://example.com/", BrowserDragFormats.ParseInternetShortcut("[InternetShortcut]\r\nURL=https://example.com/\r\n"));
        Assert.Null(BrowserDragFormats.ParseInternetShortcut("[InternetShortcut]"));
    }

    [Fact]
    public void A_picture_is_an_HTML_fragment_of_one_image_and_no_text()
    {
        const string windowsHtml = "Version:0.9\r\nStartHTML:000000105\r\nEndHTML:000000300\r\n<html><body>\r\n<!--StartFragment-->" +
                                   "<a href=\"https://example.com/post\"><img src=\"https://cdn.example.com/p/cat.webp?w=800&amp;h=600\" alt=\"A cat\"></a>" +
                                   "<!--EndFragment-->\r\n</body></html>";

        Assert.Equal("https://cdn.example.com/p/cat.webp?w=800&h=600", BrowserDragFormats.ImageOnly(windowsHtml));
        Assert.Equal("/img/dog.png", BrowserDragFormats.ImageOnly("<meta charset='utf-8'><img src='/img/dog.png'>"));
        Assert.Equal("data:image/png;base64,iVBO", BrowserDragFormats.ImageOnly("<img src=data:image/png;base64,iVBO>"));
        // A selection of text with a picture in it, or two pictures, is not "a picture".
        Assert.Null(BrowserDragFormats.ImageOnly("<p>Look at this <img src=\"a.png\"> cat</p>"));
        Assert.Null(BrowserDragFormats.ImageOnly("<img src=\"a.png\"><img src=\"b.png\">"));
        Assert.Null(BrowserDragFormats.ImageOnly("<p>Just text</p>"));
        Assert.Null(BrowserDragFormats.ImageOnly(null));
    }

    [Fact]
    public void Decodes_data_addresses()
    {
        var png = BrowserDragFormats.DecodeDataUrl("data:image/png;base64,iVBORw0KGgo=");
        Assert.NotNull(png);
        Assert.Equal("image/png", png.Value.Mime);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png.Value.Bytes);

        var svg = BrowserDragFormats.DecodeDataUrl("data:image/svg+xml,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%2F%3E");
        Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", Encoding.UTF8.GetString(svg!.Value.Bytes));

        Assert.Null(BrowserDragFormats.DecodeDataUrl("data:image/png;base64,***"));
        Assert.Null(BrowserDragFormats.DecodeDataUrl("https://example.com/a.png"));
        Assert.Null(BrowserDragFormats.DecodeDataUrl("data:image/png;base64,iVBORw0KGgo=", maxBytes: 4));
    }

    [Theory]
    [InlineData("../../Windows/System32/evil.png", "evil.png")]
    [InlineData(@"C:\Users\me\photo.jpg", "photo.jpg")]
    [InlineData("a<b>:c|d?.jpg", "a_b__c_d_.jpg")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("lpt1", "_lpt1")]
    [InlineData("  report.pdf. . ", "report.pdf")]
    [InlineData(".hidden", "hidden")]
    [InlineData("", "download")]
    [InlineData("...", "download")]
    [InlineData("tab\there.png", "tab_here.png")]
    public void File_names_from_the_web_are_made_safe(string name, string expected) =>
        Assert.Equal(expected, WebFileNames.Sanitize(name));

    [Fact]
    public void A_long_name_is_cut_and_keeps_its_extension()
    {
        var name = WebFileNames.Sanitize(new string('가', 300) + ".pdf");
        Assert.Equal(WebFileNames.MaxLength, name.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE ", true)]
    [InlineData("install.msi", true)]
    [InlineData("run.ps1", true)]
    [InlineData("Filee.dmg", true)]
    [InlineData("app.jar", true)]
    [InlineData("Example.url", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("backup.tar.gz", false)]
    [InlineData("noextension", false)]
    public void Programs_installers_and_scripts_are_blocked(string name, bool blocked) =>
        Assert.Equal(blocked, WebFileNames.IsBlocked(name));

    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=plain.png", "plain.png")]
    [InlineData("attachment; filename=\"fallback.pdf\"; filename*=UTF-8''%ED%95%9C%EA%B8%80.pdf", "한글.pdf")]
    [InlineData("inline", null)]
    [InlineData("", null)]
    public void Reads_the_name_a_server_gives(string header, string? expected) =>
        Assert.Equal(expected, WebFileNames.FromContentDisposition(header));

    [Fact]
    public void Reads_the_name_an_address_ends_in()
    {
        Assert.Equal("my photo.jpg", WebFileNames.FromUrl(new Uri("https://example.com/a/my%20photo.jpg?w=1#x")));
        Assert.Null(WebFileNames.FromUrl(new Uri("https://example.com/")));
    }

    [Fact]
    public void Files_are_created_under_a_free_name_and_never_replace_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.pdf"), "old");

        using (var first = WebFileNames.CreateUnique(dir.Path, "a.pdf", out var path))
            Assert.Equal(Path.Combine(dir.Path, "a (2).pdf"), path);
        using (WebFileNames.CreateUnique(dir.Path, "a.pdf", out var path))
            Assert.Equal(Path.Combine(dir.Path, "a (3).pdf"), path);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir.Path, "a.pdf")));

        var source = dir.File("incoming.tmp", "new");
        Assert.Equal(Path.Combine(dir.Path, "a (4).pdf"), WebFileNames.MoveUnique(source, dir.Path, "a.pdf"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir.Path, "a.pdf")));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "pdf")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 }, "avif")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x69, 0x66, 0x31, 0, 0, 0, 0, 0x61, 0x76, 0x69, 0x66 }, "avif")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "heic")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null)] // a ZIP may be a DOCX: not guessed
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, null)]
    public void Recognises_formats_by_their_first_bytes(byte[] head, string? expected) =>
        Assert.Equal(expected, WebFormats.Sniff(head));

    [Fact]
    public void Programs_and_pages_are_recognised_by_their_first_bytes()
    {
        Assert.True(WebFormats.LooksLikeProgram("MZ\x90\0"u8));
        Assert.True(WebFormats.LooksLikeProgram("#!/bin/sh\n"u8));
        Assert.False(WebFormats.LooksLikeProgram("%PDF-1.7"u8));
        Assert.True(WebFormats.LooksLikePage("\n  <!DOCTYPE html><html>"u8));
        Assert.False(WebFormats.LooksLikePage("%PDF-1.7"u8));
    }

    [Theory]
    [InlineData("photo.jpg", "webp", null, "photo.webp")]     // the address said JPG, the server sent WebP
    [InlineData("photo.jpeg", "jpg", null, "photo.jpeg")]     // the same format, another extension: kept
    [InlineData("download", "pdf", null, "download.pdf")]
    [InlineData("report.docx", null, null, "report.docx")]    // can't be sniffed: kept
    [InlineData("export", null, "application/pdf", "export.pdf")]
    [InlineData("export", null, "image/png; charset=binary", "export.png")]
    public void A_name_follows_what_the_file_really_is(string name, string? sniffed, string? mime, string expected)
    {
        byte[] head = sniffed switch
        {
            "webp" => [0x52, 0x49, 0x46, 0x46, 1, 0, 0, 0, 0x57, 0x45, 0x42, 0x50],
            "jpg" => [0xFF, 0xD8, 0xFF, 0xE0],
            "pdf" => "%PDF-1.4"u8.ToArray(),
            _ => [0x50, 0x4B, 0x03, 0x04],
        };
        Assert.Equal(expected, WebFormats.NameFor(name, head, mime));
    }

    [Fact]
    public void Media_types_map_to_extensions()
    {
        Assert.Equal("jpg", WebFormats.ExtensionForMime("image/JPEG"));
        Assert.Equal("docx", WebFormats.ExtensionForMime("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        Assert.Null(WebFormats.ExtensionForMime("application/octet-stream"));
        Assert.True(WebFormats.IsPage("text/html; charset=utf-8"));
        Assert.True(WebFormats.IsProgram("application/x-msdownload"));
    }
}
