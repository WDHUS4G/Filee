// Outlook .msg (an OLE compound file of MAPI properties) read with MsgReader (MIT) and rebuilt as a MIME message with
// MimeKit, so MSG → HTML, TXT and ZIP take the same path as EML and MSG → EML is the rebuilt message itself.
// Carried over: sender, To / Cc / Bcc, subject, sent date, Message-ID, the text and HTML bodies (MsgReader takes the
// HTML out of RTF-only messages), attachments, pictures shown in the body (Content-ID) and attached Outlook items,
// which become attached .eml messages. Attachments that only link to a file elsewhere are not followed.

using MimeKit;
using MsgReader.Outlook;

namespace Filee.Engines.Email;

internal static class OutlookMessage
{
    public static MimeMessage Load(string path)
    {
        Storage.Message message;
        try
        {
            message = new Storage.Message(path, FileAccess.Read);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new InvalidDataException($"Not an Outlook message: {ex.Message}", ex);
        }
        using (message)
            return ToMime(message);
    }

    internal static MimeMessage ToMime(Storage.Message message)
    {
        var mime = new MimeMessage();
        if (Mailbox(message.Sender?.DisplayName, message.Sender?.Email) is { } from)
            mime.From.Add(from);
        foreach (var recipient in message.Recipients)
        {
            if (Mailbox(recipient.DisplayName, recipient.Email) is not { } mailbox)
                continue;
            var list = recipient.Type switch
            {
                RecipientType.Cc => mime.Cc,
                RecipientType.Bcc => mime.Bcc,
                _ => mime.To,
            };
            list.Add(mailbox);
        }
        mime.Subject = message.Subject ?? "";
        if (message.SentOn is { } sent)
            mime.Date = sent;
        if (message.Headers?.MessageId is { Length: > 0 } id)
            mime.MessageId = id.Trim('<', '>');

        var body = new BodyBuilder
        {
            TextBody = string.IsNullOrEmpty(message.BodyText) ? null : message.BodyText,
            HtmlBody = string.IsNullOrEmpty(message.BodyHtml) ? null : message.BodyHtml,
        };
        foreach (var item in message.Attachments)
        {
            switch (item)
            {
                case Storage.Attachment { IsExternalReference: false, Data: { Length: >= 0 } data } attachment:
                    var name = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName;
                    var type = ContentType.TryParse(attachment.MimeType ?? "", out var parsed) ? parsed : ContentType.Parse(MimeTypes.GetMimeType(name));
                    // A picture the HTML body shows ("cid:") goes with the body, not into the attachments.
                    if (attachment.ContentId is { Length: > 0 } contentId
                        && body.HtmlBody?.Contains("cid:" + contentId.Trim('<', '>'), StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var resource = body.LinkedResources.Add(name, data, type);
                        resource.ContentId = contentId.Trim('<', '>');
                    }
                    else
                    {
                        body.Attachments.Add(name, data, type);
                    }
                    break;
                case Storage.Message attached:
                    var inner = ToMime(attached);
                    var fileName = string.IsNullOrWhiteSpace(inner.Subject) ? "message" : inner.Subject;
                    body.Attachments.Add(new MessagePart
                    {
                        Message = inner,
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                    });
                    break;
            }
        }
        mime.Body = body.ToMessageBody();
        return mime;
    }

    /// <summary>A sender or recipient; Exchange-only addresses (no "@") keep the name.</summary>
    private static MailboxAddress? Mailbox(string? name, string? address)
    {
        name = name?.Trim();
        address = address?.Trim();
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(address))
            return null;
        if (string.Equals(name, address, StringComparison.OrdinalIgnoreCase))
            name = null;
        return new MailboxAddress(name ?? "", address ?? "");
    }
}
