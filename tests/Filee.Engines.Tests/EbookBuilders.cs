// Generates e-book test inputs in code: EPUB 3 packages, FB2 (windows-1251), MOBI 6 files (PalmDOC or HUFF/CDIC
// compressed, with EXTH metadata, trailing entries and picture records), AZW4 Print Replica wrappers and comic
// archives. The MOBI writer here is test-only: it follows the MobileRead format description.

using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

internal static class EbookBuilders
{
    /// <summary>A PNG of the given size and colour.</summary>
    public static byte[] Png(uint width, uint height, MagickColor color)
    {
        using var image = new MagickImage(color, width, height);
        return image.ToByteArray(MagickFormat.Png);
    }

    // ───────────────────────── EPUB ─────────────────────────

    /// <summary>
    /// An EPUB 3 with three chapters in OEBPS/Text (bold / italic, a class rule, a link from chapter 1 to an anchor in
    /// chapter 3, a picture in OEBPS/Images, a table), a cover image, a style sheet, a nav document and an NCX.
    /// </summary>
    public static string Epub(string folder, string name = "소설.epub")
    {
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Entry(zip, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Entry(zip, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>
                <dc:title>별빛 이야기</dc:title>
                <dc:creator>김작가</dc:creator>
                <dc:language>ko</dc:language>
                <meta property="dcterms:modified">2026-01-01T00:00:00Z</meta>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="css" href="Styles/book.css" media-type="text/css"/>
                <item id="c1" href="Text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="Text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="c3" href="Text/ch%203.xhtml" media-type="application/xhtml+xml"/>
                <item id="pic" href="Images/pic.png" media-type="image/png"/>
                <item id="cover" href="Images/cover.png" media-type="image/png" properties="cover-image"/>
              </manifest>
              <spine toc="ncx">
                <itemref idref="c1"/>
                <itemref idref="c2"/>
                <itemref idref="c3"/>
              </spine>
            </package>
            """);
        Entry(zip, "OEBPS/nav.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>목차</title></head>
            <body><nav epub:type="toc"><ol><li><a href="Text/ch1.xhtml">1장</a></li></ol></nav></body></html>
            """);
        Entry(zip, "OEBPS/toc.ncx", """
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>별빛</text></docTitle>
            <navMap><navPoint id="n1" playOrder="1"><navLabel><text>1장</text></navLabel><content src="Text/ch1.xhtml"/></navPoint></navMap></ncx>
            """);
        Entry(zip, "OEBPS/Styles/book.css", ".loud { font-weight: bold } p.centre { text-align: center } body { color: #333 }");
        Entry(zip, "OEBPS/Text/ch1.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title/><link rel="stylesheet" type="text/css" href="../Styles/book.css"/></head>
            <body>
            <h1>첫 번째 장</h1>
            <p>밤하늘에 <b>별</b>이 <i>반짝</i>였다. <span class="loud">크게</span> 외쳤다.</p>
            <p class="centre">가운데 문단</p>
            <p>주석은 <a href="ch%203.xhtml#note1">여기</a>를 보세요.</p>
            <div id="empty"/>
            <p>빈 div 다음 문단</p>
            </body></html>
            """);
        Entry(zip, "OEBPS/Text/ch2.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>둘</title></head>
            <body>
            <h1>두 번째 장</h1>
            <p>그림이 있는 장입니다.</p>
            <p><img src="../Images/pic.png" alt="그림"/></p>
            <table><tr><th>이름</th><th>값</th></tr><tr><td>별</td><td>42</td></tr></table>
            </body></html>
            """);
        Entry(zip, "OEBPS/Text/ch 3.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>셋</title></head>
            <body>
            <h1>세 번째 장</h1>
            <p id="note1">주석 내용입니다.</p>
            <ul><li>목록 하나</li><li>목록 둘</li></ul>
            </body></html>
            """);
        Entry(zip, "OEBPS/Images/pic.png", Png(160, 90, MagickColors.Teal));
        Entry(zip, "OEBPS/Images/cover.png", Png(300, 450, MagickColors.Navy));
        return path;
    }

    public static void Entry(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    public static void Entry(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        stream.Write(data);
    }

    // ───────────────────────── FB2 ─────────────────────────

    /// <summary>A windows-1251 FB2 book with two chapters, a nested section, a poem, a note and a cover picture.</summary>
    public static string Fb2(string folder)
    {
        var cover = Convert.ToBase64String(Png(120, 180, MagickColors.DarkRed));
        var xml = $"""
            <?xml version="1.0" encoding="windows-1251"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
             <description>
              <title-info>
               <genre>prose_classic</genre>
               <author><first-name>Лев</first-name><last-name>Толстой</last-name></author>
               <book-title>Война и мир</book-title>
               <lang>ru</lang>
               <coverpage><image l:href="#cover.png"/></coverpage>
              </title-info>
             </description>
             <body>
              <title><p>Война и мир</p></title>
              <section>
               <title><p>Глава первая</p></title>
               <p>Первый <strong>абзац</strong> и <emphasis>курсив</emphasis>.<a l:href="#n1" type="note">[1]</a></p>
               <empty-line/>
               <poem><stanza><v>Строка стиха один</v><v>Строка стиха два</v></stanza></poem>
               <section>
                <title><p>Подглава</p></title>
                <p>Текст подглавы.</p>
               </section>
              </section>
              <section>
               <title><p>Глава вторая</p></title>
               <cite><p>Цитата из книги.</p></cite>
               <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
              </section>
             </body>
             <body name="notes">
              <section id="n1"><title><p>1</p></title><p>Текст примечания.</p></section>
             </body>
             <binary id="cover.png" content-type="image/png">{cover}</binary>
            </FictionBook>
            """;
        var path = Path.Combine(folder, "voina.fb2");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes(xml));
        return path;
    }

    // ───────────────────────── MOBI ─────────────────────────

    /// <summary>Options of a generated MOBI file.</summary>
    /// <param name="Compression">1 none, 2 PalmDOC, 17480 HUFF/CDIC.</param>
    /// <param name="Encryption">0 none; 2 marks the book as DRM-protected (the text stays plain).</param>
    /// <param name="Cover">EXTH 201: index of the cover among the pictures.</param>
    public sealed record MobiOptions(int Compression = 2, int Encryption = 0, int? Cover = null, string Title = "Mobi Title", string Author = "Mobi Author");

    /// <summary>A MOBI 6 book from HTML (UTF-8) and pictures (referenced as recindex="00001", ...).</summary>
    public static byte[] Mobi(string html, IReadOnlyList<byte[]> pictures, MobiOptions options) =>
        Mobi(Encoding.UTF8.GetBytes(html), pictures, options);

    public static byte[] Mobi(byte[] text, IReadOnlyList<byte[]> pictures, MobiOptions options)
    {
        const int RecordSize = 4096;
        var records = new List<byte[]> { Array.Empty<byte>() }; // record 0 is filled in last
        for (var offset = 0; offset < text.Length; offset += RecordSize)
        {
            var chunk = text.AsSpan(offset, Math.Min(RecordSize, text.Length - offset)).ToArray();
            var data = options.Compression switch
            {
                2 => PalmDocCompress(chunk),
                _ => chunk, // 1, and HUFF/CDIC with the identity code table below
            };
            // Trailing entries (extra flags 0b11): a multibyte byte, then a 2-byte indexing entry at the very end.
            records.Add([.. data, 0x00, 0x00, 0x82]);
        }
        var textRecords = records.Count - 1;
        var firstImage = pictures.Count > 0 ? records.Count : -1;
        records.AddRange(pictures);
        var huff = -1;
        if (options.Compression == 17480)
        {
            huff = records.Count;
            records.Add(IdentityHuff());
            records.Add(IdentityCdic());
        }
        records.Add([0xE9, 0x8E, 0x0D, 0x0A]); // EOF record

        records[0] = Record0(text.Length, textRecords, firstImage, huff, options);
        return PalmDb(options.Title, "BOOKMOBI", records);
    }

    private static byte[] Record0(int textLength, int textRecords, int firstImage, int huff, MobiOptions options)
    {
        var exth = new List<(int Type, byte[] Data)>
        {
            (100, Encoding.UTF8.GetBytes(options.Author)),
            (503, Encoding.UTF8.GetBytes(options.Title)),
            (524, "en"u8.ToArray()),
        };
        if (options.Cover is { } cover)
            exth.Add((201, BigEndian(cover)));
        var exthBytes = new List<byte>();
        exthBytes.AddRange("EXTH"u8.ToArray());
        var exthLength = 12 + exth.Sum(e => 8 + e.Data.Length);
        var padding = (4 - exthLength % 4) % 4;
        exthBytes.AddRange(BigEndian(exthLength + padding));
        exthBytes.AddRange(BigEndian(exth.Count));
        foreach (var (type, data) in exth)
        {
            exthBytes.AddRange(BigEndian(type));
            exthBytes.AddRange(BigEndian(8 + data.Length));
            exthBytes.AddRange(data);
        }
        exthBytes.AddRange(new byte[padding]);

        const int MobiLength = 0xE8;
        var header = new byte[16 + MobiLength];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], (ushort)options.Compression);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)textLength);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)textRecords);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 4096);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)options.Encryption);
        "MOBI"u8.CopyTo(span[16..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x14..], MobiLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x18..], 2); // book
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], 65001); // UTF-8
        BinaryPrimitives.WriteUInt32BigEndian(span[0x20..], 0x1234);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x24..], 6); // MOBI 6
        for (var o = 0x28; o <= 0x4C; o += 4)
            BinaryPrimitives.WriteUInt32BigEndian(span[o..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x50..], (uint)(textRecords + 1));
        var nameOffset = header.Length + exthBytes.Count;
        var name = Encoding.UTF8.GetBytes(options.Title);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x54..], (uint)nameOffset);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x58..], (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x5C..], 0x09); // English
        BinaryPrimitives.WriteUInt32BigEndian(span[0x68..], 6);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x6C..], firstImage < 0 ? uint.MaxValue : (uint)firstImage);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x70..], huff < 0 ? 0 : (uint)huff);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x74..], huff < 0 ? 0u : 2u);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x80..], 0x40); // EXTH present
        BinaryPrimitives.WriteUInt32BigEndian(span[0xA4..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xA8..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(span[0xC0..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[0xC2..], (ushort)textRecords);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xC4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xC8..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xD0..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(span[0xF2..], 0b11); // multibyte + one indexing entry
        return [.. header, .. exthBytes, .. name, 0, 0, 0, 0];
    }

    /// <summary>A Palm database with the given type/creator and records.</summary>
    public static byte[] PalmDb(string name, string typeCreator, List<byte[]> records)
    {
        var header = new byte[78 + records.Count * 8 + 2];
        var nameBytes = Encoding.ASCII.GetBytes(name.Replace(' ', '_'));
        nameBytes.AsSpan(0, Math.Min(31, nameBytes.Length)).CopyTo(header);
        Encoding.ASCII.GetBytes(typeCreator).CopyTo(header, 60);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76), (ushort)records.Count);
        var offset = header.Length;
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + i * 8 + 4), (uint)(i * 2) & 0x00FFFFFF);
            offset += records[i].Length;
        }
        using var stream = new MemoryStream();
        stream.Write(header);
        foreach (var record in records)
            stream.Write(record);
        return stream.ToArray();
    }

    /// <summary>PalmDOC compression using every code: literals, literal runs, back-references and space pairs.</summary>
    public static byte[] PalmDocCompress(byte[] input)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < input.Length)
        {
            // Back-reference: the longest match of 3..10 bytes within the last 2047 bytes.
            var (bestLength, bestDistance) = (0, 0);
            for (var distance = 1; distance <= Math.Min(2047, i); distance++)
            {
                var length = 0;
                while (length < 10 && i + length < input.Length && input[i + length] == input[i - distance + length])
                    length++;
                if (length > bestLength)
                    (bestLength, bestDistance) = (length, distance);
            }
            if (bestLength >= 3)
            {
                var pair = 0x8000 | (bestDistance << 3) | (bestLength - 3);
                output.Add((byte)(pair >> 8));
                output.Add((byte)pair);
                i += bestLength;
                continue;
            }
            var c = input[i];
            if (c == ' ' && i + 1 < input.Length && input[i + 1] is >= 0x40 and <= 0x7F)
            {
                output.Add((byte)(input[i + 1] ^ 0x80));
                i += 2;
            }
            else if (c is 0 or (>= 0x09 and <= 0x7F))
            {
                output.Add(c);
                i++;
            }
            else
            {
                // Bytes that need escaping (1..8 and 0x80+) go into a literal run of up to 8 bytes.
                var run = new List<byte>();
                while (i < input.Length && run.Count < 8 && input[i] is (>= 1 and <= 8) or >= 0x80)
                    run.Add(input[i++]);
                output.Add((byte)run.Count);
                output.AddRange(run);
            }
        }
        return [.. output];
    }

    /// <summary>A HUFF table in which every byte is an 8-bit code for dictionary entry of the same number.</summary>
    private static byte[] IdentityHuff()
    {
        var huff = new byte[24 + 256 * 4 + 64 * 4];
        "HUFF"u8.CopyTo(huff);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(12), 24 + 256 * 4);
        for (var b = 0; b < 256; b++)
        {
            // Code length 8, terminal; the entry index is (max code - code) >> 24 = raw - b, so raw = 2b gives entry b.
            var value = ((uint)(2 * b) << 8) | 0x80 | 8;
            BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(24 + b * 4), value);
        }
        return huff;
    }

    /// <summary>A CDIC with 256 one-byte literal phrases.</summary>
    private static byte[] IdentityCdic()
    {
        var cdic = new byte[16 + 256 * 2 + 256 * 3];
        "CDIC"u8.CopyTo(cdic);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(8), 256);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(12), 8);
        for (var b = 0; b < 256; b++)
        {
            var offset = 256 * 2 + b * 3;
            BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(16 + b * 2), (ushort)offset);
            BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(16 + offset), 0x8001); // literal, 1 byte
            cdic[16 + offset + 2] = (byte)b;
        }
        return cdic;
    }

    /// <summary>An AZW4 (Print Replica) wrapper: "%MOP", one table with one section holding the PDF.</summary>
    public static byte[] PrintReplica(byte[] pdf)
    {
        var text = new byte[20 + pdf.Length];
        "%MOP"u8.CopyTo(text);
        BinaryPrimitives.WriteUInt32BigEndian(text.AsSpan(4), 1); // tables
        BinaryPrimitives.WriteUInt32BigEndian(text.AsSpan(8), 1); // sections in table 1
        BinaryPrimitives.WriteUInt32BigEndian(text.AsSpan(12), 20); // offset
        BinaryPrimitives.WriteUInt32BigEndian(text.AsSpan(16), (uint)pdf.Length);
        pdf.CopyTo(text, 20);
        return Mobi(text, [], new MobiOptions(Compression: 1, Title: "Replica"));
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    // ───────────────────────── Comics ─────────────────────────

    /// <summary>Pages named so that only a natural sort gets the order right, each with its own size.</summary>
    public static readonly (string Name, uint Width, uint Height)[] ComicPages =
    [
        ("chapter/page1.png", 200, 300),
        ("chapter/page2.jpg", 220, 330),
        ("chapter/page10.png", 240, 360),
    ];

    public static string Cbz(string folder)
    {
        var path = Path.Combine(folder, "만화.cbz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, width, height) in ComicPages)
            Entry(zip, name, Picture(name, width, height));
        Entry(zip, "__MACOSX/chapter/._page1.png", [0, 1, 2]);
        Entry(zip, "ComicInfo.xml", "<ComicInfo/>");
        return path;
    }

    public static string Cbt(string folder)
    {
        var path = Path.Combine(folder, "comic.cbt");
        using var stream = File.Create(path);
        using var tar = new TarWriter(stream);
        foreach (var (name, width, height) in ComicPages)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Picture(name, width, height)) };
            tar.WriteEntry(entry);
        }
        return path;
    }

    private static byte[] Picture(string name, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Coral, width, height);
        return image.ToByteArray(name.EndsWith(".jpg", StringComparison.Ordinal) ? MagickFormat.Jpeg : MagickFormat.Png);
    }
}
