// E-mail (.eml) → HTML / TXT / ZIP of the attachments with MimeKit: Korean charsets (EUC-KR, ISO-2022-KR) in headers
// and bodies, quoted-printable and base64, the HTML part with an inline picture ("cid:") and attachments with
// RFC 2231 file names. Messages are written in the test.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class EmlTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static EmlTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly byte[] Report = Encoding.ASCII.GetBytes("%PDF-1.4 fake report");

    private static string EncodedWord(string text, string charset) =>
        $"=?{charset}?B?{Convert.ToBase64String(Encoding.GetEncoding(charset).GetBytes(text))}?=";

    private static string Base64Lines(byte[] bytes) =>
        string.Join("\r\n", Convert.ToBase64String(bytes).Chunk(76).Select(c => new string(c)));

    /// <summary>Quoted-printable of EUC-KR text (every non-ASCII byte as =XX).</summary>
    private static string QuotedPrintable(string text, string charset)
    {
        var sb = new StringBuilder();
        var column = 0;
        foreach (var b in Encoding.GetEncoding(charset).GetBytes(text))
        {
            var piece = b is >= 33 and <= 126 and not (byte)'=' ? ((char)b).ToString() : $"={b:X2}";
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

    /// <summary>
    /// multipart/mixed: multipart/related (multipart/alternative text + HTML in EUC-KR, an inline PNG) and two
    /// attachments, one with an RFC 2231 UTF-8 file name.
    /// </summary>
    private async Task<string> MessageAsync()
    {
        using var logo = new MagickImage(MagickColors.Teal, 16, 16);
        var png = logo.ToByteArray(MagickFormat.Png);
        const string Html = "<html><head><style>p { color: #C00000; }</style></head><body><p>안녕하세요. 자료를 보냅니다.</p>" +
                            "<img src=\"cid:logo@filee\" alt=\"logo\"><script>alert('x')</script></body></html>";
        var eml = string.Join("\r\n",
            $"From: {EncodedWord("보낸이", "EUC-KR")} <sender@example.com>",
            "To: receiver@example.com, \"Second\" <second@example.com>",
            "Cc: other@example.com",
            $"Subject: {EncodedWord("회의 자료", "EUC-KR")}",
            "Date: Thu, 01 Oct 2026 09:30:00 +0900",
            "MIME-Version: 1.0",
            "Content-Type: multipart/mixed; boundary=\"mixed\"",
            "",
            "--mixed",
            "Content-Type: multipart/related; boundary=\"related\"",
            "",
            "--related",
            "Content-Type: multipart/alternative; boundary=\"alt\"",
            "",
            "--alt",
            "Content-Type: text/plain; charset=euc-kr",
            "Content-Transfer-Encoding: base64",
            "",
            Base64Lines(Encoding.GetEncoding("EUC-KR").GetBytes("안녕하세요. 자료를 보냅니다.\r\n둘째 줄")),
            "--alt",
            "Content-Type: text/html; charset=euc-kr",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            QuotedPrintable(Html, "EUC-KR"),
            "--alt--",
            "--related",
            "Content-Type: image/png",
            "Content-ID: <logo@filee>",
            "Content-Transfer-Encoding: base64",
            "",
            Base64Lines(png),
            "--related--",
            "--mixed",
            "Content-Type: application/pdf; name=\"report.pdf\"",
            "Content-Disposition: attachment; filename*=UTF-8''%EB%B3%B4%EA%B3%A0%EC%84%9C.pdf",
            "Content-Transfer-Encoding: base64",
            "",
            Base64Lines(Report),
            "--mixed",
            "Content-Type: text/plain; charset=utf-8; name=\"notes.txt\"",
            "Content-Disposition: attachment; filename=\"notes.txt\"",
            "",
            "memo",
            "--mixed--",
            "");
        var path = Path.Combine(fx.NewFolder(), "회의.eml");
        await File.WriteAllTextAsync(path, eml, Encoding.ASCII, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public async Task Eml_to_html_shows_headers_and_the_html_body_with_inline_pictures()
    {
        var html = await File.ReadAllTextAsync(await ConvertAsync(await MessageAsync(), "html"), TestContext.Current.CancellationToken);

        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>회의 자료</title>", html);
        Assert.Contains("<th>From</th><td>보낸이 &lt;sender@example.com&gt;</td>", html);
        Assert.Contains("<th>To</th><td>receiver@example.com, Second &lt;second@example.com&gt;</td>", html);
        Assert.Contains("<th>Date</th><td>2026-10-01 09:30:00 +09:00</td>", html);
        Assert.Contains("<th>Attachments</th><td>보고서.pdf, notes.txt</td>", html);
        Assert.Contains("<p>안녕하세요. 자료를 보냅니다.</p>", html);
        Assert.Contains("p { color: #C00000; }", html); // the message's own style sheet
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("cid:", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task Eml_to_text_and_attachments_to_zip()
    {
        var eml = await MessageAsync();

        var text = await File.ReadAllTextAsync(await ConvertAsync(eml, "txt"), TestContext.Current.CancellationToken);
        Assert.StartsWith("From: 보낸이 <sender@example.com>\r\n", text.TrimStart('﻿'));
        Assert.Contains("Subject: 회의 자료\r\n", text);
        Assert.EndsWith("\r\n\r\n안녕하세요. 자료를 보냅니다.\r\n둘째 줄\r\n", text);

        using var zip = ZipFile.OpenRead(await ConvertAsync(eml, "zip"));
        Assert.Equal(["보고서.pdf", "notes.txt"], zip.Entries.Select(e => e.FullName));
        using var report = new MemoryStream();
        using (var stream = zip.GetEntry("보고서.pdf")!.Open())
            stream.CopyTo(report);
        Assert.Equal(Report, report.ToArray());
    }

    [Fact]
    public async Task Iso_2022_kr_and_plain_text_messages()
    {
        var subject = EncodedWord("한글 제목", "ISO-2022-KR");
        var body = Encoding.ASCII.GetString(Encoding.GetEncoding("ISO-2022-KR").GetBytes("본문 <첫 줄>\r\n둘째 줄"));
        var path = Path.Combine(fx.NewFolder(), "old.eml");
        await File.WriteAllTextAsync(path,
            $"From: old@example.com\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=ISO-2022-KR\r\nContent-Transfer-Encoding: 7bit\r\n\r\n{body}\r\n",
            Encoding.ASCII, TestContext.Current.CancellationToken);

        var html = await File.ReadAllTextAsync(await ConvertAsync(path, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("<title>한글 제목</title>", html);
        Assert.Contains("<pre class=\"filee-mail-text\">본문 &lt;첫 줄&gt;\n둘째 줄", html.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Attachments", html);

        // No attachments: a clear error instead of an empty ZIP.
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "zip" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no attachments", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);
    }
}
