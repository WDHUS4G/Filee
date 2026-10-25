// MHTML web archives (.mhtml / .mht) → HTML (one file, pictures as data URIs), DOCX, HWPX, PDF and text. The archives
// are written in the test the way Chrome / Edge (multipart/related, Content-Location URLs, "cid:" style sheets,
// quoted-printable page without a charset parameter) and Word (file:///C:/… locations, windows-1252, VML fallbacks)
// save them, plus a cid:-only archive in EUC-KR.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Email;
using ImageMagick;

namespace Filee.Engines.Tests;

public class MhtmlTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static MhtmlTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre"];

    private static readonly byte[] Photo = EbookBuilders.Png(40, 30, MagickColors.Teal);
    private static readonly byte[] Banner = EbookBuilders.Png(50, 10, MagickColors.Orange);
    private static readonly byte[] Secret = EbookBuilders.Png(12, 12, MagickColors.Red);

    private static string Base64Lines(byte[] bytes) =>
        string.Join("\r\n", Convert.ToBase64String(bytes).Chunk(76).Select(c => new string(c)));

    /// <summary>Quoted-printable of <paramref name="text"/> in <paramref name="charset"/>, lines kept under 76.</summary>
    private static string QuotedPrintable(string text, string charset)
    {
        var sb = new StringBuilder();
        var column = 0;
        foreach (var b in Encoding.GetEncoding(charset).GetBytes(text))
        {
            if (b == '\n')
            {
                sb.Append("\r\n");
                column = 0;
                continue;
            }
            var piece = b is >= 33 and <= 126 and not (byte)'=' || b == ' ' ? ((char)b).ToString() : $"={b:X2}";
            if (column + piece.Length > 72)
            {
                sb.Append("=\r\n");
                column = 0;
            }
            sb.Append(piece);
            column += piece.Length;
        }
        return sb.ToString();
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllTextAsync(path, content, Encoding.ASCII, TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// As Chrome saves "Webpage, Single File": the page (quoted-printable, charset only in its meta tag) refers to a
    /// style sheet by "cid:", to pictures by absolute and relative URL, and to things the archive does not hold.
    /// </summary>
    private Task<string> ChromeArchiveAsync(string? secretFile = null)
    {
        var html = $$"""
            <!DOCTYPE html><html lang="ko"><head><meta http-equiv="Content-Type" content="text/html; charset=UTF-8">
            <base href="https://example.com/news/">
            <link rel="stylesheet" type="text/css" href="cid:css-1@mhtml.blink" />
            <link rel="stylesheet" href="https://cdn.example.net/remote.css">
            <title>오늘의 기사</title><script src="https://example.com/app.js"></script></head>
            <body class="article" onload="alert(1)">
            <h1>큰 제목</h1>
            <p class="hero">본문 첫 문단입니다. 카페 메뉴 – “라떼”.</p>
            <p><img src="https://example.com/img/photo.png" alt="사진"></p>
            <p><img src="../img/banner.png" alt="배너"></p>
            <p><img src="https://tracker.example/pixel.gif" alt="추적"></p>
            <p><img src="{{secretFile ?? "secret.png"}}" alt="비밀"></p>
            <iframe src="https://ads.example/frame"></iframe>
            <script>document.write('injected')</script>
            <p><a href="https://example.com/news/other.html">다른 기사</a> · <a href="article.html#comments">댓글</a>
            · <a href="javascript:alert(1)">나쁜 링크</a></p>
            <p id="comments">댓글 영역</p>
            </body></html>
            """;
        const string Css = """
            @charset "utf-8";
            h1 { color: rgb(200, 0, 0); }
            .hero { background-image: url("https://example.com/img/banner.png"); }
            .remote { background: url(https://tracker.example/bg.png) no-repeat; }
            @font-face { font-family: Remote; src: url(https://fonts.example/x.woff2) format("woff2"); }
            .escape { content: "</style><script>alert(1)</script>"; }
            """;
        return WriteAsync("기사.mhtml", string.Join("\r\n",
            "From: <Saved by Blink>",
            "Snapshot-Content-Location: https://example.com/news/article.html",
            "Subject: =?utf-8?Q?=EC=98=A4=EB=8A=98=EC=9D=98_=EA=B8=B0=EC=82=AC?=",
            "Date: Tue, 20 Oct 2026 10:00:00 +0900",
            "MIME-Version: 1.0",
            "Content-Type: multipart/related;",
            "\ttype=\"text/html\";",
            "\tboundary=\"----MultipartBoundary--abc----\"",
            "",
            "",
            "------MultipartBoundary--abc----",
            "Content-Type: text/html",
            "Content-ID: <frame-1@mhtml.blink>",
            "Content-Transfer-Encoding: quoted-printable",
            "Content-Location: https://example.com/news/article.html",
            "",
            QuotedPrintable(html.ReplaceLineEndings("\n"), "utf-8"),
            "------MultipartBoundary--abc----",
            "Content-Type: text/css",
            "Content-Transfer-Encoding: quoted-printable",
            "Content-Location: cid:css-1@mhtml.blink",
            "",
            QuotedPrintable(Css.ReplaceLineEndings("\n"), "utf-8"),
            "------MultipartBoundary--abc----",
            "Content-Type: image/png",
            "Content-Transfer-Encoding: base64",
            "Content-Location: https://example.com/img/photo.png",
            "",
            Base64Lines(Photo),
            "------MultipartBoundary--abc----",
            "Content-Type: application/octet-stream",
            "Content-Transfer-Encoding: base64",
            "Content-Location: https://example.com/img/banner.png",
            "",
            Base64Lines(Banner),
            "------MultipartBoundary--abc------",
            ""));
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner(assumeInstalled);

    [Fact]
    public void Mhtml_is_a_read_only_text_format_with_both_extensions()
    {
        Assert.Equal("mhtml", FormatRegistry.FindByExtension("mht")!.Id);
        Assert.Equal("mhtml", FormatRegistry.Detect("page.MHTML")!.Id);
        Assert.False(FormatRegistry.Get("mhtml").Writable);
        Assert.Equal(FormatCategory.Text, FormatRegistry.Get("mhtml").Category);
        var text = BuiltInData.CreateProfiles().Single(p => p.Id == "text");
        Assert.Contains("mhtml", text.Extensions);
        Assert.Contains("mht", text.Extensions);
    }

    [Fact]
    public void Every_html_target_is_reachable_without_optional_engines()
    {
        var planner = BuiltInPlanner("rhwp");

        Assert.Equal("email", Assert.Single(planner.Plan("mhtml", "html")!.Steps).Converter.Id);
        Assert.Equal("docx-writer", Assert.Single(planner.Plan("mhtml", "docx")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("mhtml", "hwpx")!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("mhtml", "pdf")!.Steps.Select(s => s.Converter.Id));
        foreach (var target in new[] { "txt", "md", "epub", "hwp" })
            Assert.NotNull(planner.Plan("mhtml", target));
    }

    [Fact]
    public async Task Chrome_archive_becomes_one_safe_html_file_with_its_pictures()
    {
        var html = await File.ReadAllTextAsync(await ConvertAsync(await ChromeArchiveAsync(), "html"), TestContext.Current.CancellationToken);

        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"ko\">", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("<title>오늘의 기사</title>", html);
        Assert.Contains("<body class=\"article\">", html);
        Assert.Contains("<h1>큰 제목</h1>", html);
        Assert.Contains("카페 메뉴 – “라떼”.", html); // quoted-printable UTF-8, the charset only in the page's meta tag

        // Pictures of the archive: by absolute URL, by URL relative to <base>, from CSS, saved as octet-stream.
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Photo)}\"", html);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Banner)}\"", html);
        Assert.Contains($".hero {{ background-image: url(\"data:image/png;base64,{Convert.ToBase64String(Banner)}\"); }}", html);
        Assert.Contains("h1 { color: rgb(200, 0, 0); }", html); // the "cid:" style sheet, inlined

        // Nothing loads from elsewhere, nothing runs.
        Assert.DoesNotContain("tracker.example", html);
        Assert.DoesNotContain("fonts.example", html);
        Assert.DoesNotContain("cdn.example.net", html);
        Assert.DoesNotContain("ads.example", html);
        Assert.DoesNotContain("app.js", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<base", html);
        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("cid:", html);
        Assert.DoesNotContain("injected", html);
        Assert.DoesNotContain("</style><script>", html); // a style sheet cannot close its <style> element
        Assert.Contains("src: none format(\"woff2\")", html);
        Assert.DoesNotContain("src=\"secret.png\"", html);

        // Links: absolute, and the page's own anchors stay in the page.
        Assert.Contains("href=\"https://example.com/news/other.html\"", html);
        Assert.Contains("href=\"#comments\"", html);
    }

    [Fact]
    public async Task Content_location_never_opens_a_local_file()
    {
        // A real picture on disk, named by a file: URI and by a path, that the archive does not hold.
        var folder = fx.NewFolder();
        var secret = Path.Combine(folder, "secret.png");
        await File.WriteAllBytesAsync(secret, Secret, TestContext.Current.CancellationToken);
        var archive = await ChromeArchiveAsync(new Uri(secret).AbsoluteUri);
        File.Copy(secret, Path.Combine(Path.GetDirectoryName(archive)!, "secret.png"));

        var html = await File.ReadAllTextAsync(await ConvertAsync(archive, "html"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Convert.ToBase64String(Secret), html);
        Assert.DoesNotContain("secret.png", html);

        // The document writers see only the archived pictures too.
        var docx = await ConvertAsync(archive, "docx");
        DocxAssert.ValidPackage(docx);
        using var zip = ZipFile.OpenRead(docx);
        var media = zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, media.Count);
        foreach (var entry in media)
        {
            using var stream = entry.Open();
            using var image = new MagickImage(stream);
            Assert.NotEqual(12u, image.Width);
        }
    }

    [Fact]
    public async Task Word_mht_with_file_locations_and_windows_1252()
    {
        const string Html = """
            <html xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office">
            <head><meta http-equiv=Content-Type content="text/html; charset=windows-1252">
            <link rel=File-List href="report_files/filelist.xml">
            <style><!-- p.MsoNormal { margin: 0in; font-family: "Calibri"; } --></style></head>
            <body lang=EN-US style='tab-interval:.5in'>
            <p class=MsoNormal>Café crème – naïve</p>
            <p class=MsoNormal><!--[if gte vml 1]><v:shape><v:imagedata src="report_files/image001.png" o:title=""/></v:shape><![endif]--><![if !vml]><img width=40 height=30 src="report_files/image002.png" v:shapes="Picture_x0020_1"><![endif]></p>
            </body></html>
            """;
        var path = await WriteAsync("report.mht", string.Join("\r\n",
            "MIME-Version: 1.0",
            "Content-Type: multipart/related; boundary=\"----=_NextPart_01DB0000.12345678\"",
            "",
            "This document is a Single File Web Page, also known as a Web Archive file.",
            "",
            "------=_NextPart_01DB0000.12345678",
            "Content-Location: file:///C:/D0C1A2B3/report.htm",
            "Content-Transfer-Encoding: quoted-printable",
            "Content-Type: text/html; charset=\"windows-1252\"",
            "",
            QuotedPrintable(Html.ReplaceLineEndings("\n"), "windows-1252"),
            "",
            "------=_NextPart_01DB0000.12345678",
            "Content-Location: file:///C:/D0C1A2B3/report_files/image002.png",
            "Content-Transfer-Encoding: base64",
            "Content-Type: image/png",
            "",
            Base64Lines(Photo),
            "",
            "------=_NextPart_01DB0000.12345678",
            "Content-Location: file:///C:/D0C1A2B3/report_files/filelist.xml",
            "Content-Transfer-Encoding: quoted-printable",
            "Content-Type: text/xml; charset=\"utf-8\"",
            "",
            "<xml></xml>",
            "------=_NextPart_01DB0000.12345678--",
            ""));

        var html = await File.ReadAllTextAsync(await ConvertAsync(path, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("Café crème – naïve", html);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Photo)}\"", html);
        Assert.Contains("<body lang=\"EN-US\" style=\"tab-interval:.5in\">", html);
        Assert.DoesNotContain("file:", html);
        Assert.DoesNotContain("<v:", html);

        var text = await File.ReadAllTextAsync(await ConvertAsync(path, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("Café crème – naïve", text);
    }

    [Fact]
    public async Task Cid_references_in_euc_kr_base64_page_and_docx()
    {
        const string Html = "<html><head><title>사진 보고서</title></head><body><h1>보고서</h1><p>첫 문단에 한글이 있습니다.</p>" +
                            "<p><img src=\"cid:photo@archive\" alt=\"사진\"></p><p><img src=\"cid:missing@archive\"></p>" +
                            "<p><img src=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=\"></p></body></html>";
        var path = await WriteAsync("보고서.mht", string.Join("\r\n",
            "MIME-Version: 1.0",
            "Content-Type: multipart/related; type=\"text/html\"; start=\"<page@archive>\"; boundary=\"b1\"",
            "",
            "--b1",
            "Content-Type: image/png",
            "Content-ID: <photo@archive>",
            "Content-Transfer-Encoding: base64",
            "",
            Base64Lines(Photo),
            "--b1",
            "Content-Type: text/html; charset=euc-kr",
            "Content-ID: <page@archive>",
            "Content-Transfer-Encoding: base64",
            "",
            Base64Lines(Encoding.GetEncoding("euc-kr").GetBytes(Html)),
            "--b1--",
            ""));

        var html = await File.ReadAllTextAsync(await ConvertAsync(path, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("<title>사진 보고서</title>", html);
        Assert.Contains("<p>첫 문단에 한글이 있습니다.</p>", html);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Photo)}\"", html);
        Assert.DoesNotContain("cid:", html);
        Assert.DoesNotContain("svg", html);

        var docx = await ConvertAsync(path, "docx");
        DocxAssert.ValidPackage(docx);
        var document = DocxAssert.ReadBack(docx);
        var paragraphs = DocxAssert.Paragraphs(document.Sections.SelectMany(s => s.Blocks)).Select(DocxAssert.TextOf).ToList();
        Assert.Contains("보고서", paragraphs);
        Assert.Contains("첫 문단에 한글이 있습니다.", paragraphs);
        using var zip = ZipFile.OpenRead(docx);
        Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archive_without_a_page_and_oversized_archives_fail_clearly()
    {
        var empty = await WriteAsync("empty.mhtml", "MIME-Version: 1.0\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Base64Lines(Photo) + "\r\n");
        var job = await fx.ConvertAsync([empty], new Preset { TargetFormat = "html" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no HTML page", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);

        var huge = Path.Combine(fx.NewFolder(), "huge.mhtml");
        using (var file = File.Create(huge))
            file.SetLength(MhtmlArchive.MaxFileBytes + 1);
        var error = Assert.Throws<InvalidDataException>(() => MhtmlArchive.Load(huge, TestContext.Current.CancellationToken));
        Assert.Contains("larger than", error.Message);
    }

    [Fact]
    public async Task Mhtml_to_hwpx_and_pdf()
    {
        var archive = await ChromeArchiveAsync();
        var hwpx = await ConvertAsync(archive, "hwpx");
        var xml = HwpxAssert.Xml(hwpx).ToString();
        Assert.Contains("큰 제목", xml);
        Assert.DoesNotContain("injected", xml);

        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var pdf = await ConvertAsync(archive, "pdf");
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken))[..4]));
    }
}
