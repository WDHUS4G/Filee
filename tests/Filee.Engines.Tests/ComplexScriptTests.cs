// Text that needs shaping: Arabic, Hebrew, the Indic scripts, Thai, ... (issues #185 Gujarati and #202 Arabic).
//  * Documents → PDF: rhwp draws such text unshaped, so a document with it goes to LibreOffice, or is refused with that
//    reason when LibreOffice is missing (ComplexScripts finds the text, RhwpConverter's PDF edges are Unshaped).
//  * PDF → DOCX / TXT: the reader takes ActualText, keeps marks with their letters in content-stream order, reads
//    right-to-left words and lines from the right and marks such paragraphs right to left.
// The PDFs are written glyph by glyph (GlyphPdfBuilder) the way the shaping engines leave them.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using static Filee.Engines.Tests.DocxAssert;

namespace Filee.Engines.Tests;

public class ComplexScriptTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string PlainText(string pdf) => PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(pdf)).Trim();

    // ───────────────────────── Finding the text ─────────────────────────

    [Fact]
    public void Scripts_that_need_shaping_are_found_in_documents()
    {
        var folder = fx.NewFolder();
        string Docx(string name, string text, string? styles = null)
        {
            var builder = new DocxBuilder();
            if (styles is not null)
                builder.Styles = styles;
            builder.Paragraph(DocxBuilder.P(DocxBuilder.R(text)));
            return builder.Save(Path.Combine(folder, name));
        }
        string Text(string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        Assert.Equal("Gujarati", ComplexScripts.FindInFile(Docx("gujarati.docx", "સૌરાષ્ટ્ર ઘરખેડ"), "docx"));
        Assert.Equal("Arabic", ComplexScripts.FindInFile(Docx("arabic.docx", "Report: مرحبا بالعالم"), "docx"));
        Assert.Null(ComplexScripts.FindInFile(Docx("plain.docx", "English, 한국어, 中文, Ελληνικά, Русский"), "docx"));
        // Style and font names per script are no text of the document.
        Assert.Null(ComplexScripts.FindInFile(Docx("styles.docx", "English only",
            $"<w:styles {DocxBuilder.Namespaces}><w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"عنوان\"/></w:style></w:styles>"), "docx"));

        Assert.Equal("Devanagari", ComplexScripts.FindInFile(Text("hindi.txt", "Title\nहिन्दी भाषा"), "txt"));
        Assert.Equal("Hebrew", ComplexScripts.FindInFile(Text("hebrew.html", "<p>&#x5E9;&#x5DC;&#x5D5;&#x5DD;</p>"), "html"));
        Assert.Equal("Thai", ComplexScripts.FindInFile(Text("thai.rtf", @"{\rtf1 \u3585?\u3634?}"), "rtf"));
        Assert.Null(ComplexScripts.FindInFile(Text("plain.md", "# Title\n\nC:\\users\\me and &#169; 2026"), "md"));
        Assert.Null(ComplexScripts.FindInFile(Text("binary.hwp", "ignored"), "hwp"));

        // Web archives: the HTML part, quoted-printable here, as Chrome saves it.
        const string Mhtml = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"b\"\r\n\r\n--b\r\n" +
                             "Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
                             "<p>=E0=AA=B8=E0=AB=8C=E0=AA=B0=E0=AA=BE</p>\r\n--b--\r\n";
        Assert.Equal("Gujarati", ComplexScripts.FindInFile(Text("page.mhtml", Mhtml), "mhtml"));
        Assert.Null(ComplexScripts.FindInFile(Text("plain.mhtml", Mhtml.Replace("=E0=AA=B8=E0=AB=8C=E0=AA=B0=E0=AA=BE", "Hello")), "mhtml"));

        // OFD: the text of the pages (an official document in Uyghur), not the resource names.
        string Ofd(string name, string text)
        {
            var path = Path.Combine(folder, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("Doc_0/Pages/Page_0/Content.xml").Open()))
                writer.Write($"<ofd:Page xmlns:ofd=\"http://www.ofdspec.org/2016\"><ofd:TextCode X=\"0\" Y=\"4\">{text}</ofd:TextCode></ofd:Page>");
            using (var writer = new StreamWriter(zip.CreateEntry("Doc_0/PublicRes.xml").Open()))
                writer.Write("<Res><Font ID=\"1\" FontName=\"UKIJ Tuz مرحبا\"/></Res>");
            return path;
        }
        Assert.Equal("Arabic", ComplexScripts.FindInFile(Ofd("uyghur.ofd", "ئۇيغۇرچە ھۆججەت"), "ofd"));
        Assert.Null(ComplexScripts.FindInFile(Ofd("chinese.ofd", "电子发票（普通发票）"), "ofd"));
    }

    [Fact]
    public void Scripts_without_widths_are_found_in_documents()
    {
        // rhwp#8143: rhwp's width tables cover only ASCII, Latin-1, general punctuation, math, the CJK forms and
        // Hangul, so a letter of any other script — Polish, Turkish, Vietnamese, Greek, Cyrillic, ... — is drawn
        // but laid out at a fixed half-em.
        var folder = fx.NewFolder();
        string Docx(string name, string text)
        {
            var builder = new DocxBuilder();
            builder.Paragraph(DocxBuilder.P(DocxBuilder.R(text)));
            return builder.Save(Path.Combine(folder, name));
        }
        string Text(string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        Assert.Equal("Latin Extended", ComplexScripts.FindUnmeasuredInFile(Docx("polish.docx", "Zażółć gęślą jaźń"), "docx"));
        Assert.Equal("Latin Extended", ComplexScripts.FindUnmeasuredInFile(Text("vietnamese.md", "Tiếng Việt"), "md"));
        Assert.Equal("Greek", ComplexScripts.FindUnmeasuredInFile(Docx("greek.docx", "Αναφορά Report"), "docx"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Docx("russian.docx", "Отчёт за месяц"), "docx"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Text("russian.txt", "Заголовок\nтекст"), "txt"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Text("russian.md", "# Отчёт\n\ntext"), "md"));
        Assert.Equal("Armenian", ComplexScripts.FindUnmeasuredInFile(Text("armenian.txt", "Հայերեն տեքստ"), "txt"));
        Assert.Equal("Georgian", ComplexScripts.FindUnmeasuredInFile(Text("georgian.txt", "ქართული ტექსტი"), "txt"));
        // A precomposed é is Latin-1 and measured; e + ´ decomposed is not.
        Assert.Equal("combining marks", ComplexScripts.FindUnmeasuredInFile(Text("decomposed.md", "cafe\u0301"), "md"));

        // Latin-1 letters are measured, Hangul, CJK and the full-width forms are estimated right at a full em,
        // and text that needs shaping is the other scan's job.
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("latin1.docx", "Ça été über Straße niño"), "docx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("korean.docx", "한국어 문서"), "docx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("japanese.docx", "日本語のテキスト"), "docx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("chinese.docx", "中文文档"), "docx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("fullwidth.docx", "ＡＢＣ"), "docx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Docx("arabic.docx", "مرحبا بالعالم"), "docx"));
        Assert.Null(ComplexScripts.FindInFile(Docx("russian-2.docx", "Отчёт"), "docx"));
    }

    [Fact]
    public void A_lone_symbol_keeps_rhwp()
    {
        // A Greek letter or a unit between spaces (α, Ω, μ in a Korean report) draws fine on its own: only letters
        // inside a word overlap, so only they send a document away from rhwp.
        var folder = fx.NewFolder();
        string Text(string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        Assert.Null(ComplexScripts.FindUnmeasuredInFile(Text("symbols.md", "각도 α = 30°, 저항 10 Ω, 두께 5 μm"), "md"));
        Assert.Equal("Greek", ComplexScripts.FindUnmeasuredInFile(Text("word.md", "결과: Αναφορά"), "md"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Text("short.md", "Да"), "md"));
    }

    [Fact]
    public void An_hwpx_stays_with_rhwp_for_unmeasured_letters()
    {
        // rhwp is the most faithful renderer for HWPX: Cyrillic in one only goes elsewhere when it needs shaping.
        var path = Path.Combine(fx.NewFolder(), "report.hwpx");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open());
            writer.Write("<hs:sec xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\"><hp:t xmlns:hp=\"x\">보고서 Отчёт</hp:t></hs:sec>");
        }
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(path, "hwpx"));
        Assert.Null(new RhwpConverter().ScanScripts(path, "hwpx").WithoutWidths);
    }

    [Fact]
    public void A_character_reference_outside_unicode_is_skipped()
    {
        // &#1114112; (0x110000) and RTF \u-99999 (still negative past +0x10000) name no code point: a malformed
        // reference before a document's letters must not lose both answers of the scan to an exception.
        var folder = fx.NewFolder();
        string Text(string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        var html = ComplexScripts.FindBothInFile(Text("bad-ref.html", "<p>&#1114112; &#x5E9;&#x5DC;</p>"), "html");
        Assert.Equal("Hebrew", html.Shaping);

        var rtf = ComplexScripts.FindBothInFile(Text("bad-ref.rtf", @"{\rtf1 \u-99999? ᒈ\u1513?\u1500?}"), "rtf");
        Assert.Equal("Hebrew", rtf.Shaping);
    }

    [Fact]
    public void Variation_selectors_are_not_unmeasured_text()
    {
        // U+FE0F picks an emoji's picture form (❤️, ✔️), U+E0100 an ideograph's registered variant (葛󠄀): the
        // zero-width mark rides on a letter rhwp measures, so it must not reroute the document.
        var folder = fx.NewFolder();
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("Déjà vu ❤️ ✔️")));
        var docx = builder.Save(Path.Combine(folder, "emoji.docx"));
        var txt = Path.Combine(folder, "ivs.txt");
        File.WriteAllText(txt, "葛\U000E0100の本文", new UTF8Encoding(false));

        Assert.Null(ComplexScripts.FindBothInFile(docx, "docx").Unmeasured);
        Assert.Null(ComplexScripts.FindBothInFile(txt, "txt").Unmeasured);
    }

    [Fact]
    public void Both_answers_come_from_one_scan()
    {
        // Cyrillic before Arabic: the scan remembers the first unmeasured script while it looks on for shaping
        // text, so the file keeps both answers.
        var path = Path.Combine(fx.NewFolder(), "mixed.txt");
        File.WriteAllText(path, "Отчёт مرحبا", new UTF8Encoding(false));

        var (shaping, unmeasured) = ComplexScripts.FindBothInFile(path, "txt");
        Assert.Equal("Arabic", shaping);
        Assert.Equal("Cyrillic", unmeasured);
    }

    [Fact]
    public async Task Scripts_are_found_in_hwpx_sections()
    {
        var folder = fx.NewFolder();
        var txt = Path.Combine(folder, "tamil.txt");
        await File.WriteAllTextAsync(txt, "தமிழ் மொழி\n한글 문단", TestContext.Current.CancellationToken);
        var job = await fx.ConvertAsync([txt], new Preset { TargetFormat = "hwpx" });
        Assert.Equal(JobState.Completed, job.State);

        Assert.Equal("Tamil", ComplexScripts.FindInFile(job.Outputs.Single(), "hwpx"));
        Assert.Null(ComplexScripts.FindInFile(await fx.MakeHwpxAsync(fx.NewFolder()), "hwpx"));
    }

    [Fact]
    public void Rhwp_says_it_draws_pdf_text_unshaped()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.All(rhwp.Edges.Where(e => e.To == "pdf"), e => Assert.True(e.Unshaped));
        Assert.All(rhwp.Edges.Where(e => e.To != "pdf"), e => Assert.False(e.Unshaped));
        Assert.IsAssignableFrom<ITextShapingCheck>(rhwp);
    }

    [Fact]
    public void A_file_is_scanned_again_once_its_text_changed()
    {
        // A file converted twice in a session — a watch folder, a document saved again — is scanned again:
        // an answer cached on the path would route its new text the old way.
        var path = Path.Combine(fx.NewFolder(), "report.md");
        File.WriteAllText(path, "A plain English report.", new UTF8Encoding(false));
        var rhwp = new RhwpConverter();

        var clean = rhwp.ScanScripts(path, "md");
        Assert.Null(clean.NeedsShaping);
        Assert.Null(clean.WithoutWidths);

        File.WriteAllText(path, "مرحبا بالعالم", new UTF8Encoding(false));
        Assert.Equal("Arabic", rhwp.ScanScripts(path, "md").NeedsShaping);
    }

    [Fact]
    public async Task A_gujarati_document_goes_to_libreoffice_or_is_refused_with_the_reason()
    {
        var available = fx.Converters.Where(c => fx.Catalog.StatusOf(c).IsAvailable).Select(c => c.Id).ToHashSet();
        Assert.SkipUnless(available.Contains("rhwp") || available.Contains("libreoffice"), "neither rhwp nor LibreOffice is available");
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("સૌરાષ્ટ્ર ઘરખેડ ગણોત વહીવટ")));
        var docx = builder.Save(Path.Combine(fx.NewFolder(), "gujarati.docx"));

        var job = await fx.ConvertAsync([docx], new Preset { TargetFormat = "pdf" });

        var file = Assert.Single(job.Files);
        if (available.Contains("libreoffice"))
        {
            Assert.Equal(FileState.Done, file.State);
        }
        else
        {
            Assert.Equal(FileState.Failed, file.State);
            Assert.Equal("error.needs_shaping", file.ErrorKey);
            Assert.Equal("Gujarati", file.ErrorDetail);
        }
    }

    // ───────────────────────── Reading PDFs ─────────────────────────

    /// <summary>
    /// Text laid out like rhwp did: one glyph per character, a placeholder glyph (a dotted circle, here mapped to
    /// <paramref name="placeholder"/>) before every mark, bases overlapping the marks.
    /// </summary>
    private static void Unshaped(GlyphPdfBuilder pdf, string text, double x, double y, string placeholder, double size = 14)
    {
        foreach (var word in text.Split(' '))
        {
            foreach (var c in word)
            {
                if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)
                {
                    pdf.Glyph(placeholder, x, y, 0.6 * size, size);
                    x += 0.6 * size;
                    var spacing = char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpacingCombiningMark;
                    pdf.Glyph(c.ToString(), x, y, spacing ? 0.27 * size : 0, size);
                    x += spacing ? 0.27 * size : 0;
                }
                else
                {
                    pdf.Glyph(c.ToString(), x - 0.1 * size, y, 0.55 * size, size);
                    x += 0.45 * size;
                }
            }
            x += 1.2 * size;
        }
    }

    [Fact]
    public void Text_drawn_without_shaping_is_read_in_its_logical_order()
    {
        // Issue #185's PDF: Gujarati from rhwp, its dotted circles mapped to U+0002; Hindi with them mapped to a vowel sign.
        var pdf = new GlyphPdfBuilder();
        Unshaped(pdf, "સૌરાષ્ટ્ર ઘરખેડ ગણોત", 72, 100, "\u0002");
        Unshaped(pdf, "भाषा में दस्तावेज़", 72, 140, "\u093F");
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "unshaped.pdf"));

        Assert.Equal("સૌરાષ્ટ્ર ઘરખેડ ગણોત\r\n\r\nभाषा में दस्तावेज़", PlainText(path));
    }

    [Fact]
    public void Actual_text_replaces_the_glyphs_it_covers()
    {
        // LibreOffice: a conjunct drawn as one glyph and a vowel sign drawn before its consonant carry their text.
        var pdf = new GlyphPdfBuilder();
        var x = 72.0;
        pdf.Glyph("ર", x, 100, 7).Glyph("\u0ABE", x += 7, 100, 4);
        pdf.ActualText("ષ્ટ્ર", p => p.Glyph("ષ", x += 4, 100, 12));
        x += 12 + 4;
        pdf.ActualText("નિ", p => p.Glyph("\u0ABF", x, 100, 3).Glyph("ન", x + 3, 100, 8));
        pdf.Glyph("ય", x += 11, 100, 8).Glyph("મ", x + 8, 100, 9);
        // Latin text keeps working on the same page, with an ActualText for a ligature.
        pdf.Run("Office ", 72, 130, 6);
        pdf.ActualText("fi", p => p.Glyph("ﬁ", 72 + 7 * 6, 130, 6));
        pdf.Run("le", 72 + 8 * 6, 130, 6);
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "actualtext.pdf"));

        Assert.Equal("રાષ્ટ્ર નિયમ\r\n\r\nOffice file", PlainText(path));
    }

    [Fact]
    public void Vowel_signs_drawn_before_their_consonant_follow_it()
    {
        // Word's PDFs keep the visual order of ि and give no ActualText: ि क → कि.
        var pdf = new GlyphPdfBuilder();
        var x = 72.0;
        pdf.Glyph("\u093F", x, 100, 3).Glyph("क", x + 3, 100, 8).Glyph("स", x + 11, 100, 8).Glyph("\u0940", x + 19, 100, 3);
        x += 30;
        pdf.Glyph("\u093F", x, 100, 3).Glyph("ह", x + 3, 100, 7).Glyph("न्", x + 10, 100, 5).Glyph("द", x + 15, 100, 7).Glyph("\u0940", x + 22, 100, 3);
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "prebase.pdf"));

        Assert.Equal("किसी हिन्दी", PlainText(path));
    }

    /// <summary>
    /// A right-to-left line as Word and LibreOffice draw it: words from the right edge leftwards, the glyphs of each
    /// word left to right (so an Arabic word's letters are reversed in the stream), numbers left to right.
    /// </summary>
    /// <returns>The left edge of the last word.</returns>
    private static double RightToLeft(GlyphPdfBuilder pdf, IEnumerable<string> words, double right, double y, double advance = 6)
    {
        var x = right + advance;
        foreach (var word in words)
        {
            x -= advance; // the space between words
            var visual = ComplexScripts.Direction(word) < 0 ? new string([.. word.Reverse()]) : word;
            x -= visual.Length * advance;
            pdf.Run(visual, x, y, advance);
        }
        return x;
    }

    [Fact]
    public async Task Arabic_is_read_from_right_to_left_into_a_right_to_left_paragraph()
    {
        // Issue #202: Arabic PDF → DOCX. A two-line paragraph with a number and an English word in it, a short one with
        // harakat (marks after their letter in the stream), then an English paragraph.
        var pdf = new GlyphPdfBuilder();
        RightToLeft(pdf, ["هذا", "نص", "عربي", "للاختبار", "مع", "كلمة", "English", "في", "الوسط", "والرقم", "وهذه", "جملة", "طويلة", "جدا"], 523, 100);
        // The second line ends the sentence: "2024." shows as ".2024" left of the last Arabic word.
        var end = RightToLeft(pdf, ["اللغة", "العربية", "جميلة", "2024"], 523, 115);
        pdf.Glyph(".", end - 6, 115, 6);
        // مُحَمَّد in visual order: د, م with fatha and shadda, ح with fatha, م with damma.
        pdf.Glyph("د", 470, 160, 6).Glyph("م", 476, 160, 6).Glyph("\u064E", 476, 160, 0).Glyph("\u0651", 476, 160, 0)
            .Glyph("ح", 482, 160, 6).Glyph("\u064E", 482, 160, 0).Glyph("م", 488, 160, 6).Glyph("\u064F", 488, 160, 0);
        pdf.Run("Plain English paragraph.", 72, 200, 6);
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "arabic.pdf"));

        Assert.Equal(
            "هذا نص عربي للاختبار مع كلمة English في الوسط والرقم وهذه جملة طويلة جدا اللغة العربية جميلة 2024.\r\n\r\n" +
            "مُحَمَّد\r\n\r\nPlain English paragraph.", PlainText(path));

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, job.State);
        var docx = job.Outputs.Single();
        ValidPackage(docx);
        var paragraphs = Body(docx).Descendants(W + "p").Where(p => p.Descendants(W + "t").Any()).ToList();
        Assert.Equal(3, paragraphs.Count);
        bool Bidi(XElement p) => p.Element(W + "pPr")?.Element(W + "bidi") is not null;
        Assert.Equal([true, true, false], paragraphs.Select(Bidi));
        // Arabic runs are right to left (Word then uses their complex script font and size), English ones are not.
        var runs = paragraphs[0].Descendants(W + "r").Where(r => r.Element(W + "t") is not null).ToList();
        Assert.All(runs.Where(r => ComplexScripts.Direction(r.Element(W + "t")!.Value) < 0), r => Assert.NotNull(r.Element(W + "rPr")?.Element(W + "rtl")));
        Assert.Empty(paragraphs[2].Descendants(W + "rtl"));
    }
}
