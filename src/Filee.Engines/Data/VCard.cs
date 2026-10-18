// vCard (.vcf) contacts ↔ CSV, one row per contact. Reads vCard 2.1, 3.0 and 4.0: folded lines, quoted-printable
// (with its "=" soft line breaks), CHARSET parameters, escaped characters (\n \, \; \\), grouped names (item1.EMAIL),
// and 2.1's bare TYPE parameters (TEL;CELL;PREF). A value without a CHARSET is UTF-8 when it is valid UTF-8 and CP949
// otherwise, as Korean phones (Anycall, Cyon, SKY) exported their address books; UTF-16 files with a BOM are read too.
// Pictures (PHOTO, LOGO) and other binary values are skipped.
// Columns: Full name, Given name, Family name, Organization, Title, Email, Phone, Address, Birthday, Note. Several
// values in one cell are joined with "; ", each with its kind in brackets: "010-1234-5678 (mobile); 02-123-4567 (work)".
// CSV → vCard reads those columns (and common other names for them, also in Korean and Chinese) and writes vCard 3.0
// in UTF-8, which phones and mail programs import.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Data;

internal sealed class Contact
{
    public string FullName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Title { get; set; } = "";
    public List<(string Value, string? Kind)> Emails { get; } = [];
    public List<(string Value, string? Kind)> Phones { get; } = [];
    public List<(string Value, string? Kind)> Addresses { get; } = [];
    public string Birthday { get; set; } = "";
    public List<string> Notes { get; } = [];

    public bool IsEmpty =>
        FullName.Length == 0 && GivenName.Length == 0 && FamilyName.Length == 0 && Organization.Length == 0
        && Emails.Count == 0 && Phones.Count == 0;
}

internal static partial class VCard
{
    public static readonly string[] Columns =
        ["Full name", "Given name", "Family name", "Organization", "Title", "Email", "Phone", "Address", "Birthday", "Note"];

    private const string Separator = "; ";

    // ───────────────────────── Reading ─────────────────────────

