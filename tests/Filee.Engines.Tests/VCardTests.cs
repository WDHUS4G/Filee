// vCard → CSV and CSV → vCard with the built-in data engine: 2.1 from old Korean phones (CP949 bytes, quoted-printable
// with soft line breaks, bare TYPE parameters, no FN, a photo), 3.0 (folding, escapes, groups) and 4.0 (quoted TYPE
// lists, tel: URIs). Cards are written in the test.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class VCardTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static VCardTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Cp949 => Encoding.GetEncoding(949);

    /// <summary>Quoted-printable of text in a charset, every byte outside printable ASCII as =XX, soft breaks at 60.</summary>
    private static string QuotedPrintable(string text, Encoding encoding)
    {
        var sb = new StringBuilder();
        var column = 0;
        foreach (var b in encoding.GetBytes(text))
        {
            var piece = b is >= 33 and <= 126 and not (byte)'=' ? ((char)b).ToString() : $"={b:X2}";
            if (column + piece.Length > 60)
            {
                sb.Append("=\r\n");
                column = 0;
            }
            sb.Append(piece);
            column += piece.Length;
        }
        return sb.ToString();
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private static List<string[]> Rows(string csv) =>
        csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',')).ToList();

    [Fact]
    public async Task Cards_of_every_version_become_one_csv_row_each()
    {
        var file = new MemoryStream();
        void Ascii(string text) => file.Write(Encoding.ASCII.GetBytes(text));

        // vCard 2.1 as Korean feature phones wrote it: CP949, partly quoted-printable, no FN.
        Ascii("BEGIN:VCARD\r\nVERSION:2.1\r\n");
        Ascii($"N;CHARSET=KS_C_5601-1987;ENCODING=QUOTED-PRINTABLE:{QuotedPrintable("홍", Cp949)};{QuotedPrintable("길동", Cp949)};;;\r\n");
        Ascii("TEL;CELL:010-1234-5678\r\nTEL;WORK;VOICE:02-123-4567\r\nEMAIL;PREF;INTERNET:hong@example.com\r\n");
        Ascii($"ADR;HOME;ENCODING=QUOTED-PRINTABLE:;;{QuotedPrintable("서울특별시 종로구 세종대로 209 정부서울청사 별관 3층", Cp949)};;;03171;\r\n");
        Ascii("NOTE:");
        file.Write(Cp949.GetBytes("전화 주세요"));
        Ascii("\r\nPHOTO;ENCODING=BASE64;TYPE=JPEG:\r\n /9j/4AAQSkZJRgABAQAAAQABAAD\r\n 2wBDAAgGBgcGBQgHBwcJ\r\n\r\n");
        Ascii("AGENT:\r\nBEGIN:VCARD\r\nVERSION:2.1\r\nFN:Assistant\r\nTEL:000\r\nEND:VCARD\r\n");
        Ascii("BDAY:19800102\r\nEND:VCARD\r\n");

        // vCard 3.0 in UTF-8: folded lines, escapes, a group, structured ORG.
        file.Write(Encoding.UTF8.GetBytes(string.Join("\r\n",
            "BEGIN:VCARD", "VERSION:3.0", "FN:Ada Lovelace", "N:Lovelace;Ada;;;",
            "ORG:Analytical Engines\\, Ltd.;Research", "TITLE:Engineer",
            "item1.EMAIL;TYPE=INTERNET,WORK:ada@example.com", "EMAIL;TYPE=HOME:ada@home.example",
            "TEL;TYPE=CELL,VOICE:+44 20 7946 0000",
            "NOTE:First line\\nSecond line\\; folded over two li", " nes here",
            "END:VCARD", "")));

        // vCard 4.0: quoted TYPE list, tel: URI, birthday without a year.
        file.Write(Encoding.UTF8.GetBytes(string.Join("\r\n",
            "BEGIN:VCARD", "VERSION:4.0", "FN:王小明", "N:王;小明;;;",
            "TEL;VALUE=uri;TYPE=\"cell,voice\":tel:+86-10-1234-5678", "BDAY:--0412", "END:VCARD", "")));

        var path = Path.Combine(fx.NewFolder(), "contacts.vcf");
        await File.WriteAllBytesAsync(path, file.ToArray(), TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(await ConvertAsync(path, "csv"), TestContext.Current.CancellationToken);
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "CSV with BOM for Excel");
        var csv = Encoding.UTF8.GetString(bytes).TrimStart('﻿');

        Assert.Equal(string.Join("\r\n",
            "Full name,Given name,Family name,Organization,Title,Email,Phone,Address,Birthday,Note",
            "홍길동,길동,홍,,,hong@example.com,010-1234-5678 (mobile); 02-123-4567 (work),\"서울특별시 종로구 세종대로 209 정부서울청사 별관 3층, 03171 (home)\",1980-01-02,전화 주세요",
            "Ada Lovelace,Ada,Lovelace,\"Analytical Engines, Ltd., Research\",Engineer,ada@example.com (work); ada@home.example (home),+44 20 7946 0000 (mobile),,,\"First line\nSecond line; folded over two lines here\"",
            "王小明,小明,王,,,,+86-10-1234-5678 (mobile),,--0412,",
            ""), csv);
    }

    [Fact]
    public async Task Utf16_cards_are_read()
    {
        var path = Path.Combine(fx.NewFolder(), "phone.vcf");
        await File.WriteAllTextAsync(path, "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:김철수\r\nTEL:010-0000-0000\r\nEND:VCARD\r\n",
            Encoding.Unicode, TestContext.Current.CancellationToken);

        var csv = await File.ReadAllTextAsync(await ConvertAsync(path, "csv"), TestContext.Current.CancellationToken);
        Assert.Equal("김철수", Rows(csv)[1][0]);
    }

    [Fact]
    public async Task Csv_with_korean_headers_becomes_vcard_3_and_reads_back()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "주소록.csv");
        await File.WriteAllTextAsync(path,
            "이름,전화번호,이메일,회사,메모,관계\r\n" +
            "홍길동,010-1111-2222 (mobile); 02-333-4444 (work),hong@example.com,\"필이, 주식회사\",아주 긴 메모입니다. 한 줄이 75바이트를 넘으면 vCard는 줄을 접어요. 접힌 줄은 공백으로 시작해요.,친구\r\n" +
            ",,,,,\r\n",
            new UTF8Encoding(true), TestContext.Current.CancellationToken);

        var vcf = await ConvertAsync(path, "vcf");
        var text = await File.ReadAllTextAsync(vcf, TestContext.Current.CancellationToken);
        Assert.StartsWith("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:홍길동\r\nN:;;;;\r\nORG:필이\\, 주식회사\r\n", text);
        Assert.Contains("\r\nEMAIL;TYPE=INTERNET:hong@example.com\r\nTEL;TYPE=CELL:010-1111-2222\r\nTEL;TYPE=WORK:02-333-4444\r\n", text);
        Assert.Single(text.Split("BEGIN:VCARD")[1..]); // the empty row is no contact
        foreach (var line in text.Split("\r\n"))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"not folded: {line}");

        // And back to CSV: the same values.
        var csv = Rows(await File.ReadAllTextAsync(await ConvertAsync(vcf, "csv"), TestContext.Current.CancellationToken));
        Assert.Equal("홍길동", csv[1][0]);
        Assert.Contains("아주 긴 메모입니다. 한 줄이 75바이트를 넘으면 vCard는 줄을 접어요. 접힌 줄은 공백으로 시작해요.", string.Join(",", csv[1]));
        Assert.Contains("010-1111-2222 (mobile); 02-333-4444 (work)", string.Join(",", csv[1]));
    }

    [Fact]
    public async Task Csv_without_contact_columns_is_refused()
    {
        var path = Path.Combine(fx.NewFolder(), "numbers.csv");
        await File.WriteAllTextAsync(path, "x,y\r\n1,2\r\n", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "vcf" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("No contact columns", job.Files.Single().ErrorDetail);
    }
}
