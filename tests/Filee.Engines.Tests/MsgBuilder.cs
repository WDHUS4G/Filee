// Writes small Outlook .msg files (MS-OXMSG: MAPI properties in an OLE compound file) for the e-mail tests, with
// OpenMcdf, which MsgReader brings along. Only what Outlook needs to show a message is written: the property streams,
// strings and binaries in "__substg1.0_" streams, one storage per recipient and attachment, and an empty named
// property map.

using System.Text;
using OpenMcdf;

namespace Filee.Engines.Tests;

internal sealed class MsgBuilder
{
    private const ushort Long = 0x0003, Boolean = 0x000B, Time = 0x0040, Unicode = 0x001F, Binary = 0x0102, Object = 0x000D;

    public string Subject { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string SenderEmail { get; init; } = "";
    public DateTime SentUtc { get; init; } = new(2026, 10, 1, 0, 30, 0, DateTimeKind.Utc);
    public string? Body { get; init; }
    public string? Html { get; init; }
    public string? MessageId { get; init; }
    public List<(string Name, string Email, int Type)> Recipients { get; } = [];
    public List<(string FileName, byte[] Data, string MimeType, string? ContentId)> Attachments { get; } = [];
    public List<MsgBuilder> AttachedMessages { get; } = [];

    public void Save(string path)
    {
        using var root = RootStorage.Create(path, OpenMcdf.Version.V3, StorageModeFlags.None);
        WriteMessage(root, topLevel: true);
        var names = root.CreateStorage("__nameid_version1.0");
        foreach (var stream in new[] { "__substg1.0_00020102", "__substg1.0_00030102", "__substg1.0_00040102" })
            names.CreateStream(stream).Dispose();
    }

    private void WriteMessage(Storage storage, bool topLevel)
    {
        var properties = new Properties(storage);
        properties.String(0x001A, "IPM.Note");
        properties.String(0x0037, Subject);
        properties.String(0x0C1A, SenderName);
        properties.String(0x0C1F, SenderEmail);
        properties.String(0x0C1E, "SMTP");
        properties.String(0x5D01, SenderEmail);
        properties.String(0x0042, SenderName);
        properties.String(0x0065, SenderEmail);
        properties.String(0x0064, "SMTP");
        properties.Fixed(0x0039, Time, BitConverter.GetBytes(SentUtc.ToFileTimeUtc()));
        properties.Fixed(0x3FDE, Long, BitConverter.GetBytes(65001)); // PR_INTERNET_CPID: UTF-8
        properties.Fixed(0x0E07, Long, BitConverter.GetBytes(1)); // PR_MESSAGE_FLAGS: read
        if (Body is not null)
            properties.String(0x1000, Body);
        if (Html is not null)
            properties.Bytes(0x1013, Encoding.UTF8.GetBytes(Html));
        if (MessageId is not null)
        {
            properties.String(0x1035, MessageId);
            properties.String(0x007D, $"Message-ID: {MessageId}\r\nSubject: {Subject}\r\n");
        }

        for (var i = 0; i < Recipients.Count; i++)
        {
            var (name, email, type) = Recipients[i];
            var recipient = new Properties(storage.CreateStorage($"__recip_version1.0_#{i:X8}"));
            recipient.Fixed(0x0C15, Long, BitConverter.GetBytes(type)); // 1 To, 2 Cc, 3 Bcc
            recipient.Fixed(0x3000, Long, BitConverter.GetBytes(i)); // PR_ROWID
            recipient.String(0x3001, name);
            recipient.String(0x3002, "SMTP");
            recipient.String(0x3003, email);
            recipient.String(0x39FE, email);
            recipient.Save(8);
        }

        var count = 0;
        foreach (var (fileName, data, mimeType, contentId) in Attachments)
        {
            var attachment = new Properties(storage.CreateStorage($"__attach_version1.0_#{count:X8}"));
            attachment.Fixed(0x3705, Long, BitConverter.GetBytes(1)); // ATTACH_BY_VALUE
            attachment.Fixed(0x0E21, Long, BitConverter.GetBytes(count)); // PR_ATTACH_NUM
            attachment.Bytes(0x3701, data);
            attachment.String(0x3707, fileName);
            attachment.String(0x3704, fileName);
            attachment.String(0x370E, mimeType);
            if (contentId is not null)
            {
                attachment.String(0x3712, contentId);
                attachment.Fixed(0x3714, Long, BitConverter.GetBytes(4)); // ATT_MHTML_REF
                attachment.Fixed(0x7FFE, Boolean, BitConverter.GetBytes(1)); // hidden: shown in the body
            }
            attachment.Fixed(0x370B, Long, BitConverter.GetBytes(-1)); // PR_RENDERING_POSITION
            attachment.Save(8);
            count++;
        }
        foreach (var message in AttachedMessages)
        {
            var storageOfAttachment = storage.CreateStorage($"__attach_version1.0_#{count:X8}");
            var attachment = new Properties(storageOfAttachment);
            attachment.Fixed(0x3705, Long, BitConverter.GetBytes(5)); // ATTACH_EMBEDDED_MSG
            attachment.Fixed(0x0E21, Long, BitConverter.GetBytes(count));
            attachment.String(0x3001, message.Subject);
            attachment.Fixed(0x3701, Object, new byte[8]);
            attachment.Save(8);
            var embedded = storageOfAttachment.CreateStorage("__substg1.0_3701000D");
            message.WriteMessage(embedded, topLevel: false);
            count++;
        }

        // Header: reserved, next recipient id, next attachment id, recipient count, attachment count (+ reserved at
        // the top level).
        var header = new List<byte>(new byte[8]);
        header.AddRange(BitConverter.GetBytes(Recipients.Count));
        header.AddRange(BitConverter.GetBytes(count));
        header.AddRange(BitConverter.GetBytes(Recipients.Count));
        header.AddRange(BitConverter.GetBytes(count));
        if (topLevel)
            header.AddRange(new byte[8]);
        properties.Save([.. header]);
    }

    /// <summary>The property stream of one storage plus the streams of its variable-size values.</summary>
    private sealed class Properties(Storage storage)
    {
        private readonly MemoryStream _entries = new();

        public void String(ushort id, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);
            Stream(id, Unicode, bytes);
            Entry(id, Unicode, [.. BitConverter.GetBytes(bytes.Length + 2), 0, 0, 0, 0]);
        }

        public void Bytes(ushort id, byte[] value)
        {
            Stream(id, Binary, value);
            Entry(id, Binary, [.. BitConverter.GetBytes(value.Length), 0, 0, 0, 0]);
        }

        public void Fixed(ushort id, ushort type, byte[] value) => Entry(id, type, [.. value, .. new byte[8 - value.Length]]);

        private void Stream(ushort id, ushort type, byte[] data)
        {
            using var stream = storage.CreateStream($"__substg1.0_{id:X4}{type:X4}");
            stream.Write(data);
        }

        private void Entry(ushort id, ushort type, byte[] value)
        {
            _entries.Write(BitConverter.GetBytes((uint)(id << 16 | type)));
            _entries.Write(BitConverter.GetBytes(6)); // readable, writable
            _entries.Write(value);
        }

        public void Save(int headerSize) => Save(new byte[headerSize]);

        public void Save(byte[] header)
        {
            using var stream = storage.CreateStream("__properties_version1.0");
            stream.Write(header);
            stream.Write(_entries.ToArray());
        }
    }
}