    public static List<Contact> Read(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            bytes = Encoding.UTF8.GetBytes(new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd());
        else if (bytes is [0xEF, 0xBB, 0xBF, ..])
            bytes = bytes[3..];

        // Latin-1 keeps every byte as one char: the structure is ASCII, values are decoded per property.
        var lines = Encoding.Latin1.GetString(bytes).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var contacts = new List<Contact>();
        Contact? contact = null;
        var depth = 0;
        foreach (var line in Unfold(lines))
        {
            if (Property.Parse(line) is not { } property)
                continue;
            switch (property.Name)
            {
                case "BEGIN" when property.Raw.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase):
                    if (depth++ == 0)
                        contact = new Contact();
                    continue;
                case "END" when property.Raw.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase):
                    if (depth > 0 && --depth == 0 && contact is not null)
                    {
                        Finish(contact);
                        contacts.Add(contact);
                        contact = null;
                    }
                    continue;
            }
            // Properties of a nested card (2.1 AGENT) are not this contact's.
            if (contact is not null && depth == 1)
                Apply(contact, property);
        }
        return contacts;
    }

    /// <summary>
    /// Joins folded lines (a line starting with a space or tab continues the previous one) and quoted-printable soft
    /// line breaks (a quoted-printable value ending in "=" continues on the next line, indented or not).
    /// </summary>
    private static IEnumerable<string> Unfold(string[] lines)
    {
        var current = new StringBuilder();
        var started = false;
        foreach (var line in lines)
        {
            // A soft line break first: the next line's leading space is part of the value then.
            if (started && current.Length > 0 && current[^1] == '=' && IsQuotedPrintable(current))
            {
                current.Length--;
                current.Append(line);
                continue;
            }
            if (started && line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }
            if (started)
                yield return current.ToString();
            current.Clear().Append(line);
            started = true;
        }
        if (started)
            yield return current.ToString();
    }

    private static bool IsQuotedPrintable(StringBuilder line)
    {
        var text = line.ToString();
        var colon = text.IndexOf(':');
        return colon > 0 && text[..colon].Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    private static void Apply(Contact contact, Property property)
    {
        if (property.IsBinary)
            return;
        switch (property.Name)
        {
            case "FN":
                contact.FullName = Unescape(property.Value).Trim();
                break;
            case "N":
                var name = Components(property.Value);
                contact.FamilyName = name.ElementAtOrDefault(0)?.Trim() ?? "";
                contact.GivenName = string.Join(" ", name.Skip(1).Take(2).Select(n => n.Trim()).Where(n => n.Length > 0));
                break;
            case "ORG":
                contact.Organization = string.Join(", ", Components(property.Value).Select(o => o.Trim()).Where(o => o.Length > 0));
                break;
            case "TITLE":
                contact.Title = Unescape(property.Value).Trim();
                break;
            case "EMAIL":
                AddValue(contact.Emails, StripScheme(Unescape(property.Value), "mailto:"), property.Kind);
                break;
            case "TEL":
                AddValue(contact.Phones, StripScheme(Unescape(property.Value), "tel:"), property.Kind);
                break;
            case "ADR":
                var parts = Components(property.Value).Select(p => OneLine(p.Trim())).Where(p => p.Length > 0);
                AddValue(contact.Addresses, string.Join(", ", parts), property.Kind);
                break;
            case "BDAY":
                contact.Birthday = Birthday(Unescape(property.Value).Trim());
                break;
            case "NOTE":
                var note = Unescape(property.Value).Trim();
                if (note.Length > 0)
                    contact.Notes.Add(note);
                break;
        }
    }

    private static void AddValue(List<(string, string?)> list, string value, string? kind)
    {
        value = value.Trim();
        if (value.Length > 0)
            list.Add((value, kind));
    }

    private static string StripScheme(string value, string scheme) =>
        value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? value[scheme.Length..] : value;

    private static string OneLine(string text) => NewLines().Replace(text, ", ");

    /// <summary>"19900131" → "1990-01-31"; other forms ("1990-01-31", 4.0's "--0131") as written.</summary>
    private static string Birthday(string value) =>
        value.Length == 8 && value.All(char.IsAsciiDigit) ? $"{value[..4]}-{value[4..6]}-{value[6..]}" : value;

    /// <summary>A missing full name (common in 2.1 files) is made from the name parts: 홍길동, or "John Smith".</summary>
    private static void Finish(Contact contact)
    {
        if (contact.FullName.Length > 0)
            return;
        var family = contact.FamilyName;
        var given = contact.GivenName;
        contact.FullName = family.Length == 0 || given.Length == 0 ? family + given
            : IsCjk(family) && IsCjk(given) ? family + given
            : $"{given} {family}";
    }

    private static bool IsCjk(string text) =>
        text.All(ch => ch is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' or >= '一' and <= '鿿' or >= '぀' and <= 'ヿ');

    /// <summary>A structured value (N, ADR, ORG) split at unescaped ";", each part unescaped.</summary>
    private static List<string> Components(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value, i, 2);
                i++;
            }
            else if (value[i] == ';')
            {
                parts.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }
        parts.Add(Unescape(current.ToString()));
        return parts;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var next = value[++i];
            sb.Append(next is 'n' or 'N' ? '\n' : next);
        }
        return sb.ToString();
    }

    /// <summary>One content line: name (group removed), parameters and the value decoded to text.</summary>
    private sealed class Property
    {
        public string Name { get; private init; } = "";

        /// <summary>The value as written (Latin-1: one char per byte), for BEGIN / END.</summary>
        public string Raw { get; private init; } = "";

        public string Value { get; private set; } = "";
        public bool IsBinary { get; private set; }
        public string? Kind { get; private set; }

        public static Property? Parse(string line)
        {
            var colon = -1;
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                    quoted = !quoted;
                else if (line[i] == ':' && !quoted)
                {
                    colon = i;
                    break;
                }
            }
            if (colon <= 0)
                return null;

            var head = SplitParameters(line[..colon]);
            var name = head[0];
            var dot = name.LastIndexOf('.');
            var property = new Property
            {
                Name = (dot >= 0 ? name[(dot + 1)..] : name).Trim().ToUpperInvariant(),
                Raw = line[(colon + 1)..],
            };

            string? encoding = null, charset = null;
            var types = new List<string>();
            foreach (var parameter in head.Skip(1))
            {
                var equals = parameter.IndexOf('=');
                var key = (equals < 0 ? "TYPE" : parameter[..equals]).Trim().ToUpperInvariant();
                var value = (equals < 0 ? parameter : parameter[(equals + 1)..]).Trim().Trim('"');
                switch (key)
                {
                    case "ENCODING":
                        encoding = value.ToUpperInvariant();
                        break;
                    case "CHARSET":
                        charset = value;
                        break;
                    case "TYPE":
                        types.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "VALUE" when value.Equals("binary", StringComparison.OrdinalIgnoreCase):
                        property.IsBinary = true;
                        break;
                }
                // 2.1 also writes the encoding bare: TEL;QUOTED-PRINTABLE:…
                if (equals < 0 && value.ToUpperInvariant() is "QUOTED-PRINTABLE" or "BASE64" or "B")
                    encoding = value.ToUpperInvariant();
            }

            if (encoding is "BASE64" or "B")
            {
                property.IsBinary = true;
                return property;
            }
            var bytes = Encoding.Latin1.GetBytes(property.Raw);
            if (encoding == "QUOTED-PRINTABLE")
                bytes = DecodeQuotedPrintable(bytes);
            property.Value = Decode(bytes, charset);
            property.Kind = KindOf(types, property.Name);
            return property;
        }

        /// <summary>"N;CHARSET=UTF-8;TYPE=\"a;b\"" → name and parameters, ";" inside quotes kept.</summary>
        private static List<string> SplitParameters(string head)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            foreach (var ch in head)
            {
                if (ch == '"')
                    quoted = !quoted;
                if (ch == ';' && !quoted)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }
            parts.Add(current.ToString());
            return parts;
        }

        private static byte[] DecodeQuotedPrintable(byte[] bytes)
        {
            var output = new List<byte>(bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == '=' && i + 2 < bytes.Length && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
                {
                    output.Add(Convert.ToByte(Encoding.ASCII.GetString(bytes, i + 1, 2), 16));
                    i += 2;
                }
                else if (bytes[i] == '=' && i == bytes.Length - 1)
                {
                    // A soft line break left at the very end.
                }
                else
                {
                    output.Add(bytes[i]);
                }
            }
            return [.. output];
        }

        private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

        /// <summary>The declared charset, else UTF-8 when the bytes are valid UTF-8, else CP949.</summary>
        private static string Decode(byte[] bytes, string? charset)
        {
            if (charset is { Length: > 0 })
            {
                try
                {
                    return Encoding.GetEncoding(charset).GetString(bytes);
                }
                catch (ArgumentException)
                {
                    // Unknown charset name: guess as below.
                }
            }
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(949).GetString(bytes);
            }
        }

        /// <summary>The kind shown in brackets: mobile, home, work, fax, pager or main; null for none of these.</summary>
        private static string? KindOf(List<string> types, string property)
        {
            var kinds = new List<string>();
            foreach (var type in types.Select(t => t.ToUpperInvariant()))
            {
                var kind = type switch
                {
                    "CELL" when property == "TEL" => "mobile",
                    "HOME" => "home",
                    "WORK" => "work",
                    "FAX" when property == "TEL" => "fax",
                    "PAGER" when property == "TEL" => "pager",
                    "MAIN" when property == "TEL" => "main",
                    _ => null,
                };
                if (kind is not null && !kinds.Contains(kind))
                    kinds.Add(kind);
            }
            return kinds.Count == 0 ? null : string.Join(", ", kinds);
        }
    }

    // ───────────────────────── CSV ─────────────────────────

    /// <summary>The header row and one row per contact.</summary>
    public static List<List<string>> ToRows(List<Contact> contacts)
    {
        var rows = new List<List<string>> { Columns.ToList() };
        foreach (var c in contacts)
        {
            rows.Add(
            [
                c.FullName, c.GivenName, c.FamilyName, c.Organization, c.Title,
                Join(c.Emails), Join(c.Phones), Join(c.Addresses), c.Birthday, string.Join("\n", c.Notes),
            ]);
        }
        return rows;
    }

    private static string Join(List<(string Value, string? Kind)> values) =>
        string.Join(Separator, values.Select(v => v.Kind is null ? v.Value : $"{v.Value} ({v.Kind})"));

    /// <summary>Contacts from CSV rows (the first row names the columns).</summary>
    public static List<Contact> FromRows(List<List<string>> rows)
    {
        if (rows.Count == 0)
            throw new InvalidDataException("The CSV file is empty.");
        var fields = rows[0].Select(h => Field(h)).ToList();
        if (fields.All(f => f is null))
            throw new InvalidDataException("No contact columns found. Name the columns Full name, Email, Phone, Address and so on.");

        var contacts = new List<Contact>();
        foreach (var row in rows.Skip(1))
        {
            var contact = new Contact();
            for (var c = 0; c < row.Count && c < fields.Count; c++)
            {
                var value = row[c].Trim();
                if (value.Length == 0)
                    continue;
                switch (fields[c])
                {
                    case "FN": contact.FullName = value; break;
                    case "GIVEN": contact.GivenName = value; break;
                    case "FAMILY": contact.FamilyName = value; break;
                    case "ORG": contact.Organization = value; break;
                    case "TITLE": contact.Title = value; break;
                    case "EMAIL": contact.Emails.AddRange(Split(value)); break;
                    case "TEL": contact.Phones.AddRange(Split(value)); break;
                    case "ADR": contact.Addresses.AddRange(Split(value)); break;
                    case "BDAY": contact.Birthday = value; break;
                    case "NOTE": contact.Notes.Add(value); break;
                }
            }
            if (contact.IsEmpty)
                continue;
            Finish(contact);
            if (contact.FullName.Length == 0)
                contact.FullName = contact.Organization.Length > 0 ? contact.Organization
                    : contact.Emails.Count > 0 ? contact.Emails[0].Value : contact.Phones[0].Value;
            contacts.Add(contact);
        }
        if (contacts.Count == 0)
            throw new InvalidDataException("The CSV file has no contacts.");
        return contacts;
    }

    /// <summary>"a@x.com (work); b@y.com" → values with their kind.</summary>
    private static IEnumerable<(string, string?)> Split(string cell) =>
        cell.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part =>
        {
            var match = KindSuffix().Match(part);
            return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : (part, (string?)null);
        });

    /// <summary>The vCard field a CSV column holds, by its header (English, Korean or Chinese), or null.</summary>
    private static string? Field(string header)
    {
        var key = new string([.. header.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        return key switch
        {
            "fullname" or "name" or "displayname" or "이름" or "성명" or "姓名" or "名称" => "FN",
            "givenname" or "firstname" or "given" or "first" or "名" => "GIVEN",
            "familyname" or "lastname" or "surname" or "family" or "last" or "성" or "姓" => "FAMILY",
            "organization" or "organisation" or "company" or "org" or "회사" or "소속" or "公司" or "单位" => "ORG",
            "title" or "jobtitle" or "직함" or "직책" or "직위" or "职位" or "职务" => "TITLE",
            "email" or "emails" or "emailaddress" or "mail" or "이메일" or "전자메일" or "메일" or "邮箱" or "电子邮件" => "EMAIL",
            "phone" or "phones" or "telephone" or "tel" or "mobile" or "phonenumber" or "cell" or "전화" or "전화번호" or "휴대폰" or "휴대전화" or "연락처" or "电话" or "手机" => "TEL",
            "address" or "addresses" or "주소" or "地址" => "ADR",
            "birthday" or "bday" or "birthdate" or "dateofbirth" or "생일" or "생년월일" or "生日" => "BDAY",
            "note" or "notes" or "memo" or "메모" or "비고" or "备注" => "NOTE",
            _ => null,
        };
    }

    // ───────────────────────── Writing ─────────────────────────

    /// <summary>vCard 3.0, UTF-8, CRLF, lines folded at 75 bytes.</summary>
    public static string Write(List<Contact> contacts)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            Line(sb, "BEGIN:VCARD");
            Line(sb, "VERSION:3.0");
            Line(sb, "FN:" + Escape(c.FullName));
            Line(sb, $"N:{Escape(c.FamilyName)};{Escape(c.GivenName)};;;");
            if (c.Organization.Length > 0)
                Line(sb, "ORG:" + Escape(c.Organization));
            if (c.Title.Length > 0)
                Line(sb, "TITLE:" + Escape(c.Title));
            foreach (var (value, kind) in c.Emails)
                Line(sb, $"EMAIL;TYPE={string.Join(',', ["INTERNET", .. Types(kind, "EMAIL")])}:{Escape(value)}");
            foreach (var (value, kind) in c.Phones)
                Line(sb, Types(kind, "TEL") is { Count: > 0 } types ? $"TEL;TYPE={string.Join(',', types)}:{Escape(value)}" : "TEL:" + Escape(value));
            // The address as one street line: the parts can't be told apart reliably from a single cell.
            foreach (var (value, kind) in c.Addresses)
                Line(sb, (Types(kind, "ADR") is { Count: > 0 } types ? $"ADR;TYPE={string.Join(',', types)}" : "ADR") + $":;;{Escape(value)};;;;");
            if (c.Birthday.Length > 0)
                Line(sb, "BDAY:" + Escape(c.Birthday));
            if (c.Notes.Count > 0)
                Line(sb, "NOTE:" + Escape(string.Join("\n", c.Notes)));
            Line(sb, "END:VCARD");
        }
        return sb.ToString();
    }

    private static List<string> Types(string? kind, string property)
    {
        var types = new List<string>();
        foreach (var word in (kind ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var type = word.ToLowerInvariant() switch
            {
                "mobile" or "cell" or "휴대폰" or "手机" when property == "TEL" => "CELL",
                "home" or "집" or "家" => "HOME",
                "work" or "office" or "직장" or "회사" or "工作" => "WORK",
                "fax" or "팩스" or "传真" when property == "TEL" => "FAX",
                "pager" when property == "TEL" => "PAGER",
                "main" when property == "TEL" => "MAIN",
                _ => null,
            };
            if (type is not null && !types.Contains(type))
                types.Add(type);
        }
        return types;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");

    /// <summary>Appends a content line folded at 75 bytes (RFC 6350), never inside a character.</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var bytes = 0;
        var limit = 75;
        var e = StringInfo.GetTextElementEnumerator(line);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > limit)
            {
                sb.Append("\r\n ");
                bytes = 1;
                limit = 75;
            }
            sb.Append(element);
            bytes += size;
        }
        sb.Append("\r\n");
    }

    [GeneratedRegex(@"^(.*?)\s*\(([^()]*)\)$")]
    private static partial Regex KindSuffix();

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex NewLines();
}
