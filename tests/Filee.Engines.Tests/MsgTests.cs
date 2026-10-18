// Outlook .msg → EML / HTML / TXT / ZIP: MsgReader reads the message, which is rebuilt as MIME and goes the EML way.
// Messages are written in the test (MsgBuilder).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using MimeKit;

namespace Filee.Engines.Tests;

public class MsgTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly byte[] Report = Encoding.ASCII.GetBytes("%PDF-1.4 fake report");

    private string Message()
    {
        using var logo = new MagickImage(MagickColors.Teal, 16, 16);
        var builder = new MsgBuilder
        {
            Subject = "회의 자료",
            SenderName = "보낸이",
            SenderEmail = "sender@example.com",
            Body = "안녕하세요. 자료를 보냅니다.\r\n둘째 줄",
            Html = "<html><head><style>p { color: #C00000; }</style></head><body><p>안녕하세요. 자료를 보냅니다.</p>" +
                   "<img src=\"cid:logo@filee\"></body></html>",
            MessageId = "<meeting-1@example.com>",
            Recipients = { ("Receiver", "receiver@example.com", 1), ("Copy", "copy@example.com", 2) },
            Attachments =
            {
                ("보고서.pdf", Report, "application/pdf", null),
                ("logo.png", logo.ToByteArray(MagickFormat.Png), "image/png", "logo@filee"),
            },
            AttachedMessages = { new MsgBuilder { Subject = "Earlier mail", SenderName = "Old", SenderEmail = "old@example.com", Body = "older text" } },
        };
        var path = Path.Combine(fx.NewFolder(), "회의.msg");
        builder.Save(path);
        return path;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public async Task Msg_to_eml_rebuilds_the_mime_message()
    {
        var eml = await ConvertAsync(Message(), "eml");
        var mime = await MimeMessage.LoadAsync(eml, TestContext.Current.CancellationToken);

        Assert.Equal("회의 자료", mime.Subject);
        var from = mime.From.Mailboxes.Single();
        Assert.Equal(("보낸이", "sender@example.com"), (from.Name, from.Address));
        Assert.Equal("receiver@example.com", mime.To.Mailboxes.Single().Address);
        Assert.Equal("copy@example.com", mime.Cc.Mailboxes.Single().Address);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.Zero), mime.Date.ToUniversalTime());
        Assert.Equal("meeting-1@example.com", mime.MessageId);
        // MimeKit writes the EML with the platform's line endings (LF on macOS and Linux).
        Assert.Equal("안녕하세요. 자료를 보냅니다.\n둘째 줄", mime.TextBody.TrimEnd().ReplaceLineEndings("\n"));
        Assert.Contains("<img src=\"cid:logo@filee\">", mime.HtmlBody);

        var attachments = mime.Attachments.ToList();
        Assert.Equal(2, attachments.Count); // the PDF and the attached message; the logo is part of the body
        var pdf = Assert.IsType<MimePart>(attachments[0]);
        Assert.Equal("보고서.pdf", pdf.FileName);
        using var data = new MemoryStream();
        await pdf.Content.DecodeToAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(Report, data.ToArray());
        var attached = Assert.IsType<MessagePart>(attachments[1]);
        Assert.Equal("Earlier mail", attached.Message.Subject);
        Assert.Equal("older text", attached.Message.TextBody.TrimEnd());
        Assert.Contains(mime.BodyParts.OfType<MimePart>(), p => p.ContentId == "logo@filee");
    }

    [Fact]
    public async Task Msg_to_html_text_and_zip_directly()
    {
        var msg = Message();
        var route = fx.Catalog.CreatePlanner().Plan("msg", "html")!;
        Assert.Equal("email", Assert.Single(route.Steps).Converter.Id); // not through EML

        var html = await File.ReadAllTextAsync(await ConvertAsync(msg, "html"), TestContext.Current.CancellationToken);
        Assert.Contains("<title>회의 자료</title>", html);
        Assert.Contains("<th>From</th><td>보낸이 &lt;sender@example.com&gt;</td>", html);
        Assert.Contains("<th>Cc</th><td>Copy &lt;copy@example.com&gt;</td>", html);
        Assert.Contains("<th>Attachments</th><td>보고서.pdf, Earlier mail.eml</td>", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("cid:", html);

        var text = await File.ReadAllTextAsync(await ConvertAsync(msg, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains("Subject: 회의 자료\r\n", text);
        Assert.EndsWith("\r\n\r\n안녕하세요. 자료를 보냅니다.\r\n둘째 줄\r\n", text);

        using var zip = ZipFile.OpenRead(await ConvertAsync(msg, "zip"));
        Assert.Equal(["보고서.pdf", "Earlier mail.eml"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task A_file_that_is_no_outlook_message_fails_clearly()
    {
        var path = Path.Combine(fx.NewFolder(), "broken.msg");
        await File.WriteAllTextAsync(path, "not a compound file", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "html" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Not an Outlook message", job.Files.Single().ErrorDetail);
    }
}
