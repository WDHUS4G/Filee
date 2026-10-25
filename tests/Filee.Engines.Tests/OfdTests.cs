// OFD (GB/T 33190, the Chinese fixed-layout standard: e-invoices, official documents) → DOCX / TXT with the
// built-in reader. The packages are written in the test: an e-invoice in the tax-invoice structure (template page
// with the frame and labels, values on the page, QR code and seal pictures), a WPS document (page size only in
// Document.xml, decimal colours, wrapped body text over two pages), and hostile packages.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Ofd;
using ImageMagick;

namespace Filee.Engines.Tests;

public class OfdTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Ns = "xmlns:ofd=\"http://www.ofdspec.org/2016\"";

    // ───────────────────────── E-invoice ─────────────────────────

    /// <summary>
    /// A fully digitalized e-invoice (数电票) as tax-invoice writers lay it out: the template draws the frame (a grid
    /// in the layer's red-brown) and the labels, some stacked one character per line (购买方信息) or spread (合 计);
    /// the page draws the values — numbers with DeltaX in the "g" shorthand, the buyer's name scaled by a CTM, an item
    /// name wrapped onto a second line — and a QR code. Signatures are present and ignored.
    /// </summary>
    private static string MakeInvoice(string folder)
    {
        var png = File.ReadAllBytes(EngineFixture.MakeImage(folder, "qr.png", MagickFormat.Png, 80, 80));
        var path = Path.Combine(folder, "invoice.ofd");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Text(zip, "OFD.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:OFD {Ns} Version="1.1" DocType="OFD"><ofd:DocBody>
            <ofd:DocInfo><ofd:DocID>4bd9a0e0</ofd:DocID><ofd:CustomDatas><ofd:CustomData Name="发票号码">25442000000123456789</ofd:CustomData></ofd:CustomDatas></ofd:DocInfo>
            <ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot><ofd:Signatures>Doc_0/Signs/Signatures.xml</ofd:Signatures>
            </ofd:DocBody></ofd:OFD>
            """);
        Text(zip, "Doc_0/Document.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Document {Ns}><ofd:CommonData><ofd:MaxUnitID>200</ofd:MaxUnitID>
            <ofd:PageArea><ofd:PhysicalBox>0 0 210 140</ofd:PhysicalBox></ofd:PageArea>
            <ofd:PublicRes>PublicRes.xml</ofd:PublicRes><ofd:DocumentRes>DocumentRes.xml</ofd:DocumentRes>
            <ofd:TemplatePage ID="2" BaseLoc="Tpls/Tpl_0/Content.xml"/></ofd:CommonData>
            <ofd:Pages><ofd:Page ID="1" BaseLoc="Pages/Page_0/Content.xml"/></ofd:Pages></ofd:Document>
            """);
        Text(zip, "Doc_0/PublicRes.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Res {Ns} BaseLoc="Res"><ofd:Fonts>
            <ofd:Font ID="3" FontName="宋体" FamilyName="宋体"/><ofd:Font ID="4" FontName="楷体" FamilyName="楷体"/>
            <ofd:Font ID="5" FontName="Courier New" FamilyName="Courier New"/></ofd:Fonts>
            <ofd:DrawParams><ofd:DrawParam ID="6" LineWidth="0.25"><ofd:FillColor Value="128 0 0"/><ofd:StrokeColor Value="128 0 0"/></ofd:DrawParam></ofd:DrawParams>
            </ofd:Res>
            """);
        Text(zip, "Doc_0/DocumentRes.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Res {Ns} BaseLoc="Res"><ofd:MultiMedias>
            <ofd:MultiMedia ID="7" Type="Image" Format="PNG"><ofd:MediaFile>image_7.png</ofd:MediaFile></ofd:MultiMedia>
            </ofd:MultiMedias></ofd:Res>
            """);
        Entry(zip, "Doc_0/Res/image_7.png", png);

        var template = new StringBuilder();
        void Label(string text, double x, double y, double size = 3)
        {
            template.Append($"""<ofd:TextObject ID="1" Boundary="{x} {y - size} 60 {size + 1}" Font="3" Size="{size}"><ofd:TextCode X="0" Y="{size}">{text}</ofd:TextCode></ofd:TextObject>""");
        }
        void Rule(double x0, double y0, double x1, double y1) =>
            template.Append($"""<ofd:PathObject ID="1" Boundary="{x0} {y0} {Math.Max(1, x1 - x0)} {Math.Max(1, y1 - y0)}"><ofd:AbbreviatedData>M 0 0 L {x1 - x0} {y1 - y0}</ofd:AbbreviatedData></ofd:PathObject>""");
        Label("电子发票（普通发票）", 75, 12, 6);
        Label("发票号码：", 140, 18);
        Label("开票日期：", 140, 24);
        // The frame: an outer rectangle and the rules of the blocks (no rules between the item columns).
        template.Append("""<ofd:PathObject ID="1" Boundary="5 28 200 97"><ofd:AbbreviatedData>M 0 0 L 200 0 L 200 97 L 0 97 C</ofd:AbbreviatedData></ofd:PathObject>""");
        Rule(5, 50, 205, 50);
        Rule(5, 100, 205, 100);
        Rule(5, 108, 205, 108);
        Rule(12, 28, 12, 50);
        Rule(105, 28, 105, 50);
        Rule(112, 28, 112, 50);
        Rule(45, 100, 45, 108);
        Rule(12, 108, 12, 125);
        foreach (var (c, i) in "购买方信息".Select((c, i) => (c, i)))
            Label(c.ToString(), 7, 32 + 4 * i);
        foreach (var (c, i) in "销售方信息".Select((c, i) => (c, i)))
            Label(c.ToString(), 107, 32 + 4 * i);
        Label("名称：", 14, 36);
        Label("统一社会信用代码/纳税人识别号：", 14, 44);
        Label("名称：", 114, 36);
        Label("统一社会信用代码/纳税人识别号：", 114, 44);
        Label("项目名称", 15, 55);
        Label("规格型号", 50, 55);
        Label("单位", 75, 55);
        Label("数量", 95, 55);
        Label("单价", 115, 55);
        Label("金额", 140, 55);
        Label("税率/征收率", 160, 55);
        Label("税额", 185, 55);
        Label("合", 20, 96);
        Label("计", 35, 96);
        Label("价税合计（大写）", 7, 105);
        Label("（小写）", 140, 105);
        Label("备", 7, 114);
        Label("注", 7, 120);
        Label("开票人：", 8, 131);
        Text(zip, "Doc_0/Tpls/Tpl_0/Content.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Page {Ns}><ofd:Content><ofd:Layer ID="1" DrawParam="6">{template}</ofd:Layer></ofd:Content></ofd:Page>
            """);

        var page = new StringBuilder();
        void Value(string text, double x, double y, int font = 4, double size = 3.5, string extra = "") =>
            page.Append($"""<ofd:TextObject ID="1" Boundary="{x} {y - size} 80 {size + 1}" Font="{font}" Size="{size}"><ofd:FillColor Value="0 0 0"/><ofd:TextCode X="0" Y="{size}"{extra}>{text}</ofd:TextCode></ofd:TextObject>""");
        page.Append("""<ofd:ImageObject ID="1" ResourceID="7" Boundary="8 6 20 20" CTM="20 0 0 20 0 0"/>""");
        Value("25442000000123456789", 155.5, 18, 5, extra: " DeltaX=\"g 19 2.1\"");
        Value("2025年03月18日", 155.5, 24);
        // Size 7 halved by the CTM: 3.5 mm on the page, the baseline at y = 36.
        page.Append("""<ofd:TextObject ID="1" Boundary="24 32.5 60 5" Font="4" Size="7" CTM="0.5 0 0 0.5 0 0"><ofd:TextCode X="0" Y="7">成都双流科技有限公司</ofd:TextCode></ofd:TextObject>""");
        Value("91510100MA6CXXXX1A", 61, 44, 5);
        Value("深圳市南山区示例贸易有限公司", 124, 36);
        Value("91440300MA5FXXXX2B", 161, 44, 5);
        Value("*印刷品*转印组", 15, 61);
        Value("A4", 50, 61);
        Value("件", 76, 61);
        Value("2", 97, 61);
        Value("50.00", 115, 61);
        Value("100.00", 140, 61);
        Value("13%", 163, 61);
        Value("13.00", 185, 61);
        Value("件套装", 15, 65.5);
        Value("*信息技术服务*软件维护费", 15, 71);
        Value("次", 76, 71);
        Value("1", 97, 71);
        Value("200.00", 115, 71);
        Value("200.00", 140, 71);
        Value("6%", 163, 71);
        Value("12.00", 185, 71);
        Value("¥300.00", 140, 96);
        Value("¥25.00", 185, 96);
        Value("叁佰贰拾伍圆整", 50, 105);
        Value("¥325.00", 155, 105);
        Value("购方开户银行：中国银行成都分行", 14, 114);
        Value("张三", 20.5, 131);
        Text(zip, "Doc_0/Pages/Page_0/Content.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Page {Ns}><ofd:Template TemplateID="2" ZOrder="Background"/>
            <ofd:Content><ofd:Layer ID="1">{page}</ofd:Layer></ofd:Content></ofd:Page>
            """);
        Text(zip, "Doc_0/Signs/Signatures.xml", $"""<?xml version="1.0" encoding="UTF-8"?><ofd:Signatures {Ns}><ofd:Signature ID="1" BaseLoc="Sign_0/Signature.xml"/></ofd:Signatures>""");
        Entry(zip, "Doc_0/Signs/Sign_0/SignedValue.dat", [0x30, 0x82, 0x01, 0x00, 0xDE, 0xAD]);
        return path;
    }

    [Fact]
    public void Invoice_frame_becomes_a_table_with_the_labels_and_values_in_their_cells()
    {
        var folder = fx.NewFolder();
        var document = OfdReader.Read(MakeInvoice(folder), Path.Combine(folder, "media"));
        var section = document.Sections.Single();
        Assert.Equal(Hwp(210), section.Page!.Width);
        Assert.Equal(Hwp(140), section.Page.Height);

        var table = section.Blocks.OfType<HTable>().Single();
        Assert.Equal(4, table.Rows.Count);
        string CellText(HCell cell) => string.Join("\n", cell.Blocks.OfType<HParagraph>().Select(ParagraphText));
        var cells = table.Rows.SelectMany(r => r.Cells).Select(CellText).ToList();

        // Buyer and seller blocks side by side, each with its stacked block title; label and value read as one.
        Assert.Equal(["购买方信息", "名称：成都双流科技有限公司\n统一社会信用代码/纳税人识别号：91510100MA6CXXXX1A",
                      "销售方信息", "名称：深圳市南山区示例贸易有限公司\n统一社会信用代码/纳税人识别号：91440300MA5FXXXX2B"],
            table.Rows[0].Cells.Select(CellText));

        // The item list: one cell, a tabbed row per item (a wrapped name continues on a second line of its row's
        // paragraph), the spread 合 计 one word.
        var items = CellText(table.Rows[1].Cells.Single()).Split('\n');
        Assert.Equal("项目名称\t规格型号\t单位\t数量\t单价\t金额\t税率/征收率\t税额", items[0]);
        Assert.Equal("*印刷品*转印组\tA4\t件\t2\t50.00\t100.00\t13%\t13.00", items[1]);
        Assert.Equal("件套装", items[2]);
        Assert.Equal("*信息技术服务*软件维护费\t次\t1\t200.00\t200.00\t6%\t12.00", items[3]);
        Assert.Equal("合计\t¥300.00\t¥25.00", items[4]);
        Assert.Equal(4, table.Rows[1].Cells.Single().Blocks.Count);
        var itemRow = table.Rows[1].Cells.Single().Blocks.OfType<HParagraph>().ElementAt(1);
        Assert.Equal(Hwp(50 - 5.5), itemRow.Format.Tabs![0].Position); // the column where A4 starts, from the cell's text edge

        Assert.Contains("价税合计（大写）", cells);
        Assert.Contains("叁佰贰拾伍圆整\t（小写） ¥325.00", cells);
        Assert.Contains("备注", cells);
        Assert.Contains("购方开户银行：中国银行成都分行", cells);

        // The frame keeps the template's red-brown rules; labels keep its colour, values are black.
        Assert.Equal("#800000", table.Rows[0].Cells[0].Borders!.Value.Left.Color);
        var labels = table.Rows[0].Cells[1].Blocks.OfType<HParagraph>().First().Inlines.OfType<HText>().ToList();
        Assert.Equal("#800000", labels[0].Format.Color);
        Assert.Null(labels[1].Format.Color);
        Assert.Equal("KaiTi", labels[1].Format.EastAsianFont);
        Assert.Equal(1000, labels[1].Format.Size); // 3.5 mm: 7 mm halved by the CTM

        // Above the frame: the title centred, then number and date in reading order; the QR code floats beside them.
        var above = section.Blocks.TakeWhile(b => b is not HTable).OfType<HParagraph>().ToList();
        var lines = above.Select(ParagraphText).Where(t => t.Length > 0).ToList();
        Assert.Equal(["电子发票（普通发票）", "发票号码：25442000000123456789", "开票日期：2025年03月18日"], lines);
        Assert.Equal(HAlign.Center, above.Single(p => ParagraphText(p).StartsWith('电')).Format.Align);
        var qr = above[0].Inlines.OfType<HImage>().Single();
        Assert.Equal(new HAnchor("PAPER", "LEFT", Hwp(8), "PAPER", "TOP", Hwp(6), "IN_FRONT_OF_TEXT", true), qr.Anchor);
        Assert.Equal(Hwp(20), qr.Width);

        // Below the frame.
        Assert.Equal("开票人：张三", ParagraphText((HParagraph)section.Blocks[^1]));
    }

    [Fact]
    public async Task Invoice_becomes_docx_and_txt_in_reading_order()
    {
        var job = await fx.ConvertAsync([MakeInvoice(fx.NewFolder())], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, job.State);
        using (var zip = ZipFile.OpenRead(job.Outputs.Single()))
        {
            var xml = XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
            Assert.Single(xml.Descendants(), d => d.Name.LocalName == "tbl");
            Assert.Contains("SimSun", xml.ToString());
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));

            // The text in document order is the invoice's reading order.
            var text = string.Concat(xml.Descendants().Where(d => d.Name.LocalName is "t" or "p").Select(d => d.Name.LocalName == "p" ? "\n" : d.Value));
            string[] order = ["电子发票（普通发票）", "发票号码：25442000000123456789", "购买方信息", "名称：成都双流科技有限公司", "销售方信息",
                "*印刷品*转印组", "件套装", "*信息技术服务*软件维护费", "合计", "价税合计（大写）", "¥325.00", "备注", "开票人：张三"];
            var positions = order.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();
            Assert.DoesNotContain(-1, positions);
            Assert.Equal(positions.Order(), positions);
        }

        // TXT takes a route that depends on the platform's engines (through HWPX and Unhwp where it runs).
        job = await fx.ConvertAsync([MakeInvoice(fx.NewFolder())], new Preset { TargetFormat = "txt" });
        Assert.Equal(JobState.Completed, job.State);
        var txt = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("发票号码：25442000000123456789", txt);
        Assert.Contains("开票人：张三", txt);
    }

    // ───────────────────────── WPS document ─────────────────────────

    /// <summary>
    /// A two-page document as WPS writes it: the page size only in Document.xml, BaseLoc naming the content file,
    /// a Res.xml next to Document.xml that nothing names, decimal colours, bold as Weight=700, one TextObject per line.
    /// </summary>
    private const string Body = "为进一步提升医院管理水平和医疗服务质量，按照国家卫生健康委员会关于推进智慧医院建设的有关要求，结合本院实际情况，制定本方案。明确建设目标。";

    private static string MakeWps(string folder)
    {
        var path = Path.Combine(folder, "letter.ofd");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Text(zip, "OFD.xml", $"""<?xml version="1.0" encoding="utf-8"?><ofd:OFD {Ns} Version="1.1"><ofd:DocBody><ofd:DocRoot>/Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>""");
        Text(zip, "Doc_0/Document.xml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Document {Ns}><ofd:CommonData><ofd:PageArea><ofd:PhysicalBox>0 0 210 297</ofd:PhysicalBox></ofd:PageArea></ofd:CommonData>
            <ofd:Pages><ofd:Page ID="1" BaseLoc="Pages/Page_0/Content.xml"/><ofd:Page ID="2" BaseLoc="Pages/Page_1/Content.xml"/></ofd:Pages></ofd:Document>
            """);
        Text(zip, "Doc_0/Res.xml", $"""<?xml version="1.0" encoding="utf-8"?><ofd:Res {Ns}><ofd:Fonts><ofd:Font ID="3" FontName="仿宋_GB2312"/><ofd:Font ID="4" FontName="黑体"/></ofd:Fonts></ofd:Res>""");

        string Line(string text, double x, double y, int font = 3, double size = 5.6, string extra = "") =>
            $"""<ofd:TextObject ID="1" Font="{font}" Size="{size}" Boundary="{x} {y - size} 160 {size * 1.3}"{extra}><ofd:FillColor Value="5 7 59"/><ofd:TextCode X="0" Y="{size}">{text}</ofd:TextCode></ofd:TextObject>""";
        var first = new StringBuilder();
        first.Append(Line("医院信息化建设方案", 67.15, 40, 4, 8.5, " Weight=\"700\""));
        // A paragraph of three lines on a 156.8 mm measure (28 characters): the first indented by two characters.
        first.Append(Line(Body[..26], 38.2, 60));
        first.Append(Line(Body[26..54], 27, 70));
        first.Append(Line(Body[54..], 27, 80));
        first.Append(Line("第一章 总体规划", 27, 100, 4));
        Text(zip, "Doc_0/Pages/Page_0/Content.xml", $"""<?xml version="1.0" encoding="utf-8"?><ofd:Page {Ns}><ofd:Content><ofd:Layer ID="1">{first}</ofd:Layer></ofd:Content></ofd:Page>""");
        Text(zip, "Doc_0/Pages/Page_1/Content.xml", $"""<?xml version="1.0" encoding="utf-8"?><ofd:Page {Ns}><ofd:Content><ofd:Layer ID="1">{Line("第二章 实施步骤", 27, 40, 4)}</ofd:Layer></ofd:Content></ofd:Page>""");
        return path;
    }

    [Fact]
    public void Wps_document_keeps_paragraphs_pages_bold_and_colour()
    {
        var folder = fx.NewFolder();
        var document = OfdReader.Read(MakeWps(folder), Path.Combine(folder, "media"));
        // Each page starts with an empty line as high as the distance from the top margin to its first text.
        var all = document.Sections.Single().Blocks.OfType<HParagraph>().ToList();
        var blocks = all.Where(p => ParagraphText(p).Length > 0).ToList();
        Assert.Equal(["医院信息化建设方案", Body, "第一章 总体规划", "第二章 实施步骤"], blocks.Select(ParagraphText));

        var title = blocks[0];
        Assert.Equal(HAlign.Center, title.Format.Align);
        var titleRun = title.Inlines.OfType<HText>().Single().Format;
        Assert.True(titleRun.Bold);
        Assert.Equal("#05073B", titleRun.Color);
        Assert.Equal("SimHei", titleRun.EastAsianFont);

        // The body paragraph: justified, a first-line indent of two characters.
        var body = blocks[1];
        Assert.Equal(HAlign.Justify, body.Format.Align);
        Assert.Equal(Hwp(11.2), body.Format.FirstLine);
        Assert.Equal("FangSong_GB2312", body.Inlines.OfType<HText>().First().Format.Font);

        // The second page starts with a page break: on that empty line, which keeps the first text's distance.
        var second = all.IndexOf(blocks[3]);
        Assert.True(all[second - 1].PageBreakBefore);
        Assert.Equal("", ParagraphText(all[second - 1]));
        Assert.Equal(HLineSpacingKind.Fixed, all[second - 1].Format.LineSpacing!.Value.Kind);
        Assert.Equal(0, blocks[3].Format.Before);
        Assert.Single(all, p => p.PageBreakBefore);
    }

    // ───────────────────────── Structures of real invoice writers ─────────────────────────

    /// <summary>
    /// A one-page OFD around the given page content (and template content), with optional extra parts; page ID 1,
    /// fonts 3 (宋体) and 5 (Courier New).
    /// </summary>
    private static string MakeOfd(string folder, string page, string template = "", Action<ZipArchive>? extra = null,
        string docExtra = "", string bodyExtra = "", string area = "0 0 210 140")
    {
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.ofd");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Text(zip, "OFD.xml", $"""<?xml version="1.0" encoding="UTF-8"?><ofd:OFD {Ns} Version="1.1"><ofd:DocBody><ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot>{bodyExtra}</ofd:DocBody></ofd:OFD>""");
        Text(zip, "Doc_0/Document.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Document {Ns}><ofd:CommonData><ofd:PageArea><ofd:PhysicalBox>{area}</ofd:PhysicalBox></ofd:PageArea>
            <ofd:PublicRes>PublicRes.xml</ofd:PublicRes><ofd:DocumentRes>DocumentRes.xml</ofd:DocumentRes><ofd:TemplatePage ID="2" BaseLoc="Tpls/Tpl_0/Content.xml"/></ofd:CommonData>
            <ofd:Pages><ofd:Page ID="1" BaseLoc="Pages/Page_0/Content.xml"/></ofd:Pages>{docExtra}</ofd:Document>
            """);
        Text(zip, "Doc_0/PublicRes.xml", $"""<?xml version="1.0" encoding="UTF-8"?><ofd:Res {Ns} BaseLoc="Res"><ofd:Fonts><ofd:Font ID="3" FontName="宋体"/><ofd:Font ID="5" FontName="Courier New"/></ofd:Fonts></ofd:Res>""");
        Text(zip, "Doc_0/Tpls/Tpl_0/Content.xml", $"""<?xml version="1.0" encoding="UTF-8"?><ofd:Page {Ns}><ofd:Content><ofd:Layer ID="1">{template}</ofd:Layer></ofd:Content></ofd:Page>""");
        Text(zip, "Doc_0/Pages/Page_0/Content.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ofd:Page {Ns}><ofd:Template TemplateID="2" ZOrder="Background"/><ofd:Content><ofd:Layer ID="1">{page}</ofd:Layer></ofd:Content></ofd:Page>
            """);
        extra?.Invoke(zip);
        return path;
    }

    private static string TextObject(string text, double x, double y, double size = 3.175, int font = 3, string code = "", double width = 60) =>
        $"""<ofd:TextObject ID="1" Boundary="{x} {y - size} {width} {size + 1}" Font="{font}" Size="{size}"><ofd:TextCode X="0" Y="{size}"{code}>{text}</ofd:TextCode></ofd:TextObject>""";

    [Fact]
    public void A_block_drawn_by_one_text_code_keeps_its_lines_and_a_short_delta_list_goes_on()
    {
        // The cipher text of a VAT invoice's 密码区: two lines of four characters in one TextCode — DeltaX steps back
        // (-7.5) where DeltaY moves down a line. A title whose DeltaX gives one value for all its characters, and a
        // text placed right where that advance ends: drawn with the font's advance instead, a gap would open.
        var page = TextObject("ABCDEFGH", 20, 30, 4.2333, 5, """ DeltaX="g 3 2.5 -7.5 g 3 2.5" DeltaY="g 3 0 4.5 g 3 0" """)
                   + TextObject("一二三四", 20, 60, 4, code: """ DeltaX="5" """)
                   + TextObject("五六", 40.3, 60, 4, code: """ DeltaX="5" """);
        var folder = fx.NewFolder();
        var document = OfdReader.Read(MakeOfd(folder, page), Path.Combine(folder, "media"));
        var paragraphs = document.Sections.Single().Blocks.OfType<HParagraph>().Where(p => ParagraphText(p).Length > 0).ToList();

        Assert.Equal("ABCD\nEFGH", ParagraphText(paragraphs[0]));
        Assert.Equal(HAlign.Left, paragraphs[0].Format.Align);
        Assert.Equal(Hwp(4.5), paragraphs[0].Format.LineSpacing!.Value.Value); // the block's own line distance
        Assert.Equal("一二三四五六", ParagraphText(paragraphs[1]));
    }

    [Fact]
    public void A_page_value_between_template_characters_keeps_them_apart()
    {
        // 共 _ 页  第 _ 页 of a sales list: the template draws the characters, the page the numbers between them.
        var template = TextObject("共", 160, 50, width: 4) + TextObject("页", 170, 50, width: 4)
                       + TextObject("第", 180, 50, width: 4) + TextObject("页", 190, 50, width: 4)
                       + TextObject("合", 20, 70, width: 4) + TextObject("计", 30, 70, width: 4);
        var page = TextObject("4", 166, 50, width: 3) + TextObject("1", 186, 50, width: 3);
        var folder = fx.NewFolder();
        var document = OfdReader.Read(MakeOfd(folder, page, template), Path.Combine(folder, "media"));
        var texts = document.Sections.Single().Blocks.OfType<HParagraph>().Select(ParagraphText).Where(t => t.Length > 0).ToList();

        Assert.Matches(@"^共\s*4\s*页\s*第\s*1\s*页$", texts[0]);
        Assert.Equal("合计", texts[1]); // a pair with nothing between it is still one word
    }

    [Fact]
    public void Annotations_add_their_text_and_pictures()
    {
        var folder = fx.NewFolder();
        var png = File.ReadAllBytes(EngineFixture.MakeImage(folder, "stamp.png", MagickFormat.Png, 60, 60));
        var path = MakeOfd(folder, TextObject("正文内容", 20, 30), docExtra: "<ofd:Annotations>Annots/Annotations.xml</ofd:Annotations>", extra: zip =>
        {
            Text(zip, "Doc_0/DocumentRes.xml", $"""<?xml version="1.0"?><ofd:Res {Ns} BaseLoc="Res"><ofd:MultiMedias><ofd:MultiMedia ID="9" Type="Image"><ofd:MediaFile>stamp.png</ofd:MediaFile></ofd:MultiMedia></ofd:MultiMedias></ofd:Res>""");
            Entry(zip, "Doc_0/Res/stamp.png", png);
            Text(zip, "Doc_0/Annots/Annotations.xml", $"""<?xml version="1.0"?><ofd:Annotations {Ns}><ofd:Page PageID="1"><ofd:FileLoc>Page_0/Annotation.xml</ofd:FileLoc></ofd:Page></ofd:Annotations>""");
            // A stamp with a picture and a note with text, both placed from their appearance's boundary; a hidden one.
            Text(zip, "Doc_0/Annots/Page_0/Annotation.xml", $"""
                <?xml version="1.0"?>
                <ofd:PageAnnot {Ns}>
                <ofd:Annot Type="Stamp" ID="10"><ofd:Appearance Boundary="150 20 30 30"><ofd:ImageObject ID="11" ResourceID="9" Boundary="0 0 30 30" CTM="30 0 0 30 0 0"/></ofd:Appearance></ofd:Annot>
                <ofd:Annot Type="Highlight" ID="12"><ofd:Appearance Boundary="20 80 100 10">{TextObject("批注：已核对", 0, 5)}<ofd:PathObject ID="13" Boundary="0 0 100 10" Fill="true"><ofd:AbbreviatedData>M 0 0 L 100 0 L 100 10 L 0 10 C</ofd:AbbreviatedData></ofd:PathObject></ofd:Appearance></ofd:Annot>
                <ofd:Annot Type="Link" ID="14" Visible="false"><ofd:Appearance Boundary="20 100 100 10">{TextObject("隐藏", 0, 5)}</ofd:Appearance></ofd:Annot>
                </ofd:PageAnnot>
                """);
        });
        var document = OfdReader.Read(path, Path.Combine(folder, "media"));
        var paragraphs = document.Sections.Single().Blocks.OfType<HParagraph>().ToList();
        var texts = paragraphs.Select(ParagraphText).Where(t => t.Length > 0).ToList();
        Assert.Equal(["正文内容", "批注：已核对"], texts);
        var stamp = paragraphs.SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.Equal(Hwp(150), stamp.Anchor!.HorizontalOffset);
        Assert.Equal(Hwp(20), stamp.Anchor!.VerticalOffset);
    }

    // DER for the seal tests: an SES_Signature (GM/T 0031 v4) reduced to what the reader walks through.
    private static byte[] Der(byte tag, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        byte[] length = body.Length < 0x80 ? [(byte)body.Length]
            : body.Length < 0x100 ? [0x81, (byte)body.Length]
            : [0x82, (byte)(body.Length >> 8), (byte)body.Length];
        return [tag, .. length, .. body];
    }

    private static byte[] Ia5(string text) => Der(0x16, Encoding.ASCII.GetBytes(text));

    private static byte[] Signature(string type, byte[] picture) =>
        Der(0x30,
            Der(0x30, // TBS_Sign
                Der(0x02, [4]),
                Der(0x30, // SESeal
                    Der(0x30, // SES_SealInfo
                        Der(0x30, Ia5("ES"), Der(0x02, [4]), Ia5("TEST")),
                        Ia5("50011200000323"),
                        Der(0x30, Der(0x02, [3]), Der(0x0C, Encoding.UTF8.GetBytes("测试发票监制章"))),
                        Der(0x30, Ia5(type), Der(0x04, picture), Der(0x02, [40]), Der(0x02, [30]))),
                    Der(0x04, [1, 2, 3])),
                Ia5("/Doc_0/Signs/Sign_0/Signature.xml")),
            Der(0x04, [4, 5, 6]),
            Der(0x03, [0, 7]));

    /// <summary>A seal as its own small OFD package: a red oval and a rotated character, as tax-office seals are drawn.</summary>
    private static byte[] SealPackage()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(zip, "OFD.xml", $"""<?xml version="1.0"?><ofd:OFD {Ns}><ofd:DocBody><ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>""");
            Text(zip, "Doc_0/Document.xml", $"""<?xml version="1.0"?><ofd:Document {Ns}><ofd:CommonData><ofd:PublicRes>PublicRes_0.xml</ofd:PublicRes></ofd:CommonData><ofd:Pages><ofd:Page ID="1" BaseLoc="Pages/Page_0/Content.xml"/></ofd:Pages></ofd:Document>""");
            Text(zip, "Doc_0/PublicRes_0.xml", $"""<?xml version="1.0"?><ofd:Res {Ns}><ofd:Fonts><ofd:Font ID="4" FontName="楷体"/></ofd:Fonts></ofd:Res>""");
            Text(zip, "Doc_0/Pages/Page_0/Content.xml", $"""
                <?xml version="1.0"?>
                <ofd:Page {Ns}><ofd:Area><ofd:PhysicalBox>0 0 40 30</ofd:PhysicalBox></ofd:Area><ofd:Content><ofd:Layer ID="2">
                <ofd:TextObject ID="3" Boundary="5 4 4 4" CTM="0.8 -0.6 0.6 0.8 0 0" Font="4" Size="3"><ofd:FillColor Value="255 0 0"/><ofd:TextCode X="0" Y="3">全</ofd:TextCode></ofd:TextObject>
                <ofd:PathObject ID="4" Boundary="0 0 40 30" LineWidth="1.5"><ofd:StrokeColor Value="255 0 0"/>
                <ofd:AbbreviatedData>M 39 15 B 39 23 30 29 20 29 B 10 29 1 23 1 15 B 1 7 10 1 20 1 B 30 1 39 7 39 15 C</ofd:AbbreviatedData></ofd:PathObject>
                </ofd:Layer></ofd:Content></ofd:Page>
                """);
        }
        return stream.ToArray();
    }

    [Fact]
    public void Seals_in_signatures_float_where_the_stamp_is()
    {
        var folder = fx.NewFolder();
        var png = File.ReadAllBytes(EngineFixture.MakeImage(folder, "seal.png", MagickFormat.Png, 50, 50));
        var path = MakeOfd(folder, TextObject("重庆增值税电子普通发票", 60, 15, 6) + TextObject("购买方名称：个人", 20, 60),
            bodyExtra: "<ofd:Signatures>/Doc_0/Signs/Signatures.xml</ofd:Signatures>", extra: zip =>
            {
                Text(zip, "Doc_0/Signs/Signatures.xml", $"""<?xml version="1.0"?><ofd:Signatures {Ns}><ofd:Signature ID="1" BaseLoc="Sign_0/Signature.xml"/><ofd:Signature ID="2" BaseLoc="/Doc_0/Signs/Sign_1/Signature.xml"/><ofd:Signature ID="3" BaseLoc="Sign_2/Signature.xml"/></ofd:Signatures>""");
                // The tax office's supervision seal over the title (an OFD picture) and the seller's seal (PNG) over
                // the buyer line; a third signature whose signed value is no DER at all.
                Text(zip, "Doc_0/Signs/Sign_0/Signature.xml", $"""<?xml version="1.0"?><ofd:Signature {Ns}><ofd:SignedInfo><ofd:StampAnnot ID="1" PageRef="1" Boundary="85 5 40 30"/></ofd:SignedInfo><ofd:SignedValue>/Doc_0/Signs/Sign_0/SignedValue.dat</ofd:SignedValue></ofd:Signature>""");
                Entry(zip, "Doc_0/Signs/Sign_0/SignedValue.dat", Signature("ofd", SealPackage()));
                Text(zip, "Doc_0/Signs/Sign_1/Signature.xml", $"""<?xml version="1.0"?><ofd:Signature {Ns}><ofd:SignedInfo><ofd:StampAnnot ID="1" PageRef="1" Boundary="30 50 25 25"/><ofd:StampAnnot ID="2" PageRef="99" Boundary="30 50 25 25"/></ofd:SignedInfo><ofd:SignedValue>SignedValue.dat</ofd:SignedValue></ofd:Signature>""");
                Entry(zip, "Doc_0/Signs/Sign_1/SignedValue.dat", Signature("PNG", png));
                Text(zip, "Doc_0/Signs/Sign_2/Signature.xml", $"""<?xml version="1.0"?><ofd:Signature {Ns}><ofd:SignedInfo><ofd:StampAnnot ID="1" PageRef="1" Boundary="30 90 25 25"/></ofd:SignedInfo><ofd:SignedValue>SignedValue.dat</ofd:SignedValue></ofd:Signature>""");
                Entry(zip, "Doc_0/Signs/Sign_2/SignedValue.dat", [0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF, 0x16, 0x03]);
            });

        var document = OfdReader.Read(path, Path.Combine(folder, "media"));
        var images = document.Sections.Single().Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().ToList();
        Assert.Equal(2, images.Count);

        var office = images.Single(i => i.Anchor!.HorizontalOffset == Hwp(85));
        Assert.Equal(Hwp(5), office.Anchor!.VerticalOffset);
        Assert.Equal((Hwp(40), Hwp(30)), (office.Width, office.Height));
        Assert.Equal("IN_FRONT_OF_TEXT", office.Anchor!.Wrap);
        // The OFD seal is drawn: a transparent picture at its own size with the red oval on it.
        using (var drawn = new MagickImage(office.Path))
        {
            Assert.Equal(MagickFormat.Png, drawn.Format);
            Assert.Equal(480u, drawn.Width); // 40 mm at 12 pixels per mm
            using var pixels = drawn.GetPixels();
            var colours = pixels.ToByteArray(PixelMapping.RGBA)!;
            var red = 0;
            var clear = 0;
            for (var i = 0; i < colours.Length; i += 4)
            {
                if (colours[i + 3] > 200 && colours[i] > 200 && colours[i + 1] < 80)
                    red++;
                else if (colours[i + 3] == 0)
                    clear++;
            }
            Assert.True(red > 2000, $"{red} red pixels");
            Assert.True(clear > colours.Length / 4 / 2, "the seal is drawn on a transparent background");
        }

        var seller = images.Single(i => i.Anchor!.HorizontalOffset == Hwp(30));
        Assert.Equal(Hwp(50), seller.Anchor!.VerticalOffset);
        Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "seal.png")), File.ReadAllBytes(seller.Path));
    }

    /// <summary>
    /// A sales list (销货清单) as tax writers draw it: rules between the columns only, so each column is one cell with
    /// all rows; the subtotal row (小计) at the bottom of the first column and its amount at the bottom of another.
    /// </summary>
    [Fact]
    public async Task Columns_of_a_list_keep_their_rows_level_and_the_page_its_height()
    {
        var template = new StringBuilder();
        void Rule(double x0, double y0, double x1, double y1) =>
            template.Append($"""<ofd:PathObject ID="1" Boundary="{x0} {y0} {Math.Max(1, x1 - x0)} {Math.Max(1, y1 - y0)}"><ofd:AbbreviatedData>M 0 0 L {x1 - x0} {y1 - y0}</ofd:AbbreviatedData></ofd:PathObject>""");
        Rule(5, 30, 205, 30);
        Rule(5, 37, 205, 37);
        Rule(5, 110, 205, 110);
        foreach (var x in new[] { 5.0, 15, 80, 140, 205 })
            Rule(x, 30, x, 110);
        template.Append(TextObject("序号", 6, 35, width: 8)).Append(TextObject("项目名称", 40, 35, width: 20))
            .Append(TextObject("金额", 100, 35, width: 10)).Append(TextObject("税额", 160, 35, width: 10))
            .Append(TextObject("小计", 6, 105, width: 8));
        var page = new StringBuilder();
        for (var row = 0; row < 3; row++)
        {
            var y = 43 + 7 * row;
            page.Append(TextObject($"{row + 1}", 9, y, width: 3)).Append(TextObject("*餐饮服务*餐费", 16, y, width: 40))
                .Append(TextObject("100.00", 125, y, width: 12)).Append(TextObject("6.00", 195, y, width: 8));
        }
        page.Append(TextObject("¥300.00", 122, 105.2, width: 14)).Append(TextObject("¥18.00", 192, 105.2, width: 12));
        // Under the frame, right before the page's end: it stays on the page.
        page.Append(TextObject("开票人：张三", 10, 136, width: 30));

        var folder = fx.NewFolder();
        var path = MakeOfd(folder, page.ToString(), template.ToString());
        var document = OfdReader.Read(path, Path.Combine(folder, "media"));
        var table = document.Sections.Single().Blocks.OfType<HTable>().Single();
        Assert.True(table.Tight);
        var columns = table.Rows[1].Cells;
        Assert.Equal(4, columns.Count);

        // Every column starts its first row at the same height and steps down the same way, so 3 and 100.00 and the
        // subtotal and its amount stay level.
        double Position(HCell cell, int paragraph) => cell.CellMargin!.Value.Top + cell.Blocks.OfType<HParagraph>().Take(paragraph + 1)
            .Sum(p => p.Format.Before!.Value + p.Format.LineSpacing!.Value.Value) - cell.Blocks.OfType<HParagraph>().ElementAt(paragraph).Format.LineSpacing!.Value.Value;
        Assert.All(columns, c => Assert.Equal(HVerticalAlign.Top, c.VerticalAlign));
        var firstRow = columns.Select(c => Position(c, 0)).ToList();
        Assert.All(firstRow, p => Assert.InRange(p, firstRow[0] - Hwp(0.3), firstRow[0] + Hwp(0.3)));
        Assert.InRange(Position(columns[0], 3), Position(columns[2], 3) - Hwp(0.5), Position(columns[2], 3) + Hwp(0.5)); // 小计 | ¥300.00

        // HWPX: no space under the table and no empty line of its own, so the last line does not go to a new page.
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "hwpx" });
        Assert.Equal(JobState.Completed, job.State);
        using (var zip = ZipFile.OpenRead(job.Outputs.Single()))
        {
            var section = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open()).ReadToEnd();
            Assert.Contains("<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:inMargin", section);
        }
        // DOCX: the cells' top padding is written.
        job = await fx.ConvertAsync([path], new Preset { TargetFormat = "docx" });
        using (var zip = ZipFile.OpenRead(job.Outputs.Single()))
            Assert.Contains("<w:tcMar><w:top ", new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd());
    }

    // ───────────────────────── Hostile and refused packages ─────────────────────────

    [Fact]
    public void References_cannot_leave_the_package_and_huge_counts_are_capped()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "hostile.ofd");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Text(zip, "OFD.xml", $"""<?xml version="1.0"?><ofd:OFD {Ns}><ofd:DocBody><ofd:DocRoot>Doc_0/../Doc_0/./Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>""");
            Text(zip, "Doc_0/Document.xml", $"""
                <?xml version="1.0"?>
                <ofd:Document {Ns}><ofd:CommonData><ofd:PublicRes>../../../outside/Res.xml</ofd:PublicRes><ofd:DocumentRes>/Doc_0/Res.xml</ofd:DocumentRes></ofd:CommonData>
                <ofd:Pages><ofd:Page ID="1" BaseLoc="C:/Windows/win.ini"/><ofd:Page ID="2" BaseLoc="/Doc_0/Pages/Page_0/Content.xml"/></ofd:Pages></ofd:Document>
                """);
            // Pictures that climb out of the package, or name a file on the disk, are not found; a name in the package is.
            Text(zip, "Doc_0/Res.xml", $"""
                <?xml version="1.0"?>
                <ofd:Res {Ns}><ofd:MultiMedias>
                <ofd:MultiMedia ID="1" Type="Image"><ofd:MediaFile>../../../../evil.png</ofd:MediaFile></ofd:MultiMedia>
                <ofd:MultiMedia ID="2" Type="Image"><ofd:MediaFile>file:///C:/Windows/win.ini</ofd:MediaFile></ofd:MultiMedia>
                <ofd:MultiMedia ID="3" Type="Image"><ofd:MediaFile>/Doc_0/Res/../Res/ok.png</ofd:MediaFile></ofd:MultiMedia>
                </ofd:MultiMedias></ofd:Res>
                """);
            Entry(zip, "../evil.png", File.ReadAllBytes(EngineFixture.MakeImage(folder, "evil.png", MagickFormat.Png, 40, 40)));
            Entry(zip, "Doc_0/Res/ok.png", File.ReadAllBytes(EngineFixture.MakeImage(folder, "ok.png", MagickFormat.Png, 40, 40)));
            // A repeat count of two billion, a text object nested deeper than the reader follows.
            var nested = string.Concat(Enumerable.Repeat("<ofd:PageBlock>", 200)) + $"""<ofd:TextObject ID="9" Boundary="20 200 50 5" Size="4"><ofd:TextCode X="0" Y="4">太深</ofd:TextCode></ofd:TextObject>"""
                         + string.Concat(Enumerable.Repeat("</ofd:PageBlock>", 200));
            Text(zip, "Doc_0/Pages/Page_0/Content.xml", $"""
                <?xml version="1.0"?>
                <ofd:Page {Ns}><ofd:Content><ofd:Layer ID="1">
                <ofd:TextObject ID="4" Boundary="20 20 100 5" Size="4"><ofd:TextCode X="0" Y="4" DeltaX="g 2000000000 4">安全</ofd:TextCode></ofd:TextObject>
                <ofd:ImageObject ID="5" ResourceID="1" Boundary="20 40 10 10"/><ofd:ImageObject ID="6" ResourceID="2" Boundary="20 60 10 10"/>
                <ofd:ImageObject ID="7" ResourceID="3" Boundary="20 80 10 10"/>{nested}</ofd:Layer></ofd:Content></ofd:Page>
                """);
            // The same name twice: the first part wins, nothing throws.
            Text(zip, "doc_0/pages/page_0/content.xml", "not xml");
        }

        var media = Path.Combine(folder, "media");
        var document = OfdReader.Read(path, media);
        var paragraphs = document.Sections.Single().Blocks.OfType<HParagraph>().ToList();
        Assert.Equal("安全", ParagraphText(paragraphs[0]));
        Assert.Single(paragraphs.SelectMany(p => p.Inlines).OfType<HImage>());
        Assert.DoesNotContain(paragraphs, p => ParagraphText(p).Contains("太深"));
        Assert.Single(Directory.GetFiles(media));
        Assert.All(Directory.GetFiles(media), f => Assert.StartsWith(Path.GetFullPath(media), Path.GetFullPath(f)));
    }

    [Fact]
    public void Dtds_are_refused()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "xxe.ofd");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Text(zip, "OFD.xml", $"""
                <?xml version="1.0"?>
                <!DOCTYPE ofd:OFD [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]>
                <ofd:OFD {Ns}><ofd:DocBody><ofd:DocRoot>&b;&x;</ofd:DocRoot></ofd:DocBody></ofd:OFD>
                """);
        }
        var error = Assert.Throws<InvalidDataException>(() => OfdReader.Read(path, Path.Combine(folder, "media")));
        Assert.Contains("not valid XML", error.Message);
    }

    [Fact]
    public async Task Encrypted_and_broken_packages_fail_with_a_clear_message()
    {
        var folder = fx.NewFolder();
        var encrypted = Path.Combine(folder, "encrypted.ofd");
        using (var zip = ZipFile.Open(encrypted, ZipArchiveMode.Create))
        {
            Text(zip, "OFD.xml", $"""<?xml version="1.0"?><ofd:OFD {Ns}><ofd:DocBody><ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>""");
            Text(zip, "Encryptions.xml", """<?xml version="1.0"?><Encryptions><Encryption ID="1"/></Encryptions>""");
            Entry(zip, "Doc_0/Document.xml", [0x8F, 0x13, 0x77, 0x02, 0xC4]);
        }
        var job = await fx.ConvertAsync([encrypted], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("encrypted", job.Files.Single().ErrorDetail, StringComparison.OrdinalIgnoreCase);

        var notZip = Path.Combine(folder, "plain.ofd");
        await File.WriteAllTextAsync(notZip, "<OFD/>", TestContext.Current.CancellationToken);
        Assert.Contains("not an OFD document", Assert.Throws<InvalidDataException>(() => OfdReader.Read(notZip, folder)).Message);

        var empty = Path.Combine(folder, "empty.ofd");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            Text(zip, "readme.txt", "no OFD.xml here");
        Assert.Contains("no OFD.xml", Assert.Throws<InvalidDataException>(() => OfdReader.Read(empty, folder)).Message);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static int Hwp(double mm) => (int)Math.Round(mm * 7200 / 25.4);

    private static string ParagraphText(HParagraph paragraph) =>
        string.Concat(paragraph.Inlines.Select(i => i switch
        {
            HText text => text.Text,
            HTab => "\t",
            HLineBreak => "\n",
            _ => "",
        }));

    private static void Text(ZipArchive zip, string name, string content) => Entry(zip, name, Encoding.UTF8.GetBytes(content));

    private static void Entry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }
}
